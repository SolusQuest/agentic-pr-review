import { DefaultArtifactClient, type ArtifactClient } from '@actions/artifact';
import {
  type ArtifactBridgeCommand,
  type ArtifactBridgeFailure,
  type ArtifactBridgeMutationState,
  type ArtifactBridgeResult,
  type ArtifactMetadataWire,
  type ArtifactReferenceWire,
  safePositiveDecimal,
  sha256,
} from './contracts.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';
import {
  OfficialCallError,
  OfficialCallTimeoutError,
  runContainedOfficialCall,
} from './official-output.js';
import { ArtifactBridgeDeadlineError, ArtifactBridgeOperationBudget } from './operation-budget.js';
import {
  ArtifactRestAttemptDeadlineError,
  type ArtifactRestRequestBudget,
} from './artifact-rest-request-budget.js';
import { ArtifactCacheLedger, type ArtifactCacheLedgerToken } from './artifact-cache-ledger.js';
import { ArtifactLifecycleCoordinator } from './artifact-lifecycle-coordinator.js';
import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import {
  ArtifactTransportEnvelopeError,
  digestBytes,
  encodeArtifactTransportEnvelope,
  readArtifactArchive,
} from './transport-envelope.js';

export interface ArtifactBridgeExecutor {
  execute(command: ArtifactBridgeCommand, signal: AbortSignal): Promise<ArtifactBridgeResult>;
  stopAndDrain?(): Promise<void>;
  dispose?(): void | Promise<void>;
}

interface RepositoryArtifact {
  readonly id: number;
  readonly name: string;
  readonly size_in_bytes: number;
  readonly expired: boolean;
  readonly expires_at: string | null;
  readonly digest?: string | null;
  readonly workflow_run?: { readonly id?: number | null } | null;
}

export interface ArtifactActionsRestClient {
  /**
   * Clears only representations made stale by an artifact mutation. This is
   * process-local cache maintenance: it never performs an HTTP request.
   */
  invalidateArtifactMutation?(input: {
    readonly owner: string;
    readonly repo: string;
    readonly name: string;
    readonly artifact_id?: number;
  }): void;
  /** Evicts one semantically rejected repository-artifact list page. */
  invalidateArtifactListRepresentation?(input: {
    readonly owner: string;
    readonly repo: string;
    readonly name: string;
    readonly per_page: number;
    readonly page: number;
  }): void;
  /** Evicts one semantically rejected artifact descriptor. */
  invalidateArtifactRepresentation?(input: {
    readonly owner: string;
    readonly repo: string;
    readonly artifact_id: number;
  }): void;
  /** Evicts one semantically rejected workflow-run attempt descriptor. */
  invalidateWorkflowRunAttemptRepresentation?(input: {
    readonly owner: string;
    readonly repo: string;
    readonly run_id: number;
    readonly attempt_number: number;
  }): void;
  dispose?(): void | Promise<void>;

  listArtifactsForRepo(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly name: string;
      readonly per_page: number;
      readonly page: number;
    },
    signal: AbortSignal,
    latestAttemptStartAt?: number,
  ): Promise<{
    readonly status: number;
    readonly data: {
      readonly total_count: number;
      readonly artifacts: readonly RepositoryArtifact[];
    };
  }>;

  getArtifact(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly artifact_id: number;
    },
    signal: AbortSignal,
    latestAttemptStartAt?: number,
  ): Promise<{ readonly status: number; readonly data: RepositoryArtifact }>;

  downloadArtifactArchive(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly artifact_id: number;
      readonly maximum_bytes: number;
    },
    signal: AbortSignal,
    latestAttemptStartAt?: number,
  ): Promise<{ readonly status: number; readonly data: Uint8Array }>;

  getWorkflowRunAttempt(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly run_id: number;
      readonly attempt_number: number;
    },
    signal: AbortSignal,
    latestAttemptStartAt?: number,
  ): Promise<{
    readonly status: number;
    readonly data: { readonly id: number; readonly run_attempt?: number | null };
  }>;

  deleteArtifact(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly artifact_id: number;
    },
    signal: AbortSignal,
    latestAttemptStartAt?: number,
    onDispatched?: () => void,
  ): Promise<{ readonly status: number }>;
}

export interface OfficialArtifactOperationsContext {
  readonly owner: string;
  readonly repository: string;
  readonly currentRunId: string;
  readonly currentRunAttempt: string;
  readonly artifactClient?: ArtifactClient;
  readonly actions: ArtifactActionsRestClient;
  readonly staging: ArtifactBridgeStaging;
  /** Monotonic milliseconds used only for operation deadlines and pacing. */
  readonly monotonicNow?: () => number;
  /** Unix wall-clock milliseconds used only for expiry and retention. */
  readonly utcNow?: () => number;
  /** Shared by the REST representation and verified-record caches. */
  readonly cacheLedger?: ArtifactCacheLedger;
  /** Present only for the verified trusted-proof route. */
  readonly artifactRestRequestBudget?: ArtifactRestRequestBudget;
}

interface PlatformArtifact {
  readonly name: string;
  readonly id: string;
  readonly archiveSize: number;
  readonly archiveDigest: string;
  readonly expiresAtUnixSeconds: string;
  readonly expired: boolean;
  readonly producingRunId: string;
}

interface VerifiedArtifactRecord {
  readonly metadata: ArtifactMetadataWire;
  readonly bytes: Buffer;
}

const MAXIMUM_VERIFIED_RECORD_CACHE_ENTRIES = 32;
const MAXIMUM_VERIFIED_RECORD_CACHE_BYTES = 64 * 1024 * 1024;

type MutationPhase = 'not_dispatched' | 'dispatched' | 'committed';

class BridgeOperationFailure extends Error {
  constructor(
    readonly failure: ArtifactBridgeFailure,
    readonly mutationState?: ArtifactBridgeMutationState,
    readonly metadata?: ArtifactMetadataWire,
  ) {
    super('artifact_bridge_operation_failed');
    this.name = 'BridgeOperationFailure';
  }
}

export class OfficialArtifactOperations implements ArtifactBridgeExecutor {
  private readonly monotonicNow: () => number;
  private readonly utcNow: () => number;
  private readonly artifactClient: ArtifactClient;
  private readonly cacheLedger: ArtifactCacheLedger;
  private readonly verifiedRecords: VerifiedArtifactRecordCache;
  private readonly coordinator = new ArtifactLifecycleCoordinator();

  constructor(private readonly context: OfficialArtifactOperationsContext) {
    this.monotonicNow = context.monotonicNow ?? (() => performance.now());
    this.utcNow = context.utcNow ?? (() => Date.now());
    this.artifactClient = context.artifactClient ?? new DefaultArtifactClient();
    this.cacheLedger = context.cacheLedger ?? new ArtifactCacheLedger();
    this.verifiedRecords = new VerifiedArtifactRecordCache(this.cacheLedger);
    if (
      !safePositiveDecimal(context.currentRunId) ||
      !safePositiveDecimal(context.currentRunAttempt) ||
      !context.owner ||
      !context.repository
    ) {
      throw new Error('artifact_bridge_context_invalid');
    }
  }

  async execute(
    command: ArtifactBridgeCommand,
    signal: AbortSignal,
  ): Promise<ArtifactBridgeResult> {
    return await this.coordinator.run(signal, async () => {
      const budget = new ArtifactBridgeOperationBudget(
        signal,
        this.monotonicNow,
        this.monotonicNow(),
      );
      try {
        const requiredPrimaryAllocation = mandatoryPrimaryAllocation(command);
        if (requiredPrimaryAllocation !== undefined) {
          this.context.artifactRestRequestBudget?.requireObservedPrimaryAllocation(
            requiredPrimaryAllocation,
          );
        }
        switch (command.operation) {
          case 'list_exact':
            return await this.listExact(command, budget);
          case 'metadata':
            return await this.metadata(command, budget);
          case 'download':
            return await this.download(command, budget);
          case 'upload_immutable':
            return await this.upload(command, budget);
          case 'readback_exact':
            return await this.readBack(command, budget);
          case 'delete_exact':
            return await this.delete(command, budget);
        }
      } catch (error) {
        return this.failureResult(command, error);
      } finally {
        budget.dispose();
      }
    });
  }

  async dispose(): Promise<void> {
    await this.stopAndDrain();
    this.verifiedRecords.dispose();
    this.cacheLedger.clear();
  }

  async stopAndDrain(): Promise<void> {
    this.coordinator.stopIntake();
    await this.coordinator.drain();
  }

  private async listExact(
    command: Extract<ArtifactBridgeCommand, { operation: 'list_exact' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    const maximum = Number(command.maximum_objects);
    let expectedTotal: number | undefined;
    const seen = new Set<string>();
    const objects: ArtifactReferenceWire[] = [];
    for (let page = 1; page <= ARTIFACT_BRIDGE_LIMITS.maximumPages; page += 1) {
      const pageInput = {
        owner: this.context.owner,
        repo: this.context.repository,
        name: command.name,
        per_page: ARTIFACT_BRIDGE_LIMITS.recordsPerPage,
        page,
      } as const;
      const response = await this.callOfficial(
        (requestSignal) =>
          this.context.actions.listArtifactsForRepo(
            pageInput,
            requestSignal,
            budget.latestHttpAttemptStartAt(),
          ),
        budget,
      );
      budget.throwIfExpired();
      if (response.status !== 200 || !responseFits(response.data)) {
        this.rejectArtifactList(command.name, page, 'incomplete');
      }
      if (
        !Array.isArray(response.data.artifacts) ||
        response.data.artifacts.length > ARTIFACT_BRIDGE_LIMITS.recordsPerPage
      ) {
        this.rejectArtifactList(command.name, page, 'incomplete');
      }
      const total = response.data.total_count;
      if (
        !Number.isSafeInteger(total) ||
        total < 0 ||
        total > ARTIFACT_BRIDGE_LIMITS.maximumRecords ||
        total > maximum ||
        (expectedTotal !== undefined && total !== expectedTotal)
      ) {
        this.rejectArtifactList(command.name, page, 'incomplete');
      }
      expectedTotal = total;
      for (const artifact of response.data.artifacts) {
        const id = platformId(artifact.id);
        if (artifact.name !== command.name || !id || seen.has(id)) {
          this.rejectArtifactList(
            command.name,
            page,
            id && seen.has(id) ? 'duplicate' : 'incomplete',
          );
        }
        seen.add(id);
        objects.push({ name: command.name, object_id: id });
        if (objects.length > total || objects.length > maximum) {
          this.rejectArtifactList(command.name, page, 'incomplete');
        }
      }
      if (objects.length === total) {
        return {
          operation: command.operation,
          correlation_id: command.correlation_id,
          failure: 'none',
          complete: true,
          objects: objects.sort((left, right) =>
            BigInt(left.object_id) < BigInt(right.object_id) ? -1 : 1,
          ),
        };
      }
      if (response.data.artifacts.length !== ARTIFACT_BRIDGE_LIMITS.recordsPerPage) {
        this.rejectArtifactList(command.name, page, 'incomplete');
      }
    }
    return this.rejectArtifactList(command.name, ARTIFACT_BRIDGE_LIMITS.maximumPages, 'incomplete');
  }

  private async metadata(
    command: Extract<ArtifactBridgeCommand, { operation: 'metadata' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(command.name, command.object_id, budget);
    const record = await this.readRecord(platform, budget);
    try {
      return successWithMetadata(command, record.metadata);
    } finally {
      record.bytes.fill(0);
    }
  }

  private async download(
    command: Extract<ArtifactBridgeCommand, { operation: 'download' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.expected.name,
      command.expected.object_id,
      budget,
    );
    this.assertExpectedPlatform(command.expected, platform);
    this.assertNotExpired(platform);
    const record = await this.readRecord(platform, budget);
    try {
      try {
        assertMetadata(command.expected, record.metadata);
      } catch (error) {
        this.invalidatePlatformRepresentation(platform.name, platform.id);
        throw error;
      }
      if (record.bytes.length > Number(command.maximum_bytes)) {
        throw new BridgeOperationFailure('digest_mismatch');
      }
      await this.context.staging.writeDestination(
        command.destination_relative_path,
        record.bytes,
        budget,
      );
      return successWithMetadata(command, record.metadata);
    } finally {
      record.bytes.fill(0);
    }
  }

  private async upload(
    command: Extract<ArtifactBridgeCommand, { operation: 'upload_immutable' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    let phase: MutationPhase = 'not_dispatched';
    let reservation: { readonly release: () => void } | undefined;
    let metadata: ArtifactMetadataWire | undefined;
    let source: Buffer | undefined;
    let envelope: Buffer | undefined;
    try {
      source = await this.context.staging.readSource(command.source_relative_path, budget);
      if (digestBytes(source) !== command.encrypted_object_digest) {
        throw new BridgeOperationFailure('invalid', 'not_committed');
      }
      envelope = encodeArtifactTransportEnvelope(
        this.context.currentRunId,
        this.context.currentRunAttempt,
        source,
        command.encrypted_object_digest,
        budget,
      );
      const { envelopePath, operationDirectory } = await this.context.staging.writeUploadEnvelope(
        command.source_relative_path,
        envelope,
        budget,
      );
      // Staging synchronously copies the encoded envelope to its pre-created
      // file. Neither buffer is retained by staging, so this operation keeps
      // ownership and clears both before the upload can await network work.
      envelope.fill(0);
      envelope = undefined;
      source.fill(0);
      source = undefined;
      const minimumExpiry = Number(command.minimum_expires_at_unix_seconds);
      const retentionDays = Math.max(
        1,
        Math.ceil((minimumExpiry * 1000 - this.utcNow()) / 86_400_000),
      );
      // The upload data-plane call is followed by get/archive/attempt
      // verification.  Reserve those three authenticated REST observations
      // (and a conservative upload-mutative point) before irreversible work.
      reservation = this.reserveMutation(3, 8);
      const upload = async (markDispatched: () => void) =>
        await this.callOfficial(
          () => {
            // An outcome-unknown upload may still have changed the named
            // collection. Clear its list pages immediately before the SDK can
            // start its wire work; unrelated artifact and attempt validators
            // remain conditionally reusable.
            this.context.actions.invalidateArtifactMutation?.({
              owner: this.context.owner,
              repo: this.context.repository,
              name: command.name,
            });
            const pending = this.artifactClient.uploadArtifact(
              command.name,
              [envelopePath],
              operationDirectory,
              {
                retentionDays,
                compressionLevel: 0,
              },
            );
            phase = 'dispatched';
            return pending;
          },
          budget,
          () => {
            markDispatched();
          },
        );
      const response = this.context.artifactRestRequestBudget
        ? await this.context.artifactRestRequestBudget.runReservedMutationDataPlaneCall(
            {
              signal: budget.signal,
              secondaryLimitPoints: 5,
              mutative: true,
              latestAttemptStartAt: budget.latestHttpAttemptStartAt(),
            },
            upload,
          )
        : await upload(() => undefined);
      budget.throwIfExpired();
      const objectId = platformId(response.id);
      const archiveDigest = parseUploadResponseDigest(response.digest);
      if (!objectId || !archiveDigest) {
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
      phase = 'committed';
      const platform = await this.loadPlatformArtifact(command.name, objectId, budget);
      if (
        platform.archiveDigest !== archiveDigest ||
        platform.producingRunId !== this.context.currentRunId
      ) {
        throw new BridgeOperationFailure('conflict', 'committed');
      }
      const record = await this.readRecord(platform, budget);
      try {
        metadata = record.metadata;
        if (
          record.metadata.encrypted_object_digest !== command.encrypted_object_digest ||
          Number(record.metadata.expires_at_unix_seconds) < minimumExpiry
        ) {
          this.invalidatePlatformRepresentation(platform.name, platform.id);
          throw new BridgeOperationFailure('conflict', 'committed', record.metadata);
        }
        return {
          ...successWithMetadata(command, record.metadata),
          mutation_state: 'committed',
        };
      } finally {
        record.bytes.fill(0);
      }
    } catch (error) {
      if (error instanceof BridgeOperationFailure) {
        throw new BridgeOperationFailure(
          error.failure,
          error.mutationState ?? mutationStateForPhase(phase),
          error.metadata ?? metadata,
        );
      }
      if (
        phase !== 'committed' &&
        error instanceof OfficialCallError &&
        provenConflict(error.causeValue)
      ) {
        throw new BridgeOperationFailure('conflict', 'not_committed');
      }
      if (
        error instanceof ArtifactBridgeStagingError ||
        error instanceof ArtifactTransportEnvelopeError
      ) {
        throw new BridgeOperationFailure('invalid', mutationStateForPhase(phase), metadata);
      }
      if (phase !== 'not_dispatched') {
        throw new BridgeOperationFailure(
          'outcome_unknown',
          phase === 'committed' ? 'committed' : 'outcome_unknown',
          metadata,
        );
      }
      if (
        error instanceof ArtifactBridgeDeadlineError ||
        isPreDispatchDeadline(error) ||
        error instanceof OfficialCallTimeoutError
      ) {
        throw new BridgeOperationFailure('cancelled', 'not_committed');
      }
      if (error instanceof OfficialCallError) {
        throw new BridgeOperationFailure(
          structuredStatus(error.causeValue) === 404 ? 'not_found' : 'io',
          'not_committed',
        );
      }
      throw new BridgeOperationFailure('io', 'not_committed');
    } finally {
      reservation?.release();
      // Covers every pre-persistence error and any future path introduced
      // between allocation and the ownership-ending clears above.
      envelope?.fill(0);
      source?.fill(0);
    }
  }

  private async readBack(
    command: Extract<ArtifactBridgeCommand, { operation: 'readback_exact' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.expected.name,
      command.expected.object_id,
      budget,
    );
    this.assertExpectedPlatform(command.expected, platform);
    const record = await this.readRecord(platform, budget);
    try {
      try {
        assertMetadata(command.expected, record.metadata);
      } catch (error) {
        this.invalidatePlatformRepresentation(platform.name, platform.id);
        throw error;
      }
      return successWithMetadata(command, record.metadata);
    } finally {
      record.bytes.fill(0);
    }
  }

  private async delete(
    command: Extract<ArtifactBridgeCommand, { operation: 'delete_exact' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    let phase: MutationPhase = 'not_dispatched';
    let reservation: { readonly release: () => void } | undefined;
    try {
      // Preflight GET + DELETE + mandatory post-delete GET.  Reserve all of
      // them before the delete begins so a one-unit-short budget cannot issue
      // an irreversible mutation.
      reservation = this.reserveMutation(3, 7);
      const platform = await this.loadPlatformArtifact(
        command.expected.name,
        command.expected.object_id,
        budget,
      );
      this.assertExpectedPlatform(command.expected, platform);
      this.verifiedRecords.delete(platform);
      const response = await this.callOfficial(
        (requestSignal) =>
          this.context.actions.deleteArtifact(
            {
              owner: this.context.owner,
              repo: this.context.repository,
              artifact_id: Number(command.expected.object_id),
            },
            requestSignal,
            budget.latestHttpAttemptStartAt(),
            () => {
              // Admission and pacing have completed. Invalidate at the exact
              // pre-wire boundary so a rejected attempt preserves its valid
              // representations, while every outcome-unknown wire attempt
              // forces later observations to revalidate the mutated target.
              this.context.actions.invalidateArtifactMutation?.({
                owner: this.context.owner,
                repo: this.context.repository,
                name: command.expected.name,
                artifact_id: Number(command.expected.object_id),
              });
              phase = 'dispatched';
            },
          ),
        budget,
      );
      budget.throwIfExpired();
      if (response.status !== 204) {
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
      // The postcondition must be a fresh wire observation of absence, not a
      // conditional reuse of the pre-delete representation.
      this.context.actions.invalidateArtifactMutation?.({
        owner: this.context.owner,
        repo: this.context.repository,
        name: command.expected.name,
        artifact_id: Number(command.expected.object_id),
      });
      try {
        await this.loadPlatformArtifact(command.expected.name, command.expected.object_id, budget);
      } catch (error) {
        if (isNotFound(error)) {
          phase = 'committed';
          return {
            operation: command.operation,
            correlation_id: command.correlation_id,
            failure: 'none',
            mutation_state: 'committed',
          };
        }
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
      throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
    } catch (error) {
      if (error instanceof BridgeOperationFailure) {
        throw new BridgeOperationFailure(
          error.failure,
          error.mutationState ?? mutationStateForPhase(phase),
          error.metadata,
        );
      }
      if (phase !== 'not_dispatched') {
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
      if (isNotFound(error)) {
        throw new BridgeOperationFailure('not_found', 'not_committed');
      }
      if (
        error instanceof ArtifactBridgeDeadlineError ||
        isPreDispatchDeadline(error) ||
        error instanceof OfficialCallTimeoutError
      ) {
        throw new BridgeOperationFailure('cancelled', 'not_committed');
      }
      if (error instanceof OfficialCallError) {
        throw new BridgeOperationFailure('io', 'not_committed');
      }
      throw new BridgeOperationFailure('io', 'not_committed');
    } finally {
      reservation?.release();
    }
  }

  private async loadPlatformArtifact(
    expectedName: string,
    objectId: string,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<PlatformArtifact> {
    const response = await this.callOfficial(
      (requestSignal) =>
        this.context.actions.getArtifact(
          {
            owner: this.context.owner,
            repo: this.context.repository,
            artifact_id: Number(objectId),
          },
          requestSignal,
          budget.latestHttpAttemptStartAt(),
        ),
      budget,
    );
    budget.throwIfExpired();
    if (response.status === 404) {
      this.verifiedRecords.deleteByNameAndId(expectedName, objectId);
      throw new BridgeOperationFailure('not_found');
    }
    if (response.status !== 200 || !responseFits(response.data)) {
      this.invalidatePlatformRepresentation(expectedName, objectId);
      throw new BridgeOperationFailure('invalid');
    }
    const artifact = response.data;
    const id = platformId(artifact.id);
    const runId = platformId(artifact.workflow_run?.id ?? undefined);
    const archiveDigest = parseRestArtifactDigest(artifact.digest);
    const expiry = parseExpiry(artifact.expires_at);
    if (
      id !== objectId ||
      artifact.name !== expectedName ||
      !runId ||
      !archiveDigest ||
      !expiry ||
      typeof artifact.expired !== 'boolean' ||
      !Number.isSafeInteger(artifact.size_in_bytes) ||
      artifact.size_in_bytes < 1 ||
      artifact.size_in_bytes > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes
    ) {
      this.invalidatePlatformRepresentation(expectedName, objectId);
      throw new BridgeOperationFailure('invalid');
    }
    return {
      name: artifact.name,
      id,
      archiveSize: artifact.size_in_bytes,
      archiveDigest,
      expiresAtUnixSeconds: expiry,
      expired: artifact.expired,
      producingRunId: runId,
    };
  }

  private async readRecord(
    platform: PlatformArtifact,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<VerifiedArtifactRecord> {
    // Every caller first obtains a fresh platform observation. GitHub artifact
    // archives are immutable, so an exact observed platform identity may reuse
    // a prior fully verified envelope without weakening that observation.
    const cached = this.verifiedRecords.get(platform);
    if (cached) return cached;
    const response = await this.callOfficial(
      (requestSignal) =>
        this.context.actions.downloadArtifactArchive(
          {
            owner: this.context.owner,
            repo: this.context.repository,
            artifact_id: Number(platform.id),
            maximum_bytes: ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes,
          },
          requestSignal,
          budget.latestHttpAttemptStartAt(),
        ),
      budget,
    );
    if (response.status !== 200) {
      throw new BridgeOperationFailure('io');
    }
    budget.throwIfExpired();
    const archive = Buffer.from(response.data);
    let envelope: Awaited<ReturnType<typeof readArtifactArchive>> | undefined;
    try {
      if (
        archive.length < 1 ||
        archive.length !== platform.archiveSize ||
        archive.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes ||
        digestBytes(archive) !== platform.archiveDigest
      ) {
        throw new BridgeOperationFailure('digest_mismatch');
      }
      envelope = await readArtifactArchive(archive, platform.archiveDigest, budget);
      if (envelope.producingRunId !== platform.producingRunId) {
        throw new BridgeOperationFailure('conflict');
      }
      await this.verifyRunAttempt(envelope.producingRunId, envelope.producingRunAttempt, budget);
      const record: VerifiedArtifactRecord = {
        metadata: {
          name: platform.name,
          object_id: platform.id,
          producing_run_id: envelope.producingRunId,
          producing_run_attempt: envelope.producingRunAttempt,
          archive_digest: platform.archiveDigest,
          encrypted_object_digest: envelope.encryptedObjectDigest,
          expires_at_unix_seconds: platform.expiresAtUnixSeconds,
          size: String(envelope.encryptedObjectSize),
        },
        bytes: envelope.encryptedBytes,
      };
      this.verifiedRecords.store(platform, record);
      const output = this.verifiedRecords.copy(record);
      return output;
    } catch (error) {
      if (
        error instanceof BridgeOperationFailure ||
        error instanceof ArtifactTransportEnvelopeError
      ) {
        this.invalidatePlatformRepresentation(platform.name, platform.id);
      }
      throw error;
    } finally {
      // The cache and returned record each take defensive copies.  This source
      // envelope remains owned here even when verification, caching, or copying
      // throws, so it is never allowed to escape an exceptional read path.
      envelope?.encryptedBytes.fill(0);
      archive.fill(0);
    }
  }

  private async verifyRunAttempt(
    runId: string,
    runAttempt: string,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<void> {
    const response = await this.callOfficial(
      (requestSignal) =>
        this.context.actions.getWorkflowRunAttempt(
          {
            owner: this.context.owner,
            repo: this.context.repository,
            run_id: Number(runId),
            attempt_number: Number(runAttempt),
          },
          requestSignal,
          budget.latestHttpAttemptStartAt(),
        ),
      budget,
    );
    budget.throwIfExpired();
    if (
      response.status !== 200 ||
      platformId(response.data.id) !== runId ||
      platformId(response.data.run_attempt ?? undefined) !== runAttempt
    ) {
      this.context.actions.invalidateWorkflowRunAttemptRepresentation?.({
        owner: this.context.owner,
        repo: this.context.repository,
        run_id: Number(runId),
        attempt_number: Number(runAttempt),
      });
      throw new BridgeOperationFailure('conflict');
    }
  }

  private assertExpectedPlatform(expected: ArtifactMetadataWire, platform: PlatformArtifact): void {
    if (
      expected.name !== platform.name ||
      expected.object_id !== platform.id ||
      expected.producing_run_id !== platform.producingRunId ||
      expected.archive_digest !== platform.archiveDigest ||
      expected.expires_at_unix_seconds !== platform.expiresAtUnixSeconds
    ) {
      this.invalidatePlatformRepresentation(platform.name, platform.id);
      throw new BridgeOperationFailure('conflict');
    }
  }

  private assertNotExpired(platform: PlatformArtifact): void {
    if (
      platform.expired ||
      Number(platform.expiresAtUnixSeconds) <= Math.floor(this.utcNow() / 1000)
    ) {
      this.invalidatePlatformRepresentation(platform.name, platform.id);
      throw new BridgeOperationFailure('expired');
    }
  }

  private async callOfficial<T>(
    call: (signal: AbortSignal) => Promise<T>,
    budget: ArtifactBridgeOperationBudget,
    beforeDispatch?: () => void,
  ): Promise<T> {
    const latestAttemptStartAt = budget.latestHttpAttemptStartAt();
    return await runContainedOfficialCall(
      call,
      budget.remainingMs(),
      budget.signal,
      latestAttemptStartAt,
      this.monotonicNow,
      beforeDispatch,
    );
  }

  private reserveMutation(
    authenticatedRequests: number,
    secondaryPoints: number,
  ): {
    readonly release: () => void;
  } {
    const budget = this.context.artifactRestRequestBudget;
    if (!budget) return { release: () => undefined };
    return budget.reserveMutation({
      authenticatedRequests,
      primaryRequests: authenticatedRequests,
      secondaryPoints,
    });
  }

  private rejectArtifactList(
    name: string,
    throughPage: number,
    failure: Extract<ArtifactBridgeFailure, 'duplicate' | 'incomplete'>,
  ): never {
    for (let page = 1; page <= throughPage; page += 1) {
      this.context.actions.invalidateArtifactListRepresentation?.({
        owner: this.context.owner,
        repo: this.context.repository,
        name,
        per_page: ARTIFACT_BRIDGE_LIMITS.recordsPerPage,
        page,
      });
    }
    throw new BridgeOperationFailure(failure);
  }

  private invalidatePlatformRepresentation(name: string, objectId: string): void {
    this.context.actions.invalidateArtifactRepresentation?.({
      owner: this.context.owner,
      repo: this.context.repository,
      artifact_id: Number(objectId),
    });
    this.verifiedRecords.deleteByNameAndId(name, objectId);
  }

  private failureResult(command: ArtifactBridgeCommand, error: unknown): ArtifactBridgeResult {
    let failure: ArtifactBridgeFailure = 'io';
    let mutationState: ArtifactBridgeMutationState | undefined;
    let metadata: ArtifactMetadataWire | undefined;
    if (error instanceof BridgeOperationFailure) {
      failure = error.failure;
      mutationState = error.mutationState;
      metadata = error.metadata;
    } else if (
      error instanceof ArtifactBridgeStagingError ||
      error instanceof ArtifactTransportEnvelopeError
    ) {
      failure = 'invalid';
    } else if (isPreDispatchDeadline(error)) {
      failure = 'cancelled';
    } else if (error instanceof OfficialCallError) {
      const status = structuredStatus(error.causeValue);
      if (status === 404) failure = 'not_found';
      else if (provenConflict(error.causeValue) && command.operation === 'upload_immutable') {
        failure = 'conflict';
        mutationState = 'not_committed';
      }
    } else if (error instanceof OfficialCallTimeoutError) {
      failure = 'cancelled';
    } else if (
      error instanceof ArtifactBridgeDeadlineError ||
      error instanceof ArtifactRestAttemptDeadlineError
    ) {
      failure = 'cancelled';
    }
    if (
      (command.operation === 'upload_immutable' || command.operation === 'delete_exact') &&
      mutationState === undefined
    ) {
      mutationState =
        failure === 'conflict' || failure === 'invalid' || failure === 'cancelled'
          ? 'not_committed'
          : 'outcome_unknown';
      if (mutationState === 'outcome_unknown') failure = 'outcome_unknown';
    }
    return {
      operation: command.operation,
      correlation_id: command.correlation_id,
      failure,
      ...(mutationState ? { mutation_state: mutationState } : {}),
      ...(metadata ? { metadata } : {}),
      ...(command.operation === 'list_exact' ? { complete: false } : {}),
    };
  }
}

class VerifiedArtifactRecordCache {
  private readonly entries = new Map<string, VerifiedArtifactRecordCacheEntry>();
  private totalBytes = 0;

  constructor(private readonly ledger: ArtifactCacheLedger) {}

  get(platform: PlatformArtifact): VerifiedArtifactRecord | undefined {
    const key = recordKey(platform);
    const entry = this.entries.get(key);
    if (!entry) return undefined;
    this.entries.delete(key);
    this.entries.set(key, entry);
    this.ledger.touch(entry.ledgerToken);
    return this.copy(entry.record);
  }

  store(platform: PlatformArtifact, record: VerifiedArtifactRecord): void {
    const key = recordKey(platform);
    this.deleteKey(key);
    if (record.bytes.length > MAXIMUM_VERIFIED_RECORD_CACHE_BYTES) return;
    const stored: VerifiedArtifactRecord = {
      metadata: { ...record.metadata },
      bytes: Buffer.from(record.bytes),
    };
    const entry: VerifiedArtifactRecordCacheEntry = { record: stored, ledgerToken: undefined };
    const token = this.ledger.claim(stored.bytes.length, () => this.deleteKey(key, false));
    if (!token) {
      stored.bytes.fill(0);
      return;
    }
    entry.ledgerToken = token;
    this.entries.set(key, entry);
    this.totalBytes += stored.bytes.length;
    this.evict();
  }

  delete(platform: PlatformArtifact): void {
    this.deleteKey(recordKey(platform));
  }

  deleteByNameAndId(name: string, id: string): void {
    const prefix = name + '\u0000' + id + '\u0000';
    for (const key of this.entries.keys()) {
      if (key.startsWith(prefix)) this.deleteKey(key);
    }
  }

  dispose(): void {
    for (const key of [...this.entries.keys()]) this.deleteKey(key);
  }

  copy(record: VerifiedArtifactRecord): VerifiedArtifactRecord {
    return {
      metadata: { ...record.metadata },
      bytes: Buffer.from(record.bytes),
    };
  }

  private evict(): void {
    while (
      this.entries.size > MAXIMUM_VERIFIED_RECORD_CACHE_ENTRIES ||
      this.totalBytes > MAXIMUM_VERIFIED_RECORD_CACHE_BYTES
    ) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) return;
      this.deleteKey(oldest);
    }
  }

  private deleteKey(key: string, releaseLedger = true): void {
    const entry = this.entries.get(key);
    if (!entry) return;
    this.entries.delete(key);
    this.totalBytes -= entry.record.bytes.length;
    if (releaseLedger) this.ledger.release(entry.ledgerToken);
    entry.record.bytes.fill(0);
  }
}

interface VerifiedArtifactRecordCacheEntry {
  readonly record: VerifiedArtifactRecord;
  ledgerToken: ArtifactCacheLedgerToken | undefined;
}

function recordKey(platform: PlatformArtifact): string {
  return [
    platform.name,
    platform.id,
    platform.archiveSize,
    platform.archiveDigest,
    platform.expiresAtUnixSeconds,
    platform.expired,
    platform.producingRunId,
  ].join('\u0000');
}

function successWithMetadata(
  command: ArtifactBridgeCommand,
  metadata: ArtifactMetadataWire,
): ArtifactBridgeResult {
  return {
    operation: command.operation,
    correlation_id: command.correlation_id,
    failure: 'none',
    metadata,
  };
}

function assertMetadata(expected: ArtifactMetadataWire, actual: ArtifactMetadataWire): void {
  if (JSON.stringify(expected) !== JSON.stringify(actual)) {
    throw new BridgeOperationFailure('conflict');
  }
}

function platformId(value: unknown): string | undefined {
  return Number.isSafeInteger(value) && Number(value) > 0 ? String(value) : undefined;
}

function parseRestArtifactDigest(value: unknown): string | undefined {
  if (typeof value !== 'string' || !value.startsWith('sha256:')) {
    return undefined;
  }
  return sha256(value.slice('sha256:'.length));
}

function parseUploadResponseDigest(value: unknown): string | undefined {
  return typeof value === 'string' ? sha256(value) : undefined;
}

function parseExpiry(value: unknown): string | undefined {
  if (typeof value !== 'string' || value.length > 64) return undefined;
  const milliseconds = Date.parse(value);
  if (!Number.isFinite(milliseconds) || milliseconds <= 0) return undefined;
  const seconds = Math.floor(milliseconds / 1000);
  return Number.isSafeInteger(seconds) && seconds > 0 ? String(seconds) : undefined;
}

function responseFits(value: unknown): boolean {
  try {
    return (
      Buffer.byteLength(JSON.stringify(value), 'utf8') <=
      ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes
    );
  } catch {
    return false;
  }
}

function structuredStatus(error: unknown): number | undefined {
  if (typeof error !== 'object' || error === null) return undefined;
  const record = error as Record<string, unknown>;
  if (Number.isSafeInteger(record.status)) return Number(record.status);
  if (typeof record.response === 'object' && record.response !== null) {
    const response = record.response as Record<string, unknown>;
    if (Number.isSafeInteger(response.status)) return Number(response.status);
  }
  return undefined;
}

function provenConflict(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) return false;
  const record = error as Record<string, unknown>;
  return record.code === 6 || record.code === 'already_exists';
}

function mutationStateForPhase(phase: MutationPhase): ArtifactBridgeMutationState {
  return phase === 'not_dispatched'
    ? 'not_committed'
    : phase === 'committed'
      ? 'committed'
      : 'outcome_unknown';
}

/**
 * The final profile will freeze these route tails from AOT evidence. They are
 * structured here rather than inferred from a response header: every listed
 * command must perform exactly a platform GET, archive redirect, and
 * producing-run attempt verification before it can publish a verified result.
 * Mutations reserve their own raw/primary/point compound before any wire call.
 */
function mandatoryPrimaryAllocation(command: ArtifactBridgeCommand): number | undefined {
  switch (command.operation) {
    case 'list_exact':
      return ARTIFACT_BRIDGE_LIMITS.maximumPages;
    case 'metadata':
    case 'download':
    case 'readback_exact':
      return 3;
    case 'upload_immutable':
    case 'delete_exact':
      return undefined;
  }
}

function isNotFound(error: unknown): boolean {
  return (
    (error instanceof BridgeOperationFailure && error.failure === 'not_found') ||
    (error instanceof OfficialCallError && structuredStatus(error.causeValue) === 404)
  );
}

function isPreDispatchDeadline(error: unknown): boolean {
  return (
    error instanceof ArtifactRestAttemptDeadlineError ||
    (error instanceof OfficialCallError &&
      error.causeValue instanceof ArtifactRestAttemptDeadlineError)
  );
}
