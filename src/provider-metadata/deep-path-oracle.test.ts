import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseProviderRunMetadata } from './parse.js';
import { MAX_METADATA_PATH_CHARS, MAX_METADATA_PATH_UTF8_BYTES } from './types.js';
import { utf8ByteLength } from './safe-path-helpers.js';

/**
 * Deep-path oracle for issue #51.
 *
 * The design contract `docs/20_architecture/session-ledger-and-prefix-contract.md`
 * (subsection "Concrete deep-path golden vectors -- frozen oracle") freezes
 * two exact `MetadataError.path` literals for the metadata sidecar:
 *
 *   metadata-deep-path-no-truncation  -- fullSanitizedSegmentCount = 10.
 *   metadata-deep-path-truncation     -- fullSanitizedSegmentCount = 14
 *                                        (10 leading + <path-truncated> +
 *                                        terminal <untrusted-property>).
 *
 * Both vectors are exercised through the public parser (`parseProviderRunMetadata`)
 * on named fixtures whose input JSON is a top-level chain of unknown
 * properties terminated by a lone-surrogate string value. The complete
 * `MetadataError[]` is asserted byte-exact against the frozen literal via
 * the fixture's `.expected.json` oracle (never re-derived through
 * `finalizePath`).
 */

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');
const encoder = new TextEncoder();

const NO_TRUNCATION_PATH =
  '/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>';

const TRUNCATION_PATH =
  '/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<untrusted-property>/<path-truncated>/<untrusted-property>';

function loadFixture(name: string): Uint8Array {
  return encoder.encode(readFileSync(join(fixturesDir, name), 'utf8'));
}
function loadOracle(name: string): { errors: Array<{ code: string; path: string }> } {
  return JSON.parse(readFileSync(join(fixturesDir, name + '.expected.json'), 'utf8'));
}

describe('metadata-deep-path-no-truncation -- fullSanitizedSegmentCount = 10, top-level unknown-property chain', () => {
  it('parser emits the exact frozen path literal (byte-exact) and stays inside both caps', () => {
    const fixture = 'invalid-unicode-deep-path-no-truncation.json';
    const oracle = loadOracle(fixture);
    // Sanity: the oracle stores the exact frozen literal.
    expect(oracle.errors).toEqual([{ code: 'invalid-metadata-unicode', path: NO_TRUNCATION_PATH }]);
    expect(NO_TRUNCATION_PATH.length).toBe(210);
    expect(utf8ByteLength(NO_TRUNCATION_PATH)).toBe(210);
    expect(NO_TRUNCATION_PATH.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
    expect(utf8ByteLength(NO_TRUNCATION_PATH)).toBeLessThanOrEqual(MAX_METADATA_PATH_UTF8_BYTES);

    const r = parseProviderRunMetadata(loadFixture(fixture));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    // Complete MetadataError[] equals the frozen oracle literal.
    expect(r.errors).toEqual(oracle.errors);
  });
});

describe('metadata-deep-path-truncation -- fullSanitizedSegmentCount = 14, top-level unknown-property chain, greedy truncation branch', () => {
  it('parser emits the exact frozen path literal (byte-exact) with <path-truncated> inserted before the final segment', () => {
    const fixture = 'invalid-unicode-deep-path-truncation.json';
    const oracle = loadOracle(fixture);
    expect(oracle.errors).toEqual([{ code: 'invalid-metadata-unicode', path: TRUNCATION_PATH }]);
    expect(TRUNCATION_PATH.length).toBe(248);
    expect(utf8ByteLength(TRUNCATION_PATH)).toBe(248);
    expect(TRUNCATION_PATH.length).toBeLessThanOrEqual(MAX_METADATA_PATH_CHARS);
    expect(utf8ByteLength(TRUNCATION_PATH)).toBeLessThanOrEqual(MAX_METADATA_PATH_UTF8_BYTES);
    // The truncated path preserves the terminal <untrusted-property> segment
    // (final-segment rule) and inserts a single <path-truncated> marker
    // immediately before it.
    expect(TRUNCATION_PATH.endsWith('/<path-truncated>/<untrusted-property>')).toBe(true);

    const r = parseProviderRunMetadata(loadFixture(fixture));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual(oracle.errors);
  });
});
