import { describe, expect, it } from 'vitest';
import { artifactFamily, physicalArtifactMember, physicalArtifactName } from './physical-name.js';

describe('private physical artifact namespace', () => {
  it('is bounded, opaque and stable for the frozen encrypted record', () => {
    const logical = 's'.repeat(256);
    const first = physicalArtifactName(logical, 'a'.repeat(64));
    expect(Buffer.byteLength(first)).toBe(140);
    expect(first).not.toContain(logical);
    expect(first).toBe(physicalArtifactName(logical, 'a'.repeat(64)));
    expect(first).not.toBe(physicalArtifactName(logical, 'b'.repeat(64)));
    expect(first).not.toBe(physicalArtifactName('other', 'a'.repeat(64)));
    expect(physicalArtifactMember(logical, first)).toBe(true);
  });
  it('requires exact canonical family and digest membership', () => {
    const name = physicalArtifactName('logical', 'a'.repeat(64));
    for (const invalid of [
      name + '0',
      name.slice(0, -1),
      name.toUpperCase(),
      artifactFamily('logical') + 'z'.repeat(64),
      physicalArtifactName('other', 'a'.repeat(64)),
    ]) {
      expect(physicalArtifactMember('logical', invalid)).toBe(false);
    }
    expect(() => physicalArtifactName('logical', 'not-a-digest')).toThrow();
  });
});
