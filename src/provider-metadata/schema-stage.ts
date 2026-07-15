/**
 * Stage 7 -- JSON Schema (Ajv) with the ordered-exception workstream mapping
 * defined by issue #51.
 *
 * Ordered exceptions (first match wins per Ajv error):
 *   1. `additionalProperties` -> `invalid-metadata-additional-property`
 *   2. `enum` (any position)  -> `invalid-metadata-unknown-enum`
 *   3. `maximum` on a token field
 *      (attempt / request / aggregate token slot)
 *                              -> `invalid-metadata-token-out-of-range`
 *   4. any remaining failure  -> `invalid-metadata-schema`
 *
 * The mapping is per-Ajv-error; a document that produces both an `enum`
 * violation at path A and a `maxLength` violation at path B yields BOTH
 * mapped errors, and they are sorted deterministically by the shared
 * `finalizeErrors` post-processor.
 *
 * The safe path for `additionalProperties` uses the shared sanitizer/resolver
 * markers `<empty-name>`, `<invalid-control>`, `<untrusted-property>`. Metadata
 * schema has no `oneOf` variant-forbidden schema-known fields, so the
 * schema-known fallback is never taken for an additional-property rejection.
 */

import { Ajv, type ErrorObject } from 'ajv';
import { containsOtherControlChar, rfc6901Escape } from './safe-path-helpers.js';
import type { MetadataError, MetadataErrorCode } from './types.js';
import type { SchemaNode } from '../state-v2/shared-safe-path.js';

const TOKEN_FIELD_REGEX =
  /\/(totalInputTokens|uncachedInputTokens|cacheWriteInputTokens|cacheReadInputTokens|outputTokens)$/;

const ajv = new Ajv({ strict: true, allErrors: true, verbose: true });

let compiledValidator: ((data: unknown) => boolean) | null = null;

function ensureCompiled(schema: SchemaNode): (data: unknown) => boolean {
  if (compiledValidator) return compiledValidator;
  const validate = ajv.compile(schema);
  compiledValidator = validate as unknown as (data: unknown) => boolean;
  return compiledValidator;
}

export function runSchemaStage(parsed: unknown, schema: SchemaNode): MetadataError[] {
  const validate = ensureCompiled(schema);
  const validator = validate as unknown as ((data: unknown) => boolean) & {
    errors?: ErrorObject[] | null;
  };
  const ok = validator(parsed);
  if (ok) return [];
  const errors = validator.errors ?? [];
  const mapped: MetadataError[] = [];
  for (const e of errors) {
    mapped.push(mapAjvError(e));
  }
  return mapped;
}

function mapAjvError(err: ErrorObject): MetadataError {
  const keyword = err.keyword;
  const location = err.instancePath ?? '';

  // 1. additionalProperties
  if (keyword === 'additionalProperties') {
    const key = (err.params as { additionalProperty?: string }).additionalProperty ?? '';
    const marker = additionalPropertyMarker(key);
    const suffix = location === '' ? '' : location;
    return { code: 'invalid-metadata-additional-property', path: `${suffix}/${marker}` };
  }

  // 2. enum (anywhere)
  if (keyword === 'enum') {
    return { code: 'invalid-metadata-unknown-enum', path: location };
  }

  // 3. token-field maximum
  if (keyword === 'maximum' && TOKEN_FIELD_REGEX.test(location)) {
    return { code: 'invalid-metadata-token-out-of-range', path: location };
  }

  // 4. fallthrough
  return { code: 'invalid-metadata-schema' as MetadataErrorCode, path: location };
}

function additionalPropertyMarker(rawKey: string): string {
  if (rawKey === '') return '<empty-name>';
  if (containsOtherControlChar(rawKey)) return '<invalid-control>';
  return '<untrusted-property>';
}

export { rfc6901Escape };
