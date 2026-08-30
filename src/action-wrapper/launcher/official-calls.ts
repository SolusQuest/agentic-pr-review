export class OfficialCallTracker {
  private readonly active = new Set<Promise<void>>();
  private readonly stdoutWrite = process.stdout.write;
  private readonly stderrWrite = process.stderr.write;
  private sealed = false;

  wrap<T extends object>(client: T): T {
    return new Proxy(client, {
      get: (target, property) => {
        const value = Reflect.get(target, property, target) as unknown;
        if (typeof value !== 'function') return value;
        // These two methods only wipe process-local state. They deliberately
        // remain available during cleanup after the external-call gate seals.
        if (property === 'invalidateArtifactMutation' || property === 'dispose') {
          return (...args: unknown[]): unknown => Reflect.apply(value, target, args);
        }
        return (...args: unknown[]): unknown => {
          if (this.sealed) throw new Error('wrapper_official_calls_sealed');
          const result = Reflect.apply(value, target, args) as unknown;
          if (!isPromiseLike(result)) throw new Error('wrapper_official_call_invalid');
          return this.track(result);
        };
      },
    });
  }

  seal(): void {
    this.sealed = true;
  }

  async awaitQuiescence(timeoutMs: number): Promise<boolean> {
    this.seal();
    const deadline = Date.now() + timeoutMs;
    while (this.active.size > 0) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) return false;
      const settled = await within(Promise.all([...this.active]), remaining);
      if (!settled) return false;
    }
    await new Promise<void>((resolve) => setImmediate(resolve));
    return (
      this.active.size === 0 &&
      process.stdout.write === this.stdoutWrite &&
      process.stderr.write === this.stderrWrite
    );
  }

  private track<T>(promise: PromiseLike<T>): PromiseLike<T> {
    const marker = Promise.resolve(promise).then(
      () => undefined,
      () => undefined,
    );
    this.active.add(marker);
    void marker.then(() => this.active.delete(marker));
    return promise;
  }
}

function isPromiseLike(value: unknown): value is PromiseLike<unknown> {
  return (
    value !== null &&
    (typeof value === 'object' || typeof value === 'function') &&
    typeof (value as { readonly then?: unknown }).then === 'function'
  );
}

async function within(promise: Promise<unknown>, timeoutMs: number): Promise<boolean> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      promise.then(() => true),
      new Promise<false>((resolve) => {
        timer = setTimeout(() => resolve(false), timeoutMs);
      }),
    ]);
  } finally {
    if (timer) clearTimeout(timer);
  }
}
