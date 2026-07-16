export type ObservedCapability =
  | 'eligible'
  | 'ineligible'
  | 'unsupported'
  | 'telemetryUnavailable'
  | 'unknown';
export type AggregateCapability = 'eligible' | 'unsupported' | 'unknown';
export type CacheStatus = 'hit' | 'partial' | 'miss' | 'unsupported' | 'unknown';
export type UsageCompleteness = 'complete' | 'partial' | 'missing';
export type ProviderErrorCode =
  | 'provider_timeout'
  | 'provider_4xx'
  | 'provider_5xx'
  | 'provider_rate_limited'
  | 'provider_cancelled'
  | 'capability_unsupported'
  | 'cache_marker_mismatch'
  | 'stateless_proof_missing';
export const METADATA_ERROR_CODES = [
  'invalid-metadata-bounds',
  'invalid-metadata-bom',
  'invalid-metadata-utf8',
  'invalid-metadata-json',
  'invalid-metadata-duplicate-json-property',
  'invalid-metadata-unicode',
  'invalid-metadata-schema',
  'invalid-metadata-additional-property',
  'invalid-metadata-unknown-enum',
  'invalid-metadata-token-out-of-range',
  'invalid-metadata-identity-syntax',
  'invalid-metadata-model-alias-literal',
  'invalid-metadata-provider-identity-cross-mismatch',
  'invalid-metadata-attempt-uniqueness',
  'invalid-metadata-attempt-ordering',
  'invalid-metadata-attempt-contiguity',
  'invalid-metadata-request-ordering',
  'invalid-metadata-multiple-succeeded-attempts',
  'invalid-metadata-attempt-usage-inconsistent',
  'invalid-metadata-attempt-outcome-error-consistency',
  'invalid-metadata-stateless-proof',
  'invalid-metadata-error-code-order',
  'invalid-metadata-aggregate-mismatch',
  'invalid-metadata-error-list-truncated',
] as const;
export type MetadataErrorCode = (typeof METADATA_ERROR_CODES)[number];
export interface MetadataError {
  readonly code: MetadataErrorCode;
  readonly path: string;
}
export interface StatelessProof {
  readonly kind: 'providerAdvertised' | 'synthetic';
  readonly verified: boolean;
}
export interface ProviderRunMetadataV1 {
  readonly schemaVersion: 1;
  readonly selectedProviderId: string;
  readonly observedProviderId: string;
  readonly resolvedModelId: string;
  readonly adapterId: string;
  readonly logicalPrefixSha256: string;
  readonly prefixSha256: string;
  readonly capability: {
    readonly mode: 'standard' | 'stateless';
    readonly aggregate: AggregateCapability;
    readonly statelessProof: StatelessProof | null;
  };
  readonly cacheStatus: CacheStatus;
  readonly normalizedUsage: NormalizedUsage<Attempt>;
  readonly retryObservations: RetryObservations;
  readonly errorCodes: readonly ProviderErrorCode[];
  readonly telemetryCompleteness: TelemetryCompleteness;
  readonly producingRunId: string;
  readonly runAttempt: number;
  readonly interactionId: string;
  readonly consumedInputSha256: string;
  readonly resultSha256: string;
  readonly traceSha256: string;
  readonly predecessorLedgerSha256: string;
  readonly candidateLedgerSha256: string;
}
export interface Attempt {
  readonly requestOrdinal: number;
  readonly attemptOrdinal: number;
  readonly outcome: 'succeeded' | 'failed' | 'cancelled';
  readonly capability: ObservedCapability;
  readonly cacheStatus: CacheStatus;
  readonly usageCompleteness: UsageCompleteness;
  readonly totalInputTokens: number | null;
  readonly uncachedInputTokens: number | null;
  readonly cacheWriteInputTokens: number | null;
  readonly cacheReadInputTokens: number | null;
  readonly outputTokens: number | null;
  readonly attemptErrorCodes: readonly ProviderErrorCode[];
}
export interface RequestUsage {
  readonly requestOrdinal: number;
  readonly capability: ObservedCapability;
  readonly cacheStatus: CacheStatus;
  readonly usageCompleteness: UsageCompleteness;
  readonly totalInputTokens: number | null;
  readonly uncachedInputTokens: number | null;
  readonly cacheWriteInputTokens: number | null;
  readonly cacheReadInputTokens: number | null;
  readonly outputTokens: number | null;
}
export interface NormalizedUsage<TAttempt extends Attempt = Attempt> {
  readonly attempts: readonly TAttempt[];
  readonly requests: readonly RequestUsage[];
  readonly aggregate: {
    readonly totalInputTokens: number | null;
    readonly uncachedInputTokens: number | null;
    readonly cacheWriteInputTokens: number | null;
    readonly cacheReadInputTokens: number | null;
    readonly outputTokens: number | null;
    readonly requestCount: number;
    readonly attemptCount: number;
  };
}
export interface RetryObservations {
  readonly requests: readonly {
    readonly requestOrdinal: number;
    readonly attemptCount: number;
    readonly succeededCount: number;
    readonly failedCount: number;
    readonly cancelledCount: number;
  }[];
  readonly aggregate: {
    readonly requestCount: number;
    readonly attemptCount: number;
    readonly succeededCount: number;
    readonly failedCount: number;
    readonly cancelledCount: number;
  };
}
export interface TelemetryCompleteness {
  readonly usage: 'complete' | 'partial' | 'missing';
  readonly cache: 'complete' | 'partial' | 'missing' | 'unknown';
  readonly statelessProof: 'notApplicable' | 'complete' | 'missing';
  readonly aggregate: 'complete' | 'partial' | 'missing' | 'unknown';
}
declare const validatedBrand: unique symbol;
export type ValidatedProviderRunMetadataV1 = Omit<ProviderRunMetadataV1, 'normalizedUsage'> & {
  readonly normalizedUsage: NormalizedUsage<ValidatedAttempt>;
  readonly [validatedBrand]: true;
};
export type ValidatedAttempt = Attempt & { readonly [validatedBrand]: true };
export interface DerivedProviderRunMetadataAggregate {
  readonly normalizedUsage: {
    readonly requests: readonly RequestUsage[];
    readonly aggregate: NormalizedUsage['aggregate'];
  };
  readonly capability: { readonly aggregate: AggregateCapability };
  readonly cacheStatus: CacheStatus;
  readonly retryObservations: RetryObservations;
  readonly errorCodes: readonly ProviderErrorCode[];
  readonly telemetryCompleteness: TelemetryCompleteness;
}
export type DeriveAggregateInput =
  | {
      readonly attempts: readonly ValidatedAttempt[];
      readonly capabilityMode: 'standard';
      readonly statelessProof: null;
    }
  | {
      readonly attempts: readonly ValidatedAttempt[];
      readonly capabilityMode: 'stateless';
      readonly statelessProof: StatelessProof;
    };
export type DeriveAggregateResult =
  | { readonly valid: true; readonly aggregate: DerivedProviderRunMetadataAggregate }
  | { readonly valid: false; readonly errors: MetadataError[] };
export type ParseProviderRunMetadataResult =
  | { readonly valid: true; readonly metadata: ValidatedProviderRunMetadataV1 }
  | { readonly valid: false; readonly errors: MetadataError[] };
export interface SemanticEnvelope {
  readonly schemaVersion: 1;
  readonly selectedProviderId: string;
  readonly observedProviderId: string;
  readonly resolvedModelId: string;
  readonly adapterId: string;
  readonly logicalPrefixSha256: string;
  readonly prefixSha256: string;
  readonly capability: ProviderRunMetadataV1['capability'];
  readonly cacheStatus: CacheStatus;
  readonly normalizedUsage: NormalizedUsage;
  readonly retryObservations: RetryObservations;
  readonly errorCodes: readonly ProviderErrorCode[];
  readonly telemetryCompleteness: TelemetryCompleteness;
}
export interface HostMetadataIdentity {
  readonly providerId: string;
  readonly resolvedModelId: string;
  readonly adapterId: string;
}
