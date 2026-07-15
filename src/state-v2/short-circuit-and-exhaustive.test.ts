import { describe, expect, it } from 'vitest';
import {
  buildStateBundleV2,
  classifyStateBundleV2,
  type BundleClassification,
  type EntryDescriptor,
} from './index.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';
import { makeStateManifestV2Input } from './test-helpers.js';

const enc = new TextEncoder();
const LEDGER = enc.encode('ledger-bytes');
const METADATA = enc.encode('metadata-bytes');

function fullListing(): EntryDescriptor[] {
  return [
    { name: 'manifest.json', isRegularFile: true },
    { name: 'ledger.json', isRegularFile: true },
    { name: 'provider-run-metadata.json', isRegularFile: true },
  ];
}

describe('v1 short-circuit decisive precedence', () => {
  it('classifies version:1 with extra listing entries as unsupported_legacy_v1 (not bundle_extra_entry)', () => {
    const bytes = enc.encode('{"version": 1, "workflow": "agentic-pr-review"}');
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'legacy-a.txt', isRegularFile: true },
        { name: 'legacy-b.txt', isRegularFile: true },
        { name: 'runtime', isRegularFile: true },
      ],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('classifies version:1 with symlink ledger entry as unsupported_legacy_v1 (not bundle_path_unsafe)', () => {
    const bytes = enc.encode('{"version": 1}');
    const result = classifyStateBundleV2({
      entryListing: [
        { name: 'manifest.json', isRegularFile: true },
        { name: 'ledger.json', isRegularFile: false },
      ],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('classifies version:1 with oversized ledger/metadata caller-supplied bytes as unsupported_legacy_v1', () => {
    const bytes = enc.encode('{"version": 1}');
    const ledger = new Uint8Array(LEDGER_MAX_BYTES + 1);
    const metadata = new Uint8Array(METADATA_MAX_BYTES + 1);
    const result = classifyStateBundleV2({
      entryListing: fullListing(),
      manifestBytes: bytes,
      ledgerBytes: ledger,
      providerRunMetadataBytes: metadata,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('classifies version:1.0 as unsupported_legacy_v1 (JSON numeric equality)', () => {
    const bytes = enc.encode('{"version": 1.0}');
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('classifies version:1e0 as unsupported_legacy_v1', () => {
    const bytes = enc.encode('{"version": 1e0}');
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('unsupported_legacy_v1');
  });

  it('does NOT short-circuit on version:"1" (string, not numeric 1)', () => {
    const bytes = enc.encode('{"version": "1"}');
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('manifest_unknown_version');
  });

  it('rejects a symlink manifest.json before parse (safety check on manifest itself)', () => {
    const bytes = enc.encode('{"version": 1}');
    const result = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: false }],
      manifestBytes: bytes,
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    // The manifest itself must be a regular file; v1 short-circuit runs
    // AFTER we accept the manifest entry.
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') expect(result.diagnostic).toBe('bundle_path_unsafe');
  });
});

describe('diagnostic exhaustiveness', () => {
  const codes: BundleClassification['kind'] extends 'invalid' ? never : never = null as never;
  void codes;

  it('every DiagnosticCode string is reachable by at least one path', () => {
    // Aggregate observed codes across a battery of synthetic inputs. If a
    // code fails to appear, we have dead-code or an unreachable branch.
    const observed = new Set<string>();
    const built = buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);

    // 1. state_unsupported_legacy_v1
    let r: BundleClassification = classifyStateBundleV2({
      entryListing: [{ name: 'manifest.json', isRegularFile: true }],
      manifestBytes: enc.encode('{"version": 1}'),
      ledgerBytes: undefined,
      providerRunMetadataBytes: undefined,
    });
    if (r.kind === 'unsupported_legacy_v1') observed.add(r.diagnostic);

    function addInvalid(res: BundleClassification): void {
      if (res.kind === 'invalid') observed.add(res.diagnostic);
    }

    // 2. bundle_path_unsafe
    addInvalid(
      classifyStateBundleV2({
        entryListing: [{ name: 'manifest.json', isRegularFile: false }],
        manifestBytes: built.manifestBytes,
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 3. bundle_extra_entry
    addInvalid(
      classifyStateBundleV2({
        entryListing: [...fullListing(), { name: 'stray.txt', isRegularFile: true }],
        manifestBytes: built.manifestBytes,
        ledgerBytes: built.ledgerBytes,
        providerRunMetadataBytes: built.providerRunMetadataBytes,
      }),
    );

    // 4. bundle_listing_mismatch
    addInvalid(
      classifyStateBundleV2({
        entryListing: [
          { name: 'manifest.json', isRegularFile: true },
          { name: 'manifest.json', isRegularFile: true },
        ],
        manifestBytes: built.manifestBytes,
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 5. manifest_missing
    addInvalid(
      classifyStateBundleV2({
        entryListing: [],
        manifestBytes: undefined,
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 6. manifest_byte_limit_exceeded
    addInvalid(
      classifyStateBundleV2({
        entryListing: [{ name: 'manifest.json', isRegularFile: true }],
        manifestBytes: new Uint8Array(65537),
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 7. manifest_invalid_json
    addInvalid(
      classifyStateBundleV2({
        entryListing: [{ name: 'manifest.json', isRegularFile: true }],
        manifestBytes: enc.encode('{not json'),
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 8. manifest_unknown_version
    addInvalid(
      classifyStateBundleV2({
        entryListing: [{ name: 'manifest.json', isRegularFile: true }],
        manifestBytes: enc.encode('{"version": 3}'),
        ledgerBytes: undefined,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 9. manifest_unknown_field
    {
      const parsed = JSON.parse(new TextDecoder().decode(built.manifestBytes)) as Record<
        string,
        unknown
      >;
      parsed.extra = 'nope';
      addInvalid(
        classifyStateBundleV2({
          entryListing: fullListing(),
          manifestBytes: enc.encode(JSON.stringify(parsed)),
          ledgerBytes: built.ledgerBytes,
          providerRunMetadataBytes: built.providerRunMetadataBytes,
        }),
      );
    }

    // 10. manifest_shape_invalid
    {
      const parsed = JSON.parse(new TextDecoder().decode(built.manifestBytes)) as {
        transaction: { candidateLedgerSha256: string };
      };
      parsed.transaction.candidateLedgerSha256 = 'f'.repeat(64);
      addInvalid(
        classifyStateBundleV2({
          entryListing: fullListing(),
          manifestBytes: enc.encode(JSON.stringify(parsed)),
          ledgerBytes: built.ledgerBytes,
          providerRunMetadataBytes: built.providerRunMetadataBytes,
        }),
      );
    }

    // 11. ledger_missing
    addInvalid(
      classifyStateBundleV2({
        entryListing: [
          { name: 'manifest.json', isRegularFile: true },
          { name: 'provider-run-metadata.json', isRegularFile: true },
        ],
        manifestBytes: built.manifestBytes,
        ledgerBytes: undefined,
        providerRunMetadataBytes: built.providerRunMetadataBytes,
      }),
    );

    // 12. ledger_byte_limit_exceeded — manifest is valid, caller passes oversized ledger buffer.
    addInvalid(
      classifyStateBundleV2({
        entryListing: fullListing(),
        manifestBytes: built.manifestBytes,
        ledgerBytes: new Uint8Array(LEDGER_MAX_BYTES + 1),
        providerRunMetadataBytes: built.providerRunMetadataBytes,
      }),
    );

    // 13. ledger_bytes_mismatch
    addInvalid(
      classifyStateBundleV2({
        entryListing: fullListing(),
        manifestBytes: built.manifestBytes,
        ledgerBytes: built.ledgerBytes.slice(0, built.ledgerBytes.byteLength - 1),
        providerRunMetadataBytes: built.providerRunMetadataBytes,
      }),
    );

    // 14. ledger_hash_mismatch
    {
      const tampered = new Uint8Array(built.ledgerBytes);
      tampered[0] ^= 0xff;
      addInvalid(
        classifyStateBundleV2({
          entryListing: fullListing(),
          manifestBytes: built.manifestBytes,
          ledgerBytes: tampered,
          providerRunMetadataBytes: built.providerRunMetadataBytes,
        }),
      );
    }

    // 15. provider_run_metadata_missing
    addInvalid(
      classifyStateBundleV2({
        entryListing: [
          { name: 'manifest.json', isRegularFile: true },
          { name: 'ledger.json', isRegularFile: true },
        ],
        manifestBytes: built.manifestBytes,
        ledgerBytes: built.ledgerBytes,
        providerRunMetadataBytes: undefined,
      }),
    );

    // 16. provider_run_metadata_byte_limit_exceeded — manifest valid, caller passes oversized metadata.
    addInvalid(
      classifyStateBundleV2({
        entryListing: fullListing(),
        manifestBytes: built.manifestBytes,
        ledgerBytes: built.ledgerBytes,
        providerRunMetadataBytes: new Uint8Array(METADATA_MAX_BYTES + 1),
      }),
    );

    // 17. provider_run_metadata_bytes_mismatch
    addInvalid(
      classifyStateBundleV2({
        entryListing: fullListing(),
        manifestBytes: built.manifestBytes,
        ledgerBytes: built.ledgerBytes,
        providerRunMetadataBytes: built.providerRunMetadataBytes.slice(
          0,
          built.providerRunMetadataBytes.byteLength - 1,
        ),
      }),
    );

    // 18. provider_run_metadata_hash_mismatch
    {
      const tampered = new Uint8Array(built.providerRunMetadataBytes);
      tampered[0] ^= 0x55;
      addInvalid(
        classifyStateBundleV2({
          entryListing: fullListing(),
          manifestBytes: built.manifestBytes,
          ledgerBytes: built.ledgerBytes,
          providerRunMetadataBytes: tampered,
        }),
      );
    }

    const expected = [
      'state_unsupported_legacy_v1',
      'bundle_path_unsafe',
      'bundle_extra_entry',
      'bundle_listing_mismatch',
      'manifest_missing',
      'manifest_byte_limit_exceeded',
      'manifest_invalid_json',
      'manifest_unknown_version',
      'manifest_unknown_field',
      'manifest_shape_invalid',
      'ledger_missing',
      'ledger_byte_limit_exceeded',
      'ledger_bytes_mismatch',
      'ledger_hash_mismatch',
      'provider_run_metadata_missing',
      'provider_run_metadata_byte_limit_exceeded',
      'provider_run_metadata_bytes_mismatch',
      'provider_run_metadata_hash_mismatch',
    ];
    for (const code of expected) {
      expect([code, [...observed]]).toEqual([code, expect.arrayContaining([code])]);
    }
  });
});
