import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseProviderRunMetadata } from './index.js';
import { METADATA_ERROR_CODES } from './types.js';
import { runSemanticFixture } from './semantic-fixtures.js';
import { runRawFixture } from './raw-fixtures.js';
import { POSITIVE_FIXTURE_CASES, runPositiveFixture } from './positive-fixtures.js';

type Fixture = {
  name: string;
  valid: boolean;
  expectedCodes: string[];
  expectedPaths?: string[];
  runner?: string;
};
type Manifest = {
  schemaVersion: number;
  expectedCodes: string[];
  semanticCases: string[];
  fixtures: Fixture[];
};

const root = resolve(process.cwd(), 'protocol/fixtures/provider-run-metadata/v1');
const manifest = JSON.parse(readFileSync(resolve(root, 'manifest.json'), 'utf8')) as Manifest;

describe('ProviderRunMetadataV1 published fixtures', () => {
  it('covers every manifest entry and agrees with parser results', () => {
    expect(manifest.schemaVersion).toBe(1);
    expect(new Set(manifest.expectedCodes)).toEqual(
      new Set(manifest.fixtures.flatMap((fixture) => fixture.expectedCodes)),
    );
    expect(new Set(manifest.expectedCodes)).toEqual(new Set(METADATA_ERROR_CODES));
    expect(manifest.semanticCases).toEqual([
      'model-alias-literal',
      'provider-identity-cross-mismatch',
      'identity-syntax',
      'attempt-uniqueness',
      'attempt-ordering',
      'attempt-contiguity',
      'request-ordering',
      'multiple-succeeded-attempts',
      'attempt-usage-inconsistent',
      'attempt-outcome-error-consistency',
      'failed-only-provider-cancelled',
      'failed-provider-failure-plus-cancelled',
      'cancelled-with-provider-failure',
      'stateless-proof',
      'error-code-order',
      'aggregate-mismatch',
      'verified-proof-with-missing-marker',
      'eligible-with-capability-unsupported',
      'unknown-with-capability-unsupported',
      'ineligible-with-capability-unsupported',
      'telemetry-unavailable-with-capability-unsupported',
      'proof-marker-on-nonfirst-attempt',
      'duplicate-proof-marker',
    ]);
    expect(manifest.fixtures.length).toBeGreaterThanOrEqual(3);
    for (const fixture of manifest.fixtures) {
      if (fixture.runner === 'semantic') {
        const descriptor = JSON.parse(readFileSync(resolve(root, fixture.name), 'utf8')) as {
          kind: string;
          case: string;
        };
        expect(descriptor.kind).toBe('semantic');
        const errors = runSemanticFixture(descriptor.case);
        expect(errors.map((error) => error.code)).toEqual(fixture.expectedCodes);
        if (fixture.expectedPaths)
          expect(errors.map((error) => error.path)).toEqual(fixture.expectedPaths);
        continue;
      }
      if (fixture.runner === 'raw') {
        const descriptor = JSON.parse(readFileSync(resolve(root, fixture.name), 'utf8')) as {
          kind: string;
          case: string;
        };
        expect(descriptor.kind).toBe('raw');
        const errors = runRawFixture(descriptor.case);
        expect(errors.map((error) => error.code)).toEqual(fixture.expectedCodes);
        if (fixture.expectedPaths)
          expect(errors.map((error) => error.path)).toEqual(fixture.expectedPaths);
        continue;
      }
      if (fixture.runner === 'hash') {
        const vectors = JSON.parse(readFileSync(resolve(root, fixture.name), 'utf8')) as unknown[];
        expect(vectors).toHaveLength(3);
        continue;
      }
      if (fixture.runner === 'positive') {
        const descriptor = JSON.parse(readFileSync(resolve(root, fixture.name), 'utf8')) as {
          kind: string;
          cases: string[];
        };
        expect(descriptor.kind).toBe('positive-matrix');
        expect(descriptor.cases).toEqual(POSITIVE_FIXTURE_CASES);
        for (const name of POSITIVE_FIXTURE_CASES) expect(runPositiveFixture(name)).toBe(true);
        continue;
      }
      if (fixture.runner === 'vitest') {
        expect(fixture.name.endsWith('.test.ts')).toBe(true);
        continue;
      }
      const bytes = new Uint8Array(readFileSync(resolve(root, fixture.name)));
      const result = parseProviderRunMetadata(bytes);
      expect(result.valid, fixture.name).toBe(fixture.valid);
      if (!fixture.valid && fixture.expectedCodes.length > 0) {
        expect(result.valid).toBe(false);
        if (!result.valid)
          expect(result.errors.map((error) => error.code)).toEqual(fixture.expectedCodes);
        if (!result.valid && fixture.expectedPaths)
          expect(result.errors.map((error) => error.path)).toEqual(fixture.expectedPaths);
      }
    }
  });
});
