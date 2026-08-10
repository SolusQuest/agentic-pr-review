import { describe, expect, it } from 'vitest';

import { ArtifactBridgeDeadlineError, ArtifactBridgeOperationBudget } from './operation-budget.js';

describe('artifact bridge whole-operation budget', () => {
  it('shares one deadline across elapsed-time checks and caller cancellation', () => {
    let now = 10;
    const caller = new AbortController();
    const budget = new ArtifactBridgeOperationBudget(caller.signal, () => now, now);

    expect(budget.remainingMs()).toBe(120_000);
    now += 119_999;
    expect(budget.remainingMs()).toBe(1);
    now += 1;
    expect(() => budget.throwIfExpired()).toThrow(ArtifactBridgeDeadlineError);
    budget.dispose();

    const cancelled = new AbortController();
    const cancelledBudget = new ArtifactBridgeOperationBudget(cancelled.signal, () => 0, 0);
    cancelled.abort();
    expect(() => cancelledBudget.throwIfExpired()).toThrow(ArtifactBridgeDeadlineError);
    cancelledBudget.dispose();
  });
});
