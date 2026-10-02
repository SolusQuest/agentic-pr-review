import { describe, expect, it } from 'vitest';

import { ArtifactCacheLedger } from './artifact-cache-ledger.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

describe('artifact cache ledger', () => {
  it('admits the fixed 64 MiB maximum and rejects plus one', () => {
    const ledger = new ArtifactCacheLedger();
    expect(ledger.claim(64 * 1024 * 1024, () => {})).toBeDefined();
    expect(ledger.claim(64 * 1024 * 1024 + 1, () => {})).toBeUndefined();
    expect(() => new ArtifactCacheLedger(64 * 1024 * 1024 + 1)).toThrow();
  });

  it('evicts the first carrier-sized record before admitting the second', () => {
    const ledger = new ArtifactCacheLedger();
    const first = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes, 0xa5);
    expect(ledger.claim(first.length, () => first.fill(0))).toBeDefined();
    expect(ledger.claim(first.length, () => {})).toBeDefined();
    expect(first.every((byte) => byte === 0)).toBe(true);
  });
  it('evicts one combined LRU rather than granting each cache a separate cap', () => {
    const ledger = new ArtifactCacheLedger(8);
    const evicted: string[] = [];
    const first = ledger.claim(4, () => evicted.push('conditional'));
    expect(first).toBeDefined();
    ledger.touch(first);
    const second = ledger.claim(4, () => evicted.push('verified'));
    expect(second).toBeDefined();

    ledger.claim(4, () => evicted.push('newest'));

    expect(evicted).toEqual(['conditional']);
  });

  it('runs cleanup callbacks once when the process cache is cleared', () => {
    const ledger = new ArtifactCacheLedger(8);
    let wipes = 0;
    ledger.claim(4, () => {
      wipes += 1;
    });

    ledger.clear();
    ledger.dispose();

    expect(wipes).toBe(1);
  });
});
