import {
  type ChangedFile,
  type InlineCommentsMetadata,
  type InlineCommentsPolicy,
  type StructuredFindingV1,
  type StructuredReviewEnvelopeV1,
} from './types.js';
import { sha256, truncateText } from './utils.js';

export const INLINE_COMMENT_MARKER_PREFIX = '<!-- agentic-pr-review:inline:v1';
export const INLINE_COMMENT_MARKER_SUFFIX = '-->';

const INLINE_COMMENT_MARKER_PATTERN =
  /<!--\s*agentic-pr-review:inline:v1\s+key=([a-f0-9]{64})\s*-->/gi;
const SUPPORTED_PULL_REQUEST_FILE_LIMIT = 3000;
const SUPPORTED_REVIEW_COMMENT_LIMIT = 3000;
const SEVERITY_RANK: Record<StructuredFindingV1['severity'], number> = {
  low: 0,
  medium: 1,
  high: 2,
};
const CONFIDENCE_RANK: Record<StructuredFindingV1['confidence'], number> = {
  medium: 0,
  high: 1,
};

interface ReviewComment {
  body?: string | null;
}

interface PullRequestFile extends ChangedFile {}

export interface CommentableLineIndexEntry {
  lines: Set<number>;
  hasPatch: boolean;
}

export type CommentableLineIndex = Map<string, CommentableLineIndexEntry>;

export interface InlineCommentCandidate {
  finding: StructuredFindingV1;
  path: string;
  line: number;
  endLine: number | null;
  key: string;
  body: string;
}

export interface PostInlineCommentsInput {
  octokit: any;
  owner: string;
  repo: string;
  prNumber: number;
  stateKey: string;
  reviewedHeadSha: string;
  stickyCommentUrl?: string;
  structuredReview: StructuredReviewEnvelopeV1;
  policy: InlineCommentsPolicy;
}

export function defaultInlineCommentsMetadata(
  policy: InlineCommentsPolicy,
): InlineCommentsMetadata {
  return {
    enabled: policy.enabled,
    policy,
    candidateCount: 0,
    effectiveCap: policy.maxComments,
    capExceededCount: 0,
    postedCount: 0,
    duplicateCount: 0,
    skippedCount: 0,
    failedCount: 0,
    skippedReasons: {},
    failedReasons: {},
  };
}

export async function postInlineComments(
  input: PostInlineCommentsInput,
): Promise<InlineCommentsMetadata> {
  const metadata = defaultInlineCommentsMetadata(input.policy);
  if (!input.policy.enabled) {
    skipAll(metadata, input.structuredReview.findings.length, 'disabled');
    return metadata;
  }

  const files = await listPullRequestFiles(input);
  if (files.length >= SUPPORTED_PULL_REQUEST_FILE_LIMIT) {
    skipAll(metadata, input.structuredReview.findings.length, 'diff_too_large');
    return metadata;
  }

  const lineIndex = buildCommentableLineIndex(files);
  const selected = selectInlineCommentCandidates({
    review: input.structuredReview,
    policy: input.policy,
    stateKey: input.stateKey,
    lineIndex,
    stickyCommentUrl: input.stickyCommentUrl,
  });
  mergeReasons(metadata.skippedReasons, selected.skippedReasons);
  metadata.skippedCount += selected.skippedCount;
  metadata.candidateCount = selected.candidates.length;
  if (selected.candidates.length === 0) {
    return metadata;
  }

  let existingKeys = await listExistingInlineMarkerKeys(input);
  if (existingKeys === 'too_large') {
    skipAll(metadata, selected.candidates.length, 'diff_too_large');
    return metadata;
  }
  let pending = suppressDuplicateCandidates(selected.candidates, existingKeys, metadata);
  metadata.capExceededCount = Math.max(0, pending.length - input.policy.maxComments);
  addSkip(metadata, 'cap_exceeded', metadata.capExceededCount);
  pending = pending.slice(0, input.policy.maxComments);
  if (pending.length === 0) {
    return metadata;
  }

  const currentHeadSha = await fetchCurrentHeadSha(input);
  if (currentHeadSha !== input.reviewedHeadSha) {
    skipAll(metadata, pending.length, 'head_changed');
    return metadata;
  }

  try {
    await input.octokit.rest.pulls.createReview({
      owner: input.owner,
      repo: input.repo,
      pull_number: input.prNumber,
      commit_id: input.reviewedHeadSha,
      event: 'COMMENT',
      body: 'Agentic PR Review inline comments. See the sticky top-level review comment for the full structured review.',
      comments: pending.map((candidate) => ({
        path: candidate.path,
        line: candidate.line,
        side: 'RIGHT',
        body: candidate.body,
      })),
    });
    metadata.postedCount += pending.length;
    return metadata;
  } catch (error) {
    const classification = classifyGitHubPostingError(error);
    if (!classification.allowIndividualFallback) {
      failAll(metadata, pending.length, classification.reason);
      return metadata;
    }
    addReason(metadata.failedReasons, `batch_${classification.reason}`, 1);
  }

  existingKeys = await listExistingInlineMarkerKeys(input);
  if (existingKeys === 'too_large') {
    skipAll(metadata, pending.length, 'diff_too_large');
    return metadata;
  }
  pending = suppressDuplicateCandidates(pending, existingKeys, metadata);
  if (pending.length === 0) {
    return metadata;
  }
  const fallbackHeadSha = await fetchCurrentHeadSha(input);
  if (fallbackHeadSha !== input.reviewedHeadSha) {
    skipAll(metadata, pending.length, 'head_changed');
    return metadata;
  }
  for (const candidate of pending) {
    try {
      await input.octokit.rest.pulls.createReviewComment({
        owner: input.owner,
        repo: input.repo,
        pull_number: input.prNumber,
        commit_id: input.reviewedHeadSha,
        path: candidate.path,
        line: candidate.line,
        side: 'RIGHT',
        body: candidate.body,
      });
      metadata.postedCount += 1;
    } catch (error) {
      const classification = classifyGitHubPostingError(error);
      metadata.failedCount += 1;
      addReason(metadata.failedReasons, classification.reason, 1);
    }
  }
  return metadata;
}

export function buildCommentableLineIndex(files: PullRequestFile[]): CommentableLineIndex {
  const index: CommentableLineIndex = new Map();
  for (const file of files) {
    const path = normalizePath(file.filename);
    if (!file.patch) {
      index.set(path, { lines: new Set(), hasPatch: false });
      continue;
    }
    const lines = commentableRightSideLines(file.patch);
    index.set(path, { lines, hasPatch: true });
  }
  return index;
}

export function selectInlineCommentCandidates(input: {
  review: StructuredReviewEnvelopeV1;
  policy: InlineCommentsPolicy;
  stateKey: string;
  lineIndex: CommentableLineIndex;
  stickyCommentUrl?: string;
}): {
  candidates: InlineCommentCandidate[];
  skippedCount: number;
  skippedReasons: Record<string, number>;
} {
  const candidates: InlineCommentCandidate[] = [];
  const skippedReasons: Record<string, number> = {};
  let skippedCount = 0;
  for (const finding of input.review.findings) {
    const skipReason = inlineFindingSkipReason(finding, input.policy, input.lineIndex);
    if (skipReason) {
      skippedCount += 1;
      addReason(skippedReasons, skipReason, 1);
      continue;
    }
    const path = normalizePath(finding.path!);
    const line = finding.startLine!;
    const key = inlineCommentKey({
      stateKey: input.stateKey,
      fingerprint: finding.fingerprint,
      path,
      startLine: line,
      endLine: finding.endLine,
    });
    candidates.push({
      finding,
      path,
      line,
      endLine: finding.endLine,
      key,
      body: renderInlineCommentBody(finding, key, input.stickyCommentUrl),
    });
  }
  return { candidates, skippedCount, skippedReasons };
}

export function inlineCommentKey(input: {
  stateKey: string;
  fingerprint: string;
  path: string;
  startLine: number;
  endLine: number | null;
}): string {
  return sha256(
    JSON.stringify({
      version: 1,
      stateKey: input.stateKey,
      fingerprint: input.fingerprint,
      path: normalizePath(input.path),
      startLine: input.startLine,
      endLine: input.endLine ?? null,
    }),
  );
}

export function parseInlineCommentMarkerKeys(body: string): Set<string> {
  const keys = new Set<string>();
  for (const match of body.matchAll(INLINE_COMMENT_MARKER_PATTERN)) {
    keys.add(match[1]);
  }
  return keys;
}

export function renderInlineCommentBody(
  finding: StructuredFindingV1,
  key: string,
  stickyCommentUrl?: string,
): string {
  const lines = [
    `${INLINE_COMMENT_MARKER_PREFIX} key=${key} ${INLINE_COMMENT_MARKER_SUFFIX}`,
    `**${sanitizeMarkdownText(finding.title)}**`,
    '',
    `Severity: ${finding.severity} | Confidence: ${finding.confidence} | Category: ${finding.category}`,
    '',
    truncateText(sanitizeMarkdownText(finding.body), 1800),
  ];
  if (finding.endLine !== null && finding.endLine !== finding.startLine) {
    lines.push('', `Original range: \`${finding.path}:${finding.startLine}-${finding.endLine}\``);
  }
  if (finding.suggestedAction) {
    lines.push(
      '',
      'Suggested action:',
      '',
      truncateText(sanitizeMarkdownText(finding.suggestedAction), 900),
    );
  }
  lines.push(
    '',
    stickyCommentUrl
      ? `Full review: ${stickyCommentUrl}`
      : 'Full review: sticky top-level Agentic PR Review comment.',
  );
  return truncateText(lines.join('\n'), 4000);
}

function inlineFindingSkipReason(
  finding: StructuredFindingV1,
  policy: InlineCommentsPolicy,
  lineIndex: CommentableLineIndex,
): string | undefined {
  if (SEVERITY_RANK[finding.severity] < SEVERITY_RANK[policy.minSeverity]) {
    return 'below_threshold';
  }
  if (CONFIDENCE_RANK[finding.confidence] < CONFIDENCE_RANK[policy.minConfidence]) {
    return 'below_threshold';
  }
  if (!finding.path || !finding.startLine) {
    return 'missing_location';
  }
  const entry = lineIndex.get(normalizePath(finding.path));
  if (!entry) {
    return 'path_not_in_diff';
  }
  if (!entry.hasPatch) {
    return 'binary_or_missing_patch';
  }
  if (!entry.lines.has(finding.startLine)) {
    return 'line_not_commentable';
  }
  return undefined;
}

function commentableRightSideLines(patch: string): Set<number> {
  const result = new Set<number>();
  let newLine: number | undefined;
  for (const line of patch.split('\n')) {
    const hunk = line.match(/^@@ -\d+(?:,\d+)? \+(\d+)(?:,\d+)? @@/);
    if (hunk) {
      newLine = Number.parseInt(hunk[1], 10);
      continue;
    }
    if (newLine === undefined || line.startsWith('\\')) {
      continue;
    }
    if (line.startsWith('+') && !line.startsWith('+++')) {
      result.add(newLine);
      newLine += 1;
    } else if (line.startsWith(' ')) {
      result.add(newLine);
      newLine += 1;
    } else if (line.startsWith('-') && !line.startsWith('---')) {
      continue;
    }
  }
  return result;
}

async function listPullRequestFiles(input: PostInlineCommentsInput): Promise<PullRequestFile[]> {
  return (await input.octokit.paginate(input.octokit.rest.pulls.listFiles, {
    owner: input.owner,
    repo: input.repo,
    pull_number: input.prNumber,
    per_page: 100,
  })) as PullRequestFile[];
}

async function listExistingInlineMarkerKeys(
  input: PostInlineCommentsInput,
): Promise<Set<string> | 'too_large'> {
  const comments = (await input.octokit.paginate(input.octokit.rest.pulls.listReviewComments, {
    owner: input.owner,
    repo: input.repo,
    pull_number: input.prNumber,
    per_page: 100,
  })) as ReviewComment[];
  if (comments.length >= SUPPORTED_REVIEW_COMMENT_LIMIT) {
    return 'too_large';
  }
  const keys = new Set<string>();
  for (const comment of comments) {
    for (const key of parseInlineCommentMarkerKeys(comment.body ?? '')) {
      keys.add(key);
    }
  }
  return keys;
}

async function fetchCurrentHeadSha(input: PostInlineCommentsInput): Promise<string> {
  const pull = await input.octokit.rest.pulls.get({
    owner: input.owner,
    repo: input.repo,
    pull_number: input.prNumber,
  });
  return String(pull.data.head.sha);
}

function suppressDuplicateCandidates(
  candidates: InlineCommentCandidate[],
  existingKeys: Set<string>,
  metadata: InlineCommentsMetadata,
): InlineCommentCandidate[] {
  const pending: InlineCommentCandidate[] = [];
  for (const candidate of candidates) {
    if (existingKeys.has(candidate.key)) {
      metadata.duplicateCount += 1;
    } else {
      pending.push(candidate);
    }
  }
  return pending;
}

function classifyGitHubPostingError(error: unknown): {
  reason: string;
  allowIndividualFallback: boolean;
} {
  const status =
    typeof (error as { status?: unknown })?.status === 'number'
      ? (error as { status: number }).status
      : undefined;
  const message =
    error instanceof Error ? error.message.toLowerCase() : String(error).toLowerCase();
  if (status === 401) {
    return { reason: 'authentication_failed', allowIndividualFallback: false };
  }
  if (status === 403 && /secondary|rate limit|abuse/.test(message)) {
    return { reason: 'secondary_rate_limit', allowIndividualFallback: false };
  }
  if (status === 429) {
    return { reason: 'secondary_rate_limit', allowIndividualFallback: false };
  }
  if (status === 403) {
    return { reason: 'permission_denied', allowIndividualFallback: false };
  }
  if (status === 404) {
    return { reason: 'repository_policy', allowIndividualFallback: false };
  }
  if (status === 422 && /spam|spammed|abuse|secondary|rate limit/.test(message)) {
    return { reason: 'secondary_rate_limit', allowIndividualFallback: false };
  }
  if (status === 422 && /policy|protected|forbidden/.test(message)) {
    return { reason: 'repository_policy', allowIndividualFallback: false };
  }
  if (status === 422) {
    return { reason: 'validation_failed', allowIndividualFallback: true };
  }
  return { reason: 'ambiguous_failure', allowIndividualFallback: true };
}

function skipAll(metadata: InlineCommentsMetadata, count: number, reason: string): void {
  metadata.skippedCount += count;
  addReason(metadata.skippedReasons, reason, count);
}

function failAll(metadata: InlineCommentsMetadata, count: number, reason: string): void {
  metadata.failedCount += count;
  addReason(metadata.failedReasons, reason, count);
}

function addSkip(metadata: InlineCommentsMetadata, reason: string, count: number): void {
  if (count <= 0) {
    return;
  }
  metadata.skippedCount += count;
  addReason(metadata.skippedReasons, reason, count);
}

function addReason(reasons: Record<string, number>, reason: string, count: number): void {
  if (count <= 0) {
    return;
  }
  reasons[reason] = (reasons[reason] ?? 0) + count;
}

function mergeReasons(target: Record<string, number>, source: Record<string, number>): void {
  for (const [reason, count] of Object.entries(source)) {
    addReason(target, reason, count);
  }
}

function sanitizeMarkdownText(value: string): string {
  return value.replace(/<!--/g, '&lt;!--').replace(/-->/g, '--&gt;');
}

function normalizePath(value: string): string {
  return value.trim().replace(/\\/g, '/');
}
