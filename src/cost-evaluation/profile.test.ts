import { describe, expect, it } from 'vitest';
import {
  DEFAULT_PROFILE,
  DEFAULT_PROFILE_INPUT,
  FROZEN_EPSILON,
  THRESHOLD_CONTRACT_REF,
  parseProfile,
  resolveProfile,
} from './profile.js';

describe('DEFAULT_PROFILE', () => {
  it('resolves with canonical weights, frozen epsilon/ref, and a 64-hex digest', () => {
    expect(DEFAULT_PROFILE.profile).toEqual({
      schemaVersion: 1,
      profileVersion: 'm4-cost-evaluation-default-v1',
      uncachedWeight: '1',
      cacheWriteWeight: '1.25',
      cacheReadWeight: '0.01',
      outputWeight: '2',
      epsilon: '0.01',
      thresholdContractRef: THRESHOLD_CONTRACT_REF,
    });
    expect(DEFAULT_PROFILE.profile.epsilon).toBe(FROZEN_EPSILON);
    expect(DEFAULT_PROFILE.weights).toEqual({
      uncached: 1_000_000_000n,
      cacheWrite: 1_250_000_000n,
      cacheRead: 10_000_000n,
      output: 2_000_000_000n,
    });
    expect(DEFAULT_PROFILE.digest).toMatch(/^[0-9a-f]{64}$/);
  });
});

describe('resolveProfile / parseProfile', () => {
  it('round-trips the default profile through parseProfile with the same digest', () => {
    const parsed = parseProfile(DEFAULT_PROFILE.profile);
    expect(parsed.ok).toBe(true);
    if (parsed.ok) {
      expect(parsed.resolved.profile).toEqual(DEFAULT_PROFILE.profile);
      expect(parsed.resolved.digest).toBe(DEFAULT_PROFILE.digest);
    }
  });

  it('is deterministic: identical input yields identical digest', () => {
    const a = resolveProfile(DEFAULT_PROFILE_INPUT);
    const b = resolveProfile(DEFAULT_PROFILE_INPUT);
    expect(a.ok).toBe(true);
    expect(b.ok).toBe(true);
    if (a.ok && b.ok) expect(a.resolved.digest).toBe(b.resolved.digest);
  });

  it('canonicalizes trailing zeros in weights', () => {
    const r = resolveProfile({
      ...DEFAULT_PROFILE_INPUT,
      uncachedWeight: '1.000000000',
      cacheWriteWeight: '1.250',
      outputWeight: '2.0',
    });
    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.resolved.profile.uncachedWeight).toBe('1');
      expect(r.resolved.profile.cacheWriteWeight).toBe('1.25');
      expect(r.resolved.profile.outputWeight).toBe('2');
    }
  });

  it('accepts null outputWeight (total cost not evaluated)', () => {
    const r = resolveProfile({ ...DEFAULT_PROFILE_INPUT, outputWeight: null });
    expect(r.ok).toBe(true);
    if (r.ok) {
      expect(r.resolved.profile.outputWeight).toBeNull();
      expect(r.resolved.weights.output).toBeNull();
    }
  });

  it('rejects weight grammar violations', () => {
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, uncachedWeight: '1.' }).ok).toBe(false);
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, cacheReadWeight: '.5' }).ok).toBe(false);
  });

  it('rejects weights exceeding the value cap (1000)', () => {
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, uncachedWeight: '1001' }).ok).toBe(false);
  });

  it('rejects when no input weight is positive', () => {
    const r = resolveProfile({
      ...DEFAULT_PROFILE_INPUT,
      uncachedWeight: '0',
      cacheWriteWeight: '0',
      cacheReadWeight: '0',
    });
    expect(r).toMatchObject({ ok: false, reason: 'no_positive_input_weight' });
  });

  it('rejects epsilon != frozen 0.01', () => {
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, epsilon: '0.02' }).ok).toBe(false);
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, epsilon: '0.010' }).ok).toBe(true); // canonicalizes to 0.01
  });

  it('rejects invalid thresholdContractRef', () => {
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, thresholdContractRef: '' }).ok).toBe(false);
    expect(resolveProfile({ ...DEFAULT_PROFILE_INPUT, thresholdContractRef: 'has space' }).ok).toBe(
      false,
    );
  });

  it('rejects a syntax-valid but non-frozen thresholdContractRef', () => {
    expect(
      resolveProfile({ ...DEFAULT_PROFILE_INPUT, thresholdContractRef: 'm4-other-thresholds-v1' })
        .ok,
    ).toBe(false);
  });

  it('outputWeight null vs "0" produce different digests (null != zero)', () => {
    // null = "total cost not evaluated"; "0" = "output weight is zero". These
    // are semantically distinct and must hash to different profile digests.
    const absent = resolveProfile({ ...DEFAULT_PROFILE_INPUT, outputWeight: null });
    const zero = resolveProfile({ ...DEFAULT_PROFILE_INPUT, outputWeight: '0' });
    expect(absent.ok).toBe(true);
    expect(zero.ok).toBe(true);
    if (absent.ok && zero.ok) {
      expect(absent.resolved.digest).not.toBe(zero.resolved.digest);
      expect(absent.resolved.weights.output).toBeNull();
      expect(zero.resolved.weights.output).toBe(0n);
    }
  });

  it('parseProfile rejects schema violations', () => {
    expect(parseProfile({ ...DEFAULT_PROFILE.profile, schemaVersion: 2 }).ok).toBe(false);
    expect(parseProfile({ uncachedWeight: '1' }).ok).toBe(false); // missing fields
    expect(parseProfile('not-an-object').ok).toBe(false);
  });

  it('parseProfile is fail-closed: unknown fields are rejected, not silently dropped', () => {
    // An identity-bearing versioned profile must not let an unknown field escape
    // the semantic digest. Two profiles that differ only by an extra field must
    // NOT resolve to the same canonical profile/digest - the extra is rejected.
    const withExtra = parseProfile({
      ...DEFAULT_PROFILE.profile,
      futureThresholdOverride: '2.0',
    });
    expect(withExtra.ok).toBe(false);
    if (!withExtra.ok) {
      expect(withExtra.errors).toContain('futureThresholdOverride');
    }
  });
});
