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
      diagnostic: DiagnosticCode;
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
    return invalid('bundle_listing_mismatch', 'duplicate_expected_entry:manifest.json');
  }
  const manifestEntry = listingIndex.entries.get(MANIFEST_FILENAME);
  if (manifestEntry && !manifestEntry.isRegularFile) {
    return invalid('bundle_path_unsafe', 'non_regular_entry:manifest.json');
  }
  const manifestBytes = input.manifestBytes;
  if (manifestEntry && manifestBytes === undefined) {
    return invalid('bundle_listing_mismatch', 'listing_present_bytes_missing:manifest.json');
  }
  if (!manifestEntry && manifestBytes !== undefined) {
    return invalid('bundle_listing_mismatch', 'listing_absent_bytes_present:manifest.json');
  }
  if (manifestBytes === undefined) {
    return invalid('manifest_missing', 'manifest_missing');
  }

  // Step 2: manifest byte cap and parse.
  if (manifestBytes.byteLength > MANIFEST_MAX_BYTES) {
    return invalid('manifest_byte_limit_exceeded', 'manifest_bytes_over_cap');
  }
  const manifestSnapshot = copyBytes(manifestBytes);

  let manifestString: string;
  try {
    manifestString = decodeManifest(manifestSnapshot);
  } catch (err) {
    return invalid('manifest_invalid_json', `manifest_decode:${errorKind(err)}`);
  }
  let parsed: unknown;
  try {
    parsed = strictParseJson(manifestString);
  } catch (err) {
    return invalid('manifest_invalid_json', `manifest_parse:${errorKind(err)}`);
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
    return invalid('manifest_shape_invalid', wireEntry);
  }

  // Step 5: v2 Ajv + cross-field validation.
  const validation = validateStateManifestV2(parsed);
  if (!validation.ok) {
    return invalid(validation.diagnostic, validation.message);
  }
  const manifest = validation.manifest;

  // Step 6: remaining v2 layout/listing consistency (ledger + metadata entries + extras).
  // Precedence within this step: duplicates > non-regular > extras. This
  // ordering is test-observable via the classifier fixture matrix.
  for (const dup of listingIndex.duplicates) {
    if (dup !== MANIFEST_FILENAME) {
      return invalid('bundle_listing_mismatch', `duplicate_expected_entry:${sanitizeName(dup)}`);
    }
  }
  if (listingIndex.nonRegular.length > 0) {
    const bad = listingIndex.nonRegular[0];
    return invalid('bundle_path_unsafe', `non_regular_entry:${sanitizeName(bad.name)}`);
  }
  if (listingIndex.extraNames.length > 0) {
    return invalid('bundle_extra_entry', 'unexpected_entry_present');
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

  // Step 7: ledger byte cap + integrity.
  const ledger = input.ledgerBytes;
  if (ledger === undefined) return invalid('ledger_missing', 'ledger_missing');
  if (ledger.byteLength > LEDGER_MAX_BYTES) {
    return invalid('ledger_byte_limit_exceeded', 'ledger_bytes_over_cap');
  }
  const ledgerSnapshot = copyBytes(ledger);
  if (ledgerSnapshot.byteLength !== manifest.ledger.bytes) {
    return invalid('ledger_bytes_mismatch', 'ledger_byte_length_disagrees_with_descriptor');
  }
  const ledgerHash = sha256Hex(ledgerSnapshot);
  if (ledgerHash !== manifest.ledger.sha256) {
    return invalid('ledger_hash_mismatch', 'ledger_sha256_disagrees_with_descriptor');
  }

  // Step 8: provider run metadata byte cap + integrity.
  const metadata = input.providerRunMetadataBytes;
  if (metadata === undefined) {
    return invalid('provider_run_metadata_missing', 'provider_run_metadata_missing');
  }
  if (metadata.byteLength > METADATA_MAX_BYTES) {
    return invalid('provider_run_metadata_byte_limit_exceeded', 'metadata_bytes_over_cap');
  }
  const metadataSnapshot = copyBytes(metadata);
  if (metadataSnapshot.byteLength !== manifest.providerRunMetadata.bytes) {
    return invalid(
      'provider_run_metadata_bytes_mismatch',
      'metadata_byte_length_disagrees_with_descriptor',
    );
  }
  const metadataHash = sha256Hex(metadataSnapshot);
  if (metadataHash !== manifest.providerRunMetadata.sha256) {
    return invalid(
      'provider_run_metadata_hash_mismatch',
      'metadata_sha256_disagrees_with_descriptor',
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
  missingCode: DiagnosticCode,
): BundleClassification | null {
  const listed = index.entries.has(name);
  if (listed && bytes === undefined) {
    return invalid(
      'bundle_listing_mismatch',
      `listing_present_bytes_missing:${sanitizeName(name)}`,
    );
  }
  if (!listed && bytes !== undefined) {
    return invalid('bundle_listing_mismatch', `listing_absent_bytes_present:${sanitizeName(name)}`);
  }
  if (!listed && bytes === undefined) {
    return invalid(missingCode, `${sanitizeName(name)}_missing`);
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

function invalid(diagnostic: DiagnosticCode, message: string): BundleClassification {
  return {
    kind: 'invalid',
    diagnostic,
    message: boundedJoin([message]),
  };
}

/**
 * Reduce a caller-supplied filename to one of the known expected filenames.
 * Any other value collapses to `<extra>` so unexpected filenames from a
 * misfiled or attacker-controlled listing never leak into a diagnostic
 * message.
 */
function sanitizeName(name: string): string {
  if (EXPECTED_NAMES.has(name)) return name;
  return '<extra>';
}

/** Reduce an unknown error to a short structural label (never the raw text). */
function errorKind(err: unknown): string {
  if (err instanceof Error && err.name) {
    return err.name.toLowerCase();
  }
  return 'error';
}

function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}
