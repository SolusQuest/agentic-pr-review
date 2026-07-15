import { describe, expect, it } from 'vitest';
import { finalizeErrors } from './error-list.js';
import { MAX_METADATA_ERRORS, type MetadataError } from './types.js';

describe('finalizeErrors deterministic ordering', () => {
  it('sorts by path bytes then by code bytes', () => {
    const input: MetadataError[] = [
      { code: 'invalid-metadata-schema', path: '/b' },
      { code: 'invalid-metadata-unknown-enum', path: '/a' },
      { code: 'invalid-metadata-schema', path: '/a' },
    ];
    const out = finalizeErrors(input);
    expect(out).toEqual([
      { code: 'invalid-metadata-schema', path: '/a' },
      { code: 'invalid-metadata-unknown-enum', path: '/a' },
      { code: 'invalid-metadata-schema', path: '/b' },
    ]);
  });

  it('deduplicates by (code, path)', () => {
    const input: MetadataError[] = [
      { code: 'invalid-metadata-schema', path: '/a' },
      { code: 'invalid-metadata-schema', path: '/a' },
    ];
    const out = finalizeErrors(input);
    expect(out.length).toBe(1);
  });

  it('leaves lists of length <= MAX_METADATA_ERRORS untouched', () => {
    const input: MetadataError[] = Array.from({ length: MAX_METADATA_ERRORS }, (_, i) => ({
      code: 'invalid-metadata-schema' as const,
      path: '/' + String(i).padStart(3, '0'),
    }));
    const out = finalizeErrors(input);
    expect(out.length).toBe(MAX_METADATA_ERRORS);
    expect(out[out.length - 1]!.code).toBe('invalid-metadata-schema');
  });

  it('truncates > MAX_METADATA_ERRORS to 31 real + 1 sentinel = MAX_METADATA_ERRORS', () => {
    const input: MetadataError[] = Array.from({ length: MAX_METADATA_ERRORS + 1 }, (_, i) => ({
      code: 'invalid-metadata-schema' as const,
      path: '/' + String(i).padStart(3, '0'),
    }));
    const out = finalizeErrors(input);
    expect(out.length).toBe(MAX_METADATA_ERRORS);
    expect(out[out.length - 1]!.code).toBe('invalid-metadata-error-list-truncated');
    expect(out[out.length - 1]!.path).toBe('');
    // Real entries retained (31 of them).
    for (let i = 0; i < MAX_METADATA_ERRORS - 1; i += 1) {
      expect(out[i]!.code).toBe('invalid-metadata-schema');
    }
  });
});
