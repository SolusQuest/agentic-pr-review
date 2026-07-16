import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { parseProviderRunMetadata } from './index.js';
import type { MetadataError, ProviderRunMetadataV1 } from './types.js';

const fixturePath = resolve(
  'protocol/fixtures/provider-run-metadata/v1/valid-standard-resumed.json',
);

function base(): ProviderRunMetadataV1 {
  return JSON.parse(readFileSync(fixturePath, 'utf8')) as ProviderRunMetadataV1;
}

export type SemanticFixtureCase = {
  readonly name: string;
  readonly expected: string;
  readonly mutate: (value: any) => void;
};

export const SEMANTIC_FIXTURE_CASES: readonly SemanticFixtureCase[] = [
  {
    name: 'model-alias-literal',
    expected: 'invalid-metadata-model-alias-literal',
    mutate: (value) => (value.resolvedModelId = 'latest'),
  },
  {
    name: 'provider-identity-cross-mismatch',
    expected: 'invalid-metadata-provider-identity-cross-mismatch',
    mutate: (value) => (value.observedProviderId = 'other'),
  },
  {
    name: 'identity-syntax',
    expected: 'invalid-metadata-identity-syntax',
    mutate: (value) => {
      const longUtf8Identity = 'é'.repeat(256);
      value.selectedProviderId = longUtf8Identity;
      value.observedProviderId = longUtf8Identity;
    },
  },
  {
    name: 'attempt-uniqueness',
    expected: 'invalid-metadata-attempt-uniqueness',
    mutate: (value) => {
      value.normalizedUsage.attempts.push({ ...value.normalizedUsage.attempts[0] });
    },
  },
  {
    name: 'attempt-ordering',
    expected: 'invalid-metadata-attempt-ordering',
    mutate: (value) => {
      value.normalizedUsage.attempts.push({
        ...value.normalizedUsage.attempts[0],
        attemptOrdinal: 0,
        outcome: 'failed',
        attemptErrorCodes: ['provider_timeout'],
      });
      value.normalizedUsage.requests[0].outputTokens = 2;
      value.normalizedUsage.aggregate.outputTokens = 2;
      value.retryObservations.requests[0].attemptCount = 2;
      value.retryObservations.requests[0].failedCount = 1;
      value.retryObservations.aggregate.attemptCount = 2;
      value.retryObservations.aggregate.failedCount = 1;
    },
  },
  {
    name: 'attempt-contiguity',
    expected: 'invalid-metadata-attempt-contiguity',
    mutate: (value) => {
      value.normalizedUsage.attempts.push({
        ...value.normalizedUsage.attempts[0],
        attemptOrdinal: 2,
        outcome: 'failed',
        attemptErrorCodes: ['provider_timeout'],
      });
    },
  },
  {
    name: 'request-ordering',
    expected: 'invalid-metadata-request-ordering',
    mutate: (value) => (value.normalizedUsage.requests[0].requestOrdinal = 1),
  },
  {
    name: 'multiple-succeeded-attempts',
    expected: 'invalid-metadata-multiple-succeeded-attempts',
    mutate: (value) => {
      value.normalizedUsage.attempts.push({
        ...value.normalizedUsage.attempts[0],
        attemptOrdinal: 1,
      });
    },
  },
  {
    name: 'attempt-usage-inconsistent',
    expected: 'invalid-metadata-attempt-usage-inconsistent',
    mutate: (value) => (value.normalizedUsage.attempts[0].usageCompleteness = 'partial'),
  },
  {
    name: 'attempt-outcome-error-consistency',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => (value.normalizedUsage.attempts[0].outcome = 'cancelled'),
  },
  {
    name: 'failed-only-provider-cancelled',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].outcome = 'failed';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['provider_cancelled'];
      value.errorCodes = ['provider_cancelled'];
    },
  },
  {
    name: 'failed-provider-failure-plus-cancelled',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].outcome = 'failed';
      value.normalizedUsage.attempts[0].attemptErrorCodes = [
        'provider_timeout',
        'provider_cancelled',
      ];
      value.errorCodes = ['provider_timeout', 'provider_cancelled'];
    },
  },
  {
    name: 'cancelled-with-provider-failure',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].outcome = 'cancelled';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['provider_5xx', 'provider_cancelled'];
      value.errorCodes = ['provider_5xx', 'provider_cancelled'];
    },
  },
  {
    name: 'verified-proof-with-missing-marker',
    expected: 'invalid-metadata-stateless-proof',
    mutate: (value) => {
      value.capability.mode = 'stateless';
      value.capability.statelessProof = { kind: 'synthetic', verified: true };
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['stateless_proof_missing'];
      value.errorCodes = ['stateless_proof_missing'];
    },
  },
  {
    name: 'eligible-with-capability-unsupported',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].capability = 'eligible';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['capability_unsupported'];
      value.errorCodes = ['capability_unsupported'];
    },
  },
  {
    name: 'unknown-with-capability-unsupported',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].capability = 'unknown';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['capability_unsupported'];
      value.errorCodes = ['capability_unsupported'];
    },
  },
  {
    name: 'ineligible-with-capability-unsupported',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].capability = 'ineligible';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['capability_unsupported'];
      value.errorCodes = ['capability_unsupported'];
    },
  },
  {
    name: 'telemetry-unavailable-with-capability-unsupported',
    expected: 'invalid-metadata-attempt-outcome-error-consistency',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].capability = 'telemetryUnavailable';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['capability_unsupported'];
      value.errorCodes = ['capability_unsupported'];
    },
  },
  {
    name: 'proof-marker-on-nonfirst-attempt',
    expected: 'invalid-metadata-stateless-proof',
    mutate: (value) => {
      value.capability.mode = 'stateless';
      value.capability.statelessProof = { kind: 'synthetic', verified: false };
      value.normalizedUsage.attempts.push({
        ...value.normalizedUsage.attempts[0],
        attemptOrdinal: 1,
        outcome: 'failed',
        attemptErrorCodes: ['provider_timeout', 'stateless_proof_missing'],
      });
    },
  },
  {
    name: 'duplicate-proof-marker',
    expected: 'invalid-metadata-stateless-proof',
    mutate: (value) => {
      value.capability.mode = 'stateless';
      value.capability.statelessProof = { kind: 'synthetic', verified: false };
      value.normalizedUsage.attempts[0].attemptErrorCodes = [
        'stateless_proof_missing',
        'stateless_proof_missing',
      ];
    },
  },
  {
    name: 'stateless-proof',
    expected: 'invalid-metadata-stateless-proof',
    mutate: (value) => {
      value.capability.mode = 'stateless';
      value.capability.statelessProof = { kind: 'synthetic', verified: false };
    },
  },
  {
    name: 'error-code-order',
    expected: 'invalid-metadata-error-code-order',
    mutate: (value) => {
      value.normalizedUsage.attempts[0].outcome = 'failed';
      value.normalizedUsage.attempts[0].attemptErrorCodes = ['provider_5xx', 'provider_timeout'];
      value.errorCodes = ['provider_5xx', 'provider_timeout'];
    },
  },
  {
    name: 'aggregate-mismatch',
    expected: 'invalid-metadata-aggregate-mismatch',
    mutate: (value) => (value.normalizedUsage.aggregate.outputTokens = 3),
  },
];

export function runSemanticFixture(name: string): MetadataError[] {
  const fixture = SEMANTIC_FIXTURE_CASES.find((candidate) => candidate.name === name);
  if (!fixture) throw new Error(`unknown semantic fixture: ${name}`);
  const value = structuredClone(base());
  fixture.mutate(value);
  const result = parseProviderRunMetadata(canonicalJsonBytes(value));
  if (result.valid) throw new Error(`semantic fixture unexpectedly valid: ${name}`);
  return result.errors;
}
