import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseProviderRunMetadata } from './parse.js';
import { METADATA_MAX_BYTES } from './types.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');

function bytes(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

describe('stage 1: raw-transport bounds', () => {
  it('accepts exactly METADATA_MAX_BYTES (32768) input length', () => {
    // Payload that is exactly at cap: fill with well-formed but semantically bogus JSON.
    const filler = ' '.repeat(METADATA_MAX_BYTES - 2);
    const input = bytes('{' + filler + '}');
    expect(input.byteLength).toBe(METADATA_MAX_BYTES);
    const r = parseProviderRunMetadata(input);
    // May fail schema, but MUST NOT fail with invalid-metadata-bounds.
    if (!r.valid) {
      expect(r.errors.some((e) => e.code === 'invalid-metadata-bounds')).toBe(false);
    }
  });

  it('rejects METADATA_MAX_BYTES + 1 with invalid-metadata-bounds', () => {
    const oversized = new Uint8Array(METADATA_MAX_BYTES + 1).fill(0x20);
    oversized[0] = 0x7b; // '{'
    oversized[oversized.length - 1] = 0x7d; // '}'
    const r = parseProviderRunMetadata(oversized);
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors).toEqual([{ code: 'invalid-metadata-bounds', path: '' }]);
    }
  });
});

describe('stage 2: BOM', () => {
  it('rejects UTF-8 BOM with invalid-metadata-bom', () => {
    const bom = new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]); // BOM + `{}`
    const r = parseProviderRunMetadata(bom);
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors).toEqual([{ code: 'invalid-metadata-bom', path: '' }]);
    }
  });
});

describe('stage 3: illegal UTF-8', () => {
  it('rejects an invalid continuation byte with invalid-metadata-utf8', () => {
    const bad = new Uint8Array([0xc3, 0x28]); // lone start byte
    const r = parseProviderRunMetadata(bad);
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors).toEqual([{ code: 'invalid-metadata-utf8', path: '' }]);
    }
  });
});

describe('stage 4: JSON syntax', () => {
  it('rejects malformed JSON with invalid-metadata-json', () => {
    const r = parseProviderRunMetadata(bytes('{"a": ,}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors[0]!.code).toBe('invalid-metadata-json');
    }
  });
});

describe('stage 5: duplicate JSON property', () => {
  it('rejects a root-level duplicate key before JSON.parse collapses it', () => {
    const r = parseProviderRunMetadata(bytes('{"a": 1, "a": 2}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors[0]!.code).toBe('invalid-metadata-duplicate-json-property');
    }
  });

  it('rejects a nested duplicate key', () => {
    const r = parseProviderRunMetadata(bytes('{"outer": {"k": 1, "k": 2}}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors[0]!.code).toBe('invalid-metadata-duplicate-json-property');
    }
  });
});

describe('stage 6: string-safety', () => {
  it('rejects NUL inside a string value with invalid-metadata-unicode', () => {
    const r = parseProviderRunMetadata(bytes('{"a": "b\\u0000c"}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors.some((e) => e.code === 'invalid-metadata-unicode')).toBe(true);
    }
  });

  it('rejects a lone high surrogate inside a string value', () => {
    const r = parseProviderRunMetadata(bytes('{"a": "\\uD83D"}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors.some((e) => e.code === 'invalid-metadata-unicode')).toBe(true);
    }
  });
});

describe('parse pipeline: fail-closed valid input', () => {
  it('parses a valid fixture end-to-end and returns a branded value', () => {
    const text = readFileSync(join(fixturesDir, 'valid-bootstrap-hit.json'), 'utf8');
    const r = parseProviderRunMetadata(bytes(text));
    expect(r.valid).toBe(true);
    if (r.valid) {
      expect(r.metadata.schemaVersion).toBe(1);
    }
  });
});

describe('stage 4/6 iterative safety on deeply-nested JSON within METADATA_MAX_BYTES', () => {
  it('does not throw a RangeError for ~16000 nested arrays containing 0', () => {
    // ~32 KiB ASCII: 16000 '[' + '0' + 16000 ']'. Well within METADATA_MAX_BYTES.
    const depth = 16000;
    const text = '['.repeat(depth) + '0' + ']'.repeat(depth);
    // Must complete without throwing. Result will fail at stage 7 (schema)
    // because it's not a metadata object, but that's a normal validation
    // failure -- what matters is the absence of stack overflow.
    expect(() => parseProviderRunMetadata(new TextEncoder().encode(text))).not.toThrow();
  });
  it('does not throw a RangeError for ~8000 nested objects with a schema-valid trailing structure', () => {
    // 8000 '{"a":' + '1' + 8000 '}' -> ~24 KiB.
    const depth = 8000;
    const opens = '{"a":'.repeat(depth);
    const closes = '}'.repeat(depth);
    const text = opens + '1' + closes;
    expect(() => parseProviderRunMetadata(new TextEncoder().encode(text))).not.toThrow();
  });
});

describe('stage-4 precedes stage-5 (malformed JSON wins over duplicate detection)', () => {
  it('duplicate root property followed by a trailing comma returns invalid-metadata-json', () => {
    const r = parseProviderRunMetadata(new TextEncoder().encode('{"a": 1, "a": 2,}'));
    expect(r.valid).toBe(false);
    if (!r.valid) {
      expect(r.errors[0]!.code).toBe('invalid-metadata-json');
    }
  });
});
