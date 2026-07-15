/**
 * Eight-stage fail-closed parse pipeline for ProviderRunMetadataV1.
 *
 * Stages (issue #51 `### Coded validator errors and stage table`):
 *   1 raw byte bounds     -> invalid-metadata-bounds
 *   2 UTF-8 BOM detection -> invalid-metadata-bom
 *   3 UTF-8 decode        -> invalid-metadata-utf8
 *   4 JSON syntax         -> invalid-metadata-json          (must precede stage 5)
 *   5 duplicate property  -> invalid-metadata-duplicate-json-property
 *   6 string-safety scan  -> invalid-metadata-unicode
 *   7 JSON Schema (Ajv)   -> additional-property / unknown-enum /
 *                             token-out-of-range / schema
 *   8 semantic invariants -> identity / cross-mismatch / ordering /
 *                             uniqueness / contiguity / partitions / outcome /
 *                             stateless proof / error-code order /
 *                             aggregate-mismatch / model-alias / etc.
 *
 * Stage 4 wins over stage 5: the tokenizer walks the WHOLE document and only
 * yields the recorded duplicate if grammar validation completes without error.
 * The tokenizer is iterative (explicit stack) so a deeply-nested JSON document
 * within the raw byte cap cannot exhaust the JavaScript call stack.
 * `JSON.parse` is only invoked once both stages 4 and 5 have passed.
 */

import schema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };
import { type SchemaNode } from '../state-v2/shared-safe-path.js';
import { scanStringSafetyIterative } from './string-safety.js';
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

  // Stages 4 + 5 -- strict iterative JSON grammar + duplicate detection.
  const strict = strictJsonParseIterative(text);
  if (!strict.ok) return fail([strict.error]);
  const parsed = strict.value;

  // Stage 6 -- string-safety (iterative, no call-stack risk).
  const safety = scanStringSafetyIterative(parsed, rootSchema);
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
 * Convenience wrapper: encodes a JS string to UTF-8 bytes and delegates to the
 * authoritative byte parser. No independent validation path.
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
// Stages 4 + 5 -- iterative JSON tokenizer.
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

const JSON_ERR: MetadataError = { code: 'invalid-metadata-json', path: '' };

interface ObjectFrame {
  kind: 'object';
  names: Set<string>;
  expect: 'key-or-close' | 'colon' | 'value' | 'comma-or-close';
}
interface ArrayFrame {
  kind: 'array';
  expect: 'value-or-close' | 'comma-or-close';
}
type Frame = ObjectFrame | ArrayFrame;

function strictJsonParseIterative(text: string): StrictResult {
  let pos = 0;
  const stack: Frame[] = [];
  let firstDuplicate: MetadataError | null = null;
  let expectingRootValue = true;
  let rootValueSeen = false;

  const jsonErr = (): StrictErr => ({ ok: false, error: JSON_ERR });

  const skipWs = (): void => {
    while (pos < text.length) {
      const c = text.charCodeAt(pos);
      if (c === 0x20 || c === 0x09 || c === 0x0a || c === 0x0d) pos += 1;
      else return;
    }
  };

  const scanString = (): { ok: true; value: string } | { ok: false } => {
    if (text.charCodeAt(pos) !== 0x22) return { ok: false };
    pos += 1;
    let out = '';
    while (pos < text.length) {
      const c = text.charCodeAt(pos);
      if (c === 0x5c) {
        // escape
        pos += 1;
        if (pos >= text.length) return { ok: false };
        const esc = text.charCodeAt(pos);
        switch (esc) {
          case 0x22:
            out += '"';
            pos += 1;
            break;
          case 0x5c:
            out += '\\';
            pos += 1;
            break;
          case 0x2f:
            out += '/';
            pos += 1;
            break;
          case 0x62:
            out += '\b';
            pos += 1;
            break;
          case 0x66:
            out += '\f';
            pos += 1;
            break;
          case 0x6e:
            out += '\n';
            pos += 1;
            break;
          case 0x72:
            out += '\r';
            pos += 1;
            break;
          case 0x74:
            out += '\t';
            pos += 1;
            break;
          case 0x75: {
            if (pos + 4 >= text.length) return { ok: false };
            for (let i = 1; i <= 4; i += 1) {
              const h = text.charCodeAt(pos + i);
              if (!isHexDigit(h)) return { ok: false };
            }
            out += String.fromCharCode(parseInt(text.slice(pos + 1, pos + 5), 16));
            pos += 5;
            break;
          }
          default:
            return { ok: false };
        }
        continue;
      }
      if (c === 0x22) {
        pos += 1;
        return { ok: true, value: out };
      }
      if (c < 0x20) return { ok: false };
      out += text[pos];
      pos += 1;
    }
    return { ok: false };
  };

  const scanNumber = (): boolean => {
    const start = pos;
    if (text.charCodeAt(pos) === 0x2d) pos += 1;
    if (pos >= text.length) return false;
    const c = text.charCodeAt(pos);
    if (c === 0x30) pos += 1;
    else if (c >= 0x31 && c <= 0x39) {
      pos += 1;
      while (pos < text.length && text.charCodeAt(pos) >= 0x30 && text.charCodeAt(pos) <= 0x39)
        pos += 1;
    } else return false;
    if (pos < text.length && text.charCodeAt(pos) === 0x2e) {
      pos += 1;
      if (pos >= text.length || !(text.charCodeAt(pos) >= 0x30 && text.charCodeAt(pos) <= 0x39))
        return false;
      while (pos < text.length && text.charCodeAt(pos) >= 0x30 && text.charCodeAt(pos) <= 0x39)
        pos += 1;
    }
    if (pos < text.length && (text.charCodeAt(pos) === 0x65 || text.charCodeAt(pos) === 0x45)) {
      pos += 1;
      if (pos < text.length && (text.charCodeAt(pos) === 0x2b || text.charCodeAt(pos) === 0x2d))
        pos += 1;
      if (pos >= text.length || !(text.charCodeAt(pos) >= 0x30 && text.charCodeAt(pos) <= 0x39))
        return false;
      while (pos < text.length && text.charCodeAt(pos) >= 0x30 && text.charCodeAt(pos) <= 0x39)
        pos += 1;
    }
    return pos > start;
  };

  const scanLiteral = (): boolean => {
    if (text.startsWith('true', pos)) {
      pos += 4;
      return true;
    }
    if (text.startsWith('false', pos)) {
      pos += 5;
      return true;
    }
    if (text.startsWith('null', pos)) {
      pos += 4;
      return true;
    }
    return false;
  };

  const scanPrimitiveOrOpenContainer = (): 'container' | 'primitive' | 'error' => {
    skipWs();
    if (pos >= text.length) return 'error';
    const c = text.charCodeAt(pos);
    if (c === 0x7b) {
      pos += 1;
      stack.push({ kind: 'object', names: new Set(), expect: 'key-or-close' });
      return 'container';
    }
    if (c === 0x5b) {
      pos += 1;
      stack.push({ kind: 'array', expect: 'value-or-close' });
      return 'container';
    }
    if (c === 0x22) {
      const r = scanString();
      return r.ok ? 'primitive' : 'error';
    }
    if (c === 0x74 || c === 0x66 || c === 0x6e) {
      return scanLiteral() ? 'primitive' : 'error';
    }
    if (c === 0x2d || (c >= 0x30 && c <= 0x39)) {
      return scanNumber() ? 'primitive' : 'error';
    }
    return 'error';
  };

  // Main loop.
  while (true) {
    if (expectingRootValue) {
      skipWs();
      const r = scanPrimitiveOrOpenContainer();
      if (r === 'error') return jsonErr();
      expectingRootValue = false;
      if (r === 'primitive') {
        rootValueSeen = true;
      }
    }

    if (stack.length === 0) {
      // Root value complete. Verify trailing whitespace only.
      skipWs();
      if (pos !== text.length) return jsonErr();
      if (!rootValueSeen && !expectingRootValue) rootValueSeen = true;
      break;
    }

    const frame = stack[stack.length - 1]!;
    skipWs();
    if (pos >= text.length) return jsonErr();

    if (frame.kind === 'object') {
      switch (frame.expect) {
        case 'key-or-close': {
          const c = text.charCodeAt(pos);
          if (c === 0x7d) {
            pos += 1;
            stack.pop();
            afterValueOrClose(stack);
            continue;
          }
          if (c !== 0x22) return jsonErr();
          const r = scanString();
          if (!r.ok) return jsonErr();
          if (frame.names.has(r.value) && firstDuplicate === null) {
            firstDuplicate = {
              code: 'invalid-metadata-duplicate-json-property',
              path: parentPath(stack),
            };
          } else {
            frame.names.add(r.value);
          }
          frame.expect = 'colon';
          continue;
        }
        case 'colon': {
          if (text.charCodeAt(pos) !== 0x3a) return jsonErr();
          pos += 1;
          frame.expect = 'value';
          continue;
        }
        case 'value': {
          const r = scanPrimitiveOrOpenContainer();
          if (r === 'error') return jsonErr();
          if (r === 'primitive') {
            frame.expect = 'comma-or-close';
          } else {
            // container opened; new frame handles its state
          }
          continue;
        }
        case 'comma-or-close': {
          const c = text.charCodeAt(pos);
          if (c === 0x2c) {
            pos += 1;
            frame.expect = 'key-or-close';
            skipWs();
            if (pos < text.length && text.charCodeAt(pos) === 0x7d) return jsonErr(); // trailing comma
            continue;
          }
          if (c === 0x7d) {
            pos += 1;
            stack.pop();
            afterValueOrClose(stack);
            continue;
          }
          return jsonErr();
        }
      }
    } else {
      switch (frame.expect) {
        case 'value-or-close': {
          const c = text.charCodeAt(pos);
          if (c === 0x5d) {
            pos += 1;
            stack.pop();
            afterValueOrClose(stack);
            continue;
          }
          const r = scanPrimitiveOrOpenContainer();
          if (r === 'error') return jsonErr();
          if (r === 'primitive') {
            frame.expect = 'comma-or-close';
          }
          continue;
        }
        case 'comma-or-close': {
          const c = text.charCodeAt(pos);
          if (c === 0x2c) {
            pos += 1;
            frame.expect = 'value-or-close';
            skipWs();
            if (pos < text.length && text.charCodeAt(pos) === 0x5d) return jsonErr(); // trailing comma
            continue;
          }
          if (c === 0x5d) {
            pos += 1;
            stack.pop();
            afterValueOrClose(stack);
            continue;
          }
          return jsonErr();
        }
      }
    }
  }

  // Stage 4 passed. Stage 5 verdict wins if a duplicate was recorded.
  if (firstDuplicate !== null) return { ok: false, error: firstDuplicate };
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch {
    return jsonErr();
  }
}

function afterValueOrClose(stack: Frame[]): void {
  // Called when a value or container just closed. If a parent frame exists it
  // now transitions to 'comma-or-close'.
  const parent = stack[stack.length - 1];
  if (!parent) return;
  parent.expect = 'comma-or-close';
}

function parentPath(stack: readonly Frame[]): string {
  // Every level above the current inner-most object contributes an
  // <untrusted-property> or `<i>` segment. Since caller-controlled property
  // names cannot appear verbatim, we use <untrusted-property> at every object
  // depth and String(0) as a conservative array-index proxy. This is a stable
  // safe path that never leaks caller-controlled key text.
  const segments: string[] = [];
  for (let i = 0; i < stack.length - 1; i += 1) {
    const f = stack[i]!;
    if (f.kind === 'object') segments.push('<untrusted-property>');
    else segments.push('0');
  }
  return finalizePath(segments);
}

function isHexDigit(c: number): boolean {
  return (c >= 0x30 && c <= 0x39) || (c >= 0x41 && c <= 0x46) || (c >= 0x61 && c <= 0x66);
}
