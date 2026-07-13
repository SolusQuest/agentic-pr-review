import { readFile, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { copyRuntimeStateToBundle } from './runtime.js';
import {
  type ActionConfig,
  type EffectiveDiffSource,
  type LoadedBlock,
  type Phase,
  type PullRequestDiffSnapshotV1,
  type RestoredState,
  type ReviewTarget,
  type RuntimeLineageTotals,
  type RuntimeBackend,
  type RuntimeResult,
  type RuntimeUsage,
  type StructuredReviewEnvelopeV1,
} from './types.js';
import { type StructuredResultMetadata } from './structured.js';
import {
  ensureDir,
  readJsonFile,
  normalizeRepoRelativePath,
  relativePosix,
  walkFiles,
  writeJsonFile,
  writeTextFile,
} from './utils.js';
import { sha256 } from './utils.js';

interface StateManifest {
  version: 1;
  workflow: 'agentic-pr-review';
  stateKey: string;
  phase: Phase;
  runtimeProvider: ActionConfig['runtimeProvider'];
  runtimeBackend?: RuntimeBackend;
  toolMode: ActionConfig['toolMode'];
  allowedTools: string[];
  sessionId: string;
  sessionName: string;
  reviewedHeadSha?: string;
  promptSha256?: string;
  reviewInputSha256?: string;
  reviewInputBytes?: number;
  createdAt: string;
  updatedAt: string;
  usage: RuntimeUsage | null;
  observedTurns?: number | null;
  observedTurnSource?: string;
  lineageTotals?: RuntimeLineageTotals;
  usageBudgetStatus: RuntimeResult['usageBudgetStatus'];
  review?: {
    requestedMode: ActionConfig['reviewMode'];
    executedPhase: Phase;
    phaseReason: string;
    effectiveDiffSource: EffectiveDiffSource;
  };
  structuredOutput: {
    status: StructuredResultMetadata['status'];
    inputFindingCount: number;
    postFindingCapCount: number;
    renderedFindingCount: number;
    findingsTruncated: boolean;
    truncationReason?: StructuredResultMetadata['truncationReason'];
    inlineComments?: StructuredResultMetadata['inlineComments'];
  };
  contextBlocks: Array<Pick<LoadedBlock, 'name' | 'source' | 'bytes' | 'sha256'>>;
  target: {
    mode: ReviewTarget['mode'];
    prNumber?: number;
    headRepository?: string;
    baseSha: string;
    headSha: string;
    changedFiles: number;
    pullRequestDiffSnapshot?: PullRequestDiffSnapshotV1;
  };
}

export class RestoredSnapshotInvalidError extends Error {
  constructor() {
    super('restored state manifest pull request diff snapshot is incompatible');
  }
}

export class StateManifestInvalidError extends Error {
  constructor() {
    super('restored state manifest shape is invalid');
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === 'object' && !Array.isArray(value);
}

const MANIFEST_KEYS = new Set([
  'version',
  'workflow',
  'stateKey',
  'phase',
  'runtimeProvider',
  'runtimeBackend',
  'toolMode',
  'allowedTools',
  'sessionId',
  'sessionName',
  'reviewedHeadSha',
  'promptSha256',
  'reviewInputSha256',
  'reviewInputBytes',
  'createdAt',
  'updatedAt',
  'usage',
  'observedTurns',
  'observedTurnSource',
  'lineageTotals',
  'usageBudgetStatus',
  'review',
  'structuredOutput',
  'contextBlocks',
  'target',
]);

const TARGET_KEYS = new Set([
  'mode',
  'prNumber',
  'headRepository',
  'baseSha',
  'headSha',
  'changedFiles',
  'pullRequestDiffSnapshot',
]);
const REVIEW_KEYS = new Set([
  'requestedMode',
  'executedPhase',
  'phaseReason',
  'effectiveDiffSource',
]);
const STRUCTURED_OUTPUT_KEYS = new Set([
  'status',
  'inputFindingCount',
  'postFindingCapCount',
  'renderedFindingCount',
  'findingsTruncated',
  'truncationReason',
  'inlineComments',
]);
const CONTEXT_BLOCK_KEYS = new Set(['name', 'source', 'bytes', 'sha256']);
const USAGE_KEYS = new Set([
  'inputTokens',
  'cacheReadInputTokens',
  'cacheCreationInputTokens',
  'outputTokens',
  'recordsObserved',
]);
const LINEAGE_USAGE_KEYS = new Set([
  'inputTokens',
  'cacheReadInputTokens',
  'cacheCreationInputTokens',
  'outputTokens',
]);
const LINEAGE_TOTALS_KEYS = new Set(['observedTurns', 'usage', 'source', 'partial']);
const USAGE_BUDGET_KEYS = new Set(['status', 'limits', 'usageRecordsObserved', 'exceeded']);
const USAGE_LIMIT_KEYS = new Set([
  'maxUncachedInputTokens',
  'maxCachedInputTokens',
  'maxOutputTokens',
]);
const USAGE_EXCEEDED_KEYS = new Set(['category', 'limit', 'observed']);
const INLINE_COMMENTS_KEYS = new Set([
  'enabled',
  'policy',
  'candidateCount',
  'effectiveCap',
  'capExceededCount',
  'postedCount',
  'duplicateCount',
  'skippedCount',
  'failedCount',
  'skippedReasons',
  'failedReasons',
]);
const INLINE_POLICY_KEYS = new Set(['enabled', 'maxComments', 'minSeverity', 'minConfidence']);
const SNAPSHOT_KEYS = new Set(['version', 'source', 'headSha', 'baseSha', 'files']);
const SNAPSHOT_FILE_KEYS = new Set([
  'filename',
  'previousFilename',
  'status',
  'additions',
  'deletions',
  'changes',
  'fileSha',
  'patchSha256',
  'patchAvailable',
]);

function invalidManifest(): never {
  throw new StateManifestInvalidError();
}

function assertAllowedKeys(value: Record<string, unknown>, allowed: Set<string>): void {
  if (Object.keys(value).some((key) => !allowed.has(key))) invalidManifest();
}

function boundedString(value: unknown, maxLength = 1024): value is string {
  return typeof value === 'string' && value.length <= maxLength && !/[\u0000\r\n]/.test(value);
}

function nonNegativeInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value) && value >= 0;
}

function optionalPositiveInteger(value: unknown): boolean {
  return (
    value === undefined || (typeof value === 'number' && Number.isSafeInteger(value) && value > 0)
  );
}

function validateUsage(value: unknown): void {
  if (!isRecord(value)) invalidManifest();
  assertAllowedKeys(value, USAGE_KEYS);
  if (
    !nonNegativeInteger(value.inputTokens) ||
    !nonNegativeInteger(value.cacheReadInputTokens) ||
    !nonNegativeInteger(value.cacheCreationInputTokens) ||
    !nonNegativeInteger(value.outputTokens) ||
    !nonNegativeInteger(value.recordsObserved)
  ) {
    invalidManifest();
  }
}

function validateLineageTotals(value: unknown): void {
  if (!isRecord(value)) invalidManifest();
  assertAllowedKeys(value, LINEAGE_TOTALS_KEYS);
  if (
    !(value.observedTurns === null || nonNegativeInteger(value.observedTurns)) ||
    !isRecord(value.usage) ||
    (value.source !== 'current_run_only' &&
      value.source !== 'restored_manifest_plus_current_run' &&
      value.source !== 'restored_manifest_preserved_for_skipped' &&
      value.source !== 'legacy_manifest_fallback' &&
      value.source !== 'unavailable') ||
    typeof value.partial !== 'boolean'
  ) {
    invalidManifest();
  }
  assertAllowedKeys(value.usage, LINEAGE_USAGE_KEYS);
  if (
    !nonNegativeInteger(value.usage.inputTokens) ||
    !nonNegativeInteger(value.usage.cacheReadInputTokens) ||
    !nonNegativeInteger(value.usage.cacheCreationInputTokens) ||
    !nonNegativeInteger(value.usage.outputTokens)
  ) {
    invalidManifest();
  }
}

function validateUsageBudgetStatus(value: unknown): void {
  if (!isRecord(value) || !isRecord(value.limits)) invalidManifest();
  assertAllowedKeys(value, USAGE_BUDGET_KEYS);
  assertAllowedKeys(value.limits, USAGE_LIMIT_KEYS);
  if (
    (value.status !== 'disabled' &&
      value.status !== 'within_limit' &&
      value.status !== 'exceeded' &&
      value.status !== 'not_applicable') ||
    !nonNegativeInteger(value.limits.maxUncachedInputTokens) ||
    !nonNegativeInteger(value.limits.maxCachedInputTokens) ||
    !nonNegativeInteger(value.limits.maxOutputTokens) ||
    !nonNegativeInteger(value.usageRecordsObserved)
  ) {
    invalidManifest();
  }
  if (value.exceeded !== undefined) {
    if (!isRecord(value.exceeded)) invalidManifest();
    assertAllowedKeys(value.exceeded, USAGE_EXCEEDED_KEYS);
    if (
      (value.exceeded.category !== 'uncached_input' &&
        value.exceeded.category !== 'cached_input' &&
        value.exceeded.category !== 'output') ||
      !nonNegativeInteger(value.exceeded.limit) ||
      !nonNegativeInteger(value.exceeded.observed)
    ) {
      invalidManifest();
    }
  }
}

function validateCountRecord(value: unknown): void {
  if (!isRecord(value)) invalidManifest();
  for (const count of Object.values(value)) {
    if (!nonNegativeInteger(count)) invalidManifest();
  }
}

function validateInlineComments(value: unknown): void {
  if (!isRecord(value) || !isRecord(value.policy)) invalidManifest();
  assertAllowedKeys(value, INLINE_COMMENTS_KEYS);
  assertAllowedKeys(value.policy, INLINE_POLICY_KEYS);
  if (
    typeof value.enabled !== 'boolean' ||
    typeof value.policy.enabled !== 'boolean' ||
    !nonNegativeInteger(value.policy.maxComments) ||
    (value.policy.minSeverity !== 'low' &&
      value.policy.minSeverity !== 'medium' &&
      value.policy.minSeverity !== 'high') ||
    (value.policy.minConfidence !== 'medium' && value.policy.minConfidence !== 'high')
  ) {
    invalidManifest();
  }
  for (const key of [
    'candidateCount',
    'effectiveCap',
    'capExceededCount',
    'postedCount',
    'duplicateCount',
    'skippedCount',
    'failedCount',
  ]) {
    if (!nonNegativeInteger(value[key])) invalidManifest();
  }
  validateCountRecord(value.skippedReasons);
  validateCountRecord(value.failedReasons);
}

function validateStructuredOutput(value: unknown): void {
  if (!isRecord(value)) invalidManifest();
  assertAllowedKeys(value, STRUCTURED_OUTPUT_KEYS);
  if (
    (value.status !== 'valid' &&
      value.status !== 'extracted' &&
      value.status !== 'invalid_json' &&
      value.status !== 'schema_invalid') ||
    !nonNegativeInteger(value.inputFindingCount) ||
    !nonNegativeInteger(value.postFindingCapCount) ||
    !nonNegativeInteger(value.renderedFindingCount) ||
    typeof value.findingsTruncated !== 'boolean' ||
    (value.truncationReason !== undefined &&
      value.truncationReason !== 'max_findings' &&
      value.truncationReason !== 'max_review_chars' &&
      value.truncationReason !== 'both')
  ) {
    invalidManifest();
  }
  if (value.inlineComments !== undefined) validateInlineComments(value.inlineComments);
}

function validateManifestShape(value: unknown): asserts value is StateManifest {
  if (!isRecord(value) || [...Object.keys(value)].some((key) => !MANIFEST_KEYS.has(key))) {
    invalidManifest();
  }
  if (
    value.version !== 1 ||
    value.workflow !== 'agentic-pr-review' ||
    !boundedString(value.stateKey, 200) ||
    (value.phase !== 'bootstrap' && value.phase !== 'incremental') ||
    (value.runtimeProvider !== 'test' && value.runtimeProvider !== 'claude-code-cli') ||
    (value.runtimeBackend !== undefined &&
      value.runtimeBackend !== 'legacy' &&
      value.runtimeBackend !== 'deterministic-csharp') ||
    (value.toolMode !== 'none' && value.toolMode !== 'readonly') ||
    !Array.isArray(value.allowedTools) ||
    value.allowedTools.length > 20 ||
    value.allowedTools.some((item) => !boundedString(item, 80)) ||
    !boundedString(value.sessionId, 200) ||
    !boundedString(value.sessionName, 200) ||
    (value.reviewedHeadSha !== undefined && !boundedString(value.reviewedHeadSha, 200)) ||
    (value.promptSha256 !== undefined && !/^[a-f0-9]{64}$/.test(String(value.promptSha256))) ||
    (value.reviewInputSha256 !== undefined &&
      !/^[a-f0-9]{64}$/.test(String(value.reviewInputSha256))) ||
    (value.reviewInputBytes !== undefined && !nonNegativeInteger(value.reviewInputBytes)) ||
    !boundedString(value.createdAt, 80) ||
    !boundedString(value.updatedAt, 80) ||
    !(value.usage === null || isRecord(value.usage)) ||
    (value.observedTurns !== undefined &&
      !(value.observedTurns === null || nonNegativeInteger(value.observedTurns))) ||
    (value.observedTurnSource !== undefined && !boundedString(value.observedTurnSource, 80)) ||
    (value.lineageTotals !== undefined && !isRecord(value.lineageTotals)) ||
    !isRecord(value.target) ||
    !isRecord(value.structuredOutput) ||
    !Array.isArray(value.contextBlocks) ||
    value.contextBlocks.length > 3
  ) {
    invalidManifest();
  }
  if (value.usage !== null) validateUsage(value.usage);
  if (value.lineageTotals !== undefined) validateLineageTotals(value.lineageTotals);
  validateUsageBudgetStatus(value.usageBudgetStatus);
  validateStructuredOutput(value.structuredOutput);
  if (value.review !== undefined) {
    if (!isRecord(value.review)) invalidManifest();
    assertAllowedKeys(value.review, REVIEW_KEYS);
    if (
      (value.review.requestedMode !== 'auto' &&
        value.review.requestedMode !== 'bootstrap' &&
        value.review.requestedMode !== 'incremental') ||
      (value.review.executedPhase !== 'bootstrap' &&
        value.review.executedPhase !== 'incremental') ||
      !boundedString(value.review.phaseReason, 120) ||
      (value.review.effectiveDiffSource !== 'target_changed_files' &&
        value.review.effectiveDiffSource !== 'bootstrap_pr_files' &&
        value.review.effectiveDiffSource !== 'incremental_pr_diff_snapshot_delta')
    ) {
      invalidManifest();
    }
  }
  for (const block of value.contextBlocks) {
    if (!isRecord(block)) invalidManifest();
    assertAllowedKeys(block, CONTEXT_BLOCK_KEYS);
    if (
      !boundedString(block.name, 80) ||
      (block.source !== 'input' && block.source !== 'path') ||
      !nonNegativeInteger(block.bytes) ||
      !/^[a-f0-9]{64}$/.test(String(block.sha256))
    ) {
      invalidManifest();
    }
  }
  const target = value.target;
  assertAllowedKeys(target, TARGET_KEYS);
  if (
    (target.mode !== 'pull-request' && target.mode !== 'synthetic-fixture') ||
    !optionalPositiveInteger(target.prNumber) ||
    (target.headRepository !== undefined && !boundedString(target.headRepository, 200)) ||
    !boundedString(target.baseSha, 200) ||
    !boundedString(target.headSha, 200) ||
    !nonNegativeInteger(target.changedFiles) ||
    (target.pullRequestDiffSnapshot !== undefined && !isRecord(target.pullRequestDiffSnapshot))
  ) {
    invalidManifest();
  }
  if (target.pullRequestDiffSnapshot !== undefined) {
    validatePullRequestDiffSnapshot(target.pullRequestDiffSnapshot);
  }
}

const SECRET_FILE_PATTERN =
  /(^|[\\/])(\.env|credentials?|secrets?|tokens?|settings\.local)(\.|[\\/]|$)/i;
const SECRET_CONTENT_PATTERN = /(ghp_|github_pat_|sk-[a-zA-Z0-9]|authorization:\s*bearer)/i;
const AUTH_HEADER_KEYS = new Set(['authorization', 'x-api-key', 'x-api-token']);
const HIGH_RISK_TOKEN_PATTERN = /(ghp_|github_pat_|sk-[a-zA-Z0-9])\S*/g;

export async function readRestoredState(root: string): Promise<RestoredState> {
  const manifestPath = path.join(root, 'manifest.json');
  const manifest = await readJsonFile<StateManifest>(manifestPath);
  validateManifestShape(manifest);
  const runtimeBackend = manifest.runtimeBackend ?? 'legacy';
  if (runtimeBackend !== 'legacy' && runtimeBackend !== 'deterministic-csharp') {
    throw new Error('restored state manifest has unknown runtime_backend');
  }
  const pullRequestDiffSnapshot = manifest.target?.pullRequestDiffSnapshot
    ? validatePullRequestDiffSnapshot(manifest.target.pullRequestDiffSnapshot)
    : undefined;
  return {
    runtimeBackend,
    stateKey: manifest.stateKey,
    sessionId: manifest.sessionId,
    sessionName: manifest.sessionName ?? `agentic-pr-review-${manifest.stateKey}`,
    runtimeProvider: manifest.runtimeProvider,
    reviewedHeadSha: manifest.reviewedHeadSha,
    createdAt: manifest.createdAt,
    usage: manifest.usage
      ? {
          inputTokens: manifest.usage.inputTokens ?? 0,
          cacheReadInputTokens: manifest.usage.cacheReadInputTokens ?? 0,
          cacheCreationInputTokens: manifest.usage.cacheCreationInputTokens ?? 0,
          outputTokens: manifest.usage.outputTokens ?? 0,
          recordsObserved: manifest.usage.recordsObserved ?? 0,
        }
      : null,
    observedTurns: manifest.observedTurns,
    observedTurnSource: manifest.observedTurnSource,
    lineageTotals: manifest.lineageTotals,
    pullRequestDiffSnapshot,
    prNumber: manifest.target.prNumber,
    headRepository: manifest.target.headRepository,
    manifestPath,
  };
}

export async function writeStateBundle(options: {
  bundleDir: string;
  config: ActionConfig;
  target: ReviewTarget;
  stateKey: string;
  phase: Phase;
  promptSha256?: string;
  reviewInputSha256?: string;
  reviewInputBytes?: number;
  blocks: LoadedBlock[];
  runtimeResult: RuntimeResult;
  structuredReview: StructuredReviewEnvelopeV1;
  structuredMetadata: StructuredResultMetadata;
  renderedReviewMarkdown: string;
  runtimeDir: string;
  phaseReason: string;
  effectiveDiffSource: EffectiveDiffSource;
  createdAt?: string;
}): Promise<string[]> {
  await rm(options.bundleDir, { recursive: true, force: true });
  await ensureDir(options.bundleDir);
  const runtimeBackend = options.config.runtimeBackend ?? 'legacy';
  if (runtimeBackend === 'legacy') {
    await copyRuntimeStateToBundle(
      options.runtimeDir,
      options.config.runtimeProvider,
      options.bundleDir,
    );
    await sanitizeRuntimeFiles(
      path.join(options.bundleDir, 'runtime'),
      knownSecrets(options.config),
    );
  }

  const now = new Date().toISOString();
  const manifest: StateManifest = {
    version: 1,
    workflow: 'agentic-pr-review',
    stateKey: options.stateKey,
    phase: options.phase,
    runtimeProvider: options.config.runtimeProvider,
    ...(runtimeBackend === 'deterministic-csharp' ? { runtimeBackend } : {}),
    toolMode: options.runtimeResult.toolMode,
    allowedTools: options.runtimeResult.allowedTools,
    sessionId: options.runtimeResult.sessionId,
    sessionName: options.runtimeResult.sessionName,
    reviewedHeadSha: options.target.headSha,
    ...(runtimeBackend === 'legacy'
      ? { promptSha256: options.promptSha256 ?? '' }
      : {
          ...(options.reviewInputSha256 !== undefined
            ? { reviewInputSha256: options.reviewInputSha256 }
            : {}),
          ...(options.reviewInputBytes !== undefined
            ? { reviewInputBytes: options.reviewInputBytes }
            : {}),
        }),
    createdAt: options.createdAt ?? now,
    updatedAt: now,
    usage: options.runtimeResult.usage,
    observedTurns: options.runtimeResult.observedTurns,
    observedTurnSource: options.runtimeResult.observedTurnSource,
    lineageTotals: options.runtimeResult.lineageTotals,
    usageBudgetStatus: options.runtimeResult.usageBudgetStatus,
    review: {
      requestedMode: options.config.reviewMode,
      executedPhase: options.phase,
      phaseReason: options.phaseReason,
      effectiveDiffSource: options.effectiveDiffSource,
    },
    structuredOutput: {
      status: options.structuredMetadata.status,
      inputFindingCount: options.structuredMetadata.inputFindingCount,
      postFindingCapCount: options.structuredMetadata.postFindingCapCount,
      renderedFindingCount: options.structuredMetadata.renderedFindingCount,
      findingsTruncated: options.structuredMetadata.findingsTruncated,
      truncationReason: options.structuredMetadata.truncationReason,
      inlineComments: options.structuredMetadata.inlineComments,
    },
    contextBlocks: options.blocks.map((block) => ({
      name: block.name,
      source: block.source,
      bytes: block.bytes,
      sha256: block.sha256,
    })),
    target: {
      mode: options.target.mode,
      prNumber: options.target.prNumber,
      headRepository: options.target.headRepoFullName,
      baseSha: options.target.baseSha,
      headSha: options.target.headSha,
      changedFiles: options.target.changedFiles.length,
      pullRequestDiffSnapshot: options.target.pullRequestDiffSnapshot,
    },
  };

  await writeJsonFile(path.join(options.bundleDir, 'manifest.json'), manifest);
  await writeJsonFile(
    path.join(options.bundleDir, 'structured-result.json'),
    options.structuredReview,
  );
  await writeTextFile(
    path.join(options.bundleDir, 'rendered-review.md'),
    options.renderedReviewMarkdown,
  );
  await sanitizeStateBundle(options.bundleDir, options.config);
  return await walkFiles(options.bundleDir);
}

function validatePullRequestDiffSnapshot(value: unknown): PullRequestDiffSnapshotV1 {
  if (!value || typeof value !== 'object') {
    throw new RestoredSnapshotInvalidError();
  }
  const snapshot = value as PullRequestDiffSnapshotV1;
  if (isRecord(value) && Object.keys(value).some((key) => !SNAPSHOT_KEYS.has(key))) {
    throw new RestoredSnapshotInvalidError();
  }
  if (
    snapshot.version !== 1 ||
    snapshot.source !== 'github-pulls-list-files' ||
    !boundedString(snapshot.headSha, 200) ||
    !boundedString(snapshot.baseSha, 200) ||
    !Array.isArray(snapshot.files) ||
    snapshot.files.length > 10_000
  ) {
    throw new RestoredSnapshotInvalidError();
  }
  return {
    version: 1,
    source: 'github-pulls-list-files',
    headSha: snapshot.headSha,
    baseSha: snapshot.baseSha,
    files: snapshot.files.map((entry) => {
      if (!entry || typeof entry !== 'object') {
        throw new RestoredSnapshotInvalidError();
      }
      const candidate = entry as PullRequestDiffSnapshotV1['files'][number];
      if (isRecord(entry) && Object.keys(entry).some((key) => !SNAPSHOT_FILE_KEYS.has(key))) {
        throw new RestoredSnapshotInvalidError();
      }
      if (
        !boundedString(candidate.filename, 500) ||
        (candidate.previousFilename !== undefined &&
          !boundedString(candidate.previousFilename, 500)) ||
        !boundedString(candidate.status, 80) ||
        !nonNegativeInteger(candidate.additions) ||
        !nonNegativeInteger(candidate.deletions) ||
        !nonNegativeInteger(candidate.changes) ||
        (candidate.fileSha !== undefined && !boundedString(candidate.fileSha, 200)) ||
        typeof candidate.patchAvailable !== 'boolean' ||
        (candidate.patchSha256 !== null && !/^[a-f0-9]{64}$/.test(candidate.patchSha256))
      ) {
        throw new RestoredSnapshotInvalidError();
      }
      if (
        candidate.patchAvailable
          ? typeof candidate.patchSha256 !== 'string'
          : candidate.patchSha256 !== null
      ) {
        throw new RestoredSnapshotInvalidError();
      }
      return {
        filename: normalizeRepoRelativePath(candidate.filename),
        previousFilename: candidate.previousFilename
          ? normalizeRepoRelativePath(candidate.previousFilename)
          : undefined,
        status: candidate.status,
        additions: candidate.additions,
        deletions: candidate.deletions,
        changes: candidate.changes,
        fileSha: candidate.fileSha,
        patchAvailable: candidate.patchAvailable,
        patchSha256: candidate.patchSha256,
      };
    }),
  };
}

export async function sanitizeStateBundle(
  bundleDir: string,
  config: Pick<ActionConfig, 'apiKey' | 'githubToken'>,
): Promise<void> {
  const secrets = knownSecrets(config);
  const files = await walkFiles(bundleDir);
  for (const file of files) {
    const rel = relativePosix(bundleDir, file);
    const lower = rel.toLowerCase();
    const isRuntimeFile = lower.startsWith('runtime/');
    if (lower.includes('raw') || lower.includes('debug')) {
      throw new Error(`normal state artifact cannot include diagnostic file: ${rel}`);
    }
    if (!isRuntimeFile && SECRET_FILE_PATTERN.test(rel)) {
      throw new Error(`normal state artifact cannot include sensitive-looking file: ${rel}`);
    }

    const fileStat = await stat(file);
    if (fileStat.size > 1024 * 1024) {
      throw new Error(`normal state artifact file is too large to scan safely: ${rel}`);
    }

    const content = await readFile(file, 'utf8').catch(() => '');
    const contentWithoutRedactions = content.replaceAll('***REDACTED***', '');
    if (!isRuntimeFile && SECRET_CONTENT_PATTERN.test(contentWithoutRedactions)) {
      throw new Error(`normal state artifact contains sensitive-looking content: ${rel}`);
    }
    if (hasUnredactedAuthHeader(content)) {
      throw new Error(`normal state artifact contains unredacted auth header: ${rel}`);
    }
    for (const secret of secrets) {
      if (content.includes(secret)) {
        throw new Error(`normal state artifact contains a configured secret: ${rel}`);
      }
    }
  }
}

async function sanitizeRuntimeFiles(runtimeRoot: string, secrets: string[]): Promise<void> {
  const files = await walkFiles(runtimeRoot);
  for (const file of files) {
    const fileStat = await stat(file);
    if (fileStat.size > 1024 * 1024) {
      continue;
    }
    const content = await readFile(file, 'utf8').catch(() => '');
    if (!content) {
      continue;
    }
    const sanitized = content
      .split(/\r?\n/)
      .map((line) => sanitizeLine(line, secrets))
      .join('\n');
    if (sanitized !== content) {
      await writeFile(file, sanitized, 'utf8');
    }
  }
}

function sanitizeLine(line: string, secrets: string[]): string {
  if (!line.trim()) {
    return line;
  }
  const parsed = safeParseJson(line);
  if (parsed !== undefined) {
    return JSON.stringify(sanitizeJsonValue(parsed, secrets));
  }
  return sanitizeText(line, secrets);
}

function sanitizeJsonValue(value: unknown, secrets: string[]): unknown {
  if (typeof value === 'string') {
    return sanitizeText(value, secrets);
  }
  if (Array.isArray(value)) {
    return value.map((item) => sanitizeJsonValue(item, secrets));
  }
  if (value && typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if (AUTH_HEADER_KEYS.has(key.toLowerCase()) && typeof child === 'string') {
        result[key] = '***REDACTED***';
      } else {
        result[key] = sanitizeJsonValue(child, secrets);
      }
    }
    return result;
  }
  return value;
}

function sanitizeText(value: string, secrets: string[]): string {
  let result = value;
  for (const secret of secrets) {
    result = result.replaceAll(secret, '***REDACTED***');
  }
  return result
    .replace(HIGH_RISK_TOKEN_PATTERN, '***REDACTED***')
    .replace(
      /(Authorization|x-api-key|x-api-token)(["\s]*:?\s*(?:Bearer\s+)?)(?!\*\*\*)\S+/gi,
      '$1$2***REDACTED***',
    );
}

function hasUnredactedAuthHeader(content: string): boolean {
  for (const line of content.split(/\r?\n/)) {
    const parsed = safeParseJson(line);
    if (parsed !== undefined && hasUnredactedJsonAuthHeader(parsed)) {
      return true;
    }
    const sanitized = sanitizeText(line, []);
    if (sanitized !== line && !line.includes('***REDACTED***')) {
      return true;
    }
  }
  return false;
}

function hasUnredactedJsonAuthHeader(value: unknown): boolean {
  if (!value || typeof value !== 'object') {
    return false;
  }
  if (Array.isArray(value)) {
    return value.some(hasUnredactedJsonAuthHeader);
  }
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    if (AUTH_HEADER_KEYS.has(key.toLowerCase())) {
      if (typeof child === 'string' && child !== '***REDACTED***') {
        return true;
      }
    } else if (hasUnredactedJsonAuthHeader(child)) {
      return true;
    }
  }
  return false;
}

function knownSecrets(config: Pick<ActionConfig, 'apiKey' | 'githubToken'>): string[] {
  return [config.apiKey, config.githubToken].filter((value): value is string =>
    Boolean(value && value.length >= 8),
  );
}

function safeParseJson(value: string): unknown | undefined {
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return undefined;
  }
}

export function stateArtifactName(stateKey: string): string {
  return `agentic-pr-review-state-${stateKey}`;
}

export function deterministicStateKey(baseStateKey: string): string {
  return `cs-${sha256(baseStateKey).slice(0, 20)}`;
}

export function deterministicStateArtifactName(logicalStateKey: string): string {
  return `agentic-pr-review-deterministic-csharp-state-${logicalStateKey}`;
}

export function debugArtifactName(stateKey: string): string {
  return `agentic-pr-review-raw-debug-${stateKey}`;
}
