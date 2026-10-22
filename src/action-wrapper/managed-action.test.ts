import net from 'node:net';
import { mkdir, mkdtemp, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import {
  runManagedActionWrapperWithSeams,
  type ManagedActionPayload,
  type ManagedActionWrapperSeams,
} from './index.js';
import { parseLaunchDocument, type ActionRuntimeFacts } from './launcher/contracts.js';
import {
  startArtifactBridgeRuntime,
  type ArtifactBridgeRuntime,
} from './launcher/bridge-runtime.js';
import { ManagedHostProcessTerminationUnconfirmedError } from './launcher/host-process.js';
import { ManagedRuntimeCleanupError } from './launcher/managed-runtime.js';
import { RuntimePrerequisiteTerminationUnconfirmedError } from './launcher/runtime-prerequisite.js';
import type { VerifiedManagedTree } from './launcher/payload-staging.js';
import { encodeCommandMessage } from './artifact-bridge/framing.js';
import { zeroAccounting } from './presentation/accounting-fixtures.js';
import type { ActionPresentationToolkit } from './presentation/toolkit.js';

const roots: string[] = [];
afterEach(async () => {
  vi.unstubAllEnvs();
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

function deferred() {
  let resolve!: () => void;
  let reject!: (error: unknown) => void;
  const promise = new Promise<void>((yes, no) => {
    resolve = yes;
    reject = no;
  });
  return { promise, resolve, reject };
}

async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-managed-wrapper-'));
  roots.push(root);
  const runnerTemp = path.join(root, 'runner');
  const workspace = path.join(root, 'workspace');
  const treeRoot = path.join(root, 'tree');
  const runtimeRoot = path.join(root, 'runtime');
  await Promise.all(
    [runnerTemp, workspace, treeRoot, runtimeRoot].map((name) => mkdir(name, { mode: 0o700 })),
  );
  const eventPath = path.join(root, 'event.json');
  await writeFile(eventPath, '{}');
  const outputs: Record<string, string> = {};
  const summaries: string[] = [];
  const annotations: string[] = [];
  const events: string[] = [];
  const toolkit: ActionPresentationToolkit = {
    getInput: (name) => (name === 'github-token' ? 'PRIVATE_MANAGED_GITHUB_CANARY' : ''),
    setSecret: (value) => {
      events.push('mask:' + value);
    },
    setOutput: (name, value) => {
      outputs[name] = value;
      events.push('output');
    },
    writeSummary: async (value) => {
      summaries.push(value);
    },
    warning: (value) => {
      annotations.push(value);
    },
    error: (value) => {
      annotations.push(value);
    },
  };
  const facts: ActionRuntimeFacts = {
    eventJsonPath: eventPath,
    repositoryName: 'SolusQuest/agentic-pr-review',
    repositoryId: '42',
    runId: '900',
    runAttempt: '1',
    workflowPath: '.github/workflows/review.yml',
    workflowRef: 'SolusQuest/agentic-pr-review/.github/workflows/review.yml@refs/heads/main',
    workflowSha: 'a'.repeat(40),
  };
  const payload: ManagedActionPayload = {
    // This deterministic lifecycle seam never claims actual D2/D3 execution.
    tree: {} as VerifiedManagedTree,
    actionSourceSha: 'b'.repeat(40),
    payloadSha256: 'c'.repeat(64),
    buildDiscriminator: 'r7-d0',
    trustedRuntimeRoot: runtimeRoot,
    runnerStorage: { runnerTemp, workspace },
    dispose: vi.fn(async () => {
      events.push('dispose');
      await rm(treeRoot, { recursive: true });
      await rm(runtimeRoot, { recursive: true });
    }),
  };
  const bridge: ArtifactBridgeRuntime = {
    endpoint: '/tmp/apr-managed/bridge.sock',
    stagingRoot: '/tmp/apr-managed/staging',
    tempRoot: '/tmp/apr-managed',
    sealAdmission: vi.fn(() => {
      events.push('seal');
    }),
    stopAndDrain: vi.fn(async () => {
      events.push('drain');
    }),
    cleanup: vi.fn(async () => {
      events.push('bridge-cleanup');
    }),
  };
  let retention: Promise<void> | undefined;
  const seams: ManagedActionWrapperSeams = {
    toolkit,
    platform: 'linux',
    signal: new AbortController().signal,
    runtimeFacts: () => facts,
    acquirePayload: vi.fn(async () => {
      expect(events).toContain('mask:PRIVATE_MANAGED_GITHUB_CANARY');
      return payload;
    }),
    hostProcessRunner: vi.fn(async (request) => {
      expect(request.tree).toBe(payload.tree);
      expect(request.trustedRuntimeRoot).toBe(runtimeRoot);
      expect(request.workspace).toBe(workspace);
      expect(request).not.toHaveProperty('executableHandle');
      expect(request.requestBudgetProfile).toBeUndefined();
      const launch = parseLaunchDocument(request.launchBytes);
      expect(launch.facts.action_source_sha).toBe(payload.actionSourceSha);
      expect(launch.facts.payload_sha256).toBe(payload.payloadSha256);
      return { completionBytes: completion(), exitCode: 0, trustedProofBudgetReceiptLines: [] };
    }),
    bridgeRuntime: vi.fn(async (input) => {
      expect(input.runnerStorage).toEqual(payload.runnerStorage);
      return bridge;
    }),
    createArtifactExecutor: async () => {
      throw new Error('must remain lazy');
    },
    onRetainedResources: (promise) => {
      retention = promise;
    },
    fatalExit: vi.fn(),
    officialQuiescenceTimeoutMs: 20,
  };
  return {
    root,
    treeRoot,
    runtimeRoot,
    payload,
    bridge,
    seams,
    outputs,
    summaries,
    annotations,
    events,
    retention: () => retention!,
  };
}

function completion() {
  return Buffer.from(
    JSON.stringify({
      build_discriminator: 'r7-d0',
      status: 'skipped_untrusted_event',
      exit_class: 'success',
      process_exit_code: 0,
      accounting: zeroAccounting,
      termination_reason: 'not_started',
      summary: {
        reviewed_sha: null,
        publication_url: null,
        finding_count: null,
        state_disposition: 'not_accessed',
      },
      annotations: [],
    }),
  );
}
function noPresentation(f: Awaited<ReturnType<typeof fixture>>) {
  expect(f.outputs).toEqual({});
  expect(f.summaries).toEqual([]);
  expect(f.annotations).toEqual([]);
}

describe('managed Action owned launch and fatal barriers', () => {
  it('preserves masked inputs, managed provenance and all fourteen validated outputs', async () => {
    const f = await fixture();
    vi.stubEnv('AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE', 'invalid-proof-profile');
    expect(await runManagedActionWrapperWithSeams(f.seams)).toBe(0);
    expect(Object.keys(f.outputs)).toHaveLength(14);
    expect(f.outputs.status).toBe('skipped_untrusted_event');
    expect(f.outputs['model-calls']).toBe('0');
    expect(f.events.indexOf('dispose')).toBeLessThan(f.events.indexOf('output'));
    expect(f.events.indexOf('bridge-cleanup')).toBeLessThan(f.events.indexOf('output'));
    await expect(stat(f.treeRoot)).rejects.toThrow();
    await expect(stat(f.runtimeRoot)).rejects.toThrow();
  });

  it('retains unconfirmed prerequisite installation without launching or presenting, including after late close', async () => {
    const f = await fixture();
    const closed = deferred();
    let cleaned = false;
    const released = closed.promise.then(() => {
      cleaned = true;
    });
    const error = new RuntimePrerequisiteTerminationUnconfirmedError(closed.promise, released);
    expect(
      await runManagedActionWrapperWithSeams({
        ...f.seams,
        acquirePayload: async () => {
          throw error;
        },
      }),
    ).toBe(1);
    expect(cleaned).toBe(false);
    expect(f.outputs).toEqual({});
    expect(f.summaries).toEqual([]);
    expect(f.annotations).toEqual([]);
    expect(f.seams.hostProcessRunner).not.toHaveBeenCalled();
    expect(f.seams.bridgeRuntime).not.toHaveBeenCalled();
    expect(f.seams.fatalExit).not.toHaveBeenCalled();
    closed.resolve();
    await released;
    expect(cleaned).toBe(true);
    expect(f.outputs).toEqual({});
    expect(f.annotations).toEqual([]);
  });

  it.each(['acquisition', 'bridge', 'host', 'completion', 'payload-cleanup', 'bridge-cleanup'])(
    'uses only the permitted fixed failure projection after confirmed %s failure',
    async (failure) => {
      const f = await fixture();
      let seams = f.seams;
      if (failure === 'acquisition')
        seams = {
          ...seams,
          acquirePayload: async () => {
            throw new Error('PRIVATE');
          },
        };
      if (failure === 'bridge')
        seams = {
          ...seams,
          bridgeRuntime: async () => {
            throw new Error('PRIVATE');
          },
        };
      if (failure === 'host')
        seams = {
          ...seams,
          hostProcessRunner: async () => {
            throw new Error('PRIVATE');
          },
        };
      if (failure === 'completion')
        seams = {
          ...seams,
          hostProcessRunner: async () => ({
            completionBytes: Buffer.from('{}'),
            exitCode: 0,
            trustedProofBudgetReceiptLines: [],
          }),
        };
      if (failure === 'payload-cleanup')
        f.payload.dispose = vi.fn(async () => {
          throw new Error('PRIVATE');
        });
      if (failure === 'bridge-cleanup')
        f.bridge.cleanup = vi.fn(async () => {
          throw new Error('PRIVATE');
        });
      expect(await runManagedActionWrapperWithSeams(seams)).toBe(1);
      expect(f.outputs).toEqual({
        status: 'failed',
        'termination-reason': 'host_failure',
        'attempt-accounting-completeness': 'unavailable',
        'usage-completeness': 'unavailable',
      });
      expect(JSON.stringify([f.outputs, f.summaries, f.annotations])).not.toContain('PRIVATE');
    },
  );

  it('refuses an unsealable bridge before managed launch', async () => {
    const f = await fixture();
    delete f.bridge.sealAdmission;
    expect(await runManagedActionWrapperWithSeams(f.seams)).toBe(1);
    expect(f.seams.hostProcessRunner).not.toHaveBeenCalled();
    expect(f.payload.dispose).toHaveBeenCalledOnce();
    expect(f.outputs.status).toBe('failed');
  });

  it.each([
    ['direct', false],
    ['direct', true],
    ['attached', false],
    ['attached', true],
  ])(
    'retries a confirmed Host %s guard failure once and retains resources when retry fails=%s',
    async (kind, retryFails) => {
      const f = await fixture();
      const retry = vi.fn(async () => {
        f.events.push('guard-retry');
        if (retryFails) throw new Error('PRIVATE_GUARD_FAILURE');
      });
      const guardError = new ManagedRuntimeCleanupError(retry);
      const error = kind === 'direct' ? guardError : new Error('PRIVATE_HOST_FAILED');
      if (kind === 'attached') Object.defineProperty(error, 'cleanupError', { value: guardError });
      expect(
        await runManagedActionWrapperWithSeams({
          ...f.seams,
          hostProcessRunner: async () => {
            throw error;
          },
        }),
      ).toBe(1);
      expect(retry).toHaveBeenCalledOnce();
      expect(f.bridge.stopAndDrain).toHaveBeenCalledOnce();
      expect(f.bridge.cleanup).toHaveBeenCalledOnce();
      expect(f.seams.fatalExit).not.toHaveBeenCalled();
      if (retryFails) {
        expect(f.payload.dispose).not.toHaveBeenCalled();
        expect((await stat(f.treeRoot)).isDirectory()).toBe(true);
        expect((await stat(f.runtimeRoot)).isDirectory()).toBe(true);
      } else {
        expect(f.payload.dispose).toHaveBeenCalledOnce();
        expect(f.events.indexOf('guard-retry')).toBeLessThan(f.events.indexOf('dispose'));
      }
      expect(Object.keys(f.outputs)).toHaveLength(4);
      expect(f.outputs.status).toBe('failed');
      expect(JSON.stringify([f.outputs, f.summaries, f.annotations])).not.toContain('PRIVATE');
    },
  );

  it('seals immediately and waits for actual close even if resource settlement is observed first', async () => {
    const f = await fixture();
    const closed = deferred(),
      released = deferred();
    const error = new ManagedHostProcessTerminationUnconfirmedError(
      closed.promise,
      released.promise,
    );
    expect(
      await runManagedActionWrapperWithSeams({
        ...f.seams,
        hostProcessRunner: async () => {
          throw error;
        },
      }),
    ).toBe(1);
    expect(f.bridge.sealAdmission).toHaveBeenCalledOnce();
    expect(f.bridge.stopAndDrain).not.toHaveBeenCalled();
    expect(f.bridge.cleanup).not.toHaveBeenCalled();
    expect(f.payload.dispose).not.toHaveBeenCalled();
    expect(f.seams.fatalExit).not.toHaveBeenCalled();
    noPresentation(f);
    released.resolve();
    await Promise.resolve();
    expect(f.payload.dispose).not.toHaveBeenCalled();
    expect((await stat(f.treeRoot)).isDirectory()).toBe(true);
    closed.resolve();
    await f.retention();
    expect(f.bridge.stopAndDrain).toHaveBeenCalledOnce();
    expect(f.bridge.cleanup).toHaveBeenCalledOnce();
    expect(f.payload.dispose).toHaveBeenCalledOnce();
    noPresentation(f);
  });

  it('returns finite internal failure without deleting or presenting during never-confirmed observation', async () => {
    const f = await fixture();
    const closed = deferred(),
      released = deferred();
    await runManagedActionWrapperWithSeams({
      ...f.seams,
      hostProcessRunner: async () => {
        throw new ManagedHostProcessTerminationUnconfirmedError(closed.promise, released.promise);
      },
    });
    try {
      let settled = false;
      void f.retention().then(() => {
        settled = true;
      });
      await new Promise<void>((resolve) => setTimeout(resolve, 20));
      expect(settled).toBe(false);
      expect(f.payload.dispose).not.toHaveBeenCalled();
      expect(f.bridge.cleanup).not.toHaveBeenCalled();
      expect(f.seams.fatalExit).not.toHaveBeenCalled();
      noPresentation(f);
    } finally {
      closed.resolve();
      released.resolve();
      await f.retention();
    }
  });

  it.each([false, true])(
    'keeps independent bridge cleanup after resource release failure (retryable=%s)',
    async (retryable) => {
      const f = await fixture();
      const closed = deferred(),
        released = deferred();
      const retry = vi.fn(async () => {});
      await runManagedActionWrapperWithSeams({
        ...f.seams,
        hostProcessRunner: async () => {
          throw new ManagedHostProcessTerminationUnconfirmedError(closed.promise, released.promise);
        },
      });
      released.reject(
        retryable ? new ManagedRuntimeCleanupError(retry) : new Error('PRIVATE_RELEASE'),
      );
      await Promise.resolve();
      expect(retry).not.toHaveBeenCalled();
      closed.resolve();
      if (retryable) {
        await f.retention();
        expect(retry).toHaveBeenCalledOnce();
        expect(f.payload.dispose).toHaveBeenCalledOnce();
      } else {
        await expect(f.retention()).rejects.toThrow('wrapper_retained_resource_cleanup_failed');
        expect(f.payload.dispose).not.toHaveBeenCalled();
        expect((await stat(f.runtimeRoot)).isDirectory()).toBe(true);
      }
      expect(f.bridge.cleanup).toHaveBeenCalledOnce();
      noPresentation(f);
    },
  );

  it.each(
    ['quiescence-false', 'quiescence-reject', 'drain-reject'].flatMap((bridgeFailure) =>
      ['released', 'retry', 'failed-retry', 'unknown-release'].map((release) => ({
        bridgeFailure,
        release,
      })),
    ),
  )(
    'defers late $bridgeFailure fatal until independent $release settlement',
    async ({ bridgeFailure, release }) => {
      const f = await fixture();
      const closed = deferred(),
        released = deferred(),
        retryFinished = deferred();
      const dispose = f.payload.dispose;
      f.payload.dispose = vi.fn(async () => {
        await dispose();
        f.events.push('owner-disposed');
      });
      vi.mocked(f.seams.fatalExit).mockImplementation(() => {
        f.events.push('fatal');
        noPresentation(f);
      });
      const retry = vi.fn(async () => {
        await retryFinished.promise;
      });
      vi.mocked(f.bridge.stopAndDrain).mockImplementation(async () => {
        f.events.push('drain');
        if (bridgeFailure === 'drain-reject') throw new Error('PRIVATE_DRAIN');
      });
      await runManagedActionWrapperWithSeams({
        ...f.seams,
        bridgeRuntime: async (input) => {
          await input.executorFactory(f.bridge.stagingRoot);
          return f.bridge;
        },
        createArtifactExecutor: async (_context, tracker) => {
          vi.spyOn(tracker, 'awaitQuiescence').mockImplementation(async () => {
            if (bridgeFailure === 'quiescence-reject') throw new Error('PRIVATE_QUIESCENCE');
            return false;
          });
          return {
            execute: async () => {
              throw new Error('unused');
            },
          };
        },
        hostProcessRunner: async () => {
          throw new ManagedHostProcessTerminationUnconfirmedError(closed.promise, released.promise);
        },
      });
      const retained = expect(f.retention()).rejects.toThrow(
        'wrapper_retained_resource_cleanup_failed',
      );
      closed.resolve();
      // Flush both branches after actual Host close while resource release stays pending.
      await new Promise<void>((resolve) => setImmediate(resolve));
      expect(f.bridge.stopAndDrain).toHaveBeenCalledOnce();
      expect(f.seams.fatalExit).not.toHaveBeenCalled();
      expect(f.payload.dispose).not.toHaveBeenCalled();
      expect((await stat(f.runtimeRoot)).isDirectory()).toBe(true);
      noPresentation(f);
      if (release === 'released') released.resolve();
      else if (release === 'unknown-release') released.reject(new Error('PRIVATE_RELEASE'));
      else {
        released.reject(new ManagedRuntimeCleanupError(retry));
        await new Promise<void>((resolve) => setImmediate(resolve));
        expect(retry).toHaveBeenCalledOnce();
        expect(f.seams.fatalExit).not.toHaveBeenCalled();
        expect(f.payload.dispose).not.toHaveBeenCalled();
        noPresentation(f);
        if (release === 'retry') retryFinished.resolve();
        else retryFinished.reject(new Error('PRIVATE_RETRY'));
      }
      await retained;
      expect(f.seams.fatalExit).toHaveBeenCalledExactlyOnceWith(1);
      expect(f.bridge.cleanup).not.toHaveBeenCalled();
      if (release === 'released' || release === 'retry') {
        expect(f.payload.dispose).toHaveBeenCalledOnce();
        expect(f.events.indexOf('owner-disposed')).toBeLessThan(f.events.indexOf('fatal'));
        await expect(stat(f.runtimeRoot)).rejects.toThrow();
      } else {
        expect(f.payload.dispose).not.toHaveBeenCalled();
        expect((await stat(f.treeRoot)).isDirectory()).toBe(true);
        expect((await stat(f.runtimeRoot)).isDirectory()).toBe(true);
      }
      noPresentation(f);
    },
  );

  it('observes late owner cleanup failure without unhandled rejection or later presentation', async () => {
    const f = await fixture();
    const closed = deferred(),
      released = deferred();
    f.payload.dispose = vi.fn(async () => {
      throw new Error('PRIVATE_CLEANUP');
    });
    await runManagedActionWrapperWithSeams({
      ...f.seams,
      hostProcessRunner: async () => {
        throw new ManagedHostProcessTerminationUnconfirmedError(closed.promise, released.promise);
      },
    });
    closed.resolve();
    released.resolve();
    await expect(f.retention()).rejects.toThrow('wrapper_retained_resource_cleanup_failed');
    expect(f.bridge.cleanup).toHaveBeenCalledOnce();
    expect((await stat(f.treeRoot)).isDirectory()).toBe(true);
    noPresentation(f);
  });

  it('preserves fatal no-presentation semantics on confirmed Host but failed official quiescence', async () => {
    const f = await fixture();
    const pending = deferred();
    try {
      const exit = await runManagedActionWrapperWithSeams({
        ...f.seams,
        bridgeRuntime: async (input) => {
          await input.executorFactory(f.bridge.stagingRoot);
          return f.bridge;
        },
        createArtifactExecutor: async (_context, tracker) => {
          const client = tracker.wrap({ call: () => pending.promise });
          void client.call();
          return {
            execute: async () => {
              throw new Error('unused');
            },
          };
        },
      });
      expect(exit).toBe(1);
      expect(f.seams.fatalExit).toHaveBeenCalledWith(1);
      expect(f.bridge.cleanup).not.toHaveBeenCalled();
      noPresentation(f);
    } finally {
      pending.resolve();
    }
  });

  it.runIf(process.platform === 'linux')(
    'blocks a real already-connected idle bridge command after unconfirmed failure',
    async () => {
      const f = await fixture();
      const closed = deferred(),
        released = deferred();
      let activeBridge: ArtifactBridgeRuntime | undefined;
      let socket: net.Socket | undefined;
      const executor = vi.fn(async () => {
        throw new Error('must not start official operations');
      });
      try {
        expect(
          await runManagedActionWrapperWithSeams({
            ...f.seams,
            bridgeRuntime: async (input) => {
              activeBridge = await startArtifactBridgeRuntime(input);
              return activeBridge;
            },
            createArtifactExecutor: executor,
            hostProcessRunner: async (request) => {
              socket = net.createConnection(
                parseLaunchDocument(request.launchBytes).upload_endpoint,
              );
              await new Promise<void>((resolve, reject) => {
                socket!.once('connect', resolve);
                socket!.once('error', reject);
              });
              throw new ManagedHostProcessTerminationUnconfirmedError(
                closed.promise,
                released.promise,
              );
            },
          }),
        ).toBe(1);
        expect((await stat(activeBridge!.endpoint)).isSocket()).toBe(true);
        const disconnected = new Promise<void>((resolve) => socket!.once('close', () => resolve()));
        socket!.end(
          encodeCommandMessage({
            build_discriminator: 'r7-d0',
            payload: {
              operation: 'list_exact',
              correlation_id: 'idle-after-unconfirmed',
              name: 'opaque-name',
              maximum_objects: '8',
            },
          }),
        );
        await disconnected;
        expect(executor).not.toHaveBeenCalled();
        expect((await stat(activeBridge!.stagingRoot)).isDirectory()).toBe(true);
        expect(f.payload.dispose).not.toHaveBeenCalled();
        noPresentation(f);
        closed.resolve();
        released.resolve();
        await f.retention();
        await expect(stat(activeBridge!.tempRoot)).rejects.toThrow();
        noPresentation(f);
      } finally {
        socket?.destroy();
        closed.resolve();
        released.resolve();
        await f.retention()?.catch(() => {});
        await activeBridge?.stopAndDrain().catch(() => {});
        await activeBridge?.cleanup().catch(() => {});
      }
    },
  );
});
