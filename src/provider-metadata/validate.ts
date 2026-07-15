/**
 * Stage-8 semantic validator for ProviderRunMetadataV1.
 *
 * Runs after the raw-transport, string-safety, and JSON Schema stages have all
 * passed (issue #51). Emits every applicable diagnostic in a single pass so
 * multi-violation inputs produce deterministic output after
 * `finalizeErrors(...)`.
 *
 * Codes owned by this stage:
 *   invalid-metadata-identity-syntax
 *   invalid-metadata-model-alias-literal
 *   invalid-metadata-provider-identity-cross-mismatch
 *   invalid-metadata-attempt-uniqueness
 *   invalid-metadata-attempt-ordering
 *   invalid-metadata-attempt-contiguity
 *   invalid-metadata-request-ordering
 *   invalid-metadata-multiple-succeeded-attempts
 *   invalid-metadata-attempt-usage-inconsistent
 *   invalid-metadata-attempt-outcome-error-consistency
 *   invalid-metadata-stateless-proof
 *   invalid-metadata-error-code-order
 *   invalid-metadata-aggregate-mismatch
 *   invalid-metadata-token-out-of-range (aggregate-sum overflow only)
 */

import { deriveAggregateInternal } from './aggregate.js';
import {
  ALLOWED_ERROR_CODES,
  IDENTITY_CONTROL_CHARS_REGEX,
  IDENTITY_STRING_MAX_UTF8_BYTES,
  type AttemptObservation,
  type ErrorCode,
  type MetadataError,
  type MetadataErrorCode,
  type ProviderRunMetadataV1,
} from './types.js';
import { finalizePath, utf8ByteLength } from './safe-path-helpers.js';

export interface Stage8Result {
  metadata: ProviderRunMetadataV1;
  errors: MetadataError[];
}

export function validateStage8(m: ProviderRunMetadataV1): Stage8Result {
  const errors: MetadataError[] = [];
  validateIdentitySyntax(m, errors);
  validateProviderIdentityCross(m, errors);
  validateModelAliasLiteral(m, errors);
  const attempts = m.normalizedUsage.attempts;
  validateAttemptOrdering(attempts, errors);
  validateAttemptUsageCrossField(attempts, errors);
  validateOutcomeErrorConsistency(attempts, errors);
  validateStatelessDiscriminator(m, errors);
  validateStatelessProofPlacement(m, errors);
  validateErrorCodeAllowlistOrder(m, errors);
  validateAggregate(m, errors);
  return { metadata: m, errors };
}

// ---------------------------------------------------------------------------

function pushIdentityByteCap(path: string, errors: MetadataError[]): void {
  // Identity paths are always schema-known (/selectedProviderId, etc.).
  errors.push({ code: 'invalid-metadata-identity-syntax', path });
}

function validateIdentitySyntax(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  const idFields: Array<[keyof ProviderRunMetadataV1, string]> = [
    ['selectedProviderId', '/selectedProviderId'],
    ['observedProviderId', '/observedProviderId'],
    ['resolvedModelId', '/resolvedModelId'],
  ];
  for (const [key, path] of idFields) {
    const v = m[key];
    if (typeof v !== 'string') {
      pushIdentityByteCap(path, errors);
      continue;
    }
    if (IDENTITY_CONTROL_CHARS_REGEX.test(v)) {
      pushIdentityByteCap(path, errors);
      continue;
    }
    if (utf8ByteLength(v) > IDENTITY_STRING_MAX_UTF8_BYTES) {
      pushIdentityByteCap(path, errors);
    }
  }
}

function validateProviderIdentityCross(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  if (
    typeof m.selectedProviderId === 'string' &&
    typeof m.observedProviderId === 'string' &&
    m.selectedProviderId !== m.observedProviderId
  ) {
    errors.push({
      code: 'invalid-metadata-provider-identity-cross-mismatch',
      path: '/observedProviderId',
    });
  }
}

function validateModelAliasLiteral(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  if (m.resolvedModelId === 'latest') {
    errors.push({ code: 'invalid-metadata-model-alias-literal', path: '/resolvedModelId' });
  }
}

function validateAttemptOrdering(
  attempts: readonly AttemptObservation[],
  errors: MetadataError[],
): void {
  const seen = new Set<string>();
  let prevReq = -1;
  let prevAtt = -1;
  const perRequestNext = new Map<number, number>();
  for (let i = 0; i < attempts.length; i += 1) {
    const a = attempts[i]!;
    const path = `/normalizedUsage/attempts/${i}`;
    const key = `${a.requestOrdinal}:${a.attemptOrdinal}`;
    if (seen.has(key)) {
      errors.push({ code: 'invalid-metadata-attempt-uniqueness', path });
    } else {
      seen.add(key);
    }
    if (i > 0) {
      const strictlyGreater =
        a.requestOrdinal > prevReq || (a.requestOrdinal === prevReq && a.attemptOrdinal > prevAtt);
      if (!strictlyGreater) {
        errors.push({ code: 'invalid-metadata-attempt-ordering', path });
      }
    }
    prevReq = a.requestOrdinal;
    prevAtt = a.attemptOrdinal;
    const expected = perRequestNext.get(a.requestOrdinal) ?? 0;
    if (a.attemptOrdinal !== expected) {
      errors.push({ code: 'invalid-metadata-attempt-contiguity', path });
    }
    perRequestNext.set(a.requestOrdinal, a.attemptOrdinal + 1);
  }
  const requestOrdinals = Array.from(perRequestNext.keys()).sort((x, y) => x - y);
  for (let i = 0; i < requestOrdinals.length; i += 1) {
    if (requestOrdinals[i] !== i) {
      // Report the first attempt whose requestOrdinal starts a gap.
      let offendingIdx = -1;
      for (let j = 0; j < attempts.length; j += 1) {
        if (attempts[j]!.requestOrdinal === requestOrdinals[i]) {
          offendingIdx = j;
          break;
        }
      }
      const p =
        offendingIdx >= 0
          ? finalizePath(['normalizedUsage', 'attempts', String(offendingIdx)])
          : finalizePath(['normalizedUsage', 'attempts']);
      errors.push({ code: 'invalid-metadata-request-ordering', path: p });
      break;
    }
  }
  // Multiple succeeded per request.
  const succeededSeen = new Map<number, number>(); // requestOrdinal -> count
  for (let i = 0; i < attempts.length; i += 1) {
    const a = attempts[i]!;
    if (a.outcome !== 'succeeded') continue;
    const prev = succeededSeen.get(a.requestOrdinal) ?? 0;
    succeededSeen.set(a.requestOrdinal, prev + 1);
    if (prev >= 1) {
      // This attempt is the (n+1)th successful attempt in its request.
      errors.push({
        code: 'invalid-metadata-multiple-succeeded-attempts',
        path: finalizePath(['normalizedUsage', 'attempts', String(i)]),
      });
    }
  }
}

function validateAttemptUsageCrossField(
  attempts: readonly AttemptObservation[],
  errors: MetadataError[],
): void {
  for (let i = 0; i < attempts.length; i += 1) {
    const a = attempts[i]!;
    const path = `/normalizedUsage/attempts/${i}`;
    const inputSide = [a.uncachedInputTokens, a.cacheWriteInputTokens, a.cacheReadInputTokens];
    const all5 = [...inputSide, a.outputTokens, a.totalInputTokens];
    if (a.usageCompleteness === 'complete') {
      if (all5.some((v) => v === null)) {
        errors.push({ code: 'invalid-metadata-attempt-usage-inconsistent', path });
        continue;
      }
      const sum =
        (a.uncachedInputTokens as number) +
        (a.cacheWriteInputTokens as number) +
        (a.cacheReadInputTokens as number);
      if (sum !== a.totalInputTokens) {
        errors.push({ code: 'invalid-metadata-attempt-usage-inconsistent', path });
      }
    } else if (a.usageCompleteness === 'missing') {
      if (all5.some((v) => v !== null)) {
        errors.push({ code: 'invalid-metadata-attempt-usage-inconsistent', path });
      }
    } else {
      // partial: at least one of the five non-null AND at least one null.
      const anyNonNull = all5.some((v) => v !== null);
      const anyNull = all5.some((v) => v === null);
      if (!anyNonNull || !anyNull) {
        errors.push({ code: 'invalid-metadata-attempt-usage-inconsistent', path });
      }
    }
  }
}

function validateOutcomeErrorConsistency(
  attempts: readonly AttemptObservation[],
  errors: MetadataError[],
): void {
  const providerFailureCodes: ErrorCode[] = [
    'provider_timeout',
    'provider_4xx',
    'provider_5xx',
    'provider_rate_limited',
    'provider_cancelled',
  ];
  const failedRequiredCodes: ErrorCode[] = [
    'provider_timeout',
    'provider_4xx',
    'provider_5xx',
    'provider_rate_limited',
    'capability_unsupported',
  ];
  for (let i = 0; i < attempts.length; i += 1) {
    const a = attempts[i]!;
    const path = `/normalizedUsage/attempts/${i}/attemptErrorCodes`;
    const codes = new Set(a.attemptErrorCodes);
    if (a.outcome === 'succeeded') {
      if (providerFailureCodes.some((c) => codes.has(c))) {
        errors.push({ code: 'invalid-metadata-attempt-outcome-error-consistency', path });
      }
      if (a.capability !== 'eligible' || a.cacheStatus === 'unsupported') {
        errors.push({
          code: 'invalid-metadata-attempt-outcome-error-consistency',
          path: `/normalizedUsage/attempts/${i}`,
        });
      }
    } else if (a.outcome === 'cancelled') {
      if (!codes.has('provider_cancelled')) {
        errors.push({ code: 'invalid-metadata-attempt-outcome-error-consistency', path });
      }
    } else {
      if (!failedRequiredCodes.some((c) => codes.has(c))) {
        errors.push({ code: 'invalid-metadata-attempt-outcome-error-consistency', path });
      }
    }
    if (a.capability === 'unsupported') {
      if (!codes.has('capability_unsupported')) {
        errors.push({ code: 'invalid-metadata-attempt-outcome-error-consistency', path });
      }
      if (a.outcome === 'succeeded') {
        errors.push({
          code: 'invalid-metadata-attempt-outcome-error-consistency',
          path: `/normalizedUsage/attempts/${i}`,
        });
      }
    }
  }
}

function validateStatelessDiscriminator(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  const { mode, statelessProof } = m.capability;
  if (mode === 'standard' && statelessProof !== null) {
    errors.push({ code: 'invalid-metadata-stateless-proof', path: '/capability/statelessProof' });
  }
  if (mode === 'stateless' && statelessProof === null) {
    errors.push({ code: 'invalid-metadata-stateless-proof', path: '/capability/statelessProof' });
  }
}

function validateStatelessProofPlacement(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  const attempts = m.normalizedUsage.attempts;
  const flagged: number[] = [];
  for (let i = 0; i < attempts.length; i += 1) {
    if (attempts[i]!.attemptErrorCodes.includes('stateless_proof_missing')) {
      flagged.push(i);
    }
  }
  if (m.capability.mode === 'standard' && flagged.length > 0) {
    for (const i of flagged) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    }
    return;
  }
  if (
    m.capability.mode === 'stateless' &&
    m.capability.statelessProof !== null &&
    m.capability.statelessProof.verified === true &&
    flagged.length > 0
  ) {
    for (const i of flagged) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: `/normalizedUsage/attempts/${i}/attemptErrorCodes`,
      });
    }
    return;
  }
  if (
    m.capability.mode === 'stateless' &&
    m.capability.statelessProof !== null &&
    m.capability.statelessProof.verified === false
  ) {
    if (attempts.length === 0) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: '/normalizedUsage/attempts',
      });
      return;
    }
    if (flagged.length === 0) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: '/normalizedUsage/attempts',
      });
      return;
    }
    if (flagged.length > 1) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: '/normalizedUsage/attempts',
      });
      return;
    }
    const idx = flagged[0]!;
    const a = attempts[idx]!;
    if (a.requestOrdinal !== 0 || a.attemptOrdinal !== 0) {
      errors.push({
        code: 'invalid-metadata-stateless-proof',
        path: `/normalizedUsage/attempts/${idx}/attemptErrorCodes`,
      });
    }
  }
}

function validateErrorCodeAllowlistOrder(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  const allowedIndex = new Map(ALLOWED_ERROR_CODES.map((c, i) => [c, i]));
  const check = (codes: ErrorCode[], base: readonly string[]) => {
    for (let i = 1; i < codes.length; i += 1) {
      const prev = allowedIndex.get(codes[i - 1]!) ?? -1;
      const cur = allowedIndex.get(codes[i]!) ?? -1;
      if (cur <= prev) {
        errors.push({
          code: 'invalid-metadata-error-code-order',
          path: finalizePath([...base, String(i)]),
        });
        return;
      }
    }
  };
  check(m.errorCodes, ['errorCodes']);
  const attempts = m.normalizedUsage.attempts;
  for (let i = 0; i < attempts.length; i += 1) {
    check(attempts[i]!.attemptErrorCodes, [
      'normalizedUsage',
      'attempts',
      String(i),
      'attemptErrorCodes',
    ]);
  }
}

function validateAggregate(m: ProviderRunMetadataV1, errors: MetadataError[]): void {
  const derived = deriveAggregateInternal({
    attempts: m.normalizedUsage.attempts,
    capabilityMode: m.capability.mode,
    statelessProof: m.capability.statelessProof,
  });
  if (!derived.valid) {
    for (const e of derived.errors) errors.push(e);
    return;
  }
  const expected = derived.aggregate;
  // Walk the derived aggregate output and emit one mismatch per leaf that
  // disagrees. Leaves are the smallest comparable units (numbers, strings,
  // enums). Objects and arrays are descended into so the reported path targets
  // the exact offending field instead of the enclosing block.
  compareLeaves(
    m.normalizedUsage.requests,
    expected.normalizedUsage.requests,
    ['normalizedUsage', 'requests'],
    errors,
  );
  compareLeaves(
    m.normalizedUsage.aggregate,
    expected.normalizedUsage.aggregate,
    ['normalizedUsage', 'aggregate'],
    errors,
  );
  compareLeaves(
    m.capability.aggregate,
    expected.capability.aggregate,
    ['capability', 'aggregate'],
    errors,
  );
  compareLeaves(m.cacheStatus, expected.cacheStatus, ['cacheStatus'], errors);
  compareLeaves(m.retryObservations, expected.retryObservations, ['retryObservations'], errors);
  compareLeaves(m.errorCodes, expected.errorCodes, ['errorCodes'], errors);
  compareLeaves(
    m.telemetryCompleteness,
    expected.telemetryCompleteness,
    ['telemetryCompleteness'],
    errors,
  );
}

function compareLeaves(
  stored: unknown,
  expected: unknown,
  path: readonly string[],
  errors: MetadataError[],
): void {
  if (
    stored === null ||
    expected === null ||
    typeof stored !== 'object' ||
    typeof expected !== 'object'
  ) {
    if (!deepEqual(stored, expected)) {
      errors.push({
        code: 'invalid-metadata-aggregate-mismatch' as MetadataErrorCode,
        path: finalizePath(path),
      });
    }
    return;
  }
  if (Array.isArray(stored) && Array.isArray(expected)) {
    const maxLen = Math.max(stored.length, expected.length);
    if (stored.length !== expected.length) {
      errors.push({
        code: 'invalid-metadata-aggregate-mismatch' as MetadataErrorCode,
        path: finalizePath(path),
      });
      return;
    }
    for (let i = 0; i < maxLen; i += 1) {
      compareLeaves(stored[i], expected[i], [...path, String(i)], errors);
    }
    return;
  }
  const so = stored as Record<string, unknown>;
  const eo = expected as Record<string, unknown>;
  const keys = Object.keys(eo);
  for (const k of keys) {
    compareLeaves(so[k], eo[k], [...path, k], errors);
  }
}

function deepEqual(a: unknown, b: unknown): boolean {
  if (a === b) return true;
  if (typeof a !== typeof b) return false;
  if (a === null || b === null) return a === b;
  if (Array.isArray(a) && Array.isArray(b)) {
    if (a.length !== b.length) return false;
    for (let i = 0; i < a.length; i += 1) {
      if (!deepEqual(a[i], b[i])) return false;
    }
    return true;
  }
  if (typeof a === 'object' && typeof b === 'object') {
    const ao = a as Record<string, unknown>;
    const bo = b as Record<string, unknown>;
    const ak = Object.keys(ao).sort();
    const bk = Object.keys(bo).sort();
    if (ak.length !== bk.length) return false;
    for (let i = 0; i < ak.length; i += 1) if (ak[i] !== bk[i]) return false;
    for (const key of ak) {
      if (!deepEqual(ao[key], bo[key])) return false;
    }
    return true;
  }
  return false;
}
