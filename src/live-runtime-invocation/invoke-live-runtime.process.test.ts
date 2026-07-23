import { EventEmitter } from 'node:events';
import type { ChildProcess } from 'node:child_process';
import { describe, expect, it } from 'vitest';
import { classifyProviderFailure, runProcess } from './invoke-live-runtime.js';

class FakeChild extends EventEmitter {
  readonly stdout = new EventEmitter();
  readonly stderr = new EventEmitter();
  exitCode: number | null = null;
  signalCode: NodeJS.Signals | null = null;

  kill(): boolean {
    return true;
  }
}

function start(
  fake: FakeChild,
  timeoutMs: number,
  signal?: AbortSignal,
  beforeListeners?: () => void,
) {
  const promise = runProcess(
    { executablePath: '/trusted/runtime' },
    [],
    timeoutMs,
    signal,
    '/private/invocation',
    (() => {
      beforeListeners?.();
      return fake as unknown as ChildProcess;
    }) as never,
  );
  queueMicrotask(() => fake.emit('spawn'));
  return promise;
}

describe('live runtime process terminal ordering', () => {
  it('maps only the closed provider diagnostic and exit pairs', () => {
    expect(
      classifyProviderFailure(
        new TextEncoder().encode('APR_PROVIDER_TIMEOUT: Provider invocation failed.\n'),
        30,
      ),
    ).toMatchObject({ kind: 'provider-timeout', exitCode: 30 });
    expect(
      classifyProviderFailure(
        new TextEncoder().encode('APR_PROVIDER_RESPONSE: Provider invocation failed.\n'),
        30,
      ),
    ).toBeUndefined();
    expect(
      classifyProviderFailure(
        new TextEncoder().encode('APR_PROVIDER_RESPONSE: raw provider body'),
        20,
      ),
    ).toBeUndefined();
  });

  it.each(['null', 'aaaa'])('passes the provider key through child env: %s', async (key) => {
    const fake = new FakeChild();
    let childEnv: NodeJS.ProcessEnv | undefined;
    const resultPromise = runProcess(
      { executablePath: '/trusted/runtime' },
      ['review-live'],
      1_000,
      undefined,
      '/private/invocation',
      ((
        _executablePath: string,
        _args: readonly string[],
        options: { env?: NodeJS.ProcessEnv },
      ) => {
        childEnv = (options as { env?: NodeJS.ProcessEnv }).env;
        return fake as unknown as ChildProcess;
      }) as never,
      2_000,
      { AGENTIC_REVIEW_DEEPSEEK_API_KEY: key },
    );
    queueMicrotask(() => fake.emit('spawn'));
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.exitCode = 0;
    fake.emit('exit', 0, null);
    fake.emit('close', 0, null);

    await expect(resultPromise).resolves.toMatchObject({ exitCode: 0 });
    expect(childEnv?.AGENTIC_REVIEW_DEEPSEEK_API_KEY).toBe(key);
  });

  it('cancels when abort occurs during spawn before listener registration', async () => {
    const fake = new FakeChild();
    const controller = new AbortController();
    const resultPromise = start(fake, 1_000, controller.signal, () => controller.abort());
    queueMicrotask(() => fake.emit('close', null, null));

    await expect(resultPromise).rejects.toMatchObject({ kind: 'cancelled' });
  });

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

  it('preserves signal-only natural exits for host termination classification', async () => {
    const fake = new FakeChild();
    const resultPromise = start(fake, 1_000);
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.emit('exit', null, 'SIGKILL');
    fake.emit('close', null, 'SIGKILL');

    await expect(resultPromise).resolves.toMatchObject({ exitCode: null, signal: 'SIGKILL' });
  });

  it('rejects when natural exit is not followed by close before the deadline', async () => {
    const fake = new FakeChild();
    const resultPromise = runProcess(
      { executablePath: '/trusted/runtime' },
      [],
      1_000,
      undefined,
      '/private/invocation',
      (() => fake as unknown as ChildProcess) as never,
      20,
    );
    queueMicrotask(() => fake.emit('spawn'));
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.emit('exit', 0, null);

    await expect(resultPromise).rejects.toMatchObject({
      kind: 'runtime-exit',
      closeObserved: false,
    });
  });

  it('preserves a stream contract failure after natural exit', async () => {
    const fake = new FakeChild();
    const resultPromise = start(fake, 1_000);
    await new Promise<void>((resolve) => queueMicrotask(resolve));
    fake.emit('exit', 0, null);
    fake.stdout.emit('data', Buffer.alloc(1_048_577));
    fake.emit('close', 0, null);

    await expect(resultPromise).rejects.toMatchObject({ kind: 'stream-limit-exceeded' });
  });
});
