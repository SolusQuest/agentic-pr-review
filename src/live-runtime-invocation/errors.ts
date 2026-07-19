export type LiveRuntimeErrorKind =
  | 'options-invalid'
  | 'platform-unsupported'
  | 'restore-plan-invalid'
  | 'context-invalid'
  | 'predecessor-ledger-invalid'
  | 'executable-invalid'
  | 'spawn-failed'
  | 'timed-out'
  | 'cancelled'
  | 'host-terminated'
  | 'stream-limit-exceeded'
  | 'runtime-exit'
  | 'unknown-exit'
  | 'missing-output'
  | 'unsafe-output-file'
  | 'result-invalid'
  | 'trace-invalid'
  | 'candidate-ledger-invalid'
  | 'provider-metadata-invalid'
  | 'binding-mismatch'
  | 'privacy-violation'
  | 'local-bundle-invalid'
  | 'local-commit-failed';

export class LiveRuntimeInvocationError extends Error {
  readonly kind: LiveRuntimeErrorKind;
  readonly exitCode?: number;
  readonly closeObserved?: boolean;
  readonly cleanupWarnings: readonly string[];

  constructor(init: {
    kind: LiveRuntimeErrorKind;
    message: string;
    exitCode?: number;
    closeObserved?: boolean;
    cleanupWarnings?: readonly string[];
  }) {
    super(init.message);
    this.name = 'LiveRuntimeInvocationError';
    this.kind = init.kind;
    this.exitCode = init.exitCode;
    this.closeObserved = init.closeObserved;
    this.cleanupWarnings = [...(init.cleanupWarnings ?? [])];
  }
}
