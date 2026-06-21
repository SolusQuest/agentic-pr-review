import { describe, expect, it } from 'vitest';
import { resolveTarget } from './target.js';
import { type ActionConfig } from './types.js';

function config(): ActionConfig {
  return {
    runtimeProvider: 'test',
    targetMode: 'pull-request',
    reviewMode: 'auto',
    prNumber: 1,
    artifactRetentionDays: 7,
    postComment: false,
    apiKeyMode: 'auth-token',
    toolMode: 'none',
    claudeMaxTurns: 6,
    maxContextChars: 1000,
    maxPatchChars: 1000,
    maxReviewChars: 1000,
    maxFindings: 50,
    testRuntimeFixture: 'valid',
    usageBudgetLimits: {
      maxUncachedInputTokens: 0,
      maxCachedInputTokens: 0,
      maxOutputTokens: 0,
    },
    disablePromptCaching: false,
    debugCaptureRawApiBodies: false,
    githubToken: 'token',
  };
}

describe('resolveTarget', () => {
  it('fails closed when pull request head repository metadata is missing', async () => {
    const octokit = {
      rest: {
        pulls: {
          get: async () => ({
            data: {
              title: 'PR',
              body: '',
              base: { ref: 'main', sha: 'base' },
              head: { ref: 'branch', sha: 'head', repo: null },
              draft: false,
              html_url: 'https://github.com/example/repo/pull/1',
            },
          }),
          listFiles: {},
        },
      },
      paginate: async () => [],
    };

    await expect(
      resolveTarget(config(), octokit, {
        repo: { owner: 'example', repo: 'repo' },
        payload: {},
        sha: 'head',
      }),
    ).rejects.toThrow(/head repository metadata/);
  });
});
