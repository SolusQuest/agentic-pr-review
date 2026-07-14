import { createHash } from 'node:crypto';
import type { CanonicalJsonValue } from '../canonical-json/index.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';
import type { StateManifestV2, StateManifestV2Input } from './manifest.js';
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

export interface BuildStateBundleV2Result {
  manifest: StateManifestV2;
  manifestBytes: Uint8Array;
  ledgerBytes: Uint8Array;
  providerRunMetadataBytes: Uint8Array;
}

/**
 * Pure builder. Never touches the filesystem.
 *
 * Order of operations:
 *   1. Size caps on the caller's byte views (byteLength on original view).
 *   2. Deep-clone the caller's input object using the canonical-JSON accepted
 *      domain so mutation of the caller's input after return cannot mutate
 *      any BuildResult byte.
 *   3. Copy the accepted byte views into fresh Uint8Array snapshots.
 *   4. Compute SHA-256 over the snapshots and fill descriptor + transaction
 *      binding fields.
 *   5. Validate the finalized manifest and serialize it.
 */
export function buildStateBundleV2(
  input: StateManifestV2Input,
  ledgerBytes: Uint8Array,
  providerRunMetadataBytes: Uint8Array,
): BuildStateBundleV2Result {
  if (ledgerBytes.byteLength > LEDGER_MAX_BYTES) {
    throw new LedgerOverBoundError(ledgerBytes.byteLength);
  }
  if (providerRunMetadataBytes.byteLength > METADATA_MAX_BYTES) {
    throw new MetadataOverBoundError(providerRunMetadataBytes.byteLength);
  }

  const clonedInput = deepClone(input) as StateManifestV2Input;
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

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

/**
 * Deep-clone a plain JSON-ish value using the canonical-JSON accepted domain.
 * We deliberately do not use structuredClone / JSON.parse(JSON.stringify)
 * because we want deterministic rejection of the same values the serializer
 * rejects (undefined, bigint, etc.). Types are the caller's contract.
 */
function deepClone(value: unknown): unknown {
  if (value === null) return null;
  const type = typeof value;
  if (type === 'boolean' || type === 'number' || type === 'string') return value;
  if (Array.isArray(value)) {
    return (value as readonly unknown[]).map((entry) => deepClone(entry));
  }
  if (type === 'object') {
    const out: Record<string, unknown> = {};
    for (const key of Object.keys(value as Record<string, unknown>)) {
      out[key] = deepClone((value as Record<string, unknown>)[key]);
    }
    return out;
  }
  // Reject anything else via canonicalJsonBytes when the manifest is
  // serialized. For deep-clone we conservatively pass through so validation
  // errors surface with a clear code, not a clone-time crash.
  return value as unknown as CanonicalJsonValue;
}
