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
import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import {
  ArtifactTransportEnvelopeError,
  digestBytes,
  readArtifactArchive,
  writeArtifactTransportEnvelope,
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
    const startedAt = this.now();
    try {
      switch (command.operation) {
        case 'list_exact':
          return await this.listExact(command, signal, startedAt);
        case 'metadata':
          return await this.metadata(command, signal, startedAt);
        case 'download':
          return await this.download(command, signal, startedAt);
        case 'upload_immutable':
          return await this.upload(command, signal, startedAt);
        case 'readback_exact':
          return await this.readBack(command, signal, startedAt);
        case 'delete_exact':
          return await this.delete(command, signal, startedAt);
      }
    } catch (error) {
      return this.failureResult(command, error);
    }
  }

  private async listExact(
    command: Extract<ArtifactBridgeCommand, { operation: 'list_exact' }>,
    signal: AbortSignal,
    startedAt: number,
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
        signal,
        startedAt,
      );
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
    signal: AbortSignal,
    startedAt: number,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.name,
      command.object_id,
      signal,
      startedAt,
    );
    const record = await this.readRecord(platform, signal, startedAt);
    return successWithMetadata(command, record.metadata);
  }

  private async download(
    command: Extract<ArtifactBridgeCommand, { operation: 'download' }>,
    signal: AbortSignal,
    startedAt: number,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.expected.name,
      command.expected.object_id,
      signal,
      startedAt,
    );
    this.assertExpectedPlatform(command.expected, platform);
    const record = await this.readRecord(platform, signal, startedAt);
    assertMetadata(command.expected, record.metadata);
    if (record.bytes.length > Number(command.maximum_bytes)) {
      throw new BridgeOperationFailure('digest_mismatch');
    }
    await this.context.staging.writeDestination(command.destination_relative_path, record.bytes);
    return successWithMetadata(command, record.metadata);
  }

  private async upload(
    command: Extract<ArtifactBridgeCommand, { operation: 'upload_immutable' }>,
    signal: AbortSignal,
    startedAt: number,
  ): Promise<ArtifactBridgeResult> {
    const source = await this.context.staging.readSource(command.source_relative_path);
    if (digestBytes(source) !== command.encrypted_object_digest) {
      throw new BridgeOperationFailure('invalid', 'not_committed');
    }
    const operationDirectory = await this.context.staging.createOperationDirectory();
    let lateSettlement: Promise<void> | undefined;
    let cleanupMutationState: ArtifactBridgeMutationState = 'not_committed';
    let cleanupMetadata: ArtifactMetadataWire | undefined;
    try {
      const envelopePath = await writeArtifactTransportEnvelope(
        operationDirectory,
        this.context.currentRunId,
        this.context.currentRunAttempt,
        source,
        command.encrypted_object_digest,
      );
      const minimumExpiry = Number(command.minimum_expires_at_unix_seconds);
      const retentionDays = Math.max(
        1,
        Math.ceil((minimumExpiry * 1000 - this.now()) / 86_400_000),
      );
      const response = await this.callOfficial(
        () =>
          this.artifactClient.uploadArtifact(command.name, [envelopePath], operationDirectory, {
            retentionDays,
            compressionLevel: 0,
          }),
        signal,
        startedAt,
      );
      cleanupMutationState = 'outcome_unknown';
      const objectId = platformId(response.id);
      const archiveDigest = parseUploadResponseDigest(response.digest);
      if (!objectId || !archiveDigest) {
        throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
      }
      cleanupMutationState = 'committed';
      const platform = await this.loadPlatformArtifact(command.name, objectId, signal, startedAt);
      if (
        platform.archiveDigest !== archiveDigest ||
        platform.producingRunId !== this.context.currentRunId
      ) {
        throw new BridgeOperationFailure('conflict', 'committed');
      }
      const record = await this.readRecord(platform, signal, startedAt);
      cleanupMetadata = record.metadata;
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
      if (error instanceof OfficialCallTimeoutError) {
        lateSettlement = error.settled;
      }
      let propagatedError: BridgeOperationFailure;
      if (error instanceof BridgeOperationFailure) {
        cleanupMutationState = error.mutationState ?? cleanupMutationState;
        cleanupMetadata = error.metadata ?? cleanupMetadata;
        propagatedError = new BridgeOperationFailure(
          error.failure,
          cleanupMutationState,
          cleanupMetadata,
        );
      } else if (
        cleanupMutationState === 'not_committed' &&
        error instanceof OfficialCallError &&
        provenConflict(error.causeValue)
      ) {
        propagatedError = new BridgeOperationFailure('conflict', 'not_committed');
      } else if (
        cleanupMutationState === 'not_committed' &&
        (error instanceof OfficialCallError || error instanceof OfficialCallTimeoutError)
      ) {
        cleanupMutationState = 'outcome_unknown';
        propagatedError = new BridgeOperationFailure('outcome_unknown', cleanupMutationState);
      } else if (error instanceof OfficialCallTimeoutError) {
        propagatedError = new BridgeOperationFailure(
          'cancelled',
          cleanupMutationState,
          cleanupMetadata,
        );
      } else if (error instanceof OfficialCallError) {
        propagatedError = new BridgeOperationFailure(
          structuredStatus(error.causeValue) === 404 ? 'not_found' : 'io',
          cleanupMutationState,
          cleanupMetadata,
        );
      } else if (
        error instanceof ArtifactBridgeStagingError ||
        error instanceof ArtifactTransportEnvelopeError
      ) {
        propagatedError = new BridgeOperationFailure(
          'invalid',
          cleanupMutationState,
          cleanupMetadata,
        );
      } else {
        propagatedError = new BridgeOperationFailure('io', cleanupMutationState, cleanupMetadata);
      }
      throw propagatedError;
    } finally {
      if (lateSettlement) {
        scheduleLateCleanup(lateSettlement, this.context.staging, operationDirectory);
      } else {
        try {
          await this.context.staging.cleanupOperationDirectory(operationDirectory);
        } catch {
          throw new BridgeOperationFailure('cleanup', cleanupMutationState, cleanupMetadata);
        }
      }
    }
  }

  private async readBack(
    command: Extract<ArtifactBridgeCommand, { operation: 'readback_exact' }>,
    signal: AbortSignal,
    startedAt: number,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.expected.name,
      command.expected.object_id,
      signal,
      startedAt,
    );
    this.assertExpectedPlatform(command.expected, platform);
    this.assertNotExpired(platform);
    const record = await this.readRecord(platform, signal, startedAt);
    assertMetadata(command.expected, record.metadata);
    return successWithMetadata(command, record.metadata);
  }

  private async delete(
    command: Extract<ArtifactBridgeCommand, { operation: 'delete_exact' }>,
    signal: AbortSignal,
    startedAt: number,
  ): Promise<ArtifactBridgeResult> {
    const platform = await this.loadPlatformArtifact(
      command.expected.name,
      command.expected.object_id,
      signal,
      startedAt,
    );
    this.assertExpectedPlatform(command.expected, platform);
    const response = await this.callOfficial(
      (requestSignal) =>
        this.context.actions.deleteArtifact(
          {
            owner: this.context.owner,
            repo: this.context.repository,
            artifact_id: Number(command.expected.object_id),
          },
          requestSignal,
        ),
      signal,
      startedAt,
    );
    if (response.status !== 204) {
      throw new BridgeOperationFailure('outcome_unknown', 'outcome_unknown');
    }
    try {
      await this.loadPlatformArtifact(
        command.expected.name,
        command.expected.object_id,
        signal,
        startedAt,
      );
    } catch (error) {
      if (
        (error instanceof BridgeOperationFailure && error.failure === 'not_found') ||
        (error instanceof OfficialCallError && structuredStatus(error.causeValue) === 404)
      ) {
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
  }

  private async loadPlatformArtifact(
    expectedName: string,
    objectId: string,
    signal: AbortSignal,
    startedAt: number,
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
      signal,
      startedAt,
    );
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
    signal: AbortSignal,
    startedAt: number,
  ): Promise<{ readonly metadata: ArtifactMetadataWire; readonly bytes: Buffer }> {
    const operationDirectory = await this.context.staging.createOperationDirectory();
    let lateSettlement: Promise<void> | undefined;
    try {
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
        signal,
        startedAt,
      );
      if (response.status !== 200) {
        throw new BridgeOperationFailure('io');
      }
      const archive = Buffer.from(response.data);
      if (
        archive.length < 1 ||
        archive.length !== platform.archiveSize ||
        archive.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes ||
        digestBytes(archive) !== platform.archiveDigest
      ) {
        throw new BridgeOperationFailure('digest_mismatch');
      }
      const archivePath = await this.context.staging.writeArchive(operationDirectory, archive);
      const envelope = await readArtifactArchive(archivePath, platform.archiveDigest);
      if (envelope.producingRunId !== platform.producingRunId) {
        throw new BridgeOperationFailure('conflict');
      }
      await this.verifyRunAttempt(
        envelope.producingRunId,
        envelope.producingRunAttempt,
        signal,
        startedAt,
      );
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
    } catch (error) {
      if (error instanceof OfficialCallTimeoutError) {
        lateSettlement = error.settled;
      }
      throw error;
    } finally {
      if (lateSettlement) {
        scheduleLateCleanup(lateSettlement, this.context.staging, operationDirectory);
      } else {
        try {
          await this.context.staging.cleanupOperationDirectory(operationDirectory);
        } catch {
          throw new BridgeOperationFailure('cleanup');
        }
      }
    }
  }

  private async verifyRunAttempt(
    runId: string,
    runAttempt: string,
    signal: AbortSignal,
    startedAt: number,
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
      signal,
      startedAt,
    );
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
    signal: AbortSignal,
    startedAt: number,
  ): Promise<T> {
    const remaining = ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs - (this.now() - startedAt);
    if (remaining <= 0) {
      throw new BridgeOperationFailure('cancelled');
    }
    const result = await runContainedOfficialCall(call, remaining, signal);
    if (this.now() - startedAt >= ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs) {
      throw new BridgeOperationFailure('cancelled');
    }
    return result;
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
  if (structuredStatus(error) === 409) return true;
  if (typeof error !== 'object' || error === null) return false;
  const record = error as Record<string, unknown>;
  return record.code === 6 || record.code === 'already_exists';
}

function scheduleLateCleanup(
  settled: Promise<void>,
  staging: ArtifactBridgeStaging,
  operationDirectory: string,
): void {
  const cleanup = async (): Promise<void> => {
    await staging.cleanupOperationDirectory(operationDirectory);
  };
  void settled.then(cleanup, cleanup).catch(() => undefined);
}
