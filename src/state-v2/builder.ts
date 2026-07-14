import { createHash } from 'node:crypto';
import { CanonicalJsonInputError, canonicalJsonBytes } from '../canonical-json/index.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';
import type { Sha256Hex, StateManifestV2, StateManifestV2Input } from './manifest.js';
import { validateStateManifestV2, boundedDiagnosticMessage } from './schema.js';
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
    super(boundedDiagnosticMessage(`builder rejected finalized manifest: ${detail}`));
    this.name = 'BuilderValidationError';
    this.detail = detail;
  }
}

export class BuilderInputRejectedError extends Error {
  readonly reason: string;
  readonly path: string;
  constructor(reason: string, path: string) {
    // Both `reason` and `path` may come from the shared canonical helper,
    // whose messages embed caller-controlled property names. Reduce them
    // to a bounded, structural form before including in the public error.
    const safeReason = sanitizeInputReason(reason);
    const safePath = sanitizeInputPath(path);
    super(boundedDiagnosticMessage(`builder_input_rejected:${safeReason}@${safePath}`));
    this.name = 'BuilderInputRejectedError';
    this.reason = safeReason;
    this.path = safePath;
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
 *   2. Validate the caller's input tree against the canonical-JSON accepted
 *      domain by running it through the shared `canonicalJsonBytes` helper.
 *      Any `CanonicalJsonInputError` becomes a `BuilderInputRejectedError`
 *      so callers see a single typed rejection instead of two overlapping
 *      contracts. This also rejects lone UTF-16 surrogates in both string
 *      values and property names.
 *   3. Deep-copy the accepted input by round-tripping the canonical bytes
 *      through `JSON.parse` — the tree returned to the manifest builder is
 *      brand-new plain objects/arrays completely disconnected from the
 *      caller's input references.
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

  let canonicalBytes: Uint8Array;
  try {
    canonicalBytes = canonicalJsonBytes(input as unknown);
  } catch (err) {
    if (err instanceof CanonicalJsonInputError) {
      throw new BuilderInputRejectedError(err.reason, err.path);
    }
    throw err;
  }
  const clonedInput = JSON.parse(
    new TextDecoder('utf-8').decode(canonicalBytes),
  ) as StateManifestV2Input;

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
 * Sanitize a canonical-domain rejection path to a bounded form. Path
 * segments are supplied by the canonical helper and can include
 * caller-controlled property names. We only keep the top-level structure
 * shape (`$.a.b.c` or `$[0].x`) with each segment collapsed to `<segment>`
 * so unknown or hostile names never appear in the emitted error message.
 */
function sanitizeInputPath(path: string): string {
  // Preserve the leading `$` and the delimiters (`.`, `[`, `]`), but
  // replace each named segment with `<segment>` and each numeric index
  // with `<i>`. A defensive character cap prevents runaway paths.
  const MAX = 128;
  let out = '';
  let i = 0;
  while (i < path.length && out.length < MAX) {
    const ch = path[i];
    if (ch === '$' || ch === '.' || ch === '[' || ch === ']') {
      out += ch;
      i += 1;
      continue;
    }
    // Scan until the next delimiter and collapse.
    let j = i;
    let isIndex = true;
    while (j < path.length && path[j] !== '.' && path[j] !== '[' && path[j] !== ']') {
      if (path[j] < '0' || path[j] > '9') isIndex = false;
      j += 1;
    }
    out += isIndex ? '<i>' : '<segment>';
    i = j;
  }
  return out;
}

/**
 * Reduce a canonical-domain rejection reason to a fixed structural code.
 * The shared canonical helper embeds caller-controlled property names in
 * some of its reason strings (for example, "accessor property 'foo'"). We
 * translate the family of reasons to fixed codes and drop the embedded
 * names so no caller-controlled content survives.
 */
function sanitizeInputReason(reason: string): string {
  if (reason.startsWith('accessor property')) return 'accessor_property';
  if (reason.startsWith('non-enumerable own property')) return 'non_enumerable_property';
  if (reason === 'symbol-keyed own property') return 'symbol_key';
  if (reason === 'non-plain object') return 'non_plain_object';
  if (reason === 'cyclic structure') return 'cyclic_structure';
  if (reason === 'lone high surrogate') return 'lone_high_surrogate';
  if (reason === 'lone low surrogate') return 'lone_low_surrogate';
  if (reason.startsWith('sparse array')) return 'sparse_array';
  if (reason.includes('bigint')) return 'bigint';
  if (reason.includes('symbol')) return 'symbol';
  if (reason.includes('function')) return 'function';
  if (reason.includes('undefined')) return 'undefined';
  if (reason.includes('NaN') || reason.includes('Infinity')) return 'non_finite_number';
  return 'canonical_domain_reject';
}
