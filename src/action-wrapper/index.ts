import { DefaultArtifactClient } from '@actions/artifact';
import { getOctokit } from '@actions/github';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { readBoundedFile } from '../../scripts/release/build-payload.format.mjs';
import { resolveInstalledManagedActionSource } from './launcher/action-source.js';
import {
  parseManagedPayloadMap,
  MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES,
} from './launcher/payload-map.js';
import { resolveManagedReleasePayload } from './launcher/release-payload.js';
import { acquireInPayloadStaging, type VerifiedManagedTree } from './launcher/payload-staging.js';
import {
  acquireRuntimePrerequisite,
  RuntimePrerequisiteTerminationUnconfirmedError,
} from './launcher/runtime-prerequisite.js';
import { ManagedRuntimeCleanupError } from './launcher/managed-runtime.js';
import { parseStrictJson } from './launcher/strict-json.js';

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
  type PreparedPayloadIdentity,
  validateRuntimeFacts,
} from './launcher/contracts.js';
import {
  canonicalStateReconciliationDiagnostic,
  HostProcessTerminationUnconfirmedError,
  ManagedHostProcessTerminationUnconfirmedError,
  runHostProcess,
  runManagedHostProcess,
  type HostProcessRequest,
  type HostProcessResult,
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
  type VerifiedPreparedPayload,
} from './launcher/prepared-payload.js';
import { ActionWrapperContractError, fail } from './launcher/validation.js';
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
  /** Passed only after the selected native or managed payload admission succeeds. */
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
  return runActionWrapperWithSeams({
    ...seams,
    acquirePayload: async () => {
      const prepared = await verifyPreparedPayload(seams.preparedPayload);
      return { ...prepared, dispose: () => prepared.executableHandle.close() };
    },
  });
}

export interface ActionWrapperSeams extends Omit<PrivateActionWrapperSeams, 'preparedPayload'> {
  readonly acquirePayload: () => Promise<VerifiedPreparedPayload & { dispose(): Promise<void> }>;
}

export interface ManagedActionPayload extends PreparedPayloadIdentity {
  readonly tree: VerifiedManagedTree;
  readonly trustedRuntimeRoot: string;
  readonly runnerStorage: { readonly runnerTemp: string; readonly workspace: string };
  dispose(): Promise<void>;
}

export interface ManagedActionWrapperSeams extends Omit<
  PrivateActionWrapperSeams,
  'preparedPayload' | 'hostProcessRunner'
> {
  readonly acquirePayload: () => Promise<ManagedActionPayload>;
  readonly hostProcessRunner: typeof runManagedHostProcess;
  /** Keeps the production cancellation owner alive while retained resources settle. */
  readonly onRetainedResources?: (settlement: Promise<void>) => void;
}

type CommonHostRequest = Omit<HostProcessRequest, 'executableHandle'>;
interface OwnedActionPayload extends PreparedPayloadIdentity {
  readonly kind: 'native' | 'managed';
  readonly runnerStorage?: ManagedActionPayload['runnerStorage'];
  launch(request: CommonHostRequest): Promise<HostProcessResult>;
  dispose(): Promise<void>;
}
interface OwnedActionWrapperSeams extends Omit<
  ActionWrapperSeams,
  'acquirePayload' | 'hostProcessRunner'
> {
  readonly acquirePayload: () => Promise<OwnedActionPayload>;
  readonly onRetainedResources?: (settlement: Promise<void>) => void;
}

/** Native prepared consumers retain their real handle and existing fatal policy. */
export async function runActionWrapperWithSeams(seams: ActionWrapperSeams): Promise<number> {
  return runOwnedActionWrapperWithSeams({
    ...seams,
    acquirePayload: async () => {
      const payload = await seams.acquirePayload();
      return {
        ...payload,
        kind: 'native',
        launch: (request) =>
          seams.hostProcessRunner({ ...request, executableHandle: payload.executableHandle }),
      };
    },
  });
}

/** Repository lifecycle seam; production always acquires the real tree and pinned runtime below. */
export async function runManagedActionWrapperWithSeams(
  seams: ManagedActionWrapperSeams,
): Promise<number> {
  return runOwnedActionWrapperWithSeams({
    ...seams,
    acquirePayload: async () => {
      const payload = await seams.acquirePayload();
      return {
        ...payload,
        kind: 'managed',
        launch: (request) =>
          seams.hostProcessRunner({
            ...request,
            tree: payload.tree,
            trustedRuntimeRoot: payload.trustedRuntimeRoot,
            workspace: payload.runnerStorage.workspace,
          }),
      };
    },
  });
}

/** Ordinary entry owns acquisition; it exposes no prepared-proof or transport selector. */
export async function runProductionActionWrapper(
  actionRootUrl: URL,
  wrapperBuildDiscriminator: string,
): Promise<number> {
  const termination = createTerminationSignal();
  let retainedResources: Promise<void> | undefined;
  try {
    return await runManagedActionWrapperWithSeams({
      toolkit: createActionsToolkit(),
      platform: process.platform,
      signal: termination.signal,
      runtimeFacts: readProductionRuntimeFacts,
      hostProcessRunner: runManagedHostProcess,
      onRetainedResources: (settlement) => {
        retainedResources = settlement;
      },
      bridgeRuntime: startArtifactBridgeRuntime,
      createArtifactExecutor: createProductionArtifactExecutor,
      fatalExit: (code) => process.exit(code),
      acquirePayload: async () => {
        if (wrapperBuildDiscriminator !== 'r7-d0') fail('wrapper_payload_invalid');
        const actionRoot = fileURLToPath(actionRootUrl);
        const mapBytes = await readBoundedFile(
          path.join(actionRoot, 'payload-map.json'),
          MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES,
        );
        if (parseStrictJson(mapBytes, MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES) === null)
          fail('wrapper_payload_map_unbound');
        const map = parseManagedPayloadMap(mapBytes);
        const signal = AbortSignal.any([termination.signal, AbortSignal.timeout(60_000)]);
        const runnerStorage = {
          runnerTemp: required(process.env.RUNNER_TEMP),
          workspace: required(process.env.GITHUB_WORKSPACE),
        };
        // Return the original registered managed payload through the staging owner first.
        const payload = await acquireInPayloadStaging({
          ...runnerStorage,
          acquire: async (stagingParent) => {
            const actionSourceSha = await resolveInstalledManagedActionSource({
              actionRoot,
              mapBytes,
              actionRepository: process.env.GITHUB_ACTION_REPOSITORY,
              actionRef: process.env.GITHUB_ACTION_REF,
              signal,
            });
            return resolveManagedReleasePayload({
              mapBytes,
              actionSourceSha,
              stagingParent,
              signal,
              workspace: runnerStorage.workspace,
            });
          },
        });
        try {
          const runtime = await acquireRuntimePrerequisite({
            ...runnerStorage,
            signal: termination.signal,
          });
          return {
            tree: payload.tree,
            actionSourceSha: payload.actionSourceSha,
            payloadSha256: map.payload.archiveSha256,
            buildDiscriminator: wrapperBuildDiscriminator,
            trustedRuntimeRoot: runtime.trustedRuntimeRoot,
            runnerStorage,
            dispose: async () => {
              const results = await Promise.allSettled([payload.dispose(), runtime.dispose()]);
              const failures = results.flatMap((result) =>
                result.status === 'rejected' ? [result.reason] : [],
              );
              if (failures.length > 0)
                throw new AggregateError(failures, 'wrapper_owned_resource_cleanup_failed');
            },
          };
        } catch (error) {
          if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError)
            retainedResources = error.resourcesReleased;
          try {
            await payload.dispose();
          } catch (cleanupError) {
            if (error instanceof Error)
              Object.defineProperty(error, 'payloadCleanupError', { value: cleanupError });
          }
          throw error;
        }
      },
    });
  } finally {
    if (retainedResources) {
      void retainedResources.then(termination.dispose, termination.dispose);
    } else {
      termination.dispose();
    }
  }
}

async function runOwnedActionWrapperWithSeams(seams: OwnedActionWrapperSeams): Promise<number> {
  let bridge: ArtifactBridgeRuntime | undefined;
  let tracker: OfficialCallTracker | undefined;
  let completion: ActionHostCompletionDocument | undefined;
  let artifactRestRequestBudget: ArtifactRestRequestBudget | undefined;
  let hostBudgetReceiptLines: readonly string[] = [];
  let hostStateReconciliationDiagnosticLine: string | undefined;
  let failed = false;
  let hostTerminationUnconfirmed = false;
  let managedTerminationUnconfirmed: HostProcessTerminationUnconfirmedError | undefined;
  let prerequisiteTerminationUnconfirmed:
    | RuntimePrerequisiteTerminationUnconfirmedError
    | undefined;
  let managedResourcesRetained = false;
  let prepared: OwnedActionPayload | undefined;
  let unboundMap = false;
  const inputs = (() => {
    try {
      return readAndMaskActionInputs(seams.toolkit);
    } catch {
      return undefined;
    }
  })();
  if (!inputs) {
    await presentFixedWrapperFailure(seams.toolkit);
    return 1;
  }
  if (seams.platform !== 'linux') {
    await presentFixedWrapperFailure(seams.toolkit);
    return 1;
  }
  try {
    const runtimeFacts = seams.runtimeFacts();
    validateRuntimeFacts(runtimeFacts);
    if (seams.signal.aborted) fail('wrapper_cancelled_before_spawn');
    prepared = await seams.acquirePayload();
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
    const eventJsonSha256 = await digestEventJson(runtimeFacts.eventJsonPath);
    tracker = new OfficialCallTracker();
    bridge = await seams.bridgeRuntime({
      buildDiscriminator: prepared.buildDiscriminator,
      ...(prepared.kind === 'managed' ? { runnerStorage: prepared.runnerStorage! } : {}),
      executorFactory: async (stagingRoot) =>
        await seams.createArtifactExecutor(
          {
            githubToken: inputs.find((entry) => entry.name === 'github-token')?.value ?? null,
            repositoryName: runtimeFacts.repositoryName,
            runId: runtimeFacts.runId,
            runAttempt: runtimeFacts.runAttempt,
            stagingRoot,
            verifiedPreparedPayload: prepared!,
            artifactRestRequestBudget: artifactRestRequestBudget!,
          },
          tracker!,
        ),
    });
    if (prepared.kind === 'managed' && typeof bridge.sealAdmission !== 'function')
      fail('wrapper_bridge_invalid');
    const launch = buildLaunchDocument({
      inputs,
      runtimeFacts,
      eventJsonSha256,
      prepared,
      artifactBridgeEndpoint: bridge.endpoint,
      cancellation: 'active',
    });
    if (seams.signal.aborted) fail('wrapper_cancelled_before_spawn');
    const host = await prepared.launch({
      launchBytes: serializeLaunchDocument(launch),
      tempRoot: bridge.tempRoot,
      signal: seams.signal,
      ...(requestBudgetProfile === undefined ? {} : { requestBudgetProfile }),
    });
    hostBudgetReceiptLines = host.trustedProofBudgetReceiptLines;
    hostStateReconciliationDiagnosticLine = host.trustedProofStateReconciliationDiagnosticLine;
    completion = parseCompletionDocument(
      host.completionBytes,
      prepared.buildDiscriminator,
      host.exitCode,
    );
  } catch (error) {
    if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError) {
      prerequisiteTerminationUnconfirmed = error;
    } else if (error instanceof HostProcessTerminationUnconfirmedError) {
      if (prepared?.kind === 'managed') {
        managedTerminationUnconfirmed = error;
        // Revoke new effects now; admitted work and its storage retain their own barriers.
        bridge!.sealAdmission!();
      } else {
        hostTerminationUnconfirmed = true;
      }
    } else {
      failed = true;
      if (prepared?.kind === 'managed') {
        const cleanupError =
          error instanceof ManagedRuntimeCleanupError
            ? error
            : error instanceof Error && 'cleanupError' in error
              ? error.cleanupError
              : undefined;
        if (cleanupError !== undefined) {
          // Actual Host close is not evidence that its runtime guards released.
          managedResourcesRetained = true;
          if (cleanupError instanceof ManagedRuntimeCleanupError) {
            try {
              await cleanupError.retryCleanup();
              managedResourcesRetained = false;
            } catch {
              /* Retain storage whose guard release still failed. */
            }
          }
        }
      }
      unboundMap =
        error instanceof ActionWrapperContractError && error.code === 'wrapper_payload_map_unbound';
    }
  } finally {
    try {
      if (prepared?.kind !== 'managed') await prepared?.dispose();
    } catch {
      failed = true;
    }
  }

  if (prerequisiteTerminationUnconfirmed) {
    const settlement = prerequisiteTerminationUnconfirmed.resourcesReleased;
    void settlement.catch(() => {});
    seams.onRetainedResources?.(settlement);
    return 1;
  }

  if (managedTerminationUnconfirmed && prepared && bridge && tracker) {
    // No presentation or immediate process.exit can bypass the actual-child retention.
    const settlement = retainManagedResources(
      managedTerminationUnconfirmed,
      prepared,
      bridge,
      tracker,
      seams.officialQuiescenceTimeoutMs ?? OFFICIAL_QUIESCENCE_TIMEOUT_MS,
      seams.fatalExit,
    );
    void settlement.catch(() => {});
    seams.onRetainedResources?.(settlement);
    return 1;
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
        try {
          writeTrustedProofBudgetReceiptFrame(
            artifactRestRequestBudget,
            hostBudgetReceiptLines,
            hostStateReconciliationDiagnosticLine,
            seams.trustedProofBudgetReceiptSink,
          );
        } catch {
          failed = true;
        }
      }
    } catch {
      failed = true;
    } finally {
      try {
        if (quiet || prepared?.kind !== 'managed') await bridge.cleanup();
      } catch {
        failed = true;
      }
    }
    if (!quiet) {
      if (prepared?.kind === 'managed' && !managedResourcesRetained)
        await prepared.dispose().catch(() => {});
      seams.fatalExit(1);
      return 1;
    }
  }

  if (prepared?.kind === 'managed' && !managedResourcesRetained) {
    try {
      await prepared.dispose();
    } catch {
      failed = true;
    }
  }

  if (failed || !completion) {
    if (unboundMap) {
      try {
        seams.toolkit.error('No release payload has been authorized for this Action.');
      } catch {
        /* Fixed sinks remain independent. */
      }
    }
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

async function retainManagedResources(
  error: HostProcessTerminationUnconfirmedError,
  payload: OwnedActionPayload,
  bridge: ArtifactBridgeRuntime,
  tracker: OfficialCallTracker,
  quiescenceMs: number,
  fatalExit: (code: 1) => void,
): Promise<void> {
  if (!error.terminationConfirmed) return await new Promise<void>(() => {});
  let officialQuiescenceFailed = false;
  const bridgeSettlement = error.terminationConfirmed.then(async () => {
    let quiet = false;
    try {
      await bridge.stopAndDrain();
      quiet = await tracker.awaitQuiescence(quiescenceMs);
    } catch {
      /* Unsettled official work never authorizes normal presentation. */
    }
    if (!quiet) {
      officialQuiescenceFailed = true;
      throw new Error('wrapper_official_quiescence_failed');
    }
    await bridge.cleanup();
  });
  const payloadSettlement =
    error instanceof ManagedHostProcessTerminationUnconfirmedError
      ? error.terminationConfirmed.then(async () => {
          try {
            await error.resourcesReleased;
          } catch (cleanupError) {
            if (!(cleanupError instanceof ManagedRuntimeCleanupError)) throw cleanupError;
            await cleanupError.retryCleanup();
          }
          await payload.dispose();
        })
      : new Promise<void>(() => {});
  const results = await Promise.allSettled([bridgeSettlement, payloadSettlement]);
  // A bridge failure cannot bypass the independent guard release/retry or owner disposal.
  // Rejected owner settlement retains its storage; it does not authorize presentation.
  if (officialQuiescenceFailed) fatalExit(1);
  if (results.some((result) => result.status === 'rejected'))
    throw new AggregateError(
      results.flatMap((result) => (result.status === 'rejected' ? [result.reason] : [])),
      'wrapper_retained_resource_cleanup_failed',
    );
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
  diagnosticLine: string | undefined,
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
  let diagnostic = '';
  if (
    diagnosticLine?.endsWith('\n') &&
    diagnosticLine.indexOf('\n') === diagnosticLine.length - 1
  ) {
    const canonical = canonicalStateReconciliationDiagnostic(diagnosticLine.slice(0, -1));
    if (canonical !== undefined && `${canonical}\n` === diagnosticLine) {
      diagnostic = diagnosticLine;
    }
  }
  (sink ?? writeTrustedProofArtifactRestBudgetToStderr)(lines.join('') + artifact + diagnostic);
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
