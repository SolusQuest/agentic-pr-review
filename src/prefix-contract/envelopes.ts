import { canonicalJsonBytes, CanonicalJsonInputError } from '../canonical-json/index.js';
import { isValidIdentity } from './identity.js';
import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';

/**
 * Closed cache-contract envelope validation and canonicalization (issue #50,
 * D4). Envelope field sets are owned by the design contract's Prefix
 * Contract section; this module owns the TypeScript field-domain validation.
 */

export const MAX_ENVELOPE_CANONICAL_BYTES = 262_144;
export const MAX_TOOL_DEFINITIONS = 64;
const MAX_JSON_DEPTH = 64;
const MAX_OBJECT_PROPERTIES = 256;
const MAX_ARRAY_ITEMS = 1_024;

export interface ValidatedEnvelope {
  /** The original (validated) envelope value. */
  readonly value: unknown;
  /** RFC 8785 canonical UTF-8 bytes of the whole envelope. */
  readonly canonicalBytes: Uint8Array;
}

type EnvelopeKind = 'template' | 'policy' | 'tools' | 'cacheConfig' | 'adapter';

const REQUIRED_KEYS: Record<EnvelopeKind, readonly string[]> = {
  template: ['definition', 'schemaVersion', 'templateVersion'],
  policy: ['constraints', 'instructions', 'policyVersion', 'schemaVersion'],
  tools: ['definitions', 'schemaVersion', 'toolsetVersion'],
  cacheConfig: ['cacheConfigVersion', 'eligibility', 'markerPolicy', 'schemaVersion', 'statelessMode'],
  adapter: ['adapterBuildVersion', 'capabilityProfileVersion', 'schemaVersion'],
};

const VERSION_FIELDS: Record<EnvelopeKind, readonly string[]> = {
  template: ['schemaVersion', 'templateVersion'],
  policy: ['policyVersion', 'schemaVersion'],
  tools: ['schemaVersion', 'toolsetVersion'],
  cacheConfig: ['cacheConfigVersion', 'schemaVersion'],
  adapter: ['capabilityProfileVersion', 'schemaVersion'],
};

export function validateTemplateEnvelope(raw: unknown): PrefixResult<ValidatedEnvelope> {
  return validateEnvelope('template', raw);
}

export function validatePolicyEnvelope(raw: unknown): PrefixResult<ValidatedEnvelope> {
  return validateEnvelope('policy', raw);
}

export function validateToolsEnvelope(raw: unknown): PrefixResult<ValidatedEnvelope> {
  return validateEnvelope('tools', raw);
}

export function validateCacheConfigEnvelope(raw: unknown): PrefixResult<ValidatedEnvelope> {
  return validateEnvelope('cacheConfig', raw);
}

export function validateAdapterEnvelope(raw: unknown): PrefixResult<ValidatedEnvelope> {
  return validateEnvelope('adapter', raw);
}

function validateEnvelope(kind: EnvelopeKind, raw: unknown): PrefixResult<ValidatedEnvelope> {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return fail(PREFIX_CODES.envelopeInvalid, '');
  }

  const record = raw as Record<string, unknown>;
  const allowed = new Set(REQUIRED_KEYS[kind]);
  for (const key of Object.keys(record)) {
    if (!allowed.has(key)) {
      return fail(PREFIX_CODES.envelopeInvalid, '/' + key);
    }
  }
  for (const required of REQUIRED_KEYS[kind]) {
    if (!(required in record)) {
      return fail(PREFIX_CODES.envelopeInvalid, '/' + required);
    }
  }

  for (const versionField of VERSION_FIELDS[kind]) {
    const value = record[versionField];
    if (
      typeof value !== 'number' ||
      !Number.isInteger(value) ||
      value < 1 ||
      value > 2_147_483_647
    ) {
      return fail(PREFIX_CODES.envelopeInvalid, '/' + versionField);
    }
  }

  const fieldError = checkKindFields(kind, record);
  if (fieldError !== null) {
    return fieldError;
  }

  const boundsError = checkStructuralBounds(record);
  if (boundsError !== null) {
    return boundsError;
  }

  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(record);
  } catch (error) {
    if (error instanceof CanonicalJsonInputError) {
      return fail(PREFIX_CODES.canonicalInputRejected, error.path);
    }
    throw error;
  }

  if (canonical.byteLength > MAX_ENVELOPE_CANONICAL_BYTES) {
    return fail(PREFIX_CODES.envelopeTooLarge);
  }

  return ok({ value: record, canonicalBytes: canonical });
}

function checkKindFields(
  kind: EnvelopeKind,
  record: Record<string, unknown>,
): PrefixResult<ValidatedEnvelope> | null {
  switch (kind) {
    case 'policy':
      return typeof record.instructions === 'string'
        ? null
        : fail(PREFIX_CODES.envelopeInvalid, '/instructions');
    case 'tools':
      return checkToolDefinitions(record.definitions);
    case 'cacheConfig': {
      if (typeof record.markerPolicy !== 'string') {
        return fail(PREFIX_CODES.envelopeInvalid, '/markerPolicy');
      }
      if (typeof record.eligibility !== 'string') {
        return fail(PREFIX_CODES.envelopeInvalid, '/eligibility');
      }
      return typeof record.statelessMode === 'boolean'
        ? null
        : fail(PREFIX_CODES.envelopeInvalid, '/statelessMode');
    }
    case 'adapter': {
      const buildVersion = record.adapterBuildVersion;
      if (typeof buildVersion !== 'string') {
        return fail(PREFIX_CODES.envelopeInvalid, '/adapterBuildVersion');
      }
      return isValidIdentity(buildVersion)
        ? null
        : fail(PREFIX_CODES.identityInvalid, '/adapterBuildVersion');
    }
    case 'template':
      return null;
  }
}

function checkToolDefinitions(definitions: unknown): PrefixResult<ValidatedEnvelope> | null {
  if (!Array.isArray(definitions)) {
    return fail(PREFIX_CODES.envelopeInvalid, '/definitions');
  }
  if (definitions.length > MAX_TOOL_DEFINITIONS) {
    return fail(PREFIX_CODES.envelopeInvalid, '/definitions');
  }

  const names = new Set<string>();
  for (let index = 0; index < definitions.length; index++) {
    const path = '/definitions/' + String(index);
    const tool = definitions[index] as unknown;
    if (typeof tool !== 'object' || tool === null || Array.isArray(tool)) {
      return fail(PREFIX_CODES.envelopeInvalid, path);
    }

    const record = tool as Record<string, unknown>;
    for (const key of Object.keys(record)) {
      if (key !== 'description' && key !== 'inputSchema' && key !== 'name' && key !== 'policyMetadata') {
        return fail(PREFIX_CODES.envelopeInvalid, path + '/' + key);
      }
    }
    if (!('name' in record) || !('description' in record) || !('inputSchema' in record)) {
      return fail(PREFIX_CODES.envelopeInvalid, path);
    }

    const name = record.name;
    if (typeof name !== 'string') {
      return fail(PREFIX_CODES.envelopeInvalid, path + '/name');
    }
    if (!isValidIdentity(name)) {
      return fail(PREFIX_CODES.identityInvalid, path + '/name');
    }
    if (names.has(name)) {
      return fail(PREFIX_CODES.envelopeInvalid, path + '/name');
    }
    names.add(name);

    if (typeof record.description !== 'string') {
      return fail(PREFIX_CODES.envelopeInvalid, path + '/description');
    }
    if (typeof record.inputSchema !== 'object' || record.inputSchema === null || Array.isArray(record.inputSchema)) {
      return fail(PREFIX_CODES.envelopeInvalid, path + '/inputSchema');
    }
  }

  return null;
}

/** Structural bounds over the whole envelope value tree (D11). */
function checkStructuralBounds(root: Record<string, unknown>): PrefixResult<ValidatedEnvelope> | null {
  interface Frame {
    readonly value: unknown;
    readonly depth: number;
    readonly path: string;
  }
  const stack: Frame[] = [{ value: root, depth: 1, path: '' }];
  while (stack.length > 0) {
    const { value, depth, path } = stack.pop()!;
    if (Array.isArray(value)) {
      if (depth > MAX_JSON_DEPTH) {
        return fail(PREFIX_CODES.envelopeInvalid, path);
      }
      if (value.length > MAX_ARRAY_ITEMS) {
        return fail(PREFIX_CODES.envelopeInvalid, path);
      }
      for (let i = 0; i < value.length; i++) {
        stack.push({ value: value[i], depth: depth + 1, path: path + '/' + String(i) });
      }
    } else if (typeof value === 'object' && value !== null) {
      if (depth > MAX_JSON_DEPTH) {
        return fail(PREFIX_CODES.envelopeInvalid, path);
      }
      const entries = Object.entries(value as Record<string, unknown>);
      if (entries.length > MAX_OBJECT_PROPERTIES) {
        return fail(PREFIX_CODES.envelopeInvalid, path);
      }
      for (const [key, child] of entries) {
        stack.push({ value: child, depth: depth + 1, path: path + '/' + key });
      }
    }
  }
  return null;
}
