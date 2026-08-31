/**
 * One process-local lane for an artifact bridge executor.
 *
 * A bridge connection may arrive concurrently, but a lifecycle is not a set
 * of independent REST calls: a successful conditional observation is consumed
 * by a later verification or mutation.  Serialising the whole
 * lookup-through-commit lifecycle prevents an invalidation from racing a 304
 * reuse, and makes a compound mutation reservation meaningful.
 */
export class ArtifactLifecycleCoordinator {
  private tail: Promise<void> = Promise.resolve();
  private accepting = true;
  private active = 0;
  private readonly idleWaiters = new Set<() => void>();

  async run<T>(signal: AbortSignal, work: () => Promise<T>): Promise<T> {
    if (!this.accepting) throw new ArtifactLifecycleCoordinatorStoppedError();
    if (signal.aborted) throw abortError();
    const previous = this.tail;
    let release: () => void = () => undefined;
    this.tail = new Promise<void>((resolve) => {
      release = resolve;
    });
    try {
      await waitForTurn(previous, signal);
    } catch (error) {
      // The queue position still has to be released in order after its
      // predecessor.  Releasing it immediately would let a later command
      // overtake work that was already admitted.
      void previous.then(release, release);
      throw error;
    }
    if (!this.accepting || signal.aborted) {
      release();
      this.notifyIdle();
      if (!this.accepting) throw new ArtifactLifecycleCoordinatorStoppedError();
      throw abortError();
    }
    this.active += 1;
    try {
      return await work();
    } finally {
      this.active -= 1;
      release();
      this.notifyIdle();
    }
  }

  stopIntake(): void {
    this.accepting = false;
    this.notifyIdle();
  }

  async drain(): Promise<void> {
    await this.tail;
    if (this.active === 0) return;
    await new Promise<void>((resolve) => this.idleWaiters.add(resolve));
  }

  private notifyIdle(): void {
    if (this.active !== 0) return;
    for (const resolve of this.idleWaiters) resolve();
    this.idleWaiters.clear();
  }
}

export class ArtifactLifecycleCoordinatorStoppedError extends Error {
  constructor() {
    super('artifact_lifecycle_coordinator_stopped');
    this.name = 'ArtifactLifecycleCoordinatorStoppedError';
  }
}

function waitForTurn(previous: Promise<void>, signal: AbortSignal): Promise<void> {
  return new Promise<void>((resolve, reject) => {
    const abort = (): void => {
      signal.removeEventListener('abort', abort);
      reject(abortError());
    };
    signal.addEventListener('abort', abort, { once: true });
    void previous.then(
      () => {
        signal.removeEventListener('abort', abort);
        resolve();
      },
      () => {
        signal.removeEventListener('abort', abort);
        resolve();
      },
    );
  });
}

function abortError(): Error {
  const error = new Error('artifact_lifecycle_coordinator_cancelled');
  error.name = 'AbortError';
  return error;
}
