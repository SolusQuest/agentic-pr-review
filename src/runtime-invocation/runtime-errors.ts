import type { ReviewTraceDiagnosticV1 } from '../protocol/review-trace.js';

/**
 * Discriminated failure kinds surfaced by {@link RuntimeInvocationError}.
 * See issue #33 D13 for the full taxonomy and the semantic mapping of each kind.
 */
export type RuntimeInvocationErrorKind =
  | 'options-invalid'
  | 'input-invalid'
  | 'executable-invalid'
  | 'spawn-failed'
  | 'timed-out'
  | 'cancelled'
  | 'host-terminated'
  | 'host-io-failed'
  | 'stream-limit-exceeded'
  | 'runtime-exit'
  | 'unknown-exit'
  | 'missing-output'
  | 'unsafe-output-file'
  | 'result-invalid'
  | 'trace-invalid'
  | 'hash-mismatch'
  | 'version-mismatch'
  | 'process-contract-violation'
  | 'cleanup-failed';

export type RuntimeExitClass = 'usage' | 'contract' | 'runtime' | 'provider' | 'file-io';

/**
 * Bounded metadata describing a stream-shape deviation. Only the shape kind and observed
 * byte count are exposed; raw stream samples never appear on the public error surface.
 */
export interface RuntimeContractViolation {
  kind: 'stdout-nonempty' | 'stdout-over-capture' | 'stderr-over-contract' | 'stderr-over-capture';
  observedBytes: number;
}

export interface RuntimeInvocationErrorInit {
  kind: RuntimeInvocationErrorKind;
  message: string;
  exitCode?: number;
  exitClass?: RuntimeExitClass;
  diagnosticCode?: string;
  stderrSnippet?: string;
  contractViolations?: readonly RuntimeContractViolation[];
  failureTraceDiagnostics?: readonly ReviewTraceDiagnosticV1[];
  cause?: unknown;
}

/**
 * All failures raised by {@link invokeRuntime}. Callers switch on {@link kind} to
 * classify the failure. Underlying causes may be attached internally but are not
 * re-emitted verbatim to callers of the action layer (#34).
 */
export class RuntimeInvocationError extends Error {
  readonly kind: RuntimeInvocationErrorKind;
  readonly exitCode?: number;
  readonly exitClass?: RuntimeExitClass;
  readonly diagnosticCode?: string;
  readonly stderrSnippet?: string;
  readonly contractViolations?: readonly RuntimeContractViolation[];
  readonly failureTraceDiagnostics?: readonly ReviewTraceDiagnosticV1[];

  constructor(init: RuntimeInvocationErrorInit) {
    super(init.message, init.cause !== undefined ? { cause: init.cause } : undefined);
    this.name = 'RuntimeInvocationError';
    this.kind = init.kind;
    if (init.exitCode !== undefined) this.exitCode = init.exitCode;
    if (init.exitClass !== undefined) this.exitClass = init.exitClass;
    if (init.diagnosticCode !== undefined) this.diagnosticCode = init.diagnosticCode;
    if (init.stderrSnippet !== undefined) this.stderrSnippet = init.stderrSnippet;
    if (init.contractViolations !== undefined) this.contractViolations = init.contractViolations;
    if (init.failureTraceDiagnostics !== undefined)
      this.failureTraceDiagnostics = init.failureTraceDiagnostics;
  }
}

/**
 * Documented C# runtime exit codes; see docs/20_architecture/runtime-cli-process-contract.md.
 * The numeric exit code is the primary classification for host code.
 */
export const KNOWN_EXIT_CLASSES: ReadonlyMap<number, RuntimeExitClass> = new Map([
  [2, 'usage'],
  [10, 'contract'],
  [20, 'runtime'],
  [30, 'provider'],
  [40, 'file-io'],
]);

/**
 * Documented APR_* diagnostic codes and their associated exit classes. Used to
 * cross-check first-line stderr diagnostics against the observed numeric exit.
 * Codes whose class does not match the observed exit are ignored.
 */
export const KNOWN_APR_CODES: ReadonlyMap<string, RuntimeExitClass> = new Map([
  ['APR_USAGE_INVALID', 'usage'],
  ['APR_INPUT_READ_FAILED', 'file-io'],
  ['APR_INPUT_JSON_INVALID', 'contract'],
  ['APR_INPUT_SCHEMA_INVALID', 'contract'],
  ['APR_PROTOCOL_VERSION_UNSUPPORTED', 'contract'],
  ['APR_RUNTIME_VERSION_MISMATCH', 'contract'],
  ['APR_RUNTIME_INTERNAL', 'runtime'],
  ['APR_OUTPUT_SELF_VALIDATION_FAILED', 'runtime'],
  ['APR_PROVIDER_FAILED', 'provider'],
  ['APR_TRACE_WRITE_FAILED', 'file-io'],
  ['APR_RESULT_WRITE_FAILED', 'file-io'],
]);
