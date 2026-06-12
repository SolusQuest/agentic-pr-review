export type RuntimeProvider = 'test' | 'claude-code-cli';
export type TargetMode = 'pull-request' | 'synthetic-fixture';
export type ReviewMode = 'auto' | 'bootstrap' | 'incremental';
export type Phase = 'bootstrap' | 'incremental';
export type ApiKeyMode = 'auth-token' | 'api-key' | 'both';

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
  instructions?: string;
  instructionsPath?: string;
  bootstrapContext?: string;
  bootstrapContextPath?: string;
  incrementalContext?: string;
  incrementalContextPath?: string;
  maxContextChars: number;
  maxPatchChars: number;
  maxReviewChars: number;
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
  patch?: string;
}

export interface ReviewTarget {
  mode: TargetMode;
  prNumber?: number;
  title: string;
  baseSha: string;
  headSha: string;
  changedFiles: ChangedFile[];
  htmlUrl?: string;
}

export interface RestoredState {
  stateKey: string;
  sessionId: string;
  runtimeProvider: RuntimeProvider;
  reviewedHeadSha?: string;
  manifestPath: string;
}

export interface RuntimeResult {
  sessionId: string;
  reviewMarkdown: string;
  debugFiles: string[];
}

export interface UploadedArtifact {
  name: string;
  id?: number;
  url?: string;
  retentionDays: number;
}
