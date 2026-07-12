import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { expect } from 'vitest';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { RuntimeInvocationError, type RuntimeInvocationSuccess } from './invoke-runtime.js';
import {
  invokeRuntimeForTests,
  type RuntimeInvocationTestSeams,
} from './invoke-runtime.test-support.js';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Absolute path to the fake C# runtime replacement used by every scenario in
 * the adapter test suite. Kept in the harness so tests do not each recompute
 * this path.
 */
export const fakeRuntimePath = join(here, '__test-fixtures__', 'fake-runtime.mjs');

/** Private harness detail; the fixtures directory is not part of the exported surface. */
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'v1');

export interface ScenarioOptions {
  scenario: string;
  timeoutMs?: number;
  fakeVersion?: string;
  signal?: AbortSignal;
  extraEnv?: Record<string, string>;
  input?: ReviewInputV1;
  fillerBytes?: number;
  sigtermGraceMs?: number;
}

/**
 * Fresh-parses the bootstrap fixture on every call. Do not cache the return
 * value at module scope; several tests mutate the returned object.
 */
export function readBootstrapInput(): ReviewInputV1 {
  const raw = readFileSync(join(fixturesDir, 'valid-input-bootstrap.json'), 'utf8');
  return JSON.parse(raw) as ReviewInputV1;
}

export interface TempRootRegistry {
  acquire(): Promise<string>;
  cleanup(): Promise<void>;
}

/**
 * Per-test-file registry factory. Each consuming file registers
 * `afterEach(() => registry.cleanup())` itself.
 */
export function createTempRootRegistry(): TempRootRegistry {
  const dirs: string[] = [];
  return {
    async acquire() {
      const dir = await mkdtemp(join(os.tmpdir(), 'runtime-invocation-test-'));
      dirs.push(dir);
      return dir;
    },
    async cleanup() {
      while (dirs.length > 0) {
        const dir = dirs.pop();
        if (!dir) continue;
        try {
          await rm(dir, { recursive: true, force: true });
        } catch {
          // ignore per-directory failures; continue draining
        }
      }
    },
  };
}

export function withScenarioEnv(
  scenario: string,
  fakeVersion: string | undefined,
  extra: Record<string, string> | undefined,
): typeof spawn {
  return ((command: string, args: readonly string[], options: Parameters<typeof spawn>[2]) => {
    const overriddenEnv: NodeJS.ProcessEnv = { ...(options as { env?: NodeJS.ProcessEnv }).env };
    overriddenEnv.FAKE_RUNTIME_SCENARIO = scenario;
    if (fakeVersion !== undefined) overriddenEnv.FAKE_RUNTIME_VERSION = fakeVersion;
    if (extra) Object.assign(overriddenEnv, extra);
    return spawn(command, args as string[], {
      ...(options as object),
      env: overriddenEnv,
    });
  }) as typeof spawn;
}

export async function runScenario(
  opts: ScenarioOptions,
  registry: TempRootRegistry,
): Promise<{ tempRoot: string; invoke: Promise<RuntimeInvocationSuccess> }> {
  const tempRoot = await registry.acquire();
  const input = opts.input ?? readBootstrapInput();
  const extraEnv: Record<string, string> = { ...(opts.extraEnv ?? {}) };
  if (opts.fillerBytes !== undefined) {
    extraEnv.FAKE_RUNTIME_FILLER_BYTES = String(opts.fillerBytes);
  }
  const seams: RuntimeInvocationTestSeams = {
    spawnOverride: withScenarioEnv(opts.scenario, opts.fakeVersion, extraEnv),
  };
  if (opts.sigtermGraceMs !== undefined) seams.sigtermGraceMs = opts.sigtermGraceMs;
  const invoke = invokeRuntimeForTests(
    {
      command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
      input,
      timeoutMs: opts.timeoutMs ?? 15_000,
      tempRoot,
      signal: opts.signal,
    },
    seams,
  );
  return { tempRoot, invoke };
}

export async function expectFailure(
  opts: ScenarioOptions,
  registry: TempRootRegistry,
  expectedKind: string,
): Promise<RuntimeInvocationError> {
  const { invoke } = await runScenario(opts, registry);
  try {
    await invoke;
  } catch (err) {
    if (!(err instanceof RuntimeInvocationError)) throw err;
    expect(err.kind).toBe(expectedKind);
    return err;
  }
  throw new Error(`Expected RuntimeInvocationError with kind=${expectedKind}, got success`);
}
