import * as github from '@actions/github';
import { capStructuredReviewForMarkdownLimit, upsertM4StateComment } from './comments.js';
import { assembleStructuredReviewFromRuntimeContent } from './structured.js';
import { buildReviewInputV1 } from './protocol/build-review-input.js';
import { mapReviewResultV1ToRuntimeContent } from './protocol/map-review-result.js';
import { computeCacheContractDigest, computeSubjectDigest } from './prefix-contract/digest.js';
import { deriveInteractionId } from './prefix-contract/interaction-id.js';
import { invokeLiveRuntime } from './live-runtime-invocation/invoke-live-runtime.js';
import type { LiveRuntimeInvocationContextV1 } from './live-runtime-invocation/context.js';
import { resolveTrustedRuntimeCommand } from './runtime-invocation/command-resolver.js';
import { serializeInputBytes, sha256Hex } from './runtime-invocation/runtime-files.js';
import {
  acceptLocalCandidate,
  computeCandidateId,
  GitHubGitStateAcceptanceStore,
  OctokitGitDataClient,
  StickyCallbackOutcomeUnknownError,
  type StateAcceptanceStore,
  type StateSelectionSnapshot,
  type StateKeyV2,
} from './state-acceptance/index.js';
import {
  classifyStateBundleV2,
  type StateManifestV2Input,
  type StateManifestV2Transition,
} from './state-v2/index.js';
import type { ActionConfig, Phase, ReviewTarget, StructuredReviewEnvelopeV1 } from './types.js';

export const LEDGER_WORKFLOW_IDENTITY = 'agentic-pr-review/m4-stateful-review/v1';
export const LEDGER_TRUSTED_EXECUTION_DOMAIN = 'github-default-branch-workflow-run/v1';
export const VERIFICATION_WORKFLOW_IDENTITY = 'agentic-pr-review/m4-stateful-verification/v1';
export const VERIFICATION_TRUSTED_EXECUTION_DOMAIN =
  'github-default-branch-maintainer-verification/v1';
const PLACEHOLDER_EPOCH = 'AAAAAAAAAAAAAAAAAAAAAA';

const cacheContractEnvelopes = {
  template: {
    schemaVersion: 1,
    templateVersion: 3,
    definition: { role: 'system', text: 'You are a precise code reviewer.' },
  },
  policy: {
    schemaVersion: 1,
    policyVersion: 2,
    instructions: 'Review the delta carefully.',
    constraints: { maxFindings: 10, tone: 'strict' },
  },
  tools: {
    schemaVersion: 1,
    toolsetVersion: 1,
    definitions: [
      {
        name: 'submit_review',
        description: 'Submit the structured review.',
        inputSchema: {
          type: 'object',
          properties: { summary: { type: 'string' } },
          required: ['summary'],
        },
        policyMetadata: { risk: 'low' },
      },
    ],
  },
  cacheConfig: {
    schemaVersion: 1,
    cacheConfigVersion: 1,
    markerPolicy: 'stable-boundary',
    eligibility: 'min-prefix-1024',
    statelessMode: false,
  },
  adapter: {
    schemaVersion: 1,
    capabilityProfileVersion: 1,
    adapterBuildVersion: '0.0.0-fixture',
  },
} as const;

const cacheContractIdentity = {
  ledgerSchemaVersion: 1 as const,
  prefixContractVersion: 1 as const,
  providerId: 'provider',
  modelId: 'model-2024-01-01',
  templateId: 'd5c87b69a0d5d89d58e8c7209b0cbcc9624a3f0646fc19f3cebc7c3f93a5b6cf',
  policyId: '6ef8489dac852f64c567efd63a196ef4b8bbf06d9b709b8af491d24c5a2b52b3',
  toolDefinitionId: 'e58c02b21ad200207ab6ae1e8665c2c6c7d2c1413d947b8dd26f7ef14cc1bd48',
  cacheConfigId: 'd5d1e7d93a8fac3ec89b896c771e92301d8cae17fe39fb7e63a42ebe3b35bfe9',
  adapterId: 'e0b738711687dd8e1d4aefea903cc395b48dc4c9e8ef4b11fedac34e67fd16c6',
} as const;

export interface LedgerRunResult {
  readonly stateKey: string;
  readonly phase: Phase;
  readonly transition: string;
  readonly acceptanceStatus: string;
  readonly acceptanceReason: string;
  readonly publicationStatus: string;
  readonly receiptStatus: 'written' | 'failed' | 'not_written';
  readonly runtimeVersion: string;
  readonly traceSha256: string;
  readonly commentUrl: string;
  readonly stateReason: string;
  readonly candidateId: string;
  readonly markerId: string;
  readonly selectorRevision: string;
  readonly sessionEpoch: string;
  readonly stateGeneration: string;
  readonly ledgerEpoch: string;
  readonly cleanupWarnings: string;
}

export async function runLedgerCsharp(input: {
  readonly config: ActionConfig;
  readonly target: ReviewTarget;
  readonly octokit: any;
  readonly eventName: string;
  readonly defaultBranchCommitSha: string;
}): Promise<LedgerRunResult> {
  await validateLedgerInvocationEvent(input.config, input.eventName, input.octokit, input.target);
  if (input.target.mode !== 'pull-request' || !input.target.prNumber) {
    throw new Error('input-invalid: ledger-csharp requires a resolved pull request');
  }
  const repository = `${github.context.repo.owner}/${github.context.repo.repo}`;
  const stateKey = ledgerStateKey(
    repository,
    input.target.prNumber,
    input.config.verificationNamespace,
  );
  const store = new GitHubGitStateAcceptanceStore(
    new OctokitGitDataClient(input.octokit),
    github.context.repo.owner,
    github.context.repo.repo,
  );
  await store.ensureInitialized({
    defaultBranchCommitSha: input.defaultBranchCommitSha,
    stateKey,
    runId: String(github.context.runId),
    runAttempt: github.context.runAttempt,
  });
  const { selector } = await store.peekSelectorForComparison(stateKey);
  const headRelationship = await resolveHeadRelationship(
    input.octokit,
    repository,
    selector?.currentHeadSha,
    input.target.headSha,
  );
  const selection = await store.selectAcceptedState({
    stateKey,
    expectedLedgerSchemaVersion: cacheContractIdentity.ledgerSchemaVersion,
    expectedPrefixContractVersion: cacheContractIdentity.prefixContractVersion,
    cacheContractIdentity: omitContractVersions(cacheContractIdentity) as never,
    currentHeadSha: input.target.headSha as never,
    currentBaseSha: input.target.baseSha as never,
    currentBaseRef: canonicalBaseRef(input.target.baseRef),
    provenanceTrusted: true,
    workflowIdentity: stateKey.workflowIdentity,
    trustedExecutionDomain: stateKey.trustedExecutionDomain,
    expectedWorkflowEvent: input.eventName,
    expectedProducingWorkflowRef: String(process.env.GITHUB_WORKFLOW_REF ?? ''),
    expectedProducingGitRef: String(process.env.GITHUB_REF ?? ''),
    expectedProducingActionSourceSha: String(process.env.GITHUB_SHA ?? '') as never,
    headRelationship,
    explicitRestore: input.config.reviewMode === 'incremental',
  });
  if (selection.selection !== 'selected') {
    throw new Error(`state-invalid: state selection ${selection.selection}:${selection.reason}`);
  }
  if (selection.snapshot.kind === 'explicit_restore_invalid') {
    throw new Error(`state-invalid: explicit restore rejected: ${selection.snapshot.failure}`);
  }
  const plan = planLedgerInvocation({
    config: input.config,
    target: input.target,
    stateKey,
    selection: selection.snapshot,
    eventName: input.eventName,
  });
  const command = await resolveTrustedRuntimeCommand(process.env);
  const lease = await invokeLiveRuntime({
    command: command.command,
    input: plan.reviewInput,
    context: plan.context,
    manifestInput: plan.manifestInput,
    timeoutMs: 30_000,
    trustedRoot: process.env.RUNNER_TEMP,
    predecessorLedgerBytes: plan.predecessor?.ledgerBytes,
    predecessorManifestBytes: plan.predecessor?.manifestBytes,
    predecessorProviderRunMetadataBytes: plan.predecessor?.providerRunMetadataBytes,
  });
  const assembled = assembleStructuredReviewFromRuntimeContent({
    content: mapReviewResultV1ToRuntimeContent(lease.result).content,
    target: input.target,
    phase: plan.phase,
    previousReviewedHeadSha: plan.previousHeadSha,
    reviewedRange: {
      kind: plan.phase,
      fromSha: plan.previousHeadSha ?? null,
      toSha: input.target.headSha,
    },
    config: input.config,
    sessionId: `m4:${stateKey.pullRequest}`,
    usage: null,
    observedTurns: null,
    observedTurnSource: 'not_applicable',
    lineageTotals: emptyLineageTotals(),
    maxFindings: input.config.maxFindings,
  });
  const structuredReview = capStructuredReviewForMarkdownLimit(
    assembled.envelope,
    input.config.maxReviewChars,
  );
  const revalidation = await targetStillMatches(input.target, input.octokit);
  if (revalidation === 'failed') {
    await lease.release();
    throw new Error('state-invalid: target revalidation failed');
  }
  if (revalidation === 'changed') {
    await lease.release();
    return {
      stateKey: stateKey.workflowIdentity,
      phase: plan.phase,
      transition: lease.manifest.transition.kind,
      acceptanceStatus: 'not_accepted',
      acceptanceReason: 'target_changed',
      publicationStatus: 'not_attempted',
      receiptStatus: 'not_written',
      runtimeVersion: lease.result.runtimeVersion,
      traceSha256: lease.traceSha256,
      commentUrl: '',
      stateReason: transitionReason(lease.manifest.transition),
      candidateId: '',
      markerId: '',
      selectorRevision: '',
      sessionEpoch: '',
      stateGeneration: '',
      ledgerEpoch: '',
      cleanupWarnings: '',
    };
  }
  let commentUrl = '';
  let publishedComment: { commentId: string; bodySha256: string } | undefined;
  let acceptedSelectorRevision = '';
  const observingStore = new Proxy(store, {
    get(target, property, receiver) {
      if (property === 'casSelector') {
        return async (...args: Parameters<StateAcceptanceStore['casSelector']>) => {
          const outcome = await target.casSelector(...args);
          if (outcome.kind === 'applied' || outcome.kind === 'already_applied_same_target') {
            acceptedSelectorRevision = outcome.selector.selectorRevision;
          }
          return outcome;
        };
      }
      const value = Reflect.get(target, property, receiver);
      return typeof value === 'function' ? value.bind(target) : value;
    },
  }) as StateAcceptanceStore;
  const acceptance = await acceptLocalCandidate(observingStore, {
    selectionSnapshot: selection.snapshot,
    candidate: lease,
    interactionId: lease.manifest.transaction.interactionId,
    interactionOrdinal: lease.manifest.transaction.interactionOrdinal,
    producingRunId: lease.manifest.provenance.producingRunId,
    producingRunAttempt: lease.manifest.provenance.producingRunAttempt,
    acceptingRunId: String(github.context.runId),
    acceptingRunAttempt: github.context.runAttempt,
    consumedInputSha256: lease.manifest.transaction.consumedInputSha256,
    transition: lease.manifest.transition,
    publishSticky: input.config.postComment
      ? async (markerId) => {
          try {
            const comment = await publishLedgerComment({
              octokit: input.octokit,
              target: input.target,
              structuredReview,
              markerId,
              selectorRevision: acceptedSelectorRevision,
            });
            commentUrl = comment.commentUrl;
            publishedComment = {
              commentId: comment.commentId,
              bodySha256: comment.bodySha256,
            };
          } catch (error) {
            if (error instanceof Error && error.message === 'comment_outcome_unknown') {
              throw new StickyCallbackOutcomeUnknownError();
            }
            throw error;
          }
        }
      : undefined,
  });
  const acceptanceStatus = acceptance.acceptance;
  const acceptanceReason = 'reason' in acceptance ? acceptance.reason : '';
  const publicationStatus = acceptance.publication.status;
  if (acceptance.acceptance === 'unknown') {
    throw new Error('state-invalid: acceptance outcome unknown');
  }
  const receiptStatus =
    acceptance.acceptance === 'accepted' || acceptance.acceptance === 'already_accepted'
      ? receiptOutputStatus(
          await store.writePublicationReceipt({
            markerId: acceptance.markerId,
            stateKey,
            selectorRevision: acceptance.selectorRevision,
            acceptingRunId: String(github.context.runId),
            acceptingRunAttempt: github.context.runAttempt,
            publicationStatus:
              publicationStatus === 'succeeded'
                ? 'succeeded'
                : publicationStatus === 'not_attempted'
                  ? 'not_attempted'
                  : publicationStatus === 'unknown' || publicationStatus === 'pending'
                    ? 'unknown'
                    : 'failed',
            ...(publishedComment ?? {}),
            ...(publicationStatus === 'unknown' || publicationStatus === 'pending'
              ? { failureCode: 'comment_outcome_unknown' as const }
              : {}),
            recordedAt: new Date().toISOString(),
          }),
        )
      : 'not_written';
  if (acceptance.acceptance === 'not_accepted' && !isNeutralNotAccepted(acceptance.reason)) {
    throw new Error(`state-invalid: acceptance ${acceptance.reason}`);
  }
  if (receiptStatus === 'failed') {
    throw new Error('state-invalid: publication receipt write failed');
  }
  if (input.config.postComment && publicationStatus !== 'succeeded') {
    throw new Error(`state-invalid: sticky publication ${publicationStatus}`);
  }
  return {
    stateKey: stateKey.workflowIdentity,
    phase: plan.phase,
    transition: lease.manifest.transition.kind,
    acceptanceStatus,
    acceptanceReason,
    publicationStatus,
    receiptStatus,
    runtimeVersion: lease.result.runtimeVersion,
    traceSha256: lease.traceSha256,
    commentUrl,
    stateReason: transitionReason(lease.manifest.transition),
    candidateId: computeCandidateId({
      manifestSha256: sha256Hex(lease.manifestBytes),
      candidateLedgerSha256: lease.candidateLedgerSha256,
      providerRunMetadataSha256: sha256Hex(lease.providerRunMetadataBytes),
      metadataSemanticSha256: lease.metadataSemanticSha256,
      consumedInputSha256: lease.inputSha256,
      resultSha256: lease.resultSha256,
      traceSha256: lease.traceSha256,
    }),
    markerId:
      acceptance.acceptance === 'accepted' || acceptance.acceptance === 'already_accepted'
        ? acceptance.markerId
        : '',
    selectorRevision:
      acceptance.acceptance === 'accepted' || acceptance.acceptance === 'already_accepted'
        ? acceptance.selectorRevision
        : '',
    sessionEpoch: lease.manifest.sessionEpoch,
    stateGeneration: String(lease.manifest.generation.stateGeneration),
    ledgerEpoch: lease.manifest.generation.ledgerEpoch,
    cleanupWarnings: [...acceptance.cleanupWarnings].sort().join(','),
  };
}

function receiptOutputStatus(
  value: 'created' | 'already_exists_same' | 'failed',
): 'written' | 'failed' {
  return value === 'failed' ? 'failed' : 'written';
}

function isNeutralNotAccepted(reason: string): boolean {
  return reason === 'stale_candidate' || reason === 'selector_cas_rejected';
}

function transitionReason(transition: StateManifestV2Transition): string {
  return 'reason' in transition ? transition.reason : '';
}

async function validateLedgerInvocationEvent(
  config: ActionConfig,
  eventName: string,
  octokit: any,
  target: ReviewTarget,
): Promise<void> {
  const repository = await octokit.rest.repos.get({
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
  });
  const defaultBranch = String(repository.data.default_branch ?? '');
  const fullName = String(
    repository.data.full_name ?? `${github.context.repo.owner}/${github.context.repo.repo}`,
  );
  if (!defaultBranch || !fullName)
    throw new Error('input-invalid: repository default branch unavailable');
  const verification = config.verificationNamespace;
  if (verification === undefined && eventName !== 'workflow_run') {
    throw new Error('input-invalid: production ledger-csharp runs require workflow_run');
  }
  if (verification !== undefined && eventName !== 'workflow_dispatch') {
    throw new Error('input-invalid: verification_namespace requires workflow_dispatch');
  }
  const workflowFile =
    verification === undefined ? 'm4-stateful-review.yml' : 'm4-stateful-verification.yml';
  const expectedWorkflowRef = `${fullName}/.github/workflows/${workflowFile}@refs/heads/${defaultBranch}`;
  if (String(process.env.GITHUB_WORKFLOW_REF ?? '') !== expectedWorkflowRef) {
    throw new Error(
      'input-invalid: ledger-csharp workflow source is not the default-branch trusted workflow',
    );
  }
  if (String(process.env.GITHUB_REF ?? '') !== `refs/heads/${defaultBranch}`) {
    throw new Error('input-invalid: ledger-csharp must execute on the default branch ref');
  }
  if (verification === undefined) {
    const workflowRun = (github.context.payload as any).workflow_run;
    const pullRequests = workflowRun?.pull_requests;
    if (
      workflowRun?.conclusion !== 'success' ||
      !Array.isArray(pullRequests) ||
      pullRequests.length !== 1 ||
      Number(pullRequests[0]?.number) !== target.prNumber ||
      String(workflowRun?.head_repository?.full_name ?? '') !== target.headRepoFullName ||
      String(workflowRun?.event ?? '') !== 'pull_request' ||
      String(workflowRun?.name ?? '') !== 'Agentic PR Review M4 Untrusted Analysis'
    ) {
      throw new Error(
        'input-invalid: workflow_run provenance does not bind exactly one current pull request',
      );
    }
    return;
  }
  const permission = await octokit.rest.repos.getCollaboratorPermissionLevel({
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
    username: github.context.actor,
  });
  if (permission.data.user?.permissions?.admin !== true) {
    throw new Error('input-invalid: verification_namespace requires repository administrator');
  }
}

async function resolveHeadRelationship(
  octokit: any,
  repository: string,
  predecessorHeadSha: string | undefined,
  currentHeadSha: string,
): Promise<'same' | 'descendant' | 'non_descendant' | 'unknown'> {
  if (!predecessorHeadSha || predecessorHeadSha === currentHeadSha) return 'same';
  const [owner, repo] = repository.split('/');
  try {
    const result = await octokit.rest.repos.compareCommits({
      owner,
      repo,
      base: predecessorHeadSha,
      head: currentHeadSha,
    });
    return result.data.status === 'ahead' ? 'descendant' : 'non_descendant';
  } catch {
    return 'unknown';
  }
}

async function targetStillMatches(
  target: ReviewTarget,
  octokit: any,
): Promise<'matching' | 'changed' | 'failed'> {
  if (target.mode !== 'pull-request' || target.prNumber === undefined) return 'failed';
  try {
    const current = await octokit.rest.pulls.get({
      owner: github.context.repo.owner,
      repo: github.context.repo.repo,
      pull_number: target.prNumber,
    });
    return current.data.state === 'open' &&
      String(current.data.base.ref) === target.baseRef &&
      String(current.data.base.sha) === target.baseSha &&
      String(current.data.head.sha) === target.headSha &&
      String(current.data.head.repo?.full_name ?? '') === target.headRepoFullName
      ? 'matching'
      : 'changed';
  } catch {
    return 'failed';
  }
}

export function ledgerStateKey(
  repository: string,
  pullRequest: number,
  verificationNamespace?: string,
): StateKeyV2 {
  const workflowIdentity = verificationNamespace
    ? `${VERIFICATION_WORKFLOW_IDENTITY}/${verificationNamespace}`
    : LEDGER_WORKFLOW_IDENTITY;
  const trustedExecutionDomain = verificationNamespace
    ? `${VERIFICATION_TRUSTED_EXECUTION_DOMAIN}/${verificationNamespace}`
    : LEDGER_TRUSTED_EXECUTION_DOMAIN;
  return {
    namespace: 'm4-ledger-v2',
    repository,
    headRepository: repository,
    pullRequest,
    workflowIdentity,
    trustedExecutionDomain,
  };
}

export function planLedgerInvocation(input: {
  readonly config: ActionConfig;
  readonly target: ReviewTarget;
  readonly stateKey: StateKeyV2;
  readonly selection: Exclude<
    StateSelectionSnapshot,
    { readonly kind: 'explicit_restore_invalid' }
  >;
  readonly eventName: string;
}): {
  readonly phase: Phase;
  readonly previousHeadSha?: string;
  readonly predecessor?: {
    readonly manifestBytes: Uint8Array;
    readonly ledgerBytes: Uint8Array;
    readonly providerRunMetadataBytes: Uint8Array;
  };
  readonly reviewInput: ReturnType<typeof buildReviewInputV1>;
  readonly context: LiveRuntimeInvocationContextV1;
  readonly manifestInput: StateManifestV2Input;
} {
  const predecessor =
    input.selection.kind === 'continuation_selected' || input.selection.kind === 'reset_selected'
      ? input.selection.predecessorBytes
      : undefined;
  const previous = predecessor ? predecessorManifest(predecessor) : undefined;
  const transition = transitionFor(input.selection, previous);
  const phase: Phase = transition.kind === 'continuation' ? 'incremental' : 'bootstrap';
  const reviewInput = buildReviewInputV1({
    target: input.target,
    config: { ...input.config, stateKey: input.stateKey.workflowIdentity },
    phase,
    blocks: [],
    restoredState: null,
    previousFindingFingerprints: [],
    existingCommentFingerprints: [],
    repository: {
      owner: input.stateKey.repository.split('/')[0]!,
      name: input.stateKey.repository.split('/')[1]!,
    },
    requestedRuntimeVersion: null,
  });
  const inputHash = sha256Hex(serializeInputBytes(reviewInput));
  const subjectDigest = computeSubjectDigest(reviewInput.subject);
  const cacheDigest = computeCacheContractDigest(omitContractVersions(cacheContractIdentity));
  if (!subjectDigest.ok || !cacheDigest.ok)
    throw new Error('state-invalid: fixed ledger contract invalid');
  const ordinal =
    transition.kind === 'continuation' ? previous!.transaction.interactionOrdinal + 1 : 0;
  const interaction = deriveInteractionId(
    transition.kind === 'continuation' || transition.kind === 'reset'
      ? { kind: 'ledger', sha256Hex: transition.predecessorLedgerSha256 }
      : { kind: 'bootstrap' },
    inputHash,
    input.target.headSha,
    ordinal,
  );
  if (!interaction.ok) throw new Error('state-invalid: interaction identity invalid');
  const generation = {
    stateGeneration:
      transition.kind === 'continuation' || transition.kind === 'reset'
        ? transition.predecessorStateGeneration + 1
        : 0,
    ledgerEpoch: (transition.kind === 'continuation'
      ? transition.predecessorLedgerEpoch
      : PLACEHOLDER_EPOCH) as never,
  };
  const sessionEpoch =
    transition.kind === 'continuation' || transition.kind === 'reset'
      ? previous!.sessionEpoch
      : (PLACEHOLDER_EPOCH as never);
  const context: LiveRuntimeInvocationContextV1 = {
    schemaVersion: 1 as const,
    stateKey: input.stateKey as unknown as Record<string, unknown>,
    sessionEpoch,
    cacheContractIdentity: cacheContractIdentity as unknown as Record<string, unknown>,
    generation,
    transition,
    currentInteraction: {
      interactionId: interaction.value,
      interactionOrdinal: ordinal,
      consumedInputSha256: inputHash,
      subjectDigest: subjectDigest.value,
      cacheContractDigest: cacheDigest.value,
    },
    cacheContractEnvelopes,
    providerMode: 'synthetic' as const,
    producingRun: {
      producingRunId: String(github.context.runId),
      runAttempt: github.context.runAttempt,
    },
  };
  const manifestInput: StateManifestV2Input = {
    version: 2 as const,
    stateNamespace: 'm4-ledger-v2' as const,
    stateKey: input.stateKey,
    sessionEpoch: sessionEpoch as never,
    cacheContractIdentity: cacheContractIdentity as never,
    generation,
    transition,
    provenance: {
      reviewedHeadSha: input.target.headSha as never,
      reviewedBaseSha: input.target.baseSha as never,
      reviewedBaseRef: canonicalBaseRef(input.target.baseRef),
      currentHeadSha: input.target.headSha as never,
      currentBaseSha: input.target.baseSha as never,
      currentBaseRef: canonicalBaseRef(input.target.baseRef),
      workflowEvent: input.eventName,
      producingRunId: String(github.context.runId),
      producingRunAttempt: github.context.runAttempt,
      producingWorkflowRef: String(process.env.GITHUB_WORKFLOW_REF ?? ''),
      producingGitRef: String(process.env.GITHUB_REF ?? ''),
      producingActionSourceSha: String(process.env.GITHUB_SHA ?? input.target.headSha) as never,
      producedAt: new Date().toISOString(),
    },
    transaction: {
      interactionId: interaction.value as never,
      interactionOrdinal: ordinal,
      consumedInputSha256: inputHash as never,
      resultSha256: '0'.repeat(64) as never,
      traceSha256: '0'.repeat(64) as never,
      metadataSemanticSha256: '0'.repeat(64) as never,
    },
    ledger: { path: 'ledger.json' as const, schemaVersion: 1 as const },
    providerRunMetadata: {
      path: 'provider-run-metadata.json' as const,
      schemaVersion: 1 as const,
      producingGeneration: { sessionEpoch: sessionEpoch as never, ...generation },
    },
  };
  return {
    phase,
    ...(previous ? { previousHeadSha: previous.provenance.currentHeadSha } : {}),
    ...(predecessor ? { predecessor } : {}),
    reviewInput,
    context,
    manifestInput,
  };
}

function transitionFor(
  selection: Exclude<StateSelectionSnapshot, { readonly kind: 'explicit_restore_invalid' }>,
  predecessor: ReturnType<typeof predecessorManifest> | undefined,
): StateManifestV2Transition {
  if (selection.kind === 'bootstrap_selected') {
    return {
      kind: 'bootstrap',
      predecessorManifestSha256: 'bootstrap',
      predecessorLedgerSha256: 'bootstrap',
      reason: 'new_session',
    };
  }
  if (selection.kind === 'recovery_root_selected') {
    return {
      kind: 'recovery_root',
      predecessorManifestSha256: 'bootstrap',
      predecessorLedgerSha256: 'bootstrap',
      reason: selection.recoveryReason,
    };
  }
  if (!predecessor) throw new Error('state-invalid: predecessor manifest missing');
  const previous = predecessor;
  const base = {
    predecessorManifestSha256: sha256Hex(selection.predecessorBytes.manifestBytes) as never,
    predecessorLedgerSha256: sha256Hex(selection.predecessorBytes.ledgerBytes) as never,
    predecessorStateGeneration: previous.generation.stateGeneration,
    predecessorLedgerEpoch: previous.generation.ledgerEpoch,
  } as const;
  return selection.kind === 'continuation_selected'
    ? { kind: 'continuation', ...base }
    : { kind: 'reset', ...base, reason: selection.resetReason };
}

function predecessorManifest(bundle: {
  readonly manifestBytes: Uint8Array;
  readonly ledgerBytes: Uint8Array;
  readonly providerRunMetadataBytes: Uint8Array;
}) {
  const classified = classifyStateBundleV2({
    entryListing: [
      { name: 'manifest.json', isRegularFile: true },
      { name: 'ledger.json', isRegularFile: true },
      { name: 'provider-run-metadata.json', isRegularFile: true },
    ],
    manifestBytes: bundle.manifestBytes,
    ledgerBytes: bundle.ledgerBytes,
    providerRunMetadataBytes: bundle.providerRunMetadataBytes,
  });
  if (classified.kind === 'valid') return classified.manifest;
  throw new Error('state-invalid: accepted predecessor manifest is invalid');
}

async function publishLedgerComment(input: {
  readonly octokit: any;
  readonly target: ReviewTarget;
  readonly structuredReview: StructuredReviewEnvelopeV1;
  readonly markerId: string;
  readonly selectorRevision: string;
}): Promise<{ commentUrl: string; commentId: string; bodySha256: string }> {
  if (!/^sha256:[a-f0-9]{64}$/.test(input.selectorRevision)) {
    throw new Error('publication_observation_invalid');
  }
  return upsertM4StateComment({
    octokit: input.octokit,
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
    prNumber: input.target.prNumber!,
    structuredReview: input.structuredReview,
    markerId: input.markerId,
    selectorRevision: input.selectorRevision,
  });
}

function omitContractVersions<T extends typeof cacheContractIdentity>(identity: T) {
  const {
    ledgerSchemaVersion: _ledgerSchemaVersion,
    prefixContractVersion: _prefixContractVersion,
    ...rest
  } = identity;
  return rest;
}

function canonicalBaseRef(value: string): string {
  return value.startsWith('refs/') ? value : `refs/heads/${value}`;
}

function emptyLineageTotals() {
  return {
    usage: {
      cacheReadInputTokens: 0,
      cacheCreationInputTokens: 0,
      inputTokens: 0,
      outputTokens: 0,
    },
    observedTurns: null,
    source: 'current_run_only' as const,
    partial: false,
  };
}
