const MAXIMUM_COMBINED_ARTIFACT_CACHE_BYTES = 64 * 1024 * 1024;

/**
 * Shared LRU accounting for conditional API representations and verified
 * artifact records.  The cache owners retain type-specific removal and buffer
 * wiping; this ledger only decides which oldest entry must leave so the two
 * caches cannot each silently consume 64 MiB.
 */
export class ArtifactCacheLedger {
  private readonly entries = new Map<ArtifactCacheLedgerToken, LedgerEntry>();
  private totalBytes = 0;
  private disposed = false;

  constructor(readonly maximumBytes = MAXIMUM_COMBINED_ARTIFACT_CACHE_BYTES) {
    if (!Number.isSafeInteger(maximumBytes) || maximumBytes < 1) {
      throw new Error('artifact_cache_ledger_limit_invalid');
    }
    if (maximumBytes > MAXIMUM_COMBINED_ARTIFACT_CACHE_BYTES) {
      throw new Error('artifact_cache_ledger_limit_exceeds_process_cap');
    }
  }

  claim(bytes: number, evict: () => void): ArtifactCacheLedgerToken | undefined {
    if (this.disposed || !Number.isSafeInteger(bytes) || bytes < 1 || bytes > this.maximumBytes) {
      return undefined;
    }
    const token: ArtifactCacheLedgerToken = {};
    this.entries.set(token, { bytes, evict });
    this.totalBytes += bytes;
    this.evict();
    return this.entries.has(token) ? token : undefined;
  }

  touch(token: ArtifactCacheLedgerToken | undefined): void {
    if (!token) return;
    const entry = this.entries.get(token);
    if (!entry) return;
    this.entries.delete(token);
    this.entries.set(token, entry);
  }

  release(token: ArtifactCacheLedgerToken | undefined): void {
    if (!token) return;
    const entry = this.entries.get(token);
    if (!entry) return;
    this.entries.delete(token);
    this.totalBytes -= entry.bytes;
  }

  dispose(): void {
    if (this.disposed) return;
    this.disposed = true;
    this.clear();
  }

  clear(): void {
    while (this.entries.size > 0) {
      const token = this.entries.keys().next().value as ArtifactCacheLedgerToken | undefined;
      if (!token) break;
      const entry = this.entries.get(token)!;
      this.entries.delete(token);
      this.totalBytes -= entry.bytes;
      entry.evict();
    }
  }

  private evict(): void {
    while (this.totalBytes > this.maximumBytes) {
      const token = this.entries.keys().next().value as ArtifactCacheLedgerToken | undefined;
      if (!token) return;
      const entry = this.entries.get(token)!;
      this.entries.delete(token);
      this.totalBytes -= entry.bytes;
      entry.evict();
    }
  }
}

export type ArtifactCacheLedgerToken = object;

interface LedgerEntry {
  readonly bytes: number;
  readonly evict: () => void;
}

export { MAXIMUM_COMBINED_ARTIFACT_CACHE_BYTES };
