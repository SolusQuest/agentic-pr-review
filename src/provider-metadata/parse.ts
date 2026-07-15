/**
 * Eight-stage fail-closed parse pipeline for ProviderRunMetadataV1.
 *
 * Stages (issue #51 `### Coded validator errors and stage table`):
 *   1  raw byte bounds     -> invalid-metadata-bounds
 *   2  UTF-8 BOM detection -> invalid-metadata-bom
 *   3  UTF-8 decode        -> invalid-metadata-utf8
 *   4  JSON syntax         -> invalid-metadata-json          (must precede stage 5)
 *   5  duplicate property  -> invalid-metadata-duplicate-json-property
 *   6  string-safety scan  -> invalid-metadata-unicode
 *   7  JSON Schema (Ajv)   -> additional-property / unknown-enum /
 *                              token-out-of-range / schema
 *   8  semantic invariants -> identity / cross-mismatch / ordering /
 *                              uniqueness / contiguity / partitions / outcome /
 *                              stateless proof / error-code order /
 *                              aggregate-mismatch / model-alias / etc.
 *
 * Stage 4 wins over stage 5: the tokenizer walks the WHOLE document and only
 * yields the recorded duplicate if grammar validation completes without error.
 * The tokenizer decodes JSON string escapes with an in-house routine (no
 * `JSON.parse` call anywhere during stages 4 or 5). Once both stages pass,
 * `JSON.parse` is invoked to reconstruct the value tree.
 */

import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { scanStringSafety, type SchemaNode } from '../state-v2/shared-safe-path.js';
import { finalizeErrors } from './error-list.js';
import { finalizePath } from './safe-path-helpers.js';
import { runSchemaStage } from './schema-stage.js';
import { validateStage8 } from './validate.js';
import {
  METADATA_MAX_BYTES,
  type MetadataError,
  type ProviderRunMetadataV1,
  type ValidatedProviderRunMetadataV1,
  type ValidationResult,
} from './types.js';

const rootSchema = schema as unknown as SchemaNode;
const BOM = new Uint8Array([0xef, 0xbb, 0xbf]);

/**
 * Fail-closed public entry point. Consumes raw metadata bytes and returns a
 * branded validated value on success, or a deterministic single-stage error
 * list on failure.
 */
export function parseProviderRunMetadata(
  bytes: Uint8Array,
): ValidationResult<ValidatedProviderRunMetadataV1> {
  if (bytes.byteLength > METADATA_MAX_BYTES) {
    return fail([{ code: 'invalid-metadata-bounds', path: '' }]);
  }
  if (bytes.byteLength >= 3 && bytes[0] === BOM[0] && bytes[1] === BOM[1] && bytes[2] === BOM[2]) {
    return fail([{ code: 'invalid-metadata-bom', path: '' }]);
  }
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return fail([{ code: 'invalid-metadata-utf8', path: '' }]);
  }

  // Stages 4 + 5 -- strict JSON grammar + duplicate-property detection.
  const strict = strictJsonParse(text);
  if (!strict.ok) return fail([strict.error]);
  const parsed = strict.value;

  const safety = scanStringSafety(parsed, rootSchema);
  if (safety !== undefined) {
    return fail([{ code: 'invalid-metadata-unicode', path: finalizePath(safety.segments) }]);
  }
  const schemaErrors = runSchemaStage(parsed, rootSchema);
  if (schemaErrors.length > 0) return fail(schemaErrors);

  const semantic = validateStage8(parsed as ProviderRunMetadataV1);
  if (semantic.errors.length > 0) return fail(semantic.errors);

  return {
    valid: true,
    metadata: semantic.metadata as unknown as ValidatedProviderRunMetadataV1,
  };
}

/**
 * Convenience wrapper that encodes a JS string to UTF-8 bytes and delegates
 * to the authoritative byte parser. This is NOT an independent code path; it
 * exists purely so callers holding an in-memory JS string can feed it in
 * without a manual `new TextEncoder().encode(...)` step.
 */
const stringEncoder = new TextEncoder();
export function parseProviderRunMetadataFromString(
  text: string,
): ValidationResult<ValidatedProviderRunMetadataV1> {
  return parseProviderRunMetadata(stringEncoder.encode(text));
}

function fail(errors: MetadataError[]): ValidationResult<ValidatedProviderRunMetadataV1> {
  return { valid: false, errors: finalizeErrors(errors) };
}

// ---------------------------------------------------------------------------
// Strict JSON tokenizer (stages 4 + 5). Stage 4 wins over stage 5.
// ---------------------------------------------------------------------------

interface StrictOk {
  ok: true;
  value: unknown;
}
interface StrictErr {
  ok: false;
  error: MetadataError;
}
type StrictResult = StrictOk | StrictErr;

interface State {
  readonly text: string;
  pos: number;
  firstDuplicate: MetadataError | null;
}

const JSON_ERR: MetadataError = { code: 'invalid-metadata-json', path: '' };

function strictJsonParse(text: string): StrictResult {
  const state: State = { text, pos: 0, firstDuplicate: null };
  skipWhitespace(state);
  const err = parseValue(state, []);
  if (err) return { ok: false, error: err };
  skipWhitespace(state);
  if (state.pos !== text.length) return { ok: false, error: JSON_ERR };
  // Stage 4 passed. If a duplicate was recorded during the walk, stage 5 wins.
  if (state.firstDuplicate) return { ok: false, error: state.firstDuplicate };
  // Reconstruct the value. JSON.parse cannot fail here since the grammar has
  // been proven above, but wrap defensively.
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch {
    return { ok: false, error: JSON_ERR };
  }
}

function skipWhitespace(s: State): void {
  const t = s.text;
  while (s.pos < t.length) {
    const c = t.charCodeAt(s.pos);
    if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) s.pos += 1;
    else return;
  }
}

function parseValue(s: State, path: readonly string[]): MetadataError | null {
  skipWhitespace(s);
  if (s.pos >= s.text.length) return JSON_ERR;
  const c = s.text.charCodeAt(s.pos);
  if (c === 0x7b) return parseObject(s, path);
  if (c === 0x5b) return parseArray(s, path);
  if (c === 0x22) return parseString(s);
  if (c === 0x74 || c === 0x66 || c === 0x6e) return parseLiteral(s);
  if (c === 0x2d || (c >= 0x30 && c <= 0x39)) return parseNumber(s);
  return JSON_ERR;
}

function parseObject(s: State, path: readonly string[]): MetadataError | null {
  s.pos += 1; // '{'
  skipWhitespace(s);
  const names = new Set<string>();
  if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x7d) {
    s.pos += 1;
    return null;
  }
  while (s.pos < s.text.length) {
    skipWhitespace(s);
    if (s.pos >= s.text.length || s.text.charCodeAt(s.pos) !== 0x22) return JSON_ERR;
    const nameStart = s.pos + 1;
    const nameErr = parseString(s);
    if (nameErr) return nameErr;
    const rawName = s.text.slice(nameStart, s.pos - 1);
    const decoded = decodeJsonString(rawName);
    if (decoded === undefined) return JSON_ERR;
    // Record only the FIRST duplicate; stage 4 still needs to complete.
    if (names.has(decoded) && s.firstDuplicate === null) {
      s.firstDuplicate = {
        code: 'invalid-metadata-duplicate-json-property',
        path: finalizePath(path),
      };
    } else {
      names.add(decoded);
    }
    skipWhitespace(s);
    if (s.pos >= s.text.length || s.text.charCodeAt(s.pos) !== 0x3a) return JSON_ERR;
    s.pos += 1;
    const valErr = parseValue(s, [...path, '<untrusted-property>']);
    if (valErr) return valErr;
    skipWhitespace(s);
    if (s.pos >= s.text.length) return JSON_ERR;
    const next = s.text.charCodeAt(s.pos);
    if (next === 0x2c) {
      s.pos += 1;
      // Reject trailing comma (RFC 8259 forbids it).
      skipWhitespace(s);
      if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x7d) return JSON_ERR;
      continue;
    }
    if (next === 0x7d) {
      s.pos += 1;
      return null;
    }
    return JSON_ERR;
  }
  return JSON_ERR;
}

function parseArray(s: State, path: readonly string[]): MetadataError | null {
  s.pos += 1; // '['
  skipWhitespace(s);
  if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x5d) {
    s.pos += 1;
    return null;
  }
  let idx = 0;
  while (s.pos < s.text.length) {
    const err = parseValue(s, [...path, String(idx)]);
    if (err) return err;
    skipWhitespace(s);
    if (s.pos >= s.text.length) return JSON_ERR;
    const next = s.text.charCodeAt(s.pos);
    if (next === 0x2c) {
      s.pos += 1;
      idx += 1;
      skipWhitespace(s);
      if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x5d) return JSON_ERR;
      continue;
    }
    if (next === 0x5d) {
      s.pos += 1;
      return null;
    }
    return JSON_ERR;
  }
  return JSON_ERR;
}

/**
 * Advance past a JSON string starting at the current `"`. On success, `s.pos`
 * lands one past the closing `"`.
 */
function parseString(s: State): MetadataError | null {
  if (s.text.charCodeAt(s.pos) !== 0x22) return JSON_ERR;
  s.pos += 1;
  const t = s.text;
  while (s.pos < t.length) {
    const c = t.charCodeAt(s.pos);
    if (c === 0x5c) {
      // escape; validate the escape form (u#### / one of "\/bfnrt).
      s.pos += 1;
      if (s.pos >= t.length) return JSON_ERR;
      const esc = t.charCodeAt(s.pos);
      if (
        esc === 0x22 ||
        esc === 0x5c ||
        esc === 0x2f ||
        esc === 0x62 ||
        esc === 0x66 ||
        esc === 0x6e ||
        esc === 0x72 ||
        esc === 0x74
      ) {
        s.pos += 1;
        continue;
      }
      if (esc === 0x75) {
        // \uXXXX
        if (s.pos + 4 >= t.length) return JSON_ERR;
        for (let i = 1; i <= 4; i += 1) {
          const h = t.charCodeAt(s.pos + i);
          if (!isHexDigit(h)) return JSON_ERR;
        }
        s.pos += 5;
        continue;
      }
      return JSON_ERR;
    }
    if (c === 0x22) {
      s.pos += 1;
      return null;
    }
    if (c < 0x20) return JSON_ERR;
    s.pos += 1;
  }
  return JSON_ERR;
}

function isHexDigit(c: number): boolean {
  return (c >= 0x30 && c <= 0x39) || (c >= 0x41 && c <= 0x46) || (c >= 0x61 && c <= 0x66);
}

function parseLiteral(s: State): MetadataError | null {
  const rest = s.text.slice(s.pos);
  if (rest.startsWith('true')) {
    s.pos += 4;
    return null;
  }
  if (rest.startsWith('false')) {
    s.pos += 5;
    return null;
  }
  if (rest.startsWith('null')) {
    s.pos += 4;
    return null;
  }
  return JSON_ERR;
}

function parseNumber(s: State): MetadataError | null {
  const t = s.text;
  const start = s.pos;
  let i = start;
  if (t.charCodeAt(i) === 0x2d) i += 1;
  if (i >= t.length) return JSON_ERR;
  const c = t.charCodeAt(i);
  if (c === 0x30) i += 1;
  else if (c >= 0x31 && c <= 0x39) {
    i += 1;
    while (i < t.length && t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39) i += 1;
  } else return JSON_ERR;
  if (i < t.length && t.charCodeAt(i) === 0x2e) {
    i += 1;
    if (i >= t.length || !(t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39)) return JSON_ERR;
    while (i < t.length && t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39) i += 1;
  }
  if (i < t.length && (t.charCodeAt(i) === 0x65 || t.charCodeAt(i) === 0x45)) {
    i += 1;
    if (i < t.length && (t.charCodeAt(i) === 0x2b || t.charCodeAt(i) === 0x2d)) i += 1;
    if (i >= t.length || !(t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39)) return JSON_ERR;
    while (i < t.length && t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39) i += 1;
  }
  if (i === start) return JSON_ERR;
  s.pos = i;
  return null;
}

/**
 * Decode a JSON string body (the raw text between the quotes, not the quotes
 * themselves) using an in-house routine so stages 4/5 make no calls to
 * `JSON.parse`. Returns `undefined` on any malformed escape.
 */
function decodeJsonString(raw: string): string | undefined {
  let out = '';
  for (let i = 0; i < raw.length; i += 1) {
    const c = raw.charCodeAt(i);
    if (c === 0x5c) {
      if (i + 1 >= raw.length) return undefined;
      const esc = raw.charCodeAt(i + 1);
      switch (esc) {
        case 0x22:
          out += '"';
          i += 1;
          break;
        case 0x5c:
          out += '\\';
          i += 1;
          break;
        case 0x2f:
          out += '/';
          i += 1;
          break;
        case 0x62:
          out += '\b';
          i += 1;
          break;
        case 0x66:
          out += '\f';
          i += 1;
          break;
        case 0x6e:
          out += '\n';
          i += 1;
          break;
        case 0x72:
          out += '\r';
          i += 1;
          break;
        case 0x74:
          out += '\t';
          i += 1;
          break;
        case 0x75: {
          if (i + 5 >= raw.length) return undefined;
          const hex = raw.slice(i + 2, i + 6);
          if (!/^[0-9a-fA-F]{4}$/.test(hex)) return undefined;
          out += String.fromCharCode(parseInt(hex, 16));
          i += 5;
          break;
        }
        default:
          return undefined;
      }
      continue;
    }
    out += raw[i];
  }
  return out;
}
