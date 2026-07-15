/**
 * metadataSemanticSha256 helper.
 *
 * Domain tag (ASCII, fixed): agentic-pr-review/provider-run-metadata-semantic/v1
 * Hash: SHA256(UTF8(tag) || 0x00 || RFC8785(semanticMetadata))
 *
 * semanticMetadata is constructed by an allowlist so a new metadata field cannot
 * silently enter the hash. The envelope is rebuilt at every nesting level, so
 * an unvalidated caller cannot cause unknown nested fields to leak into the
 * hash preimage. The excluded (transaction-binding + provenance) fields are:
 * producingRunId, runAttempt, interactionId, consumedInputSha256, resultSha256,
 * traceSha256, predecessorLedgerSha256, candidateLedgerSha256.
 */

import { createHash } from 'node:crypto';
import type {
  AttemptObservation,
  CapabilityBlock,
  NormalizedUsage,
  ProviderRunMetadataV1,
  RequestObservation,
  RetryAggregate,
  RetryObservations,
  RetryRequestEntry,
  StatelessProof,
  TelemetryCompleteness,
  UsageAggregate,
  ValidatedProviderRunMetadataV1,
} from './types.js';
import { METADATA_SEMANTIC_HASH_DOMAIN_TAG } from './types.js';
import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';

export interface SemanticEnvelope {
  schemaVersion: 1;
  selectedProviderId: string;
  observedProviderId: string;
  resolvedModelId: string;
  adapterId: string;
  logicalPrefixSha256: string;
  prefixSha256: string;
  capability: CapabilityBlock;
  cacheStatus: ProviderRunMetadataV1['cacheStatus'];
  normalizedUsage: NormalizedUsage;
  retryObservations: RetryObservations;
  errorCodes: ProviderRunMetadataV1['errorCodes'];
  telemetryCompleteness: TelemetryCompleteness;
}

function rebuildStatelessProof(proof: StatelessProof | null): StatelessProof | null {
  if (proof === null) return null;
  return { kind: proof.kind, verified: proof.verified };
}

function rebuildCapability(capability: CapabilityBlock): CapabilityBlock {
  return {
    mode: capability.mode,
    aggregate: capability.aggregate,
    statelessProof: rebuildStatelessProof(capability.statelessProof),
  };
}

function rebuildAttempt(attempt: AttemptObservation): AttemptObservation {
  return {
    requestOrdinal: attempt.requestOrdinal,
    attemptOrdinal: attempt.attemptOrdinal,
    outcome: attempt.outcome,
    capability: attempt.capability,
    cacheStatus: attempt.cacheStatus,
    usageCompleteness: attempt.usageCompleteness,
    totalInputTokens: attempt.totalInputTokens,
    uncachedInputTokens: attempt.uncachedInputTokens,
    cacheWriteInputTokens: attempt.cacheWriteInputTokens,
    cacheReadInputTokens: attempt.cacheReadInputTokens,
    outputTokens: attempt.outputTokens,
    attemptErrorCodes: [...attempt.attemptErrorCodes],
  };
}

function rebuildRequest(request: RequestObservation): RequestObservation {
  return {
    requestOrdinal: request.requestOrdinal,
    capability: request.capability,
    cacheStatus: request.cacheStatus,
    usageCompleteness: request.usageCompleteness,
    totalInputTokens: request.totalInputTokens,
    uncachedInputTokens: request.uncachedInputTokens,
    cacheWriteInputTokens: request.cacheWriteInputTokens,
    cacheReadInputTokens: request.cacheReadInputTokens,
    outputTokens: request.outputTokens,
  };
}

function rebuildUsageAggregate(usage: UsageAggregate): UsageAggregate {
  return {
    totalInputTokens: usage.totalInputTokens,
    uncachedInputTokens: usage.uncachedInputTokens,
    cacheWriteInputTokens: usage.cacheWriteInputTokens,
    cacheReadInputTokens: usage.cacheReadInputTokens,
    outputTokens: usage.outputTokens,
    requestCount: usage.requestCount,
    attemptCount: usage.attemptCount,
  };
}

function rebuildNormalizedUsage(usage: NormalizedUsage): NormalizedUsage {
  return {
    attempts: usage.attempts.map(rebuildAttempt),
    requests: usage.requests.map(rebuildRequest),
    aggregate: rebuildUsageAggregate(usage.aggregate),
  };
}

function rebuildRetryRequest(request: RetryRequestEntry): RetryRequestEntry {
  return {
    requestOrdinal: request.requestOrdinal,
    attemptCount: request.attemptCount,
    succeededCount: request.succeededCount,
    failedCount: request.failedCount,
    cancelledCount: request.cancelledCount,
  };
}

function rebuildRetryAggregate(aggregate: RetryAggregate): RetryAggregate {
  return {
    requestCount: aggregate.requestCount,
    attemptCount: aggregate.attemptCount,
    succeededCount: aggregate.succeededCount,
    failedCount: aggregate.failedCount,
    cancelledCount: aggregate.cancelledCount,
  };
}

function rebuildRetryObservations(obs: RetryObservations): RetryObservations {
  return {
    requests: obs.requests.map(rebuildRetryRequest),
    aggregate: rebuildRetryAggregate(obs.aggregate),
  };
}

function rebuildTelemetryCompleteness(t: TelemetryCompleteness): TelemetryCompleteness {
  return {
    usage: t.usage,
    cache: t.cache,
    statelessProof: t.statelessProof,
    aggregate: t.aggregate,
  };
}

/**
 * Allowlist-based envelope construction. The envelope is rebuilt at every
 * nesting level so an unvalidated caller cannot smuggle unknown nested fields
 * into the hash preimage.
 */
export function buildSemanticEnvelope(metadata: ValidatedProviderRunMetadataV1): SemanticEnvelope {
  return {
    schemaVersion: metadata.schemaVersion,
    selectedProviderId: metadata.selectedProviderId,
    observedProviderId: metadata.observedProviderId,
    resolvedModelId: metadata.resolvedModelId,
    adapterId: metadata.adapterId,
    logicalPrefixSha256: metadata.logicalPrefixSha256,
    prefixSha256: metadata.prefixSha256,
    capability: rebuildCapability(metadata.capability),
    cacheStatus: metadata.cacheStatus,
    normalizedUsage: rebuildNormalizedUsage(metadata.normalizedUsage),
    retryObservations: rebuildRetryObservations(metadata.retryObservations),
    errorCodes: [...metadata.errorCodes],
    telemetryCompleteness: rebuildTelemetryCompleteness(metadata.telemetryCompleteness),
  };
}

export function computeMetadataSemanticSha256(metadata: ValidatedProviderRunMetadataV1): string {
  const envelope = buildSemanticEnvelope(metadata);
  const canonicalBytes = canonicalJsonBytes(envelope as unknown as CanonicalJsonValue);
  const hash = createHash('sha256');
  hash.update(Buffer.from(METADATA_SEMANTIC_HASH_DOMAIN_TAG, 'utf8'));
  hash.update(Buffer.from([0x00]));
  hash.update(Buffer.from(canonicalBytes));
  return hash.digest('hex');
}
