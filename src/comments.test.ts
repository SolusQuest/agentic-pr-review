import { describe, expect, it, vi } from 'vitest';
import {
  buildLineageCommentBody,
  buildStickyComment,
  capStructuredReviewForMarkdownLimit,
  renderStructuredReviewMarkdown,
  STICKY_COMMENT_MARKER,
  upsertLineageComment,
} from './comments.js';
import { type StructuredReviewEnvelopeV1 } from './types.js';

function structuredReview(headSha = 'head'): StructuredReviewEnvelopeV1 {
  return {
    schemaVersion: 1,
    phase: 'bootstrap',
    baseSha: 'base',
    headSha,
    previousReviewedHeadSha: null,
    reviewedRange: { kind: 'bootstrap', fromSha: null, toSha: headSha },
    toolMode: 'none',
    runtimeProvider: 'test',
    sessionId: 'session-1',
    summary: 'Structured summary.',
    findings: [
      {
        severity: 'low',
        confidence: 'medium',
        category: 'documentation',
        title: 'Top-level finding',
        body: 'This finding has no location.',
        path: null,
        startLine: null,
        endLine: null,
        fingerprint: 'fingerprint-1',
      },
    ],
    limitations: ['Synthetic limitation.'],
    usage: null,
    observedTurns: 0,
    observedTurnSource: 'not_applicable',
    lineageTotals: {
      observedTurns: 0,
      usage: {
        inputTokens: 0,
        cacheReadInputTokens: 0,
        cacheCreationInputTokens: 0,
        outputTokens: 0,
      },
      source: 'current_run_only',
      partial: false,
    },
    result: {
      inputFindingCount: 1,
      postFindingCapCount: 1,
      renderedFindingCount: 1,
      findingsTruncated: false,
    },
  };
}

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
      structuredReview(),
      'state-key',
    );
    expect(comment).toContain(STICKY_COMMENT_MARKER);
    expect(comment).toContain('agentic-pr-review:meta');
    expect(comment).toContain('Structured summary.');
  });

  it('renders findings without path or line in the top-level comment body', () => {
    const rendered = renderStructuredReviewMarkdown(structuredReview());
    expect(rendered).toContain('No file location');
    expect(rendered).toContain('Top-level finding');
  });

  it('caps the structured review before comment rendering instead of hiding artifact findings', () => {
    const findings = Array.from({ length: 8 }, (_, index) => ({
      severity: 'medium' as const,
      confidence: 'high' as const,
      category: 'maintainability' as const,
      title: `Long finding ${index + 1}`,
      body: `Long body ${index + 1}. ${'detail '.repeat(120)}`,
      path: 'src/file.ts',
      startLine: index + 1,
      endLine: index + 1,
      fingerprint: `fingerprint-${index + 1}`,
    }));
    const review: StructuredReviewEnvelopeV1 = {
      ...structuredReview(),
      findings,
      result: {
        inputFindingCount: findings.length,
        postFindingCapCount: findings.length,
        renderedFindingCount: findings.length,
        findingsTruncated: false,
      },
    };

    const capped = capStructuredReviewForMarkdownLimit(review, 2400);
    const rendered = renderStructuredReviewMarkdown(capped);
    const comment = buildLineageCommentBody(
      {
        target: {
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
        structuredReview: capped,
        stateKey: 'state-key',
        phase: 'bootstrap',
        runtimeProvider: 'test',
        sessionId: 'session-1',
        currentHeadSha: 'head',
        artifactName: 'artifact',
        runId: 1,
        runAttempt: 1,
        lineageReason: 'manual_bootstrap',
        maxReviewChars: 2400,
      },
      'create',
    );

    expect(rendered.length).toBeLessThanOrEqual(2400);
    expect(capped.findings.length).toBeLessThan(findings.length);
    expect(capped.findings).toHaveLength(capped.result.renderedFindingCount);
    expect(capped.result).toMatchObject({
      inputFindingCount: findings.length,
      postFindingCapCount: findings.length,
      findingsTruncated: true,
      truncationReason: 'max_review_chars',
    });
    expect(comment).toContain('Finding list truncated');
    expect(comment).not.toContain('[truncated to');
    expect(comment).not.toContain(findings[findings.length - 1].title);
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
        structuredReview: structuredReview('head-1'),
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
      structuredReview: structuredReview('head-2'),
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
