import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { access, chmod, mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import type { ArtifactBridgeExecutor } from './artifact-bridge/index.js';
import {
  createProductionArtifactExecutor,
  readProductionRuntimeFacts,
  runPrivateActionWrapperWithSeams,
} from './index.js';
import { parseLaunchDocument, type ActionRuntimeFacts } from './launcher/contracts.js';
import { HostProcessTerminationUnconfirmedError } from './launcher/host-process.js';
import { OfficialCallTracker } from './launcher/official-calls.js';
import type { PreparedPayloadProof } from './launcher/prepared-payload.js';
import type { ActionPresentationToolkit } from './presentation/toolkit.js';

const roots: string[] = [];
const fullWorkflowRef =
  'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main';

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
        expect(Object.keys(request).sort()).toEqual([
          'executablePath',
          'launchBytes',
          'signal',
          'tempRoot',
        ]);
        const launch = parseLaunchDocument(request.launchBytes);
        expect(launch.workflow_ref).toBe(fullWorkflowRef);
        expect(launch.inputs.github_token).toBe('github-canary');
        return { completionBytes: validCompletion(), exitCode: 0 };
      },
      fatalExit: () => events.push('fatal'),
    });
    expect(exit).toBe(0);
    expect(events.indexOf('mask:github-canary')).toBeLessThan(events.indexOf('bridge:start'));
    expect(events).toContain('bridge:drain');
    expect(events).toContain('bridge:cleanup');
    expect(events.at(-1)).toBe('summary');
    expect(presentation.summaries[0]).not.toContain('github-canary');
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
        return { completionBytes: validCompletion(), exitCode: 0 };
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
      hostProcessRunner: async () => ({ completionBytes: validCompletion(), exitCode: 0 }),
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

  it('fatally exits without bridge cleanup or presentation when Host close is unconfirmed', async () => {
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
    expect(drained).toBe(0);
    expect(cleaned).toBe(0);
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

async function wrapperFixture(): Promise<{
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
      buildDiscriminator: 'r4-h1',
      wrapperBuildDiscriminator: 'r4-h1',
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

function validCompletion(): Buffer {
  return Buffer.from(
    JSON.stringify({
      build_discriminator: 'r4-h1',
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
