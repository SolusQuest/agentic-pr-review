import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { existsSync, readFileSync } from 'node:fs';
import { mkdtemp, readdir, rm, symlink } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { invokeRuntime, RuntimeInvocationError } from './invoke-runtime.js';
import {
  invokeRuntimeForTests,
  type RuntimeInvocationTestSeams,
} from './invoke-runtime.test-support.js';

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
  signal?: AbortSignal;
  extraEnv?: Record<string, string>;
  input?: ReviewInputV1;
  fillerBytes?: number;
  sigtermGraceMs?: number;
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

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms).unref?.());
}

/**
 * Minimal ChildProcess-shaped EventEmitter for deterministic post-spawn lifecycle
 * tests. Supports the surface the adapter actually uses: stdout/stderr, spawn/error/
 * close events, kill(), exitCode, signalCode. Kill invocations are recorded so tests
 * can assert escalation and, when useful, simulate delivery failures by pre-setting
 * a killError.
 */
type MockChild = EventEmitter & {
  readonly stdout: EventEmitter;
  readonly stderr: EventEmitter;
  exitCode: number | null;
  signalCode: NodeJS.Signals | null;
  killed: boolean;
  readonly killCalls: Array<string | number | undefined>;
  killError: Error | null;
  kill: (signal?: NodeJS.Signals | number) => boolean;
};

function makeMockChild(): MockChild {
  const emitter = new EventEmitter();
  const stdout = new EventEmitter();
  const stderr = new EventEmitter();
  const killCalls: Array<string | number | undefined> = [];
  const child = Object.assign(emitter, {
    stdout,
    stderr,
    exitCode: null as number | null,
    signalCode: null as NodeJS.Signals | null,
    killed: false,
    killCalls,
    killError: null as Error | null,
    kill(signal?: NodeJS.Signals | number): boolean {
      killCalls.push(signal);
      if (this.killError) throw this.killError;
      this.killed = true;
      return true;
    },
  }) as MockChild;
  return child;
}

async function runScenario(opts: ScenarioOptions) {
  const tempRoot = await acquireTempRoot();
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

describe('invokeRuntime - public entrypoint', () => {
  it('is a single-argument function without test seams on its signature', () => {
    expect(invokeRuntime.length).toBe(1);
  });
  it('rejects a malformed options object', async () => {
    await expect(invokeRuntime(null as unknown as never)).rejects.toMatchObject({
      kind: 'options-invalid',
    });
  });
});

describe('invokeRuntime - options validation', () => {
  const input = readBootstrapInput();
  const validCommand = { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] };

  it('rejects a null command', async () => {
    await expect(
      invokeRuntime({ command: null as unknown as never, input, timeoutMs: 1000 }),
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
      invokeRuntime({ command: validCommand, input, timeoutMs: 1000, tempRoot: 'relative-path' }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-absolute executablePath', async () => {
    await expect(
      invokeRuntime({ command: { executablePath: 'relative-runtime' }, input, timeoutMs: 1000 }),
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
  it('rejects a signal missing addEventListener', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: { aborted: false } as unknown as AbortSignal,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a signal with a non-boolean aborted', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: {
          aborted: 'nope',
          addEventListener: () => undefined,
          removeEventListener: () => undefined,
        } as unknown as AbortSignal,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('fails as cancelled if signal already aborted', async () => {
    const ctrl = new AbortController();
    ctrl.abort();
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 1000, signal: ctrl.signal }),
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
    expect(err.message).toBe('ReviewInputV1 schema validation failed (1 errors).');
    // Sanity: no raw property names or values leaked.
    expect(err.message).not.toMatch(/protocolVersion|additionalProperty/i);
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
  it('fails as cancelled when signal aborts mid-preflight', async () => {
    const ctrl = new AbortController();
    // Abort between options validation and mkdtemp; use a signal that aborts before we get there.
    setImmediate(() => ctrl.abort());
    await expect(
      invokeRuntime({
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        signal: ctrl.signal,
      }),
    ).rejects.toMatchObject({ kind: 'cancelled' });
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
    const entries = await readdir(tempRoot);
    expect(entries).toEqual([]);
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

describe('invokeRuntime - non-zero exits and APR codes', () => {
  const cases: Array<{ scenario: string; exitCode: number; exitClass: string; aprCode?: string }> =
    [
      { scenario: 'exit-2', exitCode: 2, exitClass: 'usage', aprCode: 'APR_USAGE_INVALID' },
      {
        scenario: 'exit-10',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_RUNTIME_VERSION_MISMATCH',
      },
      { scenario: 'exit-10-input-read', exitCode: 10, exitClass: 'contract' },
      {
        scenario: 'exit-10-input-json',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_INPUT_JSON_INVALID',
      },
      {
        scenario: 'exit-10-protocol-version',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_PROTOCOL_VERSION_UNSUPPORTED',
      },
      { scenario: 'exit-20', exitCode: 20, exitClass: 'runtime', aprCode: 'APR_RUNTIME_INTERNAL' },
      {
        scenario: 'exit-20-self-validation',
        exitCode: 20,
        exitClass: 'runtime',
        aprCode: 'APR_OUTPUT_SELF_VALIDATION_FAILED',
      },
      { scenario: 'exit-30', exitCode: 30, exitClass: 'provider', aprCode: 'APR_PROVIDER_FAILED' },
      {
        scenario: 'exit-40-trace-write',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_TRACE_WRITE_FAILED',
      },
      {
        scenario: 'exit-40',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_RESULT_WRITE_FAILED',
      },
      {
        scenario: 'exit-40-input-read',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_INPUT_READ_FAILED',
      },
    ];
  for (const c of cases) {
    it(`maps ${c.scenario} to ${c.exitCode} (${c.exitClass}${c.aprCode ? '/' + c.aprCode : ''})`, async () => {
      const err = await expectFailure({ scenario: c.scenario }, 'runtime-exit');
      expect(err.exitCode).toBe(c.exitCode);
      expect(err.exitClass).toBe(c.exitClass);
      if (c.aprCode) expect(err.diagnosticCode).toBe(c.aprCode);
    });
  }
  it('maps unknown non-zero exit to unknown-exit', async () => {
    const err = await expectFailure({ scenario: 'exit-77' }, 'unknown-exit');
    expect(err.exitCode).toBe(77);
    expect(err.diagnosticCode).toBeUndefined();
  });
  it('drops APR_* code when its class does not match the observed exit class', async () => {
    // exit-2 emitted with a provider APR code should be dropped
    const err = await expectFailure({ scenario: 'exit-2-mismatched-apr' }, 'runtime-exit');
    expect(err.exitCode).toBe(2);
    expect(err.exitClass).toBe('usage');
    expect(err.diagnosticCode).toBeUndefined();
  });
});

describe('invokeRuntime - failure trace diagnostics', () => {
  it('exposes failure trace diagnostics when provenance holds', async () => {
    const err = await expectFailure({ scenario: 'exit-10-with-failure-trace' }, 'runtime-exit');
    expect(err.failureTraceDiagnostics?.[0]?.code).toBe('FAKE_OK');
    // Positive-class case for APR_INPUT_SCHEMA_INVALID (documented at exit 10 contract).
    expect(err.diagnosticCode).toBe('APR_INPUT_SCHEMA_INVALID');
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
  it('reports unsafe-output-file when trace.json is a directory (non-regular)', async () => {
    await expectFailure({ scenario: 'directory-trace' }, 'unsafe-output-file');
  });
  it('reports result-invalid for non-UTF8 bytes', async () => {
    await expectFailure({ scenario: 'invalid-utf8-result' }, 'result-invalid');
  });
  it('reports result-invalid for non-JSON', async () => {
    await expectFailure({ scenario: 'invalid-json-result' }, 'result-invalid');
  });
  it('reports result-invalid for schema failure', async () => {
    const err = await expectFailure({ scenario: 'schema-invalid-result' }, 'result-invalid');
    expect(err.message).toMatch(/^ReviewResultV1 schema validation failed \(\d+ errors\)\.$/);
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
  it('reports process-contract-violation when result.trace.sha256 missing', async () => {
    await expectFailure({ scenario: 'missing-result-trace-sha' }, 'process-contract-violation');
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
  it('flags stdout leak on exit 0 as process-contract-violation before file validation', async () => {
    // Even without result files, stream shape violates first.
    const err = await expectFailure(
      { scenario: 'stdout-leak-no-output' },
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stdout-nonempty')).toBe(true);
  });
  it('flags stdout leak on exit 0 as process-contract-violation even with valid output', async () => {
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
  it('sanitizes stderrSnippet: drops non-UTF-8 first line', async () => {
    const err = await expectFailure({ scenario: 'stderr-non-utf8' }, 'runtime-exit');
    expect(err.stderrSnippet).toBeUndefined();
  });
  it('sanitizes stderrSnippet: strips control characters', async () => {
    const err = await expectFailure({ scenario: 'stderr-control-chars' }, 'runtime-exit');
    expect(err.stderrSnippet).toBeDefined();
    expect(err.stderrSnippet!).toMatch(/^[\x20-\x7e]+$/);
  });
  it('sanitizes stderrSnippet: drops when it contains the invocation path', async () => {
    const err = await expectFailure({ scenario: 'stderr-path-leak' }, 'runtime-exit');
    expect(err.stderrSnippet).toBeUndefined();
  });
});

describe('invokeRuntime - timeout, cancellation, and termination', () => {
  it('times out a hanging child', async () => {
    await expectFailure({ scenario: 'hang', timeoutMs: 500 }, 'timed-out');
  });
  it('escalates to SIGKILL when child ignores SIGTERM', async () => {
    if (process.platform === 'win32') return;
    const err = await expectFailure(
      { scenario: 'ignore-sigterm', timeoutMs: 500, sigtermGraceMs: 150 },
      'timed-out',
    );
    expect(err.kind).toBe('timed-out');
  }, 8000);
  it('cancels via AbortSignal mid-run', async () => {
    const ctrl = new AbortController();
    const { invoke } = await runScenario({
      scenario: 'hang',
      timeoutMs: 30_000,
      signal: ctrl.signal,
    });
    setTimeout(() => ctrl.abort(), 100).unref();
    await expect(invoke).rejects.toMatchObject({ kind: 'cancelled' });
  });
  it('gives timeout precedence over subsequent stream floods', async () => {
    // Child idles until timeout fires and SIGTERM is delivered, then floods stderr past
    // the hard cap while continuing to ignore SIGTERM. The first terminal event (timeout)
    // must remain the primary classification even though a stream-hard-cap would also
    // otherwise qualify. Small sigtermGraceMs keeps the wall-clock bounded via SIGKILL.
    if (process.platform === 'win32') return;
    await expectFailure(
      { scenario: 'flood-stderr-after-sigterm', timeoutMs: 300, sigtermGraceMs: 150 },
      'timed-out',
    );
  }, 8000);
  it('classifies external OS termination as host-terminated', async () => {
    if (process.platform === 'win32') return;
    await expectFailure({ scenario: 'self-signal', timeoutMs: 3000 }, 'host-terminated');
  });
});

describe('invokeRuntime - post-spawn lifecycle (mock ChildProcess)', () => {
  it('preserves timed-out primary when a post-spawn error precedes close', async () => {
    const tempRoot = await acquireTempRoot();
    const mock = makeMockChild();
    const spawnOverride = (() => {
      // Emit 'spawn' asynchronously, then let the adapter arm its timeout.
      setImmediate(() => mock.emit('spawn'));
      return mock as unknown as ReturnType<typeof spawn>;
    }) as unknown as typeof spawn;

    const invoke = invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [] },
        input: readBootstrapInput(),
        timeoutMs: 50,
        tempRoot,
      },
      { spawnOverride, sigtermGraceMs: 20, closeGraceMs: 100 },
    );

    // Once we know the adapter has fired the timeout (setTermination -> kill), emit a
    // synthetic post-spawn 'error' simulating a kill failure. This must not settle the
    // exit promise before 'close' and must not overwrite the timeout classification.
    await delay(80);
    const killErr = new Error('synthetic post-spawn kill failure');
    (killErr as NodeJS.ErrnoException).code = 'ESRCH';
    mock.emit('error', killErr);
    await delay(30);
    // Now emit close as the OS would eventually deliver it after SIGKILL took effect.
    mock.emit('close', null, 'SIGKILL');

    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('timed-out');
    expect(await readdir(tempRoot)).toEqual([]);
  }, 8000);

  it('applies the bounded close-deadline fallback when close never arrives after kill', async () => {
    const tempRoot = await acquireTempRoot();
    const mock = makeMockChild();
    const spawnOverride = (() => {
      setImmediate(() => mock.emit('spawn'));
      return mock as unknown as ReturnType<typeof spawn>;
    }) as unknown as typeof spawn;

    const invoke = invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [] },
        input: readBootstrapInput(),
        timeoutMs: 50,
        tempRoot,
      },
      // Tight bounds: SIGTERM grace 20ms, then SIGKILL, then closeGraceMs=60ms for close;
      // total wall-clock bound comfortably fits within the 8000ms test budget.
      { spawnOverride, sigtermGraceMs: 20, closeGraceMs: 60 },
    );

    // Never emit 'close'. The adapter's bounded fallback must still resolve the run.
    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('timed-out');
    expect(await readdir(tempRoot)).toEqual([]);
  }, 8000);

  it('routes a pre-spawn error to spawn-failed', async () => {
    const tempRoot = await acquireTempRoot();
    const mock = makeMockChild();
    const spawnOverride = (() => {
      // Never emit 'spawn'; instead emit a synthetic pre-spawn error.
      setImmediate(() => {
        const err = new Error('pre-spawn failure');
        (err as NodeJS.ErrnoException).code = 'ENOENT';
        mock.emit('error', err);
      });
      return mock as unknown as ReturnType<typeof spawn>;
    }) as unknown as typeof spawn;

    const invoke = invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [] },
        input: readBootstrapInput(),
        timeoutMs: 500,
        tempRoot,
      },
      { spawnOverride, sigtermGraceMs: 20, closeGraceMs: 60 },
    );

    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('spawn-failed');
    expect((err as RuntimeInvocationError).diagnosticCode).toBe('ENOENT');
    expect(await readdir(tempRoot)).toEqual([]);
  }, 8000);
});
describe('invokeRuntime - error hygiene', () => {
  it('does not embed input content in error messages', async () => {
    const err = await expectFailure(
      {
        scenario: 'success',
        input: {
          ...readBootstrapInput(),
          protocolVersion: 999 as unknown as 1,
        },
      },
      'input-invalid',
    );
    expect(err.message).not.toMatch(/999/);
    expect(err.message).not.toMatch(/protocolVersion/);
  }, 8000);
  it('does not embed invocation directory paths in error messages or stderrSnippet', async () => {
    const err = await expectFailure({ scenario: 'exit-20' }, 'runtime-exit');
    expect(err.message).not.toMatch(/runtime-\w{6,}/);
    if (err.stderrSnippet) expect(err.stderrSnippet).not.toMatch(/runtime-\w{6,}/);
  }, 8000);
  it('exposes a sanitized diagnosticCode on spawn-failed instead of raw cause', async () => {
    const tempRoot = await acquireTempRoot();
    const err = await invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        tempRoot,
      },
      {
        spawnOverride: (() => {
          const e = new Error('spawn EACCES') as NodeJS.ErrnoException;
          e.code = 'EACCES';
          throw e;
        }) as unknown as typeof spawn,
      },
    ).catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('spawn-failed');
    expect((err as RuntimeInvocationError).diagnosticCode).toBe('EACCES');
  }, 8000);
});

describe('invokeRuntime - child environment', () => {
  it('does not forward GITHUB_* or provider credentials to the child', async () => {
    const previous = process.env.GITHUB_TOKEN;
    const previousAnthropic = process.env.ANTHROPIC_API_KEY;
    const previousAgentic = process.env.AGENTIC_REVIEW_API_KEY;
    process.env.GITHUB_TOKEN = 'ghtok-should-not-forward';
    process.env.ANTHROPIC_API_KEY = 'anth-should-not-forward';
    process.env.AGENTIC_REVIEW_API_KEY = 'agentic-should-not-forward';
    try {
      const { invoke } = await runScenario({ scenario: 'env-dump-success' });
      const success = await invoke;
      const dump = success.result.summary;
      // All three sensitive variables must be reported as absent in the child.
      expect(dump).toContain('GITHUB_TOKEN=absent');
      expect(dump).toContain('ANTHROPIC_API_KEY=absent');
      expect(dump).toContain('AGENTIC_REVIEW_API_KEY=absent');
      expect(dump).not.toContain('ghtok-should-not-forward');
    } finally {
      if (previous === undefined) delete process.env.GITHUB_TOKEN;
      else process.env.GITHUB_TOKEN = previous;
      if (previousAnthropic === undefined) delete process.env.ANTHROPIC_API_KEY;
      else process.env.ANTHROPIC_API_KEY = previousAnthropic;
      if (previousAgentic === undefined) delete process.env.AGENTIC_REVIEW_API_KEY;
      else process.env.AGENTIC_REVIEW_API_KEY = previousAgentic;
    }
  }, 8000);
  it('sets NO_COLOR, DOTNET_NOLOGO, and DOTNET_CLI_TELEMETRY_OPTOUT unconditionally', async () => {
    const { invoke } = await runScenario({ scenario: 'env-dump-required-vars' });
    const success = await invoke;
    expect(success.result.summary).toContain('NO_COLOR=1');
    expect(success.result.summary).toContain('DOTNET_NOLOGO=1');
    expect(success.result.summary).toContain('DOTNET_CLI_TELEMETRY_OPTOUT=1');
  }, 8000);
});

describe('invokeRuntime - byte budgets', () => {
  it('reports unsafe-output-file when result.json exceeds cap', async () => {
    // The oversized-result scenario writes a filler > 8 MiB cap.
    await expectFailure(
      { scenario: 'oversized-result', fillerBytes: 10 * 1024 * 1024 },
      'unsafe-output-file',
    );
  }, 8000);
});

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
    const tempRoot = await acquireTempRoot();
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
    const tempRoot = await acquireTempRoot();
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
        tempRoot: await acquireTempRoot(),
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
  }, 8000);
});
