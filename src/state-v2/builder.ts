import { createHash } from 'node:crypto';
import { canonicalJsonBytes, type CanonicalJsonValue } from '../canonical-json/index.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';
import type { Sha256Hex, StateManifestV2, StateManifestV2Input } from './manifest.js';
import { validateStateManifestV2 } from './schema.js';
import { serializeStateManifestV2 } from './serializer.js';

export class LedgerOverBoundError extends Error {
  readonly bytes: number;
  constructor(bytes: number) {
    super(`ledger bytes ${bytes} exceed LEDGER_MAX_BYTES=${LEDGER_MAX_BYTES}`);
    this.name = 'LedgerOverBoundError';
    this.bytes = bytes;
  }
}

export class MetadataOverBoundError extends Error {
  readonly bytes: number;
  constructor(bytes: number) {
    super(`provider-run-metadata bytes ${bytes} exceed METADATA_MAX_BYTES=${METADATA_MAX_BYTES}`);
    this.name = 'MetadataOverBoundError';
    this.bytes = bytes;
  }
}

export class BuilderValidationError extends Error {
  readonly detail: string;
  constructor(detail: string) {
    super(`state manifest v2 builder rejected the finalized manifest: ${detail}`);
    this.name = 'BuilderValidationError';
    this.detail = detail;
  }
}

export class BuilderInputRejectedError extends Error {
  readonly path: string;
  constructor(reason: string, path: string) {
    super(`state manifest v2 builder rejected input at ${path}: ${reason}`);
    this.name = 'BuilderInputRejectedError';
    this.path = path;
  }
}

export interface BuildResult {
  manifest: StateManifestV2;
  manifestBytes: Uint8Array;
  ledgerBytes: Uint8Array;
  providerRunMetadataBytes: Uint8Array;
}

/** Alias kept for the AC-visible name. */
export type BuildStateBundleV2Result = BuildResult;

/**
 * Pure builder. Never touches the filesystem.
 *
 * Order of operations:
 *   1. Size caps on the caller's byte views (byteLength on original view).
 *   2. Reject non-plain input structures (getters, symbol keys,
 *      non-enumerable own properties, custom prototypes, etc.) by traversing
 *      the input under the same rules as `canonicalJsonBytes`; produce a
 *      typed error immediately.
 *   3. Deep-copy the accepted input object using own enumerable data
 *      properties only, so mutation of the caller's input after return
 *      cannot mutate any BuildResult byte.
 *   4. Copy the accepted byte views into fresh Uint8Array snapshots.
 *   5. Compute SHA-256 over the snapshots and fill descriptor + transaction
 *      binding fields.
 *   6. Validate the finalized manifest and serialize it.
 */
export function buildStateBundleV2(
  input: StateManifestV2Input,
  ledgerBytes: Uint8Array,
  providerRunMetadataBytes: Uint8Array,
): BuildResult {
  if (ledgerBytes.byteLength > LEDGER_MAX_BYTES) {
    throw new LedgerOverBoundError(ledgerBytes.byteLength);
  }
  if (providerRunMetadataBytes.byteLength > METADATA_MAX_BYTES) {
    throw new MetadataOverBoundError(providerRunMetadataBytes.byteLength);
  }

  const clonedInput = canonicalDeepClone(input as unknown, '$') as StateManifestV2Input;
  const ledgerSnapshot = copyBytes(ledgerBytes);
  const metadataSnapshot = copyBytes(providerRunMetadataBytes);

  const ledgerSha = sha256Hex(ledgerSnapshot);
  const metadataSha = sha256Hex(metadataSnapshot);

  const manifest: StateManifestV2 = {
    version: clonedInput.version,
    stateNamespace: clonedInput.stateNamespace,
    stateKey: clonedInput.stateKey,
    sessionEpoch: clonedInput.sessionEpoch,
    cacheContractIdentity: clonedInput.cacheContractIdentity,
    generation: clonedInput.generation,
    transition: clonedInput.transition,
    provenance: clonedInput.provenance,
    transaction: {
      ...clonedInput.transaction,
      candidateLedgerSha256: ledgerSha,
    },
    ledger: {
      path: clonedInput.ledger.path,
      sha256: ledgerSha,
      bytes: ledgerSnapshot.byteLength,
      schemaVersion: clonedInput.ledger.schemaVersion,
    },
    providerRunMetadata: {
      path: clonedInput.providerRunMetadata.path,
      sha256: metadataSha,
      bytes: metadataSnapshot.byteLength,
      schemaVersion: clonedInput.providerRunMetadata.schemaVersion,
      producingGeneration: clonedInput.providerRunMetadata.producingGeneration,
    },
  };

  const validation = validateStateManifestV2(manifest);
  if (!validation.ok) {
    throw new BuilderValidationError(validation.message);
  }
  const manifestBytes = serializeStateManifestV2(validation.manifest);
  return {
    manifest: validation.manifest,
    manifestBytes,
    ledgerBytes: ledgerSnapshot,
    providerRunMetadataBytes: metadataSnapshot,
  };
}

function copyBytes(source: Uint8Array): Uint8Array {
  const copy = new Uint8Array(source.byteLength);
  copy.set(source);
  return copy;
}

function sha256Hex(bytes: Uint8Array): Sha256Hex {
  return createHash('sha256').update(bytes).digest('hex') as Sha256Hex;
}

/**
 * Deep-clone using the canonical-JSON accepted domain. This is the same
 * accepted-domain contract as `canonicalJsonBytes` — it rejects getters,
 * symbol-keyed properties, non-enumerable own properties, non-plain
 * prototypes, sparse arrays, and non-JSON primitives. Every accepted value
 * is copied into a brand-new plain-object / array with `Object.prototype` /
 * `Array.prototype`, so subsequent mutation of the caller's input object
 * cannot reach into the returned tree.
 */
function canonicalDeepClone(
  value: unknown,
  path: string,
  seen: WeakSet<object> = new WeakSet(),
): unknown {
  if (value === null) return null;
  if (typeof value === 'boolean') return value;
  if (typeof value === 'number') {
    if (Number.isNaN(value) || !Number.isFinite(value)) {
      throw new BuilderInputRejectedError('non_finite_number', path);
    }
    return value;
  }
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) {
    if (seen.has(value)) {
      throw new BuilderInputRejectedError('cyclic_structure', path);
    }
    seen.add(value);
    try {
      if (Object.getPrototypeOf(value) !== Array.prototype) {
        throw new BuilderInputRejectedError('array_non_array_prototype', path);
      }
      if (Object.getOwnPropertySymbols(value).length > 0) {
        throw new BuilderInputRejectedError('array_symbol_key', path);
      }
      for (const name of Object.getOwnPropertyNames(value)) {
        if (name === 'length') continue;
        const desc = Object.getOwnPropertyDescriptor(value, name);
        if (!desc) continue;
        if ('get' in desc || 'set' in desc) {
          throw new BuilderInputRejectedError('array_accessor', path);
        }
        if (!isNonNegativeIntegerString(name) || Number(name) >= value.length) {
          throw new BuilderInputRejectedError('array_extra_own_property', path);
        }
        if (!desc.enumerable) {
          throw new BuilderInputRejectedError('array_non_enumerable_index', path);
        }
      }
      const out: unknown[] = [];
      for (let i = 0; i < value.length; i++) {
        const idx = String(i);
        if (!Object.prototype.hasOwnProperty.call(value, idx)) {
          throw new BuilderInputRejectedError('sparse_array', path);
        }
        out.push(canonicalDeepClone(value[i], `${path}[${i}]`, seen));
      }
      return out;
    } finally {
      seen.delete(value);
    }
  }
  if (typeof value === 'object') {
    const record = value as object;
    if (seen.has(record)) {
      throw new BuilderInputRejectedError('cyclic_structure', path);
    }
    seen.add(record);
    try {
      const proto = Object.getPrototypeOf(record);
      if (proto !== Object.prototype && proto !== null) {
        throw new BuilderInputRejectedError('non_plain_object', path);
      }
      if (Object.getOwnPropertySymbols(record).length > 0) {
        throw new BuilderInputRejectedError('symbol_key', path);
      }
      const out: Record<string, unknown> = {};
      for (const key of Object.getOwnPropertyNames(record)) {
        const desc = Object.getOwnPropertyDescriptor(record, key);
        if (!desc) continue;
        if ('get' in desc || 'set' in desc) {
          throw new BuilderInputRejectedError('accessor_property', `${path}.${key}`);
        }
        if (!desc.enumerable) {
          throw new BuilderInputRejectedError('non_enumerable_property', `${path}.${key}`);
        }
        out[key] = canonicalDeepClone(desc.value, `${path}.${key}`, seen);
      }
      return out;
    } finally {
      seen.delete(record);
    }
  }
  throw new BuilderInputRejectedError('unsupported_runtime_value', path);
}

function isNonNegativeIntegerString(name: string): boolean {
  return /^(?:0|[1-9]\d*)$/.test(name);
}

// Force TS to import `canonicalJsonBytes` so the module boundary is visible in
// import-graph tests. Not used at runtime.
void (canonicalJsonBytes as unknown as (value: CanonicalJsonValue) => Uint8Array);
