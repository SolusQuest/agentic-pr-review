import path from 'node:path';
import * as core from '@actions/core';
import * as github from '@actions/github';
import { GitHubArtifactStore, LocalArtifactStore, type ArtifactStore } from './artifacts.js';
import { parseActionConfig, type InputReader } from './config.js';
import { loadContextBlocks } from './context-blocks.js';
import { upsertLineageComment } from './comments.js';
import { buildReviewPrompt } from './prompt.js';
import { createRuntime, defaultTempDir, restoreRuntimeState } from './runtime.js';
import {
  debugArtifactName,
  readRestoredState,
  stateArtifactName,
  writeStateBundle,
} from './state.js';
import { deriveStateKey, fetchTargetCompare, resolveTarget } from './target.js';
import {
  type ActionConfig,
  type LineageReason,
  type Phase,
  type RestoredState,
  type RuntimeResult,
  type UploadedArtifact,
} from './types.js';
import { ensureDir, sanitizeStateKey, walkFiles, writeTextFile } from './utils.js';

type ReviewPhaseOutput = Phase | 'skipped-identical';

interface PhaseResolution {
  phase: Phase;
  restoredState?: RestoredState;
  restoreDir?: string;
  lineageReason: LineageReason;
  restoredArtifact?: { id: number; workflowRunId: number };
}

class CoreInputReader implements InputReader {
  getInput(name: string): string {
    return core.getInput(name);
  }
}

export async function run(): Promise<void> {
  const eventName = github.context.eventName || process.env.GITHUB_EVENT_NAME || '';
  const config = parseActionConfig(new CoreInputReader(), process.env, eventName);
  if (config.apiKey) {
    core.setSecret(config.apiKey);
  }
  core.setSecret(config.githubToken);

  const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
  const tempRoot = path.join(defaultTempDir(), 'agentic-pr-review');
  const runtimeDir = path.join(tempRoot, 'runtime', config.runtimeProvider);
  await ensureDir(tempRoot);

  const octokit = github.getOctokit(config.githubToken);
  const target = await resolveTarget(config, octokit, github.context);
  validateSameRepositoryTarget(target);

  const stateKey = sanitizeStateKey(deriveStateKey(config, target));
  const artifactName = stateArtifactName(stateKey);
  const store = createArtifactStore(config.githubToken, octokit);
  let resolution = await resolvePhase(config, store, artifactName, tempRoot, stateKey);
  let compare =
    resolution.phase === 'incremental' && target.mode === 'pull-request'
      ? await fetchTargetCompare(
          octokit,
          github.context,
          resolution.restoredState?.reviewedHeadSha ?? target.baseSha,
          target.headSha,
          config.maxPatchChars,
        )
      : undefined;

  if (resolution.phase === 'incremental' && target.mode === 'pull-request') {
    if (!compare) {
      if (config.reviewMode === 'auto') {
        resolution = {
          phase: 'bootstrap',
          lineageReason: 'compare_unavailable',
        };
      } else {
        throw new Error(
          'Unable to compare prior reviewed head to current head. Use review_mode=bootstrap or review_mode=auto to recover.',
        );
      }
    } else if (compare.status === 'identical') {
      await restoreRuntimeState(resolution.restoreDir, config.runtimeProvider, runtimeDir);
      await finishSkippedIdentical({
        config,
        store,
        target,
        stateKey,
        artifactName,
        runtimeDir,
        restoredState: requireRestoredState(resolution.restoredState),
      });
      return;
    } else if (compare.status === 'diverged' || compare.status === 'behind') {
      if (config.reviewMode === 'auto') {
        resolution = {
          phase: 'bootstrap',
          lineageReason: 'compare_diverged',
        };
        compare = undefined;
      } else {
        throw new Error(
          `Compare status is "${compare.status}". Use review_mode=bootstrap or review_mode=auto to recover.`,
        );
      }
    }
  }

  await restoreRuntimeState(
    resolution.restoredState ? resolution.restoreDir : undefined,
    config.runtimeProvider,
    runtimeDir,
  );

  const blocks = await loadContextBlocks(config, workspace, resolution.phase);
  const prompt = buildReviewPrompt(
    target,
    resolution.phase,
    blocks,
    config.maxPatchChars,
    compare,
    resolution.restoredState?.reviewedHeadSha,
  );
  const runtime = createRuntime(config.runtimeProvider);
  const runtimeResult = await runtime.run({
    config,
    phase: resolution.phase,
    stateKey,
    prompt: prompt.text,
    promptHash: prompt.sha256,
    restoredState: resolution.restoredState,
    workspace,
    tempDir: tempRoot,
    runtimeDir,
  });

  const reviewMarkdownPath = path.join(tempRoot, 'review.md');
  await writeTextFile(reviewMarkdownPath, runtimeResult.reviewMarkdown);

  const bundleDir = path.join(tempRoot, 'state-bundle');
  const bundleFiles = await writeStateBundle({
    bundleDir,
    config,
    target,
    stateKey,
    phase: resolution.phase,
    promptSha256: prompt.sha256,
    blocks,
    runtimeResult,
    runtimeDir,
    createdAt: resolution.restoredState?.createdAt,
  });

  const uploadedState = await store.upload(
    artifactName,
    bundleDir,
    bundleFiles,
    config.artifactRetentionDays,
  );
  let debugArtifact: UploadedArtifact | undefined;
  if (config.debugCaptureRawApiBodies) {
    debugArtifact = await uploadDebugArtifact(store, stateKey, runtimeResult.debugFiles);
  }

  const comment = await maybePostComment({
    config,
    target,
    runtimeResult,
    stateKey,
    phase: resolution.phase,
    artifactName,
    lineageReason: resolution.lineageReason,
    previousHeadSha: resolution.restoredState?.reviewedHeadSha,
    reviewMarkdown: runtimeResult.reviewMarkdown,
    octokit,
  });

  setOutputs({
    stateKey,
    reviewMode: config.reviewMode,
    phase: resolution.phase,
    reviewPhase: resolution.phase,
    runtimeProvider: config.runtimeProvider,
    sessionId: runtimeResult.sessionId,
    reviewedHeadSha: target.headSha,
    artifact: uploadedState,
    reviewMarkdownPath,
    commentUrl: comment.commentUrl,
    lineageAction: comment.lineageAction,
    lineageReason: comment.lineageReason,
    debugArtifact,
  });

  await writeSummary({
    config,
    target,
    stateKey,
    phase: resolution.phase,
    reviewPhase: resolution.phase,
    runtimeResult,
    promptSha256: prompt.sha256,
    promptBytes: Buffer.byteLength(prompt.text, 'utf8'),
    restored: resolution,
    compareUrl: compare?.htmlUrl,
    artifactName,
    commentUrl: comment.commentUrl,
    bundleDir,
  });
}

function createArtifactStore(token: string, octokit: any): ArtifactStore {
  const localRoot = process.env.AGENTIC_REVIEW_LOCAL_ARTIFACT_DIR;
  if (localRoot) {
    return new LocalArtifactStore(localRoot);
  }
  return new GitHubArtifactStore(
    octokit,
    token,
    github.context.repo.owner,
    github.context.repo.repo,
    github.context.runId,
  );
}

async function resolvePhase(
  config: ActionConfig,
  store: ArtifactStore,
  artifactName: string,
  tempRoot: string,
  stateKey: string,
): Promise<PhaseResolution> {
  if (config.reviewMode === 'bootstrap') {
    return { phase: 'bootstrap', lineageReason: 'manual_bootstrap' };
  }

  const artifact = await store.findStateArtifact(artifactName, config.stateArtifactRunId);
  if (!artifact) {
    if (config.reviewMode === 'incremental') {
      throw new Error(`review_mode=incremental requires a state artifact named ${artifactName}`);
    }
    return { phase: 'bootstrap', lineageReason: 'auto_bootstrap_no_state' };
  }

  const restoreDir = path.join(tempRoot, 'restored-state');
  await store.download(artifact, restoreDir);

  let restoredState: RestoredState | undefined;
  try {
    restoredState = await readRestoredState(restoreDir);
    validateRestoredState(restoredState, stateKey, config.runtimeProvider);
  } catch (error) {
    if (config.reviewMode === 'auto') {
      const reason: LineageReason = messageOf(error).includes('runtime_provider')
        ? 'auto_bootstrap_runtime'
        : 'auto_bootstrap_invalid';
      core.warning(
        `Restored state artifact is not usable; falling back to bootstrap: ${messageOf(error)}`,
      );
      return { phase: 'bootstrap', lineageReason: reason };
    }
    throw error;
  }

  return {
    phase: 'incremental',
    restoredState,
    restoreDir,
    lineageReason: 'continuity_mismatch',
    restoredArtifact: {
      id: artifact.id,
      workflowRunId: artifact.workflowRunId,
    },
  };
}

function validateRestoredState(
  restoredState: RestoredState,
  stateKey: string,
  runtimeProvider: string,
): void {
  if (restoredState.stateKey !== stateKey) {
    throw new Error('restored state artifact state_key does not match the requested state_key');
  }
  if (restoredState.runtimeProvider !== runtimeProvider) {
    throw new Error(
      'restored state artifact runtime_provider does not match the requested runtime_provider',
    );
  }
  if (!restoredState.sessionId) {
    throw new Error('restored state artifact is missing session_id');
  }
}

function validateSameRepositoryTarget(target: Awaited<ReturnType<typeof resolveTarget>>): void {
  if (target.mode !== 'pull-request' || !target.headRepoFullName) {
    return;
  }
  const expected = `${github.context.repo.owner}/${github.context.repo.repo}`;
  if (target.headRepoFullName !== expected) {
    throw new Error(
      `Fork pull requests are not supported. Expected head repository ${expected}, got ${target.headRepoFullName}.`,
    );
  }
}

async function finishSkippedIdentical(options: {
  config: ActionConfig;
  store: ArtifactStore;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  stateKey: string;
  artifactName: string;
  runtimeDir: string;
  restoredState: RestoredState;
}): Promise<void> {
  const runtimeResult: RuntimeResult = {
    sessionId: options.restoredState.sessionId,
    sessionName: options.restoredState.sessionName,
    reviewMarkdown: `No changes since prior review for ${options.target.headSha}. Provider call skipped.`,
    debugFiles: [],
    usage: options.restoredState.usage,
  };
  const bundleDir = path.join(defaultTempDir(), 'agentic-pr-review', 'state-bundle');
  const bundleFiles = await writeStateBundle({
    bundleDir,
    config: options.config,
    target: options.target,
    stateKey: options.stateKey,
    phase: 'incremental',
    promptSha256: 'skipped-identical',
    blocks: [],
    runtimeResult,
    runtimeDir: options.runtimeDir,
    createdAt: options.restoredState.createdAt,
  });
  const uploadedState = await options.store.upload(
    options.artifactName,
    bundleDir,
    bundleFiles,
    options.config.artifactRetentionDays,
  );
  setOutputs({
    stateKey: options.stateKey,
    reviewMode: options.config.reviewMode,
    phase: 'incremental',
    reviewPhase: 'skipped-identical',
    runtimeProvider: options.config.runtimeProvider,
    sessionId: runtimeResult.sessionId,
    reviewedHeadSha: options.target.headSha,
    artifact: uploadedState,
    reviewMarkdownPath: path.join(bundleDir, 'review.md'),
    commentUrl: 'skipped-identical',
    lineageAction: '',
    lineageReason: '',
  });
  await writeSummary({
    config: options.config,
    target: options.target,
    stateKey: options.stateKey,
    phase: 'incremental',
    reviewPhase: 'skipped-identical',
    runtimeResult,
    promptSha256: 'skipped-identical',
    promptBytes: 0,
    restored: {
      phase: 'incremental',
      restoredState: options.restoredState,
      lineageReason: 'continuity_mismatch',
    },
    compareUrl: undefined,
    artifactName: options.artifactName,
    commentUrl: 'skipped-identical',
    bundleDir,
  });
}

function requireRestoredState(restoredState: RestoredState | undefined): RestoredState {
  if (!restoredState) {
    throw new Error('internal error: identical incremental requires restored state');
  }
  return restoredState;
}

async function maybePostComment(options: {
  config: ActionConfig;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  runtimeResult: RuntimeResult;
  stateKey: string;
  phase: Phase;
  artifactName: string;
  lineageReason: LineageReason;
  previousHeadSha?: string;
  reviewMarkdown: string;
  octokit: any;
}): Promise<{ commentUrl: string; lineageAction: string; lineageReason: string }> {
  if (!options.config.postComment) {
    return { commentUrl: '', lineageAction: '', lineageReason: '' };
  }
  if (options.target.mode !== 'pull-request' || !options.target.prNumber) {
    throw new Error('post_comment=true requires target_mode=pull-request');
  }

  try {
    const comment = await upsertLineageComment({
      octokit: options.octokit,
      owner: github.context.repo.owner,
      repo: github.context.repo.repo,
      prNumber: options.target.prNumber,
      target: options.target,
      reviewMarkdown: options.reviewMarkdown,
      stateKey: options.stateKey,
      phase: options.phase,
      runtimeProvider: options.config.runtimeProvider,
      sessionId: options.runtimeResult.sessionId,
      previousHeadSha: options.previousHeadSha,
      currentHeadSha: options.target.headSha,
      artifactName: options.artifactName,
      runId: github.context.runId,
      runAttempt: github.context.runAttempt,
      lineageReason: options.lineageReason,
      usage: options.runtimeResult.usage,
      maxReviewChars: options.config.maxReviewChars,
    });
    return {
      commentUrl: comment.commentUrl,
      lineageAction: comment.lineageAction,
      lineageReason: comment.lineageReason,
    };
  } catch (error) {
    const message = messageOf(error);
    core.warning(`sticky comment update failed after state artifact upload: ${message}`);
    return {
      commentUrl: `failed: ${message}`,
      lineageAction: 'failed',
      lineageReason: options.lineageReason,
    };
  }
}

async function uploadDebugArtifact(
  store: ArtifactStore,
  stateKey: string,
  debugFiles: string[],
): Promise<UploadedArtifact> {
  if (debugFiles.length === 0) {
    throw new Error('debug_capture_raw_api_bodies was enabled but no debug files were produced');
  }
  const root = path.dirname(debugFiles[0]);
  const files = await walkFiles(root);
  return await store.upload(debugArtifactName(stateKey), root, files, 1);
}

function setOutputs(options: {
  stateKey: string;
  reviewMode: string;
  phase: Phase;
  reviewPhase: ReviewPhaseOutput;
  runtimeProvider: string;
  sessionId: string;
  reviewedHeadSha: string;
  artifact: UploadedArtifact;
  reviewMarkdownPath: string;
  commentUrl: string;
  lineageAction: string;
  lineageReason: string;
  debugArtifact?: UploadedArtifact;
}): void {
  core.setOutput('state_key', options.stateKey);
  core.setOutput('review_mode', options.reviewMode);
  core.setOutput('phase', options.phase);
  core.setOutput('review_phase', options.reviewPhase);
  core.setOutput('runtime_provider', options.runtimeProvider);
  core.setOutput('session_id', options.sessionId);
  core.setOutput('reviewed_head_sha', options.reviewedHeadSha);
  core.setOutput('artifact_name', options.artifact.name);
  core.setOutput('artifact_id', options.artifact.id ?? '');
  core.setOutput('artifact_url', options.artifact.url ?? '');
  core.setOutput('artifact_retention_days', String(options.artifact.retentionDays));
  core.setOutput('review_markdown_path', options.reviewMarkdownPath);
  core.setOutput('comment_url', options.commentUrl);
  core.setOutput('lineage_action', options.lineageAction);
  core.setOutput('lineage_reason', options.lineageReason);
  if (options.debugArtifact) {
    core.setOutput('debug_artifact_name', options.debugArtifact.name);
    core.setOutput('debug_artifact_id', options.debugArtifact.id ?? '');
    core.setOutput('debug_artifact_url', options.debugArtifact.url ?? '');
  }
}

async function writeSummary(input: {
  config: ActionConfig;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  stateKey: string;
  phase: Phase;
  reviewPhase: ReviewPhaseOutput;
  runtimeResult: RuntimeResult;
  promptSha256: string;
  promptBytes: number;
  restored: PhaseResolution;
  compareUrl?: string;
  artifactName: string;
  commentUrl: string;
  bundleDir: string;
}): Promise<void> {
  const restored = input.restored.restoredState
    ? `yes, head ${input.restored.restoredState.reviewedHeadSha ?? 'unknown'}`
    : 'no';
  const usage = input.runtimeResult.usage
    ? [
        `cache_read=${input.runtimeResult.usage.cacheReadInputTokens ?? input.runtimeResult.usage.promptCacheHitTokens ?? 'n/a'}`,
        `input=${input.runtimeResult.usage.inputTokens ?? 'n/a'}`,
        `output=${input.runtimeResult.usage.outputTokens ?? 'n/a'}`,
      ].join(', ')
    : 'not exposed';
  const lines = [
    '### Agentic PR Review',
    '',
    `- Requested mode: ${input.config.reviewMode}`,
    `- Resolved phase: ${input.phase}`,
    `- Review phase: ${input.reviewPhase}`,
    `- Runtime: ${input.config.runtimeProvider}`,
    `- State key: ${input.stateKey}`,
    `- Session id: ${input.runtimeResult.sessionId}`,
    `- Restored previous state: ${restored}`,
    `- Previous reviewed head: ${input.restored.restoredState?.reviewedHeadSha ?? 'n/a'}`,
    `- Current head: ${input.target.headSha}`,
    `- Prompt sha256: ${input.promptSha256}`,
    `- Prompt bytes: ${input.promptBytes}`,
    `- Usage: ${usage}`,
    `- Compare URL: ${input.compareUrl ?? 'n/a'}`,
    `- Sticky comment: ${input.commentUrl || 'not requested'}`,
    `- State artifact: ${input.artifactName}`,
    `- Artifact retention days: ${input.config.artifactRetentionDays}`,
    `- Local bundle path: ${input.bundleDir}`,
  ];
  try {
    await core.summary.addRaw(lines.join('\n')).write();
  } catch (error) {
    core.info(`Unable to write job summary: ${messageOf(error)}`);
  }
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

run().catch((error: unknown) => {
  core.setFailed(messageOf(error));
});
