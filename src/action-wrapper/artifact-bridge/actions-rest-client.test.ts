import { afterEach, describe, expect, it, vi } from 'vitest';

import { createArtifactActionsRestClient } from './actions-rest-client.js';

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('bounded artifact archive acquisition', () => {
  it('returns only an in-bound fixed-route archive', async () => {
    const bytes = Buffer.from('zip');
    const fetchMock = vi.fn(
      async () =>
        new Response(bytes, {
          status: 200,
          headers: { 'content-length': String(bytes.length) },
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    const client = createArtifactActionsRestClient(octokitWithRedirect());

    const result = await client.downloadArtifactArchive(
      {
        owner: 'owner',
        repo: 'repo',
        artifact_id: 1,
        maximum_bytes: bytes.length,
      },
      new AbortController().signal,
    );

    expect(result).toEqual({ status: 200, data: bytes });
    expect(fetchMock).toHaveBeenCalledWith(
      'https://blob.invalid/archive',
      expect.objectContaining({ method: 'GET', redirect: 'error' }),
    );
  });

  it('cancels at cap plus one before any archive can be staged', async () => {
    const cancelled = vi.fn();
    const stream = new ReadableStream<Uint8Array>({
      start(controller) {
        controller.enqueue(Buffer.alloc(4, 1));
        controller.enqueue(Buffer.of(2));
      },
      cancel: cancelled,
    });
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(stream, { status: 200 })),
    );
    const client = createArtifactActionsRestClient(octokitWithRedirect());

    await expect(
      client.downloadArtifactArchive(
        {
          owner: 'owner',
          repo: 'repo',
          artifact_id: 1,
          maximum_bytes: 4,
        },
        new AbortController().signal,
      ),
    ).rejects.toThrow('artifact_archive_download_failed');
    expect(cancelled).toHaveBeenCalledOnce();
  });
});

function octokitWithRedirect() {
  return {
    rest: {
      actions: {
        downloadArtifact: vi.fn(async () => ({
          status: 302,
          headers: { location: 'https://blob.invalid/archive' },
        })),
      },
    },
  } as never;
}
