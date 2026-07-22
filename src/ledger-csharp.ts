import * as github from '@actions/github';
import { capStructuredReviewForMarkdownLimit, upsertLineageComment } from './comments.js';
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
  GitHubGitStateAcceptanceStore,
  OctokitGitDataClient,
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
  readonly receiptStatus: 'created' | 'already_exists_same' | 'failed' | 'not_attempted';
  readonly runtimeVersion: string;
  readonly traceSha256: string;
  readonly commentUrl: string;
}

export async function runLedgerCsharp(input: {
  readonly config: ActionConfig;
  readonly target: ReviewTarget;
  readonly octokit: any;
  readonly eventName: string;
  readonly defaultBranchCommitSha: string;
}): Promise<LedgerRunResult> {
  await validateLedgerInvocationEvent(input.config, input.eventName, input.octokit);
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
    headRelationship: 'same',
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
  let commentUrl = '';
  const acceptance = await acceptLocalCandidate(store, {
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
      ? async () => {
          const comment = await publishLedgerComment({
            octokit: input.octokit,
            target: input.target,
            stateKey: stateKey.workflowIdentity,
            phase: plan.phase,
            structuredReview,
            runId: github.context.runId,
            runAttempt: github.context.runAttempt,
            previousHeadSha: plan.previousHeadSha,
          });
          commentUrl = comment;
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
      ? await store.writePublicationReceipt({
          markerId: acceptance.markerId,
          runId: String(github.context.runId),
          runAttempt: github.context.runAttempt,
          publicationStatus,
          commentUrl,
        })
      : 'not_attempted';
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
  };
}

async function validateLedgerInvocationEvent(
  config: ActionConfig,
  eventName: string,
  octokit: any,
): Promise<void> {
  const verification = config.verificationNamespace;
  if (verification === undefined && eventName !== 'workflow_run') {
    throw new Error('input-invalid: production ledger-csharp runs require workflow_run');
  }
  if (verification !== undefined && eventName !== 'workflow_dispatch') {
    throw new Error('input-invalid: verification_namespace requires workflow_dispatch');
  }
  if (verification === undefined) return;
  const permission = await octokit.rest.repos.getCollaboratorPermissionLevel({
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
    username: github.context.actor,
  });
  if (permission.data.user?.permissions?.admin !== true) {
    throw new Error('input-invalid: verification_namespace requires repository administrator');
  }
}

export function ledgerStateKey(
  repository: string,
  pullRequest: number,
  verificationNamespace?: string,
): StateKeyV2 {
  const suffix = verificationNamespace ? `/verification/${verificationNamespace}` : '';
  return {
    namespace: 'm4-ledger-v2',
    repository,
    headRepository: repository,
    pullRequest,
    workflowIdentity: `${LEDGER_WORKFLOW_IDENTITY}${suffix}`,
    trustedExecutionDomain: `${LEDGER_WORKFLOW_IDENTITY}${suffix}`,
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
  readonly stateKey: string;
  readonly phase: Phase;
  readonly structuredReview: StructuredReviewEnvelopeV1;
  readonly runId: number;
  readonly runAttempt: number;
  readonly previousHeadSha?: string;
}): Promise<string> {
  const result = await upsertLineageComment({
    octokit: input.octokit,
    owner: github.context.repo.owner,
    repo: github.context.repo.repo,
    prNumber: input.target.prNumber!,
    target: input.target,
    structuredReview: input.structuredReview,
    stateKey: input.stateKey,
    phase: input.phase,
    runtimeProvider: 'test',
    runtimeBackend: 'ledger-csharp',
    sessionId: `m4:${input.target.prNumber}`,
    previousHeadSha: input.previousHeadSha,
    currentHeadSha: input.target.headSha,
    artifactName: 'git-data:m4-state-v1',
    runId: input.runId,
    runAttempt: input.runAttempt,
    lineageReason: input.phase === 'bootstrap' ? 'manual_bootstrap' : 'continuity_mismatch',
    usage: null,
    observedTurns: null,
    maxReviewChars: 12000,
    lineageTotals: emptyLineageTotals(),
  });
  return result.commentUrl;
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
