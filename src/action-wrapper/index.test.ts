import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import {
  access,
  chmod,
  mkdir,
  mkdtemp,
  readFile,
  rm,
  writeFile,
  type FileHandle,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ArtifactBridgeExecutor } from './artifact-bridge/index.js';
import {
  ArtifactRestRequestBudget,
  TRUSTED_PROOF_ARTIFACT_REST_REQUEST_RECEIPT_PREFIX,
} from './artifact-bridge/artifact-rest-request-budget.js';
import {
  createProductionArtifactExecutor,
  readProductionRuntimeFacts,
  runPrivateActionWrapperWithSeams,
} from './index.js';
import { parseLaunchDocument, type ActionRuntimeFacts } from './launcher/contracts.js';
import { HostProcessTerminationUnconfirmedError } from './launcher/host-process.js';
import { OfficialCallTracker } from './launcher/official-calls.js';
import type { PreparedPayloadProof } from './launcher/prepared-payload.js';
import {
  R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE,
  readTrustedProofRequestBudgetProfile,
} from './launcher/request-budget-profile.js';
import type { ActionPresentationToolkit } from './presentation/toolkit.js';

const roots: string[] = [];
const fullWorkflowRef =
  'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main';
const githubBudgetReceipt =
  'APR_R4_E2P_GITHUB_REQUEST_BUDGET {"authenticated_rest_requests":180,"authenticated_rest_limit":216,"anonymous_codeload_requests":1,"anonymous_codeload_limit":1,"rejected_requests":0,"measurement_only":true,"invalid_remaining_header":false,"terminal_rate_limited":false,"low_remaining_guard":false,"remaining_tail_reserve":1,"host_head_source_rest":{"raw":180,"primary":180,"not_modified":0,"secondary_points":180,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0},"host_other_github_rest":{"raw":0,"primary":0,"not_modified":0,"secondary_points":0,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0}}\n';
const controlBudgetReceipt =
  'APR_R4_E2P_CONTROL_REQUEST_BUDGET {"consumed":9,"limit":64,"primary":9,"not_modified":0,"secondary_points":13,"mutation_count":1,"remaining_tail_required":0,"remaining_tail_reserve":1,"permission_denied":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_remaining_header":false,"measurement_only":true,"rate_limited":false}\n';

afterEach(async () => {
  vi.unstubAllEnvs();
  await Promise.all(
    roots.splice(0).map(async (root) => await rm(root, { recursive: true, force: true })),
  );
});

describe('W1 production composition', () => {
  it('reads only the exact Actions facts and preserves the complete H2 workflow ref', () => {
    vi.stubEnv('GITHUB_EVENT_PATH', '/runner/event.json');
    vi.stubEnv('GITHUB_REPOSITORY', 'SolusQuest/agentic-pr-review');
    vi.stubEnv('GITHUB_REPOSITORY_ID', '9223372036854775807');
    vi.stubEnv('GITHUB_RUN_ID', '9007199254740993');
    vi.stubEnv('GITHUB_RUN_ATTEMPT', '2');
    vi.stubEnv('GITHUB_WORKFLOW_REF', fullWorkflowRef);
    vi.stubEnv('GITHUB_WORKFLOW_SHA', 'b'.repeat(40));
    vi.stubEnv('SESSION_CANARY', 'must-not-cross');
    expect(readProductionRuntimeFacts()).toEqual({
      eventJsonPath: '/runner/event.json',
      repositoryName: 'SolusQuest/agentic-pr-review',
      repositoryId: '9223372036854775807',
      runId: '9007199254740993',
      runAttempt: '2',
      workflowPath: '.github/workflows/r4-trusted-proof.yml',
      workflowRef: fullWorkflowRef,
      workflowSha: 'b'.repeat(40),
    });
  });

  it('constructs the package-bound S2 executor without dispatching an SDK call', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-production-s2-'));
    roots.push(root);
    const stagingRoot = path.join(root, 'artifact-staging');
    await mkdir(stagingRoot, { mode: 0o700 });
    const tracker = new OfficialCallTracker();
    const executor = await createProductionArtifactExecutor(
      {
        githubToken: 'synthetic-token',
        repositoryName: 'SolusQuest/agentic-pr-review',
        runId: '1',
        runAttempt: '1',
        stagingRoot,
        verifiedPreparedPayload: { buildDiscriminator: 'r4-h1' },
        artifactRestRequestBudget: ArtifactRestRequestBudget.forVerifiedPreparedPayload({
          buildDiscriminator: 'r4-h1',
        }),
      },
      tracker,
    );
    expect(typeof executor.execute).toBe('function');
    await expect(tracker.awaitQuiescence(100)).resolves.toBe(true);
  });

  it('runs masking, proof, bridge, Host validation, quiescence, and presentation in order', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({ 'github-token': 'github-canary' });
    const events = presentation.events;
    let admittedHandle: FileHandle | undefined;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        events.push('bridge:start');
        expect(input.buildDiscriminator).toBe('r4-h1');
        return {
          endpoint: '/tmp/apr-w1/bridge.sock',
          stagingRoot: '/tmp/apr-w1/artifact-staging',
          tempRoot: '/tmp/apr-w1',
          stopAndDrain: async () => {
            events.push('bridge:drain');
          },
          cleanup: async () => {
            events.push('bridge:cleanup');
          },
        };
      },
      createArtifactExecutor: async () => {
        throw new Error('must remain lazy');
      },
      hostProcessRunner: async (request) => {
        events.push('host:run');
        admittedHandle = request.executableHandle;
        expect(Object.keys(request).sort()).toEqual([
          'executableHandle',
          'launchBytes',
          'signal',
          'tempRoot',
        ]);
        expect(request.executableHandle.fd).toBeGreaterThanOrEqual(0);
        const launch = parseLaunchDocument(request.launchBytes);
        expect(launch.workflow_ref).toBe(fullWorkflowRef);
        expect(launch.inputs.github_token).toBe('github-canary');
        return {
          completionBytes: validCompletion(),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [],
        };
      },
      fatalExit: () => events.push('fatal'),
    });
    expect(exit).toBe(0);
    expect(events.indexOf('mask:github-canary')).toBeLessThan(events.indexOf('bridge:start'));
    expect(events).toContain('bridge:drain');
    expect(events).toContain('bridge:cleanup');
    expect(events.at(-1)).toBe('summary');
    expect(admittedHandle?.fd).toBe(-1);
    expect(presentation.summaries[0]).not.toContain('github-canary');
  });

  it('emits only exact protected budget receipts after drain and quiescence', async () => {
    vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'measurement');
    const fixture = await wrapperFixture('r4-w2');
    const presentation = recordingToolkit({ 'github-token': 'github-canary' });
    const events = presentation.events;
    const receipts: string[] = [];
    let artifactBudgetReceipt: ReturnType<ArtifactRestRequestBudget['receipt']> | undefined;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        await input.executorFactory('/tmp/apr-w2/artifact-staging');
        return {
          endpoint: '/tmp/apr-w2/bridge.sock',
          stagingRoot: '/tmp/apr-w2/artifact-staging',
          tempRoot: '/tmp/apr-w2',
          stopAndDrain: async () => {
            events.push('bridge:drain');
          },
          cleanup: async () => {
            events.push('bridge:cleanup');
          },
        };
      },
      createArtifactExecutor: async (context) => {
        artifactBudgetReceipt = context.artifactRestRequestBudget.receipt();
        return { execute: async () => ({ status: 'ok' }) as never };
      },
      hostProcessRunner: async (request) => {
        expect(request.requestBudgetProfile).toBe('measurement');
        return {
          completionBytes: validCompletion('r4-w2'),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
        };
      },
      trustedProofBudgetReceiptSink: (frame) => {
        receipts.push(frame);
        events.push('budget:frame');
      },
      fatalExit: () => events.push('fatal'),
    });

    expect(exit).toBe(0);
    expect(receipts).toHaveLength(1);
    expect(artifactBudgetReceipt).toMatchObject({
      maximum_total_authenticated_api_requests: 2_304,
      maximum_primary_rate_limit_requests: 256,
      remaining_tail_required: 0,
      remaining_tail_reserve: 1,
      measurement_only: true,
    });
    const lines = receipts[0]!.trimEnd().split('\n');
    expect(lines.slice(0, 2)).toEqual([
      githubBudgetReceipt.trimEnd(),
      controlBudgetReceipt.trimEnd(),
    ]);
    expect(lines[2]).toContain(TRUSTED_PROOF_ARTIFACT_REST_REQUEST_RECEIPT_PREFIX);
    expect(JSON.parse(lines[2]!.split(' ', 2)[1]!)).toMatchObject({
      repository: fixture.facts.repositoryName,
      repository_id: fixture.facts.repositoryId,
      workflow_sha: fixture.facts.workflowSha,
      action_source_sha: fixture.proof.actionSourceSha,
      payload_sha256: fixture.proof.payloadSha256,
      build_discriminator: 'r4-w2',
      run_id: fixture.facts.runId,
      run_attempt: fixture.facts.runAttempt,
      cap_profile: 'apr-r4-artifact-rest-request-budget-v2',
      measurement_only: true,
    });
    expect(events.indexOf('bridge:drain')).toBeLessThan(events.indexOf('budget:frame'));
    expect(events.indexOf('budget:frame')).toBeLessThan(events.indexOf('bridge:cleanup'));
  });

  it('cleans the bridge and fails closed when the protected receipt sink throws', async () => {
    vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'measurement');
    const fixture = await wrapperFixture('r4-w2');
    const presentation = recordingToolkit({});
    const events = presentation.events;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        await input.executorFactory('/tmp/apr-w2/artifact-staging');
        return {
          endpoint: '/tmp/apr-w2/bridge.sock',
          stagingRoot: '/tmp/apr-w2/artifact-staging',
          tempRoot: '/tmp/apr-w2',
          stopAndDrain: async () => {
            events.push('bridge:drain');
          },
          cleanup: async () => {
            events.push('bridge:cleanup');
          },
        };
      },
      createArtifactExecutor: async () => ({
        execute: async () => ({ status: 'ok' }) as never,
      }),
      hostProcessRunner: async () => ({
        completionBytes: validCompletion('r4-w2'),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
      }),
      trustedProofBudgetReceiptSink: () => {
        events.push('artifact-budget:receipt-failed');
        throw new Error('receipt-sink-failure');
      },
      fatalExit: () => events.push('fatal'),
    });

    expect(exit).toBe(1);
    expect(events.indexOf('bridge:drain')).toBeLessThan(
      events.indexOf('artifact-budget:receipt-failed'),
    );
    expect(events.indexOf('artifact-budget:receipt-failed')).toBeLessThan(
      events.indexOf('bridge:cleanup'),
    );
    expect(presentation.errors).toEqual(['The private review wrapper failed.']);
  });

  it('keeps the protected receipt after a business failure once bridge work has quiesced', async () => {
    vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'measurement');
    const fixture = await wrapperFixture('r4-w2');
    const presentation = recordingToolkit({});
    const events = presentation.events;
    const receipts: string[] = [];
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        await input.executorFactory('/tmp/apr-w2/artifact-staging');
        return {
          endpoint: '/tmp/apr-w2/bridge.sock',
          stagingRoot: '/tmp/apr-w2/artifact-staging',
          tempRoot: '/tmp/apr-w2',
          stopAndDrain: async () => {
            events.push('bridge:drain');
          },
          cleanup: async () => {
            events.push('bridge:cleanup');
            throw new Error('cleanup-failure');
          },
        };
      },
      createArtifactExecutor: async () => ({
        execute: async () => ({ status: 'ok' }) as never,
      }),
      hostProcessRunner: async () => ({
        completionBytes: Buffer.from('{"malformed":true}'),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
      }),
      trustedProofBudgetReceiptSink: (line) => {
        receipts.push(line);
        events.push('artifact-budget:receipt');
      },
      fatalExit: () => events.push('fatal'),
    });

    expect(exit).toBe(1);
    expect(receipts).toHaveLength(1);
    expect(receipts[0]?.startsWith('APR_R4_E2P_GITHUB_REQUEST_BUDGET ')).toBe(true);
    expect(receipts[0]).toContain(TRUSTED_PROOF_ARTIFACT_REST_REQUEST_RECEIPT_PREFIX);
    expect(events.indexOf('bridge:drain')).toBeLessThan(events.indexOf('artifact-budget:receipt'));
    expect(events.indexOf('artifact-budget:receipt')).toBeLessThan(
      events.indexOf('bridge:cleanup'),
    );
    expect(presentation.errors).toEqual(['The private review wrapper failed.']);
  });

  it('does not enable the protected receipt from ordinary action inputs', async () => {
    const fixture = await wrapperFixture('r4-h1');
    const presentation = recordingToolkit({
      'github-token': 'r4-w2',
      'provider-api-key': 'r4-w2',
    });
    const receipts: string[] = [];
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        await input.executorFactory('/tmp/apr-h1/artifact-staging');
        return await fakeBridge(input);
      },
      createArtifactExecutor: async () => ({
        execute: async () => ({ status: 'ok' }) as never,
      }),
      hostProcessRunner: async () => ({
        completionBytes: validCompletion('r4-h1'),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
      }),
      trustedProofBudgetReceiptSink: (line) => receipts.push(line),
      fatalExit: () => undefined,
    });

    expect(exit).toBe(0);
    expect(receipts).toEqual([]);
  });

  it.each([
    ['missing', undefined, 'wrapper_request_budget_profile_invalid'],
    ['invalid', 'wide-open', 'wrapper_request_budget_profile_invalid'],
    ['final before allocations freeze', 'final', 'wrapper_request_budget_profile_unfrozen'],
  ])(
    'fails closed before bridge or Host for a protected %s request-budget profile',
    async (_caseName, profile, expectedCode) => {
      vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', profile);
      const fixture = await wrapperFixture('r4-w2');
      const presentation = recordingToolkit({});
      let bridgeStarted = false;
      let hostStarted = false;

      const exit = await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: async (input) => {
          bridgeStarted = true;
          return await fakeBridge(input);
        },
        createArtifactExecutor: async () => {
          throw new Error('must remain lazy');
        },
        hostProcessRunner: async () => {
          hostStarted = true;
          return {
            completionBytes: validCompletion('r4-w2'),
            exitCode: 0,
            trustedProofBudgetReceiptLines: [],
          };
        },
        fatalExit: () => undefined,
      });

      expect(exit).toBe(1);
      expect(bridgeStarted).toBe(false);
      expect(hostStarted).toBe(false);
      expect(presentation.errors).toEqual(['The private review wrapper failed.']);
      expect(() =>
        readTrustedProofRequestBudgetProfile(
          'r4-w2',
          profile === undefined
            ? {}
            : { [R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE]: profile },
        ),
      ).toThrow(expectedCode);
    },
  );

  it('fails closed on malformed Host output without forwarding canaries', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({ 'provider-api-key': 'provider-canary' });
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: fakeBridge,
      createArtifactExecutor: async () => {
        throw new Error('must remain lazy');
      },
      hostProcessRunner: async () => ({
        completionBytes: Buffer.from('{"private":"provider-canary"}'),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [],
      }),
      fatalExit: () => undefined,
    });
    expect(exit).toBe(1);
    expect(presentation.summaries.join('')).not.toContain('provider-canary');
    expect(presentation.errors).toEqual(['The private review wrapper failed.']);
  });

  it('rejects the atomic wrapper/payload build mismatch before bridge or spawn', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({ 'state-key': 'state-canary' });
    let bridgeStarted = false;
    let hostStarted = false;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: { ...fixture.proof, wrapperBuildDiscriminator: 'r4-h2' },
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        bridgeStarted = true;
        return await fakeBridge(input);
      },
      createArtifactExecutor: async () => {
        throw new Error('must not construct');
      },
      hostProcessRunner: async () => {
        hostStarted = true;
        return {
          completionBytes: validCompletion(),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [],
        };
      },
      fatalExit: () => undefined,
    });
    expect(exit).toBe(1);
    expect(bridgeStarted).toBe(false);
    expect(hostStarted).toBe(false);
    expect(presentation.events).toContain('mask:state-canary');
    expect(presentation.summaries.join('')).not.toContain('state-canary');
  });

  it('requests fatal termination and attempts no presentation when SDK work cannot quiesce', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({});
    let fatal = 0;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => {
        await input.executorFactory('/tmp/staging');
        return await fakeBridge(input);
      },
      createArtifactExecutor: async (_context, tracker) => {
        const client = tracker.wrap({
          call: async () => await new Promise<never>(() => undefined),
        });
        void client.call();
        return { execute: async () => Promise.reject(new Error('unused')) };
      },
      hostProcessRunner: async () => ({
        completionBytes: validCompletion(),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [],
      }),
      fatalExit: () => {
        fatal += 1;
      },
      officialQuiescenceTimeoutMs: 10,
    });
    expect(exit).toBe(1);
    expect(fatal).toBe(1);
    expect(presentation.summaries).toEqual([]);
    expect(presentation.errors).toEqual([]);
  });

  it('fatally exits after one bridge cleanup attempt and without presentation when Host close is unconfirmed', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({ 'github-token': 'termination-canary' });
    let fatal = 0;
    let drained = 0;
    let cleaned = 0;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async () => ({
        endpoint: '/tmp/apr-w1/bridge.sock',
        stagingRoot: '/tmp/apr-w1/artifact-staging',
        tempRoot: '/tmp/apr-w1',
        stopAndDrain: async () => {
          drained += 1;
        },
        cleanup: async () => {
          cleaned += 1;
        },
      }),
      createArtifactExecutor: async () => {
        throw new Error('must remain lazy');
      },
      hostProcessRunner: async () => {
        throw new HostProcessTerminationUnconfirmedError();
      },
      fatalExit: () => {
        fatal += 1;
      },
    });
    expect(exit).toBe(1);
    expect(fatal).toBe(1);
    expect(drained).toBe(1);
    expect(cleaned).toBe(1);
    expect(presentation.summaries).toEqual([]);
    expect(presentation.errors).toEqual([]);
    expect(presentation.events).toContain('mask:termination-canary');
  });

  it('terminates an independent process with a referenced handle at the quiescence bound', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-fatal-parent-'));
    roots.push(root);
    const fixture = path.resolve('src/action-wrapper/launcher/fatal-exit.fixture.ts');
    const viteNode = path.resolve('node_modules/vite-node/vite-node.mjs');
    const started = Date.now();
    const result = await childResult(process.execPath, [viteNode, fixture, root]);
    expect(result.code).toBe(1);
    expect(Date.now() - started).toBeLessThan(8_000);
    expect(result.stdout).toBe('');
    expect(result.stderr).toBe('');
  }, 10_000);

  it.runIf(process.platform === 'linux')(
    'terminates independently without presentation or cleanup when Host close is unconfirmed',
    async () => {
      const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-host-close-parent-'));
      roots.push(root);
      const fixture = path.resolve('src/action-wrapper/launcher/host-close-fatal.fixture.ts');
      const viteNode = path.resolve('node_modules/vite-node/vite-node.mjs');
      const started = Date.now();
      const result = await childResult(process.execPath, [viteNode, fixture, root]);
      expect(result.code).toBe(1);
      expect(Date.now() - started).toBeLessThan(8_000);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('');
      await expect(access(path.join(root, 'cleanup-called'))).rejects.toThrow();
    },
    10_000,
  );

  it.runIf(process.platform === 'linux')(
    'keeps duplicate parent signals inside the controlled cancellation lifecycle',
    async () => {
      const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-duplicate-signal-parent-'));
      roots.push(root);
      const fixture = path.resolve('src/action-wrapper/launcher/duplicate-signal.fixture.ts');
      const viteNode = path.resolve('node_modules/vite-node/vite-node.mjs');
      const child = spawn(process.execPath, [viteNode, fixture, root], {
        stdio: ['ignore', 'pipe', 'pipe'],
        windowsHide: true,
      });
      const result = childCapture(child);
      await waitForFile(path.join(root, 'host-ready'));
      expect(child.kill('SIGTERM')).toBe(true);
      await waitForFile(path.join(root, 'host-signals'));
      expect(child.kill('SIGTERM')).toBe(true);
      await writeFile(path.join(root, 'host-release'), 'release');
      await expect(result).resolves.toEqual({ code: 0, signal: null, stdout: '', stderr: '' });
      expect(await readFile(path.join(root, 'host-signals'), 'utf8')).toBe('x');
    },
    10_000,
  );
});

async function fakeBridge(_input: {
  readonly buildDiscriminator: string;
  readonly executorFactory: (stagingRoot: string) => Promise<ArtifactBridgeExecutor>;
}) {
  return {
    endpoint: '/tmp/apr-w1/bridge.sock',
    stagingRoot: '/tmp/apr-w1/artifact-staging',
    tempRoot: '/tmp/apr-w1',
    stopAndDrain: async () => undefined,
    cleanup: async () => undefined,
  };
}

async function wrapperFixture(buildDiscriminator = 'r4-h1'): Promise<{
  readonly proof: PreparedPayloadProof;
  readonly facts: ActionRuntimeFacts;
}> {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-index-'));
  roots.push(root);
  const executable = path.join(root, 'host');
  const event = path.join(root, 'event.json');
  const bytes = Buffer.from('host-fixture');
  await writeFile(executable, bytes);
  if (process.platform !== 'win32') await chmod(executable, 0o700);
  await writeFile(event, '{}');
  return {
    proof: {
      trustedRoot: root,
      executableRelativePath: 'host',
      actionSourceSha: 'a'.repeat(40),
      payloadSha256: createHash('sha256').update(bytes).digest('hex'),
      buildDiscriminator,
      wrapperBuildDiscriminator: buildDiscriminator,
    },
    facts: {
      eventJsonPath: event,
      repositoryName: 'SolusQuest/agentic-pr-review',
      repositoryId: '9223372036854775807',
      runId: '9007199254740993',
      runAttempt: '2',
      workflowPath: '.github/workflows/r4-trusted-proof.yml',
      workflowRef: fullWorkflowRef,
      workflowSha: 'b'.repeat(40),
    },
  };
}

function recordingToolkit(values: Record<string, string>) {
  const events: string[] = [];
  const summaries: string[] = [];
  const errors: string[] = [];
  const toolkit: ActionPresentationToolkit = {
    getInput: (name) => values[name] ?? '',
    setSecret: (value) => events.push(`mask:${value}`),
    writeSummary: async (value) => {
      summaries.push(value);
      events.push('summary');
    },
    warning: (value) => events.push(`warning:${value}`),
    error: (value) => errors.push(value),
  };
  return { toolkit, events, summaries, errors };
}

function validCompletion(buildDiscriminator = 'r4-h1'): Buffer {
  return Buffer.from(
    JSON.stringify({
      build_discriminator: buildDiscriminator,
      status: 'reviewed',
      exit_class: 'success',
      process_exit_code: 0,
      summary: {
        reviewed_sha: 'c'.repeat(40),
        publication_url: 'https://github.com/SolusQuest/agentic-pr-review/pull/163',
        finding_count: 0,
        state_disposition: 'accepted',
      },
      annotations: [],
    }),
  );
}

function childResult(command: string, args: string[]) {
  const child = spawn(command, args, { stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
  return childCapture(child);
}

function childCapture(child: ReturnType<typeof spawn>) {
  return new Promise<{
    readonly code: number | null;
    readonly signal: NodeJS.Signals | null;
    readonly stdout: string;
    readonly stderr: string;
  }>((resolve, reject) => {
    let stdout = '';
    let stderr = '';
    child.stdout!.setEncoding('utf8').on('data', (chunk: string) => (stdout += chunk));
    child.stderr!.setEncoding('utf8').on('data', (chunk: string) => (stderr += chunk));
    child.once('error', reject);
    child.once('close', (code, signal) => resolve({ code, signal, stdout, stderr }));
  });
}

async function waitForFile(filePath: string): Promise<void> {
  const deadline = Date.now() + 5_000;
  while (Date.now() < deadline) {
    try {
      await access(filePath);
      return;
    } catch {
      await new Promise<void>((resolve) => setTimeout(resolve, 10));
    }
  }
  throw new Error('fixture_not_ready');
}
