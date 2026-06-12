import {
  type LineageAction,
  type LineageReason,
  type Phase,
  type ReviewTarget,
  type RuntimeUsage,
} from './types.js';
import { truncateText } from './utils.js';

export const STICKY_COMMENT_MARKER = '<!-- agentic-pr-review:v1 -->';
const LINEAGE_META_PREFIX = '<!-- agentic-pr-review:meta';
const LINEAGE_META_SUFFIX = '-->';
const CURRENT_BLOCK_START = '<!-- agentic-pr-review:current:start -->';
const CURRENT_BLOCK_END = '<!-- agentic-pr-review:current:end -->';
const HISTORY_BLOCK_START = '<!-- agentic-pr-review:history:start -->';
const HISTORY_BLOCK_END = '<!-- agentic-pr-review:history:end -->';
const HISTORY_ENTRY_START = '<!-- agentic-pr-review:history-entry:start -->';
const HISTORY_ENTRY_END = '<!-- agentic-pr-review:history-entry:end -->';
const COMMENT_MAX_CHARS = 50000;
const HISTORY_RETAIN_COUNT = 3;

interface IssueComment {
  id: number;
  body?: string;
  html_url?: string;
  updated_at?: string;
}

interface LineageMeta {
  version: 1;
  lineage_id: string;
  state_key: string;
  runtime_provider: string;
  session_id: string;
  from_head_sha: string | null;
  to_head_sha: string;
  phase: Phase;
  run_id: string;
  run_attempt: string;
  lineage_reason: LineageReason;
  created_at: string;
  updated_at: string;
}

export interface LineageCommentInput {
  octokit: any;
  owner: string;
  repo: string;
  prNumber: number;
  target: ReviewTarget;
  reviewMarkdown: string;
  stateKey: string;
  phase: Phase;
  runtimeProvider: string;
  sessionId: string;
  previousHeadSha?: string;
  currentHeadSha: string;
  artifactName: string;
  runId: number;
  runAttempt: number;
  lineageReason: LineageReason;
  usage?: RuntimeUsage;
  maxReviewChars: number;
}

export function buildStickyComment(
  target: ReviewTarget,
  reviewMarkdown: string,
  stateKey: string,
): string {
  return buildLineageCommentBody(
    {
      target,
      reviewMarkdown,
      stateKey,
      phase: 'bootstrap',
      runtimeProvider: 'test',
      sessionId: 'session',
      currentHeadSha: target.headSha,
      artifactName: 'artifact',
      runId: 1,
      runAttempt: 1,
      lineageReason: 'manual_bootstrap',
      usage: undefined,
      maxReviewChars: 12000,
    },
    'create',
  );
}

export async function upsertLineageComment(
  options: LineageCommentInput,
): Promise<{ commentUrl: string; lineageAction: LineageAction; lineageReason: LineageReason }> {
  if (options.phase === 'bootstrap') {
    const body = buildLineageCommentBody(options, 'create');
    const created = await options.octokit.rest.issues.createComment({
      owner: options.owner,
      repo: options.repo,
      issue_number: options.prNumber,
      body,
    });
    return {
      commentUrl: String(created.data.html_url),
      lineageAction: 'create',
      lineageReason: options.lineageReason,
    };
  }

  const comments = (await options.octokit.paginate(options.octokit.rest.issues.listComments, {
    owner: options.owner,
    repo: options.repo,
    issue_number: options.prNumber,
    per_page: 100,
  })) as IssueComment[];
  const match = findLineageComment(comments, options);
  if (match.action === 'create') {
    const body = buildLineageCommentBody(
      { ...options, lineageReason: match.lineageReason },
      'create',
    );
    const created = await options.octokit.rest.issues.createComment({
      owner: options.owner,
      repo: options.repo,
      issue_number: options.prNumber,
      body,
    });
    return {
      commentUrl: String(created.data.html_url),
      lineageAction: 'create',
      lineageReason: match.lineageReason,
    };
  }

  const body = buildLineageCommentBody(
    { ...options, lineageReason: match.meta.lineage_reason },
    match.action,
    match.meta,
    match.comment.body,
  );
  const updated = await options.octokit.rest.issues.updateComment({
    owner: options.owner,
    repo: options.repo,
    comment_id: match.comment.id,
    body,
  });
  return {
    commentUrl: String(updated.data.html_url),
    lineageAction: match.action,
    lineageReason: match.meta.lineage_reason,
  };
}

export function buildLineageCommentBody(
  input: Omit<LineageCommentInput, 'octokit' | 'owner' | 'repo' | 'prNumber'>,
  action: LineageAction,
  existingMeta?: LineageMeta,
  existingBody?: string,
): string {
  const now = new Date().toISOString();
  const chainStart = existingMeta?.lineage_id.split(':').at(-1) ?? shortSha(input.currentHeadSha);
  const lineageId =
    existingMeta?.lineage_id ??
    `${input.stateKey}:${input.runtimeProvider}:${input.runId}:${chainStart}`;
  const meta: LineageMeta = {
    version: 1,
    lineage_id: lineageId,
    state_key: input.stateKey,
    runtime_provider: input.runtimeProvider,
    session_id: input.sessionId,
    from_head_sha: input.previousHeadSha ?? null,
    to_head_sha: input.currentHeadSha,
    phase: input.phase,
    run_id: String(input.runId),
    run_attempt: String(input.runAttempt),
    lineage_reason:
      action === 'create'
        ? input.lineageReason
        : (existingMeta?.lineage_reason ?? input.lineageReason),
    created_at: existingMeta?.created_at ?? now,
    updated_at: now,
  };

  const repository = input.target.htmlUrl ? repositoryFromUrl(input.target.htmlUrl) : undefined;
  const runValue = repository
    ? `[${input.runId}.${input.runAttempt}](https://github.com/${repository}/actions/runs/${input.runId})`
    : `${input.runId}.${input.runAttempt}`;
  const previous = input.previousHeadSha ? shortSha(input.previousHeadSha) : 'n/a';
  const header = [
    STICKY_COMMENT_MARKER,
    `${LINEAGE_META_PREFIX}\n${JSON.stringify(meta)}\n${LINEAGE_META_SUFFIX}`,
    '',
    '## Agentic PR Review',
    '',
    '| Field | Value |',
    '| --- | --- |',
    `| Mode | ${input.phase} |`,
    `| Range | \`${previous}\` -> \`${shortSha(input.currentHeadSha)}\` |`,
    `| Runtime | ${input.runtimeProvider} |`,
    `| Session | \`${input.sessionId}\` |`,
    `| State artifact | \`${input.artifactName}\` |`,
    `| Run | ${runValue} |`,
    `| Usage | ${formatUsage(input.usage)} |`,
    '',
  ].join('\n');

  const currentBlock = [
    CURRENT_BLOCK_START,
    '### Current Review',
    '',
    truncateText(input.reviewMarkdown, input.maxReviewChars),
    CURRENT_BLOCK_END,
  ].join('\n');
  const historyBlock = buildHistoryBlock(action, existingMeta, existingBody);
  return enforceMaxChars(header, currentBlock, historyBlock);
}

function findLineageComment(
  comments: IssueComment[],
  input: LineageCommentInput,
):
  | { action: 'create'; lineageReason: LineageReason }
  | { action: 'update' | 'update_in_place'; comment: IssueComment; meta: LineageMeta } {
  const candidates = comments
    .map((comment) => ({ comment, meta: parseLineageMeta(comment.body ?? '') }))
    .filter((candidate): candidate is { comment: IssueComment; meta: LineageMeta } =>
      Boolean(
        candidate.meta &&
        candidate.meta.state_key === input.stateKey &&
        candidate.meta.runtime_provider === input.runtimeProvider,
      ),
    )
    .sort((left, right) => {
      const leftUpdated = left.comment.updated_at ? Date.parse(left.comment.updated_at) : 0;
      const rightUpdated = right.comment.updated_at ? Date.parse(right.comment.updated_at) : 0;
      return rightUpdated - leftUpdated || right.comment.id - left.comment.id;
    });

  const sameRange = candidates.find(
    (candidate) =>
      candidate.meta.from_head_sha === (input.previousHeadSha ?? null) &&
      candidate.meta.to_head_sha === input.currentHeadSha &&
      candidate.meta.session_id === input.sessionId,
  );
  if (sameRange) {
    return { action: 'update_in_place', comment: sameRange.comment, meta: sameRange.meta };
  }

  const continuation = candidates.find(
    (candidate) => input.previousHeadSha && candidate.meta.to_head_sha === input.previousHeadSha,
  );
  if (continuation) {
    return { action: 'update', comment: continuation.comment, meta: continuation.meta };
  }

  return { action: 'create', lineageReason: 'continuity_mismatch' };
}

function parseLineageMeta(body: string): LineageMeta | undefined {
  if (!body.includes(STICKY_COMMENT_MARKER)) {
    return undefined;
  }
  const start = body.indexOf(LINEAGE_META_PREFIX);
  if (start === -1) {
    return undefined;
  }
  const jsonStart = start + LINEAGE_META_PREFIX.length;
  const end = body.indexOf(LINEAGE_META_SUFFIX, jsonStart);
  if (end === -1) {
    return undefined;
  }
  try {
    const parsed = JSON.parse(body.slice(jsonStart, end).trim()) as LineageMeta;
    return parsed.version === 1 ? parsed : undefined;
  } catch {
    return undefined;
  }
}

function buildHistoryBlock(
  action: LineageAction,
  existingMeta: LineageMeta | undefined,
  existingBody: string | undefined,
): string {
  if (!existingBody || action === 'create') {
    return '';
  }
  const existingHistory = extractBlock(existingBody, HISTORY_BLOCK_START, HISTORY_BLOCK_END);
  if (action === 'update_in_place') {
    return existingHistory
      ? ['', '---', '', HISTORY_BLOCK_START, existingHistory, HISTORY_BLOCK_END].join('\n')
      : '';
  }

  const oldCurrent = extractBlock(existingBody, CURRENT_BLOCK_START, CURRENT_BLOCK_END);
  if (!oldCurrent) {
    return existingHistory
      ? ['', '---', '', HISTORY_BLOCK_START, existingHistory, HISTORY_BLOCK_END].join('\n')
      : '';
  }

  const entry = [
    HISTORY_ENTRY_START,
    buildHistoryEntrySummary(existingMeta),
    '',
    truncateText(oldCurrent, 4000),
    '</details>',
    HISTORY_ENTRY_END,
  ].join('\n');
  const previousEntries = existingHistory
    ? extractAllBlocks(existingHistory, HISTORY_ENTRY_START, HISTORY_ENTRY_END)
    : [];
  return [
    '',
    '---',
    '',
    HISTORY_BLOCK_START,
    ...[entry, ...previousEntries].slice(0, HISTORY_RETAIN_COUNT),
    HISTORY_BLOCK_END,
  ].join('\n');
}

function buildHistoryEntrySummary(meta: LineageMeta | undefined): string {
  if (!meta) {
    return '<details><summary>Previous review</summary>';
  }
  const from = meta.from_head_sha ? shortSha(meta.from_head_sha) : 'base';
  const to = shortSha(meta.to_head_sha);
  return `<details><summary>${meta.phase} ${from} -> ${to} ${meta.runtime_provider}</summary>`;
}

function enforceMaxChars(header: string, currentBlock: string, historyBlock: string): string {
  let body = header + currentBlock + historyBlock;
  if (body.length <= COMMENT_MAX_CHARS || !historyBlock) {
    return body;
  }
  const entries = extractAllBlocks(historyBlock, HISTORY_ENTRY_START, HISTORY_ENTRY_END);
  for (let count = entries.length; count >= 0; count -= 1) {
    const trimmedHistory =
      count > 0
        ? ['', '---', '', HISTORY_BLOCK_START, ...entries.slice(0, count), HISTORY_BLOCK_END].join(
            '\n',
          )
        : '';
    body = header + currentBlock + trimmedHistory;
    if (body.length <= COMMENT_MAX_CHARS) {
      return body;
    }
  }
  return truncateText(header + currentBlock, COMMENT_MAX_CHARS);
}

function extractBlock(body: string, startMarker: string, endMarker: string): string | undefined {
  const start = body.indexOf(startMarker);
  if (start === -1) {
    return undefined;
  }
  const contentStart = start + startMarker.length;
  const end = body.indexOf(endMarker, contentStart);
  if (end === -1) {
    return undefined;
  }
  return body.slice(contentStart, end).trim();
}

function extractAllBlocks(body: string, startMarker: string, endMarker: string): string[] {
  const result: string[] = [];
  let index = 0;
  while (index < body.length) {
    const start = body.indexOf(startMarker, index);
    if (start === -1) {
      break;
    }
    const end = body.indexOf(endMarker, start + startMarker.length);
    if (end === -1) {
      break;
    }
    result.push(body.slice(start, end + endMarker.length));
    index = end + endMarker.length;
  }
  return result;
}

function formatUsage(usage: RuntimeUsage | undefined): string {
  if (!usage) {
    return 'not exposed';
  }
  return [
    `cache_read=${usage.cacheReadInputTokens ?? usage.promptCacheHitTokens ?? 'n/a'}`,
    `input=${usage.inputTokens ?? 'n/a'}`,
    `output=${usage.outputTokens ?? 'n/a'}`,
  ].join(', ');
}

function repositoryFromUrl(url: string): string | undefined {
  const match = url.match(/^https:\/\/github\.com\/([^/]+\/[^/]+)/);
  return match ? match[1] : undefined;
}

function shortSha(value: string): string {
  return value.slice(0, 12);
}
