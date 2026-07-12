import { spawn } from 'node:child_process';
import path from 'node:path';
import { validateReviewInputV1 } from '../protocol/review-input.js';
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
import { runProcess, type StreamCaptureResult } from './process-runner.js';
import { sanitizeErrorCode } from './sanitizers.js';
import { assertOptionsShape } from './options-validation.js';
import { validateSuccessAndBuildResult } from './success-validator.js';

export type { InvokeRuntimeOptions, RuntimeCommand, RuntimeInvocationSuccess };
export {
  RuntimeInvocationError,
  type RuntimeContractViolation,
  type RuntimeExitClass,
} from './runtime-errors.js';
export type { RuntimeInvocationErrorKind } from './runtime-errors.js';

const STDERR_CONTRACT_LIMIT = 1000;

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
  /**
   * Override the bounded wait for 'close' after the final kill has been issued.
   * Tests use a short value so the fallback path is exercised without waiting.
   */
  closeGraceMs?: number;
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

export async function runInvocation(
  options: InvokeRuntimeOptions,
  testSeams: RuntimeInvocationTestSeams | undefined,
): Promise<RuntimeInvocationSuccess> {
  assertOptionsShape(options);

  const { command, input, timeoutMs, tempRoot, signal } = options;
  const seams: FsSeams = { ...defaultFsSeams, ...(testSeams?.fs ?? {}) };
  const spawnFn = testSeams?.spawnOverride ?? spawn;
  const sigtermGraceMs = testSeams?.sigtermGraceMs;
  const closeGraceMs = testSeams?.closeGraceMs;

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
  // Default true: pre-spawn failures (options-invalid, input-invalid, executable-invalid,
  // early input write failures, etc.) never expose a live child to the invocation
  // directory, so cleanup is always safe until we prove otherwise from runProcess.
  let childCloseObserved = true;

  try {
    const inputPath = await writeInputFile(invocationDir, inputBytes, seams);
    if (signal?.aborted) {
      throw new RuntimeInvocationError({
        kind: 'cancelled',
        message: 'AbortSignal aborted before spawn.',
      });
    }
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

    const { outcome, capture } = await runProcess({
      command,
      cliArgs,
      invocationDir,
      timeoutMs,
      signal,
      spawnFn,
      sigtermGraceMs,
      closeGraceMs,
    });
    childCloseObserved = outcome.closeObserved;

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

    success = await validateSuccessAndBuildResult({
      resultPath,
      tracePath,
      input,
      inputSha256,
      seams,
    });
  } catch (err) {
    if (err instanceof RuntimeInvocationError) {
      primaryError = err;
    } else {
      primaryError = new RuntimeInvocationError({
        kind: 'host-io-failed',
        message: 'Unclassified host failure during runtime invocation.',
        diagnosticCode: sanitizeErrorCode(err),
      });
    }
  }

  // Only clean up the invocation directory when we have positive evidence that the
  // child process has actually released it. If the bounded post-kill close deadline
  // fired without an observed 'close' event, the child may still be running and could
  // race with recursive rm on its cwd; in that case we deliberately leak the directory
  // and surface the primary error. Callers may inspect stale invocation directories
  // under tempRoot when investigating such runs.
  const cleanupError = childCloseObserved
    ? await cleanupInvocationDir(invocationDir, seams, testSeams)
    : undefined;

  if (primaryError) {
    throw primaryError;
  }
  if (cleanupError) {
    throw new RuntimeInvocationError({
      kind: 'cleanup-failed',
      message: 'Failed to clean up runtime invocation directory after a successful run.',
      diagnosticCode: sanitizeErrorCode(cleanupError),
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
