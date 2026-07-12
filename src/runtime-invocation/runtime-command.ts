import type { ReviewInputV1 } from '../protocol/review-input.js';
import type { ReviewResultV1 } from '../protocol/review-result.js';
import type { ReviewTraceV1 } from '../protocol/review-trace.js';

/**
 * A caller-supplied, trusted host-owned descriptor for the runtime binary to spawn.
 * See #33 D4 and D16. Neither `executablePath` nor `prefixArgs` may be derived from
 * untrusted review content.
 */
export interface RuntimeCommand {
  /** Absolute path to the executable actually spawned. */
  executablePath: string;
  /** Optional args placed before the CLI arguments; used for framework-dependent invocations. */
  prefixArgs?: readonly string[];
}

/**
 * Options accepted by {@link invokeRuntime}. All fields except {@link signal} and
 * {@link tempRoot} are required. Validation happens before any filesystem work.
 */
export interface InvokeRuntimeOptions {
  command: RuntimeCommand;
  input: ReviewInputV1;
  /** Required. Positive finite safe integer. No implicit default. */
  timeoutMs: number;
  signal?: AbortSignal;
  /** Optional trusted absolute host path; defaults to os.tmpdir(). See #33 D6/D15. */
  tempRoot?: string;
}

/**
 * Successful result returned by {@link invokeRuntime}. The adapter has already
 * validated exact-byte hashes, runtime version consistency, and M2 CLI-specific
 * success postconditions before returning.
 */
export interface RuntimeInvocationSuccess {
  result: ReviewResultV1;
  trace: ReviewTraceV1;
  /** Lowercase hex SHA-256 of the exact input bytes written to disk. */
  inputSha256: string;
  resultBytes: Uint8Array;
  traceBytes: Uint8Array;
  runtimeVersion: string;
}
