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
