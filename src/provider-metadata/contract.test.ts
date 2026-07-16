import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  MAX_METADATA_ERRORS,
  MAX_METADATA_PATH_CHARS,
  MAX_METADATA_PATH_UTF8_BYTES,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  buildSemanticEnvelope,
  computeMetadataSemanticSha256,
  parseProviderRunMetadata,
} from './index.js';
import type { ValidatedProviderRunMetadataV1 } from './types.js';

const hash = 'a'.repeat(64);

function standardEmpty(): Record<string, unknown> {
  return {
    schemaVersion: 1,
    selectedProviderId: 'synthetic',
    observedProviderId: 'synthetic',
    resolvedModelId: 'model-1',
    adapterId: hash,
    logicalPrefixSha256: hash,
    prefixSha256: hash,
    capability: { mode: 'standard', aggregate: 'unknown', statelessProof: null },
    cacheStatus: 'unknown',
    normalizedUsage: {
      attempts: [],
      requests: [],
      aggregate: {
        totalInputTokens: null,
        uncachedInputTokens: null,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: null,
        outputTokens: null,
        requestCount: 0,
        attemptCount: 0,
      },
    },
    retryObservations: {
      requests: [],
      aggregate: {
        requestCount: 0,
        attemptCount: 0,
        succeededCount: 0,
        failedCount: 0,
        cancelledCount: 0,
      },
    },
    errorCodes: [],
    telemetryCompleteness: {
      usage: 'missing',
      cache: 'missing',
      statelessProof: 'notApplicable',
      aggregate: 'missing',
    },
    producingRunId: '1',
    runAttempt: 1,
    interactionId: hash,
    consumedInputSha256: hash,
    resultSha256: hash,
    traceSha256: hash,
    predecessorLedgerSha256: 'bootstrap',
    candidateLedgerSha256: hash,
  };
}

function validated(): ValidatedProviderRunMetadataV1 {
  const result = parseProviderRunMetadata(canonicalJsonBytes(standardEmpty()));
  if (!result.valid) throw new Error(JSON.stringify(result.errors));
  return result.metadata;
}

describe('ProviderRunMetadataV1 contract bindings', () => {
  it('binds schema and raw-byte constants to the public contract', () => {
    expect(
      (schema as { properties: { schemaVersion: { const: number } } }).properties.schemaVersion
        .const,
    ).toBe(PROVIDER_RUN_METADATA_SCHEMA_VERSION);
    expect(METADATA_MAX_BYTES).toBe(32768);
    expect(MAX_METADATA_ERRORS).toBe(32);
    expect(MAX_METADATA_PATH_CHARS).toBe(256);
    expect(MAX_METADATA_PATH_UTF8_BYTES).toBe(1024);
    expect(parseProviderRunMetadata(new Uint8Array(METADATA_MAX_BYTES)).valid).toBe(false);
    expect(parseProviderRunMetadata(new Uint8Array(METADATA_MAX_BYTES + 1))).toEqual({
      valid: false,
      errors: [{ code: 'invalid-metadata-bounds', path: '' }],
    });
  });

  it('hashes only the nested semantic allowlist', () => {
    const metadata = validated();
    const baseline = computeMetadataSemanticSha256(metadata);
    const provenanceChanged = structuredClone(metadata) as ValidatedProviderRunMetadataV1;
    (provenanceChanged as { producingRunId: string }).producingRunId = '2';
    expect(computeMetadataSemanticSha256(provenanceChanged)).toBe(baseline);

    const includedChanged = structuredClone(metadata) as ValidatedProviderRunMetadataV1;
    (includedChanged as { cacheStatus: 'hit' }).cacheStatus = 'hit';
    expect(computeMetadataSemanticSha256(includedChanged)).not.toBe(baseline);

    const envelope = buildSemanticEnvelope(metadata);
    (envelope as unknown as { capability: { extra: string } }).capability.extra = 'ignored';
    expect(computeMetadataSemanticSha256(metadata)).toBe(baseline);
    const runtimeExtra = structuredClone(metadata) as ValidatedProviderRunMetadataV1 & {
      capability: { extra?: string };
    };
    runtimeExtra.capability.extra = 'ignored';
    expect(computeMetadataSemanticSha256(runtimeExtra)).toBe(baseline);
  });

  it('locks three byte-exact semantic hash vectors', () => {
    const fixture = (name: string): ValidatedProviderRunMetadataV1 => {
      const result = parseProviderRunMetadata(
        new Uint8Array(readFileSync(resolve('protocol/fixtures/provider-run-metadata/v1', name))),
      );
      if (!result.valid) throw new Error(JSON.stringify(result.errors));
      return result.metadata;
    };
    expect(computeMetadataSemanticSha256(validated())).toBe(
      '055625bd8fecda84f8f25c79d6ee9c7b57234d8f0eb208e57b51a71c1e22ee3c',
    );
    expect(computeMetadataSemanticSha256(fixture('valid-standard-resumed.json'))).toBe(
      '688947ca6636d7e475db04781e012de69678fa2cae777a4b468bbbbbf5f6950c',
    );
    expect(computeMetadataSemanticSha256(fixture('valid-standard-partial-cache.json'))).toBe(
      '17cc2949406249ca8a1daabb4fe41fc2d9274c0cc93c3c8d21dac102c3d6d638',
    );
  });

  it('locks canonical envelope bytes alongside each published hash vector', () => {
    const vectors = JSON.parse(
      readFileSync(resolve('protocol/fixtures/provider-run-metadata/v1/hash-vectors.json'), 'utf8'),
    ) as Array<{
      fixture: string;
      canonicalEnvelopeHex: string;
      semanticSha256: string;
    }>;
    expect(vectors).toHaveLength(3);
    for (const vector of vectors) {
      const result = parseProviderRunMetadata(
        new Uint8Array(
          readFileSync(resolve('protocol/fixtures/provider-run-metadata/v1', vector.fixture)),
        ),
      );
      if (!result.valid) throw new Error(JSON.stringify(result.errors));
      const envelopeBytes = canonicalJsonBytes(buildSemanticEnvelope(result.metadata));
      expect(Buffer.from(envelopeBytes).toString('hex')).toBe(vector.canonicalEnvelopeHex);
      expect(computeMetadataSemanticSha256(result.metadata)).toBe(vector.semanticSha256);
    }
  });

  it('keeps provider metadata imports on the shared canonical-json boundary', () => {
    const files = readdirSync(resolve('src/provider-metadata')).filter((name) =>
      name.endsWith('.ts'),
    );
    const sources = files
      .map((name) => readFileSync(resolve('src/provider-metadata', name), 'utf8'))
      .join('\n');
    expect(files).not.toEqual(
      expect.arrayContaining(['jcs.ts', 'canonical-json.ts', 'canonicalize.ts']),
    );
    expect(sources).toMatch(/from ['"]\.\.\/canonical-json\/index\.js['"]/);
    expect(sources).not.toMatch(/(?:export\s+)?(?:function|const|class)\s+canonicalJsonBytes\b/);
    expect(sources).not.toMatch(/function\s+(canonicalJson|sha256)|class\s+CanonicalJson/i);
    expect(sources).not.toMatch(
      /(?:function|const)\s+(?:writeCanonical|serializeCanonical|canonicalize)\b/i,
    );
    expect(sources).not.toMatch(/from ['"]\.\/[^'"]*(?:serializer|jcs|canonical)[^'"]*['"]/i);
    for (const source of files.map((name) =>
      readFileSync(resolve('src/provider-metadata', name), 'utf8'),
    )) {
      for (const specifier of source.matchAll(/from ['"]([^'"]+)['"]/g)) {
        if (/canonical|sha256/i.test(specifier[1]!))
          expect(specifier[1]).toBe('../canonical-json/index.js');
      }
    }
  });
});
