import { setTimeout as delay } from 'node:timers/promises';
import { describe, expect, it } from 'vitest';

import {
  OfficialCallTimeoutError,
  runContainedOfficialCall,
} from '../artifact-bridge/official-output.js';
import { OfficialCallTracker } from './official-calls.js';

describe('W1 official SDK quiescence', () => {
  it('does not report quiescence until a timed-out S2 call settles and restores writers', async () => {
    const tracker = new OfficialCallTracker();
    let release!: () => void;
    const pending = new Promise<string>((resolve) => {
      release = () => resolve('done');
    });
    const client = tracker.wrap({ call: async () => await pending });
    await expect(
      runContainedOfficialCall(() => client.call(), 10, new AbortController().signal),
    ).rejects.toBeInstanceOf(OfficialCallTimeoutError);

    let quiescent = false;
    const waiting = tracker.awaitQuiescence(1_000).then((value) => {
      quiescent = value;
      return value;
    });
    await delay(20);
    expect(quiescent).toBe(false);
    release();
    await expect(waiting).resolves.toBe(true);
  });

  it('returns false at the bound and never restores contained writers itself', async () => {
    const tracker = new OfficialCallTracker();
    const original = process.stdout.write;
    const client = tracker.wrap({ call: async () => await new Promise<never>(() => undefined) });
    void client.call();
    process.stdout.write = (() => true) as typeof process.stdout.write;
    try {
      await expect(tracker.awaitQuiescence(10)).resolves.toBe(false);
      expect(process.stdout.write).not.toBe(original);
    } finally {
      process.stdout.write = original;
    }
  });

  it('preserves client this-binding and rejects calls after sealing', async () => {
    const tracker = new OfficialCallTracker();
    const client = tracker.wrap({
      value: 7,
      async read() {
        return this.value;
      },
    });
    await expect(client.read()).resolves.toBe(7);
    await expect(tracker.awaitQuiescence(100)).resolves.toBe(true);
    expect(() => client.read()).toThrow('wrapper_official_calls_sealed');
  });

  it('allows every precise local cache operation after sealing', async () => {
    const tracker = new OfficialCallTracker();
    const operations: Array<{ readonly method: string; readonly input?: unknown }> = [];
    const client = tracker.wrap({
      async read() {
        return 'remote';
      },
      invalidateArtifactMutation(input: unknown) {
        operations.push({ method: 'invalidateArtifactMutation', input });
      },
      invalidateArtifactListRepresentation(input: unknown) {
        operations.push({ method: 'invalidateArtifactListRepresentation', input });
      },
      invalidateArtifactRepresentation(input: unknown) {
        operations.push({ method: 'invalidateArtifactRepresentation', input });
      },
      invalidateWorkflowRunAttemptRepresentation(input: unknown) {
        operations.push({ method: 'invalidateWorkflowRunAttemptRepresentation', input });
      },
      dispose() {
        operations.push({ method: 'dispose' });
      },
    });

    await expect(tracker.awaitQuiescence(100)).resolves.toBe(true);
    const target = { owner: 'owner', repo: 'repo', name: 'target', artifact_id: 7 };
    client.invalidateArtifactMutation(target);
    client.invalidateArtifactListRepresentation({ ...target, per_page: 100, page: 2 });
    client.invalidateArtifactRepresentation(target);
    client.invalidateWorkflowRunAttemptRepresentation({
      owner: 'owner',
      repo: 'repo',
      run_id: 9,
      attempt_number: 3,
    });
    client.dispose();

    expect(operations).toEqual([
      { method: 'invalidateArtifactMutation', input: target },
      {
        method: 'invalidateArtifactListRepresentation',
        input: { ...target, per_page: 100, page: 2 },
      },
      { method: 'invalidateArtifactRepresentation', input: target },
      {
        method: 'invalidateWorkflowRunAttemptRepresentation',
        input: { owner: 'owner', repo: 'repo', run_id: 9, attempt_number: 3 },
      },
      { method: 'dispose' },
    ]);
    expect(() => client.read()).toThrow('wrapper_official_calls_sealed');
  });

  it('does not admit a near-miss synchronous method as a local cache operation', () => {
    const tracker = new OfficialCallTracker();
    const client = tracker.wrap({ invalidateArtifactRepresentations: () => undefined });

    expect(() => client.invalidateArtifactRepresentations()).toThrow(
      'wrapper_official_call_invalid',
    );
  });
});
