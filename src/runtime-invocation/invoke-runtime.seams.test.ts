import { spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { afterEach, describe, expect, it } from 'vitest';
import { RuntimeInvocationError } from './invoke-runtime.js';
import { invokeRuntimeForTests } from './invoke-runtime.test-support.js';
import {
  createTempRootRegistry,
  fakeRuntimePath,
  readBootstrapInput,
  withScenarioEnv,
} from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

describe('invokeRuntime - internal seams (via invokeRuntimeForTests)', () => {
  it('surfaces mkdtemp failure as host-io-failed', async () => {
    const bootError = new Error('boom');
    (bootError as NodeJS.ErrnoException).code = 'EACCES';
    await expect(
      invokeRuntimeForTests(
        {
          command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
          input: readBootstrapInput(),
          timeoutMs: 1000,
        },
        {
          fs: {
            mkdtemp: async () => {
              throw bootError;
            },
          },
        },
      ),
    ).rejects.toMatchObject({ kind: 'host-io-failed' });
  }, 8000);
  it('surfaces cleanup-failed on success when rm throws', async () => {
    const tempRoot = await registry.acquire();
    const err = await invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 15_000,
        tempRoot,
      },
      {
        spawnOverride: withScenarioEnv('success', undefined, undefined),
        fs: {
          rm: async () => {
            throw new Error('rm blocked');
          },
        },
      },
    ).catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('cleanup-failed');
  }, 8000);
  it('preserves primary error when cleanup fails after failure', async () => {
    const tempRoot = await registry.acquire();
    const err = await invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 15_000,
        tempRoot,
      },
      {
        spawnOverride: withScenarioEnv('exit-20', undefined, undefined),
        fs: {
          rm: async () => {
            throw new Error('rm blocked');
          },
        },
      },
    ).catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('runtime-exit');
    expect((err as RuntimeInvocationError).exitClass).toBe('runtime');
  }, 8000);
  it('does not spawn when the signal aborts during input write', async () => {
    let spawnCalled = false;
    const spawnOverride = ((..._args: unknown[]) => {
      spawnCalled = true;
      throw new Error('should not spawn');
    }) as unknown as typeof spawn;
    const ctrl = new AbortController();
    let writeStarted = false;
    const err = await invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        tempRoot: await registry.acquire(),
        signal: ctrl.signal,
      },
      {
        spawnOverride,
        fs: {
          writeFile: (async (target: unknown, data: unknown) => {
            writeStarted = true;
            ctrl.abort();
            // Slight delay so the abort races the resolve.
            await new Promise((r) => setTimeout(r, 10));
            const { writeFile } = await import('node:fs/promises');
            return writeFile(target as unknown as string, data as unknown as Uint8Array);
          }) as unknown as (typeof import('node:fs/promises'))['writeFile'],
        },
      },
    ).catch((e) => e);
    expect(writeStarted).toBe(true);
    expect(spawnCalled).toBe(false);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('cancelled');
  }, 8000);

  it('invokes the onBeforeCleanup test hook before rm', async () => {
    const seen: string[] = [];
    await expect(
      invokeRuntimeForTests(
        {
          command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
          input: readBootstrapInput(),
          timeoutMs: 15_000,
          tempRoot: await registry.acquire(),
        },
        {
          spawnOverride: withScenarioEnv('exit-20', undefined, undefined),
          onBeforeCleanup: (dir) => {
            seen.push(dir);
            expect(existsSync(dir)).toBe(true);
          },
        },
      ),
    ).rejects.toMatchObject({ kind: 'runtime-exit' });
    expect(seen.length).toBe(1);
    expect(existsSync(seen[0]!)).toBe(false);
  }, 8000);
});
