import { createHash } from 'node:crypto';
import { Ajv, type ErrorObject } from 'ajv';
import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  MAX_METADATA_PATH_CHARS,
  MAX_METADATA_PATH_UTF8_BYTES,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
} from '../state-v2/constants.js';
import {
  normalizePosition,
  resolveArrayItem,
  resolveProperty,
  sanitizeSegment,
  scanStringSafety,
  type SchemaNode,
  UNKNOWN_POSITION,
} from '../state-v2/shared-safe-path.js';
import type {
  AggregateCapability,
  Attempt,
  CacheStatus,
  DeriveAggregateInput,
  DeriveAggregateResult,
  DerivedProviderRunMetadataAggregate,
  HostMetadataIdentity,
  MetadataError,
  MetadataErrorCode,
  ParseProviderRunMetadataResult,
  ProviderErrorCode,
  ProviderRunMetadataV1,
  RequestUsage,
  RetryObservations,
  SemanticEnvelope,
  StatelessProof,
  TelemetryCompleteness,
  ValidatedAttempt,
  ValidatedProviderRunMetadataV1,
} from './types.js';
export * from './types.js';
export { METADATA_ERROR_CODES } from './types.js';
export {
  MAX_METADATA_PATH_CHARS,
  MAX_METADATA_PATH_UTF8_BYTES,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
} from '../state-v2/constants.js';
export const MAX_METADATA_ERRORS = 32 as const;

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: false });
const schemaVersion = (schema as { properties?: { schemaVersion?: { const?: unknown } } })
  .properties?.schemaVersion?.const;
if (schemaVersion !== PROVIDER_RUN_METADATA_SCHEMA_VERSION)
  throw new Error('provider-run-metadata schema version is not bound to the shared constant');
const validateSchema = ajv.compile(schema);
const errorOrder: readonly ProviderErrorCode[] = [
  'provider_timeout',
  'provider_4xx',
  'provider_5xx',
  'provider_rate_limited',
  'provider_cancelled',
  'capability_unsupported',
  'cache_marker_mismatch',
  'stateless_proof_missing',
];
const providerFailures = new Set<ProviderErrorCode>([
  'provider_timeout',
  'provider_4xx',
  'provider_5xx',
  'provider_rate_limited',
]);
const tokenFieldNames = [
  'totalInputTokens',
  'uncachedInputTokens',
  'cacheWriteInputTokens',
  'cacheReadInputTokens',
  'outputTokens',
] as const;
type TokenFieldName = (typeof tokenFieldNames)[number];
interface InternalDerivationResult {
  readonly valid: boolean;
  readonly errors?: MetadataError[];
  readonly aggregate?: import('./types.js').DerivedProviderRunMetadataAggregate;
  readonly invalidRequestFields?: ReadonlyMap<number, ReadonlySet<TokenFieldName>>;
  readonly invalidAggregateFields?: ReadonlySet<TokenFieldName>;
}

export function parseProviderRunMetadata(bytes: Uint8Array): ParseProviderRunMetadataResult {
  if (bytes.byteLength > METADATA_MAX_BYTES) return fail('invalid-metadata-bounds', '');
  const owned = new Uint8Array(bytes);
  if (owned[0] === 0xef && owned[1] === 0xbb && owned[2] === 0xbf)
    return fail('invalid-metadata-bom', '');
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(owned);
  } catch {
    return fail('invalid-metadata-utf8', '');
  }
  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return fail('invalid-metadata-json', '');
  }
  if (hasDuplicateJsonProperty(text)) return fail('invalid-metadata-duplicate-json-property', '');
  const unicode = scanStringSafety(value, schema as unknown as SchemaNode);
  if (unicode) return fail('invalid-metadata-unicode', renderMetadataPath(unicode.segments));
  if (!validateSchema(value))
    return { valid: false, errors: schemaErrors(validateSchema.errors ?? [], value) };
  const semantic = validateSemantic(value as unknown as ProviderRunMetadataV1);
  if (semantic.length) return { valid: false, errors: boundErrors(semantic) };
  return { valid: true, metadata: value as unknown as ValidatedProviderRunMetadataV1 };
}

export function deriveAggregate(input: DeriveAggregateInput): DeriveAggregateResult {
  const result = deriveAggregateInternal(input);
  return result.valid
    ? { valid: true, aggregate: result.aggregate! }
    : { valid: false, errors: result.errors ?? [] };
}

function deriveAggregateInternal(input: DeriveAggregateInput): InternalDerivationResult {
  if (input.capabilityMode === 'stateless' && input.attempts.length === 0)
    return {
      valid: false,
      errors: [{ code: 'invalid-metadata-stateless-proof', path: '/capability' }],
    };
  const proofError = validateStatelessProofConsistency(
    input.attempts,
    input.capabilityMode,
    input.statelessProof,
  );
  const derivationErrors: MetadataError[] = proofError ? [proofError] : [];
  const attempts = [...input.attempts].sort(
    (a, b) => a.requestOrdinal - b.requestOrdinal || a.attemptOrdinal - b.attemptOrdinal,
  );
  const groups = new Map<number, Attempt[]>();
  for (const attempt of attempts)
    (
      groups.get(attempt.requestOrdinal) ??
      (groups.set(attempt.requestOrdinal, []), groups.get(attempt.requestOrdinal)!)
    ).push(attempt);
  const requests: RequestUsage[] = [];
  const retryRequests: RetryObservations['requests'][number][] = [];
  let firstOverflow: MetadataError | undefined;
  const invalidRequestFields = new Map<number, Set<TokenFieldName>>();
  const invalidAggregateFields = new Set<TokenFieldName>();
  for (const [requestOrdinal, group] of groups) {
    const totals = tokenFields(group, attempts.indexOf(group[0]! as ValidatedAttempt));
    firstOverflow ??= totals.error;
    if (totals.invalidFields.size > 0) {
      invalidRequestFields.set(requestOrdinal, totals.invalidFields);
      for (const field of totals.invalidFields) invalidAggregateFields.add(field);
    }
    const capability = reduceCapability(group.map((x) => x.capability));
    const cacheStatus = reduceCache(group.map((x) => x.cacheStatus));
    const usageCompleteness = completeness(totals.values);
    const partitionFields: readonly TokenFieldName[] = [
      'totalInputTokens',
      'uncachedInputTokens',
      'cacheWriteInputTokens',
      'cacheReadInputTokens',
    ];
    const partitionUnavailable = partitionFields.some((field) => totals.invalidFields.has(field));
    if (
      usageCompleteness === 'complete' &&
      !partitionUnavailable &&
      totals.values.uncachedInputTokens! +
        totals.values.cacheWriteInputTokens! +
        totals.values.cacheReadInputTokens! !==
        totals.values.totalInputTokens
    )
      derivationErrors.push({
        code: 'invalid-metadata-attempt-usage-inconsistent',
        path: `/normalizedUsage/requests/${requestOrdinal}`,
      });
    requests.push({ requestOrdinal, capability, cacheStatus, usageCompleteness, ...totals.values });
    retryRequests.push({
      requestOrdinal,
      attemptCount: group.length,
      succeededCount: group.filter((x) => x.outcome === 'succeeded').length,
      failedCount: group.filter((x) => x.outcome === 'failed').length,
      cancelledCount: group.filter((x) => x.outcome === 'cancelled').length,
    });
  }
  const aggregate = sumRequests(requests);
  firstOverflow ??= aggregate.error;
  for (const field of aggregate.invalidFields) invalidAggregateFields.add(field);
  const retryAggregate = sumRetry(retryRequests);
  const capability = reduceRunCapability(requests.map((x) => x.capability));
  const cacheStatus = reduceRunCache(
    requests.map((x) => x.cacheStatus),
    requests.map((x) => x.capability),
  );
  const telemetry = deriveTelemetry(requests, input.capabilityMode, input.statelessProof);
  const codes = [...new Set(attempts.flatMap((x) => x.attemptErrorCodes))].sort(
    (a, b) => errorOrder.indexOf(a) - errorOrder.indexOf(b),
  );
  const derivedAggregate: DerivedProviderRunMetadataAggregate = {
    normalizedUsage: {
      requests,
      aggregate: {
        ...aggregate.values,
        requestCount: requests.length,
        attemptCount: attempts.length,
      },
    },
    capability: { aggregate: capability },
    cacheStatus,
    retryObservations: {
      requests: retryRequests,
      aggregate: {
        ...retryAggregate,
        requestCount: requests.length,
        attemptCount: attempts.length,
      },
    },
    errorCodes: codes,
    telemetryCompleteness: telemetry,
  };
  if (firstOverflow || derivationErrors.length > 0)
    return {
      valid: false,
      errors: [...derivationErrors, ...(firstOverflow ? [firstOverflow] : [])],
      aggregate: derivedAggregate,
      invalidRequestFields,
      invalidAggregateFields,
    };
  return {
    valid: true,
    aggregate: derivedAggregate,
    invalidRequestFields,
    invalidAggregateFields,
  };
}

export function buildSemanticEnvelope(metadata: ValidatedProviderRunMetadataV1): SemanticEnvelope {
  return structuredClone({
    schemaVersion: 1,
    selectedProviderId: metadata.selectedProviderId,
    observedProviderId: metadata.observedProviderId,
    resolvedModelId: metadata.resolvedModelId,
    adapterId: metadata.adapterId,
    logicalPrefixSha256: metadata.logicalPrefixSha256,
    prefixSha256: metadata.prefixSha256,
    capability: {
      mode: metadata.capability.mode,
      aggregate: metadata.capability.aggregate,
      statelessProof: metadata.capability.statelessProof
        ? {
            kind: metadata.capability.statelessProof.kind,
            verified: metadata.capability.statelessProof.verified,
          }
        : null,
    },
    cacheStatus: metadata.cacheStatus,
    normalizedUsage: {
      attempts: metadata.normalizedUsage.attempts.map((attempt) => ({
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
      })),
      requests: metadata.normalizedUsage.requests.map((request) => ({
        requestOrdinal: request.requestOrdinal,
        capability: request.capability,
        cacheStatus: request.cacheStatus,
        usageCompleteness: request.usageCompleteness,
        totalInputTokens: request.totalInputTokens,
        uncachedInputTokens: request.uncachedInputTokens,
        cacheWriteInputTokens: request.cacheWriteInputTokens,
        cacheReadInputTokens: request.cacheReadInputTokens,
        outputTokens: request.outputTokens,
      })),
      aggregate: {
        totalInputTokens: metadata.normalizedUsage.aggregate.totalInputTokens,
        uncachedInputTokens: metadata.normalizedUsage.aggregate.uncachedInputTokens,
        cacheWriteInputTokens: metadata.normalizedUsage.aggregate.cacheWriteInputTokens,
        cacheReadInputTokens: metadata.normalizedUsage.aggregate.cacheReadInputTokens,
        outputTokens: metadata.normalizedUsage.aggregate.outputTokens,
        requestCount: metadata.normalizedUsage.aggregate.requestCount,
        attemptCount: metadata.normalizedUsage.aggregate.attemptCount,
      },
    },
    retryObservations: {
      requests: metadata.retryObservations.requests.map((request) => ({
        requestOrdinal: request.requestOrdinal,
        attemptCount: request.attemptCount,
        succeededCount: request.succeededCount,
        failedCount: request.failedCount,
        cancelledCount: request.cancelledCount,
      })),
      aggregate: {
        requestCount: metadata.retryObservations.aggregate.requestCount,
        attemptCount: metadata.retryObservations.aggregate.attemptCount,
        succeededCount: metadata.retryObservations.aggregate.succeededCount,
        failedCount: metadata.retryObservations.aggregate.failedCount,
        cancelledCount: metadata.retryObservations.aggregate.cancelledCount,
      },
    },
    errorCodes: [...metadata.errorCodes],
    telemetryCompleteness: {
      usage: metadata.telemetryCompleteness.usage,
      cache: metadata.telemetryCompleteness.cache,
      statelessProof: metadata.telemetryCompleteness.statelessProof,
      aggregate: metadata.telemetryCompleteness.aggregate,
    },
  });
}

export function computeMetadataSemanticSha256(metadata: ValidatedProviderRunMetadataV1): string {
  const domain = new TextEncoder().encode('agentic-pr-review/provider-run-metadata-semantic/v1');
  const body = canonicalJsonBytes(buildSemanticEnvelope(metadata));
  const framed = new Uint8Array(domain.byteLength + 1 + body.byteLength);
  framed.set(domain);
  framed[domain.byteLength] = 0;
  framed.set(body, domain.byteLength + 1);
  return createHash('sha256').update(framed).digest('hex');
}

export function identityAgrees(
  metadata: ValidatedProviderRunMetadataV1,
  expected: HostMetadataIdentity,
): boolean {
  return (
    metadata.selectedProviderId === expected.providerId &&
    metadata.observedProviderId === expected.providerId &&
    metadata.resolvedModelId === expected.resolvedModelId &&
    metadata.adapterId === expected.adapterId
  );
}

function validateSemantic(metadata: ProviderRunMetadataV1): MetadataError[] {
  const e: MetadataError[] = [];
  if (metadata.resolvedModelId === 'latest')
    e.push({ code: 'invalid-metadata-model-alias-literal', path: '/resolvedModelId' });
  if (metadata.selectedProviderId !== metadata.observedProviderId)
    e.push({
      code: 'invalid-metadata-provider-identity-cross-mismatch',
      path: '/observedProviderId',
    });
  const attempts = metadata.normalizedUsage.attempts;
  const proofError = validateStatelessProofConsistency(
    attempts,
    metadata.capability.mode,
    metadata.capability.statelessProof,
  );
  if (proofError) e.push(proofError);
  const seenAttempts = new Set<string>();
  let expectedRequest = 0;
  if (
    new Set(metadata.errorCodes).size !== metadata.errorCodes.length ||
    metadata.errorCodes.some(
      (x, i) => i > 0 && errorOrder.indexOf(x) <= errorOrder.indexOf(metadata.errorCodes[i - 1]!),
    )
  )
    e.push({ code: 'invalid-metadata-error-code-order', path: '/errorCodes' });
  for (const [name, value] of Object.entries({
    selectedProviderId: metadata.selectedProviderId,
    observedProviderId: metadata.observedProviderId,
    resolvedModelId: metadata.resolvedModelId,
    producingRunId: metadata.producingRunId,
  }))
    if (new TextEncoder().encode(value).byteLength > 256 || /[\u0000-\u001f\u007f]/.test(value))
      e.push({ code: 'invalid-metadata-identity-syntax', path: `/${name}` });
  for (let i = 0; i < attempts.length; i++) {
    const a = attempts[i]!;
    const expected = i === 0 ? null : attempts[i - 1]!;
    const pair = `${a.requestOrdinal}:${a.attemptOrdinal}`;
    if (seenAttempts.has(pair))
      e.push({
        code: 'invalid-metadata-attempt-uniqueness',
        path: `/normalizedUsage/attempts/${i}`,
      });
    seenAttempts.add(pair);
    if (i === 0 && a.requestOrdinal !== 0)
      e.push({
        code: 'invalid-metadata-request-ordering',
        path: `/normalizedUsage/attempts/${i}/requestOrdinal`,
      });
    if (a.requestOrdinal > expectedRequest)
      e.push({
        code: 'invalid-metadata-request-ordering',
        path: `/normalizedUsage/attempts/${i}/requestOrdinal`,
      });
    if (
      a.requestOrdinal === expectedRequest &&
      (i === 0 || expected?.requestOrdinal !== a.requestOrdinal)
    )
      expectedRequest++;
    if (
      expected &&
      (a.requestOrdinal < expected.requestOrdinal ||
        (a.requestOrdinal === expected.requestOrdinal &&
          a.attemptOrdinal <= expected.attemptOrdinal))
    )
      e.push({ code: 'invalid-metadata-attempt-ordering', path: `/normalizedUsage/attempts/${i}` });
    if (a.attemptOrdinal !== 0 && (!expected || a.requestOrdinal !== expected.requestOrdinal))
      e.push({
        code: 'invalid-metadata-attempt-contiguity',
        path: `/normalizedUsage/attempts/${i}/attemptOrdinal`,
      });
    if (expected && a.requestOrdinal > expected.requestOrdinal + 1)
      e.push({
        code: 'invalid-metadata-request-ordering',
        path: `/normalizedUsage/attempts/${i}/requestOrdinal`,
      });
    if (
      expected &&
      a.requestOrdinal === expected.requestOrdinal &&
      a.attemptOrdinal === expected.attemptOrdinal
    )
      e.push({
        code: 'invalid-metadata-attempt-uniqueness',
        path: `/normalizedUsage/attempts/${i}/attemptOrdinal`,
      });
    if (
      expected &&
      a.requestOrdinal === expected.requestOrdinal &&
      a.attemptOrdinal !== expected.attemptOrdinal + 1
    )
      e.push({
        code: 'invalid-metadata-attempt-contiguity',
        path: `/normalizedUsage/attempts/${i}/attemptOrdinal`,
      });
    if (
      new Set(a.attemptErrorCodes).size !== a.attemptErrorCodes.length ||
      a.attemptErrorCodes.some(
        (x, j) => j > 0 && errorOrder.indexOf(x) <= errorOrder.indexOf(a.attemptErrorCodes[j - 1]!),
      )
    )
      e.push({
        code: 'invalid-metadata-error-code-order',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    if ((a.capability === 'unsupported') !== a.attemptErrorCodes.includes('capability_unsupported'))
      e.push({
        code: 'invalid-metadata-attempt-outcome-error-consistency',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    if ((a.outcome === 'cancelled') !== a.attemptErrorCodes.includes('provider_cancelled'))
      e.push({
        code: 'invalid-metadata-attempt-outcome-error-consistency',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    const tokenValues = [
      a.totalInputTokens,
      a.uncachedInputTokens,
      a.cacheWriteInputTokens,
      a.cacheReadInputTokens,
      a.outputTokens,
    ];
    const tokenComplete = tokenValues.every((x) => x !== null);
    const tokenMissing = tokenValues.every((x) => x === null);
    if (
      (a.usageCompleteness === 'complete' &&
        (!tokenComplete ||
          a.uncachedInputTokens! + a.cacheWriteInputTokens! + a.cacheReadInputTokens! !==
            a.totalInputTokens)) ||
      (a.usageCompleteness === 'missing' && !tokenMissing) ||
      (a.usageCompleteness === 'partial' && (tokenComplete || tokenMissing))
    )
      e.push({
        code: 'invalid-metadata-attempt-usage-inconsistent',
        path: `/normalizedUsage/attempts/${i}/usageCompleteness`,
      });
    if (
      a.outcome === 'failed' &&
      !a.attemptErrorCodes.some((x) => providerFailures.has(x) || x === 'capability_unsupported')
    )
      e.push({
        code: 'invalid-metadata-attempt-outcome-error-consistency',
        path: `/normalizedUsage/attempts/${i}/outcome`,
      });
    if (
      a.outcome === 'succeeded' &&
      a.attemptErrorCodes.some((x) => providerFailures.has(x) || x === 'provider_cancelled')
    )
      e.push({
        code: 'invalid-metadata-attempt-outcome-error-consistency',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    if (a.outcome === 'cancelled' && a.attemptErrorCodes.some((x) => providerFailures.has(x)))
      e.push({
        code: 'invalid-metadata-attempt-outcome-error-consistency',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
  }
  for (let i = 0; i < metadata.normalizedUsage.requests.length; i++) {
    const request = metadata.normalizedUsage.requests[i]!;
    if (request.requestOrdinal !== i)
      e.push({
        code: 'invalid-metadata-request-ordering',
        path: `/normalizedUsage/requests/${i}/requestOrdinal`,
      });
  }
  for (const requestOrdinal of new Set(attempts.map((x) => x.requestOrdinal))) {
    if (
      attempts.filter((x) => x.requestOrdinal === requestOrdinal && x.outcome === 'succeeded')
        .length > 1
    )
      e.push({
        code: 'invalid-metadata-multiple-succeeded-attempts',
        path: '/normalizedUsage/attempts',
      });
  }
  if (attempts.length === 0) {
    if (metadata.capability.mode === 'stateless')
      e.push({ code: 'invalid-metadata-stateless-proof', path: '/capability' });
    if (
      metadata.capability.aggregate !== 'unknown' ||
      metadata.cacheStatus !== 'unknown' ||
      metadata.errorCodes.length !== 0 ||
      metadata.telemetryCompleteness.usage !== 'missing' ||
      metadata.telemetryCompleteness.cache !== 'missing' ||
      metadata.telemetryCompleteness.aggregate !== 'missing' ||
      metadata.telemetryCompleteness.statelessProof !== 'notApplicable' ||
      metadata.normalizedUsage.requests.length !== 0 ||
      metadata.retryObservations.requests.length !== 0 ||
      metadata.normalizedUsage.aggregate.requestCount !== 0 ||
      metadata.normalizedUsage.aggregate.attemptCount !== 0 ||
      metadata.retryObservations.aggregate.requestCount !== 0 ||
      metadata.retryObservations.aggregate.attemptCount !== 0 ||
      metadata.normalizedUsage.aggregate.totalInputTokens !== null ||
      metadata.normalizedUsage.aggregate.uncachedInputTokens !== null ||
      metadata.normalizedUsage.aggregate.cacheWriteInputTokens !== null ||
      metadata.normalizedUsage.aggregate.cacheReadInputTokens !== null ||
      metadata.normalizedUsage.aggregate.outputTokens !== null ||
      metadata.retryObservations.aggregate.succeededCount !== 0 ||
      metadata.retryObservations.aggregate.failedCount !== 0 ||
      metadata.retryObservations.aggregate.cancelledCount !== 0
    )
      e.push({ code: 'invalid-metadata-aggregate-mismatch', path: '/normalizedUsage' });
    return e;
  }
  const derived =
    metadata.capability.mode === 'standard'
      ? deriveAggregateInternal({
          attempts: attempts as readonly ValidatedAttempt[],
          capabilityMode: 'standard',
          statelessProof: null,
        })
      : deriveAggregateInternal({
          attempts: attempts as readonly ValidatedAttempt[],
          capabilityMode: 'stateless',
          statelessProof: metadata.capability.statelessProof as StatelessProof,
        });
  const persistedAggregate = {
    normalizedUsage: {
      requests: metadata.normalizedUsage.requests,
      aggregate: metadata.normalizedUsage.aggregate,
    },
    capability: { aggregate: metadata.capability.aggregate },
    cacheStatus: metadata.cacheStatus,
    retryObservations: metadata.retryObservations,
    errorCodes: metadata.errorCodes,
    telemetryCompleteness: metadata.telemetryCompleteness,
  };
  const invalidRequestFields = derived.invalidRequestFields ?? new Map();
  const invalidAggregateFields = derived.invalidAggregateFields ?? new Set<TokenFieldName>();
  const derivedBytes = derived.aggregate
    ? canonicalJsonBytes(
        maskInvalidTokenFields(derived.aggregate, invalidRequestFields, invalidAggregateFields),
      )
    : new Uint8Array();
  const persistedBytes = canonicalJsonBytes(
    maskInvalidTokenFields(persistedAggregate, invalidRequestFields, invalidAggregateFields),
  );
  if (
    derived.aggregate &&
    (derivedBytes.length !== persistedBytes.length ||
      derivedBytes.some((x, i) => x !== persistedBytes[i]))
  )
    e.push({ code: 'invalid-metadata-aggregate-mismatch', path: '' });
  if (!derived.valid) e.push(...(derived.errors ?? []));
  return e;
}

function maskInvalidTokenFields<
  T extends {
    normalizedUsage: { requests: readonly RequestUsage[]; aggregate: Record<string, unknown> };
  },
>(
  value: T,
  invalidRequestFields: ReadonlyMap<number, ReadonlySet<TokenFieldName>>,
  invalidAggregateFields: ReadonlySet<TokenFieldName>,
): T {
  if (invalidRequestFields.size === 0 && invalidAggregateFields.size === 0) return value;
  const clone = structuredClone(value) as T;
  const aggregate = clone.normalizedUsage.aggregate as Record<string, unknown>;
  for (const field of invalidAggregateFields) {
    aggregate[field] = null;
  }
  for (const [requestOrdinal, fields] of invalidRequestFields) {
    const request = clone.normalizedUsage.requests.find(
      (candidate) => candidate.requestOrdinal === requestOrdinal,
    );
    if (!request) continue;
    for (const field of fields) (request as unknown as Record<string, unknown>)[field] = null;
  }
  return clone;
}

function fail(code: MetadataErrorCode, path: string): ParseProviderRunMetadataResult {
  return { valid: false, errors: [{ code, path }] };
}
function validateStatelessProofConsistency(
  attempts: readonly Attempt[],
  mode: 'standard' | 'stateless',
  proof: StatelessProof | null,
): MetadataError | undefined {
  const occurrences = attempts.flatMap((attempt) =>
    attempt.attemptErrorCodes.filter((code) => code === 'stateless_proof_missing'),
  ).length;
  if (mode === 'standard') {
    if (proof !== null || occurrences !== 0)
      return { code: 'invalid-metadata-stateless-proof', path: '/capability' };
    return undefined;
  }
  if (proof === null)
    return { code: 'invalid-metadata-stateless-proof', path: '/capability/statelessProof' };
  if (proof.verified) {
    return occurrences === 0
      ? undefined
      : { code: 'invalid-metadata-stateless-proof', path: '/normalizedUsage/attempts' };
  }
  if (occurrences !== 1)
    return {
      code: 'invalid-metadata-stateless-proof',
      path: attempts.length === 0 ? '/capability' : '/normalizedUsage/attempts',
    };
  const ordered = [...attempts].sort(
    (a, b) => a.requestOrdinal - b.requestOrdinal || a.attemptOrdinal - b.attemptOrdinal,
  );
  const first = ordered[0]!;
  const containing = attempts.find((attempt) =>
    attempt.attemptErrorCodes.includes('stateless_proof_missing'),
  )!;
  return containing.requestOrdinal === first.requestOrdinal &&
    containing.attemptOrdinal === first.attemptOrdinal
    ? undefined
    : { code: 'invalid-metadata-stateless-proof', path: '/normalizedUsage/attempts' };
}
function boundErrors(errors: MetadataError[]): MetadataError[] {
  const unique = [
    ...new Map(errors.map((error) => [`${error.code}\u0000${error.path}`, error])).values(),
  ];
  unique.sort((a, b) => {
    const path = compareUtf8(a.path, b.path);
    return path || compareAscii(a.code, b.code);
  });
  return unique.length <= MAX_METADATA_ERRORS
    ? unique
    : [
        ...unique.slice(0, MAX_METADATA_ERRORS - 1),
        { code: 'invalid-metadata-error-list-truncated', path: '' },
      ];
}
function schemaErrors(errors: readonly ErrorObject[], value: unknown): MetadataError[] {
  return boundErrors(
    errors.map(
      (x) =>
        ({
          code:
            x.keyword === 'additionalProperties'
              ? 'invalid-metadata-additional-property'
              : x.keyword === 'enum'
                ? 'invalid-metadata-unknown-enum'
                : x.keyword === 'maximum' && String(x.instancePath).toLowerCase().includes('token')
                  ? 'invalid-metadata-token-out-of-range'
                  : 'invalid-metadata-schema',
          path: schemaErrorPath(x, value),
        }) as MetadataError,
    ),
  );
}
function schemaErrorPath(error: ErrorObject, rootValue: unknown): string {
  const base = error.instancePath || '';
  let position = normalizePosition(schema as unknown as SchemaNode);
  let trusted = true;
  const safe: string[] = [];
  let runtimeValue: unknown = rootValue;
  const segments =
    base === ''
      ? []
      : base
          .slice(1)
          .split('/')
          .map((part) => part.replace(/~1/g, '/').replace(/~0/g, '~'));
  for (const segment of segments) {
    const isArray = Array.isArray(runtimeValue);
    const resolved = isArray ? resolveArrayItem(position) : resolveProperty(position, segment);
    const known: boolean = trusted && resolved.schemaKnown;
    safe.push(isArray ? segment : sanitizeSegment(segment, known));
    trusted = known;
    position = known ? resolved.childSchemaPosition : UNKNOWN_POSITION;
    runtimeValue =
      isArray && /^(?:0|[1-9]\d*)$/.test(segment)
        ? (runtimeValue as unknown[])[Number(segment)]
        : runtimeValue && typeof runtimeValue === 'object'
          ? (runtimeValue as Record<string, unknown>)[segment]
          : undefined;
  }
  if (error.keyword === 'additionalProperties') {
    const property = String(
      (error.params as { additionalProperty?: unknown }).additionalProperty ?? '',
    );
    const resolved = resolveProperty(position, property);
    safe.push(sanitizeSegment(property, trusted && resolved.schemaKnown));
  }
  return renderMetadataPath(safe);
}
function compareUtf8(a: string, b: string): number {
  const aa = new TextEncoder().encode(a);
  const bb = new TextEncoder().encode(b);
  for (let i = 0; i < Math.min(aa.length, bb.length); i++)
    if (aa[i] !== bb[i]) return aa[i]! - bb[i]!;
  return aa.length - bb.length;
}
function compareAscii(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}
function renderMetadataPath(segments: readonly string[]): string {
  if (segments.length === 0) return '';
  const pathFits = (parts: readonly string[]) => {
    const path = '/' + parts.join('/');
    return (
      path.length <= MAX_METADATA_PATH_CHARS &&
      new TextEncoder().encode(path).byteLength <= MAX_METADATA_PATH_UTF8_BYTES
    );
  };
  if (pathFits(segments)) return '/' + segments.join('/');
  const finalSegment = segments[segments.length - 1]!;
  const marker = '<path-truncated>';
  const kept: string[] = [];
  for (const segment of segments.slice(0, -1)) {
    const candidate = [...kept, segment, marker, finalSegment];
    if (!pathFits(candidate)) break;
    kept.push(segment);
  }
  return '/' + [...kept, marker, finalSegment].join('/');
}
function hasDuplicateJsonProperty(text: string): boolean {
  const stack: Set<string>[] = [];
  let i = 0;
  while (i < text.length) {
    if (text[i] === '"') {
      const start = ++i;
      let s = '';
      while (i < text.length) {
        if (text[i] === '\\') {
          s += text[i++] + (text[i++] ?? '');
          continue;
        }
        if (text[i] === '"') break;
        s += text[i++];
      }
      const key = JSON.parse('"' + text.slice(start, i) + '"');
      i++;
      let j = i;
      while (/\s/.test(text[j] ?? '')) j++;
      if (text[j] === ':') {
        const set = stack[stack.length - 1];
        if (set?.has(key)) return true;
        set?.add(key);
      }
    } else if (text[i] === '{') stack.push(new Set());
    else if (text[i] === '}') stack.pop();
    i++;
  }
  return false;
}
function tokenFields(group: readonly Attempt[], groupOffset: number) {
  const names = tokenFieldNames;
  const values = {} as Record<(typeof names)[number], number | null>;
  let overflow: MetadataError | undefined;
  const invalidFields = new Set<TokenFieldName>();
  for (const name of names) {
    if (group.some((item) => item[name] === null)) {
      values[name] = null;
      continue;
    }
    let total = 0;
    for (const item of group) {
      const value = item[name];
      if (value === null) continue;
      if (total > Number.MAX_SAFE_INTEGER - value) {
        invalidFields.add(name);
        overflow ??= {
          code: 'invalid-metadata-token-out-of-range',
          path: `/normalizedUsage/attempts/${groupOffset + group.indexOf(item)}/${name}`,
        };
        break;
      }
      total += value;
    }
    values[name] = total;
  }
  return {
    values,
    error: overflow,
    invalidFields,
  };
}
function sumRequests(reqs: readonly RequestUsage[]) {
  const names = tokenFieldNames;
  if (reqs.length === 0)
    return {
      values: {
        totalInputTokens: null,
        uncachedInputTokens: null,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: null,
        outputTokens: null,
      },
      error: undefined,
      invalidFields: new Set<TokenFieldName>(),
    };
  const values = {} as Record<(typeof names)[number], number | null>;
  let overflow: MetadataError | undefined;
  const invalidFields = new Set<TokenFieldName>();
  for (const name of names) {
    if (reqs.some((item) => item[name] === null)) {
      values[name] = null;
      continue;
    }
    let total = 0;
    for (const item of reqs) {
      const value = item[name];
      if (value === null) continue;
      if (total > Number.MAX_SAFE_INTEGER - value) {
        invalidFields.add(name);
        overflow ??= {
          code: 'invalid-metadata-token-out-of-range',
          path: `/normalizedUsage/requests/${item.requestOrdinal}/${name}`,
        };
        break;
      }
      total += value;
    }
    values[name] = total;
  }
  return {
    values,
    error: overflow,
    invalidFields,
  };
}
function sumRetry(reqs: readonly RetryObservations['requests'][number][]) {
  return {
    succeededCount: reqs.reduce((n, x) => n + x.succeededCount, 0),
    failedCount: reqs.reduce((n, x) => n + x.failedCount, 0),
    cancelledCount: reqs.reduce((n, x) => n + x.cancelledCount, 0),
  };
}
function completeness(v: Record<string, number | null>): 'complete' | 'partial' | 'missing' {
  const vals = Object.values(v);
  return vals.every((x) => x === null)
    ? 'missing'
    : vals.every((x) => x !== null)
      ? 'complete'
      : 'partial';
}
function reduceCapability(v: readonly string[]): any {
  return v.includes('unsupported')
    ? 'unsupported'
    : v.includes('ineligible')
      ? 'ineligible'
      : v.includes('telemetryUnavailable')
        ? 'telemetryUnavailable'
        : v.includes('unknown')
          ? 'unknown'
          : 'eligible';
}
function reduceRunCapability(v: readonly string[]): AggregateCapability {
  if (v.length === 0) return 'unknown';
  return v.includes('unsupported') || v.includes('ineligible')
    ? 'unsupported'
    : v.includes('telemetryUnavailable') || v.includes('unknown')
      ? 'unknown'
      : 'eligible';
}
function reduceCache(v: readonly string[]): CacheStatus {
  return v.includes('unsupported')
    ? 'unsupported'
    : v.includes('unknown')
      ? 'unknown'
      : v.includes('partial') || (v.includes('hit') && v.includes('miss'))
        ? 'partial'
        : v.every((x) => x === 'hit')
          ? 'hit'
          : 'miss';
}
function reduceRunCache(v: readonly string[], capabilities: readonly string[] = []): CacheStatus {
  if (v.length === 0) return 'unknown';
  if (capabilities.some((x) => x === 'unsupported' || x === 'ineligible')) return 'unsupported';
  if (capabilities.some((x) => x === 'telemetryUnavailable' || x === 'unknown')) return 'unknown';
  return reduceCache(v);
}
function deriveTelemetry(
  reqs: readonly RequestUsage[],
  mode: 'standard' | 'stateless',
  proof: StatelessProof | null,
): TelemetryCompleteness {
  const usage =
    reqs.length === 0 || reqs.every((x) => x.usageCompleteness === 'missing')
      ? 'missing'
      : reqs.every((x) => x.usageCompleteness === 'complete')
        ? 'complete'
        : 'partial';
  const cache =
    reqs.length === 0 || reqs.every((x) => x.cacheStatus === 'unsupported')
      ? 'missing'
      : reqs.some((x) => x.cacheStatus === 'unknown')
        ? 'unknown'
        : reqs.some((x) => x.cacheStatus === 'unsupported')
          ? 'partial'
          : 'complete';
  const statelessProof =
    mode === 'standard' ? 'notApplicable' : proof?.verified ? 'complete' : 'missing';
  const aggregate =
    cache === 'unknown'
      ? 'unknown'
      : usage === 'complete' && cache === 'complete' && statelessProof !== 'missing'
        ? 'complete'
        : usage === 'missing' && cache === 'missing' && statelessProof !== 'complete'
          ? 'missing'
          : 'partial';
  return { usage, cache, statelessProof, aggregate };
}
