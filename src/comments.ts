import { type ReviewTarget } from './types.js';
import { truncateText } from './utils.js';

export const STICKY_COMMENT_MARKER = '<!-- agentic-pr-review:v1 -->';

export function buildStickyComment(
  target: ReviewTarget,
  reviewMarkdown: string,
  stateKey: string,
): string {
  const meta = `<!-- agentic-pr-review:meta ${JSON.stringify({
    state_key: stateKey,
    head_sha: target.headSha,
  })} -->`;
  return [
    STICKY_COMMENT_MARKER,
    meta,
    '## Agentic PR Review',
    '',
    truncateText(reviewMarkdown, 12000),
  ].join('\n');
}

export async function upsertStickyComment(options: {
  octokit: any;
  owner: string;
  repo: string;
  prNumber: number;
  target: ReviewTarget;
  reviewMarkdown: string;
  stateKey: string;
}): Promise<{ commentUrl: string; lineageAction: string; lineageReason: string }> {
  const comments = (await options.octokit.paginate(options.octokit.rest.issues.listComments, {
    owner: options.owner,
    repo: options.repo,
    issue_number: options.prNumber,
    per_page: 100,
  })) as Array<{ id: number; body?: string; html_url?: string }>;

  const body = buildStickyComment(options.target, options.reviewMarkdown, options.stateKey);
  const existing = comments.find((comment) => comment.body?.includes(STICKY_COMMENT_MARKER));
  if (existing) {
    const updated = await options.octokit.rest.issues.updateComment({
      owner: options.owner,
      repo: options.repo,
      comment_id: existing.id,
      body,
    });
    return {
      commentUrl: String(updated.data.html_url),
      lineageAction: 'updated',
      lineageReason: 'existing sticky comment marker found',
    };
  }

  const created = await options.octokit.rest.issues.createComment({
    owner: options.owner,
    repo: options.repo,
    issue_number: options.prNumber,
    body,
  });
  return {
    commentUrl: String(created.data.html_url),
    lineageAction: 'created',
    lineageReason: 'no existing sticky comment marker found',
  };
}
