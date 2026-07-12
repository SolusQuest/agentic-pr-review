import { spawn, type ChildProcess } from 'node:child_process';
import type { RuntimeCommand } from './runtime-command.js';
import { sanitizeErrorCode } from './sanitizers.js';

/** POSIX SIGTERM-to-SIGKILL grace period used when the caller does not override. */
const DEFAULT_SIGTERM_GRACE_MS = 5000;
/**
 * Bounded deadline for the child's 'close' event after we have already sent SIGKILL
 * (or the platform-equivalent final kill). If the OS never delivers close within this
 * window we fall back to the observed terminationReason rather than hang forever.
 */
const DEFAULT_POST_KILL_CLOSE_GRACE_MS = 3000;
/** Maximum stdout bytes captured before we terminate the child on stream-hard-cap. */
const STDOUT_HARD_CAP = 1024;
/** Maximum stderr bytes captured before we terminate the child on stream-hard-cap. */
const STDERR_HARD_CAP = 4096;

/**
 * Options accepted by {@link runProcess}. Grace values are optional; when undefined,
 * `runProcess` uses its production defaults. Test callers pass short values to
 * exercise SIGKILL escalation and post-kill close deadline paths deterministically.
 */
export interface RunProcessOptions {
  command: RuntimeCommand;
  cliArgs: readonly string[];
  invocationDir: string;
  timeoutMs: number;
  signal: AbortSignal | undefined;
  spawnFn: typeof spawn;
  sigtermGraceMs?: number;
  closeGraceMs?: number;
}

export interface StreamCaptureResult {
  stdoutBytes: Uint8Array;
  stderrBytes: Uint8Array;
}

export interface ProcessOutcome {
  kind:
    | 'natural-exit'
    | 'timeout'
    | 'cancelled'
    | 'stream-hard-cap'
    | 'host-terminated'
    | 'spawn-failed';
  exitCode?: number;
  streamCap?: 'stdout' | 'stderr';
  streamObservedBytes?: number;
  spawnErrorCode?: string;
  /**
   * True iff the child process either (a) never spawned (pre-spawn failure) or
   * (b) actually emitted its 'close' event before runProcess returned. False
   * when the bounded post-kill close deadline fired without an observed close.
   * The orchestrator uses this flag to decide whether cleaning up the invocation
   * directory is safe: with an unobserved close the child may still be running
   * and could race with recursive rm on its cwd.
   */
  closeObserved: boolean;
}

export interface RunProcessResult {
  outcome: ProcessOutcome;
  capture: StreamCaptureResult;
}

type TerminationReason =
  | { kind: 'timeout' }
  | { kind: 'cancelled' }
  | {
      kind: 'stream-hard-cap';
      stream: 'stdout' | 'stderr';
      observedBytes: number;
    }
  | { kind: 'host-terminated' };

/**
 * Build the minimal child environment. Forwards only a documented allowlist of
 * host variables and forces documentation-required opt-outs. Never forwards
 * `GITHUB_*`, `ANTHROPIC_*`, or `AGENTIC_REVIEW_*` credentials.
 */
function buildChildEnv(): NodeJS.ProcessEnv {
  const parent = process.env;
  const passthrough = [
    'PATH',
    'LANG',
    'LC_ALL',
    'LC_CTYPE',
    'TMPDIR',
    'TMP',
    'TEMP',
    'DOTNET_ROOT',
  ];
  const env: NodeJS.ProcessEnv = {};
  for (const name of passthrough) {
    const value = parent[name];
    if (value !== undefined) env[name] = value;
  }
  env.NO_COLOR = '1';
  env.DOTNET_NOLOGO = '1';
  env.DOTNET_CLI_TELEMETRY_OPTOUT = '1';
  return env;
}

/**
 * Spawn the runtime CLI and manage its full lifecycle: capture bounded stdout/stderr,
 * enforce timeout and abort, escalate SIGTERM -> SIGKILL on POSIX, and apply a
 * bounded post-kill close deadline so a stalled 'close' cannot hang the caller.
 *
 * Returns a {@link RunProcessResult} that already encodes first-terminal-event-wins
 * classification and a `closeObserved` flag the orchestrator uses to gate cleanup.
 */
export async function runProcess(options: RunProcessOptions): Promise<RunProcessResult> {
  const {
    command,
    cliArgs,
    invocationDir,
    timeoutMs,
    signal,
    spawnFn,
    sigtermGraceMs = DEFAULT_SIGTERM_GRACE_MS,
    closeGraceMs = DEFAULT_POST_KILL_CLOSE_GRACE_MS,
  } = options;

  const args = [...(command.prefixArgs ?? []), ...cliArgs];
  const env = buildChildEnv();
  // Final abort check immediately before spawn. Any abort that arrived during input write
  // or executable validation must prevent the child process from ever starting.
  if (signal?.aborted) {
    return {
      outcome: { kind: 'cancelled', closeObserved: true },
      capture: { stdoutBytes: new Uint8Array(), stderrBytes: new Uint8Array() },
    };
  }
  let child: ChildProcess;
  try {
    child = spawnFn(command.executablePath, args, {
      cwd: invocationDir,
      env,
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: false,
      windowsHide: true,
    });
  } catch (cause) {
    return {
      outcome: {
        kind: 'spawn-failed',
        spawnErrorCode: sanitizeErrorCode(cause),
        closeObserved: true,
      },
      capture: { stdoutBytes: new Uint8Array(), stderrBytes: new Uint8Array() },
    };
  }

  interface ExitPayload {
    code: number | null;
    signal: NodeJS.Signals | null;
    spawnError?: unknown;
    closeObserved: boolean;
  }

  const stdoutChunks: Buffer[] = [];
  const stderrChunks: Buffer[] = [];
  let stdoutLen = 0;
  let stderrLen = 0;
  let terminationReason: TerminationReason | undefined;
  let sigkillTimer: NodeJS.Timeout | undefined;
  let closeDeadlineTimer: NodeJS.Timeout | undefined;
  let closed = false;
  let settled = false;
  let spawnedOk = false;
  let resolveExit!: (payload: ExitPayload) => void;
  const exitPromise = new Promise<ExitPayload>((resolve) => {
    resolveExit = resolve;
  });

  const settle = (payload: ExitPayload): void => {
    if (settled) return;
    settled = true;
    closed = true;
    resolveExit(payload);
  };

  // Bounded fallback: after the last kill escalation is issued we cannot rely on the
  // OS to deliver 'close'. If it never arrives within a bounded window we still resolve
  // the exit promise so the adapter does not hang. Outcome classification below prefers
  // the observed terminationReason (timeout / cancelled / stream-hard-cap / etc.), which
  // is preserved on any code path that arms this deadline.
  const armCloseDeadline = (): void => {
    if (closeDeadlineTimer !== undefined) return;
    closeDeadlineTimer = setTimeout(() => {
      settle({ code: null, signal: null, closeObserved: false });
    }, closeGraceMs);
    closeDeadlineTimer.unref?.();
  };

  const killChild = (): void => {
    if (closed) return;
    if (process.platform === 'win32') {
      try {
        child.kill('SIGKILL');
      } catch {
        // ignore
      }
      armCloseDeadline();
      return;
    }
    try {
      child.kill('SIGTERM');
    } catch {
      // ignore
    }
    if (sigkillTimer) return;
    sigkillTimer = setTimeout(() => {
      // Do NOT rely on child.killed (which flips true as soon as a signal is sent);
      // check whether the process has actually exited before attempting SIGKILL.
      // Whether or not we actually send SIGKILL, we must always arm the bounded close
      // deadline so a stalled 'close' (for example when descendants keep stdio open
      // after the direct child exited) cannot leave the exit promise unresolved.
      if (closed) return;
      if (child.exitCode === null && child.signalCode === null) {
        try {
          child.kill('SIGKILL');
        } catch {
          // ignore; bounded fallback still applies
        }
      }
      armCloseDeadline();
    }, sigtermGraceMs);
    sigkillTimer.unref?.();
  };

  const setTermination = (reason: TerminationReason): void => {
    if (terminationReason) return;
    terminationReason = reason;
    killChild();
  };

  const onStdout = (chunk: Buffer): void => {
    stdoutLen += chunk.length;
    if (stdoutLen <= STDOUT_HARD_CAP) {
      stdoutChunks.push(chunk);
      return;
    }
    const room = STDOUT_HARD_CAP - (stdoutLen - chunk.length);
    if (room > 0) stdoutChunks.push(chunk.subarray(0, room));
    setTermination({ kind: 'stream-hard-cap', stream: 'stdout', observedBytes: stdoutLen });
  };
  const onStderr = (chunk: Buffer): void => {
    stderrLen += chunk.length;
    if (stderrLen <= STDERR_HARD_CAP) {
      stderrChunks.push(chunk);
      return;
    }
    const room = STDERR_HARD_CAP - (stderrLen - chunk.length);
    if (room > 0) stderrChunks.push(chunk.subarray(0, room));
    setTermination({ kind: 'stream-hard-cap', stream: 'stderr', observedBytes: stderrLen });
  };

  child.stdout?.on('data', onStdout);
  child.stderr?.on('data', onStderr);

  child.once('spawn', () => {
    spawnedOk = true;
  });
  child.on('error', (err) => {
    // Node ChildProcess emits 'error' for spawn failures and for post-spawn control
    // errors (for example when a signal cannot be delivered). Only the pre-spawn
    // variant is a real spawn-failed; post-spawn errors must not settle the promise
    // before 'close' and must not overwrite an existing terminationReason. If we
    // already sent a kill and the OS never delivers close, the bounded deadline
    // armed by killChild() will still resolve the promise.
    if (!spawnedOk) {
      settle({ code: null, signal: null, spawnError: err, closeObserved: true });
      return;
    }
    // Post-spawn control error: we deliberately do NOT change terminationReason. If a
    // primary termination (timeout, cancelled, stream-hard-cap) is already recorded it
    // must win. If none is recorded and 'close' still arrives with a real exit code,
    // that natural exit should classify the outcome.
    //
    // Safety net for the case where the OS never delivers 'close': arm the bounded
    // close deadline. However, if a POSIX SIGTERM->SIGKILL escalation is already
    // scheduled (sigkillTimer pending), the close deadline must not fire before
    // SIGKILL has been attempted; otherwise a short closeGraceMs (< sigtermGraceMs)
    // could resolve the exit promise, cause cleanup of pending kill timers, and
    // leave an orphan process that was otherwise going to be SIGKILL'd. In that
    // case we defer arming and let the sigkillTimer callback arm the deadline
    // after it fires (or does not fire) the SIGKILL.
    if (sigkillTimer !== undefined && process.platform !== 'win32') {
      return;
    }
    armCloseDeadline();
  });
  child.on('close', (code, signalCode) =>
    settle({ code, signal: signalCode, closeObserved: true }),
  );

  const timeoutTimer = setTimeout(() => {
    setTermination({ kind: 'timeout' });
  }, timeoutMs);
  timeoutTimer.unref?.();

  const abortListener = (): void => {
    setTermination({ kind: 'cancelled' });
  };
  if (signal) {
    if (signal.aborted) {
      // Signal aborted between preflight and listener registration.
      setTermination({ kind: 'cancelled' });
    } else {
      signal.addEventListener('abort', abortListener, { once: true });
    }
  }

  const exit = await exitPromise;

  clearTimeout(timeoutTimer);
  if (sigkillTimer) clearTimeout(sigkillTimer);
  if (closeDeadlineTimer) clearTimeout(closeDeadlineTimer);
  if (signal) signal.removeEventListener('abort', abortListener);

  const capture: StreamCaptureResult = {
    stdoutBytes: Buffer.concat(stdoutChunks, Math.min(stdoutLen, STDOUT_HARD_CAP)),
    stderrBytes: Buffer.concat(stderrChunks, Math.min(stderrLen, STDERR_HARD_CAP)),
  };

  const closeObserved = exit.closeObserved;

  if (exit.spawnError !== undefined && exit.code === null && exit.signal === null) {
    return {
      outcome: {
        kind: 'spawn-failed',
        spawnErrorCode: sanitizeErrorCode(exit.spawnError),
        closeObserved: true,
      },
      capture,
    };
  }

  if (terminationReason) {
    if (terminationReason.kind === 'timeout') {
      return { outcome: { kind: 'timeout', closeObserved }, capture };
    }
    if (terminationReason.kind === 'cancelled') {
      return { outcome: { kind: 'cancelled', closeObserved }, capture };
    }
    if (terminationReason.kind === 'stream-hard-cap') {
      return {
        outcome: {
          kind: 'stream-hard-cap',
          streamCap: terminationReason.stream,
          streamObservedBytes: terminationReason.observedBytes,
          closeObserved,
        },
        capture,
      };
    }
    if (terminationReason.kind === 'host-terminated') {
      return { outcome: { kind: 'host-terminated', closeObserved }, capture };
    }
  }

  if (exit.code === null) {
    return {
      outcome: { kind: 'host-terminated', closeObserved },
      capture,
    };
  }

  return {
    outcome: {
      kind: 'natural-exit',
      exitCode: exit.code,
      closeObserved,
    },
    capture,
  };
}
