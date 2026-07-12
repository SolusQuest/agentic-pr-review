import { spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { readdir } from 'node:fs/promises';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { RuntimeInvocationError } from './invoke-runtime.js';
import { invokeRuntimeForTests } from './invoke-runtime.test-support.js';
import {
  createTempRootRegistry,
  expectFailure,
  readBootstrapInput,
  runScenario,
} from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

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

describe('invokeRuntime - timeout, cancellation, and termination', () => {
  it('times out a hanging child', async () => {
    await expectFailure({ scenario: 'hang', timeoutMs: 500 }, registry, 'timed-out');
  });
  it('escalates to SIGKILL when child ignores SIGTERM', async () => {
    if (process.platform === 'win32') return;
    const err = await expectFailure(
      { scenario: 'ignore-sigterm', timeoutMs: 500, sigtermGraceMs: 150 },
      registry,
      'timed-out',
    );
    expect(err.kind).toBe('timed-out');
  }, 8000);
  it('cancels via AbortSignal mid-run', async () => {
    const ctrl = new AbortController();
    const { invoke } = await runScenario(
      {
        scenario: 'hang',
        timeoutMs: 30_000,
        signal: ctrl.signal,
      },
      registry,
    );
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
      registry,
      'timed-out',
    );
  }, 8000);
  it('classifies external OS termination as host-terminated', async () => {
    if (process.platform === 'win32') return;
    await expectFailure({ scenario: 'self-signal', timeoutMs: 3000 }, registry, 'host-terminated');
  });
});

describe('invokeRuntime - post-spawn lifecycle (mock ChildProcess)', () => {
  it('preserves timed-out primary when a post-spawn error precedes close', async () => {
    const tempRoot = await registry.acquire();
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
    const tempRoot = await registry.acquire();
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
      // Tight bounds: SIGTERM grace 20ms, then SIGKILL, then closeGraceMs=60ms for close.
      { spawnOverride, sigtermGraceMs: 20, closeGraceMs: 60 },
    );

    // Never emit 'close'. The adapter's bounded fallback must resolve the run with the
    // primary termination (timed-out), but MUST NOT run cleanup on the invocation
    // directory because the child may still be alive and racing on its cwd. The dir
    // is deliberately leaked; the outer afterEach hook removes it after the test.
    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('timed-out');
    const remaining = await readdir(tempRoot);
    expect(remaining.length).toBe(1);
    // The leaked directory should still contain the input file we wrote pre-spawn.
    const invocationDir = join(tempRoot, remaining[0]);
    const leaked = await readdir(invocationDir);
    expect(leaked).toContain('input.json');
  }, 8000);

  it('routes a pre-spawn error to spawn-failed', async () => {
    const tempRoot = await registry.acquire();
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

  it('does not let a short closeGraceMs pre-empt a pending POSIX SIGKILL escalation', async () => {
    if (process.platform === 'win32') return;
    const tempRoot = await registry.acquire();
    const mock = makeMockChild();
    const spawnOverride = (() => {
      setImmediate(() => mock.emit('spawn'));
      return mock as unknown as ReturnType<typeof spawn>;
    }) as unknown as typeof spawn;

    // Real short closeGraceMs < sigtermGraceMs so a naive implementation (post-spawn
    // error immediately calls armCloseDeadline) would resolve the exit promise long
    // before the sigkillTimer fires, thereby cancelling the pending SIGKILL. The
    // property under test is that the adapter defers arming while sigkillTimer is
    // pending and lets the SIGKILL escalation actually run.
    //
    // Never emit 'close'; instead assert that after the run bounded-returns:
    //   - the correct implementation attempted SIGKILL (killCalls contains 'SIGKILL')
    //   - the outcome is timed-out
    //   - the invocation directory is preserved (closeObserved=false)
    //
    // A broken implementation would arm a 30 ms deadline at t~80 ms, settle at
    // t~110 ms, clearTimeout the pending sigkillTimer at t~200 ms, and never send
    // SIGKILL. The killCalls assertion catches that regression directly.
    const invoke = invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [] },
        input: readBootstrapInput(),
        timeoutMs: 40,
        tempRoot,
      },
      { spawnOverride, sigtermGraceMs: 200, closeGraceMs: 30 },
    );

    // Wait past the timeout so setTermination('timeout') runs and killChild() sends
    // SIGTERM, scheduling the SIGKILL timer at t~240 ms.
    await delay(80);
    expect(mock.killCalls).toContain('SIGTERM');
    expect(mock.killCalls).not.toContain('SIGKILL');

    // Emit a synthetic post-spawn control error. A broken implementation would call
    // armCloseDeadline() here (30 ms) and pre-empt the pending SIGKILL escalation.
    const controlErr = new Error('synthetic kill delivery failure');
    (controlErr as NodeJS.ErrnoException).code = 'ESRCH';
    mock.emit('error', controlErr);

    // Do NOT emit 'close'. Let the correct implementation follow the full sequence:
    //   ~240 ms: sigkillTimer fires -> SIGKILL attempted -> armCloseDeadline(30 ms)
    //   ~270 ms: bounded fallback settles with closeObserved=false
    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('timed-out');
    // The escalation must have attempted SIGKILL despite the intervening control
    // error and the short closeGraceMs.
    expect(mock.killCalls).toContain('SIGKILL');
    // closeObserved=false -> cleanup skipped -> invocation directory preserved.
    const remaining = await readdir(tempRoot);
    expect(remaining.length).toBe(1);
    const invocationDir = join(tempRoot, remaining[0]);
    const leaked = await readdir(invocationDir);
    expect(leaked).toContain('input.json');
  }, 8000);

  it('bounded-returns when child appears exited but close never arrives', async () => {
    if (process.platform === 'win32') return;
    const tempRoot = await registry.acquire();
    const mock = makeMockChild();
    const spawnOverride = (() => {
      setImmediate(() => mock.emit('spawn'));
      return mock as unknown as ReturnType<typeof spawn>;
    }) as unknown as typeof spawn;

    const invoke = invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [] },
        input: readBootstrapInput(),
        timeoutMs: 40,
        tempRoot,
      },
      { spawnOverride, sigtermGraceMs: 100, closeGraceMs: 80 },
    );

    // After the timeout fires and SIGTERM is delivered, simulate a direct child that
    // has already exited (exitCode set) but whose 'close' never arrives because a
    // descendant is still holding stdio open. The sigkillTimer must NOT skip arming
    // the close deadline just because exitCode is non-null.
    await delay(60);
    expect(mock.killCalls).toContain('SIGTERM');
    mock.exitCode = 0;
    // Never emit 'close'. The adapter must bounded-return within
    // sigtermGraceMs + closeGraceMs (100 + 80 = 180 ms) after SIGTERM.
    const err = await invoke.catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('timed-out');
    // Because close was never observed, cleanup was intentionally skipped and the
    // invocation directory is preserved.
    const remaining = await readdir(tempRoot);
    expect(remaining.length).toBe(1);
    const invocationDir = join(tempRoot, remaining[0]);
    const leaked = await readdir(invocationDir);
    expect(leaked).toContain('input.json');
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
      const { invoke } = await runScenario({ scenario: 'env-dump-success' }, registry);
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
    const { invoke } = await runScenario({ scenario: 'env-dump-required-vars' }, registry);
    const success = await invoke;
    expect(success.result.summary).toContain('NO_COLOR=1');
    expect(success.result.summary).toContain('DOTNET_NOLOGO=1');
    expect(success.result.summary).toContain('DOTNET_CLI_TELEMETRY_OPTOUT=1');
  }, 8000);
});
