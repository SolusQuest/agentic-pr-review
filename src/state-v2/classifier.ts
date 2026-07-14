import { createHash } from 'node:crypto';
import {
  LEDGER_FILENAME,
  LEDGER_MAX_BYTES,
  MANIFEST_FILENAME,
  MANIFEST_MAX_BYTES,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_FILENAME,
} from './constants.js';
import type { DiagnosticCode } from './diagnostics.js';
import type { StateManifestV2 } from './manifest.js';
import { boundedJoin, validateStateManifestV2 } from './schema.js';
import { strictParseJson } from './strict-json.js';

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
      diagnostic: DiagnosticCode;
      message: string;
    };

interface ListingIndex {
  entries: Map<string, EntryDescriptor>;
  duplicates: Set<string>;
  extraNames: string[];
  nonRegular: EntryDescriptor[];
}

/**
 * Pure classifier. Reads the caller-supplied listing plus buffers; never
 * touches the filesystem. Byte caps run on the caller's original views;
 * accepted buffers are copied into internal snapshots before parsing/hashing.
 */
export function classifyStateBundleV2(input: ClassifyStateBundleV2Input): BundleClassification {
  const listing = input.entryListing ?? [];
  const listingIndex = indexByName(listing);

  // Step 1: manifest-entry safety and manifest listing/bytes consistency.
  if (listingIndex.duplicates.has(MANIFEST_FILENAME)) {
    return invalid('bundle_listing_mismatch', 'duplicate entry: manifest.json');
  }
  const manifestEntry = listingIndex.entries.get(MANIFEST_FILENAME);
  if (manifestEntry && !manifestEntry.isRegularFile) {
    return invalid('bundle_path_unsafe', 'manifest.json is not a regular file');
  }
  const manifestBytes = input.manifestBytes;
  if (manifestEntry && manifestBytes === undefined) {
    return invalid('bundle_listing_mismatch', 'manifest.json listed but manifestBytes missing');
  }
  if (!manifestEntry && manifestBytes !== undefined) {
    return invalid(
      'bundle_listing_mismatch',
      'manifestBytes provided but manifest.json not listed',
    );
  }
  if (manifestBytes === undefined) {
    return invalid('manifest_missing', 'manifest.json is not present');
  }

  // Step 2: manifest byte cap and parse.
  if (manifestBytes.byteLength > MANIFEST_MAX_BYTES) {
    return invalid(
      'manifest_byte_limit_exceeded',
      `manifest bytes ${manifestBytes.byteLength} exceed MANIFEST_MAX_BYTES=${MANIFEST_MAX_BYTES}`,
    );
  }
  const manifestSnapshot = copyBytes(manifestBytes);

  let manifestString: string;
  try {
    manifestString = decodeManifest(manifestSnapshot);
  } catch (err) {
    return invalid('manifest_invalid_json', errorMessage(err));
  }
  let parsed: unknown;
  try {
    parsed = strictParseJson(manifestString);
  } catch (err) {
    return invalid('manifest_invalid_json', errorMessage(err));
  }

  // Step 3: legacy v1 short-circuit. If own `version` equals JSON number 1,
  // classify as unsupported_legacy_v1 without applying any v2-layout rule.
  if (isLegacyV1Manifest(parsed)) {
    return { kind: 'unsupported_legacy_v1', diagnostic: 'state_unsupported_legacy_v1' };
  }

  // Step 4: v2 Ajv + cross-field validation.
  const validation = validateStateManifestV2(parsed);
  if (!validation.ok) {
    return invalid(validation.diagnostic, validation.message);
  }
  const manifest = validation.manifest;

  // Step 5: remaining v2 layout/listing consistency (ledger + metadata + extras).
  if (listingIndex.nonRegular.length > 0) {
    const bad = listingIndex.nonRegular[0];
    return invalid('bundle_path_unsafe', `entry '${bad.name}' is not a regular file`);
  }
  for (const dup of listingIndex.duplicates) {
    if (dup !== MANIFEST_FILENAME) {
      return invalid('bundle_listing_mismatch', `duplicate entry: ${dup}`);
    }
  }
  if (listingIndex.extraNames.length > 0) {
    return invalid('bundle_extra_entry', `unexpected entry: ${listingIndex.extraNames[0]}`);
  }
  const ledgerConsistency = expectedFileConsistency(
    LEDGER_FILENAME,
    listingIndex,
    input.ledgerBytes,
    'ledger_missing',
  );
  if (ledgerConsistency) return ledgerConsistency;
  const metadataConsistency = expectedFileConsistency(
    PROVIDER_RUN_METADATA_FILENAME,
    listingIndex,
    input.providerRunMetadataBytes,
    'provider_run_metadata_missing',
  );
  if (metadataConsistency) return metadataConsistency;

  // Step 6: ledger byte cap + integrity.
  const ledger = input.ledgerBytes;
  if (ledger === undefined) return invalid('ledger_missing', 'ledger.json is not present');
  if (ledger.byteLength > LEDGER_MAX_BYTES) {
    return invalid(
      'ledger_byte_limit_exceeded',
      `ledger bytes ${ledger.byteLength} exceed LEDGER_MAX_BYTES=${LEDGER_MAX_BYTES}`,
    );
  }
  const ledgerSnapshot = copyBytes(ledger);
  if (ledgerSnapshot.byteLength !== manifest.ledger.bytes) {
    return invalid(
      'ledger_bytes_mismatch',
      `ledger byte length ${ledgerSnapshot.byteLength} does not match manifest.ledger.bytes ${manifest.ledger.bytes}`,
    );
  }
  const ledgerHash = sha256Hex(ledgerSnapshot);
  if (ledgerHash !== manifest.ledger.sha256) {
    return invalid(
      'ledger_hash_mismatch',
      `ledger sha256 ${ledgerHash} does not match manifest.ledger.sha256 ${manifest.ledger.sha256}`,
    );
  }

  // Step 7: provider run metadata byte cap + integrity.
  const metadata = input.providerRunMetadataBytes;
  if (metadata === undefined) {
    return invalid('provider_run_metadata_missing', 'provider-run-metadata.json is not present');
  }
  if (metadata.byteLength > METADATA_MAX_BYTES) {
    return invalid(
      'provider_run_metadata_byte_limit_exceeded',
      `provider-run-metadata bytes ${metadata.byteLength} exceed METADATA_MAX_BYTES=${METADATA_MAX_BYTES}`,
    );
  }
  const metadataSnapshot = copyBytes(metadata);
  if (metadataSnapshot.byteLength !== manifest.providerRunMetadata.bytes) {
    return invalid(
      'provider_run_metadata_bytes_mismatch',
      `metadata byte length ${metadataSnapshot.byteLength} does not match manifest.providerRunMetadata.bytes ${manifest.providerRunMetadata.bytes}`,
    );
  }
  const metadataHash = sha256Hex(metadataSnapshot);
  if (metadataHash !== manifest.providerRunMetadata.sha256) {
    return invalid(
      'provider_run_metadata_hash_mismatch',
      `metadata sha256 ${metadataHash} does not match manifest.providerRunMetadata.sha256 ${manifest.providerRunMetadata.sha256}`,
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
  const expected = new Set<string>([
    MANIFEST_FILENAME,
    LEDGER_FILENAME,
    PROVIDER_RUN_METADATA_FILENAME,
  ]);
  for (const entry of listing) {
    if (!entry.isRegularFile) {
      nonRegular.push(entry);
    }
    if (expected.has(entry.name)) {
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
  missingCode: DiagnosticCode,
): BundleClassification | null {
  const listed = index.entries.has(name);
  if (listed && bytes === undefined) {
    return invalid('bundle_listing_mismatch', `${name} listed but bytes missing`);
  }
  if (!listed && bytes !== undefined) {
    return invalid('bundle_listing_mismatch', `${name} bytes provided but not listed`);
  }
  if (!listed && bytes === undefined) {
    return invalid(missingCode, `${name} is not present`);
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
    throw new Error('BOM not permitted');
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

function invalid(diagnostic: DiagnosticCode, message: string): BundleClassification {
  return {
    kind: 'invalid',
    diagnostic,
    message: boundedJoin([message]),
  };
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : String(err);
}

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}
