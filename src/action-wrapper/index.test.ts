import {
  FIXED_WRAPPER_FAILURE_OUTPUTS,
  projectCompletionOutputs,
  type ActionOutputName,
} from './presentation/outputs.js';
import {
  parseCompletionDocument,
  type ActionHostCompletionDocument,
} from './presentation/completion.js';
import { unavailableAccounting } from './presentation/accounting-fixtures.js';
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
  artifactRestRequestBudgetProfile,
  R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE,
  readTrustedProofRequestBudgetProfile,
  TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE,
} from './launcher/request-budget-profile.js';
import type { ActionPresentationToolkit } from './presentation/toolkit.js';

const roots: string[] = [];
const fullWorkflowRef =
  'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main';
const githubBudgetReceipt =
  'APR_R4_E2P_GITHUB_REQUEST_BUDGET {"authenticated_rest_requests":180,"authenticated_rest_limit":256,"anonymous_codeload_requests":1,"anonymous_codeload_limit":1,"rejected_requests":0,"measurement_only":true,"invalid_remaining_header":false,"terminal_rate_limited":false,"low_remaining_guard":false,"remaining_tail_reserve":1,"host_head_source_rest":{"raw":180,"primary":180,"not_modified":0,"secondary_points":180,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0},"host_other_github_rest":{"raw":0,"primary":0,"not_modified":0,"secondary_points":0,"permission":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_rate_headers":0,"remaining_tail_required":0}}\n';
const controlBudgetReceipt =
  'APR_R4_E2P_CONTROL_REQUEST_BUDGET {"consumed":9,"limit":64,"primary":9,"not_modified":0,"secondary_points":13,"mutation_count":1,"remaining_tail_required":0,"remaining_tail_reserve":1,"permission_denied":0,"primary_rate_limited":0,"secondary_rate_limited":0,"combined_rate_limited":0,"invalid_remaining_header":false,"measurement_only":true,"rate_limited":false}\n';
const reconciliationDiagnostic =
  'APR_R4_E2P_STATE_RECONCILIATION {"owner":"lineage_head","outcome":"committed","exact_readback":"matched","observations":3,"terminal":"target_absent","schedule_index":2}\n';

afterEach(async () => {
  vi.unstubAllEnvs();
  await Promise.all(
    roots.splice(0).map(async (root) => await rm(root, { recursive: true, force: true })),
  );
});

describe('W1 production composition', () => {
  it.each([
    'retry_success',
    'final_failure_unknown_usage',
    'unknown_partition',
    'model_limit',
    'cancelled_unreconciled',
    'missing_finalizer',
    'recovery_only',
    'skipped',
    'exact_int64',
    'overflow_known_sum',
    'pre_provider',
  ])('presents actual strict Host accounting at the entrypoint: %s', async (name) => {
    const cases = JSON.parse(
      await readFile(
        'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
        'utf8',
      ),
    ) as {
      name: string;
      document: {
        process_exit_code: number;
        termination_reason: string;
        accounting: { provider_failed_attempts: string | null };
      };
    }[];
    const document = cases.find((item) => item.name === name)!.document;
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({ 'github-token': 'github-canary' });
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async () => ({
        endpoint: '/tmp/apr-h2/bridge.sock',
        stagingRoot: '/tmp/apr-h2/staging',
        tempRoot: '/tmp/apr-h2',
        stopAndDrain: async () => undefined,
        cleanup: async () => undefined,
      }),
      createArtifactExecutor: async () => ({ execute: async () => ({ status: 'ok' }) as never }),
      hostProcessRunner: async () => ({
        completionBytes: Buffer.from(JSON.stringify(document)),
        exitCode: document.process_exit_code,
        trustedProofBudgetReceiptLines: [],
      }),
      fatalExit: () => {
        throw new Error('unexpected fatal');
      },
    });
    expect(exit).toBe(document.process_exit_code);
    expect(presentation.summaries).toHaveLength(1);
    expect(presentation.summaries[0]).toContain(
      `| Review termination | ${document.termination_reason} |`,
    );
    expect(presentation.summaries[0]).toContain(
      `| Failed provider attempts | ${document.accounting.provider_failed_attempts ?? 'Not available'} |`,
    );
    expect(presentation.summaries[0]).not.toContain('github-canary');
    expect(presentation.outputs).toEqual(
      projectCompletionOutputs(
        parseCompletionDocument(
          Buffer.from(JSON.stringify(document)),
          'r4-h1',
          document.process_exit_code,
        ),
      ),
    );
  });

  it('presents unavailable facts on input toolkit failure without forwarding its exception', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({});
    presentation.toolkit.getInput = () => {
      throw new Error('PRIVATE_INPUT_CANARY');
    };
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async () => {
        throw new Error('must not start');
      },
      createArtifactExecutor: async () => {
        throw new Error('must not start');
      },
      hostProcessRunner: async () => {
        throw new Error('must not start');
      },
      fatalExit: () => undefined,
    });
    expect(exit).toBe(1);
    expect(presentation.summaries).toHaveLength(1);
    expect(presentation.summaries[0]).toContain('completeness: unavailable');
    expect(presentation.summaries[0]).not.toMatch(/CANARY|accepted|Provider attempts: 0/);
    expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
  });

  it.each(['cleanup', 'output-prefix', 'summary', 'warning', 'error'])(
    'preserves the admitted lifecycle boundary on %s failure',
    async (sink) => {
      const fixture = await wrapperFixture();
      let document = await accountingCompletion(
        sink === 'error' ? 'final_failure_unknown_usage' : 'retry_success',
      );
      if (sink === 'warning') {
        document = {
          ...document,
          status: 'reviewed_with_inline_warnings',
          annotations: [
            {
              code: 'inline_publication_incomplete',
              severity: 'warning',
              message: 'Some inline annotations could not be published.',
            },
          ],
        };
      }
      const presentation = recordingToolkit({});
      const expected = projectCompletionOutputs(document);
      const setter = presentation.toolkit.setOutput;
      let attempts = 0;
      if (sink === 'output-prefix')
        presentation.toolkit.setOutput = (name, value) => {
          if (++attempts === 4) throw new Error('PRIVATE_OUTPUT_CANARY');
          setter(name, value);
        };
      if (sink === 'summary')
        presentation.toolkit.writeSummary = async () => {
          throw new Error('PRIVATE_SUMMARY_CANARY');
        };
      if (sink === 'warning')
        presentation.toolkit.warning = () => {
          throw new Error('PRIVATE_ANNOTATION_CANARY');
        };
      if (sink === 'error')
        presentation.toolkit.error = () => {
          throw new Error('PRIVATE_ANNOTATION_CANARY');
        };
      const exit = await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: async () => ({
          ...(await fakeBridge({
            buildDiscriminator: 'r4-h1',
            executorFactory: async () => ({ execute: async () => ({ status: 'ok' }) as never }),
          })),
          cleanup: async () => {
            if (sink === 'cleanup') throw new Error('PRIVATE_CLEANUP_CANARY');
          },
        }),
        createArtifactExecutor: async () => ({ execute: async () => ({ status: 'ok' }) as never }),
        hostProcessRunner: async () => ({
          completionBytes: Buffer.from(JSON.stringify(document)),
          exitCode: document.process_exit_code,
          trustedProofBudgetReceiptLines: [],
        }),
        fatalExit: () => {
          throw new Error('unexpected fatal');
        },
      });
      expect(exit).toBe(1);
      if (sink === 'cleanup') {
        expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
        expect(presentation.summaries[0]).toContain('failed safely');
      } else {
        expect(presentation.outputs).toEqual(
          sink === 'output-prefix'
            ? Object.fromEntries(Object.entries(expected).slice(0, 3))
            : expected,
        );
        expect(presentation.outputCalls).toHaveLength(
          sink === 'output-prefix' ? 3 : Object.keys(expected).length,
        );
        expect(presentation.outputs.status).toBe(document.status);
        expect(presentation.summaries).toHaveLength(
          sink === 'summary' || sink === 'output-prefix' ? 0 : 1,
        );
      }
      expect(
        JSON.stringify(presentation.outputs) +
          presentation.summaries.join('') +
          presentation.errors.join(''),
      ).not.toContain('CANARY');
    },
  );

  it('attempts every fixed fallback sink once after actual input failure', async () => {
    const fixture = await wrapperFixture();
    const presentation = recordingToolkit({});
    const attempts: string[] = [];
    presentation.toolkit.getInput = () => {
      throw new Error('PRIVATE_INPUT_CANARY');
    };
    presentation.toolkit.setOutput = (name) => {
      attempts.push(name);
      throw new Error('PRIVATE_SINK_CANARY');
    };
    presentation.toolkit.writeSummary = async () => {
      attempts.push('summary');
      throw new Error('PRIVATE_SUMMARY_CANARY');
    };
    presentation.toolkit.error = (message) => {
      expect(message).toBe('The private review wrapper failed.');
      attempts.push('error');
      throw new Error('PRIVATE_ERROR_CANARY');
    };
    expect(
      await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: fakeBridge,
        createArtifactExecutor: async () => {
          throw new Error('must stay lazy');
        },
        hostProcessRunner: async () => {
          throw new Error('must not spawn');
        },
        fatalExit: () => undefined,
      }),
    ).toBe(1);
    expect(attempts).toEqual([
      'status',
      'termination-reason',
      'attempt-accounting-completeness',
      'usage-completeness',
      'summary',
      'error',
    ]);
  });

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

  it('emits only exact protected receipts after drain and quiescence', async () => {
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
          trustedProofStateReconciliationDiagnosticLine: reconciliationDiagnostic,
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
    expect(lines[3]).toBe(reconciliationDiagnostic.trimEnd());
    expect(lines).toHaveLength(4);
    expect(events.indexOf('bridge:drain')).toBeLessThan(events.indexOf('budget:frame'));
    expect(events.indexOf('budget:frame')).toBeLessThan(events.indexOf('bridge:cleanup'));
  });

  it('cleans the bridge and fails closed when the protected receipt sink throws', async () => {
    vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'measurement');
    const fixture = await wrapperFixture('r4-w2');
    const presentation = recordingToolkit({});
    const events = presentation.events;
    const document = await accountingCompletion('retry_success', 'r4-w2');
    let sinkInvocations = 0;
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
        completionBytes: Buffer.from(JSON.stringify(document)),
        exitCode: 0,
        trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
        trustedProofStateReconciliationDiagnosticLine: reconciliationDiagnostic,
      }),
      trustedProofBudgetReceiptSink: () => {
        sinkInvocations++;
        events.push('artifact-budget:receipt-failed');
        throw new Error('receipt-sink-failure');
      },
      fatalExit: () => events.push('fatal'),
    });

    expect(exit).toBe(1);
    expect(sinkInvocations).toBe(1);
    expect(events.indexOf('bridge:drain')).toBeLessThan(
      events.indexOf('artifact-budget:receipt-failed'),
    );
    expect(events.indexOf('artifact-budget:receipt-failed')).toBeLessThan(
      events.indexOf('bridge:cleanup'),
    );
    expect(presentation.errors).toEqual(['The private review wrapper failed.']);
    expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
    expect(presentation.summaries[0]).not.toContain('| Provider attempts | 2 |');
  });

  it.each([
    reconciliationDiagnostic + reconciliationDiagnostic,
    reconciliationDiagnostic.replace('"owner":"lineage_head"', '"owner":"unknown"'),
  ])(
    'keeps one complete three-line frame when the optional diagnostic is invalid',
    async (line) => {
      vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'measurement');
      const fixture = await wrapperFixture('r4-w2');
      const presentation = recordingToolkit({});
      const receipts: string[] = [];
      const exit = await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: async (input) => {
          await input.executorFactory('/tmp/apr-w2/artifact-staging');
          return await fakeBridge(input);
        },
        createArtifactExecutor: async () => ({
          execute: async () => ({ status: 'ok' }) as never,
        }),
        hostProcessRunner: async () => ({
          completionBytes: validCompletion('r4-w2'),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [githubBudgetReceipt, controlBudgetReceipt],
          trustedProofStateReconciliationDiagnosticLine: line,
        }),
        trustedProofBudgetReceiptSink: (frame) => receipts.push(frame),
        fatalExit: () => undefined,
      });

      expect(exit).toBe(0);
      expect(receipts).toHaveLength(1);
      expect(receipts[0]!.trimEnd().split('\n')).toHaveLength(3);
    },
  );

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
    expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
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
        trustedProofStateReconciliationDiagnosticLine: reconciliationDiagnostic,
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
      expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
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

  it('selects the frozen final profile only from the verified protected environment', () => {
    expect(
      readTrustedProofRequestBudgetProfile('r4-h1', {
        [R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE]: 'final-bootstrap',
      }),
    ).toBeUndefined();
    const profile = readTrustedProofRequestBudgetProfile('r4-w2', {
      [R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE]: 'final-bootstrap',
    });
    expect(profile).toBe('final-bootstrap');
    expect(artifactRestRequestBudgetProfile(profile!)).toMatchObject({
      limits: {
        maximumTotalAuthenticatedApiRequests: 4_096,
        maximumPrimaryRateLimitRequests: 256,
      },
      remainingTailRequired: 0,
      remainingTailReserve: TRUSTED_PROOF_NORMAL_PROCESS_PRIMARY_RESERVE,
      measurementOnly: false,
    });
  });

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
    expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
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
    expect(presentation.outputs).toEqual({});
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
    expect(presentation.outputs).toEqual({});
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
      await expect(access(path.join(root, 'cleanup-called'))).resolves.toBeUndefined();
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

// Wrapper-only completion coverage; actual production Runtime denial/cancel is
// exercised separately by the new ActionHostFixture framework/AOT supervisor.
describe('D0 ordinary production handshake', () => {
  it.each([undefined, 'measurement', 'final-bootstrap', 'invalid'])(
    'presents an ordinary completion without proof receipts under ambient profile %s',
    async (profile) => {
      vi.stubEnv(R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE, profile);
      const fixture = await wrapperFixture('r7-d0');
      const presentation = recordingToolkit({});
      const receipts: string[] = [];
      const document = validCompletion('r7-d0');
      const exit = await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: async (input) => await fakeBridge(input),
        createArtifactExecutor: async () => {
          throw new Error('must remain lazy');
        },
        hostProcessRunner: async (request) => {
          expect(request.requestBudgetProfile).toBeUndefined();
          expect(parseLaunchDocument(request.launchBytes).build_discriminator).toBe('r7-d0');
          return { completionBytes: document, exitCode: 0, trustedProofBudgetReceiptLines: [] };
        },
        trustedProofBudgetReceiptSink: (frame) => receipts.push(frame),
        fatalExit: () => {
          throw new Error('unexpected fatal');
        },
      });
      expect(exit).toBe(0);
      expect(receipts).toEqual([]);
      expect(presentation.errors).toEqual([]);
      expect(presentation.outputs).toEqual(
        projectCompletionOutputs(parseCompletionDocument(document, 'r7-d0', 0)),
      );
    },
  );

  it.each(['malformed', 'foreign-build'])(
    'keeps ordinary %s completions fail-closed',
    async (kind) => {
      const fixture = await wrapperFixture('r7-d0');
      const presentation = recordingToolkit({});
      const exit = await runPrivateActionWrapperWithSeams({
        toolkit: presentation.toolkit,
        preparedPayload: fixture.proof,
        platform: 'linux',
        signal: new AbortController().signal,
        runtimeFacts: () => fixture.facts,
        bridgeRuntime: async (input) => await fakeBridge(input),
        createArtifactExecutor: async () => {
          throw new Error('must remain lazy');
        },
        hostProcessRunner: async () => ({
          completionBytes: kind === 'malformed' ? Buffer.from('{') : validCompletion('r4-w2'),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [],
        }),
        fatalExit: () => {
          throw new Error('unexpected fatal');
        },
      });
      expect(exit).toBe(1);
      expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
      expect(presentation.errors).toEqual(['The private review wrapper failed.']);
    },
  );

  it('still rejects a valid r4-w2 completion without its required proof receipts', async () => {
    vi.stubEnv(R4_REQUEST_BUDGET_PROFILE_ENVIRONMENT_VARIABLE, 'measurement');
    const fixture = await wrapperFixture('r4-w2');
    const presentation = recordingToolkit({});
    let hostCalls = 0;
    const exit = await runPrivateActionWrapperWithSeams({
      toolkit: presentation.toolkit,
      preparedPayload: fixture.proof,
      platform: 'linux',
      signal: new AbortController().signal,
      runtimeFacts: () => fixture.facts,
      bridgeRuntime: async (input) => await fakeBridge(input),
      createArtifactExecutor: async () => {
        throw new Error('must remain lazy');
      },
      hostProcessRunner: async (request) => {
        hostCalls++;
        expect(request.requestBudgetProfile).toBe('measurement');
        return {
          completionBytes: validCompletion('r4-w2'),
          exitCode: 0,
          trustedProofBudgetReceiptLines: [],
        };
      },
      fatalExit: () => {
        throw new Error('unexpected fatal');
      },
    });
    expect(hostCalls).toBe(1);
    expect(exit).toBe(1);
    expect(presentation.outputs).toEqual(FIXED_WRAPPER_FAILURE_OUTPUTS);
    expect(presentation.errors).toEqual(['The private review wrapper failed.']);
  });
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
  const outputs: Partial<Record<ActionOutputName, string>> = {};
  const outputCalls: [ActionOutputName, string][] = [];
  const toolkit: ActionPresentationToolkit = {
    getInput: (name) => values[name] ?? '',
    setOutput: (name, value) => {
      outputs[name] = value;
      outputCalls.push([name, value]);
    },
    setSecret: (value) => events.push(`mask:${value}`),
    writeSummary: async (value) => {
      summaries.push(value);
      events.push('summary');
    },
    warning: (value) => events.push(`warning:${value}`),
    error: (value) => errors.push(value),
  };
  return { toolkit, events, summaries, errors, outputs, outputCalls };
}

function validCompletion(buildDiscriminator = 'r4-h1'): Buffer {
  return Buffer.from(
    JSON.stringify({
      build_discriminator: buildDiscriminator,
      status: 'reviewed',
      exit_class: 'success',
      process_exit_code: 0,
      accounting: unavailableAccounting,
      termination_reason: 'review_completed',
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

async function accountingCompletion(
  name: string,
  build = 'r4-h1',
): Promise<ActionHostCompletionDocument> {
  const cases = JSON.parse(
    await readFile(
      'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
      'utf8',
    ),
  ) as { name: string; document: ActionHostCompletionDocument }[];
  return { ...cases.find((item) => item.name === name)!.document, build_discriminator: build };
}
