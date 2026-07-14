import { describe, expect, it } from 'vitest';
import {
  BuilderValidationError,
  LedgerOverBoundError,
  MetadataOverBoundError,
  StateManifestSerializationError,
  buildStateBundleV2,
  canonicalJsonBytes,
  classifyStateBundleV2,
  crossFieldValidate,
  semanticIdentityValidate,
  serializeStateManifestV2,
  validateStateManifestV2,
  type StateManifestV2,
} from './index.js';
import type { CanonicalJsonValue } from '../canonical-json/index.js';
import { makeStateManifestV2Input, sha256Hex } from './test-helpers.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';

const LEDGER = new TextEncoder().encode('ledger-bytes');
const METADATA = new TextEncoder().encode('metadata-bytes');

function build() {
  return buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);
}

describe('validateStateManifestV2', () => {
  it('accepts a bootstrap manifest and preserves finalized manifest', () => {
    const built = build();
    const result = validateStateManifestV2(built.manifest);
    expect(result.ok).toBe(true);
    if (result.ok) {
      expect(result.manifest).toEqual(built.manifest);
    }
  });

  it('rejects an unknown top-level field', () => {
    const built = build();
    const bad = { ...built.manifest, extra: 'x' } as unknown;
    const result = validateStateManifestV2(bad);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.diagnostic).toBe('manifest_unknown_field');
  });

  it('rejects wrong version', () => {
    const built = build();
    const bad = { ...built.manifest, version: 3 } as unknown;
    const result = validateStateManifestV2(bad);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.diagnostic).toBe('manifest_unknown_version');
  });

  it('rejects uppercase hex hashes', () => {
    const built = build();
    const bad = {
      ...built.manifest,
      ledger: { ...built.manifest.ledger, sha256: built.manifest.ledger.sha256.toUpperCase() },
    } as unknown;
    const result = validateStateManifestV2(bad);
    expect(result.ok).toBe(false);
    if (!result.ok) expect(result.diagnostic).toBe('manifest_shape_invalid');
  });

  it('cross-field: transaction.candidateLedgerSha256 must equal ledger.sha256', () => {
    const built = build();
    const bad: StateManifestV2 = {
      ...built.manifest,
      transaction: { ...built.manifest.transaction, candidateLedgerSha256: sha256Hex('nope') },
    };
    const errors = crossFieldValidate(bad);
    expect(errors.some((e) => e.includes('x_transaction_ledger_binding'))).toBe(true);
  });

  it('semantic identity rejects too-long provider id', () => {
    const built = build();
    const bad: StateManifestV2 = {
      ...built.manifest,
      cacheContractIdentity: {
        ...built.manifest.cacheContractIdentity,
        providerId: 'p'.repeat(257),
      },
    };
    const errors = semanticIdentityValidate(bad);
    expect(errors.some((e) => e.startsWith('x_identity_too_long'))).toBe(true);
  });

  it('semantic identity rejects control characters', () => {
    const built = build();
    const bad: StateManifestV2 = {
      ...built.manifest,
      cacheContractIdentity: {
        ...built.manifest.cacheContractIdentity,
        modelId: 'model\u0001x',
      },
    };
    const errors = semanticIdentityValidate(bad);
    expect(errors.some((e) => e.startsWith('x_identity_control_chars'))).toBe(true);
  });

  it('semantic identity rejects malformed repository', () => {
    const built = build();
    const bad: StateManifestV2 = {
      ...built.manifest,
      stateKey: { ...built.manifest.stateKey, repository: 'not-a-slash-path' },
    };
    const errors = semanticIdentityValidate(bad);
    expect(errors.some((e) => e.startsWith('x_repository_syntax'))).toBe(true);
  });
});

describe('serializeStateManifestV2', () => {
  it('is byte-stable', () => {
    const built = build();
    const a = serializeStateManifestV2(built.manifest);
    const b = serializeStateManifestV2(built.manifest);
    expect(a).toEqual(b);
  });

  it('matches canonicalJsonBytes over the same value', () => {
    const built = build();
    const canonical = canonicalJsonBytes(built.manifest as unknown as CanonicalJsonValue);
    expect(serializeStateManifestV2(built.manifest)).toEqual(canonical);
  });

  it('rejects invalid input with StateManifestSerializationError', () => {
    const built = build();
    const bad = { ...built.manifest, version: 1 } as unknown as StateManifestV2;
    expect(() => serializeStateManifestV2(bad)).toThrow(StateManifestSerializationError);
  });
});

describe('buildStateBundleV2', () => {
  it('fills ledger + metadata descriptors and transaction.candidateLedgerSha256', () => {
    const built = build();
    expect(built.manifest.ledger.bytes).toBe(LEDGER.byteLength);
    expect(built.manifest.providerRunMetadata.bytes).toBe(METADATA.byteLength);
    expect(built.manifest.transaction.candidateLedgerSha256).toBe(built.manifest.ledger.sha256);
  });

  it('rejects over-bound ledger bytes without allocating a copy', () => {
    const over = new Uint8Array(LEDGER_MAX_BYTES + 1);
    expect(() => buildStateBundleV2(makeStateManifestV2Input(), over, METADATA)).toThrow(
      LedgerOverBoundError,
    );
  });

  it('rejects over-bound metadata bytes', () => {
    const over = new Uint8Array(METADATA_MAX_BYTES + 1);
    expect(() => buildStateBundleV2(makeStateManifestV2Input(), LEDGER, over)).toThrow(
      MetadataOverBoundError,
    );
  });

  it('rejects a manifest that fails cross-field validation', () => {
    const input = makeStateManifestV2Input({
      transition: {
        kind: 'continuation',
        predecessorManifestSha256: sha256Hex('pred-manifest'),
        predecessorLedgerSha256: sha256Hex('pred-ledger'),
        predecessorStateGeneration: 5,
        predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA',
      },
      generation: { stateGeneration: 99, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' },
      transaction: { interactionOrdinal: 3 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow(BuilderValidationError);
  });

  it('is immune to caller mutation of the ledger buffer after return', () => {
    const ledger = new Uint8Array(LEDGER);
    const result = buildStateBundleV2(makeStateManifestV2Input(), ledger, METADATA);
    ledger[0] = 0xff;
    expect(result.ledgerBytes[0]).toBe(LEDGER[0]);
    expect(result.manifest.ledger.sha256).toBe(sha256Hex(LEDGER));
  });

  it('is immune to caller mutation of the input object after return', () => {
    const input = makeStateManifestV2Input();
    const result = buildStateBundleV2(input, LEDGER, METADATA);
    input.stateKey.repository = 'other/repo';
    input.cacheContractIdentity.providerId = 'x';
    input.provenance.workflowEvent = 'push';
    input.transaction.interactionOrdinal = 999;
    input.providerRunMetadata.producingGeneration.stateGeneration = 42;
    // Manifest identity must not change.
    expect(result.manifest.stateKey.repository).toBe('SolusQuest/agentic-pr-review');
    expect(result.manifest.cacheContractIdentity.providerId).toBe('anthropic');
    // manifestBytes must remain byte-equal to a fresh serialization of the finalized manifest.
    const reserialized = serializeStateManifestV2(result.manifest);
    expect(reserialized).toEqual(result.manifestBytes);
  });
});

describe('classifyStateBundleV2', () => {
  function listing(names = ['manifest.json', 'ledger.json', 'provider-run-metadata.json']) {
    return names.map((name) => ({ name, isRegularFile: true }));
  }

  it('returns valid for a freshly built bundle', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('valid');
    if (result.kind === 'valid') {
      expect(result.manifest).toEqual(built.manifest);
      expect(result.manifestBytes).toEqual(built.manifestBytes);
      expect(result.ledgerBytes).toEqual(built.ledgerBytes);
      expect(result.providerRunMetadataBytes).toEqual(built.providerRunMetadataBytes);
    }
  });

  it('classifies a v1 manifest as unsupported_legacy_v1 before layout checks', () => {
    const v1Bytes = new TextEncoder().encode(JSON.stringify({ version: 1, extra: 'anything' }));
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'runtime', isRegularFile: true },
        { name: 'some-legacy-file.txt', isRegularFile: true },
      ],
      manifestBytes: v1Bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('classifies v1 manifest as unsupported_legacy_v1 even when non-v2 sidecars exceed v2 caps', () => {
    const v1Bytes = new TextEncoder().encode(JSON.stringify({ version: 1 }));
    const ledger = new Uint8Array(LEDGER_MAX_BYTES + 1);
    const metadata = new Uint8Array(METADATA_MAX_BYTES + 1);
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'legacy-a', isRegularFile: true },
        { name: 'legacy-b', isRegularFile: true },
      ],
      manifestBytes: v1Bytes,
      ledgerBytes: ledger,
      providerRunMetadataBytes: metadata,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('rejects a symlink manifest entry', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: false },
        { name: 'ledger.json', isRegularFile: true },
        { name: 'provider-run-metadata.json', isRegularFile: true },
      ],
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_path_unsafe');
  });

  it('rejects an extra entry', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'ledger.json', isRegularFile: true },
        { name: 'provider-run-metadata.json', isRegularFile: true },
        { name: 'unexpected.txt', isRegularFile: true },
      ],
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_extra_entry');
  });

  it('rejects duplicate expected filename in listing', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'ledger.json', isRegularFile: true },
        { name: 'ledger.json', isRegularFile: true },
        { name: 'provider-run-metadata.json', isRegularFile: true },
      ],
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_listing_mismatch');
  });

  it('rejects a listing/bytes inconsistency (listed but bytes undefined)', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_listing_mismatch');
  });

  it('rejects a listing/bytes inconsistency (not listed but bytes provided)', () => {
    const built = build();
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'ledger.json', isRegularFile: true },
      ],
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_listing_mismatch');
  });

  it('rejects a byte-limit-exceeded manifest', () => {
    const big = new Uint8Array(65537);
    // JSON must at least be interpretable — but size cap runs first.
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: big,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('manifest_byte_limit_exceeded');
  });

  it('rejects duplicate JSON keys', () => {
    const source = '{"version": 2, "version": 2}';
    const bytes = new TextEncoder().encode(source);
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('manifest_invalid_json');
  });

  it('rejects manifest with BOM', () => {
    const bytes = new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]);
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('manifest_invalid_json');
  });

  it('rejects a ledger hash mismatch', () => {
    const built = build();
    const tampered = new Uint8Array(built.ledgerBytes);
    tampered[0] = tampered[0] ^ 0x01;
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: tampered,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('ledger_hash_mismatch');
  });

  it('rejects a ledger byte-length mismatch', () => {
    const built = build();
    const shorter = built.ledgerBytes.slice(0, built.ledgerBytes.byteLength - 1);
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: shorter,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('ledger_bytes_mismatch');
  });

  it('rejects a metadata hash mismatch', () => {
    const built = build();
    const tampered = new Uint8Array(built.providerRunMetadataBytes);
    tampered[0] ^= 0x02;
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: tampered,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid')
      expect(result.diagnostic).toBe('provider_run_metadata_hash_mismatch');
  });

  it('is immune to caller mutation of buffers after return', () => {
    const built = build();
    const ledgerCopy = new Uint8Array(built.ledgerBytes);
    const result = classifyStateBundleV2({
      entryListing: listing(),
      manifestBytes: built.manifestBytes,
      ledgerBytes: ledgerCopy,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('valid');
    ledgerCopy[0] ^= 0xff;
    if (result.kind === 'valid') {
      expect(result.ledgerBytes).toEqual(built.ledgerBytes);
    }
  });
});
