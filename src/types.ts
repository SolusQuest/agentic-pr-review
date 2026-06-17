export type RuntimeProvider = 'test' | 'claude-code-cli';
export type TargetMode = 'pull-request' | 'synthetic-fixture';
export type ReviewMode = 'auto' | 'bootstrap' | 'incremental';
export type Phase = 'bootstrap' | 'incremental';
export type ApiKeyMode = 'auth-token' | 'api-key' | 'both';
export type ToolMode = 'none' | 'readonly';

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
  runtimeProvider: RuntimeProvider;
  targetMode: TargetMode;
  reviewMode: ReviewMode;
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
  usageBudgetLimits: UsageBudgetLimits;
  disablePromptCaching: boolean;
  debugCaptureRawApiBodies: boolean;
  debugAcknowledgement?: string;
  githubToken: string;
  apiKey?: string;
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
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  patch?: string;
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
  changedFiles: ChangedFile[];
  htmlUrl?: string;
}

export interface RestoredState {
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
  manifestPath: string;
}

export interface RuntimeResult {
  sessionId: string;
  sessionName: string;
  reviewMarkdown: string;
  debugFiles: string[];
  toolMode: ToolMode;
  allowedTools: string[];
  observedTurns: number | null;
  observedTurnSource: 'unique_assistant_message_ids' | 'not_applicable' | 'unavailable';
  usage: RuntimeUsage | null;
  usageBudgetStatus: UsageBudgetStatus;
  lineageTotals: RuntimeLineageTotals;
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

export interface PullRequestCompare {
  baseSha: string;
  headSha: string;
  htmlUrl: string;
  status: string;
  aheadBy: number;
  behindBy: number;
  changedFiles: ChangedFile[];
}

export type LineageReason =
  | 'manual_bootstrap'
  | 'auto_bootstrap_no_state'
  | 'auto_bootstrap_invalid'
  | 'auto_bootstrap_runtime'
  | 'compare_unavailable'
  | 'compare_diverged'
  | 'continuity_mismatch';

export type LineageAction = 'create' | 'update' | 'update_in_place';
