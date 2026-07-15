import { deriveAggregateInternal } from '../src/provider-metadata/aggregate.js';
import type {
  AttemptObservation,
  CapabilityMode,
  ProviderRunMetadataV1,
  StatelessProof,
} from '../src/provider-metadata/types.js';
import { computeMetadataSemanticSha256 } from '../src/provider-metadata/semantic-hash.js';
import { parseProviderRunMetadata } from '../src/provider-metadata/parse.js';
import { writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const outDir = join(here, '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');
if (!existsSync(outDir)) mkdirSync(outDir, { recursive: true });

interface BaseInput {
  attempts: AttemptObservation[];
  capabilityMode?: CapabilityMode;
  statelessProof?: StatelessProof | null;
  selectedProviderId?: string;
  observedProviderId?: string;
  resolvedModelId?: string;
  adapterId?: string;
  logicalPrefixSha256?: string;
  prefixSha256?: string;
  producingRunId?: string;
  runAttempt?: number;
  interactionId?: string;
  consumedInputSha256?: string;
  resultSha256?: string;
  traceSha256?: string;
  predecessorLedgerSha256?: string;
  candidateLedgerSha256?: string;
}

function h(byte: number): string {
  return byte.toString(16).padStart(2, '0').repeat(32);
}

function build(input: BaseInput): ProviderRunMetadataV1 {
  const capabilityMode = input.capabilityMode ?? 'standard';
  const statelessProof = input.statelessProof ?? null;
  const result = deriveAggregateInternal({
    attempts: input.attempts,
    capabilityMode,
    statelessProof,
  });
  if (!result.valid) {
    throw new Error(
      `deriveAggregate failed while building fixture: ${JSON.stringify(result.errors)}`,
    );
  }
  const derived = result.aggregate;
  return {
    schemaVersion: 1,
    selectedProviderId: input.selectedProviderId ?? 'anthropic',
    observedProviderId: input.observedProviderId ?? 'anthropic',
    resolvedModelId: input.resolvedModelId ?? 'claude-sonnet-4-5-2025-09-29',
    adapterId: input.adapterId ?? h(0x11),
    logicalPrefixSha256: input.logicalPrefixSha256 ?? h(0x22),
    prefixSha256: input.prefixSha256 ?? h(0x33),
    capability: {
      mode: capabilityMode,
      aggregate: derived.capability.aggregate,
      statelessProof,
    },
    cacheStatus: derived.cacheStatus,
    normalizedUsage: derived.normalizedUsage,
    retryObservations: derived.retryObservations,
    errorCodes: derived.errorCodes,
    telemetryCompleteness: derived.telemetryCompleteness,
    producingRunId: input.producingRunId ?? '17123456789',
    runAttempt: input.runAttempt ?? 1,
    interactionId: input.interactionId ?? h(0x44),
    consumedInputSha256: input.consumedInputSha256 ?? h(0x55),
    resultSha256: input.resultSha256 ?? h(0x66),
    traceSha256: input.traceSha256 ?? h(0x77),
    predecessorLedgerSha256: input.predecessorLedgerSha256 ?? 'bootstrap',
    candidateLedgerSha256: input.candidateLedgerSha256 ?? h(0x88),
  };
}

function att(overrides: Partial<AttemptObservation> = {}): AttemptObservation {
  const base: AttemptObservation = {
    requestOrdinal: 0,
    attemptOrdinal: 0,
    outcome: 'succeeded',
    capability: 'eligible',
    cacheStatus: 'hit',
    usageCompleteness: 'complete',
    totalInputTokens: 100,
    uncachedInputTokens: 0,
    cacheWriteInputTokens: 0,
    cacheReadInputTokens: 100,
    outputTokens: 10,
    attemptErrorCodes: [],
  };
  return { ...base, ...overrides };
}

function write(name: string, value: unknown): void {
  const p = join(outDir, name);
  writeFileSync(p, JSON.stringify(value, null, 2) + '\n', 'utf8');
}

// Positive fixtures
const bootstrapHit = build({
  attempts: [att({ cacheStatus: 'hit', totalInputTokens: 100, cacheReadInputTokens: 100 })],
});
write('valid-bootstrap-hit.json', bootstrapHit);

const resumedCacheWriteThenHit = build({
  attempts: [
    att({
      requestOrdinal: 0,
      attemptOrdinal: 0,
      cacheStatus: 'miss',
      outcome: 'succeeded',
      totalInputTokens: 200,
      uncachedInputTokens: 100,
      cacheWriteInputTokens: 100,
      cacheReadInputTokens: 0,
      outputTokens: 20,
    }),
    att({
      requestOrdinal: 1,
      attemptOrdinal: 0,
      cacheStatus: 'hit',
      outcome: 'succeeded',
      totalInputTokens: 200,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 200,
      outputTokens: 25,
    }),
  ],
  predecessorLedgerSha256: h(0x99),
});
write('valid-resumed-cachewrite-then-hit.json', resumedCacheWriteThenHit);

const missOnly = build({
  attempts: [
    att({
      cacheStatus: 'miss',
      totalInputTokens: 100,
      uncachedInputTokens: 100,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
    }),
  ],
});
write('valid-miss-only.json', missOnly);

const partialCache = build({
  attempts: [
    att({
      requestOrdinal: 0,
      attemptOrdinal: 0,
      cacheStatus: 'hit',
      totalInputTokens: 100,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 100,
    }),
    att({
      requestOrdinal: 1,
      attemptOrdinal: 0,
      cacheStatus: 'miss',
      totalInputTokens: 100,
      uncachedInputTokens: 100,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
    }),
  ],
});
write('valid-partial-cache.json', partialCache);

const unsupportedRun = build({
  attempts: [
    att({
      capability: 'unsupported',
      cacheStatus: 'unsupported',
      outcome: 'failed',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      attemptErrorCodes: ['capability_unsupported'],
    }),
  ],
});
write('valid-unsupported.json', unsupportedRun);

const ineligibleRun = build({
  attempts: [
    att({
      capability: 'ineligible',
      cacheStatus: 'miss',
      outcome: 'failed',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      attemptErrorCodes: ['provider_4xx'],
    }),
  ],
});
write('valid-ineligible.json', ineligibleRun);

const unknownRun = build({
  attempts: [
    att({
      capability: 'unknown',
      cacheStatus: 'unknown',
      outcome: 'failed',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      attemptErrorCodes: ['provider_5xx'],
    }),
  ],
});
write('valid-unknown.json', unknownRun);

const telemetryUnavailable = build({
  attempts: [
    att({
      capability: 'telemetryUnavailable',
      cacheStatus: 'miss',
      outcome: 'failed',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      attemptErrorCodes: ['provider_timeout'],
    }),
  ],
});
write('valid-telemetry-unavailable.json', telemetryUnavailable);

const zeroRequest = build({ attempts: [] });
write('valid-zero-request.json', zeroRequest);

const statelessProviderAdvertised = build({
  attempts: [att()],
  capabilityMode: 'stateless',
  statelessProof: { kind: 'providerAdvertised', verified: true },
});
write('valid-stateless-provider-advertised.json', statelessProviderAdvertised);

const statelessSyntheticUnverified = build({
  attempts: [
    att({ requestOrdinal: 0, attemptOrdinal: 0, attemptErrorCodes: ['stateless_proof_missing'] }),
  ],
  capabilityMode: 'stateless',
  statelessProof: { kind: 'synthetic', verified: false },
});
write('valid-stateless-synthetic-unverified.json', statelessSyntheticUnverified);

const retriesSumByAttempt = build({
  attempts: [
    att({
      requestOrdinal: 0,
      attemptOrdinal: 0,
      outcome: 'failed',
      cacheStatus: 'miss',
      capability: 'eligible',
      usageCompleteness: 'complete',
      totalInputTokens: 40,
      uncachedInputTokens: 40,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 0,
      outputTokens: 5,
      attemptErrorCodes: ['provider_5xx'],
    }),
    att({
      requestOrdinal: 0,
      attemptOrdinal: 1,
      outcome: 'succeeded',
      cacheStatus: 'hit',
      totalInputTokens: 60,
      uncachedInputTokens: 0,
      cacheWriteInputTokens: 0,
      cacheReadInputTokens: 60,
      outputTokens: 10,
    }),
  ],
});
write('valid-retries-summed-by-attempt.json', retriesSumByAttempt);

const partialUsage = build({
  attempts: [
    att({
      usageCompleteness: 'partial',
      totalInputTokens: null,
      uncachedInputTokens: 40,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: 10,
    }),
  ],
});
write('valid-partial-usage.json', partialUsage);

const goldenSubjects: Array<[string, ProviderRunMetadataV1]> = [
  ['golden-hash-bootstrap-hit', bootstrapHit],
  ['golden-hash-resumed-partial', partialCache],
  ['golden-hash-stateless-synthetic-unverified', statelessSyntheticUnverified],
];
for (const [name, m] of goldenSubjects) {
  // Golden vectors must be hashed only after full schema + semantic validation.
  // Otherwise a tightened rule could silently permit an invalid shape to seed the
  // byte oracle that #48/#52/#55 rely on.
  const bytes = new TextEncoder().encode(JSON.stringify(m));
  const result = parseProviderRunMetadata(bytes);
  if (!result.valid) {
    throw new Error(`golden ${name} failed validation: ${JSON.stringify(result.errors)}`);
  }
  const hex = computeMetadataSemanticSha256(result.metadata);
  write(name + '.json', { metadata: result.metadata, metadataSemanticSha256: hex });
}

// Negative fixtures.
{
  const m = build({ attempts: [att()] }) as ProviderRunMetadataV1 & { endpoint?: string };
  m.endpoint = 'https://api.example.com';
  write('invalid-forbidden-field-endpoint.json', m);
}
{
  const m = build({ attempts: [att()] }) as ProviderRunMetadataV1 & { providerRequestId?: string };
  m.providerRequestId = 'req-123';
  write('invalid-forbidden-field-provider-request-id.json', m);
}
{
  const m = build({ attempts: [att()] }) as ProviderRunMetadataV1 & { rawError?: string };
  m.rawError = 'boom';
  write('invalid-forbidden-field-raw-error.json', m);
}
{
  const m = build({ attempts: [att()] }) as ProviderRunMetadataV1 & { notes?: string };
  m.notes = 'commentary';
  write('invalid-forbidden-field-notes.json', m);
}
{
  const m = build({ attempts: [att()] }) as ProviderRunMetadataV1 & {
    providerExtensions?: unknown;
  };
  m.providerExtensions = {};
  write('invalid-forbidden-field-provider-extensions.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { cacheStatus: string };
  cast.cacheStatus = 'partiallyish';
  write('invalid-unknown-enum-cache-status.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { resolvedModelId: string };
  cast.resolvedModelId = 'latest';
  write('invalid-model-alias-latest.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { observedProviderId: string };
  cast.observedProviderId = 'openai';
  write('invalid-provider-identity-cross-mismatch.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { selectedProviderId: string; observedProviderId: string };
  cast.selectedProviderId = 'anthr\u00f6pic';
  cast.observedProviderId = 'anthr\u00f6pic';
  write('invalid-identity-syntax-unicode.json', m);
}

{
  const m = build({ attempts: [att()], predecessorLedgerSha256: 'boots' });
  write('invalid-predecessor-syntax.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as {
    normalizedUsage: { attempts: Array<{ totalInputTokens: number }> };
  };
  cast.normalizedUsage.attempts[0].totalInputTokens = -1;
  write('invalid-token-negative.json', m);
}

{
  // Aggregate mismatch: stored attemptCount disagrees with the derived count of
  // per-attempt entries. Individual counts still satisfy the schema bounds.
  const m = build({ attempts: [att()] });
  const cast = m as unknown as {
    retryObservations: {
      requests: Array<{
        attemptCount: number;
        succeededCount: number;
        failedCount: number;
        cancelledCount: number;
      }>;
      aggregate: { attemptCount: number };
    };
  };
  cast.retryObservations.requests[0].attemptCount = 2;
  cast.retryObservations.requests[0].failedCount = 1;
  cast.retryObservations.aggregate.attemptCount = 2;
  write('invalid-retry-count-mismatch.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { cacheStatus: string };
  cast.cacheStatus = 'miss';
  write('invalid-aggregate-mismatch-cache-status.json', m);
}

{
  const m = build({
    attempts: [
      att({
        requestOrdinal: 0,
        attemptOrdinal: 0,
        outcome: 'failed',
        usageCompleteness: 'missing',
        totalInputTokens: null,
        uncachedInputTokens: null,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: null,
        outputTokens: null,
        capability: 'unknown',
        cacheStatus: 'unknown',
        attemptErrorCodes: ['provider_5xx'],
      }),
      att({
        requestOrdinal: 1,
        attemptOrdinal: 0,
        outcome: 'failed',
        usageCompleteness: 'missing',
        totalInputTokens: null,
        uncachedInputTokens: null,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: null,
        outputTokens: null,
        capability: 'unknown',
        cacheStatus: 'unknown',
        attemptErrorCodes: ['provider_4xx'],
      }),
    ],
  });
  const cast = m as unknown as { errorCodes: string[] };
  cast.errorCodes = ['provider_5xx', 'provider_4xx'];
  write('invalid-error-codes-unsorted.json', m);
}

{
  const m = build({ attempts: [att({ requestOrdinal: 0, attemptOrdinal: 0 })] });
  const cast = m as unknown as {
    normalizedUsage: { attempts: AttemptObservation[] };
  };
  cast.normalizedUsage.attempts.push(
    att({
      requestOrdinal: 0,
      attemptOrdinal: 0,
      outcome: 'failed',
      cacheStatus: 'miss',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      attemptErrorCodes: ['provider_5xx'],
    }),
  );
  write('invalid-attempt-duplicate.json', m);
}

{
  const m = build({ attempts: [att({ requestOrdinal: 0, attemptOrdinal: 0 })] });
  const cast = m as unknown as {
    normalizedUsage: { attempts: AttemptObservation[] };
  };
  cast.normalizedUsage.attempts.push(
    att({
      requestOrdinal: 0,
      attemptOrdinal: 2,
      outcome: 'failed',
      usageCompleteness: 'missing',
      totalInputTokens: null,
      uncachedInputTokens: null,
      cacheWriteInputTokens: null,
      cacheReadInputTokens: null,
      outputTokens: null,
      cacheStatus: 'miss',
      attemptErrorCodes: ['provider_5xx'],
    }),
  );
  write('invalid-attempt-non-contiguous.json', m);
}

{
  const m = build({
    attempts: [
      att({ requestOrdinal: 0, attemptOrdinal: 0 }),
      att({
        requestOrdinal: 2,
        attemptOrdinal: 0,
        cacheStatus: 'miss',
        totalInputTokens: 100,
        uncachedInputTokens: 100,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
      }),
    ],
  });
  write('invalid-request-non-contiguous.json', m);
}

{
  const m = build({ attempts: [att({ requestOrdinal: 0, attemptOrdinal: 0 })] });
  const cast = m as unknown as { normalizedUsage: { attempts: AttemptObservation[] } };
  cast.normalizedUsage.attempts.push(att({ requestOrdinal: 0, attemptOrdinal: 1 }));
  write('invalid-multiple-succeeded-attempts.json', m);
}

{
  const attempt = att();
  attempt.totalInputTokens = 100;
  attempt.uncachedInputTokens = 50;
  attempt.cacheWriteInputTokens = 20;
  attempt.cacheReadInputTokens = 20;
  const m = build({ attempts: [attempt] });
  write('invalid-partition-inconsistent.json', m);
}

{
  const attempt = att();
  attempt.usageCompleteness = 'complete';
  attempt.outputTokens = null;
  const m = build({ attempts: [attempt] });
  write('invalid-attempt-usage-complete-with-null.json', m);
}

{
  const attempt = att();
  attempt.attemptErrorCodes = ['provider_timeout'];
  const m = build({ attempts: [attempt] });
  write('invalid-outcome-succeeded-with-provider-error.json', m);
}

{
  const m = build({
    attempts: [att({ attemptErrorCodes: ['stateless_proof_missing'] })],
    capabilityMode: 'stateless',
    statelessProof: null,
  });
  const cast = m as unknown as {
    capability: { mode: string; statelessProof: unknown };
  };
  cast.capability.mode = 'stateless';
  cast.capability.statelessProof = null;
  write('invalid-stateless-mode-null-proof.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { capability: { statelessProof: unknown } };
  cast.capability.statelessProof = { kind: 'synthetic', verified: false };
  write('invalid-standard-mode-with-proof.json', m);
}

{
  const m = build({
    attempts: [att()],
    capabilityMode: 'stateless',
    statelessProof: { kind: 'synthetic', verified: true },
  });
  const cast = m as unknown as {
    capability: { statelessProof: { kind: string; verified: boolean } };
  };
  cast.capability.statelessProof.verified = false;
  write('invalid-stateless-unverified-missing-code.json', m);
}

{
  const m = build({
    attempts: [
      att({ requestOrdinal: 0, attemptOrdinal: 0 }),
      att({
        requestOrdinal: 1,
        attemptOrdinal: 0,
        cacheStatus: 'miss',
        totalInputTokens: 100,
        uncachedInputTokens: 100,
        cacheWriteInputTokens: 0,
        cacheReadInputTokens: 0,
        attemptErrorCodes: ['stateless_proof_missing'],
      }),
    ],
    capabilityMode: 'stateless',
    statelessProof: { kind: 'synthetic', verified: false },
  });
  write('invalid-stateless-code-wrong-placement.json', m);
}

{
  const m = build({ attempts: [att()], producingRunId: '0' });
  write('invalid-producing-run-id-zero.json', m);
}
{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { producingRunId: string };
  cast.producingRunId = 'abc';
  write('invalid-producing-run-id-non-numeric.json', m);
}

{
  const m = build({ attempts: [att()] });
  const cast = m as unknown as { errorCodes: string[] };
  cast.errorCodes = ['identity_mismatch'];
  write('invalid-error-codes-contains-validator-code.json', m);
}

console.log('fixtures written to', outDir);
