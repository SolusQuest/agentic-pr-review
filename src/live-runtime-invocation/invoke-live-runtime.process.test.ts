import { EventEmitter } from 'node:events';
import type { ChildProcess } from 'node:child_process';
import { describe, expect, it } from 'vitest';
import { runProcess } from './invoke-live-runtime.js';

class FakeChild extends EventEmitter {
  readonly stdout = new EventEmitter();
  readonly stderr = new EventEmitter();
  exitCode: number | null = null;
  signalCode: NodeJS.Signals | null = null;

  kill(): boolean {
    return true;
  }
}

function start(fake: FakeChild, timeoutMs: number, signal?: AbortSignal) {
  const promise = runProcess(
    { executablePath: '/trusted/runtime' },
    [],
    timeoutMs,
    signal,
    '/private/invocation',
    (() => fake as unknown as ChildProcess) as never,
  );
  queueMicrotask(() => fake.emit('spawn'));
  return promise;
}

describe('live runtime process terminal ordering', () => {
  it('keeps natural exit when abort arrives before close', async () => {
    const fake = new FakeChild();
    const controller = new AbortController();
    const resultPromise = start(fake, 1_000, controller.signal);
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.exitCode = 0;
    fake.emit('exit', 0, null);
    controller.abort();
    fake.emit('close', 0, null);

    await expect(resultPromise).resolves.toMatchObject({ exitCode: 0 });
  });

  it('keeps natural exit when timeout arrives before close', async () => {
    const fake = new FakeChild();
    const resultPromise = start(fake, 10);
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.exitCode = 0;
    fake.emit('exit', 0, null);
    await new Promise((resolve) => setTimeout(resolve, 25));
    fake.emit('close', 0, null);

    await expect(resultPromise).resolves.toMatchObject({ exitCode: 0 });
  });
});
