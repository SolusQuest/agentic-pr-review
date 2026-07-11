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
const DEFAULT_SIGTERM_GRACE_MS = 5000;

/**
 * Test seams for src/runtime-invocation. Not exported from the module's public entry;
 * consumed only by test files via {@link invokeRuntimeForTests}.
 */
export interface RuntimeInvocationTestSeams {
  fs?: Partial<FsSeams>;
  spawnOverride?: typeof spawn;
  /** Called with the invocation directory before cleanup runs. */
  onBeforeCleanup?: (invocationDir: string) => void | Promise<void>;
  /**
   * Override the POSIX SIGTERM-to-SIGKILL grace period. Tests use a short value so
   * the SIGKILL escalation path is exercised deterministically without a wall-clock wait.
   */
  sigtermGraceMs?: number;
}

interface StreamCaptureResult {
  stdoutBytes: Uint8Array;
  stderrBytes: Uint8Array;
}

type TerminationReason =
  | { kind: 'timeout' }
  | { kind: 'cancelled' }
  | {
      kind: 'stream-hard-cap';
      stream: 'stdout' | 'stderr';
      observedBytes: number;
    };

interface ProcessOutcome {
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

function isValidAbortSignal(value: unknown): value is AbortSignal {
  if (!isNonNullObject(value)) return false;
  const candidate = value as {
    aborted?: unknown;
    addEventListener?: unknown;
    removeEventListener?: unknown;
  };
  return (
    typeof candidate.aborted === 'boolean' &&
    typeof candidate.addEventListener === 'function' &&
    typeof candidate.removeEventListener === 'function'
  );
}

function sanitizeCauseCode(cause: unknown): string | undefined {
  if (cause === null || cause === undefined) return undefined;
  if (typeof cause !== 'object') return undefined;
  const code = (cause as { code?: unknown }).code;
  return typeof code === 'string' && /^[A-Z0-9_]{1,32}$/.test(code) ? code : undefined;
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
  if (signal !== undefined && !isValidAbortSignal(signal)) {
    throw new RuntimeInvocationError({
      kind: 'options-invalid',
      message: 'options.signal must be a valid AbortSignal.',
    });
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
  if (bytes.length === 0) return undefined;
  const nlIndex = bytes.indexOf(0x0a);
  const cap = STDERR_CONTRACT_LIMIT;
  const end = nlIndex >= 0 ? Math.min(nlIndex, cap) : Math.min(bytes.length, cap);
  const lineBytes = bytes.subarray(0, end);
  if (lineBytes.length === 0) return undefined;
  let text: string;
  try {
    text = decodeStrictUtf8(lineBytes);
  } catch {
    return undefined;
  }
  if (invocationDir.length > 0 && text.includes(invocationDir)) return undefined;
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
  sigtermGraceMs: number,
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
      outcome: {
        kind: 'spawn-failed',
        spawnErrorCode: sanitizeCauseCode(cause),
      },
      capture: { stdoutBytes: new Uint8Array(), stderrBytes: new Uint8Array() },
    };
  }

  const stdoutChunks: Buffer[] = [];
  const stderrChunks: Buffer[] = [];
  let stdoutLen = 0;
  let stderrLen = 0;
  let terminationReason: TerminationReason | undefined;
  let sigkillTimer: NodeJS.Timeout | undefined;
  let closed = false;

  const killChild = (): void => {
    if (closed) return;
    if (process.platform === 'win32') {
      try {
        child.kill('SIGKILL');
      } catch {
        // ignore
      }
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
      // check whether the process has actually exited.
      if (closed) return;
      if (child.exitCode !== null || child.signalCode !== null) return;
      try {
        child.kill('SIGKILL');
      } catch {
        // ignore
      }
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
      closed = true;
      resolve(payload);
    };
    child.on('error', (err) => settle({ code: null, signal: null, error: err }));
    child.on('close', (code, signalCode) => settle({ code, signal: signalCode }));
  });

  clearTimeout(timeoutTimer);
  if (sigkillTimer) clearTimeout(sigkillTimer);
  if (signal) signal.removeEventListener('abort', abortListener);

  const capture: StreamCaptureResult = {
    stdoutBytes: Buffer.concat(stdoutChunks, Math.min(stdoutLen, STDOUT_HARD_CAP)),
    stderrBytes: Buffer.concat(stderrChunks, Math.min(stderrLen, STDERR_HARD_CAP)),
  };

  if (exit.error && exit.code === null && exit.signal === null) {
    return {
      outcome: {
        kind: 'spawn-failed',
        spawnErrorCode: sanitizeCauseCode(exit.error),
      },
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
      outcome: { kind: 'host-terminated' },
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
  seamsForHooks: RuntimeInvocationTestSeams | undefined,
): Promise<Error | null> {
  if (seamsForHooks?.onBeforeCleanup) {
    try {
      await seamsForHooks.onBeforeCleanup(invocationDir);
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

async function runInvocation(
  options: InvokeRuntimeOptions,
  testSeams: RuntimeInvocationTestSeams | undefined,
): Promise<RuntimeInvocationSuccess> {
  assertOptionsShape(options);

  const { command, input, timeoutMs, tempRoot, signal } = options;
  const seams: FsSeams = { ...defaultFsSeams, ...(testSeams?.fs ?? {}) };
  const spawnFn = testSeams?.spawnOverride ?? spawn;
  const sigtermGraceMs = testSeams?.sigtermGraceMs ?? DEFAULT_SIGTERM_GRACE_MS;

  if (signal?.aborted) {
    throw new RuntimeInvocationError({
      kind: 'cancelled',
      message: 'AbortSignal was already aborted before invocation began.',
    });
  }

  const inputValidation = validateReviewInputV1(input);
  if (!inputValidation.ok) {
    const count = inputValidation.errors?.length ?? 0;
    throw new RuntimeInvocationError({
      kind: 'input-invalid',
      message:
        count > 0
          ? `ReviewInputV1 schema validation failed (${count} errors).`
          : 'ReviewInputV1 schema validation failed.',
    });
  }

  const inputBytes = serializeInputBytes(input);
  if (inputBytes.length > BYTE_LIMITS.input) {
    throw new RuntimeInvocationError({
      kind: 'input-invalid',
      message: 'Serialized input exceeds host byte cap.',
    });
  }
  const inputSha256 = sha256Hex(inputBytes);

  if (signal?.aborted) {
    throw new RuntimeInvocationError({
      kind: 'cancelled',
      message: 'AbortSignal aborted during preflight.',
    });
  }

  if (!(await isExecutableFile(command.executablePath, seams))) {
    throw new RuntimeInvocationError({
      kind: 'executable-invalid',
      message: 'Runtime executable is missing, a symlink, non-regular, or not executable.',
    });
  }

  if (signal?.aborted) {
    throw new RuntimeInvocationError({
      kind: 'cancelled',
      message: 'AbortSignal aborted during preflight.',
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
      sigtermGraceMs,
    );

    const stderrSnippet = sanitizeStderrSnippet(capture.stderrBytes, invocationDir);
    const contractViolations = buildContractViolations(capture);

    if (outcome.kind === 'spawn-failed') {
      throw new RuntimeInvocationError({
        kind: 'spawn-failed',
        message: 'Failed to spawn runtime executable.',
        diagnosticCode: outcome.spawnErrorCode,
      });
    }

    if (outcome.kind === 'timeout') {
      throw new RuntimeInvocationError({
        kind: 'timed-out',
        message: 'Runtime did not exit within the configured timeout.',
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
        message: 'Runtime output exceeded the host stream capture limit.',
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
          message: 'Runtime exited with a documented non-zero code.',
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
        message: 'Runtime exited with an undocumented non-zero code.',
        exitCode,
        stderrSnippet,
        contractViolations,
        failureTraceDiagnostics,
      });
    }

    // Exit 0 - enforce D9 stream shape first, then D11 output validation.
    if (capture.stdoutBytes.length !== 0 || capture.stderrBytes.length > STDERR_CONTRACT_LIMIT) {
      throw new RuntimeInvocationError({
        kind: 'process-contract-violation',
        message: 'Runtime violated stream shape rules on exit 0.',
        stderrSnippet,
        contractViolations,
      });
    }

    await statSafeOutputFile('result', resultPath, seams, { silentOnFailure: false });
    await statSafeOutputFile('trace', tracePath, seams, { silentOnFailure: false });

    const resultBytes = await readSafeOutputBytes('result', resultPath, seams, {
      silentOnFailure: false,
    });
    const traceBytes = await readSafeOutputBytes('trace', tracePath, seams, {
      silentOnFailure: false,
    });

    let resultText: string;
    let traceText: string;
    try {
      resultText = decodeStrictUtf8(resultBytes);
    } catch {
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: 'result.json is not valid UTF-8.',
      });
    }
    try {
      traceText = decodeStrictUtf8(traceBytes);
    } catch {
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'trace.json is not valid UTF-8.',
      });
    }

    let resultParsed: unknown;
    let traceParsed: unknown;
    try {
      resultParsed = JSON.parse(resultText);
    } catch {
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: 'result.json is not valid JSON.',
      });
    }
    try {
      traceParsed = JSON.parse(traceText);
    } catch {
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: 'trace.json is not valid JSON.',
      });
    }

    const resultValidation = validateReviewResultV1(resultParsed);
    if (!resultValidation.ok) {
      const count = resultValidation.errors?.length ?? 0;
      throw new RuntimeInvocationError({
        kind: 'result-invalid',
        message: `ReviewResultV1 schema validation failed (${count} errors).`,
      });
    }
    const traceValidation = validateReviewTraceV1(traceParsed);
    if (!traceValidation.ok) {
      const count = traceValidation.errors?.length ?? 0;
      throw new RuntimeInvocationError({
        kind: 'trace-invalid',
        message: `ReviewTraceV1 schema validation failed (${count} errors).`,
      });
    }
    const result = resultParsed as ReviewResultV1;
    const trace = traceParsed as ReviewTraceV1;

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
        diagnosticCode: sanitizeCauseCode(err),
      });
    }
  }

  const cleanupError = await cleanupInvocationDir(invocationDir, seams, testSeams);

  if (primaryError) {
    throw primaryError;
  }
  if (cleanupError) {
    throw new RuntimeInvocationError({
      kind: 'cleanup-failed',
      message: 'Failed to clean up runtime invocation directory after a successful run.',
      diagnosticCode: sanitizeCauseCode(cleanupError),
    });
  }
  if (!success) {
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: 'invokeRuntime completed with neither a result nor an error.',
    });
  }
  return success;
}

/**
 * Materialize protocol files, invoke the deterministic C# runtime CLI, and return
 * validated result and trace data. All failure paths raise {@link RuntimeInvocationError}
 * with a discriminated `kind`. See docs/20_architecture/runtime-cli-process-contract.md
 * and issue #33 for the complete adapter contract.
 *
 * The public entrypoint takes a single `options` object; test seams are not exposed here.
 */
export function invokeRuntime(options: InvokeRuntimeOptions): Promise<RuntimeInvocationSuccess> {
  return runInvocation(options, undefined);
}

/**
 * Test-only entrypoint. Consumed by src/runtime-invocation/*.test.ts to exercise
 * host-I/O, cleanup, and process seams without touching the production API surface.
 * Do not import this from action wiring, #34, or any release build path.
 *
 * @internal
 */
export function invokeRuntimeForTests(
  options: InvokeRuntimeOptions,
  seams: RuntimeInvocationTestSeams,
): Promise<RuntimeInvocationSuccess> {
  return runInvocation(options, seams);
}
