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
 * The first stage that emits at least one error terminates the pipeline. All
 * errors returned come from that single stage. `MetadataError.path` is produced
 * by the shared safe-diagnostic-path helpers imported from `src/state-v2/`;
 * caller-controlled property names never appear verbatim.
 */

import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { scanStringSafety, type SchemaNode } from '../state-v2/shared-safe-path.js';
import { validateStage8 } from './validate.js';
import { runSchemaStage } from './schema-stage.js';
import {
  METADATA_MAX_BYTES,
  type MetadataError,
  type ProviderRunMetadataV1,
  type ValidatedProviderRunMetadataV1,
  type ValidationResult,
} from './types.js';
import { finalizeErrors } from './error-list.js';

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
  // Stage 1 -- raw-transport bounds.
  if (bytes.byteLength > METADATA_MAX_BYTES) {
    return fail([{ code: 'invalid-metadata-bounds', path: '' }]);
  }

  // Stage 2 -- BOM.
  if (bytes.byteLength >= 3 && bytes[0] === BOM[0] && bytes[1] === BOM[1] && bytes[2] === BOM[2]) {
    return fail([{ code: 'invalid-metadata-bom', path: '' }]);
  }

  // Stage 3 -- UTF-8 decode. Node's TextDecoder with fatal: true throws on any
  // invalid UTF-8 sequence, including overlong forms and lone surrogates.
  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return fail([{ code: 'invalid-metadata-utf8', path: '' }]);
  }

  // Stage 4 -- JSON syntax + Stage 5 -- duplicate property. Both are performed
  // by a streaming JSON checker so duplicate detection precedes ordinary
  // `JSON.parse` (which would otherwise silently overwrite the earlier value).
  const jsonResult = parseJsonStrict(text);
  if (!jsonResult.ok) {
    return fail([jsonResult.error]);
  }
  const parsed = jsonResult.value;

  // Stage 6 -- string-safety scan (NUL / unpaired UTF-16 surrogate) using the
  // shared traversal and Schema-position resolver.
  const safety = scanStringSafety(parsed, rootSchema);
  if (safety !== undefined) {
    return fail([{ code: 'invalid-metadata-unicode', path: safeSegmentPath(safety.segments) }]);
  }

  // Stage 7 -- JSON Schema.
  const schemaErrors = runSchemaStage(parsed, rootSchema);
  if (schemaErrors.length > 0) {
    return fail(schemaErrors);
  }

  // Stage 8 -- semantic invariants.
  const semanticResult = validateStage8(parsed as ProviderRunMetadataV1);
  if (semanticResult.errors.length > 0) {
    return fail(semanticResult.errors);
  }

  // Success: apply the brand once.
  return {
    valid: true,
    metadata: semanticResult.metadata as ValidatedProviderRunMetadataV1,
  };
}

function fail(errors: MetadataError[]): ValidationResult<ValidatedProviderRunMetadataV1> {
  return { valid: false, errors: finalizeErrors(errors) };
}

function safeSegmentPath(segments: readonly string[]): string {
  if (segments.length === 0) return '';
  return '/' + segments.join('/');
}

// ---------------------------------------------------------------------------
// Stage 4 + Stage 5 -- JSON parser that rejects duplicate properties before
// they are collapsed by JSON.parse.
// ---------------------------------------------------------------------------

interface JsonOk {
  ok: true;
  value: unknown;
}
interface JsonErr {
  ok: false;
  error: MetadataError;
}
type JsonResult = JsonOk | JsonErr;

function parseJsonStrict(text: string): JsonResult {
  // First run JSON.parse for shape; a syntactic failure trumps duplicate.
  try {
    JSON.parse(text);
  } catch {
    return { ok: false, error: { code: 'invalid-metadata-json', path: '' } };
  }
  // Then scan for duplicate keys and return the parsed value alongside.
  const dup = findDuplicateKey(text);
  if (dup !== undefined) {
    return {
      ok: false,
      error: {
        code: 'invalid-metadata-duplicate-json-property',
        path: dup,
      },
    };
  }
  return { ok: true, value: JSON.parse(text) };
}

/**
 * Lightweight streaming key scanner. Walks the JSON text character-by-character
 * tracking object/array stack; each `"key":` inside an object is checked
 * against the sibling set at the same object depth. On the first duplicate
 * this returns the safe path to the parent (the resolver name of the offending
 * key is intentionally NOT included; only the parent scope is reported so
 * caller-controlled keys never leak).
 */
function findDuplicateKey(text: string): string | undefined {
  interface Frame {
    kind: 'object' | 'array';
    keys: Set<string>;
    parentPath: string;
    parentKey: string; // last seen key from parent scope for array indexing
    arrayIndex: number;
  }
  const stack: Frame[] = [];
  let i = 0;
  const pushObject = (parentPath: string) => {
    stack.push({
      kind: 'object',
      keys: new Set<string>(),
      parentPath,
      parentKey: '',
      arrayIndex: 0,
    });
  };
  const pushArray = (parentPath: string) => {
    stack.push({
      kind: 'array',
      keys: new Set<string>(),
      parentPath,
      parentKey: '',
      arrayIndex: 0,
    });
  };
  while (i < text.length) {
    const c = text.charCodeAt(i);
    // whitespace
    if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) {
      i += 1;
      continue;
    }
    if (c === 0x7b /* { */) {
      pushObject(currentPath(stack));
      i += 1;
      continue;
    }
    if (c === 0x7d /* } */) {
      stack.pop();
      i += 1;
      continue;
    }
    if (c === 0x5b /* [ */) {
      pushArray(currentPath(stack));
      i += 1;
      continue;
    }
    if (c === 0x5d /* ] */) {
      stack.pop();
      i += 1;
      continue;
    }
    if (c === 0x22 /* " */) {
      const stringEnd = scanStringEnd(text, i);
      const raw = text.slice(i + 1, stringEnd);
      const key = decodeJsonString(raw);
      i = stringEnd + 1;
      // After skipping whitespace, if next non-ws is `:`, this string was a key.
      let j = i;
      while (
        j < text.length &&
        (text.charCodeAt(j) === 0x20 ||
          text.charCodeAt(j) === 0x09 ||
          text.charCodeAt(j) === 0x0a ||
          text.charCodeAt(j) === 0x0d)
      ) {
        j += 1;
      }
      if (j < text.length && text.charCodeAt(j) === 0x3a /* : */) {
        // It was a key.
        const top = stack[stack.length - 1];
        if (top && top.kind === 'object') {
          if (top.keys.has(key)) {
            // Duplicate! Report the parent-object safe path.
            return top.parentPath;
          }
          top.keys.add(key);
        }
        i = j + 1;
      } else {
        // Value string.
      }
      continue;
    }
    if (c === 0x2c /* , */) {
      // Comma at object depth: reset currentKey. At array depth: advance index.
      const top = stack[stack.length - 1];
      if (top && top.kind === 'array') top.arrayIndex += 1;
      i += 1;
      continue;
    }
    // any other char -- number / literal / value body
    i += 1;
  }
  return undefined;
}

function scanStringEnd(text: string, start: number): number {
  let i = start + 1;
  while (i < text.length) {
    const c = text.charCodeAt(i);
    if (c === 0x5c /* \ */) {
      i += 2;
      continue;
    }
    if (c === 0x22 /* " */) return i;
    i += 1;
  }
  return text.length;
}

function decodeJsonString(raw: string): string {
  // Fast path: no backslashes.
  if (raw.indexOf('\\') === -1) return raw;
  try {
    return JSON.parse('"' + raw + '"');
  } catch {
    return raw;
  }
}

/**
 * The current path is intentionally "" — reporting caller-controlled parent
 * keys verbatim would violate the safe-diagnostic-path contract. A future
 * enhancement can attach a resolver-derived breadcrumb per the shared
 * subsection, but the sentinel path is safe by construction.
 */
function currentPath(stack: ReadonlyArray<{ kind: 'object' | 'array' }>): string {
  return stack.length === 0 ? '' : '';
}
