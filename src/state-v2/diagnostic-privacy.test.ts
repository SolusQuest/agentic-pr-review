import { describe, expect, it } from 'vitest';
import {
  classifyStateBundleV2,
  serializeStateManifestV2,
  validateStateManifestV2,
  type EntryDescriptor,
} from './index.js';
import { buildStateBundleV2 } from './index.js';
import { makeStateManifestV2Input } from './test-helpers.js';

const LEDGER = new TextEncoder().encode('ledger');
const METADATA = new TextEncoder().encode('metadata');

function buildValid() {
  return buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);
}

const FULL_LISTING: EntryDescriptor[] = [
  { name: 'manifest.json', isRegularFile: true },
  { name: 'ledger.json', isRegularFile: true },
  { name: 'provider-run-metadata.json', isRegularFile: true },
];

describe('diagnostic privacy (blocker #2)', () => {
  it('validator does not echo unknown-property names', () => {
    const manifest = JSON.parse(new TextDecoder().decode(buildValid().manifestBytes));
    manifest.attackerControlledProperty = 'secret-value';
    manifest.__proto_pollute__ = 'x';
    const result = validateStateManifestV2(manifest);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.message).not.toContain('attackerControlledProperty');
      expect(result.message).not.toContain('__proto_pollute__');
      expect(result.message).not.toContain('secret-value');
      // Fixed code appears instead.
      expect(result.message).toContain('x_invalid_field:');
      expect(result.message).toContain('<untrusted-property>');
    }
  });

  it('validator does not echo type-mismatch offending values', () => {
    const manifest = JSON.parse(new TextDecoder().decode(buildValid().manifestBytes));
    manifest.generation.stateGeneration = 'attacker-controlled-string';
    const result = validateStateManifestV2(manifest);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.message).not.toContain('attacker-controlled-string');
      expect(result.message).toContain('x_invalid_field:/generation/stateGeneration');
    }
  });

  it('classifier does not echo attacker-controlled extra entry names', () => {
    const built = buildValid();
    const listing: EntryDescriptor[] = [
      ...FULL_LISTING,
      { name: '../../etc/passwd', isRegularFile: true },
      { name: 'proprietary-secrets.dat', isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      entryListing: listing,
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.message).not.toContain('etc/passwd');
      expect(result.message).not.toContain('proprietary');
      expect(result.message).not.toContain('..');
    }
  });

  it('classifier does not echo hash values on integrity mismatch', () => {
    const built = buildValid();
    // Corrupt the ledger bytes so the sha256 check fails.
    const corruptLedger = new Uint8Array(built.ledgerBytes);
    corruptLedger[0] = corruptLedger[0] ^ 0xff;
    const result = classifyStateBundleV2({
      entryListing: FULL_LISTING,
      manifestBytes: built.manifestBytes,
      ledgerBytes: corruptLedger,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('ledger_hash_mismatch');
      // Neither the observed nor the expected hex hash appears in the
      // message; only a fixed structural label does.
      expect(result.message).not.toMatch(/[0-9a-f]{16}/i);
      expect(result.message).toContain('ledger_sha256_disagrees_with_descriptor');
    }
  });

  it('classifier does not echo caller JSON key names on duplicate-key parse error', () => {
    const built = buildValid();
    // Hand-craft a manifest with a duplicate key. JSON.parse itself accepts
    // duplicate keys, but strictParseJson rejects them.
    const text = new TextDecoder().decode(built.manifestBytes);
    const injected = text.replace(
      /"version"\s*:\s*2/,
      '"version":2,"attackerControlledKey":1,"attackerControlledKey":2',
    );
    const injectedBytes = new TextEncoder().encode(injected);
    const result = classifyStateBundleV2({
      entryListing: FULL_LISTING,
      manifestBytes: injectedBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('invalid');
    if (result.kind === 'invalid') {
      expect(result.diagnostic).toBe('manifest_invalid_json');
      expect(result.message).not.toContain('attackerControlledKey');
    }
  });

  it('roundtrips a valid bundle through classify+serialize', () => {
    const built = buildValid();
    const result = classifyStateBundleV2({
      entryListing: FULL_LISTING,
      manifestBytes: built.manifestBytes,
      ledgerBytes: built.ledgerBytes,
      providerRunMetadataBytes: built.providerRunMetadataBytes,
    });
    expect(result.kind).toBe('valid');
    if (result.kind === 'valid') {
      expect(serializeStateManifestV2(result.manifest)).toEqual(built.manifestBytes);
    }
  });
});
