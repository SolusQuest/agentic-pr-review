export type RuntimeProvider = 'test' | 'claude-code-cli';
export type RuntimeBackend = 'legacy' | 'deterministic-csharp' | 'ledger-csharp';
export type TargetMode = 'pull-request' | 'synthetic-fixture';
export type ReviewMode = 'auto' | 'bootstrap' | 'incremental';
export type Phase = 'bootstrap' | 'incremental';
export type EffectiveDiffSource =
  | 'target_changed_files'
  | 'bootstrap_pr_files'
  | 'incremental_pr_diff_snapshot_delta';
export type ApiKeyMode = 'auth-token' | 'api-key' | 'both';
export type ToolMode = 'none' | 'readonly';
export type InlineCommentSeverity = 'low' | 'medium' | 'high';
export type InlineCommentConfidence = 'medium' | 'high';
export type TestRuntimeFixture =
  | 'valid'
  | 'no_findings'
  | 'null_location'
  | 'many_findings'
  | 'inline_commentable'
  | 'inline_non_commentable'
  | 'inline_many_findings'
  | 'invalid_json'
  | 'schema_invalid';

export interface ModelReviewFindingV1 {
  severity: 'low' | 'medium' | 'high';
  confidence: 'medium' | 'high';
  category:
    | 'correctness'
    | 'security'
    | 'requirements'
    | 'test_coverage'
    | 'build'
    | 'performance'
    | 'maintainability'
    | 'documentation';
  title: string;
  body: string;
  path: string | null;
  startLine: number | null;
  endLine: number | null;
  suggestedAction?: string;
}

export interface ModelReviewContentV1 {
  schemaVersion: 1;
  summary: string;
  findings: ModelReviewFindingV1[];
  limitations: string[];
}

export interface StructuredFindingV1 extends ModelReviewFindingV1 {
  fingerprint: string;
}

export interface ReviewedRange {
  kind: 'bootstrap' | 'incremental';
  fromSha: string | null;
  toSha: string;
}

export interface StructuredReviewEnvelopeV1 {
  schemaVersion: 1;
  phase: Phase;
  baseSha: string;
  headSha: string;
  previousReviewedHeadSha: string | null;
  reviewedRange: ReviewedRange;
  toolMode: ToolMode;
  runtimeProvider: RuntimeProvider;
  sessionId: string;
  summary: string;
  findings: StructuredFindingV1[];
  limitations: string[];
  usage: RuntimeUsage | null;
  observedTurns: number | null;
  observedTurnSource: string;
  lineageTotals: RuntimeLineageTotals;
  result: {
    inputFindingCount: number;
    postFindingCapCount: number;
    renderedFindingCount: number;
    findingsTruncated: boolean;
    truncationReason?: 'max_findings' | 'max_review_chars' | 'both';
  };
  inlineComments?: InlineCommentsMetadata;
}

export interface UsageBudgetLimits {
  maxUncachedInputTokens: number;
  maxCachedInputTokens: number;
  maxOutputTokens: number;
}

export interface UsageBudgetStatus {
  status: 'disabled' | 'within_limit' | 'exceeded' | 'not_applicable';
  limits: UsageBudgetLimits;
  usageRecordsObserved: number;
  exceeded?: {
    category: 'uncached_input' | 'cached_input' | 'output';
    limit: number;
    observed: number;
  };
}

export interface ActionConfig {
  /** Missing in hand-built legacy test fixtures; parsed action config always supplies legacy. */
  runtimeBackend?: RuntimeBackend;
  runtimeProvider: RuntimeProvider;
  targetMode: TargetMode;
  reviewMode: ReviewMode;
  verificationNamespace?: string;
  prNumber?: number;
  stateKey?: string;
  stateArtifactRunId?: number;
  artifactRetentionDays: number;
  postComment: boolean;
  modelBaseUrl?: string;
  modelName?: string;
  smallModelName?: string;
  apiKeyMode: ApiKeyMode;
  claudeCodeVersion?: string;
  toolMode: ToolMode;
  claudeMaxTurns: number;
  instructions?: string;
  instructionsPath?: string;
  bootstrapContext?: string;
  bootstrapContextPath?: string;
  incrementalContext?: string;
  incrementalContextPath?: string;
  maxContextChars: number;
  maxPatchChars: number;
  maxReviewChars: number;
  maxFindings: number;
  inlineComments: boolean;
  maxInlineComments: number;
  inlineMinSeverity: InlineCommentSeverity;
  inlineMinConfidence: InlineCommentConfidence;
  testRuntimeFixture: TestRuntimeFixture;
  usageBudgetLimits: UsageBudgetLimits;
  disablePromptCaching: boolean;
  debugCaptureRawApiBodies: boolean;
  debugAcknowledgement?: string;
  githubToken: string;
  apiKey?: string;
}

export interface InlineCommentsPolicy {
  enabled: boolean;
  maxComments: number;
  minSeverity: InlineCommentSeverity;
  minConfidence: InlineCommentConfidence;
}

export interface InlineCommentsMetadata {
  enabled: boolean;
  policy: InlineCommentsPolicy;
  candidateCount: number;
  effectiveCap: number;
  capExceededCount: number;
  postedCount: number;
  duplicateCount: number;
  skippedCount: number;
  failedCount: number;
  skippedReasons: Record<string, number>;
  failedReasons: Record<string, number>;
}

export interface LoadedBlock {
  name: 'instructions' | 'bootstrap_context' | 'incremental_context';
  source: 'input' | 'path';
  text: string;
  bytes: number;
  sha256: string;
}

export interface ChangedFile {
  filename: string;
  previousFilename?: string;
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  patch?: string;
}

export interface PullRequestDiffSnapshotV1 {
  version: 1;
  source: 'github-pulls-list-files';
  headSha: string;
  baseSha: string;
  files: PullRequestDiffSnapshotEntryV1[];
}

export interface PullRequestDiffSnapshotEntryV1 {
  filename: string;
  previousFilename?: string;
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  fileSha?: string;
  patchSha256: string | null;
  patchAvailable: boolean;
}

export interface PullRequestDiffSnapshotChangedEntryV1 {
  kind: 'current_changed';
  reason: 'new_file' | 'metadata_changed';
  current: PullRequestDiffSnapshotEntryV1;
  previous?: PullRequestDiffSnapshotEntryV1;
  patch?: string;
}

export interface PullRequestDiffSnapshotRemovedEntryV1 {
  kind: 'removed_from_pr_diff';
  previous: PullRequestDiffSnapshotEntryV1;
}

export interface PullRequestDiffSnapshotDeltaV1 {
  version: 1;
  source: 'github-pulls-list-files';
  changedEntries: PullRequestDiffSnapshotChangedEntryV1[];
  removedEntries: PullRequestDiffSnapshotRemovedEntryV1[];
  unchangedCount: number;
}

export interface ReviewTarget {
  mode: TargetMode;
  prNumber?: number;
  title: string;
  body: string;
  baseRef: string;
  baseSha: string;
  headRef: string;
  headSha: string;
  headRepoFullName?: string;
  draft: boolean;
  /** GitHub pull-request state when resolved from the API. */
  isOpen?: boolean;
  changedFiles: ChangedFile[];
  pullRequestDiffSnapshot?: PullRequestDiffSnapshotV1;
  htmlUrl?: string;
}

export interface RestoredState {
  /** Missing manifests and legacy fixtures normalize to legacy. */
  runtimeBackend?: RuntimeBackend;
  stateKey: string;
  sessionId: string;
  sessionName: string;
  runtimeProvider: RuntimeProvider;
  reviewedHeadSha?: string;
  createdAt?: string;
  usage: RuntimeUsage | null;
  observedTurns?: number | null;
  observedTurnSource?: string;
  lineageTotals?: RuntimeLineageTotals;
  prNumber?: number;
  headRepository?: string;
  pullRequestDiffSnapshot?: PullRequestDiffSnapshotV1;
  manifestPath: string;
}

export interface RuntimeResult {
  sessionId: string;
  sessionName: string;
  /** Legacy bridge payload; deterministic C# leaves it absent and uses typed content. */
  modelReviewJson?: string;
  debugFiles: string[];
  toolMode: ToolMode;
  allowedTools: string[];
  observedTurns: number | null;
  observedTurnSource: 'unique_assistant_message_ids' | 'not_applicable' | 'unavailable';
  usage: RuntimeUsage | null;
  usageBudgetStatus: UsageBudgetStatus;
  lineageTotals: RuntimeLineageTotals;
  reviewInputSha256?: string;
  reviewInputBytes?: number;
  runtimeVersion?: string;
  traceSha256?: string;
  diagnosticSummary?: string;
}

export interface RuntimeUsage {
  inputTokens: number;
  cacheReadInputTokens: number;
  cacheCreationInputTokens: number;
  outputTokens: number;
  recordsObserved: number;
}

export interface RuntimeUsageTotals {
  inputTokens: number;
  cacheReadInputTokens: number;
  cacheCreationInputTokens: number;
  outputTokens: number;
}

export interface RuntimeLineageTotals {
  observedTurns: number | null;
  usage: RuntimeUsageTotals;
  source:
    | 'current_run_only'
    | 'restored_manifest_plus_current_run'
    | 'restored_manifest_preserved_for_skipped'
    | 'legacy_manifest_fallback'
    | 'unavailable';
  partial: boolean;
}

export interface UploadedArtifact {
  name: string;
  id?: number;
  url?: string;
  retentionDays: number;
}

export type LineageReason =
  | 'manual_bootstrap'
  | 'auto_bootstrap_no_state'
  | 'auto_bootstrap_invalid'
  | 'auto_bootstrap_runtime'
  | 'compare_unavailable'
  | 'compare_diverged'
  | 'continuity_mismatch'
  | 'snapshot_state_missing'
  | 'snapshot_state_incompatible';

export type LineageAction = 'create' | 'update' | 'update_in_place';
