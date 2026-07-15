/**
 * Manifest deep-path oracle tests — byte-exact assertions against the
 * frozen literals in the design contract's
 * `### Safe diagnostic path for Unicode / additional-property rejections`,
 * block "Concrete deep-path golden vectors — frozen oracle".
 *
 * The expected literals are copied verbatim from the shared subsection.
 * They are NOT re-derived from the production implementation.
 */

import { describe, expect, it } from 'vitest';
import { MANIFEST_FILENAME, LEDGER_FILENAME, PROVIDER_RUN_METADATA_FILENAME } from './constants.js';
import { classifyStateBundleV2, type EntryDescriptor } from './classifier.js';

const LISTING: readonly EntryDescriptor[] = [
  { name: MANIFEST_FILENAME, isRegularFile: true },
  { name: LEDGER_FILENAME, isRegularFile: true },
  { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
];

const LEDGER_BYTES = new TextEncoder().encode('l');
const METADATA_BYTES = new TextEncoder().encode('m');

/**
 * Build a raw manifest object containing an ancestor chain of `depth`
 * unknown property names terminating in a lone-surrogate value.
 * The nested property names are all `untrusted-<i>` — ASCII, not schema-known,
 * not control, not empty; all get sanitized to `<untrusted-property>`.
 */
function buildDeepChain(depth: number): unknown {
  let value: unknown = '\uD800'; // terminal lone-surrogate value
  for (let i = depth - 1; i >= 0; i -= 1) {
    value = { [`untrusted-${i}`]: value };
  }
  return { chain: value };
}

// Because we want the top-level unknown chain (no schema-known prefix),
// we inject the chain at the top level of a raw parsed manifest — NOT
// nested under `stateKey` or another schema-known property.
function buildRawManifestWithTopLevelChain(ancestorCount: number): unknown {
  let value: unknown = '\uD800';
  const chain: Record<string, unknown> = {};
  let cursor: Record<string, unknown> = chain;
  for (let i = 0; i < ancestorCount - 1; i += 1) {
    const child: Record<string, unknown> = {};
    cursor[`untrusted-${i}`] = child;
    cursor = child;
  }
  cursor[`untrusted-${ancestorCount - 1}`] = value;
  return chain;
}

function classifyRaw(raw: unknown): ReturnType<typeof classifyStateBundleV2> {
  const bytes = new TextEncoder().encode(JSON.stringify(raw));
  return classifyStateBundleV2({
    manifestBytes: bytes,
    ledgerBytes: LEDGER_BYTES,
    providerRunMetadataBytes: METADATA_BYTES,
    entryListing: LISTING,
  });
}

// Silence unused-import lint.
void buildDeepChain;

describe('manifest-deep-path-no-truncation (shared oracle byte-equality)', () => {
  it('produces the byte-exact expected message', () => {
    // 9 total segments (8 unknown ancestors above the terminal lone-surrogate
    // value + the terminal leaf property whose value is the offending string).
    const raw = buildRawManifestWithTopLevelChain(9);
    const result = classifyRaw(raw);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    const expected =
      'x_invalid_unicode:' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>';
    expect(expected.length).toBe(207);
    expect(result.message).toBe(expected);
  });
});

describe('manifest-deep-path-truncation (shared oracle byte-equality)', () => {
  it('produces the byte-exact expected truncated message with <path-truncated> before final segment', () => {
    // 13 total segments (12 unknown ancestors above the terminal lone-surrogate
    // value + the terminal leaf property).
    const raw = buildRawManifestWithTopLevelChain(13);
    const result = classifyRaw(raw);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    const expected =
      'x_invalid_unicode:' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<untrusted-property>' +
      '/<path-truncated>' +
      '/<untrusted-property>';
    expect(expected.length).toBe(245);
    expect(result.message).toBe(expected);
  });
});
