import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export class ArtifactBridgeDeadlineError extends Error {
  constructor() {
    super('artifact_bridge_deadline_exceeded');
    this.name = 'ArtifactBridgeDeadlineError';
  }
}

export class ArtifactBridgeOperationBudget {
  private readonly controller = new AbortController();
  private readonly timeout: NodeJS.Timeout;
  private readonly abortFromCaller: () => void;

  constructor(
    private readonly callerSignal: AbortSignal,
    private readonly now: () => number,
    private readonly startedAt: number,
  ) {
    this.abortFromCaller = () => this.controller.abort(callerSignal.reason);
    if (callerSignal.aborted) {
      this.abortFromCaller();
    } else {
      callerSignal.addEventListener('abort', this.abortFromCaller, { once: true });
    }
    this.timeout = setTimeout(
      () => this.controller.abort(new ArtifactBridgeDeadlineError()),
      ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs,
    );
    this.timeout.unref();
  }

  get signal(): AbortSignal {
    return this.controller.signal;
  }

  remainingMs(): number {
    this.throwIfExpired();
    return ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs - (this.now() - this.startedAt);
  }

  throwIfExpired(): void {
    if (
      this.controller.signal.aborted ||
      this.now() - this.startedAt >= ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs
    ) {
      throw new ArtifactBridgeDeadlineError();
    }
  }

  dispose(): void {
    clearTimeout(this.timeout);
    this.callerSignal.removeEventListener('abort', this.abortFromCaller);
  }
}
