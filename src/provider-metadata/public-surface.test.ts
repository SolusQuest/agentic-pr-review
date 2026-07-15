import { describe, expect, it } from 'vitest';
import { parseProviderRunMetadata } from './parse.js';
import { computeMetadataSemanticSha256, buildSemanticEnvelope } from './semantic-hash.js';
import { deriveAggregate } from './aggregate.js';
import { identityAgrees } from './identity.js';
import {
  METADATA_MAX_BYTES,
  MAX_METADATA_ERRORS,
  MAX_METADATA_PATH_CHARS,
  MAX_METADATA_PATH_UTF8_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  METADATA_SEMANTIC_HASH_DOMAIN_TAG,
} from './types.js';

describe('provider-metadata public surface', () => {
  it('exports shared constants (imported from #48)', () => {
    expect(METADATA_MAX_BYTES).toBe(32768);
    expect(PROVIDER_RUN_METADATA_SCHEMA_VERSION).toBe(1);
  });

  it('exports workstream-local constants at frozen values', () => {
    expect(MAX_METADATA_ERRORS).toBe(32);
    expect(MAX_METADATA_PATH_CHARS).toBe(256);
    expect(MAX_METADATA_PATH_UTF8_BYTES).toBe(1024);
    expect(METADATA_SEMANTIC_HASH_DOMAIN_TAG).toBe(
      'agentic-pr-review/provider-run-metadata-semantic/v1',
    );
  });

  it('exports functions', () => {
    expect(typeof parseProviderRunMetadata).toBe('function');
    expect(typeof computeMetadataSemanticSha256).toBe('function');
    expect(typeof buildSemanticEnvelope).toBe('function');
    expect(typeof deriveAggregate).toBe('function');
    expect(typeof identityAgrees).toBe('function');
  });
});

describe('provider-metadata public API restriction', () => {
  it('does not export validateProviderRunMetadata (only parseProviderRunMetadata is public)', async () => {
    const mod = await import('./index.js');
    const exported = Object.keys(mod);
    expect(exported).not.toContain('validateProviderRunMetadata');
    expect(exported).toContain('parseProviderRunMetadata');
  });
});

describe('parseProviderRunMetadataFromString convenience', () => {
  it('is exported alongside parseProviderRunMetadata', async () => {
    const mod = await import('./index.js');
    expect(Object.keys(mod)).toContain('parseProviderRunMetadataFromString');
  });
  it('delegates to the byte parser (produces identical result to encode+parse)', async () => {
    const { parseProviderRunMetadataFromString, parseProviderRunMetadata } =
      await import('./index.js');
    const oversized = 'x'.repeat(33 * 1024); // > 32 KiB after UTF-8 encoding.
    const asBytes = new TextEncoder().encode(oversized);
    const byBytes = parseProviderRunMetadata(asBytes);
    const byString = parseProviderRunMetadataFromString(oversized);
    expect(byString).toEqual(byBytes);
  });
});
