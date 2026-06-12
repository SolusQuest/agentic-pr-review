import { describe, expect, it, vi } from 'vitest';
import {
  buildLineageCommentBody,
  buildStickyComment,
  STICKY_COMMENT_MARKER,
  upsertLineageComment,
} from './comments.js';

describe('sticky comment', () => {
  it('uses generic markers', () => {
    const comment = buildStickyComment(
      {
        mode: 'synthetic-fixture',
        title: 'Synthetic',
        body: '',
        baseRef: 'main',
        baseSha: 'base',
        headRef: 'branch',
        headSha: 'head',
        draft: false,
        changedFiles: [],
      },
      'No findings.',
      'state-key',
    );
    expect(comment).toContain(STICKY_COMMENT_MARKER);
    expect(comment).toContain('agentic-pr-review:meta');
  });

  it('updates an incremental lineage comment and keeps markers generic', async () => {
    const target = {
      mode: 'pull-request' as const,
      prNumber: 1,
      title: 'Synthetic',
      body: '',
      baseRef: 'main',
      baseSha: 'base',
      headRef: 'branch',
      headSha: 'head-2',
      draft: false,
      htmlUrl: 'https://github.com/example/repo/pull/1',
      changedFiles: [],
    };
    const existingBody = buildLineageCommentBody(
      {
        target: { ...target, headSha: 'head-1' },
        reviewMarkdown: 'First review.',
        stateKey: 'state-key',
        phase: 'bootstrap',
        runtimeProvider: 'test',
        sessionId: 'session-1',
        currentHeadSha: 'head-1',
        artifactName: 'artifact',
        runId: 1,
        runAttempt: 1,
        lineageReason: 'manual_bootstrap',
        maxReviewChars: 12000,
      },
      'create',
    );
    const updateComment = vi.fn().mockResolvedValue({ data: { html_url: 'https://comment' } });
    const octokit = {
      paginate: vi.fn().mockResolvedValue([
        {
          id: 10,
          body: existingBody,
          updated_at: '2026-01-01T00:00:00Z',
        },
      ]),
      rest: {
        issues: {
          listComments: vi.fn(),
          updateComment,
          createComment: vi.fn(),
        },
      },
    };

    const result = await upsertLineageComment({
      octokit,
      owner: 'example',
      repo: 'repo',
      prNumber: 1,
      target,
      reviewMarkdown: 'Second review.',
      stateKey: 'state-key',
      phase: 'incremental',
      runtimeProvider: 'test',
      sessionId: 'session-1',
      previousHeadSha: 'head-1',
      currentHeadSha: 'head-2',
      artifactName: 'artifact',
      runId: 2,
      runAttempt: 1,
      lineageReason: 'continuity_mismatch',
      maxReviewChars: 12000,
    });

    expect(result.lineageAction).toBe('update');
    const updatedBody = updateComment.mock.calls[0][0].body as string;
    expect(updatedBody).toContain('agentic-pr-review:history-entry:start');
    expect(updatedBody).not.toMatch(/solusquest/i);
  });
});
