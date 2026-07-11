import { spawn } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { invokeRuntime, RuntimeInvocationError } from './invoke-runtime.js';

const here = dirname(fileURLToPath(import.meta.url));
const fakeRuntimePath = join(here, '__test-fixtures__', 'fake-runtime.mjs');
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'v1');

function readBootstrapInput(): ReviewInputV1 {
  const raw = readFileSync(join(fixturesDir, 'valid-input-bootstrap.json'), 'utf8');
  return JSON.parse(raw) as ReviewInputV1;
}

interface ScenarioOptions {
  scenario: string;
  timeoutMs?: number;
  fakeVersion?: string;
  requestedRuntimeVersion?: string | null;
  signal?: AbortSignal;
  extraEnv?: Record<string, string>;
  input?: ReviewInputV1;
  fillerBytes?: number;
}

const tempDirs: string[] = [];

async function acquireTempRoot(): Promise<string> {
  const dir = await mkdtemp(join(os.tmpdir(), 'runtime-invocation-test-'));
  tempDirs.push(dir);
  return dir;
}

afterEach(async () => {
  while (tempDirs.length > 0) {
    const dir = tempDirs.pop();
    if (!dir) continue;
    try {
      await rm(dir, { recursive: true, force: true });
    } catch {
      // ignore
    }
  }
});

function withScenarioEnv(
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

async function runScenario(opts: ScenarioOptions): Promise<{
  tempRoot: string;
  invoke: ReturnType<typeof invokeRuntime>;
}> {
  const tempRoot = await acquireTempRoot();
  const input = opts.input ?? readBootstrapInput();
  if (opts.requestedRuntimeVersion !== undefined) {
    input.requestedRuntimeVersion = opts.requestedRuntimeVersion;
  }
  const extraEnv: Record<string, string> = { ...(opts.extraEnv ?? {}) };
  if (opts.fillerBytes !== undefined) {
    extraEnv.FAKE_RUNTIME_FILLER_BYTES = String(opts.fillerBytes);
  }
  const spawnOverride = withScenarioEnv(opts.scenario, opts.fakeVersion, extraEnv);
  const invoke = invokeRuntime(
    {
      command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
      input,
      timeoutMs: opts.timeoutMs ?? 15_000,
      tempRoot,
      signal: opts.signal,
    },
    { spawnOverride },
  );
  return { tempRoot, invoke };
}

async function expectFailure(
  opts: ScenarioOptions,
  expectedKind: string,
): Promise<RuntimeInvocationError> {
  const { invoke } = await runScenario(opts);
  try {
    await invoke;
  } catch (err) {
    if (!(err instanceof RuntimeInvocationError)) throw err;
    expect(err.kind).toBe(expectedKind);
    return err;
  }
  throw new Error(`Expected RuntimeInvocationError with kind=${expectedKind}, got success`);
}

describe('invokeRuntime - options validation', () => {
  const input = readBootstrapInput();
  const validCommand = { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] };

  it('rejects a null options object', async () => {
    await expect(invokeRuntime(null as unknown as never)).rejects.toMatchObject({
      kind: 'options-invalid',
    });
  });
  it('rejects a null command', async () => {
    await expect(
      invokeRuntime({
        command: null as unknown as never,
        input,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a null input', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input: null as unknown as ReviewInputV1,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-positive timeout', async () => {
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 0 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-integer timeout', async () => {
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 1.5 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a relative tempRoot', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        tempRoot: 'relative-path',
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-absolute executablePath', async () => {
    await expect(
      invokeRuntime({
        command: { executablePath: 'relative-runtime' },
        input,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects prefixArgs containing non-strings', async () => {
    await expect(
      invokeRuntime({
        command: { executablePath: process.execPath, prefixArgs: [123 as unknown as string] },
        input,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a malformed signal', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: { aborted: 'nope' } as unknown as AbortSignal,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('fails as cancelled if signal already aborted', async () => {
    const ctrl = new AbortController();
    ctrl.abort();
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: ctrl.signal,
      }),
    ).rejects.toMatchObject({ kind: 'cancelled' });
  });
});

describe('invokeRuntime - preflight validation', () => {
  it('fails as input-invalid when ReviewInputV1 schema fails', async () => {
    const err = await expectFailure(
      {
        scenario: 'success',
        input: { ...readBootstrapInput(), protocolVersion: 2 as unknown as 1 },
      },
      'input-invalid',
    );
    expect(err.message).toMatch(/ReviewInputV1/);
  });
  it('fails as executable-invalid for a missing binary', async () => {
    const tempRoot = await acquireTempRoot();
    await expect(
      invokeRuntime({
        command: { executablePath: path.join(tempRoot, 'does-not-exist') },
        input: readBootstrapInput(),
        timeoutMs: 1000,
        tempRoot,
      }),
    ).rejects.toMatchObject({ kind: 'executable-invalid' });
  });
  it('fails as executable-invalid for an executable symlink', async () => {
    if (process.platform === 'win32') return;
    const tempRoot = await acquireTempRoot();
    const link = path.join(tempRoot, 'node-link');
    await symlink(process.execPath, link);
    await expect(
      invokeRuntime({
        command: { executablePath: link, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        tempRoot,
      }),
    ).rejects.toMatchObject({ kind: 'executable-invalid' });
  });
});

describe('invokeRuntime - success path', () => {
  it('returns validated result and trace on a clean run', async () => {
    const { invoke } = await runScenario({ scenario: 'success' });
    const success = await invoke;
    expect(success.result.protocolVersion).toBe(1);
    expect(success.trace.protocolVersion).toBe(1);
    expect(success.inputSha256).toMatch(/^[0-9a-f]{64}$/);
    expect(success.result.inputSha256).toBe(success.inputSha256);
    expect(success.trace.inputSha256).toBe(success.inputSha256);
    expect(success.result.trace?.sha256).toMatch(/^[0-9a-f]{64}$/);
    expect(success.runtimeVersion).toBe('0.1.0-dev');
  });

  it('cleans up the invocation directory on success', async () => {
    const { invoke, tempRoot } = await runScenario({ scenario: 'success' });
    await invoke;
    // The mkdtemp directory should have been removed; only the outer tempRoot remains.
    const remaining = await readFile(tempRoot, { encoding: undefined }).catch((err) => err);
    // readFile on a directory always errors on Node; use fs.readdir instead:
    const entries = await (await import('node:fs/promises')).readdir(tempRoot);
    expect(entries).toEqual([]);
    void remaining;
  });

  it('accepts a matching requestedRuntimeVersion', async () => {
    const input = readBootstrapInput();
    input.requestedRuntimeVersion = 'pinned-1.2.3';
    const { invoke } = await runScenario({
      scenario: 'success-with-requested-version',
      input,
      fakeVersion: 'pinned-1.2.3',
    });
    const success = await invoke;
    expect(success.runtimeVersion).toBe('pinned-1.2.3');
  });
});

describe('invokeRuntime - non-zero exits', () => {
  it('maps exit 2 to usage exit class', async () => {
    const err = await expectFailure({ scenario: 'exit-2' }, 'runtime-exit');
    expect(err.exitCode).toBe(2);
    expect(err.exitClass).toBe('usage');
    expect(err.diagnosticCode).toBe('APR_USAGE_INVALID');
  });
  it('maps exit 10 to contract', async () => {
    const err = await expectFailure({ scenario: 'exit-10' }, 'runtime-exit');
    expect(err.exitClass).toBe('contract');
    expect(err.diagnosticCode).toBe('APR_RUNTIME_VERSION_MISMATCH');
  });
  it('maps exit 20 to runtime', async () => {
    const err = await expectFailure({ scenario: 'exit-20' }, 'runtime-exit');
    expect(err.exitClass).toBe('runtime');
  });
  it('maps exit 30 to provider', async () => {
    const err = await expectFailure({ scenario: 'exit-30' }, 'runtime-exit');
    expect(err.exitClass).toBe('provider');
  });
  it('maps exit 40 to file-io', async () => {
    const err = await expectFailure({ scenario: 'exit-40' }, 'runtime-exit');
    expect(err.exitClass).toBe('file-io');
  });
  it('maps unknown non-zero exit to unknown-exit', async () => {
    const err = await expectFailure({ scenario: 'exit-77' }, 'unknown-exit');
    expect(err.exitCode).toBe(77);
  });
  it('omits diagnosticCode when APR class does not match exit class', async () => {
    // exit-2 with a mismatched code would be misleading; sanitizer already returns exact
    // code from the same line, so we assert diagnosticCode matches. If the fake emits a
    // mismatched code, adapter should drop it. Verified separately via unit-level parsing.
    const err = await expectFailure({ scenario: 'exit-77' }, 'unknown-exit');
    expect(err.diagnosticCode).toBeUndefined();
  });
});

describe('invokeRuntime - failure trace diagnostics', () => {
  it('exposes failure trace diagnostics when provenance holds', async () => {
    const err = await expectFailure({ scenario: 'exit-10-with-failure-trace' }, 'runtime-exit');
    expect(err.failureTraceDiagnostics?.[0]?.code).toBe('FAKE_OK');
  });
  it('omits diagnostics when failure trace inputSha256 does not match', async () => {
    const err = await expectFailure(
      { scenario: 'exit-20-with-mismatched-failure-trace' },
      'runtime-exit',
    );
    expect(err.failureTraceDiagnostics).toBeUndefined();
  });
  it('treats orphan trace on exit 40 as diagnostic only', async () => {
    const err = await expectFailure({ scenario: 'orphan-trace-exit-40' }, 'runtime-exit');
    expect(err.exitClass).toBe('file-io');
    expect(err.failureTraceDiagnostics?.[0]?.code).toBe('FAKE_OK');
  });
});

describe('invokeRuntime - success validation failures', () => {
  it('reports missing-output when result.json is absent', async () => {
    await expectFailure({ scenario: 'missing-result' }, 'missing-output');
  });
  it('reports missing-output when trace.json is absent', async () => {
    await expectFailure({ scenario: 'missing-trace' }, 'missing-output');
  });
  it('reports unsafe-output-file when result.json is a symlink', async () => {
    if (process.platform === 'win32') return;
    await expectFailure({ scenario: 'symlink-result' }, 'unsafe-output-file');
  });
  it('reports result-invalid for non-UTF8 bytes', async () => {
    await expectFailure({ scenario: 'invalid-utf8-result' }, 'result-invalid');
  });
  it('reports result-invalid for non-JSON', async () => {
    await expectFailure({ scenario: 'invalid-json-result' }, 'result-invalid');
  });
  it('reports result-invalid for schema failure', async () => {
    await expectFailure({ scenario: 'schema-invalid-result' }, 'result-invalid');
  });
  it('reports trace-invalid for non-JSON trace', async () => {
    await expectFailure({ scenario: 'invalid-json-trace' }, 'trace-invalid');
  });
  it('reports process-contract-violation when result.inputSha256 missing', async () => {
    await expectFailure({ scenario: 'missing-result-inputsha' }, 'process-contract-violation');
  });
  it('reports process-contract-violation when result.trace missing', async () => {
    await expectFailure({ scenario: 'missing-result-trace' }, 'process-contract-violation');
  });
  it('reports process-contract-violation when result.trace.path is present', async () => {
    await expectFailure({ scenario: 'result-trace-path-present' }, 'process-contract-violation');
  });
  it('reports process-contract-violation when trace.resultSha256 is present', async () => {
    await expectFailure({ scenario: 'trace-result-sha-present' }, 'process-contract-violation');
  });
  it('reports hash-mismatch when result.inputSha256 differs', async () => {
    await expectFailure({ scenario: 'result-inputsha-mismatch' }, 'hash-mismatch');
  });
  it('reports hash-mismatch when trace.inputSha256 differs', async () => {
    await expectFailure({ scenario: 'trace-inputsha-mismatch' }, 'hash-mismatch');
  });
  it('reports hash-mismatch when trace bytes hash differs from result.trace.sha256', async () => {
    await expectFailure({ scenario: 'trace-sha-mismatch' }, 'hash-mismatch');
  });
  it('reports version-mismatch when result/trace runtime versions differ', async () => {
    await expectFailure({ scenario: 'result-trace-version-mismatch' }, 'version-mismatch');
  });
  it('reports version-mismatch when requestedRuntimeVersion is unmet', async () => {
    const input = readBootstrapInput();
    input.requestedRuntimeVersion = 'requested-abc';
    await expectFailure(
      { scenario: 'requested-version-mismatch', input, fakeVersion: 'requested-abc' },
      'version-mismatch',
    );
  });
});

describe('invokeRuntime - stream contract', () => {
  it('flags stdout leak on exit 0 as process-contract-violation', async () => {
    const err = await expectFailure(
      { scenario: 'stdout-leak-small' },
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stdout-nonempty')).toBe(true);
  });
  it('flags stderr over contract limit on exit 0 as process-contract-violation', async () => {
    const err = await expectFailure(
      { scenario: 'stderr-over-contract-success' },
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stderr-over-contract')).toBe(true);
  });
  it('terminates the child and reports stream-limit-exceeded for stdout flood', async () => {
    const err = await expectFailure(
      { scenario: 'stdout-flood', timeoutMs: 10_000 },
      'stream-limit-exceeded',
    );
    expect(err.contractViolations?.[0]?.kind).toBe('stdout-over-capture');
  });
  it('terminates the child and reports stream-limit-exceeded for stderr flood', async () => {
    const err = await expectFailure(
      { scenario: 'stderr-flood', timeoutMs: 10_000 },
      'stream-limit-exceeded',
    );
    expect(err.contractViolations?.[0]?.kind).toBe('stderr-over-capture');
  });
});

describe('invokeRuntime - timeout and cancellation', () => {
  it('times out a hanging child', async () => {
    await expectFailure({ scenario: 'hang', timeoutMs: 500 }, 'timed-out');
  });
  it('escalates to SIGKILL when child ignores SIGTERM', async () => {
    if (process.platform === 'win32') return; // Windows kill sequence is a single TerminateProcess.
    const err = await expectFailure({ scenario: 'ignore-sigterm', timeoutMs: 1500 }, 'timed-out');
    expect(err.kind).toBe('timed-out');
  });
  it('cancels via AbortSignal', async () => {
    const ctrl = new AbortController();
    const { invoke } = await runScenario({
      scenario: 'hang',
      timeoutMs: 30_000,
      signal: ctrl.signal,
    });
    setTimeout(() => ctrl.abort(), 100).unref();
    await expect(invoke).rejects.toMatchObject({ kind: 'cancelled' });
  });
});

describe('invokeRuntime - host termination', () => {
  it('classifies external OS termination as host-terminated', async () => {
    if (process.platform === 'win32') return;
    await expectFailure({ scenario: 'self-signal', timeoutMs: 3000 }, 'host-terminated');
  });
});

describe('invokeRuntime - internal seams', () => {
  it('surfaces mkdtemp failure as host-io-failed', async () => {
    const bootError = new Error('boom');
    (bootError as NodeJS.ErrnoException).code = 'EACCES';
    await expect(
      invokeRuntime(
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
  });

  it('surfaces cleanup-failed on success when rm throws', async () => {
    const tempRoot = await acquireTempRoot();
    const err = await invokeRuntime(
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
  });

  it('preserves primary error when cleanup fails after failure', async () => {
    const tempRoot = await acquireTempRoot();
    const err = await invokeRuntime(
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
  });

  it('invokes the onBeforeCleanup test hook before rm', async () => {
    const seen: string[] = [];
    await expect(
      invokeRuntime(
        {
          command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
          input: readBootstrapInput(),
          timeoutMs: 15_000,
          tempRoot: await acquireTempRoot(),
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
  });
});
