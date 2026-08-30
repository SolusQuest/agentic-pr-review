import { DefaultArtifactClient } from '@actions/artifact';
import { getOctokit } from '@actions/github';

import {
  ArtifactBridgeStaging,
  ArtifactCacheLedger,
  ArtifactRestRequestBudget,
  createArtifactActionsRestClient,
  OfficialArtifactOperations,
  type ArtifactBridgeExecutor,
} from './artifact-bridge/index.js';
import {
  startArtifactBridgeRuntime,
  type ArtifactBridgeRuntime,
} from './launcher/bridge-runtime.js';
import {
  buildLaunchDocument,
  parseProductionWorkflowRef,
  serializeLaunchDocument,
  type ActionRuntimeFacts,
  validateRuntimeFacts,
} from './launcher/contracts.js';
import {
  HostProcessTerminationUnconfirmedError,
  runHostProcess,
  type HostProcessRunner,
} from './launcher/host-process.js';
import { readAndMaskActionInputs } from './launcher/inputs.js';
import { OfficialCallTracker } from './launcher/official-calls.js';
import {
  artifactRestRequestBudgetProfile,
  readTrustedProofRequestBudgetProfile,
} from './launcher/request-budget-profile.js';
import {
  digestEventJson,
  verifyPreparedPayload,
  type PreparedPayloadProof,
} from './launcher/prepared-payload.js';
import { fail } from './launcher/validation.js';
import {
  parseCompletionDocument,
  type ActionHostCompletionDocument,
} from './presentation/completion.js';
import {
  createActionsToolkit,
  presentCompletion,
  presentFixedWrapperFailure,
  type ActionPresentationToolkit,
} from './presentation/toolkit.js';

const OFFICIAL_QUIESCENCE_TIMEOUT_MS = 30_000;
const GITHUB_REQUEST_BUDGET_PREFIX = 'APR_R4_E2P_GITHUB_REQUEST_BUDGET ';
const CONTROL_REQUEST_BUDGET_PREFIX = 'APR_R4_E2P_CONTROL_REQUEST_BUDGET ';

export interface PrivateActionWrapperSeams {
  readonly toolkit: ActionPresentationToolkit;
  readonly preparedPayload: PreparedPayloadProof;
  readonly platform: NodeJS.Platform;
  readonly signal: AbortSignal;
  readonly runtimeFacts: () => ActionRuntimeFacts;
  readonly hostProcessRunner: HostProcessRunner;
  readonly bridgeRuntime: typeof startArtifactBridgeRuntime;
  readonly createArtifactExecutor: (
    context: ProductionArtifactExecutorContext,
    tracker: OfficialCallTracker,
  ) => Promise<ArtifactBridgeExecutor>;
  /**
   * Receives one complete, stable, secret-free receipt frame. Production writes
   * the frame once to stderr, so a failing sink cannot publish a prefix of the
   * Host/control/artifact evidence set.
   */
  readonly trustedProofBudgetReceiptSink?: (frame: string) => void;
  readonly fatalExit: (code: 1) => void;
  readonly officialQuiescenceTimeoutMs?: number;
}

export interface ProductionArtifactExecutorContext {
  readonly githubToken: string | null;
  readonly repositoryName: string;
  readonly runId: string;
  readonly runAttempt: string;
  readonly stagingRoot: string;
  /** Passed only after verifyPreparedPayload admits the exact payload receipt. */
  readonly verifiedPreparedPayload: {
    readonly buildDiscriminator: string;
  };
  /** Created once from the verified prepared payload and shared by the bridge process. */
  readonly artifactRestRequestBudget: ArtifactRestRequestBudget;
}

export async function runPrivateActionWrapper(
  preparedPayload: PreparedPayloadProof,
): Promise<number> {
  const termination = createTerminationSignal();
  try {
    return await runPrivateActionWrapperWithSeams({
      toolkit: createActionsToolkit(),
      preparedPayload,
      platform: process.platform,
      signal: termination.signal,
      runtimeFacts: readProductionRuntimeFacts,
      hostProcessRunner: runHostProcess,
      bridgeRuntime: startArtifactBridgeRuntime,
      createArtifactExecutor: createProductionArtifactExecutor,
      fatalExit: (code) => process.exit(code),
    });
  } finally {
    termination.dispose();
  }
}

export async function runPrivateActionWrapperWithSeams(
  seams: PrivateActionWrapperSeams,
): Promise<number> {
  let bridge: ArtifactBridgeRuntime | undefined;
  let tracker: OfficialCallTracker | undefined;
  let completion: ActionHostCompletionDocument | undefined;
  let artifactRestRequestBudget: ArtifactRestRequestBudget | undefined;
  let hostBudgetReceiptLines: readonly string[] = [];
  let failed = false;
  let hostTerminationUnconfirmed = false;
  const inputs = (() => {
    try {
      return readAndMaskActionInputs(seams.toolkit);
    } catch {
      return undefined;
    }
  })();
  if (!inputs) return 1;
  if (seams.platform !== 'linux') {
    await presentFixedWrapperFailure(seams.toolkit);
    return 1;
  }
  try {
    const runtimeFacts = seams.runtimeFacts();
    validateRuntimeFacts(runtimeFacts);
    if (seams.signal.aborted) fail('wrapper_cancelled_before_spawn');
    const prepared = await verifyPreparedPayload(seams.preparedPayload);
    // This profile is an explicit, protected-process capability. It is never
    // inferred from an action input and ordinary r4-h1 payloads receive none.
    const requestBudgetProfile = readTrustedProofRequestBudgetProfile(prepared.buildDiscriminator);
    // This is intentionally derived from the verified payload, never from an
    // action input or bridge command. The one instance is then shared by every
    // authenticated artifact REST command in this wrapper process.
    artifactRestRequestBudget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
      buildDiscriminator: prepared.buildDiscriminator,
      identity: {
        repository: runtimeFacts.repositoryName,
        repositoryId: runtimeFacts.repositoryId,
        workflowSha: runtimeFacts.workflowSha,
        actionSourceSha: prepared.actionSourceSha,
        payloadSha256: prepared.payloadSha256,
        buildDiscriminator: prepared.buildDiscriminator,
        runId: runtimeFacts.runId,
        runAttempt: runtimeFacts.runAttempt,
      },
      ...(requestBudgetProfile === undefined
        ? {}
        : { profile: artifactRestRequestBudgetProfile(requestBudgetProfile) }),
    });
    try {
      const eventJsonSha256 = await digestEventJson(runtimeFacts.eventJsonPath);
      tracker = new OfficialCallTracker();
      bridge = await seams.bridgeRuntime({
        buildDiscriminator: prepared.buildDiscriminator,
        executorFactory: async (stagingRoot) =>
          await seams.createArtifactExecutor(
            {
              githubToken: inputs.github_token,
              repositoryName: runtimeFacts.repositoryName,
              runId: runtimeFacts.runId,
              runAttempt: runtimeFacts.runAttempt,
              stagingRoot,
              verifiedPreparedPayload: prepared,
              artifactRestRequestBudget: artifactRestRequestBudget!,
            },
            tracker!,
          ),
      });
      const launch = buildLaunchDocument({
        inputs,
        runtimeFacts,
        eventJsonSha256,
        prepared,
        artifactBridgeEndpoint: bridge.endpoint,
        cancellation: 'active',
      });
      if (seams.signal.aborted) fail('wrapper_cancelled_before_spawn');
      const host = await seams.hostProcessRunner({
        executableHandle: prepared.executableHandle,
        launchBytes: serializeLaunchDocument(launch),
        tempRoot: bridge.tempRoot,
        signal: seams.signal,
        ...(requestBudgetProfile === undefined ? {} : { requestBudgetProfile }),
      });
      hostBudgetReceiptLines = host.trustedProofBudgetReceiptLines;
      completion = parseCompletionDocument(
        host.completionBytes,
        prepared.buildDiscriminator,
        host.exitCode,
      );
    } finally {
      await prepared.executableHandle.close();
    }
  } catch (error) {
    if (error instanceof HostProcessTerminationUnconfirmedError) {
      hostTerminationUnconfirmed = true;
    } else {
      failed = true;
    }
  }

  if (hostTerminationUnconfirmed) {
    if (bridge && tracker) {
      try {
        await bridge.stopAndDrain();
        await tracker.awaitQuiescence(
          seams.officialQuiescenceTimeoutMs ?? OFFICIAL_QUIESCENCE_TIMEOUT_MS,
        );
      } catch {
        // Fatal termination still receives one best-effort cleanup attempt.
      } finally {
        try {
          await bridge.cleanup();
        } catch {
          // The fatal exit below remains authoritative.
        }
      }
    }
    seams.fatalExit(1);
    return 1;
  }

  if (bridge && tracker) {
    let quiet = false;
    try {
      await bridge.stopAndDrain();
      quiet = await tracker.awaitQuiescence(
        seams.officialQuiescenceTimeoutMs ?? OFFICIAL_QUIESCENCE_TIMEOUT_MS,
      );
    } catch {
      quiet = false;
    }
    try {
      if (quiet) {
        writeTrustedProofBudgetReceiptFrame(
          artifactRestRequestBudget,
          hostBudgetReceiptLines,
          seams.trustedProofBudgetReceiptSink,
        );
      }
    } catch {
      failed = true;
    } finally {
      try {
        await bridge.cleanup();
      } catch {
        failed = true;
      }
    }
    if (!quiet) {
      seams.fatalExit(1);
      return 1;
    }
  }

  if (failed || !completion) {
    await presentFixedWrapperFailure(seams.toolkit);
    return 1;
  }
  try {
    await presentCompletion(seams.toolkit, completion);
    return completion.process_exit_code;
  } catch {
    return 1;
  }
}

export function readProductionRuntimeFacts(): ActionRuntimeFacts {
  const eventJsonPath = required(process.env.GITHUB_EVENT_PATH);
  const repositoryName = required(process.env.GITHUB_REPOSITORY);
  const repositoryId = required(process.env.GITHUB_REPOSITORY_ID);
  const runId = required(process.env.GITHUB_RUN_ID);
  const runAttempt = required(process.env.GITHUB_RUN_ATTEMPT);
  const sourceWorkflowRef = required(process.env.GITHUB_WORKFLOW_REF);
  const workflowSha = required(process.env.GITHUB_WORKFLOW_SHA);
  const parsed = parseProductionWorkflowRef(repositoryName, sourceWorkflowRef);
  return {
    eventJsonPath,
    repositoryName,
    repositoryId,
    runId,
    runAttempt,
    workflowPath: parsed.workflowPath,
    workflowRef: parsed.workflowRef,
    workflowSha,
  };
}

export async function createProductionArtifactExecutor(
  context: ProductionArtifactExecutorContext,
  tracker: OfficialCallTracker,
): Promise<ArtifactBridgeExecutor> {
  if (!context.githubToken) fail('wrapper_artifact_credentials_unavailable');
  const separator = context.repositoryName.indexOf('/');
  if (separator <= 0 || separator === context.repositoryName.length - 1) {
    fail('wrapper_artifact_context_invalid');
  }
  const staging = await ArtifactBridgeStaging.create(context.stagingRoot);
  const octokit = getOctokit(context.githubToken);
  // Conditional REST representations and decrypted verified records share one
  // bounded ledger; neither cache receives an independent 64 MiB allowance.
  const cacheLedger = new ArtifactCacheLedger();
  const actions = tracker.wrap(
    createArtifactActionsRestClient(
      octokit,
      context.artifactRestRequestBudget,
      undefined,
      cacheLedger,
    ),
  );
  const artifactClient = tracker.wrap(new DefaultArtifactClient());
  return new OfficialArtifactOperations({
    owner: context.repositoryName.slice(0, separator),
    repository: context.repositoryName.slice(separator + 1),
    currentRunId: context.runId,
    currentRunAttempt: context.runAttempt,
    artifactClient,
    actions,
    staging,
    cacheLedger,
    artifactRestRequestBudget: context.artifactRestRequestBudget,
  });
}

function writeTrustedProofBudgetReceiptFrame(
  budget: ArtifactRestRequestBudget | undefined,
  lines: readonly string[],
  sink: ((frame: string) => void) | undefined,
): void {
  // The verified R4 payload discriminator is the authority for all three
  // proof-budget records. Ordinary payloads cannot enable this route with
  // action inputs or Host stderr text.
  if (!budget?.protectedRoute) return;
  if (
    lines.length !== 2 ||
    !lines[0]?.startsWith(GITHUB_REQUEST_BUDGET_PREFIX) ||
    !lines[0].endsWith('\n') ||
    !lines[1]?.startsWith(CONTROL_REQUEST_BUDGET_PREFIX) ||
    !lines[1].endsWith('\n')
  ) {
    throw new Error('trusted_proof_budget_receipt_frame_invalid');
  }
  const artifact = budget.sealAndCreateReceipt();
  if (!artifact) throw new Error('trusted_proof_budget_receipt_frame_invalid');
  (sink ?? writeTrustedProofArtifactRestBudgetToStderr)(lines.join('') + artifact);
}

function writeTrustedProofArtifactRestBudgetToStderr(frame: string): void {
  process.stderr.write(frame);
}

function required(value: string | undefined): string {
  if (value === undefined || value.length === 0) fail('wrapper_runtime_facts_invalid');
  return value;
}

export function createTerminationSignal(): {
  readonly signal: AbortSignal;
  readonly dispose: () => void;
} {
  const controller = new AbortController();
  const abort = (): void => controller.abort();
  process.on('SIGTERM', abort);
  process.on('SIGINT', abort);
  return {
    signal: controller.signal,
    dispose: () => {
      process.off('SIGTERM', abort);
      process.off('SIGINT', abort);
    },
  };
}
