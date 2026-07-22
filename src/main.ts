import path from 'node:path';
import { pathToFileURL } from 'node:url';
import * as core from '@actions/core';
import * as github from '@actions/github';
import { GitHubArtifactStore, LocalArtifactStore, type ArtifactStore } from './artifacts.js';
import { parseActionConfig, type InputReader } from './config.js';
import { loadContextBlocks } from './context-blocks.js';
import {
  capStructuredReviewForMarkdownLimit,
  renderStructuredReviewMarkdown,
  upsertLineageComment,
} from './comments.js';
import { buildReviewPrompt } from './prompt.js';
import {
  allowedToolsForMode,
  computeLineageTotals,
  createRuntime,
  defaultTempDir,
  preserveLineageTotalsForSkipped,
  restoreRuntimeState,
} from './runtime.js';
import {
  debugArtifactName,
  deterministicStateArtifactName,
  deterministicStateKey,
  readRestoredState,
  RestoredSnapshotInvalidError,
  StateManifestInvalidError,
  stateArtifactName,
  writeStateBundle,
} from './state.js';
import {
  assembleStructuredReviewFromRuntimeContent,
  buildReviewedRange,
  normalizeStructuredReview,
  StructuredReviewValidationError,
  type StructuredResultMetadata,
} from './structured.js';
import { buildReviewInputV1 } from './protocol/build-review-input.js';
import { mapReviewResultV1ToRuntimeContent } from './protocol/map-review-result.js';
import { validateReviewInputV1 } from './protocol/review-input.js';
import { invokeRuntime } from './runtime-invocation/invoke-runtime.js';
import { resolveTrustedRuntimeCommand } from './runtime-invocation/command-resolver.js';
import { LedgerRunFailure, runLedgerCsharp, type LedgerRunResult } from './ledger-csharp.js';
import { RuntimeInvocationError } from './runtime-invocation/runtime-errors.js';
import {
  deriveStateKey,
  diffPullRequestDiffSnapshots,
  pullRequestDiffSnapshotsEquivalent,
  resolveTarget,
} from './target.js';
import {
  type ActionConfig,
  type EffectiveDiffSource,
  type InlineCommentsMetadata,
  type InlineCommentsPolicy,
  type LineageReason,
  type Phase,
  type PullRequestDiffSnapshotDeltaV1,
  type PullRequestDiffSnapshotV1,
  type RestoredState,
  type RuntimeResult,
  type StructuredReviewEnvelopeV1,
  type UploadedArtifact,
} from './types.js';
import type { RuntimeInvocationSuccess } from './runtime-invocation/runtime-command.js';
import type { ReviewTraceV1 } from './protocol/review-trace.js';
import {
  ensureDir,
  sanitizeStateKey,
  truncateText,
  walkFiles,
  writeJsonFile,
  writeTextFile,
  sha256,
} from './utils.js';
import { defaultInlineCommentsMetadata, postInlineComments } from './inline-comments.js';

type ReviewPhaseOutput = Phase | 'skipped-identical';

interface PhaseResolution {
  phase: Phase;
  restoredState?: RestoredState;
  restoreDir?: string;
  lineageReason: LineageReason;
  restoredArtifact?: { id: number; workflowRunId: number };
}

class SnapshotStateCompatibilityError extends Error {}
class StateArtifactInvalidError extends Error {}
class StateRuntimeCompatibilityError extends Error {}

class CoreInputReader implements InputReader {
  getInput(name: string): string {
    return core.getInput(name);
  }
}

export async function run(): Promise<void> {
  const eventName = github.context.eventName || process.env.GITHUB_EVENT_NAME || '';
  const ledgerRequested = core.getInput('runtime_backend').trim() === 'ledger-csharp';
  if (ledgerRequested) setLedgerInitialOutputs();
  let config;
  try {
    config = parseActionConfig(new CoreInputReader(), process.env, eventName);
  } catch (error) {
    if (ledgerRequested) setLedgerErrorOutputs(error);
    throw error;
  }
  if (config.runtimeBackend === 'ledger-csharp' && !ledgerRequested) setLedgerInitialOutputs();
  if (config.apiKey) {
    core.setSecret(config.apiKey);
  }
  core.setSecret(config.githubToken);

  const workspace = process.env.GITHUB_WORKSPACE || process.cwd();
  const tempRoot = path.join(defaultTempDir(), 'agentic-pr-review');
  const runtimeDir = path.join(tempRoot, 'runtime', config.runtimeProvider);
  try {
    await ensureDir(tempRoot);
  } catch (error) {
    if (
      (config.runtimeBackend ?? 'legacy') === 'deterministic-csharp' ||
      config.runtimeBackend === 'ledger-csharp'
    ) {
      if (config.runtimeBackend === 'ledger-csharp') {
        setLedgerErrorOutputs(new Error('state-invalid: temporary directory unavailable'));
      } else {
        setDeterministicErrorOutputs(new Error('state-invalid: temporary directory unavailable'));
      }
      throw new Error('state-invalid: temporary directory unavailable');
    }
    throw error;
  }

  const octokit = github.getOctokit(config.githubToken);
  let target: Awaited<ReturnType<typeof resolveTarget>>;
  try {
    target = await resolveTarget(config, octokit, github.context);
    validateSameRepositoryTarget(target);
  } catch (error) {
    if (
      (config.runtimeBackend ?? 'legacy') === 'deterministic-csharp' ||
      config.runtimeBackend === 'ledger-csharp'
    ) {
      if (config.runtimeBackend === 'ledger-csharp') {
        setLedgerErrorOutputs(new Error('input-invalid: target resolution failed'));
      } else {
        setDeterministicErrorOutputs(new Error('input-invalid: target resolution failed'));
      }
      throw new Error('input-invalid: target resolution failed');
    }
    throw error;
  }

  if ((config.runtimeBackend ?? 'legacy') === 'ledger-csharp') {
    try {
      const defaultBranchCommitSha = process.env.GITHUB_SHA?.trim();
      if (!defaultBranchCommitSha || !/^[a-f0-9]{40}$/i.test(defaultBranchCommitSha)) {
        throw new Error('state-invalid: default branch commit SHA is unavailable');
      }
      const result = await runLedgerCsharp({
        config,
        target,
        octokit,
        eventName,
        defaultBranchCommitSha,
      });
      setLedgerSuccessOutputs(result);
      await writeLedgerSummary(result);
      return;
    } catch (error) {
      if (error instanceof LedgerRunFailure) {
        setLedgerPartialFailureOutputs(error);
        await writeLedgerSummary(error.result, error.errorKind);
      } else {
        setLedgerErrorOutputs(error);
      }
      throw error;
    }
  }

  let stateKey: string;
  let artifactName: string;
  try {
    const baseStateKey = deriveStateKey(config, target);
    stateKey =
      (config.runtimeBackend ?? 'legacy') === 'deterministic-csharp'
        ? deterministicStateKey(baseStateKey)
        : sanitizeStateKey(baseStateKey);
    artifactName =
      (config.runtimeBackend ?? 'legacy') === 'deterministic-csharp'
        ? deterministicStateArtifactName(stateKey)
        : stateArtifactName(stateKey);
  } catch (error) {
    if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(
        new Error('state-invalid: state identity could not be resolved'),
      );
      throw new Error('state-invalid: state identity could not be resolved');
    }
    throw error;
  }
  const store = createArtifactStore(config.githubToken, octokit, target, config.runtimeBackend);
  let resolution: PhaseResolution;
  try {
    resolution = await resolvePhase(config, store, artifactName, tempRoot, stateKey, target);
  } catch (error) {
    if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(error);
    }
    throw error;
  }
  let incrementalDiff: PullRequestDiffSnapshotDeltaV1 | undefined;
  const effectiveDiffSource = effectiveDiffSourceFor(target, resolution.phase);

  if (resolution.phase === 'incremental' && target.mode === 'pull-request') {
    let snapshotsEquivalent = false;
    try {
      const previousSnapshot = requireRestoredState(
        resolution.restoredState,
      ).pullRequestDiffSnapshot;
      const currentSnapshot = requirePullRequestDiffSnapshot(target);
      if (!previousSnapshot) {
        throw new Error('missing restored pull-request diff snapshot');
      }
      incrementalDiff = diffPullRequestDiffSnapshots(
        previousSnapshot,
        currentSnapshot,
        target.changedFiles,
      );
      snapshotsEquivalent = pullRequestDiffSnapshotsEquivalent(previousSnapshot, currentSnapshot);
    } catch (error) {
      if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
        setDeterministicErrorOutputs(new Error(`state-invalid: incremental snapshot failed`));
        throw new Error('state-invalid: incremental pull-request snapshot is unusable');
      }
      throw error;
    }
    if (snapshotsEquivalent) {
      if ((config.runtimeBackend ?? 'legacy') === 'legacy') {
        await restoreRuntimeState(resolution.restoreDir, config.runtimeProvider, runtimeDir);
      }
      await finishSkippedIdentical({
        config,
        store,
        target,
        stateKey,
        artifactName,
        runtimeDir,
        restoredState: requireRestoredState(resolution.restoredState),
        phaseReason: resolution.lineageReason,
        effectiveDiffSource,
        octokit,
      });
      return;
    }
  }

  if ((config.runtimeBackend ?? 'legacy') === 'legacy') {
    await restoreRuntimeState(
      resolution.restoredState ? resolution.restoreDir : undefined,
      config.runtimeProvider,
      runtimeDir,
    );
  }

  let blocks: Awaited<ReturnType<typeof loadContextBlocks>>;
  try {
    blocks = await loadContextBlocks(config, workspace, resolution.phase);
  } catch (error) {
    if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(new Error('input-invalid: context loading failed'));
      throw new Error('input-invalid: context loading failed');
    }
    throw error;
  }
  const reviewedRange = buildReviewedRange({
    phase: resolution.phase,
    target,
    previousReviewedHeadSha: resolution.restoredState?.reviewedHeadSha,
  });
  let structuredReview: StructuredReviewEnvelopeV1;
  let structuredMetadata: StructuredResultMetadata;
  let runtimeResult: RuntimeResult;
  let promptSha256: string | undefined;
  let promptBytes: number | undefined;
  try {
    if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      const input = buildReviewInputV1({
        target,
        config: { ...config, stateKey },
        phase: resolution.phase,
        blocks,
        restoredState: resolution.restoredState ?? null,
        previousFindingFingerprints: [],
        existingCommentFingerprints: [],
        repository: { owner: github.context.repo.owner, name: github.context.repo.repo },
        requestedRuntimeVersion: null,
      });
      const inputValidation = validateReviewInputV1(input);
      if (!inputValidation.ok) {
        throw new Error(
          `input-invalid: ${truncateText((inputValidation.errors ?? []).join('; '), 240)}`,
        );
      }
      const inputBytes = Buffer.byteLength(`${JSON.stringify(input, null, 2)}\n`, 'utf8');
      const resolvedCommand = await resolveTrustedRuntimeCommand(process.env);
      const invocation = await invokeRuntime({
        command: resolvedCommand.command,
        input,
        timeoutMs: 30000,
        tempRoot: tempRoot,
      });
      if (!invocation.result.trace?.sha256) {
        throw new Error('trace-invalid: deterministic result must include trace.sha256');
      }
      validateDeterministicTrace(invocation.trace, invocation.inputSha256);
      for (const warning of boundedRuntimeMessages(invocation.result.warnings)) {
        core.warning(sanitizeRuntimeDiagnosticForHost(warning));
      }
      for (const warning of boundedRuntimeMessages(invocation.trace.warnings)) {
        core.warning(sanitizeRuntimeDiagnosticForHost(warning));
      }
      const errorDiagnostic = invocation.result.diagnostics.find(
        (diagnostic) => diagnostic.level === 'error',
      );
      if (errorDiagnostic) {
        throw new Error(
          `diagnostic-error: ${sanitizeRuntimeDiagnosticForHost(errorDiagnostic.code)}`,
        );
      }
      const diagnosticSummary = summarizeRuntimeDiagnostics([
        ...invocation.result.diagnostics,
        ...invocation.trace.diagnostics,
      ]);
      runtimeResult = buildDeterministicRuntimeResult(
        invocation,
        resolution,
        config,
        stateKey,
        inputBytes,
      );
      runtimeResult.diagnosticSummary = diagnosticSummary;
      let assembled: ReturnType<typeof assembleStructuredReviewFromRuntimeContent>;
      try {
        const projection = mapReviewResultV1ToRuntimeContent(invocation.result);
        assembled = assembleStructuredReviewFromRuntimeContent({
          content: projection.content,
          target,
          phase: resolution.phase,
          previousReviewedHeadSha: resolution.restoredState?.reviewedHeadSha,
          reviewedRange,
          config,
          sessionId: runtimeResult.sessionId,
          usage: runtimeResult.usage,
          observedTurns: runtimeResult.observedTurns,
          observedTurnSource: runtimeResult.observedTurnSource,
          lineageTotals: runtimeResult.lineageTotals,
          maxFindings: config.maxFindings,
        });
      } catch {
        throw new Error('mapping-invalid: typed runtime content could not be assembled');
      }
      structuredReview = capStructuredReviewForMarkdownLimit(
        assembled.envelope,
        config.maxReviewChars,
      );
      structuredMetadata = metadataForStructuredReview(structuredReview, assembled.metadata.status);
    } else {
      const prompt = buildReviewPrompt(
        target,
        resolution.phase,
        blocks,
        config.maxPatchChars,
        incrementalDiff,
        resolution.restoredState?.reviewedHeadSha,
      );
      promptSha256 = prompt.sha256;
      promptBytes = Buffer.byteLength(prompt.text, 'utf8');
      const runtime = createRuntime(config.runtimeProvider);
      runtimeResult = await runtime.run({
        config,
        phase: resolution.phase,
        stateKey,
        prompt: prompt.text,
        promptHash: prompt.sha256,
        restoredState: resolution.restoredState,
        workspace,
        tempDir: tempRoot,
        runtimeDir,
        target,
      });
      const normalized = normalizeStructuredReview({
        modelJsonText: runtimeResult.modelReviewJson ?? '',
        target,
        phase: resolution.phase,
        previousReviewedHeadSha: resolution.restoredState?.reviewedHeadSha,
        reviewedRange,
        config,
        sessionId: runtimeResult.sessionId,
        usage: runtimeResult.usage,
        observedTurns: runtimeResult.observedTurns,
        observedTurnSource: runtimeResult.observedTurnSource,
        lineageTotals: runtimeResult.lineageTotals,
        maxFindings: config.maxFindings,
      });
      structuredReview = capStructuredReviewForMarkdownLimit(
        normalized.envelope,
        config.maxReviewChars,
      );
      structuredMetadata = metadataForStructuredReview(
        structuredReview,
        normalized.metadata.status,
      );
    }
  } catch (error) {
    if ((config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(error);
      if (error instanceof RuntimeInvocationError) {
        throw new Error(`deterministic runtime failed: ${error.kind}`);
      }
    }
    handleStructuredValidationFailure(error);
  }

  const deterministicBackend = (config.runtimeBackend ?? 'legacy') === 'deterministic-csharp';
  let comment = { commentUrl: '', lineageAction: '', lineageReason: '' };
  let inlineMetadata: InlineCommentsMetadata;
  if (deterministicBackend) {
    inlineMetadata = defaultInlineCommentsMetadata(inlinePolicy(config));
  } else {
    comment = await maybePostComment({
      config,
      target,
      runtimeResult,
      stateKey,
      phase: resolution.phase,
      artifactName,
      lineageReason: resolution.lineageReason,
      previousHeadSha: resolution.restoredState?.reviewedHeadSha,
      structuredReview: structuredReview!,
      octokit,
    });
    inlineMetadata = await maybePostInlineComments({
      config,
      target,
      stateKey,
      structuredReview: structuredReview!,
      octokit,
      stickyCommentUrl: comment.commentUrl,
    });
  }
  structuredReview = {
    ...structuredReview!,
    inlineComments: inlineMetadata,
  };
  structuredMetadata = {
    ...structuredMetadata!,
    inlineComments: inlineMetadata,
  };

  const structuredResultPath = path.join(tempRoot, 'structured-result.json');
  const renderedReviewMarkdownPath = path.join(tempRoot, 'rendered-review.md');
  let renderedReviewMarkdown: string;
  try {
    renderedReviewMarkdown = renderStructuredReviewMarkdown(structuredReview);
    await writeJsonFile(structuredResultPath, structuredReview);
    await writeTextFile(renderedReviewMarkdownPath, renderedReviewMarkdown);
  } catch (error) {
    if (deterministicBackend) {
      setDeterministicErrorOutputs(new Error(`rendering-invalid: ${messageOf(error)}`));
      throw new Error('rendering-invalid: rendered review could not be written');
    }
    throw error;
  }

  const bundleDir = path.join(tempRoot, 'state-bundle');
  let bundleFiles: string[];
  try {
    bundleFiles = await writeStateBundle({
      bundleDir,
      config,
      target,
      stateKey,
      phase: resolution.phase,
      promptSha256,
      reviewInputSha256: runtimeResult.reviewInputSha256,
      reviewInputBytes: runtimeResult.reviewInputBytes,
      blocks,
      runtimeResult,
      structuredReview,
      structuredMetadata,
      renderedReviewMarkdown,
      runtimeDir,
      phaseReason: resolution.lineageReason,
      effectiveDiffSource,
      createdAt: resolution.restoredState?.createdAt,
    });
  } catch (error) {
    if (deterministicBackend) {
      setDeterministicErrorOutputs(new Error('state-invalid: state bundle construction failed'));
      throw new Error('state-invalid: state bundle construction failed');
    }
    throw error;
  }

  if (deterministicBackend) {
    try {
      await assertTargetHeadUnchanged(target, octokit);
    } catch {
      setDeterministicErrorOutputs(new Error('state-invalid: target head could not be confirmed'));
      throw new Error('state-invalid: target head could not be confirmed');
    }
  }
  if (deterministicBackend) {
    setDeterministicSuccessOutputs(runtimeResult);
  }
  if (deterministicBackend) {
    try {
      comment = await maybePostComment({
        config,
        target,
        runtimeResult,
        stateKey,
        phase: resolution.phase,
        artifactName,
        lineageReason: resolution.lineageReason,
        previousHeadSha: resolution.restoredState?.reviewedHeadSha,
        structuredReview: structuredReview!,
        octokit,
      });
    } catch {
      setDeterministicHostErrorKind('rendering-invalid');
      await writeDeterministicStickyFailureSummary({
        phase: resolution.phase,
        reviewPhase: resolution.phase,
        runtimeResult,
      });
      throw new Error('rendering-invalid: deterministic sticky comment could not be published');
    }
  }
  let uploadedState: UploadedArtifact;
  try {
    uploadedState = await store.upload(
      artifactName,
      bundleDir,
      bundleFiles,
      config.artifactRetentionDays,
    );
  } catch (error) {
    if (deterministicBackend) {
      await writeDeterministicUploadFailureSummary({
        phase: resolution.phase,
        reviewPhase: resolution.phase,
        runtimeResult,
        stickyWritten: Boolean(comment.commentUrl),
        artifactUploaded: false,
      });
      throw new Error('state-invalid: state artifact upload failed');
    }
    throw error;
  }
  let debugArtifact: UploadedArtifact | undefined;
  if (config.debugCaptureRawApiBodies) {
    debugArtifact = await uploadDebugArtifact(store, stateKey, runtimeResult.debugFiles);
  }

  setOutputs({
    stateKey,
    reviewMode: config.reviewMode,
    phase: resolution.phase,
    reviewPhase: resolution.phase,
    runtimeProvider: config.runtimeProvider,
    runtimeBackend: config.runtimeBackend ?? 'legacy',
    sessionId: runtimeResult.sessionId,
    reviewedHeadSha: target.headSha,
    artifact: uploadedState,
    structuredResultPath,
    renderedReviewMarkdownPath,
    commentUrl: comment.commentUrl,
    lineageAction: comment.lineageAction,
    lineageReason: comment.lineageReason,
    debugArtifact,
    runtimeResult,
    structuredMetadata: structuredMetadata!,
  });

  await writeSummary({
    config,
    target,
    stateKey,
    phase: resolution.phase,
    reviewPhase: resolution.phase,
    runtimeResult,
    promptSha256,
    promptBytes,
    restored: resolution,
    effectiveDiffSource,
    artifactName,
    commentUrl: comment.commentUrl,
    bundleDir,
    structuredMetadata: structuredMetadata!,
    structuredResultPath,
    renderedReviewMarkdownPath,
  });
}

function buildDeterministicRuntimeResult(
  invocation: RuntimeInvocationSuccess,
  resolution: PhaseResolution,
  config: ActionConfig,
  stateKey: string,
  inputBytes: number,
): RuntimeResult {
  const sessionId =
    resolution.phase === 'incremental' && resolution.restoredState
      ? resolution.restoredState.sessionId
      : `csharp-${sha256(stateKey).slice(0, 20)}`;
  return {
    sessionId,
    sessionName: `agentic-pr-review-${stateKey}`,
    debugFiles: [],
    toolMode: 'none',
    allowedTools: [],
    observedTurns: null,
    observedTurnSource: 'not_applicable',
    usage: null,
    usageBudgetStatus: {
      status: 'not_applicable',
      limits: config.usageBudgetLimits,
      usageRecordsObserved: 0,
    },
    lineageTotals: computeLineageTotals(resolution.restoredState, null, null),
    reviewInputSha256: invocation.inputSha256,
    reviewInputBytes: inputBytes,
    runtimeVersion: invocation.runtimeVersion,
    traceSha256: invocation.result.trace?.sha256,
  };
}

function validateDeterministicTrace(trace: ReviewTraceV1, inputSha256: string): void {
  if (trace.mode !== 'deterministic-fixture') {
    throw new Error('trace-invalid: trace.mode must be deterministic-fixture');
  }
  if (!trace.fixture || trace.fixture.length > 200) {
    throw new Error('trace-invalid: trace.fixture must be a bounded non-empty string');
  }
  if (trace.provider !== undefined || trace.usage !== undefined) {
    throw new Error('trace-invalid: deterministic trace cannot include provider or usage');
  }
  if (trace.inputSha256 !== inputSha256) {
    throw new Error('trace-invalid: trace inputSha256 does not match adapter inputSha256');
  }
  if (!isSafeRuntimeVersion(trace.runtimeVersion)) {
    throw new Error('trace-invalid: runtimeVersion is not bounded single-line metadata');
  }
  if (trace.toolCalls.length !== 0) {
    throw new Error('trace-invalid: deterministic trace cannot include tool calls');
  }
  if (trace.diagnostics.some((diagnostic) => diagnostic.level === 'error')) {
    throw new Error('diagnostic-error: deterministic trace contains an error diagnostic');
  }
}

function createArtifactStore(
  token: string,
  octokit: any,
  target: Awaited<ReturnType<typeof resolveTarget>>,
  runtimeBackend: ActionConfig['runtimeBackend'],
): ArtifactStore {
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
    {
      targetMode: target.mode,
      prNumber: target.prNumber,
      runtimeBackend: runtimeBackend === 'ledger-csharp' ? undefined : runtimeBackend,
    },
  );
}

async function resolvePhase(
  config: ActionConfig,
  store: ArtifactStore,
  artifactName: string,
  tempRoot: string,
  stateKey: string,
  target: Awaited<ReturnType<typeof resolveTarget>>,
): Promise<PhaseResolution> {
  if (config.reviewMode === 'bootstrap') {
    return { phase: 'bootstrap', lineageReason: 'manual_bootstrap' };
  }

  let artifact: Awaited<ReturnType<ArtifactStore['findStateArtifact']>>;
  try {
    artifact = await store.findStateArtifact(artifactName, config.stateArtifactRunId);
  } catch (error) {
    if (config.runtimeBackend === 'deterministic-csharp') {
      throw new Error('state-invalid: state artifact lookup failed');
    }
    throw error;
  }
  if (!artifact) {
    if (target.mode === 'pull-request') {
      return { phase: 'bootstrap', lineageReason: 'snapshot_state_missing' };
    }
    if (config.reviewMode === 'incremental') {
      throw new Error(
        `${config.runtimeBackend === 'deterministic-csharp' ? 'state-invalid: ' : ''}review_mode=incremental requires a state artifact named ${artifactName}`,
      );
    }
    return { phase: 'bootstrap', lineageReason: 'auto_bootstrap_no_state' };
  }

  const restoreDir = path.join(tempRoot, 'restored-state');
  try {
    await store.download(artifact, restoreDir);
  } catch (error) {
    if (config.runtimeBackend === 'deterministic-csharp') {
      throw new Error('state-invalid: restored state artifact could not be downloaded');
    }
    throw error;
  }

  let restoredState: RestoredState | undefined;
  try {
    restoredState = await readRestoredState(restoreDir);
    validateRestoredState(
      restoredState,
      stateKey,
      config.runtimeProvider,
      config.runtimeBackend ?? 'legacy',
      target,
      artifact.runHeadSha,
    );
  } catch (error) {
    if (error instanceof StateManifestInvalidError) {
      if (config.reviewMode === 'auto') {
        core.warning('Restored state manifest is invalid; falling back to bootstrap.');
        return { phase: 'bootstrap', lineageReason: 'auto_bootstrap_invalid' };
      }
      if (config.runtimeBackend === 'deterministic-csharp' && config.reviewMode === 'incremental') {
        throw new Error('state-invalid: restored state manifest is invalid');
      }
      throw error;
    }
    if (
      error instanceof SnapshotStateCompatibilityError ||
      error instanceof RestoredSnapshotInvalidError
    ) {
      core.warning(
        'Restored state artifact is not snapshot-compatible; falling back to bootstrap.',
      );
      return { phase: 'bootstrap', lineageReason: 'snapshot_state_incompatible' };
    }
    if (config.reviewMode === 'auto') {
      const reason: LineageReason =
        error instanceof StateRuntimeCompatibilityError
          ? 'auto_bootstrap_runtime'
          : 'auto_bootstrap_invalid';
      core.warning('Restored state artifact is not usable; falling back to bootstrap.');
      return { phase: 'bootstrap', lineageReason: reason };
    }
    if (config.runtimeBackend === 'deterministic-csharp' && config.reviewMode === 'incremental') {
      if (
        error instanceof StateArtifactInvalidError ||
        error instanceof StateRuntimeCompatibilityError
      ) {
        throw new Error('state-invalid: restored state is incompatible');
      }
      throw new Error('state-invalid: restored state could not be read');
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
  runtimeBackend: NonNullable<ActionConfig['runtimeBackend']>,
  target: Awaited<ReturnType<typeof resolveTarget>>,
  expectedArtifactHeadSha?: string,
): void {
  if (restoredState.stateKey !== stateKey) {
    throw new StateArtifactInvalidError(
      'restored state artifact state_key does not match the requested state_key',
    );
  }
  if (restoredState.runtimeProvider !== runtimeProvider) {
    throw new StateRuntimeCompatibilityError(
      'restored state artifact runtime_provider does not match the requested runtime_provider',
    );
  }
  if ((restoredState.runtimeBackend ?? 'legacy') !== runtimeBackend) {
    throw new StateRuntimeCompatibilityError(
      'restored state artifact runtime_backend does not match the requested runtime_backend',
    );
  }
  if (!restoredState.sessionId) {
    throw new StateArtifactInvalidError('restored state artifact is missing session_id');
  }
  const deterministicBackend = runtimeBackend === 'deterministic-csharp';
  if (
    deterministicBackend &&
    expectedArtifactHeadSha !== undefined &&
    restoredState.reviewedHeadSha !== expectedArtifactHeadSha
  ) {
    throw new StateArtifactInvalidError(
      'restored state artifact reviewed_head_sha does not match its workflow run head_sha',
    );
  }
  if (deterministicBackend && target.mode === 'pull-request') {
    if (restoredState.prNumber !== target.prNumber) {
      throw new StateArtifactInvalidError(
        'restored state artifact pull request number does not match the requested target',
      );
    }
    if (restoredState.headRepository !== target.headRepoFullName) {
      throw new StateArtifactInvalidError(
        'restored state artifact head repository does not match the requested target',
      );
    }
  }
  if (target.mode === 'pull-request' && !restoredState.pullRequestDiffSnapshot) {
    throw new SnapshotStateCompatibilityError(
      'restored state artifact is missing pull request diff snapshot metadata',
    );
  }
}

function validateSameRepositoryTarget(target: Awaited<ReturnType<typeof resolveTarget>>): void {
  if (target.mode !== 'pull-request') {
    return;
  }
  if (!target.headRepoFullName) {
    throw new Error('Pull request head repository metadata is required for same-repo validation');
  }
  const expected = `${github.context.repo.owner}/${github.context.repo.repo}`;
  if (target.headRepoFullName !== expected) {
    throw new Error(
      `Fork pull requests are not supported. Expected head repository ${expected}, got ${target.headRepoFullName}.`,
    );
  }
}

async function assertTargetHeadUnchanged(
  target: Awaited<ReturnType<typeof resolveTarget>>,
  octokit: any,
): Promise<void> {
  if (target.mode !== 'pull-request' || !target.prNumber) return;
  try {
    const response = await octokit.rest.pulls.get({
      owner: github.context.repo.owner,
      repo: github.context.repo.repo,
      pull_number: target.prNumber,
    });
    if (response.data.head?.sha !== target.headSha) {
      throw new Error('stale-target: pull request head changed during review');
    }
  } catch (error) {
    if (error instanceof Error && error.message.startsWith('stale-target:')) throw error;
    throw new Error('stale-target: pull request head could not be confirmed');
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
  phaseReason: LineageReason;
  effectiveDiffSource: EffectiveDiffSource;
  octokit: any;
}): Promise<void> {
  const lineageTotals = preserveLineageTotalsForSkipped(options.restoredState);
  const deterministicBackend =
    (options.config.runtimeBackend ?? 'legacy') === 'deterministic-csharp';
  const runtimeResult: RuntimeResult = {
    sessionId: options.restoredState.sessionId,
    sessionName: deterministicBackend
      ? `agentic-pr-review-${options.stateKey}`
      : options.restoredState.sessionName,
    modelReviewJson:
      (options.config.runtimeBackend ?? 'legacy') === 'legacy'
        ? JSON.stringify({
            schemaVersion: 1,
            summary: `No changes since prior review for ${options.target.headSha}. Provider call skipped.`,
            findings: [],
            limitations: [
              'Current PR diff snapshot entries matched the previous reviewed snapshot.',
            ],
          })
        : undefined,
    debugFiles: [],
    toolMode: options.config.toolMode,
    allowedTools: allowedToolsForMode(options.config.toolMode),
    observedTurns: 0,
    observedTurnSource: 'not_applicable',
    usage: null,
    usageBudgetStatus: {
      status: 'not_applicable',
      limits: options.config.usageBudgetLimits,
      usageRecordsObserved: 0,
    },
    lineageTotals,
  };
  const reviewedRange = buildReviewedRange({
    phase: 'incremental',
    target: options.target,
    previousReviewedHeadSha: options.restoredState.reviewedHeadSha,
  });
  const skippedContent = {
    summary: `No changes since prior review for ${options.target.headSha}. Provider call skipped.`,
    findings: [],
    limitations: ['Current PR diff snapshot entries matched the previous reviewed snapshot.'],
  };
  let structuredReview: StructuredReviewEnvelopeV1;
  let structuredMetadata: StructuredResultMetadata;
  if ((options.config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
    let assembled: ReturnType<typeof assembleStructuredReviewFromRuntimeContent>;
    try {
      assembled = assembleStructuredReviewFromRuntimeContent({
        content: skippedContent,
        target: options.target,
        phase: 'incremental',
        previousReviewedHeadSha: options.restoredState.reviewedHeadSha,
        reviewedRange,
        config: options.config,
        sessionId: runtimeResult.sessionId,
        usage: runtimeResult.usage,
        observedTurns: runtimeResult.observedTurns,
        observedTurnSource: runtimeResult.observedTurnSource,
        lineageTotals: runtimeResult.lineageTotals,
        maxFindings: options.config.maxFindings,
      });
    } catch {
      setDeterministicErrorOutputs(
        new Error('mapping-invalid: skipped review could not be assembled'),
      );
      throw new Error('mapping-invalid: skipped review could not be assembled');
    }
    try {
      structuredReview = capStructuredReviewForMarkdownLimit(
        assembled.envelope,
        options.config.maxReviewChars,
      );
      structuredMetadata = metadataForStructuredReview(structuredReview, assembled.metadata.status);
    } catch {
      setDeterministicErrorOutputs(
        new Error('rendering-invalid: skipped review could not be rendered'),
      );
      throw new Error('rendering-invalid: skipped review could not be rendered');
    }
  } else {
    const normalized = normalizeStructuredReview({
      modelJsonText: runtimeResult.modelReviewJson ?? '',
      target: options.target,
      phase: 'incremental',
      previousReviewedHeadSha: options.restoredState.reviewedHeadSha,
      reviewedRange,
      config: options.config,
      sessionId: runtimeResult.sessionId,
      usage: runtimeResult.usage,
      observedTurns: runtimeResult.observedTurns,
      observedTurnSource: runtimeResult.observedTurnSource,
      lineageTotals: runtimeResult.lineageTotals,
      maxFindings: options.config.maxFindings,
    });
    structuredReview = capStructuredReviewForMarkdownLimit(
      normalized.envelope,
      options.config.maxReviewChars,
    );
    structuredMetadata = metadataForStructuredReview(structuredReview, normalized.metadata.status);
  }
  const inlineMetadata = defaultInlineCommentsMetadata(inlinePolicy(options.config));
  structuredReview = {
    ...structuredReview,
    inlineComments: inlineMetadata,
  };
  structuredMetadata.inlineComments = inlineMetadata;
  let renderedReviewMarkdown: string;
  try {
    renderedReviewMarkdown = renderStructuredReviewMarkdown(structuredReview);
  } catch (error) {
    if ((options.config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(new Error(`rendering-invalid: ${messageOf(error)}`));
      throw new Error('rendering-invalid: skipped review could not be rendered');
    }
    throw error;
  }
  const bundleDir = path.join(defaultTempDir(), 'agentic-pr-review', 'state-bundle');
  const structuredResultPath = path.join(bundleDir, 'structured-result.json');
  const renderedReviewMarkdownPath = path.join(bundleDir, 'rendered-review.md');
  let bundleFiles: string[];
  try {
    bundleFiles = await writeStateBundle({
      bundleDir,
      config: options.config,
      target: options.target,
      stateKey: options.stateKey,
      phase: 'incremental',
      promptSha256:
        (options.config.runtimeBackend ?? 'legacy') === 'legacy' ? 'skipped-identical' : undefined,
      blocks: [],
      runtimeResult,
      structuredReview,
      structuredMetadata,
      renderedReviewMarkdown,
      runtimeDir: options.runtimeDir,
      phaseReason: options.phaseReason,
      effectiveDiffSource: options.effectiveDiffSource,
      createdAt: options.restoredState.createdAt,
    });
  } catch (error) {
    if ((options.config.runtimeBackend ?? 'legacy') === 'deterministic-csharp') {
      setDeterministicErrorOutputs(new Error('state-invalid: state bundle construction failed'));
      throw new Error('state-invalid: state bundle construction failed');
    }
    throw error;
  }
  if (deterministicBackend) {
    try {
      await assertTargetHeadUnchanged(options.target, options.octokit);
    } catch {
      setDeterministicErrorOutputs(new Error('state-invalid: target head could not be confirmed'));
      throw new Error('state-invalid: target head could not be confirmed');
    }
  }
  if (deterministicBackend) setDeterministicSuccessOutputs(runtimeResult);
  let uploadedState: UploadedArtifact;
  try {
    uploadedState = await options.store.upload(
      options.artifactName,
      bundleDir,
      bundleFiles,
      options.config.artifactRetentionDays,
    );
  } catch (error) {
    if (deterministicBackend) {
      await writeDeterministicUploadFailureSummary({
        phase: 'incremental',
        reviewPhase: 'skipped-identical',
        runtimeResult,
        stickyWritten: false,
        artifactUploaded: false,
      });
      throw new Error('state-invalid: state artifact upload failed');
    }
    throw error;
  }
  setOutputs({
    stateKey: options.stateKey,
    reviewMode: options.config.reviewMode,
    phase: 'incremental',
    reviewPhase: 'skipped-identical',
    runtimeProvider: options.config.runtimeProvider,
    runtimeBackend: options.config.runtimeBackend ?? 'legacy',
    sessionId: runtimeResult.sessionId,
    reviewedHeadSha: options.target.headSha,
    artifact: uploadedState,
    structuredResultPath,
    renderedReviewMarkdownPath,
    commentUrl: 'skipped-identical',
    lineageAction: '',
    lineageReason: '',
    runtimeResult,
    structuredMetadata,
  });
  await writeSummary({
    config: options.config,
    target: options.target,
    stateKey: options.stateKey,
    phase: 'incremental',
    reviewPhase: 'skipped-identical',
    runtimeResult,
    promptSha256:
      (options.config.runtimeBackend ?? 'legacy') === 'legacy' ? 'skipped-identical' : undefined,
    promptBytes: (options.config.runtimeBackend ?? 'legacy') === 'legacy' ? 0 : undefined,
    restored: {
      phase: 'incremental',
      restoredState: options.restoredState,
      lineageReason: options.phaseReason,
    },
    effectiveDiffSource: options.effectiveDiffSource,
    artifactName: options.artifactName,
    commentUrl: 'skipped-identical',
    bundleDir,
    structuredMetadata,
    structuredResultPath,
    renderedReviewMarkdownPath,
  });
}

function requireRestoredState(restoredState: RestoredState | undefined): RestoredState {
  if (!restoredState) {
    throw new Error('internal error: identical incremental requires restored state');
  }
  return restoredState;
}

function requirePullRequestDiffSnapshot(
  target: Awaited<ReturnType<typeof resolveTarget>>,
): PullRequestDiffSnapshotV1 {
  if (!target.pullRequestDiffSnapshot) {
    throw new Error('internal error: pull-request target requires current diff snapshot');
  }
  return target.pullRequestDiffSnapshot;
}

function effectiveDiffSourceFor(
  target: Awaited<ReturnType<typeof resolveTarget>>,
  phase: Phase,
): EffectiveDiffSource {
  if (target.mode !== 'pull-request') {
    return 'target_changed_files';
  }
  return phase === 'incremental' ? 'incremental_pr_diff_snapshot_delta' : 'bootstrap_pr_files';
}

async function maybePostComment(options: {
  config: ActionConfig;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  runtimeResult: RuntimeResult;
  stateKey: string;
  phase: Phase;
  runtimeBackend?: ActionConfig['runtimeBackend'];
  artifactName: string;
  lineageReason: LineageReason;
  previousHeadSha?: string;
  structuredReview: StructuredReviewEnvelopeV1;
  octokit: any;
}): Promise<{ commentUrl: string; lineageAction: string; lineageReason: string }> {
  if (!options.config.postComment) {
    return { commentUrl: '', lineageAction: '', lineageReason: '' };
  }
  if (options.target.mode !== 'pull-request' || !options.target.prNumber) {
    throw new Error('post_comment=true requires target_mode=pull-request');
  }

  const comment = await upsertLineageComment({
    octokit: options.octokit,
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
    prNumber: options.target.prNumber,
    target: options.target,
    structuredReview: options.structuredReview,
    stateKey: options.stateKey,
    phase: options.phase,
    runtimeProvider: options.config.runtimeProvider,
    runtimeBackend: options.config.runtimeBackend ?? 'legacy',
    sessionId: options.runtimeResult.sessionId,
    previousHeadSha: options.previousHeadSha,
    currentHeadSha: options.target.headSha,
    artifactName: options.artifactName,
    runId: github.context.runId,
    runAttempt: github.context.runAttempt,
    lineageReason: options.lineageReason,
    usage: options.runtimeResult.usage,
    observedTurns: options.runtimeResult.observedTurns,
    maxTurns: options.config.claudeMaxTurns,
    lineageTotals: options.runtimeResult.lineageTotals,
    maxReviewChars: options.config.maxReviewChars,
  });
  return {
    commentUrl: comment.commentUrl,
    lineageAction: comment.lineageAction,
    lineageReason: comment.lineageReason,
  };
}

async function maybePostInlineComments(options: {
  config: ActionConfig;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  stateKey: string;
  structuredReview: StructuredReviewEnvelopeV1;
  octokit: any;
  stickyCommentUrl: string;
}): Promise<InlineCommentsMetadata> {
  const policy = inlinePolicy(options.config);
  if (options.target.mode !== 'pull-request' || !options.target.prNumber) {
    const metadata = defaultInlineCommentsMetadata(policy);
    if (policy.enabled) {
      metadata.skippedCount = options.structuredReview.findings.length;
      metadata.skippedReasons.non_pull_request = options.structuredReview.findings.length;
    }
    return metadata;
  }
  if (policy.enabled && !options.config.postComment) {
    const metadata = defaultInlineCommentsMetadata(policy);
    metadata.skippedCount = options.structuredReview.findings.length;
    metadata.skippedReasons.sticky_comment_disabled = options.structuredReview.findings.length;
    core.warning(
      'Inline PR review comments require post_comment=true so the sticky review remains the source of truth.',
    );
    return metadata;
  }
  try {
    return await postInlineComments({
      octokit: options.octokit,
      owner: github.context.repo.owner,
      repo: github.context.repo.repo,
      prNumber: options.target.prNumber,
      stateKey: options.stateKey,
      reviewedHeadSha: options.structuredReview.headSha,
      stickyCommentUrl: options.stickyCommentUrl,
      structuredReview: options.structuredReview,
      policy,
    });
  } catch (error) {
    const metadata = defaultInlineCommentsMetadata(policy);
    metadata.failedCount = policy.enabled ? options.structuredReview.findings.length : 0;
    if (metadata.failedCount > 0) {
      metadata.failedReasons.api_failed = metadata.failedCount;
    }
    const diagnostic = truncateText(messageOf(error).replace(/\s+/g, ' '), 240);
    core.warning(`Inline PR review comments skipped after sanitized failure: ${diagnostic}`);
    return metadata;
  }
}

function inlinePolicy(config: ActionConfig): InlineCommentsPolicy {
  return {
    enabled: config.inlineComments,
    maxComments: config.maxInlineComments,
    minSeverity: config.inlineMinSeverity,
    minConfidence: config.inlineMinConfidence,
  };
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
  runtimeBackend: string;
  sessionId: string;
  reviewedHeadSha: string;
  artifact: UploadedArtifact;
  structuredResultPath: string;
  renderedReviewMarkdownPath: string;
  commentUrl: string;
  lineageAction: string;
  lineageReason: string;
  debugArtifact?: UploadedArtifact;
  runtimeResult?: RuntimeResult;
  structuredMetadata: StructuredResultMetadata;
}): void {
  core.setOutput('state_key', options.stateKey);
  core.setOutput('review_mode', options.reviewMode);
  core.setOutput('phase', options.phase);
  core.setOutput('review_phase', options.reviewPhase);
  core.setOutput('runtime_provider', options.runtimeProvider);
  if (options.runtimeBackend === 'deterministic-csharp') {
    setDeterministicSuccessOutputs(options.runtimeResult);
  }
  core.setOutput('session_id', options.sessionId);
  core.setOutput('reviewed_head_sha', options.reviewedHeadSha);
  core.setOutput('artifact_name', options.artifact.name);
  core.setOutput('artifact_id', options.artifact.id ?? '');
  core.setOutput('artifact_url', options.artifact.url ?? '');
  core.setOutput('artifact_retention_days', String(options.artifact.retentionDays));
  core.setOutput('structured_result_path', options.structuredResultPath);
  core.setOutput('rendered_review_markdown_path', options.renderedReviewMarkdownPath);
  core.setOutput('structured_output_status', options.structuredMetadata.status);
  core.setOutput('findings_input_count', String(options.structuredMetadata.inputFindingCount));
  core.setOutput('findings_post_cap_count', String(options.structuredMetadata.postFindingCapCount));
  core.setOutput(
    'findings_rendered_count',
    String(options.structuredMetadata.renderedFindingCount),
  );
  core.setOutput('findings_truncated', String(options.structuredMetadata.findingsTruncated));
  core.setOutput('findings_truncation_reason', options.structuredMetadata.truncationReason ?? '');
  const inlineMetadata =
    options.structuredMetadata.inlineComments ??
    defaultInlineCommentsMetadata({
      enabled: false,
      maxComments: 5,
      minSeverity: 'medium',
      minConfidence: 'high',
    });
  core.setOutput('inline_comments_enabled', String(inlineMetadata.enabled));
  core.setOutput('inline_comments_candidate_count', String(inlineMetadata.candidateCount));
  core.setOutput('inline_comments_effective_cap', String(inlineMetadata.effectiveCap));
  core.setOutput('inline_comments_cap_exceeded_count', String(inlineMetadata.capExceededCount));
  core.setOutput('inline_comments_posted_count', String(inlineMetadata.postedCount));
  core.setOutput('inline_comments_duplicate_count', String(inlineMetadata.duplicateCount));
  core.setOutput('inline_comments_skipped_count', String(inlineMetadata.skippedCount));
  core.setOutput('inline_comments_failed_count', String(inlineMetadata.failedCount));
  core.setOutput('comment_url', options.commentUrl);
  core.setOutput('lineage_action', options.lineageAction);
  core.setOutput('lineage_reason', options.lineageReason);
  if (options.runtimeResult) {
    core.setOutput('observed_turns', String(options.runtimeResult.observedTurns ?? ''));
    core.setOutput('observed_turn_source', options.runtimeResult.observedTurnSource);
    core.setOutput(
      'lineage_observed_turns',
      String(options.runtimeResult.lineageTotals.observedTurns ?? ''),
    );
    core.setOutput('lineage_totals_source', options.runtimeResult.lineageTotals.source);
    core.setOutput('lineage_totals_partial', String(options.runtimeResult.lineageTotals.partial));
    core.setOutput(
      'lineage_usage_input_tokens',
      String(options.runtimeResult.lineageTotals.usage.inputTokens),
    );
    core.setOutput(
      'lineage_usage_cache_read_input_tokens',
      String(options.runtimeResult.lineageTotals.usage.cacheReadInputTokens),
    );
    core.setOutput(
      'lineage_usage_cache_creation_input_tokens',
      String(options.runtimeResult.lineageTotals.usage.cacheCreationInputTokens),
    );
    core.setOutput(
      'lineage_usage_output_tokens',
      String(options.runtimeResult.lineageTotals.usage.outputTokens),
    );
  }
  if (options.debugArtifact) {
    core.setOutput('debug_artifact_name', options.debugArtifact.name);
    core.setOutput('debug_artifact_id', options.debugArtifact.id ?? '');
    core.setOutput('debug_artifact_url', options.debugArtifact.url ?? '');
  }
}

function setDeterministicSuccessOutputs(runtimeResult: RuntimeResult | undefined): void {
  core.setOutput('runtime_backend', 'deterministic-csharp');
  core.setOutput('runtime_version', runtimeResult?.runtimeVersion ?? '');
  core.setOutput('runtime_trace_sha256', runtimeResult?.traceSha256 ?? '');
  core.setOutput('runtime_error_kind', '');
  core.setOutput('runtime_error_class', '');
  if (runtimeResult) {
    core.setOutput('usage_budget_status', formatUsageBudgetStatus(runtimeResult.usageBudgetStatus));
  }
}

function setLedgerSuccessOutputs(result: Awaited<ReturnType<typeof runLedgerCsharp>>): void {
  core.setOutput('runtime_backend', 'ledger-csharp');
  core.setOutput('runtime_version', result.runtimeVersion);
  core.setOutput('runtime_trace_sha256', result.traceSha256);
  core.setOutput('runtime_error_kind', '');
  core.setOutput('runtime_error_class', '');
  core.setOutput('usage_budget_status', 'not_applicable (records=0)');
  core.setOutput('state_key', result.stateKey);
  core.setOutput('phase', result.phase);
  core.setOutput('review_phase', result.phase);
  core.setOutput('state_transition', result.transition);
  core.setOutput('state_reason', result.stateReason);
  core.setOutput('state_candidate_id', result.candidateId);
  core.setOutput('state_marker_id', result.markerId);
  core.setOutput('state_selector_revision', result.selectorRevision);
  core.setOutput('state_session_epoch', result.sessionEpoch);
  core.setOutput('state_generation', result.stateGeneration);
  core.setOutput('state_ledger_epoch', result.ledgerEpoch);
  core.setOutput('state_acceptance_status', result.acceptanceStatus);
  core.setOutput('state_acceptance_reason', result.acceptanceReason);
  core.setOutput('state_publication_status', result.publicationStatus);
  core.setOutput('state_receipt_status', result.receiptStatus);
  core.setOutput('state_cleanup_warnings', result.cleanupWarnings);
  core.setOutput('state_error_kind', '');
  core.setOutput('comment_url', result.commentUrl);
}

function setLedgerPartialFailureOutputs(error: LedgerRunFailure): void {
  setLedgerSuccessOutputs(error.result);
  core.setOutput('runtime_error_kind', error.errorKind);
  core.setOutput('state_error_kind', error.errorKind);
}

async function writeLedgerSummary(result: LedgerRunResult, failureKind = ''): Promise<void> {
  try {
    await core.summary
      .addRaw(
        [
          '### Agentic PR Review M4 ledger',
          '',
          `- Phase: ${result.phase}`,
          `- Transition: ${result.transition}`,
          `- Acceptance: ${result.acceptanceStatus}${result.acceptanceReason ? ` (${result.acceptanceReason})` : ''}`,
          `- Publication: ${result.publicationStatus}`,
          `- Receipt: ${result.receiptStatus}`,
          `- Selector revision: ${result.selectorRevision || 'n/a'}`,
          `- Failure classification: ${failureKind || 'none'}`,
        ].join('\n'),
      )
      .write();
  } catch {
    core.info('Unable to write M4 ledger summary.');
  }
}

function setLedgerInitialOutputs(): void {
  for (const [name, value] of Object.entries({
    runtime_backend: 'ledger-csharp',
    runtime_version: '',
    runtime_trace_sha256: '',
    runtime_error_kind: '',
    runtime_error_class: '',
    usage_budget_status: 'not_applicable (records=0)',
    state_key: '',
    phase: '',
    review_phase: '',
    state_transition: '',
    state_reason: '',
    state_candidate_id: '',
    state_marker_id: '',
    state_selector_revision: '',
    state_session_epoch: '',
    state_generation: '',
    state_ledger_epoch: '',
    state_acceptance_status: 'not_started',
    state_acceptance_reason: '',
    state_publication_status: 'not_started',
    state_receipt_status: 'not_started',
    state_cleanup_warnings: '',
    state_error_kind: '',
    comment_url: '',
  })) {
    core.setOutput(name, value);
  }
}

function setDeterministicHostErrorKind(kind: string): void {
  core.setOutput('runtime_error_kind', kind);
  core.setOutput('runtime_error_class', '');
}

function metadataForStructuredReview(
  review: StructuredReviewEnvelopeV1,
  status: StructuredResultMetadata['status'],
): StructuredResultMetadata {
  return {
    inputFindingCount: review.result.inputFindingCount,
    postFindingCapCount: review.result.postFindingCapCount,
    renderedFindingCount: review.result.renderedFindingCount,
    findingsTruncated: review.result.findingsTruncated,
    truncationReason: review.result.truncationReason,
    status,
    inlineComments: review.inlineComments,
  };
}

async function writeSummary(input: {
  config: ActionConfig;
  target: Awaited<ReturnType<typeof resolveTarget>>;
  stateKey: string;
  phase: Phase;
  reviewPhase: ReviewPhaseOutput;
  runtimeResult: RuntimeResult;
  promptSha256?: string;
  promptBytes?: number;
  restored: PhaseResolution;
  effectiveDiffSource: EffectiveDiffSource;
  artifactName: string;
  commentUrl: string;
  bundleDir: string;
  structuredMetadata: StructuredResultMetadata;
  structuredResultPath: string;
  renderedReviewMarkdownPath: string;
}): Promise<void> {
  const restored = input.restored.restoredState
    ? `yes, head ${input.restored.restoredState.reviewedHeadSha ?? 'unknown'}`
    : 'no';
  const usage = input.runtimeResult.usage
    ? [
        `cache_read=${input.runtimeResult.usage.cacheReadInputTokens}`,
        `cache_creation=${input.runtimeResult.usage.cacheCreationInputTokens}`,
        `input=${input.runtimeResult.usage.inputTokens}`,
        `output=${input.runtimeResult.usage.outputTokens}`,
      ].join(', ')
    : 'not exposed';
  const lineageUsage = [
    `cache_read=${input.runtimeResult.lineageTotals.usage.cacheReadInputTokens}`,
    `cache_creation=${input.runtimeResult.lineageTotals.usage.cacheCreationInputTokens}`,
    `input=${input.runtimeResult.lineageTotals.usage.inputTokens}`,
    `output=${input.runtimeResult.lineageTotals.usage.outputTokens}`,
  ].join(', ');
  const lineageSourceDetail = input.runtimeResult.lineageTotals.partial
    ? `${input.runtimeResult.lineageTotals.source}, partial`
    : `${input.runtimeResult.lineageTotals.source}, complete`;
  const allowedTools =
    input.runtimeResult.allowedTools.length > 0
      ? input.runtimeResult.allowedTools.join(', ')
      : 'none';
  const budgetStatus = formatUsageBudgetStatus(input.runtimeResult.usageBudgetStatus);
  const maxTurns = input.config.claudeMaxTurns;
  const runtimeBackend = input.config.runtimeBackend ?? 'legacy';
  const inputMetadata =
    runtimeBackend === 'deterministic-csharp'
      ? input.reviewPhase === 'skipped-identical'
        ? ['- Review input: not applicable (skipped-identical)']
        : [
            `- Review input SHA-256: ${input.runtimeResult.reviewInputSha256 ?? 'n/a'}`,
            `- Review input bytes: ${input.runtimeResult.reviewInputBytes ?? 'n/a'}`,
          ]
      : [
          `- Prompt sha256: ${input.promptSha256 ?? 'n/a'}`,
          `- Prompt bytes: ${input.promptBytes ?? 'n/a'}`,
        ];
  const deterministicMetadata =
    runtimeBackend === 'deterministic-csharp'
      ? [
          `- Runtime version: ${formatRuntimeVersion(input.runtimeResult.runtimeVersion)}`,
          `- Runtime trace sha256: ${input.runtimeResult.traceSha256 ?? 'n/a'}`,
          ...(input.runtimeResult.diagnosticSummary
            ? [`- Runtime diagnostics: ${input.runtimeResult.diagnosticSummary}`]
            : []),
        ]
      : [];
  const pathMetadata =
    runtimeBackend === 'legacy'
      ? [
          `- Local bundle path: ${input.bundleDir}`,
          `- Structured result path: ${input.structuredResultPath}`,
          `- Rendered review markdown path: ${input.renderedReviewMarkdownPath}`,
        ]
      : [];
  const lines = [
    '### Agentic PR Review',
    '',
    `- Requested mode: ${input.config.reviewMode}`,
    `- Resolved phase: ${input.phase}`,
    `- Review phase: ${input.reviewPhase}`,
    `- Phase reason: ${input.restored.lineageReason}`,
    `- Effective diff source: ${input.effectiveDiffSource}`,
    `- Runtime: ${
      runtimeBackend === 'deterministic-csharp'
        ? `${input.config.runtimeProvider} (${runtimeBackend})`
        : input.config.runtimeProvider
    }`,
    `- Tool mode: ${input.runtimeResult.toolMode}`,
    `- Allowed tools: ${allowedTools}`,
    `- State key: ${input.stateKey}`,
    `- Session id: ${input.runtimeResult.sessionId}`,
    `- Restored previous state: ${restored}`,
    `- Previous reviewed head: ${input.restored.restoredState?.reviewedHeadSha ?? 'n/a'}`,
    `- Current head: ${input.target.headSha}`,
    ...inputMetadata,
    ...deterministicMetadata,
    `- Observed turns: ${input.runtimeResult.observedTurns ?? 'n/a'} / max ${maxTurns}`,
    `- Usage: ${usage}`,
    `- Usage budget: ${budgetStatus}`,
    `- Structured output status: ${input.structuredMetadata.status}`,
    `- Findings: ${input.structuredMetadata.renderedFindingCount}/${input.structuredMetadata.postFindingCapCount}/${input.structuredMetadata.inputFindingCount}`,
    `- Findings truncated: ${input.structuredMetadata.findingsTruncated}`,
    `- Findings truncation reason: ${input.structuredMetadata.truncationReason ?? 'n/a'}`,
    `- Inline comments: ${formatInlineComments(input.structuredMetadata.inlineComments)}`,
    `- Lineage turns: ${input.runtimeResult.lineageTotals.observedTurns ?? 'n/a'}`,
    `- Lineage usage: ${lineageUsage}`,
    `- Lineage source: ${lineageSourceDetail}`,
    `- Sticky comment: ${input.commentUrl || 'not requested'}`,
    `- State artifact: ${input.artifactName}`,
    `- Artifact retention days: ${input.config.artifactRetentionDays}`,
    ...pathMetadata,
  ];
  try {
    await core.summary.addRaw(lines.join('\n')).write();
  } catch {
    core.info('Unable to write job summary.');
  }
}

async function writeDeterministicUploadFailureSummary(input: {
  phase: Phase;
  reviewPhase: ReviewPhaseOutput;
  runtimeResult: RuntimeResult;
  stickyWritten: boolean;
  artifactUploaded: boolean;
}): Promise<void> {
  try {
    await core.summary
      .addRaw(
        [
          '### Agentic PR Review',
          '',
          '- Runtime backend: deterministic-csharp',
          `- Runtime version: ${formatRuntimeVersion(input.runtimeResult.runtimeVersion)}`,
          `- Runtime trace sha256: ${input.runtimeResult.traceSha256 ?? 'n/a'}`,
          `- Resolved phase: ${input.phase}`,
          `- Review phase: ${input.reviewPhase}`,
          `- Runtime execution: succeeded`,
          `- Sticky comment written: ${input.stickyWritten}`,
          `- State artifact upload: ${input.artifactUploaded ? 'succeeded' : 'failed'}`,
          '- Failure classification: state-invalid',
        ].join('\n'),
      )
      .write();
  } catch {
    core.info('Unable to write deterministic failure summary.');
  }
}

async function writeDeterministicStickyFailureSummary(input: {
  phase: Phase;
  reviewPhase: ReviewPhaseOutput;
  runtimeResult: RuntimeResult;
}): Promise<void> {
  try {
    await core.summary
      .addRaw(
        [
          '### Agentic PR Review',
          '',
          '- Runtime backend: deterministic-csharp',
          `- Runtime version: ${formatRuntimeVersion(input.runtimeResult.runtimeVersion)}`,
          `- Runtime trace sha256: ${input.runtimeResult.traceSha256 ?? 'n/a'}`,
          `- Resolved phase: ${input.phase}`,
          `- Review phase: ${input.reviewPhase}`,
          '- Runtime execution: succeeded',
          '- Sticky comment written: false',
          '- State artifact upload: not attempted',
          '- Failure classification: rendering-invalid',
        ].join('\n'),
      )
      .write();
  } catch {
    core.info('Unable to write deterministic sticky failure summary.');
  }
}

function formatInlineComments(metadata: InlineCommentsMetadata | undefined): string {
  if (!metadata) {
    return 'disabled';
  }
  const skippedReasons = Object.keys(metadata.skippedReasons).join(', ') || 'none';
  const failedReasons = Object.keys(metadata.failedReasons).join(', ') || 'none';
  return [
    `enabled=${metadata.enabled}`,
    `candidates=${metadata.candidateCount}`,
    `cap=${metadata.effectiveCap}`,
    `posted=${metadata.postedCount}`,
    `duplicates=${metadata.duplicateCount}`,
    `skipped=${metadata.skippedCount} (${skippedReasons})`,
    `failed=${metadata.failedCount} (${failedReasons})`,
  ].join(', ');
}

function formatUsageBudgetStatus(status: RuntimeResult['usageBudgetStatus']): string {
  if (status.status === 'exceeded' && status.exceeded) {
    return `${status.status} (${status.exceeded.category} ${status.exceeded.observed}/${status.exceeded.limit})`;
  }
  return `${status.status} (records=${status.usageRecordsObserved})`;
}

function formatRuntimeVersion(value: string | undefined): string {
  if (!value) return 'n/a';
  return value.replace(/[`\r\n\u0000-\u001f\u007f]/g, '?').slice(0, 120);
}

function isSafeRuntimeVersion(value: string): boolean {
  return value.length > 0 && value.length <= 120 && !/[\u0000-\u001f\u007f\r\n]/.test(value);
}

function handleStructuredValidationFailure(error: unknown): never {
  if (error instanceof StructuredReviewValidationError) {
    core.setOutput('structured_output_status', error.status);
    core.setOutput('findings_input_count', '0');
    core.setOutput('findings_post_cap_count', '0');
    core.setOutput('findings_rendered_count', '0');
    core.setOutput('findings_truncated', 'false');
    core.setOutput('findings_truncation_reason', '');
    throw new Error(`structured review output validation failed: ${error.sanitizedDiagnostic}`);
  }
  throw error;
}

function setDeterministicErrorOutputs(error: unknown): void {
  const adapterError = error instanceof RuntimeInvocationError ? error : undefined;
  const message = messageOf(error);
  const hostKinds = [
    'config-invalid',
    'command-unavailable',
    'input-invalid',
    'trace-invalid',
    'diagnostic-error',
    'mapping-invalid',
    'state-invalid',
    'rendering-invalid',
  ];
  const prefix = message.split(':', 1)[0];
  const kind = adapterError?.kind ?? (hostKinds.includes(prefix) ? prefix : 'rendering-invalid');
  core.setOutput('runtime_backend', 'deterministic-csharp');
  core.setOutput('runtime_version', '');
  core.setOutput('runtime_trace_sha256', '');
  core.setOutput('runtime_error_kind', kind);
  core.setOutput('runtime_error_class', adapterError?.exitClass ?? '');
  core.setOutput('usage_budget_status', 'not_applicable (records=0)');
}

function setLedgerErrorOutputs(error: unknown): void {
  const message = messageOf(error);
  const prefix = message.split(':', 1)[0];
  const kind = ['config-invalid', 'command-unavailable', 'input-invalid', 'state-invalid'].includes(
    prefix,
  )
    ? prefix
    : 'state-invalid';
  core.setOutput('runtime_backend', 'ledger-csharp');
  core.setOutput('runtime_version', '');
  core.setOutput('runtime_trace_sha256', '');
  core.setOutput('runtime_error_kind', kind);
  core.setOutput('runtime_error_class', '');
  core.setOutput('usage_budget_status', 'not_applicable (records=0)');
  core.setOutput('state_transition', '');
  core.setOutput('state_reason', '');
  core.setOutput('state_candidate_id', '');
  core.setOutput('state_marker_id', '');
  core.setOutput('state_selector_revision', '');
  core.setOutput('state_session_epoch', '');
  core.setOutput('state_generation', '');
  core.setOutput('state_ledger_epoch', '');
  core.setOutput('state_acceptance_status', 'not_started');
  core.setOutput('state_acceptance_reason', '');
  core.setOutput('state_publication_status', 'not_attempted');
  core.setOutput('state_receipt_status', 'not_attempted');
  core.setOutput('state_cleanup_warnings', '');
  core.setOutput('state_error_kind', kind);
}

export function sanitizeRuntimeDiagnostic(value: string, secrets: readonly string[] = []): string {
  let sanitized = value.replace(/\s+/g, ' ').trim();
  for (const secret of secrets.filter((item) => item.length > 4)) {
    sanitized = sanitized.replaceAll(secret, '***');
  }
  return truncateText(
    sanitized
      .replace(/Authorization\s*[:=]\s*[^,;|]+/gi, 'Authorization: ***')
      .replace(/x-api-(?:key|token)\s*[:=]\s*[^\s,;|]+/gi, 'x-api-key: ***')
      .replace(/(^|[^A-Za-z0-9_])(?:(?:[A-Za-z]:[\\/])|(?:\\\\)|\/)[^\s"'`() ,;]*/g, '$1<path>')
      .replace(
        /(^|[^A-Za-z0-9_])(?:ghp_|github_pat_|gho_|ghu_|ghs_|ghr_|sk-)[A-Za-z0-9_-]+/gi,
        '$1***',
      ),
    240,
  );
}

function sanitizeRuntimeDiagnosticForHost(value: string): string {
  return sanitizeRuntimeDiagnostic(
    value,
    [
      process.env.GITHUB_TOKEN,
      process.env.AGENTIC_REVIEW_API_KEY,
      process.env.ANTHROPIC_API_KEY,
      process.env.ANTHROPIC_AUTH_TOKEN,
    ].filter((item): item is string => Boolean(item)),
  );
}

function boundedRuntimeMessages(messages: readonly string[]): string[] {
  const limit = 10;
  if (messages.length <= limit) {
    return [...messages];
  }
  return [...messages.slice(0, limit), `${messages.length - limit} additional messages omitted`];
}

function summarizeRuntimeDiagnostics(
  diagnostics: ReadonlyArray<{ code: string; level: 'info' | 'warning' | 'error' }>,
): string {
  const counts = new Map<string, number>();
  for (const diagnostic of diagnostics) {
    if (diagnostic.level === 'error') {
      continue;
    }
    const code = sanitizeRuntimeDiagnosticForHost(diagnostic.code);
    counts.set(code, (counts.get(code) ?? 0) + 1);
  }
  return [...counts.entries()]
    .slice(0, 20)
    .map(([code, count]) => `${code}=${count}`)
    .join(', ');
}

function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  run().catch((error: unknown) => {
    core.setFailed(sanitizeRuntimeDiagnosticForHost(messageOf(error)));
  });
}
