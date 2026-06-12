import path from 'node:path';
import * as core from '@actions/core';
import * as github from '@actions/github';
import { GitHubArtifactStore, LocalArtifactStore, type ArtifactStore } from './artifacts.js';
import { parseActionConfig, type InputReader } from './config.js';
import { loadContextBlocks } from './context-blocks.js';
import { upsertStickyComment } from './comments.js';
import { buildReviewPrompt } from './prompt.js';
import { createRuntime, defaultTempDir } from './runtime.js';
import {
  debugArtifactName,
  readRestoredState,
  stateArtifactName,
  writeStateBundle,
} from './state.js';
import { deriveStateKey, resolveTarget } from './target.js';
import { type Phase, type RestoredState, type UploadedArtifact } from './types.js';
import { ensureDir, sanitizeStateKey, walkFiles, writeTextFile } from './utils.js';

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

  const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
  const tempRoot = path.join(defaultTempDir(), 'agentic-pr-review');
  await ensureDir(tempRoot);

  const octokit = github.getOctokit(config.githubToken);
  const target = await resolveTarget(config, octokit, github.context);
  const stateKey = sanitizeStateKey(deriveStateKey(config, target));
  const artifactName = stateArtifactName(stateKey);
  const store = createArtifactStore(config.githubToken, octokit);

  const restoredState = await restoreStateIfAvailable(
    store,
    artifactName,
    config.stateArtifactRunId,
    tempRoot,
  );
  if (restoredState) {
    validateRestoredState(restoredState, stateKey, config.runtimeProvider);
  }
  const phase = decidePhase(config.reviewMode, restoredState);
  const blocks = await loadContextBlocks(config, workspace, phase);
  const prompt = buildReviewPrompt(target, phase, blocks, config.maxPatchChars);
  const runtime = createRuntime(config.runtimeProvider);
  const runtimeResult = await runtime.run({
    config,
    phase,
    stateKey,
    prompt: prompt.text,
    promptHash: prompt.sha256,
    restoredState,
    workspace,
    tempDir: tempRoot,
  });

  const reviewMarkdownPath = path.join(tempRoot, 'review.md');
  await writeTextFile(reviewMarkdownPath, runtimeResult.reviewMarkdown);

  const bundleDir = path.join(tempRoot, 'state-bundle');
  const bundleFiles = await writeStateBundle({
    bundleDir,
    config,
    target,
    stateKey,
    phase,
    promptSha256: prompt.sha256,
    blocks,
    runtimeResult,
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

  let commentUrl = '';
  let lineageAction = '';
  let lineageReason = '';
  if (config.postComment) {
    if (target.mode !== 'pull-request' || !target.prNumber) {
      throw new Error('post_comment=true requires target_mode=pull-request');
    }
    const comment = await upsertStickyComment({
      octokit,
      owner: github.context.repo.owner,
      repo: github.context.repo.repo,
      prNumber: target.prNumber,
      target,
      reviewMarkdown: runtimeResult.reviewMarkdown,
      stateKey,
    });
    commentUrl = comment.commentUrl;
    lineageAction = comment.lineageAction;
    lineageReason = comment.lineageReason;
  }

  setOutputs({
    stateKey,
    reviewMode: config.reviewMode,
    phase,
    runtimeProvider: config.runtimeProvider,
    sessionId: runtimeResult.sessionId,
    reviewedHeadSha: target.headSha,
    artifact: uploadedState,
    reviewMarkdownPath,
    commentUrl,
    lineageAction,
    lineageReason,
    debugArtifact,
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

async function restoreStateIfAvailable(
  store: ArtifactStore,
  artifactName: string,
  explicitRunId: number | undefined,
  tempRoot: string,
): Promise<RestoredState | undefined> {
  const artifact = await store.findStateArtifact(artifactName, explicitRunId);
  if (!artifact) {
    return undefined;
  }
  const restoreDir = path.join(tempRoot, 'restored-state');
  await store.download(artifact, restoreDir);
  return await readRestoredState(restoreDir);
}

function decidePhase(reviewMode: string, restoredState: RestoredState | undefined): Phase {
  if (reviewMode === 'bootstrap') {
    return 'bootstrap';
  }
  if (reviewMode === 'incremental') {
    if (!restoredState) {
      throw new Error('review_mode=incremental requires a valid restored state artifact');
    }
    return 'incremental';
  }
  return restoredState ? 'incremental' : 'bootstrap';
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

run().catch((error: unknown) => {
  core.setFailed(error instanceof Error ? error : String(error));
});
