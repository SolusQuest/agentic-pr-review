import type { getOctokit } from '@actions/github';

import type { ArtifactActionsRestClient } from './official-artifact-operations.js';

type ActionsOctokit = ReturnType<typeof getOctokit>;

export function createArtifactActionsRestClient(
  octokit: ActionsOctokit,
): ArtifactActionsRestClient {
  return {
    listArtifactsForRepo: async (input, signal) =>
      await octokit.rest.actions.listArtifactsForRepo({
        ...input,
        request: { signal },
      }),
    getArtifact: async (input, signal) =>
      await octokit.rest.actions.getArtifact({
        ...input,
        request: { signal },
      }),
    downloadArtifactArchive: async (input, signal) => {
      const { maximum_bytes: maximumBytes, ...request } = input;
      const redirect = await octokit.rest.actions.downloadArtifact({
        ...request,
        archive_format: 'zip',
        request: { redirect: 'manual', signal },
      });
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
    getWorkflowRunAttempt: async (input, signal) =>
      await octokit.rest.actions.getWorkflowRunAttempt({
        ...input,
        request: { signal },
      }),
    deleteArtifact: async (input, signal) =>
      await octokit.rest.actions.deleteArtifact({
        ...input,
        request: { signal },
      }),
  };
}

class ArtifactArchiveDownloadError extends Error {
  constructor() {
    super('artifact_archive_download_failed');
    this.name = 'ArtifactArchiveDownloadError';
  }
}
