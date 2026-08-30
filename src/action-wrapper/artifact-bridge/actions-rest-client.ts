import type { getOctokit } from '@actions/github';

import { ArtifactRestRequestBudget } from './artifact-rest-request-budget.js';
import { ArtifactCacheLedger, type ArtifactCacheLedgerToken } from './artifact-cache-ledger.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';
import type { ArtifactActionsRestClient } from './official-artifact-operations.js';

type ActionsOctokit = ReturnType<typeof getOctokit>;
type HeadersLike =
  | Headers
  | Readonly<Record<string, string | string[] | undefined>>
  | { readonly get: (name: string) => string | null | undefined };

const MAXIMUM_CONDITIONAL_GET_CACHE_ENTRIES = 1_024;
const MAXIMUM_CONDITIONAL_GET_CACHE_BYTES = 64 * 1024 * 1024;

export interface ConditionalGetCacheLimits {
  readonly maximumEntries: number;
  readonly maximumBytes: number;
}

const DEFAULT_CONDITIONAL_GET_CACHE_LIMITS: ConditionalGetCacheLimits = Object.freeze({
  maximumEntries: MAXIMUM_CONDITIONAL_GET_CACHE_ENTRIES,
  maximumBytes: MAXIMUM_CONDITIONAL_GET_CACHE_BYTES,
});

export function createArtifactActionsRestClient(
  octokit: ActionsOctokit,
  budget: ArtifactRestRequestBudget,
  cacheLimits: ConditionalGetCacheLimits = DEFAULT_CONDITIONAL_GET_CACHE_LIMITS,
  ledger = new ArtifactCacheLedger(),
): ArtifactActionsRestClient {
  const cache = new ConditionalGetCache(cacheLimits, ledger);
  return {
    invalidateRepository: (input) => {
      cache.deleteRepository(input.owner, input.repo);
    },
    dispose: () => cache.dispose(),
    listArtifactsForRepo: async (input, signal, latestAttemptStartAt) => {
      const key = ['list', input.owner, input.repo, input.name, input.per_page, input.page].join(
        '\u0000',
      );
      return await cache.conditionalGet(
        key,
        budget,
        signal,
        latestAttemptStartAt,
        async (etag) =>
          await octokit.rest.actions.listArtifactsForRepo({
            ...input,
            ...conditionalRequestOptions(signal, etag),
          }),
      );
    },
    getArtifact: async (input, signal, latestAttemptStartAt) => {
      const key = ['artifact', input.owner, input.repo, input.artifact_id].join('\u0000');
      try {
        return await cache.conditionalGet(
          key,
          budget,
          signal,
          latestAttemptStartAt,
          async (etag) =>
            await octokit.rest.actions.getArtifact({
              ...input,
              ...conditionalRequestOptions(signal, etag),
            }),
        );
      } catch (error) {
        if (responseStatus(error) === 404) cache.delete(key);
        throw error;
      }
    },
    downloadArtifactArchive: async (input, signal, latestAttemptStartAt) => {
      const { maximum_bytes: maximumBytes, ...request } = input;
      const redirect = await authenticatedApiCall(
        budget,
        { signal, secondaryLimitPoints: 1, mutative: false, latestAttemptStartAt },
        async () =>
          await octokit.rest.actions.downloadArtifact({
            ...request,
            archive_format: 'zip',
            request: { redirect: 'manual', signal },
          }),
      );
      const location = redirect.headers.location;
      if (redirect.status !== 302 || typeof location !== 'string') {
        throw new ArtifactArchiveDownloadError();
      }
      const response = await fetch(location, {
        method: 'GET',
        redirect: 'error',
        signal,
      });
      if (response.status !== 200 || response.body === null) {
        throw new ArtifactArchiveDownloadError();
      }
      const declaredLength = response.headers.get('content-length');
      if (
        declaredLength !== null &&
        (!/^(0|[1-9][0-9]*)$/.test(declaredLength) || Number(declaredLength) > maximumBytes)
      ) {
        await response.body.cancel();
        throw new ArtifactArchiveDownloadError();
      }
      const reader = response.body.getReader();
      const chunks: Buffer[] = [];
      let total = 0;
      try {
        for (;;) {
          const chunk = await reader.read();
          if (chunk.done) break;
          if (total + chunk.value.byteLength > maximumBytes) {
            await reader.cancel();
            throw new ArtifactArchiveDownloadError();
          }
          chunks.push(Buffer.from(chunk.value));
          total += chunk.value.byteLength;
        }
      } catch {
        throw new ArtifactArchiveDownloadError();
      } finally {
        reader.releaseLock();
      }
      if (total < 1) throw new ArtifactArchiveDownloadError();
      return { status: 200, data: Buffer.concat(chunks, total) };
    },
    getWorkflowRunAttempt: async (input, signal, latestAttemptStartAt) => {
      const key = ['attempt', input.owner, input.repo, input.run_id, input.attempt_number].join(
        '\u0000',
      );
      return await cache.conditionalGet(
        key,
        budget,
        signal,
        latestAttemptStartAt,
        async (etag) =>
          await octokit.rest.actions.getWorkflowRunAttempt({
            ...input,
            ...conditionalRequestOptions(signal, etag),
          }),
      );
    },
    deleteArtifact: async (input, signal, latestAttemptStartAt, onDispatched) => {
      cache.deleteRepository(input.owner, input.repo);
      return await authenticatedApiCall(
        budget,
        { signal, secondaryLimitPoints: 5, mutative: true, latestAttemptStartAt },
        async () => {
          // The lifecycle phase is a wire-truth marker, not a limiter marker.
          // `authenticatedApiCall` has already admitted this attempt through the
          // FIFO limiter at this point; mark immediately before Octokit can
          // construct/dispatch its HTTP request.
          onDispatched?.();
          const pending = octokit.rest.actions.deleteArtifact({
            ...input,
            request: { signal },
          });
          return await pending;
        },
      );
    },
  };
}

async function authenticatedApiCall<T extends { readonly status: number }>(
  budget: ArtifactRestRequestBudget,
  dispatch: {
    readonly signal: AbortSignal;
    readonly secondaryLimitPoints: 1 | 5;
    readonly mutative: boolean;
    readonly latestAttemptStartAt?: number;
  },
  call: () => Promise<T>,
): Promise<T> {
  return await budget.runAuthenticatedApiCall(dispatch, call);
}

class ConditionalGetCache {
  private readonly entries = new Map<string, ConditionalGetEntry<unknown>>();
  private totalBytes = 0;

  constructor(
    private readonly limits: ConditionalGetCacheLimits,
    private readonly ledger: ArtifactCacheLedger,
  ) {
    if (!positiveInteger(limits.maximumEntries) || !positiveInteger(limits.maximumBytes)) {
      throw new Error('conditional_get_cache_limits_invalid');
    }
  }

  async conditionalGet<T extends { readonly status: number; readonly data: unknown }>(
    key: string,
    budget: ArtifactRestRequestBudget,
    signal: AbortSignal,
    latestAttemptStartAt: number | undefined,
    request: (etag: string | undefined) => Promise<T>,
  ): Promise<T> {
    const cached = this.get<T>(key);
    try {
      const response = await authenticatedApiCall(
        budget,
        { signal, secondaryLimitPoints: 1, mutative: false, latestAttemptStartAt },
        async () => await request(cached?.etag),
      );
      if (response.status === 304) {
        if (!cached) throw new ConditionalGetCacheError();
        return { ...response, status: 200, data: clone(cached.data) } as T;
      }
      if (response.status === 200) this.store(key, response);
      else if (response.status === 404) this.delete(key);
      return response;
    } catch (error) {
      if (responseStatus(error) === 304 && cached) {
        return { status: 200, data: clone(cached.data) } as T;
      }
      if (responseStatus(error) === 404) this.delete(key);
      throw error;
    }
  }

  delete(key: string): void {
    this.deleteEntry(key);
  }

  deleteRepository(owner: string, repo: string): void {
    const suffix = '\u0000' + owner + '\u0000' + repo + '\u0000';
    for (const key of this.entries.keys()) {
      if (key.includes(suffix)) this.deleteEntry(key);
    }
  }

  dispose(): void {
    for (const key of [...this.entries.keys()]) this.deleteEntry(key);
  }

  private get<T>(key: string): ConditionalGetEntry<T> | undefined {
    const entry = this.entries.get(key) as ConditionalGetEntry<T> | undefined;
    if (!entry) return undefined;
    this.entries.delete(key);
    this.entries.set(key, entry);
    this.ledger.touch(entry.ledgerToken);
    return entry;
  }

  private store<
    T extends { readonly status: number; readonly data: unknown; readonly headers?: HeadersLike },
  >(key: string, response: T): void {
    const etag = header(response.headers, 'etag');
    const bytes = cacheableDocumentBytes(response.data);
    if (!validEtag(etag) || bytes === undefined) {
      this.delete(key);
      return;
    }
    this.deleteEntry(key);
    const entry: ConditionalGetEntry<unknown> = {
      etag,
      data: clone(response.data),
      bytes,
      ledgerToken: undefined,
    };
    const token = this.ledger.claim(bytes, () => this.deleteEntry(key, false));
    if (!token) return;
    entry.ledgerToken = token;
    this.entries.set(key, entry);
    this.totalBytes += bytes;
    while (
      this.entries.size > this.limits.maximumEntries ||
      this.totalBytes > this.limits.maximumBytes
    ) {
      const oldest = this.entries.keys().next().value;
      if (oldest === undefined) break;
      this.deleteEntry(oldest);
    }
  }

  private deleteEntry(key: string, releaseLedger = true): void {
    const entry = this.entries.get(key);
    if (!entry) return;
    this.entries.delete(key);
    this.totalBytes -= entry.bytes;
    if (releaseLedger) this.ledger.release(entry.ledgerToken);
  }
}

interface ConditionalGetEntry<T> {
  readonly etag: string;
  readonly data: T;
  readonly bytes: number;
  ledgerToken: ArtifactCacheLedgerToken | undefined;
}

class ConditionalGetCacheError extends Error {
  constructor() {
    super('conditional_get_without_cached_representation');
    this.name = 'ConditionalGetCacheError';
  }
}

function conditionalRequestOptions(signal: AbortSignal, etag: string | undefined) {
  return {
    request: { signal },
    ...(etag === undefined ? {} : { headers: { 'if-none-match': etag } }),
  };
}

function validEtag(value: string | undefined): value is string {
  return (
    value !== undefined && value.length > 0 && value.length <= 512 && /^[\x21-\x7e]+$/.test(value)
  );
}

function clone<T>(value: T): T {
  return structuredClone(value);
}

function cacheableDocumentBytes(value: unknown): number | undefined {
  try {
    const serialized = JSON.stringify(value);
    if (serialized === undefined) return undefined;
    const bytes = Buffer.byteLength(serialized, 'utf8');
    return bytes <= ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes ? bytes : undefined;
  } catch {
    return undefined;
  }
}

function positiveInteger(value: number): boolean {
  return Number.isSafeInteger(value) && value > 0;
}

function responseStatus(error: unknown): number | undefined {
  if (!error || typeof error !== 'object') return undefined;
  const candidate = error as {
    readonly status?: unknown;
    readonly response?: { readonly status?: unknown };
  };
  const status = candidate.response?.status ?? candidate.status;
  return typeof status === 'number' ? status : undefined;
}

function header(headers: HeadersLike | undefined, name: string): string | undefined {
  if (!headers) return undefined;
  const getter = (headers as { readonly get?: unknown }).get;
  if (typeof getter === 'function') {
    return (getter as (headerName: string) => string | null | undefined)(name) ?? undefined;
  }
  const record = headers as Readonly<Record<string, string | string[] | undefined>>;
  const value = record[name] ?? record[name.toLowerCase()];
  return Array.isArray(value) ? value[0] : value;
}

class ArtifactArchiveDownloadError extends Error {
  constructor() {
    super('artifact_archive_download_failed');
    this.name = 'ArtifactArchiveDownloadError';
  }
}
