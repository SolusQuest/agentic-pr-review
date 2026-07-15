/**
 * Deterministic sort / dedup / truncation helpers for the parser's returned
 * `MetadataError[]`. Applied by `parseProviderRunMetadata` immediately before
 * returning any failing stage's error list. Rules (issue #51):
 *
 * - Deduplicate by tuple `(code, path)`.
 * - Stable sort with primary key `path` (byte-lexicographic on UTF-8) and
 *   secondary key `code` (byte-lexicographic on UTF-8).
 * - `MAX_METADATA_ERRORS = 32` is the TOTAL returned array length (sentinel
 *   included). > 32 -> keep the first 31 sorted real errors, append one
 *   terminal sentinel `{ code: "invalid-metadata-error-list-truncated",
 *   path: "" }`. The sentinel is exempt from the ordinary comparator and is
 *   always the final entry.
 */

import { MAX_METADATA_ERRORS, type MetadataError } from './types.js';

const TRUNCATED: MetadataError = {
  code: 'invalid-metadata-error-list-truncated',
  path: '',
};

const encoder = new TextEncoder();

function utf8Bytes(s: string): Uint8Array {
  return encoder.encode(s);
}

function compareBytes(a: Uint8Array, b: Uint8Array): number {
  const minLen = Math.min(a.length, b.length);
  for (let i = 0; i < minLen; i += 1) {
    const aa = a[i]!;
    const bb = b[i]!;
    if (aa < bb) return -1;
    if (aa > bb) return 1;
  }
  if (a.length < b.length) return -1;
  if (a.length > b.length) return 1;
  return 0;
}

function compareByPathThenCode(x: MetadataError, y: MetadataError): number {
  const pathCmp = compareBytes(utf8Bytes(x.path), utf8Bytes(y.path));
  if (pathCmp !== 0) return pathCmp;
  return compareBytes(utf8Bytes(x.code), utf8Bytes(y.code));
}

export function finalizeErrors(input: readonly MetadataError[]): MetadataError[] {
  if (input.length === 0) return [];
  const seen = new Set<string>();
  const dedup: MetadataError[] = [];
  for (const e of input) {
    const k = `${e.code}\u0000${e.path}`;
    if (seen.has(k)) continue;
    seen.add(k);
    dedup.push(e);
  }
  dedup.sort(compareByPathThenCode);
  if (dedup.length <= MAX_METADATA_ERRORS) return dedup;
  const retained = dedup.slice(0, MAX_METADATA_ERRORS - 1);
  retained.push(TRUNCATED);
  return retained;
}
