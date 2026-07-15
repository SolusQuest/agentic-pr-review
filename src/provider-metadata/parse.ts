/**
 * Eight-stage fail-closed parse pipeline for ProviderRunMetadataV1.
 *
 * Stages (issue #51 `### Coded validator errors and stage table`):
 *   1  raw byte bounds     -> invalid-metadata-bounds
 *   2  UTF-8 BOM detection -> invalid-metadata-bom
 *   3  UTF-8 decode        -> invalid-metadata-utf8
 *   4  JSON syntax         -> invalid-metadata-json
 *   5  duplicate property  -> invalid-metadata-duplicate-json-property
 *   6  string-safety scan  -> invalid-metadata-unicode
 *   7  JSON Schema (Ajv)   -> additional-property / unknown-enum /
 *                              token-out-of-range / schema
 *   8  semantic invariants -> identity / cross-mismatch / ordering /
 *                              uniqueness / contiguity / partitions / outcome /
 *                              stateless proof / error-code order /
 *                              aggregate-mismatch / model-alias / etc.
 *
 * Stages 4 + 5 run in a single strict JSON parser pass that rejects duplicate
 * property names on descent, so the JSON.parse duplicate-collapsing behavior
 * is never observed. Only after stage 5 passes is `JSON.parse` invoked (the
 * grammar has already been proven by the strict parser).
 *
 * The first stage that emits at least one error terminates the pipeline. All
 * errors returned come from that single stage. Every `MetadataError.path` is
 * post-processed by `finalizePath` so caller-controlled property names never
 * leak into diagnostics and every path is bounded by the workstream-local
 * `MAX_METADATA_PATH_CHARS` / `MAX_METADATA_PATH_UTF8_BYTES` caps with the
 * final-segment-preserving truncation from the shared safe-diagnostic-path
 * subsection.
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
 * list on failure. Every returned array is finalized (deduplicated, sorted,
 * bounded to `MAX_METADATA_ERRORS` with a terminal sentinel when needed).
 */
export function parseProviderRunMetadata(
  bytes: Uint8Array,
): ValidationResult<ValidatedProviderRunMetadataV1> {
  // Stage 1 -- raw byte bounds.
  if (bytes.byteLength > METADATA_MAX_BYTES) {
    return fail([{ code: 'invalid-metadata-bounds', path: '' }]);
  }
  // Stage 2 -- UTF-8 BOM.
  if (bytes.byteLength >= 3 && bytes[0] === BOM[0] && bytes[1] === BOM[1] && bytes[2] === BOM[2]) {
    return fail([{ code: 'invalid-metadata-bom', path: '' }]);
  }
  // Stage 3 -- UTF-8 decode.
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return fail([{ code: 'invalid-metadata-utf8', path: '' }]);
  }

  // Stages 4 + 5 -- strict JSON parse + duplicate-property detection. If the
  // strict parser reports a grammar failure, that is stage 4; if it reports a
  // duplicate name, that is stage 5 and takes precedence over the (later)
  // JSON.parse call, which is only used to construct the value once stages 4
  // and 5 have both passed.
  const strict = strictJsonParse(text);
  if (!strict.ok) {
    return fail([strict.error]);
  }
  const parsed = strict.value;

  // Stage 6 -- string-safety (NUL / lone UTF-16 surrogate) via shared traversal.
  const safety = scanStringSafety(parsed, rootSchema);
  if (safety !== undefined) {
    return fail([{ code: 'invalid-metadata-unicode', path: finalizePath(safety.segments) }]);
  }

  // Stage 7 -- schema.
  const schemaErrors = runSchemaStage(parsed, rootSchema);
  if (schemaErrors.length > 0) {
    return fail(schemaErrors);
  }

  // Stage 8 -- semantic invariants.
  const semantic = validateStage8(parsed as ProviderRunMetadataV1);
  if (semantic.errors.length > 0) {
    return fail(semantic.errors);
  }

  return {
    valid: true,
    metadata: semantic.metadata as ValidatedProviderRunMetadataV1,
  };
}

function fail(errors: MetadataError[]): ValidationResult<ValidatedProviderRunMetadataV1> {
  return { valid: false, errors: finalizeErrors(errors) };
}

// ---------------------------------------------------------------------------
// Strict JSON parser -- stage 4 + stage 5.
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

/**
 * Grammar-preserving JSON tokenizer + validator. Emits at most one error:
 * `invalid-metadata-json` for any grammar failure or `invalid-metadata-duplicate-json-property`
 * for the first duplicate object name encountered on descent. The value is
 * reconstructed via `JSON.parse` only after both stages pass; the reconstruction
 * cannot introduce any error not already visible.
 *
 * Path reporting for duplicates uses safe segments only. Object keys that are
 * NOT schema-known at that depth are replaced by the shared sanitizer marker;
 * caller-controlled key text never appears verbatim.
 */
function strictJsonParse(text: string): StrictResult {
  const state: State = {
    text,
    pos: 0,
  };
  skipWhitespace(state);
  const stack: string[][] = [];
  const path: string[] = [];
  const dupResult = parseValue(state, stack, path);
  if (dupResult !== null) return { ok: false, error: dupResult };
  skipWhitespace(state);
  if (state.pos !== text.length) {
    return { ok: false, error: { code: 'invalid-metadata-json', path: '' } };
  }
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch {
    return { ok: false, error: { code: 'invalid-metadata-json', path: '' } };
  }
}

interface State {
  readonly text: string;
  pos: number;
}

function skipWhitespace(s: State): void {
  const t = s.text;
  while (s.pos < t.length) {
    const c = t.charCodeAt(s.pos);
    if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) {
      s.pos += 1;
    } else {
      break;
    }
  }
}

const JSON_ERR: MetadataError = { code: 'invalid-metadata-json', path: '' };

/**
 * Parses one JSON value at the current position. Returns `null` on success or
 * the first `MetadataError` encountered (grammar or duplicate).
 */
function parseValue(s: State, stack: string[][], path: string[]): MetadataError | null {
  skipWhitespace(s);
  if (s.pos >= s.text.length) return JSON_ERR;
  const c = s.text.charCodeAt(s.pos);
  if (c === 0x7b /* { */) return parseObject(s, stack, path);
  if (c === 0x5b /* [ */) return parseArray(s, stack, path);
  if (c === 0x22 /* " */) return parseString(s);
  if (c === 0x74 /* t */ || c === 0x66 /* f */) return parseLiteral(s);
  if (c === 0x6e /* n */) return parseLiteral(s);
  if (c === 0x2d /* - */ || (c >= 0x30 && c <= 0x39)) return parseNumber(s);
  return JSON_ERR;
}

function parseObject(s: State, stack: string[][], path: string[]): MetadataError | null {
  s.pos += 1; // '{'
  const names = new Set<string>();
  const currentPath: string[] = path.slice();
  skipWhitespace(s);
  if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x7d) {
    s.pos += 1;
    return null;
  }
  while (s.pos < s.text.length) {
    skipWhitespace(s);
    // Key.
    if (s.text.charCodeAt(s.pos) !== 0x22) return JSON_ERR;
    const keyStart = s.pos;
    const keyErr = parseString(s);
    if (keyErr) return keyErr;
    const rawKey = s.text.slice(keyStart, s.pos);
    let key: string;
    try {
      key = JSON.parse(rawKey);
    } catch {
      return JSON_ERR;
    }
    if (typeof key !== 'string') return JSON_ERR;
    if (names.has(key)) {
      // Stage 5 duplicate. Report the parent-object safe path.
      return {
        code: 'invalid-metadata-duplicate-json-property',
        path: finalizePath(currentPath),
      };
    }
    names.add(key);
    // ':'
    skipWhitespace(s);
    if (s.pos >= s.text.length || s.text.charCodeAt(s.pos) !== 0x3a) return JSON_ERR;
    s.pos += 1;
    // Value; descend with a schema-agnostic safe path (always <untrusted-property>).
    // The correct schema-known/untrusted decision requires the shared resolver,
    // but the duplicate path is defined only for the parent scope, so this
    // descent segment is only used if a nested duplicate is found within the
    // value itself.
    stack.push([...currentPath, sanitizeParentSegment(key)]);
    const childErr = parseValue(s, stack, stack[stack.length - 1]!);
    stack.pop();
    if (childErr) return childErr;
    skipWhitespace(s);
    if (s.pos >= s.text.length) return JSON_ERR;
    const next = s.text.charCodeAt(s.pos);
    if (next === 0x2c) {
      s.pos += 1;
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

function parseArray(s: State, stack: string[][], path: string[]): MetadataError | null {
  s.pos += 1; // '['
  skipWhitespace(s);
  if (s.pos < s.text.length && s.text.charCodeAt(s.pos) === 0x5d) {
    s.pos += 1;
    return null;
  }
  let idx = 0;
  while (s.pos < s.text.length) {
    const nested = [...path, String(idx)];
    stack.push(nested);
    const err = parseValue(s, stack, nested);
    stack.pop();
    if (err) return err;
    skipWhitespace(s);
    if (s.pos >= s.text.length) return JSON_ERR;
    const next = s.text.charCodeAt(s.pos);
    if (next === 0x2c) {
      s.pos += 1;
      idx += 1;
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

function parseString(s: State): MetadataError | null {
  if (s.text.charCodeAt(s.pos) !== 0x22) return JSON_ERR;
  s.pos += 1;
  while (s.pos < s.text.length) {
    const c = s.text.charCodeAt(s.pos);
    if (c === 0x5c) {
      s.pos += 2;
      continue;
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
  const start = s.pos;
  let i = start;
  const t = s.text;
  if (t.charCodeAt(i) === 0x2d) i += 1;
  // int part
  if (i >= t.length) return JSON_ERR;
  const c = t.charCodeAt(i);
  if (c === 0x30) {
    i += 1;
  } else if (c >= 0x31 && c <= 0x39) {
    i += 1;
    while (i < t.length && t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39) i += 1;
  } else {
    return JSON_ERR;
  }
  // frac
  if (i < t.length && t.charCodeAt(i) === 0x2e) {
    i += 1;
    if (i >= t.length || !(t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39)) return JSON_ERR;
    while (i < t.length && t.charCodeAt(i) >= 0x30 && t.charCodeAt(i) <= 0x39) i += 1;
  }
  // exp
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
 * Every key encountered by this stage-5 tokenizer is untrusted at the time of
 * inspection (the schema stage has not yet run), so a caller-controlled name
 * MUST NOT appear verbatim in a duplicate-detection safe path. Every parent
 * segment is replaced with the shared `<untrusted-property>` marker.
 */
function sanitizeParentSegment(_key: string): string {
  return '<untrusted-property>';
}
