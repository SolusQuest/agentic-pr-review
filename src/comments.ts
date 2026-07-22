import {
  type LineageAction,
  type LineageReason,
  type Phase,
  type ReviewTarget,
  type RuntimeLineageTotals,
  type RuntimeBackend,
  type RuntimeUsage,
  type StructuredFindingV1,
  type StructuredReviewEnvelopeV1,
} from './types.js';
import { sha256, truncateText } from './utils.js';

export const STICKY_COMMENT_MARKER = '<!-- agentic-pr-review:v1 -->';
const M4_STICKY_MARKER_PREFIX = '<!-- agentic-pr-review:m4-state/v1 ';
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
  runtime_backend?: RuntimeBackend;
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
  structuredReview: StructuredReviewEnvelopeV1;
  stateKey: string;
  phase: Phase;
  runtimeProvider: string;
  runtimeBackend?: RuntimeBackend;
  sessionId: string;
  previousHeadSha?: string;
  currentHeadSha: string;
  artifactName: string;
  runId: number;
  runAttempt: number;
  lineageReason: LineageReason;
  usage?: RuntimeUsage | null;
  observedTurns?: number | null;
  maxTurns?: number;
  lineageTotals?: RuntimeLineageTotals;
  maxReviewChars: number;
}

export interface M4StateCommentInput {
  octokit: any;
  owner: string;
  repo: string;
  prNumber: number;
  markerId: string;
  selectorRevision: string;
  structuredReview: StructuredReviewEnvelopeV1;
}

export async function upsertM4StateComment(
  input: M4StateCommentInput,
): Promise<{ commentUrl: string; commentId: string; bodySha256: string }> {
  const body = buildM4StateCommentBody(
    input.structuredReview,
    input.markerId,
    input.selectorRevision,
  );
  const comments = (await input.octokit.paginate(input.octokit.rest.issues.listComments, {
    owner: input.owner,
    repo: input.repo,
    issue_number: input.prNumber,
    per_page: 100,
  })) as IssueComment[];
  const match = comments
    .filter((comment) => parseM4StateMarker(comment.body ?? '')?.markerId === input.markerId)
    .sort((left, right) => left.id - right.id)[0];
  let response: { data: IssueComment };
  try {
    response = match
      ? await input.octokit.rest.issues.updateComment({
          owner: input.owner,
          repo: input.repo,
          comment_id: match.id,
          body,
        })
      : await input.octokit.rest.issues.createComment({
          owner: input.owner,
          repo: input.repo,
          issue_number: input.prNumber,
          body,
        });
  } catch {
    const reconciled = (await input.octokit.paginate(input.octokit.rest.issues.listComments, {
      owner: input.owner,
      repo: input.repo,
      issue_number: input.prNumber,
      per_page: 100,
    })) as IssueComment[];
    const exact = reconciled
      .filter((comment) => comment.body === body)
      .sort((left, right) => left.id - right.id)[0];
    if (exact) {
      return {
        commentUrl: String(exact.html_url ?? ''),
        commentId: String(exact.id),
        bodySha256: sha256(renderStructuredReviewMarkdown(input.structuredReview)),
      };
    }
    throw new Error('comment_outcome_unknown');
  }
  if (String(response.data.body ?? '') !== body) {
    throw new Error('comment_readback_failed');
  }
  return {
    commentUrl: String(response.data.html_url ?? ''),
    commentId: String(response.data.id),
    bodySha256: sha256(renderStructuredReviewMarkdown(input.structuredReview)),
  };
}

export function buildM4StateCommentBody(
  structuredReview: StructuredReviewEnvelopeV1,
  markerId: string,
  selectorRevision: string,
): string {
  const rendered = renderStructuredReviewMarkdown(structuredReview);
  const bodySha256 = sha256(rendered);
  return `${rendered}\n${M4_STICKY_MARKER_PREFIX}{"bodySha256":"${bodySha256}","markerId":"${markerId}","selectorRevision":"${selectorRevision}"} -->`;
}

function parseM4StateMarker(body: string): {
  readonly bodySha256: string;
  readonly markerId: string;
  readonly selectorRevision: string;
} | null {
  if ([...body.matchAll(/<!-- agentic-pr-review:m4-state\/v1 /gu)].length !== 1) return null;
  const match = body.match(
    /\n<!-- agentic-pr-review:m4-state\/v1 \{"bodySha256":"([a-f0-9]{64})","markerId":"([a-f0-9]{64})","selectorRevision":"(sha256:[a-f0-9]{64})"\} -->$/u,
  );
  if (!match) return null;
  const [, bodySha256, markerId, selectorRevision] = match;
  const rendered = body.slice(0, body.length - match[0].length);
  return sha256(rendered) === bodySha256 ? { bodySha256, markerId, selectorRevision } : null;
}

export function buildStickyComment(
  target: ReviewTarget,
  structuredReview: StructuredReviewEnvelopeV1,
  stateKey: string,
): string {
  return buildLineageCommentBody(
    {
      target,
      structuredReview,
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
    (isNonLegacyBackend(input.runtimeBackend)
      ? `${input.runtimeBackend}:${input.stateKey}:${input.runtimeProvider}:${input.runId}:${chainStart}`
      : `${input.stateKey}:${input.runtimeProvider}:${input.runId}:${chainStart}`);
  const meta: LineageMeta = {
    version: 1,
    lineage_id: lineageId,
    state_key: input.stateKey,
    runtime_provider: input.runtimeProvider,
    ...(isNonLegacyBackend(input.runtimeBackend) ? { runtime_backend: input.runtimeBackend } : {}),
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
  const runtimeLabel = isNonLegacyBackend(input.runtimeBackend)
    ? `${input.runtimeProvider} (${input.runtimeBackend})`
    : input.runtimeProvider;
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
    `| Runtime | ${runtimeLabel} |`,
    `| Session | \`${input.sessionId}\` |`,
    `| State artifact | \`${input.artifactName}\` |`,
    `| Run | ${runValue} |`,
    `| Usage | ${formatUsage(input.usage)} |`,
    `| Turns | ${formatTurns(input.observedTurns, input.maxTurns)} |`,
    `| Lineage | ${input.lineageTotals ? formatLineageTable(input.lineageTotals) : 'not available'} |`,
    `| Findings | ${formatFindingCounts(input.structuredReview)} |`,
    '',
  ].join('\n');

  const renderedReviewMarkdown = renderStructuredReviewMarkdown(input.structuredReview);
  if (renderedReviewMarkdown.length > input.maxReviewChars) {
    throw new Error(
      `structured review markdown exceeds max_review_chars (${renderedReviewMarkdown.length}/${input.maxReviewChars}); cap the structured review before posting`,
    );
  }
  const currentBlock = [
    CURRENT_BLOCK_START,
    '### Current Review',
    '',
    renderedReviewMarkdown,
    CURRENT_BLOCK_END,
  ].join('\n');
  const historyBlock = buildHistoryBlock(action, existingMeta, existingBody);
  return enforceMaxChars(header, currentBlock, historyBlock);
}

export function capStructuredReviewForMarkdownLimit(
  review: StructuredReviewEnvelopeV1,
  maxReviewChars: number,
): StructuredReviewEnvelopeV1 {
  let candidate = withRenderedFindings(review, review.findings);
  while (renderStructuredReviewMarkdown(candidate).length > maxReviewChars) {
    if (candidate.findings.length === 0) {
      throw new Error(
        `structured review metadata exceeds max_review_chars without findings (${renderStructuredReviewMarkdown(candidate).length}/${maxReviewChars})`,
      );
    }
    candidate = withRenderedFindings(candidate, candidate.findings.slice(0, -1));
  }
  return candidate;
}

export function renderStructuredReviewMarkdown(review: StructuredReviewEnvelopeV1): string {
  const lines = ['### Summary', '', sanitizeMarkdownText(review.summary), '', '### Findings', ''];

  if (review.findings.length === 0) {
    lines.push('No findings.', '');
  } else {
    for (const [index, finding] of review.findings.entries()) {
      lines.push(
        `#### ${index + 1}. ${sanitizeMarkdownText(finding.title)}`,
        '',
        `- Severity: ${finding.severity}`,
        `- Confidence: ${finding.confidence}`,
        `- Category: ${finding.category}`,
        `- Location: ${formatFindingLocation(finding)}`,
        `- Fingerprint: \`${finding.fingerprint}\``,
        '',
        sanitizeMarkdownText(finding.body),
        '',
      );
      if (finding.suggestedAction) {
        lines.push('Suggested action:', '', sanitizeMarkdownText(finding.suggestedAction), '');
      }
    }
  }

  if (review.result.findingsTruncated) {
    const reason = review.result.truncationReason
      ? ` Reason: ${review.result.truncationReason}.`
      : '';
    lines.push(
      `Finding list truncated from ${review.result.inputFindingCount} to ${review.result.renderedFindingCount}.${reason}`,
      '',
    );
  }

  lines.push('### Limitations', '');
  if (review.limitations.length === 0) {
    lines.push('None reported.', '');
  } else {
    for (const limitation of review.limitations) {
      lines.push(`- ${sanitizeMarkdownText(limitation)}`);
    }
    lines.push('');
  }

  lines.push(
    '### Review Metadata',
    '',
    '| Field | Value |',
    '| --- | --- |',
    `| Phase | ${review.phase} |`,
    `| Range | ${review.reviewedRange.kind} \`${formatOptionalSha(review.reviewedRange.fromSha)}\` -> \`${shortSha(review.reviewedRange.toSha)}\` |`,
    `| Tool mode | ${review.toolMode} |`,
    `| Runtime | ${review.runtimeProvider} |`,
    `| Usage | ${formatUsage(review.usage)} |`,
    `| Turns | ${review.observedTurns ?? 'not exposed'} |`,
    `| Lineage | ${formatLineageTable(review.lineageTotals)} |`,
  );

  return lines.join('\n');
}

function withRenderedFindings(
  review: StructuredReviewEnvelopeV1,
  findings: StructuredFindingV1[],
): StructuredReviewEnvelopeV1 {
  const postFindingCapCount =
    review.result.postFindingCapCount ?? review.result.renderedFindingCount;
  const truncatedByMaxFindings = review.result.inputFindingCount > postFindingCapCount;
  const truncatedByReviewChars = findings.length < postFindingCapCount;
  const truncationReason =
    truncatedByMaxFindings && truncatedByReviewChars
      ? 'both'
      : truncatedByReviewChars
        ? 'max_review_chars'
        : truncatedByMaxFindings
          ? 'max_findings'
          : undefined;
  return {
    ...review,
    findings,
    result: {
      ...review.result,
      postFindingCapCount,
      renderedFindingCount: findings.length,
      findingsTruncated: review.result.inputFindingCount > findings.length,
      truncationReason,
    },
  };
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
        candidate.meta.runtime_provider === input.runtimeProvider &&
        (candidate.meta.runtime_backend ?? 'legacy') === (input.runtimeBackend ?? 'legacy'),
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

function formatFindingCounts(review: StructuredReviewEnvelopeV1): string {
  const base = `${review.result.renderedFindingCount}/${review.result.postFindingCapCount}/${review.result.inputFindingCount}`;
  return review.result.findingsTruncated ? `${base} rendered, truncated` : `${base} rendered`;
}

function formatFindingLocation(finding: StructuredFindingV1): string {
  if (!finding.path) {
    return 'No file location';
  }
  const path = `\`${finding.path}\``;
  if (finding.startLine === null) {
    return path;
  }
  if (finding.endLine !== null && finding.endLine !== finding.startLine) {
    return `${path}:${finding.startLine}-${finding.endLine}`;
  }
  return `${path}:${finding.startLine}`;
}

function sanitizeMarkdownText(value: string): string {
  return value.replace(/<!--/g, '&lt;!--').replace(/-->/g, '--&gt;');
}

function formatUsage(usage: RuntimeUsage | undefined | null): string {
  if (!usage) {
    return 'not exposed';
  }
  return [
    `cache_read=${usage.cacheReadInputTokens}`,
    `cache_creation=${usage.cacheCreationInputTokens}`,
    `input=${usage.inputTokens}`,
    `output=${usage.outputTokens}`,
  ].join(', ');
}

function formatLineageTable(lineage: RuntimeLineageTotals): string {
  return [
    `turns=${lineage.observedTurns ?? 'n/a'}`,
    `input=${lineage.usage.inputTokens}`,
    `cache_read=${lineage.usage.cacheReadInputTokens}`,
    `output=${lineage.usage.outputTokens}`,
  ].join(', ');
}

function formatTurns(
  observedTurns: number | null | undefined,
  maxTurns: number | undefined,
): string {
  if (observedTurns === null || observedTurns === undefined) {
    return 'not exposed';
  }
  const max = maxTurns !== undefined ? ` / max ${maxTurns}` : '';
  return `${observedTurns}${max}`;
}

function repositoryFromUrl(url: string): string | undefined {
  const match = url.match(/^https:\/\/github\.com\/([^/]+\/[^/]+)/);
  return match ? match[1] : undefined;
}

function shortSha(value: string): string {
  return value.slice(0, 12);
}

function formatOptionalSha(value: string | null): string {
  return value ? shortSha(value) : 'n/a';
}

function isNonLegacyBackend(runtimeBackend: RuntimeBackend | undefined): boolean {
  return runtimeBackend !== undefined && runtimeBackend !== 'legacy';
}
