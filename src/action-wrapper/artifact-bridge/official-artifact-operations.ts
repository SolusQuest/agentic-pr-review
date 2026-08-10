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
import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import {
  ArtifactTransportEnvelopeError,
  digestBytes,
  encodeArtifactTransportEnvelope,
  readArtifactArchive,
} from './transport-envelope.js';

export interface ArtifactBridgeExecutor {
  execute(command: ArtifactBridgeCommand, signal: AbortSignal): Promise<ArtifactBridgeResult>;
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
  listArtifactsForRepo(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly name: string;
      readonly per_page: number;
      readonly page: number;
    },
    signal: AbortSignal,
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
  ): Promise<{ readonly status: number; readonly data: RepositoryArtifact }>;

  downloadArtifactArchive(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly artifact_id: number;
      readonly maximum_bytes: number;
    },
    signal: AbortSignal,
  ): Promise<{ readonly status: number; readonly data: Uint8Array }>;

  getWorkflowRunAttempt(
    input: {
      readonly owner: string;
      readonly repo: string;
      readonly run_id: number;
      readonly attempt_number: number;
    },
    signal: AbortSignal,
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
  readonly now?: () => number;
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
  private readonly now: () => number;
  private readonly artifactClient: ArtifactClient;

  constructor(private readonly context: OfficialArtifactOperationsContext) {
    this.now = context.now ?? Date.now;
    this.artifactClient = context.artifactClient ?? new DefaultArtifactClient();
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
    const budget = new ArtifactBridgeOperationBudget(signal, this.now, this.now());
    try {
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
      const response = await this.callOfficial(
        (requestSignal) =>
          this.context.actions.listArtifactsForRepo(
            {
              owner: this.context.owner,
              repo: this.context.repository,
              name: command.name,
              per_page: ARTIFACT_BRIDGE_LIMITS.recordsPerPage,
              page,
            },
            requestSignal,
          ),
        budget,
      );
      budget.throwIfExpired();
      if (response.status !== 200 || !responseFits(response.data)) {
        throw new BridgeOperationFailure('incomplete');
      }
      if (
        !Array.isArray(response.data.artifacts) ||
        response.data.artifacts.length > ARTIFACT_BRIDGE_LIMITS.recordsPerPage
      ) {
        throw new BridgeOperationFailure('incomplete');
      }
      const total = response.data.total_count;
      if (
        !Number.isSafeInteger(total) ||
        total < 0 ||
        total > ARTIFACT_BRIDGE_LIMITS.maximumRecords ||
        total > maximum ||
        (expectedTotal !== undefined && total !== expectedTotal)
      ) {
        throw new BridgeOperationFailure('incomplete');
      }
      expectedTotal = total;
      for (const artifact of response.data.artifacts) {
        const id = platformId(artifact.id);
        if (artifact.name !== command.name || !id || seen.has(id)) {
          throw new BridgeOperationFailure(id && seen.has(id) ? 'duplicate' : 'incomplete');
        }
        seen.add(id);
        objects.push({ name: command.name, object_id: id });
        if (objects.length > total || objects.length > maximum) {
          throw new BridgeOperationFailure('incomplete');
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
        throw new BridgeOperationFailure('incomplete');
      }
    }
    throw new BridgeOperationFailure('incomplete');
  }

  private async metadata(
    command: Extract<ArtifactBridgeCommand, { operation: 'metadata' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(command.name, command.object_id, budget);
    const record = await this.readRecord(platform, budget);
    return successWithMetadata(command, record.metadata);
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
    assertMetadata(command.expected, record.metadata);
    if (record.bytes.length > Number(command.maximum_bytes)) {
      throw new BridgeOperationFailure('digest_mismatch');
    }
    await this.context.staging.writeDestination(
      command.destination_relative_path,
      record.bytes,
      budget,
    );
    return successWithMetadata(command, record.metadata);
  }

  private async upload(
    command: Extract<ArtifactBridgeCommand, { operation: 'upload_immutable' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    let phase: MutationPhase = 'not_dispatched';
    let metadata: ArtifactMetadataWire | undefined;
    try {
      const source = await this.context.staging.readSource(command.source_relative_path, budget);
      if (digestBytes(source) !== command.encrypted_object_digest) {
        throw new BridgeOperationFailure('invalid', 'not_committed');
      }
      const envelope = encodeArtifactTransportEnvelope(
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
      const minimumExpiry = Number(command.minimum_expires_at_unix_seconds);
      const retentionDays = Math.max(
        1,
        Math.ceil((minimumExpiry * 1000 - this.now()) / 86_400_000),
      );
      const response = await this.callOfficial(() => {
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
      }, budget);
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
      metadata = record.metadata;
      if (
        record.metadata.encrypted_object_digest !== command.encrypted_object_digest ||
        Number(record.metadata.expires_at_unix_seconds) < minimumExpiry
      ) {
        throw new BridgeOperationFailure('conflict', 'committed', record.metadata);
      }
      return {
        ...successWithMetadata(command, record.metadata),
        mutation_state: 'committed',
      };
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
    assertMetadata(command.expected, record.metadata);
    return successWithMetadata(command, record.metadata);
  }

  private async delete(
    command: Extract<ArtifactBridgeCommand, { operation: 'delete_exact' }>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<ArtifactBridgeResult> {
    let phase: MutationPhase = 'not_dispatched';
    try {
      const platform = await this.loadPlatformArtifact(
        command.expected.name,
        command.expected.object_id,
        budget,
      );
      this.assertExpectedPlatform(command.expected, platform);
      const response = await this.callOfficial((requestSignal) => {
        const pending = this.context.actions.deleteArtifact(
          {
            owner: this.context.owner,
            repo: this.context.repository,
            artifact_id: Number(command.expected.object_id),
          },
          requestSignal,
        );
        phase = 'dispatched';
        return pending;
      }, budget);
      budget.throwIfExpired();
      if (response.status !== 204) {
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
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
        error instanceof OfficialCallTimeoutError
      ) {
        throw new BridgeOperationFailure('cancelled', 'not_committed');
      }
      if (error instanceof OfficialCallError) {
        throw new BridgeOperationFailure('io', 'not_committed');
      }
      throw new BridgeOperationFailure('io', 'not_committed');
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
        ),
      budget,
    );
    budget.throwIfExpired();
    if (response.status === 404) {
      throw new BridgeOperationFailure('not_found');
    }
    if (response.status !== 200 || !responseFits(response.data)) {
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
  ): Promise<{ readonly metadata: ArtifactMetadataWire; readonly bytes: Buffer }> {
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
        ),
      budget,
    );
    if (response.status !== 200) {
      throw new BridgeOperationFailure('io');
    }
    budget.throwIfExpired();
    const archive = Buffer.from(response.data);
    if (
      archive.length < 1 ||
      archive.length !== platform.archiveSize ||
      archive.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes ||
      digestBytes(archive) !== platform.archiveDigest
    ) {
      throw new BridgeOperationFailure('digest_mismatch');
    }
    const envelope = await readArtifactArchive(archive, platform.archiveDigest, budget);
    if (envelope.producingRunId !== platform.producingRunId) {
      throw new BridgeOperationFailure('conflict');
    }
    await this.verifyRunAttempt(envelope.producingRunId, envelope.producingRunAttempt, budget);
    return {
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
        ),
      budget,
    );
    budget.throwIfExpired();
    if (
      response.status !== 200 ||
      platformId(response.data.id) !== runId ||
      platformId(response.data.run_attempt ?? undefined) !== runAttempt
    ) {
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
      throw new BridgeOperationFailure('conflict');
    }
  }

  private assertNotExpired(platform: PlatformArtifact): void {
    if (
      platform.expired ||
      Number(platform.expiresAtUnixSeconds) <= Math.floor(this.now() / 1000)
    ) {
      throw new BridgeOperationFailure('expired');
    }
  }

  private async callOfficial<T>(
    call: (signal: AbortSignal) => Promise<T>,
    budget: ArtifactBridgeOperationBudget,
  ): Promise<T> {
    return await runContainedOfficialCall(call, budget.remainingMs(), budget.signal);
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
    } else if (error instanceof OfficialCallError) {
      const status = structuredStatus(error.causeValue);
      if (status === 404) failure = 'not_found';
      else if (provenConflict(error.causeValue) && command.operation === 'upload_immutable') {
        failure = 'conflict';
        mutationState = 'not_committed';
      }
    } else if (error instanceof OfficialCallTimeoutError) {
      failure = 'cancelled';
    } else if (error instanceof ArtifactBridgeDeadlineError) {
      failure = 'cancelled';
    }
    if (
      (command.operation === 'upload_immutable' || command.operation === 'delete_exact') &&
      mutationState === undefined
    ) {
      mutationState =
        failure === 'conflict' || failure === 'invalid' ? 'not_committed' : 'outcome_unknown';
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

function isNotFound(error: unknown): boolean {
  return (
    (error instanceof BridgeOperationFailure && error.failure === 'not_found') ||
    (error instanceof OfficialCallError && structuredStatus(error.causeValue) === 404)
  );
}
