import { describe, expect, it, vi } from 'vitest';
import {
  buildCommentableLineIndex,
  inlineCommentKey,
  parseInlineCommentMarkerKeys,
  postInlineComments,
  renderInlineCommentBody,
  selectInlineCommentCandidates,
} from './inline-comments.js';
import {
  type InlineCommentsPolicy,
  type StructuredFindingV1,
  type StructuredReviewEnvelopeV1,
} from './types.js';

const enabledPolicy: InlineCommentsPolicy = {
  enabled: true,
  maxComments: 5,
  minSeverity: 'medium',
  minConfidence: 'high',
};

function finding(overrides: Partial<StructuredFindingV1> = {}): StructuredFindingV1 {
  return {
    severity: 'medium',
    confidence: 'high',
    category: 'correctness',
    title: 'Inline finding',
    body: 'Validated finding body.',
    path: 'src/file.ts',
    startLine: 10,
    endLine: 10,
    fingerprint: 'fingerprint-1',
    ...overrides,
  };
}

function review(findings: StructuredFindingV1[] = [finding()]): StructuredReviewEnvelopeV1 {
  return {
    schemaVersion: 1,
    phase: 'bootstrap',
    baseSha: 'base-sha',
    headSha: 'head-sha',
    previousReviewedHeadSha: null,
    reviewedRange: { kind: 'bootstrap', fromSha: null, toSha: 'head-sha' },
    toolMode: 'none',
    runtimeProvider: 'test',
    sessionId: 'session-1',
    summary: 'Structured summary.',
    findings,
    limitations: [],
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
      inputFindingCount: findings.length,
      postFindingCapCount: findings.length,
      renderedFindingCount: findings.length,
      findingsTruncated: false,
    },
  };
}

function patchForLine(line = 10): string {
  return [`@@ -${line},2 +${line},3 @@`, ' context', '+added', ' context'].join('\n');
}

function mockOctokit(
  options: {
    files?: unknown[];
    comments?: unknown[][];
    headSha?: string;
    createReviewError?: unknown;
    createReviewCommentError?: unknown;
  } = {},
) {
  const listFiles = vi.fn();
  const listReviewComments = vi.fn();
  const comments = [...(options.comments ?? [[]])];
  const createReview = vi.fn();
  if (options.createReviewError) {
    createReview.mockRejectedValue(options.createReviewError);
  } else {
    createReview.mockResolvedValue({ data: { id: 1 } });
  }
  const createReviewComment = vi.fn();
  if (options.createReviewCommentError) {
    createReviewComment.mockRejectedValue(options.createReviewCommentError);
  } else {
    createReviewComment.mockResolvedValue({ data: { id: 2 } });
  }
  const octokit = {
    paginate: vi.fn(async (endpoint: unknown) => {
      if (endpoint === listFiles) {
        return (
          options.files ?? [
            {
              filename: 'src/file.ts',
              status: 'modified',
              additions: 1,
              deletions: 0,
              changes: 1,
              patch: patchForLine(10),
            },
          ]
        );
      }
      if (endpoint === listReviewComments) {
        return comments.shift() ?? [];
      }
      return [];
    }),
    rest: {
      pulls: {
        get: vi.fn().mockResolvedValue({
          data: { head: { sha: options.headSha ?? 'head-sha' } },
        }),
        listFiles,
        listReviewComments,
        createReview,
        createReviewComment,
      },
    },
  };
  return { octokit, listFiles, listReviewComments, createReview, createReviewComment };
}

async function post(
  octokit: ReturnType<typeof mockOctokit>['octokit'],
  policy: InlineCommentsPolicy = enabledPolicy,
  structuredReview = review(),
) {
  return await postInlineComments({
    octokit,
    owner: 'example',
    repo: 'repo',
    prNumber: 1,
    stateKey: 'state-key',
    reviewedHeadSha: structuredReview.headSha,
    stickyCommentUrl: 'https://github.com/example/repo/pull/1#issuecomment-1',
    structuredReview,
    policy,
  });
}

describe('inline comment candidate selection', () => {
  it('does not post inline comments when disabled', async () => {
    const { octokit, createReview } = mockOctokit();
    const metadata = await post(octokit, { ...enabledPolicy, enabled: false });

    expect(createReview).not.toHaveBeenCalled();
    expect(octokit.paginate).not.toHaveBeenCalled();
    expect(metadata.enabled).toBe(false);
    expect(metadata.candidateCount).toBe(0);
  });

  it('selects eligible findings on current-side PR diff lines', () => {
    const lineIndex = buildCommentableLineIndex([
      {
        filename: 'src/file.ts',
        status: 'modified',
        additions: 1,
        deletions: 0,
        changes: 1,
        patch: patchForLine(10),
      },
    ]);

    const selected = selectInlineCommentCandidates({
      review: review(),
      policy: enabledPolicy,
      stateKey: 'state-key',
      lineIndex,
      stickyCommentUrl: 'https://comment',
    });

    expect(selected.candidates).toHaveLength(1);
    expect(selected.candidates[0]).toMatchObject({ path: 'src/file.ts', line: 10 });
  });

  it('keeps findings sticky-only when location, diff, patch, or thresholds are not eligible', () => {
    const lineIndex = buildCommentableLineIndex([
      {
        filename: 'src/file.ts',
        status: 'modified',
        additions: 1,
        deletions: 0,
        changes: 1,
        patch: patchForLine(10),
      },
      {
        filename: 'src/binary.bin',
        status: 'modified',
        additions: 0,
        deletions: 0,
        changes: 0,
      },
    ]);
    const selected = selectInlineCommentCandidates({
      review: review([
        finding({ path: null, startLine: null, endLine: null }),
        finding({ startLine: 99, endLine: 99 }),
        finding({ path: 'src/binary.bin' }),
        finding({ severity: 'low' }),
        finding({ confidence: 'medium' }),
      ]),
      policy: enabledPolicy,
      stateKey: 'state-key',
      lineIndex,
    });

    expect(selected.candidates).toHaveLength(0);
    expect(selected.skippedReasons).toMatchObject({
      missing_location: 1,
      line_not_commentable: 1,
      binary_or_missing_patch: 1,
      below_threshold: 2,
    });
  });

  it('renders a generic public-safe marker and mentions original multi-line range', () => {
    const key = inlineCommentKey({
      stateKey: 'state-key',
      fingerprint: 'fingerprint-1',
      path: 'src/file.ts',
      startLine: 10,
      endLine: 12,
    });
    const body = renderInlineCommentBody(finding({ endLine: 12 }), key, 'https://comment');

    expect(body).toContain(`agentic-pr-review:inline:v1 key=${key}`);
    expect(body).toContain('Original range: `src/file.ts:10-12`');
    expect(body).not.toMatch(/session|model|run id|authorization|secret/i);
  });
});

describe('inline comment posting', () => {
  it('posts a batch pull request review for valid candidates', async () => {
    const { octokit, createReview } = mockOctokit();
    const metadata = await post(octokit);

    expect(createReview).toHaveBeenCalledWith(
      expect.objectContaining({
        commit_id: 'head-sha',
        event: 'COMMENT',
        comments: [
          expect.objectContaining({
            path: 'src/file.ts',
            line: 10,
            side: 'RIGHT',
          }),
        ],
      }),
    );
    expect(metadata).toMatchObject({
      candidateCount: 1,
      postedCount: 1,
      duplicateCount: 0,
      skippedCount: 0,
      failedCount: 0,
    });
  });

  it('skips posting when the pull request head changed after review generation', async () => {
    const { octokit, createReview } = mockOctokit({ headSha: 'new-head-sha' });
    const metadata = await post(octokit);

    expect(createReview).not.toHaveBeenCalled();
    expect(metadata.postedCount).toBe(0);
    expect(metadata.skippedReasons.head_changed).toBe(1);
  });

  it('caps candidates with max_inline_comments', async () => {
    const findings = Array.from({ length: 3 }, (_, index) =>
      finding({ fingerprint: `fingerprint-${index}`, startLine: 10 + index, endLine: 10 + index }),
    );
    const { octokit, createReview } = mockOctokit({
      files: [
        {
          filename: 'src/file.ts',
          status: 'modified',
          additions: 3,
          deletions: 0,
          changes: 3,
          patch: '@@ -10,3 +10,3 @@\n+line 10\n+line 11\n+line 12',
        },
      ],
    });
    const metadata = await post(octokit, { ...enabledPolicy, maxComments: 2 }, review(findings));

    expect(createReview.mock.calls[0][0].comments).toHaveLength(2);
    expect(metadata.candidateCount).toBe(3);
    expect(metadata.capExceededCount).toBe(1);
    expect(metadata.skippedReasons.cap_exceeded).toBe(1);
  });

  it('applies cap after duplicate suppression so later non-duplicates can still post', async () => {
    const findings = Array.from({ length: 3 }, (_, index) =>
      finding({ fingerprint: `fingerprint-${index}`, startLine: 10 + index, endLine: 10 + index }),
    );
    const duplicateKey = inlineCommentKey({
      stateKey: 'state-key',
      fingerprint: 'fingerprint-0',
      path: 'src/file.ts',
      startLine: 10,
      endLine: 10,
    });
    const { octokit, createReview } = mockOctokit({
      files: [
        {
          filename: 'src/file.ts',
          status: 'modified',
          additions: 3,
          deletions: 0,
          changes: 3,
          patch: '@@ -10,3 +10,3 @@\n+line 10\n+line 11\n+line 12',
        },
      ],
      comments: [[{ body: `Existing <!-- agentic-pr-review:inline:v1 key=${duplicateKey} -->` }]],
    });
    const metadata = await post(octokit, { ...enabledPolicy, maxComments: 2 }, review(findings));

    expect(createReview.mock.calls[0][0].comments).toHaveLength(2);
    expect(
      createReview.mock.calls[0][0].comments.map((comment: { line: number }) => comment.line),
    ).toEqual([11, 12]);
    expect(metadata.candidateCount).toBe(3);
    expect(metadata.duplicateCount).toBe(1);
    expect(metadata.capExceededCount).toBe(0);
  });

  it('suppresses duplicates from existing generic markers without runtime identity fields', async () => {
    const key = inlineCommentKey({
      stateKey: 'state-key',
      fingerprint: 'fingerprint-1',
      path: 'src/file.ts',
      startLine: 10,
      endLine: 10,
    });
    const { octokit, createReview } = mockOctokit({
      comments: [[{ body: `Prior comment <!-- agentic-pr-review:inline:v1 key=${key} -->` }]],
    });
    const metadata = await post(octokit);

    expect(createReview).not.toHaveBeenCalled();
    expect(metadata.duplicateCount).toBe(1);
    expect(
      parseInlineCommentMarkerKeys(`<!-- agentic-pr-review:inline:v1 key=${key} -->`).has(key),
    ).toBe(true);
  });

  it('re-lists comments before individual fallback after batch validation failure', async () => {
    const { octokit, listReviewComments, createReviewComment } = mockOctokit({
      comments: [[], []],
      createReviewError: Object.assign(new Error('Validation Failed'), { status: 422 }),
    });
    const metadata = await post(octokit);

    expect(
      octokit.paginate.mock.calls.filter((call) => call[0] === listReviewComments),
    ).toHaveLength(2);
    expect(createReviewComment).toHaveBeenCalledWith(
      expect.objectContaining({
        commit_id: 'head-sha',
        path: 'src/file.ts',
        line: 10,
        side: 'RIGHT',
      }),
    );
    expect(metadata.postedCount).toBe(1);
    expect(metadata.failedReasons.batch_validation_failed).toBe(1);
  });

  it.each([
    ['authentication_failed', Object.assign(new Error('Bad credentials'), { status: 401 })],
    ['permission_denied', Object.assign(new Error('Resource not accessible'), { status: 403 })],
    ['secondary_rate_limit', Object.assign(new Error('secondary rate limit'), { status: 403 })],
    ['secondary_rate_limit', Object.assign(new Error('Too Many Requests'), { status: 429 })],
    [
      'secondary_rate_limit',
      Object.assign(new Error('Validation failed, or the endpoint has been spammed'), {
        status: 422,
      }),
    ],
    ['repository_policy', Object.assign(new Error('not found'), { status: 404 })],
  ])('does not fan out fallback for %s batch failures', async (reason, error) => {
    const { octokit, createReviewComment } = mockOctokit({ createReviewError: error });
    const metadata = await post(octokit);

    expect(createReviewComment).not.toHaveBeenCalled();
    expect(metadata.failedCount).toBe(1);
    expect(metadata.failedReasons[reason]).toBe(1);
  });

  it('records individual posting failures without hiding sticky findings', async () => {
    const { octokit } = mockOctokit({
      comments: [[], []],
      createReviewError: Object.assign(new Error('socket reset'), { status: 500 }),
      createReviewCommentError: Object.assign(new Error('Validation Failed'), { status: 422 }),
    });
    const metadata = await post(octokit);

    expect(metadata.postedCount).toBe(0);
    expect(metadata.failedCount).toBe(1);
    expect(metadata.failedReasons.validation_failed).toBe(1);
  });

  it('skips inline posting for supported pagination limits', async () => {
    const { octokit: fileOctokit } = mockOctokit({
      files: Array.from({ length: 3000 }, (_, index) => ({
        filename: `src/file-${index}.ts`,
        status: 'modified',
        additions: 1,
        deletions: 0,
        changes: 1,
        patch: patchForLine(10),
      })),
    });
    const fileMetadata = await post(fileOctokit);
    expect(fileMetadata.skippedReasons.diff_too_large).toBe(1);

    const { octokit: commentOctokit } = mockOctokit({
      comments: [Array.from({ length: 3000 }, () => ({ body: 'existing' }))],
    });
    const commentMetadata = await post(commentOctokit);
    expect(commentMetadata.skippedReasons.diff_too_large).toBe(1);
  });
});
