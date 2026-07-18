import {
  canonicalJsonBytes,
  CanonicalJsonByteCapError,
  CanonicalJsonInputError,
} from '../canonical-json/index.js';
import {
  deepDescriptorSnapshot,
  isCanonicalViolationMarker,
  canonicalViolationReason,
} from './deep-snapshot.js';
import { isValidIdentity } from './identity.js';
import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';
import {
  encodePrefixPath,
  scanCanonicalDomainAndBounds,
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

  // Root domain: only plain objects without symbol keys enter the snapshot.
  const proto = Object.getPrototypeOf(raw);
  if (proto !== Object.prototype && proto !== null) {
    return fail(PREFIX_CODES.envelopeInvalid);
  }
  if (Object.getOwnPropertySymbols(raw).length > 0) {
    return fail(PREFIX_CODES.envelopeInvalid);
  }

  // Exact key set is validated against the raw own-property names BEFORE any
  // copying, so a "__proto__" own property can never slip past as a prototype
  // mutation instead of an unknown-field rejection.
  const rawNames = Object.getOwnPropertyNames(raw);
  const allowed = new Set(REQUIRED_KEYS[kind]);
  for (const key of rawNames) {
    if (!allowed.has(key)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: key }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }
  for (const required of REQUIRED_KEYS[kind]) {
    if (!rawNames.includes(required)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: required }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }

  // Deep descriptor snapshot: the whole envelope graph is copied exactly
  // once through descriptors. Accessor or non-enumerable own properties at
  // the contract-owned root are structural rejections; nested anomalies are
  // preserved as violation markers for the canonical-domain stage.
  const snapshot = deepDescriptorSnapshot(raw) as Record<string, unknown>;
  const record: Record<string, unknown> = Object.create(null);
  for (const name of rawNames) {
    const value = snapshot[name];
    if (
      isCanonicalViolationMarker(value) &&
      (canonicalViolationReason(value) === 'accessor-property' ||
        canonicalViolationReason(value) === 'non-enumerable-property')
    ) {
      // Only a root-level accessor / non-enumerable own property is a
      // structural rejection; every other anomaly belongs to the
      // canonical-domain stage.
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    Object.defineProperty(record, name, { value, enumerable: true });
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

  const boundsError = checkStructuralBounds(kind, record);
  if (boundsError !== null) {
    return boundsError;
  }

  const identityError = checkEmbeddedIdentities(kind, record);
  if (identityError !== null) {
    return identityError;
  }

  const violation = scanCanonicalDomainAndBounds(record);
  if (violation !== null) {
    return fail(
      PREFIX_CODES.canonicalInputRejected,
      encodePrefixPath(violation.segments, kind, PREFIX_CODES.canonicalInputRejected),
    );
  }

  // Bounded-counting canonicalization: the shared helper aborts as soon as
  // the canonical output would exceed the cap; no full copy is allocated.
  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(record, MAX_ENVELOPE_CANONICAL_BYTES);
  } catch (error) {
    if (error instanceof CanonicalJsonByteCapError) {
      return fail(PREFIX_CODES.envelopeTooLarge);
    }
    if (error instanceof CanonicalJsonInputError) {
      // Unreachable after the structured pre-scan; defensive mapping.
      return fail(PREFIX_CODES.canonicalInputRejected);
    }
    throw error;
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

/** Snapshot an array's index values via descriptors (never invoking getters). */
function snapshotArrayIndices(
  kind: EnvelopeKind,
  path: PrefixPathSegment[],
  value: unknown,
): { ok: true; items: unknown[] } | { ok: false; error: PrefixResult<ValidatedEnvelope> } {
  if (!Array.isArray(value)) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  const items: unknown[] = [];
  for (let index = 0; index < value.length; index++) {
    const descriptor = Object.getOwnPropertyDescriptor(value, String(index));
    if (descriptor === undefined) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(
            [...path, { name: String(index), isIndex: true }],
            kind,
            PREFIX_CODES.envelopeInvalid,
          ),
        ),
      };
    }
    if ('get' in descriptor || 'set' in descriptor || !descriptor.enumerable) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(
            [...path, { name: String(index), isIndex: true }],
            kind,
            PREFIX_CODES.envelopeInvalid,
          ),
        ),
      };
    }
    items.push(descriptor.value);
  }
  return { ok: true, items };
}

/** Snapshot an object's own data properties via descriptors (never invoking getters). */
function snapshotObjectData(value: unknown): Record<string, unknown> | null {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return null;
  }
  const proto = Object.getPrototypeOf(value);
  if (proto !== Object.prototype && proto !== null) {
    return null;
  }
  if (Object.getOwnPropertySymbols(value).length > 0) {
    return null;
  }
  const record: Record<string, unknown> = Object.create(null);
  for (const name of Object.getOwnPropertyNames(value)) {
    const descriptor = Object.getOwnPropertyDescriptor(value, name)!;
    if ('get' in descriptor || 'set' in descriptor || !descriptor.enumerable) {
      return null;
    }
    Object.defineProperty(record, name, { value: descriptor.value, enumerable: true });
  }
  return record;
}

function checkToolDefinitions(
  kind: EnvelopeKind,
  definitions: unknown,
): PrefixResult<ValidatedEnvelope> | null {
  const arrayPath: PrefixPathSegment[] = [{ name: 'definitions' }];
  const snapshot = snapshotArrayIndices(kind, arrayPath, definitions);
  if (!snapshot.ok) {
    return snapshot.error;
  }

  const items = snapshot.items;
  if (items.length > MAX_TOOL_DEFINITIONS) {
    return fail(
      PREFIX_CODES.envelopeInvalid,
      encodePrefixPath(arrayPath, kind, PREFIX_CODES.envelopeInvalid),
    );
  }

  const names = new Set<string>();
  for (let index = 0; index < items.length; index++) {
    const indexText = String(index);
    const wrapperPath: PrefixPathSegment[] = [
      { name: 'definitions' },
      { name: indexText, isIndex: true },
    ];

    const tool = snapshotObjectData(items[index]);
    if (tool === null) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
      );
    }

    for (const key of Object.keys(tool)) {
      if (
        key !== 'description' &&
        key !== 'inputSchema' &&
        key !== 'name' &&
        key !== 'policyMetadata'
      ) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([...wrapperPath, { name: key }], kind, PREFIX_CODES.envelopeInvalid),
        );
      }
    }
    if (!('name' in tool) || !('description' in tool) || !('inputSchema' in tool)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
      );
    }

    const name = tool.name;
    if (typeof name !== 'string') {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([...wrapperPath, { name: 'name' }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    if (names.has(name)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([...wrapperPath, { name: 'name' }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    names.add(name);

    if (typeof tool.description !== 'string') {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [...wrapperPath, { name: 'description' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
    if (
      typeof tool.inputSchema !== 'object' ||
      tool.inputSchema === null ||
      Array.isArray(tool.inputSchema)
    ) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(
          [...wrapperPath, { name: 'inputSchema' }],
          kind,
          PREFIX_CODES.envelopeInvalid,
        ),
      );
    }
  }

  return null;
}

/** Structural bounds (structure stage): depth / object properties / array items. */
function checkStructuralBounds(
  kind: EnvelopeKind,
  root: Record<string, unknown>,
): PrefixResult<ValidatedEnvelope> | null {
  interface Frame {
    readonly value: unknown;
    readonly depth: number;
    readonly segments: readonly PrefixPathSegment[];
  }
  const stack: Frame[] = [{ value: root, depth: 0, segments: [] }];
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
        const descriptor = Object.getOwnPropertyDescriptor(value, String(i));
        if (
          descriptor === undefined ||
          'get' in descriptor ||
          'set' in descriptor ||
          !descriptor.enumerable
        ) {
          // Accessor holes are owned by the canonical-domain stage.
          continue;
        }
        stack.push({
          value: descriptor.value,
          depth: depth + 1,
          segments: [...segments, { name: String(i), isIndex: true }],
        });
      }
      continue;
    }
    if (typeof value === 'object' && value !== null) {
      if (depth > MAX_JSON_DEPTH) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      const names = Object.getOwnPropertyNames(value);
      if (names.length > MAX_OBJECT_PROPERTIES) {
        return fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(segments, kind, PREFIX_CODES.envelopeInvalid),
        );
      }
      for (const name of names) {
        const descriptor = Object.getOwnPropertyDescriptor(value, name)!;
        if ('get' in descriptor || 'set' in descriptor || !descriptor.enumerable) {
          continue;
        }
        stack.push({
          value: descriptor.value,
          depth: depth + 1,
          segments: [...segments, { name }],
        });
      }
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
