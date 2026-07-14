import { describe, expect, it } from 'vitest';
import { validateStateManifestV2, buildStateBundleV2 } from './index.js';
import { makeStateManifestV2Input } from './test-helpers.js';

describe('unknown-property diagnostic classification (blocker #3)', () => {
  it('classifies unknown property with empty-string name as manifest_unknown_field', () => {
    const built = buildStateBundleV2(
      makeStateManifestV2Input(),
      new TextEncoder().encode('l'),
      new TextEncoder().encode('m'),
    );
    const manifest = JSON.parse(new TextDecoder().decode(built.manifestBytes));
    manifest[''] = 'attacker-controlled-value';
    const result = validateStateManifestV2(manifest);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.diagnostic).toBe('manifest_unknown_field');
      expect(result.message).not.toContain('attacker-controlled-value');
    }
  });

  it('classifies empty-string unknown property even when version is wrong', () => {
    const built = buildStateBundleV2(
      makeStateManifestV2Input(),
      new TextEncoder().encode('l'),
      new TextEncoder().encode('m'),
    );
    const manifest = JSON.parse(new TextDecoder().decode(built.manifestBytes));
    manifest.version = 999;
    manifest[''] = 'v';
    const result = validateStateManifestV2(manifest);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      // additionalProperties takes precedence over unknown_version, per
      // fixed diagnostic taxonomy.
      expect(result.diagnostic).toBe('manifest_unknown_field');
    }
  });

  it('still classifies non-empty unknown property names as manifest_unknown_field', () => {
    const built = buildStateBundleV2(
      makeStateManifestV2Input(),
      new TextEncoder().encode('l'),
      new TextEncoder().encode('m'),
    );
    const manifest = JSON.parse(new TextDecoder().decode(built.manifestBytes));
    manifest.leaked_secret_field_name = 'v';
    const result = validateStateManifestV2(manifest);
    expect(result.ok).toBe(false);
    if (!result.ok) {
      expect(result.diagnostic).toBe('manifest_unknown_field');
      expect(result.message).not.toContain('leaked_secret_field_name');
    }
  });
});
