import { spawn, type ChildProcess } from 'node:child_process';
import path from 'node:path';
import { validateReviewInputV1 } from '../protocol/review-input.js';
import { validateReviewResultV1, type ReviewResultV1 } from '../protocol/review-result.js';
import {
  validateReviewTraceV1,
  type ReviewTraceDiagnosticV1,
  type ReviewTraceV1,
} from '../protocol/review-trace.js';
import type {
  InvokeRuntimeOptions,
  RuntimeCommand,
  RuntimeInvocationSuccess,
} from './runtime-command.js';
import {
  KNOWN_APR_CODES,
  KNOWN_EXIT_CLASSES,
  RuntimeInvocationError,
  type RuntimeContractViolation,
  type RuntimeExitClass,
} from './runtime-errors.js';
import {
  BYTE_LIMITS,
  createInvocationDir,
  decodeStrictUtf8,
  defaultFsSeams,
  isExecutableFile,
  readSafeOutputBytes,
  serializeInputBytes,
  sha256Hex,
  statSafeOutputFile,
  writeInputFile,
  type FsSeams,
} from './runtime-files.js';

/** Public option object exposed to callers. */
export type { InvokeRuntimeOptions, RuntimeCommand, RuntimeInvocationSuccess };
export {
  RuntimeInvocationError,
  type RuntimeContractViolation,
  type RuntimeExitClass,
} from './runtime-errors.js';
export type { RuntimeInvocationErrorKind } from './runtime-errors.js';

const STDOUT_HARD_CAP = 1024;
const STDERR_CONTRACT_LIMIT = 1000;
const STDERR_HARD_CAP = 4096;
const SIGTERM_GRACE_MS = 5000;

/**
 * Optional internal seams. Not exported publicly; used only by tests to observe
 * cleanup behavior and inject filesystem failures deterministically.
 */
export interface InternalTestHooks {
  fs?: Partial<FsSeams>;
  /** Called with the invocation directory before cleanup runs; used to snapshot files. */
  onBeforeCleanup?: (invocationDir: string) => void | Promise<void>;
  /** Overrides `os.tmpdir()`-derived resolution; primarily used in error-injection tests. */
  spawnOverride?: typeof spawn;
}

interface StreamCaptureResult {
  stdoutBytes: Uint8Array;
  stderrBytes: Uint8Array;
  stdoutTruncated: boolean;
  stderrTruncated: boolean;
  hardCapExceeded?: { stream: 'stdout' | 'stderr'; observedBytes: number };
}

interface ProcessOutcome {
  kind:
    | 'natural-exit'
    | 'timeout'
    | 'cancelled'
    | 'stream-hard-cap'
    | 'host-terminated'
    | 'spawn-failed';
  exitCode?: number;
  signal?: NodeJS.Signals;
  streamCap?: 'stdout' | 'stderr';
  streamObservedBytes?: number;
  cause?: unknown;
}

interface RunProcessResult {
  outcome: ProcessOutcome;
  capture: StreamCaptureResult;
}

function isPositiveSafeInt(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value > 0;
}

function isNonNullObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null;
}

function isStringArray(value: unknown): value is readonly string[] {
  return Array.isArray(value) && value.every((entry) => typeof entry === 'string');
}

function assertOptionsShape(options: InvokeRuntimeOptions): void {
  if (!isNonNullObject(options)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'invokeRuntime requires a non-null options object.',
    });
  }
  const { command, timeoutMs, tempRoot, input, signal } = options;

  if (!isNonNullObject(command)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.command must be a non-null object.',
    });
  }
  if (input === undefined || input === null) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.input is required.',
    });
  }
  if (!isPositiveSafeInt(timeoutMs)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.timeoutMs must be a positive safe integer.',
    });
  }
  if (tempRoot !== undefined) {
    if (typeof tempRoot !== 'string' || tempRoot.length === 0 || !path.isAbsolute(tempRoot)) {
      throw new RuntimeInvocationError({
        kind: 'options-invalid',
        message: 'options.tempRoot must be an absolute host-owned path.',
      });
    }
  }
  if (typeof command.executablePath !== 'string' || command.executablePath.length === 0) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.executablePath must be a non-empty string.',
    });
  }
  if (!path.isAbsolute(command.executablePath)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.executablePath must be an absolute path.',
    });
  }
  if (command.prefixArgs !== undefined && !isStringArray(command.prefixArgs)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'command.prefixArgs must be an array of strings when provided.',
    });
  }
  if (signal !== undefined) {
    if (!isNonNullObject(signal) || typeof (signal as AbortSignal).aborted !== 'boolean') {
      throw new RuntimeInvocationError({
        kind: 'options-invalid',
        message: 'options.signal must be a valid AbortSignal.',
      });
    }
  }
}

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

function sanitizeStderrSnippet(bytes: Uint8Array, invocationDir: string): string | undefined {
  const nlIndex = bytes.indexOf(0x0a);
  const lineBytes =
    nlIndex >= 0
      ? bytes.subarray(0, Math.min(nlIndex, STDERR_CONTRACT_LIMIT))
      : bytes.subarray(0, Math.min(bytes.length, STDERR_CONTRACT_LIMIT));
  if (lineBytes.length === 0) return undefined;
  let text: string;
  try {
    text = decodeStrictUtf8(lineBytes);
  } catch {
    return undefined;
  }
  if (text.includes(invocationDir)) return undefined;
  const normalized = text
    .split('')
    .map((ch) => {
      const code = ch.charCodeAt(0);
      if (code === 0x09) return ' ';
      if (code < 0x20 || code > 0x7e) return ' ';
      return ch;
    })
    .join('')
    .replace(/ +/g, ' ')
    .trim();
  return normalized.length > 0 ? normalized : undefined;
}

function parseAprCode(
  snippet: string | undefined,
  exitClass: RuntimeExitClass,
): string | undefined {
  if (!snippet) return undefined;
  const match = snippet.match(/^(APR_[A-Z0-9_]+)/);
  if (!match) return undefined;
  const code = match[1];
  const expected = KNOWN_APR_CODES.get(code);
  if (!expected || expected !== exitClass) return undefined;
  return code;
}

function buildContractViolations(
  capture: StreamCaptureResult,
): RuntimeContractViolation[] | undefined {
  const violations: RuntimeContractViolation[] = [];
  if (capture.stdoutBytes.length > 0) {
    violations.push({ kind: 'stdout-nonempty', observedBytes: capture.stdoutBytes.length });
  }
  if (capture.stderrBytes.length > STDERR_CONTRACT_LIMIT) {
    violations.push({ kind: 'stderr-over-contract', observedBytes: capture.stderrBytes.length });
  }
  return violations.length > 0 ? violations : undefined;
}

async function runProcess(
  command: RuntimeCommand,
  cliArgs: readonly string[],
  invocationDir: string,
  timeoutMs: number,
  signal: AbortSignal | undefined,
  spawnFn: typeof spawn,
): Promise<RunProcessResult> {
  const args = [...(command.prefixArgs ?? []), ...cliArgs];
  const env = buildChildEnv();
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
      outcome: { kind: 'spawn-failed', cause },
      capture: {
        stdoutBytes: new Uint8Array(),
        stderrBytes: new Uint8Array(),
        stdoutTruncated: false,
        stderrTruncated: false,
      },
    };
  }

  const stdoutChunks: Buffer[] = [];
  const stderrChunks: Buffer[] = [];
  let stdoutLen = 0;
  let stderrLen = 0;
  let stdoutTruncated = false;
  let stderrTruncated = false;
  let hardCapExceeded: { stream: 'stdout' | 'stderr'; observedBytes: number } | undefined;
  let terminationReason:
    | {
        kind: 'timeout' | 'cancelled' | 'stream-hard-cap' | 'spawn-failed';
        stream?: 'stdout' | 'stderr';
        observedBytes?: number;
        cause?: unknown;
      }
    | undefined;

  const killChild = (): void => {
    if (child.killed) return;
    try {
      child.kill('SIGTERM');
    } catch {
      // ignore
    }
    if (process.platform !== 'win32') {
      setTimeout(() => {
        if (!child.killed && child.exitCode === null && child.signalCode === null) {
          try {
            child.kill('SIGKILL');
          } catch {
            // ignore
          }
        }
      }, SIGTERM_GRACE_MS).unref?.();
    }
  };

  const onStdout = (chunk: Buffer): void => {
    stdoutLen += chunk.length;
    if (stdoutLen <= STDOUT_HARD_CAP) {
      stdoutChunks.push(chunk);
    } else {
      const room = STDOUT_HARD_CAP - (stdoutLen - chunk.length);
      if (room > 0) stdoutChunks.push(chunk.subarray(0, room));
      stdoutTruncated = true;
      if (!hardCapExceeded) {
        hardCapExceeded = { stream: 'stdout', observedBytes: stdoutLen };
        terminationReason = { kind: 'stream-hard-cap', stream: 'stdout', observedBytes: stdoutLen };
        killChild();
      }
    }
  };
  const onStderr = (chunk: Buffer): void => {
    stderrLen += chunk.length;
    if (stderrLen <= STDERR_HARD_CAP) {
      stderrChunks.push(chunk);
    } else {
      const room = STDERR_HARD_CAP - (stderrLen - chunk.length);
      if (room > 0) stderrChunks.push(chunk.subarray(0, room));
      stderrTruncated = true;
      if (!hardCapExceeded) {
        hardCapExceeded = { stream: 'stderr', observedBytes: stderrLen };
        terminationReason = { kind: 'stream-hard-cap', stream: 'stderr', observedBytes: stderrLen };
        killChild();
      }
    }
  };

  child.stdout?.on('data', onStdout);
  child.stderr?.on('data', onStderr);

  const timeoutTimer = setTimeout(() => {
    if (!terminationReason) {
      terminationReason = { kind: 'timeout' };
      killChild();
    }
  }, timeoutMs);
  timeoutTimer.unref?.();

  const abortListener = (): void => {
    if (!terminationReason) {
      terminationReason = { kind: 'cancelled' };
      killChild();
    }
  };
  if (signal) signal.addEventListener('abort', abortListener, { once: true });

  const exit = await new Promise<{
    code: number | null;
    signal: NodeJS.Signals | null;
    error?: unknown;
  }>((resolve) => {
    let settled = false;
    const settle = (payload: {
      code: number | null;
      signal: NodeJS.Signals | null;
      error?: unknown;
    }): void => {
      if (settled) return;
      settled = true;
      resolve(payload);
    };
    child.on('error', (err) => settle({ code: null, signal: null, error: err }));
    child.on('close', (code, signalCode) => settle({ code, signal: signalCode }));
  });

  clearTimeout(timeoutTimer);
  if (signal) signal.removeEventListener('abort', abortListener);

  const capture: StreamCaptureResult = {
    stdoutBytes: Buffer.concat(stdoutChunks, Math.min(stdoutLen, STDOUT_HARD_CAP)),
    stderrBytes: Buffer.concat(stderrChunks, Math.min(stderrLen, STDERR_HARD_CAP)),
    stdoutTruncated,
    stderrTruncated,
    hardCapExceeded,
  };

  if (exit.error && exit.code === null && exit.signal === null) {
    return {
      outcome: { kind: 'spawn-failed', cause: exit.error },
      capture,
    };
  }

  if (terminationReason) {
    if (terminationReason.kind === 'timeout') {
      return { outcome: { kind: 'timeout' }, capture };
    }
    if (terminationReason.kind === 'cancelled') {
      return { outcome: { kind: 'cancelled' }, capture };
    }
    if (terminationReason.kind === 'stream-hard-cap') {
      return {
        outcome: {
          kind: 'stream-hard-cap',
          streamCap: terminationReason.stream,
          streamObservedBytes: terminationReason.observedBytes,
        },
        capture,
      };
    }
  }

  if (exit.code === null) {
    return {
      outcome: {
        kind: 'host-terminated',
        signal: exit.signal ?? undefined,
      },
      capture,
    };
  }

  return {
    outcome: {
      kind: 'natural-exit',
      exitCode: exit.code,
    },
    capture,
  };
}

async function readFailureTraceDiagnostics(
  invocationDir: string,
  adapterInputSha256: string,
  seams: FsSeams,
): Promise<readonly ReviewTraceDiagnosticV1[] | undefined> {
  const tracePath = path.join(invocationDir, 'trace.json');
  const stat = await statSafeOutputFile('trace', tracePath, seams, { silentOnFailure: true });
  if (!stat) return undefined;
  const bytes = await readSafeOutputBytes('trace', tracePath, seams, { silentOnFailure: true });
  if (!bytes) return undefined;
  let text: string;
  try {
    text = decodeStrictUtf8(bytes);
  } catch {
    return undefined;
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    return undefined;
  }
  const validation = validateReviewTraceV1(parsed);
  if (!validation.ok) return undefined;
  const trace = parsed as ReviewTraceV1;
  if (trace.inputSha256 !== adapterInputSha256) return undefined;
  if (trace.resultSha256 !== undefined) return undefined;
  return trace.diagnostics;
}

async function cleanupInvocationDir(
  invocationDir: string,
  seams: FsSeams,
  hooks: InternalTestHooks | undefined,
): Promise<Error | null> {
  if (hooks?.onBeforeCleanup) {
    try {
      await hooks.onBeforeCleanup(invocationDir);
    } catch {
      // ignore hook errors
    }
  }
  try {
    await seams.rm(invocationDir, { recursive: true, force: true });
    return null;
  } catch (err) {
    return err instanceof Error ? err : new Error(String(err));
  }
}

/**
 * Materialize protocol files, invoke the deterministic C# runtime CLI, and return
 * validated result and trace data. All failure paths raise {@link RuntimeInvocationError}
 * with a discriminated `kind`. See docs/20_architecture/runtime-cli-process-contract.md
 * and issue #33 for the complete adapter contract.
 *
 * This module is internal to the TypeScript action package and is not re-exported
 * from any public barrel. It is unstable until #34 integration.
 */
export async function invokeRuntime(
  options: InvokeRuntimeOptions,
  hooks?: InternalTestHooks,
): Promise<RuntimeInvocationSuccess> {
  assertOptionsShape(options);

  const { command, input, timeoutMs, tempRoot, signal } = options;
  const seams: FsSeams = { ...defaultFsSeams, ...(hooks?.fs ?? {}) };
  const spawnFn = hooks?.spawnOverride ?? spawn;

  if (signal?.aborted) {
    throw new RuntimeInvocationError({
      kind: 'cancelled',
      message: 'AbortSignal was already aborted before invocation began.',
    });
  }

  const inputValidation = validateReviewInputV1(input);
  if (!inputValidation.ok) {
    throw new RuntimeInvocationError({
      kind: 'input-invalid',
      message: `ReviewInputV1 validation failed: ${(inputValidation.errors ?? []).slice(0, 3).join('; ')}`,
    });
  }

  const inputBytes = serializeInputBytes(input);
  if (inputBytes.length > BYTE_LIMITS.input) {
    throw new RuntimeInvocationError({
      kind: 'input-invalid',
      message: `Serialized input exceeds host byte cap (${inputBytes.length} > ${BYTE_LIMITS.input}).`,
    });
  }
  const inputSha256 = sha256Hex(inputBytes);

  if (!(await isExecutableFile(command.executablePath, seams))) {
    throw new RuntimeInvocationError({
      kind: 'executable-invalid',
      message: 'Runtime executable is missing, a symlink, non-regular, or not executable.',
    });
  }

  const invocationDir = await createInvocationDir(tempRoot, seams);

  let primaryError: RuntimeInvocationError | undefined;
  let success: RuntimeInvocationSuccess | undefined;

  try {
    const inputPath = await writeInputFile(invocationDir, inputBytes, seams);
    const resultPath = path.join(invocationDir, 'result.json');
    const tracePath = path.join(invocationDir, 'trace.json');

    const cliArgs = [
      'review',
      '--input',
      inputPath,
      '--output',
      resultPath,
      '--trace',
      tracePath,
    ] as const;

    const { outcome, capture } = await runProcess(
      command,
      cliArgs,
      invocationDir,
      timeoutMs,
      signal,
      spawnFn,
    );

    const stderrSnippet = sanitizeStderrSnippet(capture.stderrBytes, invocationDir);
    const contractViolations = buildContractViolations(capture);

    if (outcome.kind === 'spawn-failed') {
      throw new RuntimeInvocationError({
        kind: 'spawn-failed',
        message: 'Failed to spawn runtime executable.',
        cause: outcome.cause,
      });
    }

    if (outcome.kind === 'timeout') {
      throw new RuntimeInvocationError({
        kind: 'timed-out',
        message: `Runtime did not exit within ${timeoutMs} ms.`,
        stderrSnippet,
      });
    }

    if (outcome.kind === 'cancelled') {
      throw new RuntimeInvocationError({
        kind: 'cancelled',
        message: 'Runtime invocation was cancelled by AbortSignal.',
        stderrSnippet,
      });
    }

    if (outcome.kind === 'stream-hard-cap') {
      throw new RuntimeInvocationError({
        kind: 'stream-limit-exceeded',
        message: `Runtime ${outcome.streamCap} exceeded the host capture limit.`,
        stderrSnippet,
        contractViolations: [
          {
            kind: outcome.streamCap === 'stdout' ? 'stdout-over-capture' : 'stderr-over-capture',
            observedBytes: outcome.streamObservedBytes ?? 0,
          },
        ],
      });
    }

    if (outcome.kind === 'host-terminated') {
      throw new RuntimeInvocationError({
        kind: 'host-terminated',
        message: 'Runtime process ended without an exit code (external signal or OS termination).',
        stderrSnippet,
      });
    }

    const exitCode = outcome.exitCode ?? 0;

    if (exitCode !== 0) {
      const knownClass = KNOWN_EXIT_CLASSES.get(exitCode);
      const failureTraceDiagnostics = await readFailureTraceDiagnostics(
        invocationDir,
        inputSha256,
        seams,
      );
      if (knownClass) {
        const diagnosticCode = parseAprCode(stderrSnippet, knownClass);
        throw new RuntimeInvocationError({
          kind: 'runtime-exit',
          message: `Runtime exited with code ${exitCode} (${knownClass}).`,
          exitCode,
          exitClass: knownClass,
          diagnosticCode,
          stderrSnippet,
          contractViolations,
          failureTraceDiagnostics,
        });
      }
      throw new RuntimeInvocationError({
        kind: 'unknown-exit',
        message: `Runtime exited with unknown code ${exitCode}.`,
        exitCode,
        stderrSnippet,
        contractViolations,
        failureTraceDiagnostics,
      });
    }

    // Exit 0 - validate success outputs (D11).
    const resultStat = await statSafeOutputFile('result', resultPath, seams, {
      silentOnFailure: false,
    });
    if (!resultStat) throw new Error('unreachable');
    const traceStat = await statSafeOutputFile('trace', tracePath, seams, {
      silentOnFailure: false,
    });
    if (!traceStat) throw new Error('unreachable');

    const resultBytes = (await readSafeOutputBytes('result', resultPath, seams, {
      silentOnFailure: false,
    })) as Uint8Array;
    const traceBytes = (await readSafeOutputBytes('trace', tracePath, seams, {
      silentOnFailure: false,
    })) as Uint8Array;

    let resultText: string;
    let traceText: string;
    try {
      resultText = decodeStrictUtf8(resultBytes);
    } catch (cause) {
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: 'result.json is not valid UTF-8.',
        cause,
      });
    }
    try {
      traceText = decodeStrictUtf8(traceBytes);
    } catch (cause) {
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'trace.json is not valid UTF-8.',
        cause,
      });
    }

    let resultParsed: unknown;
    let traceParsed: unknown;
    try {
      resultParsed = JSON.parse(resultText);
    } catch (cause) {
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: 'result.json is not valid JSON.',
        cause,
      });
    }
    try {
      traceParsed = JSON.parse(traceText);
    } catch (cause) {
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'trace.json is not valid JSON.',
        cause,
      });
    }

    const resultValidation = validateReviewResultV1(resultParsed);
    if (!resultValidation.ok) {
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: `ReviewResultV1 validation failed: ${(resultValidation.errors ?? []).slice(0, 3).join('; ')}`,
      });
    }
    const traceValidation = validateReviewTraceV1(traceParsed);
    if (!traceValidation.ok) {
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: `ReviewTraceV1 validation failed: ${(traceValidation.errors ?? []).slice(0, 3).join('; ')}`,
      });
    }
    const result = resultParsed as ReviewResultV1;
    const trace = traceParsed as ReviewTraceV1;

    // M2 CLI-specific postconditions (D11 8-12).
    if (result.inputSha256 === undefined) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'result.inputSha256 must be present on the M2 CLI success path.',
      });
    }
    if (result.trace === undefined) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'result.trace must be present on the M2 CLI success path.',
      });
    }
    if (result.trace.sha256 === undefined) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'result.trace.sha256 must be present on the M2 CLI success path.',
      });
    }
    if (result.trace.path !== undefined) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'result.trace.path must be absent on the M2 CLI success path.',
      });
    }
    if (trace.resultSha256 !== undefined) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'trace.resultSha256 must be absent on the M2 CLI success path.',
      });
    }

    // Hash and version invariants (D11 13-17).
    if (result.inputSha256 !== inputSha256) {
      throw new RuntimeInvocationError({
        kind: 'hash-mismatch',
        message: 'result.inputSha256 does not match adapter-computed inputSha256.',
      });
    }
    if (trace.inputSha256 !== inputSha256) {
      throw new RuntimeInvocationError({
        kind: 'hash-mismatch',
        message: 'trace.inputSha256 does not match adapter-computed inputSha256.',
      });
    }
    const traceBytesSha = sha256Hex(traceBytes);
    if (result.trace.sha256 !== traceBytesSha) {
      throw new RuntimeInvocationError({
        kind: 'hash-mismatch',
        message: 'result.trace.sha256 does not match sha256(traceBytes).',
      });
    }
    if (result.runtimeVersion !== trace.runtimeVersion) {
      throw new RuntimeInvocationError({
        kind: 'version-mismatch',
        message: 'result.runtimeVersion and trace.runtimeVersion differ.',
      });
    }
    if (
      input.requestedRuntimeVersion !== null &&
      input.requestedRuntimeVersion !== result.runtimeVersion
    ) {
      throw new RuntimeInvocationError({
        kind: 'version-mismatch',
        message: 'result.runtimeVersion does not match requestedRuntimeVersion.',
      });
    }

    // Stream shape recheck (D11 18).
    if (capture.stdoutBytes.length !== 0 || capture.stderrBytes.length > STDERR_CONTRACT_LIMIT) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'Runtime violated stream shape rules on the success path.',
        stderrSnippet,
        contractViolations,
      });
    }

    success = {
      result,
      trace,
      inputSha256,
      resultBytes,
      traceBytes,
      runtimeVersion: result.runtimeVersion,
    };
  } catch (err) {
    if (err instanceof RuntimeInvocationError) {
      primaryError = err;
    } else {
      primaryError = new RuntimeInvocationError({
        kind: 'host-io-failed',
        message: 'Unclassified host failure during runtime invocation.',
        cause: err,
      });
    }
  }

  const cleanupError = await cleanupInvocationDir(invocationDir, seams, hooks);

  if (primaryError) {
    throw primaryError;
  }
  if (cleanupError) {
    throw new RuntimeInvocationError({
      kind: 'cleanup-failed',
      message: 'Failed to clean up runtime invocation directory after a successful run.',
      cause: cleanupError,
    });
  }
  if (!success) {
    // Defensive: neither success nor primaryError should be impossible.
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: 'invokeRuntime completed with neither a result nor an error.',
    });
  }
  return success;
}
