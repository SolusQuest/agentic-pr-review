import {
  canonicalJsonBytes,
  CanonicalJsonByteCapError,
  CanonicalJsonInputError,
} from '../canonical-json/index.js';
import {
  deepDescriptorSnapshot,
  isCanonicalArrayIndexName,
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
  /** Descriptor-safe validated snapshot; never the caller's original object. */
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
  policy: ['schemaVersion', 'policyVersion'],
  tools: ['schemaVersion', 'toolsetVersion'],
  cacheConfig: ['schemaVersion', 'cacheConfigVersion'],
  adapter: ['schemaVersion', 'capabilityProfileVersion'],
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

  // Root domain: only plain objects without symbol keys enter validation.
  const proto = Object.getPrototypeOf(raw);
  if (proto !== Object.prototype && proto !== null) {
    return fail(PREFIX_CODES.envelopeInvalid);
  }
  // Capture every root own property exactly once (name + descriptor value).
  // The exact-key-set check, the deep snapshot, and the validated record are
  // all built from this single capture, so a stateful root Proxy cannot
  // present different own-key sets to different stages. A "__proto__" own
  // property is just another unknown field here.
  interface RootEntry {
    readonly name: string;
    readonly value: unknown;
  }
  const rootKeys = Reflect.ownKeys(raw);
  if (rootKeys.some((key) => typeof key === 'symbol')) {
    return fail(PREFIX_CODES.envelopeInvalid);
  }
  const rootEntries: RootEntry[] = [];
  const rootNames = (rootKeys as string[]).sort(compareClosedKeys);
  for (const name of rootNames) {
    const descriptor = Object.getOwnPropertyDescriptor(raw, name);
    if (descriptor === undefined) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    if ('get' in descriptor || 'set' in descriptor || !descriptor.enumerable) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    rootEntries.push({ name, value: descriptor.value });
  }
  const allowed = new Set(REQUIRED_KEYS[kind]);
  for (const entry of rootEntries) {
    if (!allowed.has(entry.name)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: entry.name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }
  for (const required of REQUIRED_KEYS[kind]) {
    if (!rootEntries.some((entry) => entry.name === required)) {
      return fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath([{ name: required }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
  }

  // Contract-owned shallow structure is validated before recursive bounds,
  // matching the C# stage-2 order. For the tools envelope this also replaces
  // the caller-owned array/wrappers with descriptor-captured internal clones,
  // so the later deep snapshot never observes those caller nodes a second
  // time while open JSON fields are still snapshotted normally.
  const rawRecord = recordFromEntries(rootEntries);
  for (const versionField of VERSION_FIELDS[kind]) {
    const value = rawRecord[versionField];
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

  let snapshotRootEntries: readonly RootEntry[] = rootEntries;
  let snapshotReplacements:
    | readonly { readonly source: object; readonly target: object }[]
    | undefined;
  if (kind === 'tools') {
    const prepared = prepareToolDefinitions(kind, rawRecord.definitions);
    if (!prepared.ok) {
      return prepared.error;
    }
    snapshotRootEntries = rootEntries.map((entry) =>
      entry.name === 'definitions' ? { name: entry.name, value: prepared.value } : entry,
    );
    snapshotReplacements = prepared.replacements;
  } else {
    const fieldError = checkKindFields(kind, rawRecord);
    if (fieldError !== null) {
      return fieldError;
    }
  }

  // Deep descriptor snapshot with inline structural bounds: the whole
  // envelope graph is copied exactly once through descriptors, and an
  // oversize graph is rejected before its size can be iterated or allocated.
  const snapshotOutcome = deepDescriptorSnapshot(
    raw,
    {
      maxDepth: MAX_JSON_DEPTH,
      maxObjectProperties: MAX_OBJECT_PROPERTIES,
      maxArrayItems: MAX_ARRAY_ITEMS,
      maxRetainedCanonicalBytes: MAX_ENVELOPE_CANONICAL_BYTES,
    },
    snapshotRootEntries,
    snapshotReplacements,
  );
  if (!snapshotOutcome.ok) {
    return fail(
      PREFIX_CODES.envelopeInvalid,
      encodePrefixPath(snapshotOutcome.violation.segments, kind, PREFIX_CODES.envelopeInvalid),
    );
  }
  const snapshot = snapshotOutcome.value as Record<string, unknown>;
  const record: Record<string, unknown> = Object.create(null);
  const retainedEntries = snapshotOutcome.retentionExceeded ? snapshotRootEntries : rootEntries;
  for (const entry of retainedEntries) {
    const value = snapshotOutcome.retentionExceeded ? entry.value : snapshot[entry.name];
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
        encodePrefixPath([{ name: entry.name }], kind, PREFIX_CODES.envelopeInvalid),
      );
    }
    Object.defineProperty(record, entry.name, { value, enumerable: true });
  }

  const identityError = checkEmbeddedIdentities(kind, record);
  if (identityError !== null) {
    return identityError;
  }

  if (snapshotOutcome.canonicalViolation !== undefined) {
    return fail(
      PREFIX_CODES.canonicalInputRejected,
      encodePrefixPath(
        snapshotOutcome.canonicalViolation.segments,
        kind,
        PREFIX_CODES.canonicalInputRejected,
      ),
    );
  }

  if (snapshotOutcome.retentionExceeded) {
    return fail(PREFIX_CODES.envelopeTooLarge);
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

function recordFromEntries(
  entries: readonly { readonly name: string; readonly value: unknown }[],
): Record<string, unknown> {
  const record: Record<string, unknown> = Object.create(null);
  for (const entry of entries) {
    Object.defineProperty(record, entry.name, { value: entry.value, enumerable: true });
  }
  return record;
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
      return null;
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

function prepareToolDefinitions(
  kind: EnvelopeKind,
  value: unknown,
):
  | {
      ok: true;
      value: readonly Record<string, unknown>[];
      replacements: readonly { readonly source: object; readonly target: object }[];
    }
  | { ok: false; error: PrefixResult<ValidatedEnvelope> } {
  const path: PrefixPathSegment[] = [{ name: 'definitions' }];
  if (!Array.isArray(value)) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  const lengthDescriptor = Object.getOwnPropertyDescriptor(value, 'length');
  if (
    lengthDescriptor === undefined ||
    lengthDescriptor.enumerable ||
    'get' in lengthDescriptor ||
    'set' in lengthDescriptor ||
    typeof lengthDescriptor.value !== 'number'
  ) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  const length = lengthDescriptor.value;
  if (length > MAX_TOOL_DEFINITIONS) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  const keys = Reflect.ownKeys(value);
  if (Object.getPrototypeOf(value) !== Array.prototype) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  if (keys.some((key) => typeof key === 'symbol')) {
    return {
      ok: false,
      error: fail(
        PREFIX_CODES.envelopeInvalid,
        encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
      ),
    };
  }
  for (const key of keys as string[]) {
    if (key !== 'length' && !isCanonicalArrayIndexName(key, length)) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(path, kind, PREFIX_CODES.envelopeInvalid),
        ),
      };
    }
  }

  const items: unknown[] = [];
  for (let index = 0; index < length; index++) {
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

  const names = new Set<string>();
  const prepared: Record<string, unknown>[] = [];
  const wrapperMemo = new WeakMap<object, Record<string, unknown>>();
  const replacements: { source: object; target: object }[] = [];
  for (let index = 0; index < items.length; index++) {
    const indexText = String(index);
    const wrapperPath: PrefixPathSegment[] = [
      { name: 'definitions' },
      { name: indexText, isIndex: true },
    ];

    const rawTool = items[index];
    if (typeof rawTool !== 'object' || rawTool === null || Array.isArray(rawTool)) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
        ),
      };
    }
    let tool = wrapperMemo.get(rawTool);
    if (tool === undefined) {
      const proto = Object.getPrototypeOf(rawTool);
      if (proto !== Object.prototype && proto !== null) {
        return {
          ok: false,
          error: fail(
            PREFIX_CODES.envelopeInvalid,
            encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
          ),
        };
      }
      const wrapperKeys = Reflect.ownKeys(rawTool);
      if (wrapperKeys.some((key) => typeof key === 'symbol')) {
        return {
          ok: false,
          error: fail(
            PREFIX_CODES.envelopeInvalid,
            encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
          ),
        };
      }
      tool = Object.create(null) as Record<string, unknown>;
      const sortedKeys = (wrapperKeys as string[]).sort(compareClosedKeys);
      for (const key of sortedKeys) {
        const descriptor = Object.getOwnPropertyDescriptor(rawTool, key);
        if (
          descriptor === undefined ||
          'get' in descriptor ||
          'set' in descriptor ||
          !descriptor.enumerable
        ) {
          return {
            ok: false,
            error: fail(
              PREFIX_CODES.envelopeInvalid,
              encodePrefixPath([...wrapperPath, { name: key }], kind, PREFIX_CODES.envelopeInvalid),
            ),
          };
        }
        if (
          key !== 'description' &&
          key !== 'inputSchema' &&
          key !== 'name' &&
          key !== 'policyMetadata'
        ) {
          return {
            ok: false,
            error: fail(
              PREFIX_CODES.envelopeInvalid,
              encodePrefixPath([...wrapperPath, { name: key }], kind, PREFIX_CODES.envelopeInvalid),
            ),
          };
        }
        Object.defineProperty(tool, key, { value: descriptor.value, enumerable: true });
      }
      wrapperMemo.set(rawTool, tool);
      replacements.push({ source: rawTool, target: tool });
    }
    if (!('name' in tool) || !('description' in tool) || !('inputSchema' in tool)) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(wrapperPath, kind, PREFIX_CODES.envelopeInvalid),
        ),
      };
    }

    const name = tool.name;
    if (typeof name !== 'string') {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([...wrapperPath, { name: 'name' }], kind, PREFIX_CODES.envelopeInvalid),
        ),
      };
    }
    if (names.has(name)) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath([...wrapperPath, { name: 'name' }], kind, PREFIX_CODES.envelopeInvalid),
        ),
      };
    }
    names.add(name);

    if (typeof tool.description !== 'string') {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(
            [...wrapperPath, { name: 'description' }],
            kind,
            PREFIX_CODES.envelopeInvalid,
          ),
        ),
      };
    }
    if (
      typeof tool.inputSchema !== 'object' ||
      tool.inputSchema === null ||
      Array.isArray(tool.inputSchema)
    ) {
      return {
        ok: false,
        error: fail(
          PREFIX_CODES.envelopeInvalid,
          encodePrefixPath(
            [...wrapperPath, { name: 'inputSchema' }],
            kind,
            PREFIX_CODES.envelopeInvalid,
          ),
        ),
      };
    }
    prepared.push(tool);
  }

  replacements.unshift({ source: value, target: prepared });
  return { ok: true, value: prepared, replacements };
}

/** Invalid UTF-16 closed keys share the public sentinel sort position in both languages. */
function compareClosedKeys(left: string, right: string): number {
  const leftKey = hasUnpairedSurrogate(left) ? '\ud800' : left;
  const rightKey = hasUnpairedSurrogate(right) ? '\ud800' : right;
  return leftKey < rightKey ? -1 : leftKey > rightKey ? 1 : 0;
}

function hasUnpairedSurrogate(value: string): boolean {
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    if (code >= 0xd800 && code <= 0xdbff) {
      if (index + 1 >= value.length) return true;
      const low = value.charCodeAt(index + 1);
      if (low < 0xdc00 || low > 0xdfff) return true;
      index++;
    } else if (code >= 0xdc00 && code <= 0xdfff) {
      return true;
    }
  }
  return false;
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
