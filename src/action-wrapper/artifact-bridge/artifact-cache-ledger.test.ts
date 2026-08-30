import { describe, expect, it } from 'vitest';

import { ArtifactCacheLedger } from './artifact-cache-ledger.js';

describe('artifact cache ledger', () => {
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
