import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

export class OfficialCallTimeoutError extends Error {
  constructor(readonly settled: Promise<void>) {
    super('artifact_bridge_official_timeout');
    this.name = 'OfficialCallTimeoutError';
  }
}

export class OfficialCallError extends Error {
  constructor(
    readonly causeValue: unknown,
    readonly settled: Promise<void>,
  ) {
    super('artifact_bridge_official_failure');
    this.name = 'OfficialCallError';
  }
}

interface OfficialCallOutcome<T> {
  readonly ok: boolean;
  readonly value?: T;
  readonly error?: unknown;
}

let containmentTail: Promise<void> = Promise.resolve();

export async function runContainedOfficialCall<T>(
  call: (signal: AbortSignal) => Promise<T>,
  remainingLogicalMs: number,
  outerSignal: AbortSignal,
): Promise<T> {
  const queuedAt = Date.now();
  let release!: () => void;
  const preceding = containmentTail;
  containmentTail = new Promise<void>((resolve) => {
    release = resolve;
  });

  const acquired = await waitForTurn(preceding, Math.max(1, remainingLogicalMs), outerSignal);
  if (!acquired) {
    void preceding.then(release);
    throw new OfficialCallTimeoutError(Promise.resolve());
  }
  if (outerSignal.aborted) {
    release();
    throw new OfficialCallTimeoutError(Promise.resolve());
  }
  const logicalRemaining = remainingLogicalMs - (Date.now() - queuedAt);
  if (logicalRemaining <= 0) {
    release();
    throw new OfficialCallTimeoutError(Promise.resolve());
  }
  const timeoutMs = Math.max(
    1,
    Math.min(ARTIFACT_BRIDGE_LIMITS.requestTimeoutMs, logicalRemaining),
  );

  let settledResolve!: () => void;
  const settled = new Promise<void>((resolve) => {
    settledResolve = resolve;
  });
  const outcome = (async (): Promise<OfficialCallOutcome<T>> => {
    const restore = suppressProcessOutput();
    const requestController = new AbortController();
    const abortRequest = (): void => requestController.abort();
    outerSignal.addEventListener('abort', abortRequest, { once: true });
    const requestTimer = setTimeout(abortRequest, timeoutMs);
    requestTimer.unref();
    try {
      return { ok: true, value: await call(requestController.signal) };
    } catch (error) {
      return { ok: false, error };
    } finally {
      clearTimeout(requestTimer);
      outerSignal.removeEventListener('abort', abortRequest);
      restore();
      settledResolve();
      release();
    }
  })();

  const timeout = new Promise<'timeout'>((resolve) => {
    const timer = setTimeout(() => resolve('timeout'), timeoutMs);
    timer.unref();
    void outcome.finally(() => clearTimeout(timer));
  });
  const raced = await Promise.race([outcome, timeout, abortPromise(outerSignal)]);
  if (raced === 'timeout') throw new OfficialCallTimeoutError(settled);
  if (!raced.ok) throw new OfficialCallError(raced.error, settled);
  return raced.value as T;
}

async function waitForTurn(
  preceding: Promise<void>,
  maximumWaitMs: number,
  signal: AbortSignal,
): Promise<boolean> {
  if (signal.aborted) return false;
  return await new Promise<boolean>((resolve) => {
    let complete = false;
    const finish = (acquired: boolean): void => {
      if (complete) return;
      complete = true;
      clearTimeout(timer);
      signal.removeEventListener('abort', onAbort);
      resolve(acquired);
    };
    const onAbort = (): void => finish(false);
    const timer = setTimeout(() => finish(false), maximumWaitMs);
    timer.unref();
    signal.addEventListener('abort', onAbort, { once: true });
    void preceding.then(() => finish(true));
  });
}

function abortPromise(signal: AbortSignal): Promise<'timeout'> {
  return new Promise((resolve) => {
    if (signal.aborted) {
      resolve('timeout');
      return;
    }
    signal.addEventListener('abort', () => resolve('timeout'), {
      once: true,
    });
  });
}

function suppressProcessOutput(): () => void {
  const stdoutWrite = process.stdout.write;
  const stderrWrite = process.stderr.write;
  const discard = (() => true) as typeof process.stdout.write;
  process.stdout.write = discard;
  process.stderr.write = discard as typeof process.stderr.write;
  return () => {
    process.stdout.write = stdoutWrite;
    process.stderr.write = stderrWrite;
  };
}
