import { canonicalJsonBytes, CanonicalJsonInputError } from '../canonical-json/index.js';
import { isValidIdentity } from './identity.js';
import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';
import {
  encodePrefixPath,
  findCanonicalDomainViolation,
  type EnvelopeKind,
  type PrefixPathSegment,
} from './safe-path.js';

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

const REQUIRED_KEYS: Record<EnvelopeKind, readonly string[]> = {
  template: ['definition', 'schemaVersion', 'templateVersion'],
  policy: ['constraints', 'instructions', 'policyVersion', 'schemaVersion'],
  tools: ['definitions', 'schemaVersion', 'toolsetVersion'],
  cacheConfig: [
    'cacheConfigVersion',
    'eligibility',
    'markerPolicy',
    'schemaVersion',
    'statelessMode',
  ],
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
  // The outer guard converts any reflection/Proxy/getter failure into a typed
  // failure; no exception crosses the public boundary.
  try {
    return validateEnvelopeCore(kind, raw);
  } catch {
    return fail(PREFIX_CODES.envelopeInvalid);
  }
}

function validateEnvelopeCore(kind: EnvelopeKind, raw: unknown): PrefixResult<ValidatedEnvelope> {
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) {
    return fail(PREFIX_CODES.envelopeInvalid);
  }

  // Snapshot data properties via descriptors so accessor getters are never
  // invoked; accessor or non-enumerable own properties are rejected.
  const record: Record<string, unknown> = {};
  for (const name of Object.getOwnPropertyNames(raw)) {
    const descriptor = Object.getOwnPropertyDescriptor(raw, name)!;
    if ('get' in descriptor || 'set' in descriptor) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    if (!descriptor.enumerable) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    record[name] = descriptor.value;
  }

  const allowed = new Set(REQUIRED_KEYS[kind]);
  for (const key of Object.keys(record)) {
    if (!allowed.has(key)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: key }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }
  for (const required of REQUIRED_KEYS[kind]) {
    if (!(required in record)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: required }], kind, PREFIX_CODES.envelopeInvalid),
      );
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
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: versionField }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }

  const fieldError = checkKindFields(kind, record);
  if (fieldError !== null) {
    return fieldError;
  }

  const identityError = checkEmbeddedIdentities(kind, record);
  if (identityError !== null) {
    return identityError;
  }

  const boundsError = checkStructuralBounds(kind, record);
  if (boundsError !== null) {
    return boundsError;
  }

  const violation = findCanonicalDomainViolation(record);
  if (violation !== null) {
    return fail(
      PREFIX_CODES.canonicalInputRejected,
      encodePrefixPath(violation.segments, kind, PREFIX_CODES.canonicalInputRejected),
    );
  }

  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(record);
  } catch (error) {
    if (error instanceof CanonicalJsonInputError) {
      // Unreachable after the structured pre-scan; defensive mapping.
      return fail(PREFIX_CODES.canonicalInputRejected);
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
        : fail(
            PREFIX_CODES.envelopeInvalid,
            encodePrefixPath([{ name: 'instructions' }], kind, PREFIX_CODES.envelopeInvalid),
          );
    case 'tools':
      return checkToolDefinitions(kind, record.definitions);
    case 'cacheConfig': {
      if (typeof record.markerPolicy !== 'string') {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([{ name: 'markerPolicy' }], kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      if (typeof record.eligibility !== 'string') {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([{ name: 'eligibility' }], kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      return typeof record.statelessMode === 'boolean'
        ? null
        : fail(
            PREFIX_CODES.envelopeInvalid,
            encodePrefixPath([{ name: 'statelessMode' }], kind, PREFIX_CODES.envelopeInvalid),
          );
    }
    case 'adapter': {
      const buildVersion = record.adapterBuildVersion;
      if (typeof buildVersion !== 'string') {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([{ name: 'adapterBuildVersion' }], kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      return null;
    }
    case 'template':
      return null;
  }
}

function checkToolDefinitions(
  kind: EnvelopeKind,
  definitions: unknown,
): PrefixResult<ValidatedEnvelope> | null {
  if (!Array.isArray(definitions)) {
    return fail(
      PREFIX_CODES.envelopeInvalid,
      encodePrefixPath([{ name: 'definitions' }], kind, PREFIX_CODES.envelopeInvalid),
    );
  }
  if (definitions.length > MAX_TOOL_DEFINITIONS) {
    return fail(
      PREFIX_CODES.envelopeInvalid,
      encodePrefixPath([{ name: 'definitions' }], kind, PREFIX_CODES.envelopeInvalid),
    );
  }

  const names = new Set<string>();
  for (let index = 0; index < definitions.length; index++) {
    const indexText = String(index);
    const tool = definitions[index] as unknown;
    if (typeof tool !== 'object' || tool === null || Array.isArray(tool)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }

    const record = tool as Record<string, unknown>;
    for (const key of Object.keys(record)) {
      if (
        key !== 'description' &&
        key !== 'inputSchema' &&
        key !== 'name' &&
        key !== 'policyMetadata'
      ) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(
            [{ name: 'definitions' }, { name: indexText, isIndex: true }, { name: key }],
            kind,
            PREFIX_CODES.envelopeInvalid,
          ),
        );
      }
    }
    if (!('name' in record) || !('description' in record) || !('inputSchema' in record)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }

    const name = record.name;
    if (typeof name !== 'string') {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }, { name: 'name' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
    if (names.has(name)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }, { name: 'name' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
    names.add(name);

    if (typeof record.description !== 'string') {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }, { name: 'description' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
    if (
      typeof record.inputSchema !== 'object' ||
      record.inputSchema === null ||
      Array.isArray(record.inputSchema)
    ) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [{ name: 'definitions' }, { name: indexText, isIndex: true }, { name: 'inputSchema' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
  }

  return null;
}

/** Stage: embedded identity semantics (tool names, adapterBuildVersion). */
function checkEmbeddedIdentities(
  kind: EnvelopeKind,
  record: Record<string, unknown>,
): PrefixResult<ValidatedEnvelope> | null {
  if (kind === 'tools') {
    const definitions = record.definitions;
    if (!Array.isArray(definitions)) {
      return null;
    }
    for (let index = 0; index < definitions.length; index++) {
      const tool = definitions[index] as Record<string, unknown>;
      if (typeof tool?.name === 'string' && !isValidIdentity(tool.name)) {
        return fail(
          PREFIX_CODES.identityInvalid,
          encodePrefixPath(
            [{ name: 'definitions' }, { name: String(index), isIndex: true }, { name: 'name' }],
            kind,
            PREFIX_CODES.identityInvalid,
          ),
        );
      }
    }
    return null;
  }
  if (kind === 'adapter') {
    const buildVersion = record.adapterBuildVersion;
    if (typeof buildVersion === 'string' && !isValidIdentity(buildVersion)) {
      return fail(
        PREFIX_CODES.identityInvalid,
        encodePrefixPath([{ name: 'adapterBuildVersion' }], kind, PREFIX_CODES.identityInvalid),
      );
    }
  }
  return null;
}

/** Structural bounds over the whole envelope value tree (D11). Depth counts an envelope field's root value as 1. */
function checkStructuralBounds(
  kind: EnvelopeKind,
  root: Record<string, unknown>,
): PrefixResult<ValidatedEnvelope> | null {
  interface Frame {
    readonly value: unknown;
    readonly depth: number;
    readonly segments: readonly PrefixPathSegment[];
  }
  const stack: Frame[] = [{ value: root, depth: 0, segments: [] as PrefixPathSegment[] }];
  while (stack.length > 0) {
    const { value, depth, segments } = stack.pop()!;
    if (Array.isArray(value)) {
      if (depth > MAX_JSON_DEPTH) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      if (value.length > MAX_ARRAY_ITEMS) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      for (let i = 0; i < value.length; i++) {
        stack.push({
          value: value[i],
          depth: depth + 1,
          segments: [...segments, { name: String(i), isIndex: true }],
        });
      }
    } else if (typeof value === 'object' && value !== null) {
      if (depth > MAX_JSON_DEPTH) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      const entries = Object.entries(value as Record<string, unknown>);
      if (entries.length > MAX_OBJECT_PROPERTIES) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      for (const [key, child] of entries) {
        stack.push({ value: child, depth: depth + 1, segments: [...segments, { name: key }] });
      }
    }
  }
  return null;
}
