import { createHash } from 'node:crypto';
import {
  LEDGER_FILENAME,
  LEDGER_MAX_BYTES,
  MANIFEST_FILENAME,
  MANIFEST_MAX_BYTES,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_FILENAME,
} from './constants.js';
import type { InvalidDiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import { boundedJoin, validateStateManifestV2 } from './schema.js';
import { strictParseJson } from './strict-json.js';
import { renderWireEntry, scanStringSafety } from './shared-safe-path.js';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };

export interface EntryDescriptor {
  name: string;
  isRegularFile: boolean;
}

export interface ClassifyStateBundleV2Input {
  entryListing: readonly EntryDescriptor[];
  manifestBytes: Uint8Array | undefined;
  ledgerBytes: Uint8Array | undefined;
  providerRunMetadataBytes: Uint8Array | undefined;
}

export type BundleClassification =
  | {
      kind: 'valid';
      manifest: StateManifestV2;
      manifestBytes: Uint8Array;
      ledgerBytes: Uint8Array;
      providerRunMetadataBytes: Uint8Array;
    }
  | {
      kind: 'unsupported_legacy_v1';
      diagnostic: 'state_unsupported_legacy_v1';
    }
  | {
      kind: 'invalid';
      diagnostic: InvalidDiagnosticCode;
      message: string;
    };

interface ListingIndex {
  entries: Map<string, EntryDescriptor>;
  duplicates: Set<string>;
  extraNames: string[];
  nonRegular: EntryDescriptor[];
}

const EXPECTED_NAMES = new Set<string>([
  MANIFEST_FILENAME,
  LEDGER_FILENAME,
  PROVIDER_RUN_METADATA_FILENAME,
]);

/**
 * Pure classifier. Reads the caller-supplied listing plus buffers; never
 * touches the filesystem. Byte caps run on the caller's original views;
 * accepted buffers are copied into internal snapshots before parsing/hashing.
 *
 * Diagnostic messages carry only fixed reason codes, structural JSON paths,
 * and generic descriptions. They never include unknown property names,
 * duplicate keys, unexpected entry filenames, or observed hash digests.
 */
export function classifyStateBundleV2(input: ClassifyStateBundleV2Input): BundleClassification {
  const listing = input.entryListing ?? [];
  const listingIndex = indexByName(listing);

  // Step 1: manifest-entry safety and manifest listing/bytes consistency.
  if (listingIndex.duplicates.has(MANIFEST_FILENAME)) {
    return invalidWire('bundle_listing_mismatch', 'x_invalid_field', '/');
  }
  const manifestEntry = listingIndex.entries.get(MANIFEST_FILENAME);
  if (manifestEntry && !manifestEntry.isRegularFile) {
    return invalidWire('bundle_path_unsafe', 'x_invalid_field', '/');
  }
  const manifestBytes = input.manifestBytes;
  if (manifestEntry && manifestBytes === undefined) {
    return invalidWire('bundle_listing_mismatch', 'x_invalid_field', '/');
  }
  if (!manifestEntry && manifestBytes !== undefined) {
    return invalidWire('bundle_listing_mismatch', 'x_invalid_field', '/');
  }
  if (manifestBytes === undefined) {
    return invalidWire('manifest_missing', 'x_invalid_field', '/');
  }

  // Step 2: manifest byte cap and parse.
  if (manifestBytes.byteLength > MANIFEST_MAX_BYTES) {
    return invalidWire('manifest_byte_limit_exceeded', 'x_invalid_field', '/');
  }
  const manifestSnapshot = copyBytes(manifestBytes);

  let manifestString: string;
  try {
    manifestString = decodeManifest(manifestSnapshot);
  } catch (err) {
    return invalidWire('manifest_invalid_json', 'x_invalid_json', '/');
  }
  let parsed: unknown;
  try {
    parsed = strictParseJson(manifestString);
  } catch (err) {
    return invalidWire('manifest_invalid_json', 'x_invalid_json', '/');
  }

  // Step 3: legacy v1 short-circuit.
  if (isLegacyV1Manifest(parsed)) {
    return { kind: 'unsupported_legacy_v1', diagnostic: 'state_unsupported_legacy_v1' };
  }

  // Step 4: shared string-safety traversal (NUL + unpaired UTF-16 surrogate)
  // per the design contract's `### Shared traversal order and stage
  // precedence`. Runs before Ajv so attacker-controlled property names do
  // not leak into Ajv error paths.
  const stringSafety = scanStringSafety(
    parsed,
    schema as unknown as Parameters<typeof scanStringSafety>[1],
  );
  if (stringSafety) {
    const { wireEntry } = renderWireEntry('x_invalid_unicode', stringSafety.segments);
    return {
      kind: 'invalid',
      diagnostic: 'manifest_shape_invalid',
      message: boundedJoin([wireEntry]),
    };
  }

  // Step 5: v2 Ajv + cross-field validation.
  const validation = validateStateManifestV2(parsed);
  if (!validation.ok) {
    return {
      kind: 'invalid',
      diagnostic: validation.diagnostic as InvalidDiagnosticCode,
      message: boundedJoin([validation.message]),
    };
  }
  const manifest = validation.manifest;

  // Step 6: remaining v2 layout/listing consistency (ledger + metadata entries + extras).
  // Precedence within this step: duplicates > non-regular > extras. This
  // ordering is test-observable via the classifier fixture matrix.
  for (const dup of listingIndex.duplicates) {
    if (dup !== MANIFEST_FILENAME) {
      return invalidWire('bundle_listing_mismatch', 'x_invalid_field', '/');
    }
  }
  if (listingIndex.nonRegular.length > 0) {
    return invalidWire('bundle_path_unsafe', 'x_invalid_field', '/');
  }
  if (listingIndex.extraNames.length > 0) {
    return invalidWire('bundle_extra_entry', 'x_invalid_field', '/');
  }
  const ledgerConsistency = expectedFileConsistency(
    LEDGER_FILENAME,
    listingIndex,
    input.ledgerBytes,
    'ledger_missing',
    '/ledger',
  );
  if (ledgerConsistency) return ledgerConsistency;
  const metadataConsistency = expectedFileConsistency(
    PROVIDER_RUN_METADATA_FILENAME,
    listingIndex,
    input.providerRunMetadataBytes,
    'provider_run_metadata_missing',
    '/providerRunMetadata',
  );
  if (metadataConsistency) return metadataConsistency;

  // Step 7: ledger byte cap + integrity.
  const ledger = input.ledgerBytes;
  if (ledger === undefined) return invalidWire('ledger_missing', 'x_invalid_field', '/ledger');
  if (ledger.byteLength > LEDGER_MAX_BYTES) {
    return invalidWire('ledger_byte_limit_exceeded', 'x_invalid_field', '/ledger');
  }
  const ledgerSnapshot = copyBytes(ledger);
  if (ledgerSnapshot.byteLength !== manifest.ledger.bytes) {
    return invalidWire('ledger_bytes_mismatch', 'x_invalid_field', '/ledger/bytes');
  }
  const ledgerHash = sha256Hex(ledgerSnapshot);
  if (ledgerHash !== manifest.ledger.sha256) {
    return invalidWire('ledger_hash_mismatch', 'x_invalid_field', '/ledger/sha256');
  }

  // Step 8: provider run metadata byte cap + integrity.
  const metadata = input.providerRunMetadataBytes;
  if (metadata === undefined) {
    return invalidWire('provider_run_metadata_missing', 'x_invalid_field', '/providerRunMetadata');
  }
  if (metadata.byteLength > METADATA_MAX_BYTES) {
    return invalidWire(
      'provider_run_metadata_byte_limit_exceeded',
      'x_invalid_field',
      '/providerRunMetadata',
    );
  }
  const metadataSnapshot = copyBytes(metadata);
  if (metadataSnapshot.byteLength !== manifest.providerRunMetadata.bytes) {
    return invalidWire(
      'provider_run_metadata_bytes_mismatch',
      'x_invalid_field',
      '/providerRunMetadata/bytes',
    );
  }
  const metadataHash = sha256Hex(metadataSnapshot);
  if (metadataHash !== manifest.providerRunMetadata.sha256) {
    return invalidWire(
      'provider_run_metadata_hash_mismatch',
      'x_invalid_field',
      '/providerRunMetadata/sha256',
    );
  }

  return {
    kind: 'valid',
    manifest,
    manifestBytes: manifestSnapshot,
    ledgerBytes: ledgerSnapshot,
    providerRunMetadataBytes: metadataSnapshot,
  };
}

function indexByName(listing: readonly EntryDescriptor[]): ListingIndex {
  const entries = new Map<string, EntryDescriptor>();
  const duplicates = new Set<string>();
  const extraNames: string[] = [];
  const nonRegular: EntryDescriptor[] = [];
  for (const entry of listing) {
    if (!entry.isRegularFile) {
      nonRegular.push(entry);
    }
    if (EXPECTED_NAMES.has(entry.name)) {
      if (entries.has(entry.name)) {
        duplicates.add(entry.name);
      } else {
        entries.set(entry.name, entry);
      }
    } else {
      extraNames.push(entry.name);
    }
  }
  return { entries, duplicates, extraNames, nonRegular };
}

function expectedFileConsistency(
  name: string,
  index: ListingIndex,
  bytes: Uint8Array | undefined,
  missingCode: InvalidDiagnosticCode,
  safePath: string,
): BundleClassification | null {
  const listed = index.entries.has(name);
  if (listed && bytes === undefined) {
    return invalidWire('bundle_listing_mismatch', 'x_invalid_field', safePath);
  }
  if (!listed && bytes !== undefined) {
    return invalidWire('bundle_listing_mismatch', 'x_invalid_field', safePath);
  }
  if (!listed && bytes === undefined) {
    return invalidWire(missingCode, 'x_invalid_field', safePath);
  }
  return null;
}

function copyBytes(source: Uint8Array): Uint8Array {
  const copy = new Uint8Array(source.byteLength);
  copy.set(source);
  return copy;
}

function decodeManifest(bytes: Uint8Array): string {
  if (bytes.byteLength >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
    throw new Error('bom_not_permitted');
  }
  return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
}

function isLegacyV1Manifest(value: unknown): boolean {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  if (!Object.prototype.hasOwnProperty.call(record, 'version')) return false;
  const version = record.version;
  return typeof version === 'number' && version === 1;
}

function invalidWire(
  diagnostic: InvalidDiagnosticCode,
  code: 'x_invalid_json' | 'x_invalid_unicode' | 'x_invalid_field',
  safePath: string,
): BundleClassification {
  const segs = safePath === '' ? [] : safePath.slice(1).split('/');
  const wire = renderWireEntry(code, segs).wireEntry;
  return {
    kind: 'invalid',
    diagnostic,
    message: boundedJoin([wire]),
  };
}

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}
