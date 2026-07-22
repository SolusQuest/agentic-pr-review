import { describe, expect, it } from 'vitest';
import {
  buildPullRequestDiffSnapshot,
  diffPullRequestDiffSnapshots,
  resolveTarget,
} from './target.js';
import { type ActionConfig } from './types.js';
import { sha256 } from './utils.js';

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
    inlineComments: false,
    maxInlineComments: 5,
    inlineMinSeverity: 'medium',
    inlineMinConfidence: 'high',
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

  it('builds PR diff snapshots with raw patch hashes before prompt truncation', async () => {
    const rawPatch = '@@ -1 +1 @@\n-old\n+' + 'new '.repeat(200);
    const octokit = {
      rest: {
        pulls: {
          get: async () => ({
            data: {
              title: 'PR',
              body: '',
              base: { ref: 'main', sha: 'base' },
              head: { ref: 'branch', sha: 'head', repo: { full_name: 'example/repo' } },
              draft: false,
              state: 'open',
              html_url: 'https://github.com/example/repo/pull/1',
            },
          }),
          listFiles: {},
        },
      },
      paginate: async () => [
        {
          sha: 'file-sha-current',
          filename: 'docs/current-change.md',
          status: 'modified',
          additions: 1,
          deletions: 1,
          changes: 2,
          patch: rawPatch,
        },
        {
          sha: 'file-sha-binary',
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    };

    const target = await resolveTarget({ ...config(), maxPatchChars: 10 }, octokit, {
      repo: { owner: 'example', repo: 'repo' },
      payload: {},
      sha: 'head',
    });

    expect(target.pullRequestDiffSnapshot?.files[0]).toMatchObject({
      filename: 'docs/current-change.md',
      fileSha: 'file-sha-current',
      patchAvailable: true,
      patchSha256: sha256(rawPatch),
    });
    expect(target.pullRequestDiffSnapshot?.files[1]).toMatchObject({
      filename: 'assets/generated.bin',
      fileSha: 'file-sha-binary',
      patchAvailable: false,
      patchSha256: null,
    });
    expect(target.changedFiles[0].patch).toBe(rawPatch);
    expect(target.isOpen).toBe(true);
  });

  it('retains the pull request open state for the ledger invocation gate', async () => {
    const octokit = {
      rest: {
        pulls: {
          get: async () => ({
            data: {
              title: 'Closed PR',
              body: '',
              base: { ref: 'main', sha: 'base' },
              head: { ref: 'branch', sha: 'head', repo: { full_name: 'example/repo' } },
              draft: false,
              state: 'closed',
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
    ).resolves.toMatchObject({ isOpen: false });
  });

  it('computes changed current entries and stale removed entries from snapshots', () => {
    const previous = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'old-head',
      files: [
        {
          filename: 'docs/current-change.md',
          status: 'modified',
          additions: 1,
          deletions: 0,
          changes: 1,
          patch: 'old patch',
        },
        {
          filename: 'src/old-pr-file.ts',
          status: 'modified',
          additions: 1,
          deletions: 0,
          changes: 1,
          patch: 'stale patch',
        },
      ],
    });
    const currentPatch = 'new patch';
    const current = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'new-head',
      files: [
        {
          filename: 'docs/current-change.md',
          status: 'modified',
          additions: 2,
          deletions: 0,
          changes: 2,
          patch: currentPatch,
        },
      ],
    });

    const delta = diffPullRequestDiffSnapshots(previous, current, [
      {
        filename: 'docs/current-change.md',
        status: 'modified',
        additions: 2,
        deletions: 0,
        changes: 2,
        patch: currentPatch,
      },
    ]);

    expect(delta.changedEntries).toHaveLength(1);
    expect(delta.changedEntries[0]).toMatchObject({
      kind: 'current_changed',
      reason: 'metadata_changed',
      patch: currentPatch,
      current: { filename: 'docs/current-change.md' },
    });
    expect(delta.removedEntries).toEqual([
      {
        kind: 'removed_from_pr_diff',
        previous: previous.files[1],
      },
    ]);
  });

  it('uses file sha to detect patch-unavailable PR diff changes', () => {
    const previous = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'old-head',
      files: [
        {
          sha: 'old-binary-sha',
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });
    const current = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'new-head',
      files: [
        {
          sha: 'new-binary-sha',
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });

    const delta = diffPullRequestDiffSnapshots(previous, current, [
      {
        filename: 'assets/generated.bin',
        status: 'modified',
        additions: 0,
        deletions: 0,
        changes: 0,
      },
    ]);

    expect(delta.changedEntries).toHaveLength(1);
    expect(delta.changedEntries[0]).toMatchObject({
      kind: 'current_changed',
      reason: 'metadata_changed',
      current: {
        filename: 'assets/generated.bin',
        fileSha: 'new-binary-sha',
        patchAvailable: false,
        patchSha256: null,
      },
      previous: {
        fileSha: 'old-binary-sha',
      },
    });
  });

  it('treats patch-unavailable entries with the same file sha as unchanged', () => {
    const previous = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'old-head',
      files: [
        {
          sha: 'same-binary-sha',
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });
    const current = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'new-head',
      files: [
        {
          sha: 'same-binary-sha',
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });

    const delta = diffPullRequestDiffSnapshots(previous, current, [
      {
        filename: 'assets/generated.bin',
        status: 'modified',
        additions: 0,
        deletions: 0,
        changes: 0,
      },
    ]);

    expect(delta.changedEntries).toEqual([]);
    expect(delta.unchangedCount).toBe(1);
  });

  it('conservatively treats patch-unavailable entries without file sha as changed', () => {
    const previous = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'old-head',
      files: [
        {
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });
    const current = buildPullRequestDiffSnapshot({
      baseSha: 'base',
      headSha: 'new-head',
      files: [
        {
          filename: 'assets/generated.bin',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });

    const delta = diffPullRequestDiffSnapshots(previous, current, [
      {
        filename: 'assets/generated.bin',
        status: 'modified',
        additions: 0,
        deletions: 0,
        changes: 0,
      },
    ]);

    expect(delta.changedEntries).toHaveLength(1);
  });
});
