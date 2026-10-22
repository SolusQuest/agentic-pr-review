import { DefaultArtifactClient, type ArtifactClient } from '@actions/artifact';
import type {
  ArtifactBridgeCommand,
  ArtifactBridgeResult,
  ArtifactUploadReceipt,
} from './contracts.js';
import { safePositiveDecimal, sha256 } from './contracts.js';
import { ArtifactLifecycleCoordinator } from './artifact-lifecycle-coordinator.js';
import { ArtifactBridgeOperationBudget, ArtifactBridgeDeadlineError } from './operation-budget.js';
import {
  OfficialCallError,
  OfficialCallTimeoutError,
  runContainedOfficialCall,
} from './official-output.js';
import { physicalArtifactName } from './physical-name.js';
import { ArtifactBridgeStaging, ArtifactBridgeStagingError } from './staging.js';
import {
  digestBytes,
  encodeArtifactTransportEnvelope,
  ArtifactTransportEnvelopeError,
} from './transport-envelope.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export interface ArtifactBridgeExecutor {
  execute(command: ArtifactBridgeCommand, signal: AbortSignal): Promise<ArtifactBridgeResult>;
  stopAndDrain?(): Promise<void>;
  dispose?(): void | Promise<void>;
}

export interface OfficialArtifactOperationsContext {
  readonly currentRunId: string;
  readonly currentRunAttempt: string;
  readonly staging: ArtifactBridgeStaging;
  readonly artifactClient?: ArtifactClient;
  readonly monotonicNow?: () => number;
  readonly utcNow?: () => number;
}

// T1 transitional capability: the raw-source request and mechanical carrier
// glue remain until T2. There is no GitHub token, REST client or accepted store
// metadata here. C# alone verifies the provisional SDK receipt through REST.
export class OfficialArtifactOperations implements ArtifactBridgeExecutor {
  private readonly coordinator = new ArtifactLifecycleCoordinator();
  private readonly client: ArtifactClient;
  private readonly now: () => number;
  private readonly utcNow: () => number;

  constructor(private readonly context: OfficialArtifactOperationsContext) {
    if (
      !safePositiveDecimal(context.currentRunId) ||
      !safePositiveDecimal(context.currentRunAttempt)
    )
      throw new Error('artifact_bridge_context_invalid');
    this.client = context.artifactClient ?? new DefaultArtifactClient();
    this.now = context.monotonicNow ?? (() => performance.now());
    this.utcNow = context.utcNow ?? (() => Date.now());
  }

  async execute(
    command: ArtifactBridgeCommand,
    signal: AbortSignal,
  ): Promise<ArtifactBridgeResult> {
    return await this.coordinator.run(signal, async () => {
      if (command.operation !== 'upload_immutable') {
        return {
          operation: command.operation,
          correlation_id: command.correlation_id,
          failure: 'invalid',
          ...(command.operation === 'list_exact' ? { complete: false } : {}),
          ...(command.operation === 'delete_exact'
            ? { mutation_state: 'not_committed' as const }
            : {}),
        };
      }
      const budget = new ArtifactBridgeOperationBudget(signal, this.now, this.now());
      let dispatched = false;
      let receipt: ArtifactUploadReceipt | null = null;
      let source: Buffer | undefined;
      let envelope: Buffer | undefined;
      const result = (failure: ArtifactBridgeResult['failure']): ArtifactBridgeResult => ({
        operation: command.operation,
        correlation_id: command.correlation_id,
        dispatch: dispatched ? 'dispatched' : 'not_dispatched',
        failure,
        receipt,
      });
      try {
        source = await this.context.staging.readSource(command.source_relative_path, budget);
        if (digestBytes(source) !== command.encrypted_object_digest) return result('invalid');
        envelope = encodeArtifactTransportEnvelope(
          this.context.currentRunId,
          this.context.currentRunAttempt,
          source,
          command.encrypted_object_digest,
          budget,
        );
        const staged = await this.context.staging.writeUploadEnvelope(
          command.source_relative_path,
          envelope,
          budget,
        );
        source.fill(0);
        source = undefined;
        envelope.fill(0);
        envelope = undefined;
        const retentionDays = Math.max(
          1,
          Math.ceil(
            (Number(command.minimum_expires_at_unix_seconds) * 1000 - this.utcNow()) / 86_400_000,
          ),
        );
        const response = await runContainedOfficialCall(
          () => {
            // Mark before entering the SDK, including a synchronous exception.
            dispatched = true;
            return this.client.uploadArtifact(
              physicalArtifactName(command.name, command.encrypted_object_digest),
              [staged.envelopePath],
              staged.operationDirectory,
              { retentionDays, compressionLevel: 0 },
            );
          },
          budget.remainingMs(),
          budget.signal,
          budget.latestHttpAttemptStartAt(),
          this.now,
        );
        const id =
          typeof response.id === 'number' && Number.isSafeInteger(response.id) && response.id > 0
            ? String(response.id)
            : undefined;
        const digest = typeof response.digest === 'string' ? sha256(response.digest) : undefined;
        const size =
          typeof response.size === 'number' &&
          Number.isSafeInteger(response.size) &&
          response.size > 0 &&
          response.size <= ARTIFACT_BRIDGE_LIMITS.maximumArchiveBytes
            ? String(response.size)
            : undefined;
        if (!id || !digest || !size) return result('outcome_unknown');
        receipt = { artifact_id: id, archive_sha256: digest, archive_size: size };
        budget.throwIfExpired();
        return result('none');
      } catch (error) {
        if (dispatched) {
          if (error instanceof OfficialCallError && provenConflict(error.causeValue))
            return result('conflict');
          return result('outcome_unknown');
        }
        return result(
          error instanceof ArtifactBridgeDeadlineError ||
            error instanceof OfficialCallTimeoutError ||
            signal.aborted
            ? 'cancelled'
            : error instanceof ArtifactBridgeStagingError ||
                error instanceof ArtifactTransportEnvelopeError
              ? 'invalid'
              : 'io',
        );
      } finally {
        source?.fill(0);
        envelope?.fill(0);
        budget.dispose();
      }
    });
  }

  async stopAndDrain(): Promise<void> {
    this.coordinator.stopIntake();
    await this.coordinator.drain();
  }
  async dispose(): Promise<void> {
    await this.stopAndDrain();
  }
}

function provenConflict(error: unknown): boolean {
  if (typeof error !== 'object' || error === null) return false;
  const code = (error as { readonly code?: unknown }).code;
  return code === 6 || code === 'already_exists';
}
