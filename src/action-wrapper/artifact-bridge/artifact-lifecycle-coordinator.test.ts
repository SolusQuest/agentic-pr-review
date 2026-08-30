import { describe, expect, it } from 'vitest';

import { ArtifactLifecycleCoordinator } from './artifact-lifecycle-coordinator.js';

describe('artifact lifecycle coordinator', () => {
  it('keeps lookup-through-commit work in one FIFO lane', async () => {
    const coordinator = new ArtifactLifecycleCoordinator();
    const events: string[] = [];
    let releaseFirst: () => void = () => undefined;
    const first = coordinator.run(new AbortController().signal, async () => {
      events.push('first:start');
      await new Promise<void>((resolve) => {
        releaseFirst = resolve;
      });
      events.push('first:commit');
    });
    const second = coordinator.run(new AbortController().signal, async () => {
      events.push('second:lookup');
      events.push('second:commit');
    });

    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(events).toEqual(['first:start']);
    releaseFirst();
    await Promise.all([first, second]);

    expect(events).toEqual(['first:start', 'first:commit', 'second:lookup', 'second:commit']);
  });

  it('removes an aborted waiter without allowing it to stall later work', async () => {
    const coordinator = new ArtifactLifecycleCoordinator();
    let releaseFirst: () => void = () => undefined;
    const first = coordinator.run(new AbortController().signal, async () => {
      await new Promise<void>((resolve) => {
        releaseFirst = resolve;
      });
    });
    const aborted = new AbortController();
    const cancelled = coordinator.run(aborted.signal, async () => undefined);
    const third = coordinator.run(new AbortController().signal, async () => 'third');
    aborted.abort();
    await expect(cancelled).rejects.toMatchObject({ name: 'AbortError' });
    releaseFirst();

    await expect(Promise.all([first, third])).resolves.toEqual([undefined, 'third']);
  });

  it('stops intake, drains committed work, and never starts an already queued later epoch', async () => {
    const coordinator = new ArtifactLifecycleCoordinator();
    const events: string[] = [];
    let releaseFirst: () => void = () => undefined;
    const first = coordinator.run(new AbortController().signal, async () => {
      events.push('first:start');
      await new Promise<void>((resolve) => {
        releaseFirst = resolve;
      });
      events.push('first:commit');
    });
    const queued = coordinator.run(new AbortController().signal, async () => {
      events.push('queued:must-not-start');
    });
    void queued.catch(() => undefined);

    await new Promise<void>((resolve) => setImmediate(resolve));
    expect(events).toEqual(['first:start']);
    coordinator.stopIntake();
    const drained = coordinator.drain();
    releaseFirst();
    await first;
    await expect(queued).rejects.toMatchObject({
      name: 'ArtifactLifecycleCoordinatorStoppedError',
    });
    await drained;

    expect(events).toEqual(['first:start', 'first:commit']);
    await expect(
      coordinator.run(new AbortController().signal, async () => undefined),
    ).rejects.toMatchObject({ name: 'ArtifactLifecycleCoordinatorStoppedError' });
  });
});
