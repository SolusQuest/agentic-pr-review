import { execFileSync, spawn } from 'node:child_process';
import { EventEmitter } from 'node:events';
import { chmodSync } from 'node:fs';
import {
  chmod,
  cp,
  mkdtemp,
  readFile,
  readdir,
  rename,
  rm,
  stat,
  symlink,
  writeFile,
} from 'node:fs/promises';
import path from 'node:path';
import { tmpdir } from 'node:os';
import { sha256, type ManagedReceipt } from '../../scripts/release/build-payload.format.mjs';
import {
  buildLaunchDocument,
  serializeLaunchDocument,
} from '../../src/action-wrapper/launcher/contracts.js';
import { parseCompletionDocument } from '../../src/action-wrapper/presentation/completion.js';
import { startArtifactBridgeRuntime } from '../../src/action-wrapper/launcher/bridge-runtime.js';
import { PassThrough } from 'node:stream';
import { afterAll, afterEach, beforeAll, beforeEach, describe, expect, test, vi } from 'vitest';

import { H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES } from '../../src/action-wrapper/launcher/contracts.js';
import {
  encodeFrame,
  managedChildEnvironment,
  ManagedHostProcessTerminationUnconfirmedError,
  runManagedHostProcess as executeManagedHostProcess,
} from '../../src/action-wrapper/launcher/host-process.js';
import {
  admitManagedRuntime,
  ManagedRuntimeCleanupError,
  ManagedRuntimePrerequisiteError,
} from '../../src/action-wrapper/launcher/managed-runtime.js';
import {
  acquireVerifiedManagedTreeLease,
  ManagedTreeInUseError,
  type VerifiedManagedTree,
} from '../../src/action-wrapper/launcher/payload-staging.js';
import {
  buildLoaderFixture,
  acquireManagedArchiveThroughFakeRelease,
  managedContext,
  minimalManagedFiles,
  minimalRuntime,
  packageManagedFiles,
  unsafeManagedControl,
  waitForFile,
} from './managed-host-fixture.js';

const control = vi.hoisted(() => ({
  realSpawn: undefined as unknown as typeof import('node:child_process').spawn,
}));
vi.mock('node:child_process', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:child_process')>();
  control.realSpawn = actual.spawn;
  return { ...actual, spawn: vi.fn(actual.spawn) };
});
const activeChildren = new Map<ReturnType<typeof spawn>, Promise<void>>();
const runningCalls: ReturnType<typeof executeManagedHostProcess>[] = [];
const deferredCleanup: Promise<void>[] = [];
function runManagedHostProcess(request: Parameters<typeof executeManagedHostProcess>[0]) {
  const running = executeManagedHostProcess(request);
  runningCalls.push(running);
  void running.catch(() => {});
  return running;
}
const trackedSpawn = ((...args: Parameters<typeof spawn>) => {
  const child = control.realSpawn(...args);
  activeChildren.set(
    child,
    new Promise<void>((resolve) =>
      child.once('close', () => {
        activeChildren.delete(child);
        resolve();
      }),
    ),
  );
  return child;
}) as typeof spawn;
beforeEach(() => {
  vi.mocked(spawn).mockImplementation(trackedSpawn);
});
const supported = process.platform === 'linux' && process.arch === 'x64';
const actualProof = supported && process.env.APR_CR1_MANAGED_LOADER_PROOF === '1';
if (process.env.APR_CR1_MANAGED_LOADER_PROOF === '1' && !supported)
  throw new Error('managed_loader_proof_requires_linux_x64');
if (process.env.APR_CR1_MANAGED_APR_LOADER_PROOF === '1' && !actualProof)
  throw new Error('actual_apr_loader_proof_requires_loader_fixture');
const contexts: Awaited<ReturnType<typeof managedContext>>[] = [];
afterEach(async () => {
  const closing = [...activeChildren.entries()].map(([child, closed]) => {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    return closed;
  });
  let timeout: NodeJS.Timeout | undefined;
  try {
    await Promise.race([
      Promise.all(closing),
      new Promise<never>((_resolve, reject) => {
        timeout = setTimeout(
          () => reject(new Error('managed_fixture_termination_unconfirmed')),
          5000,
        );
      }),
    ]);
    const outcomes = await Promise.allSettled(runningCalls.splice(0));
    for (const outcome of outcomes)
      if (
        outcome.status === 'rejected' &&
        outcome.reason instanceof ManagedHostProcessTerminationUnconfirmedError
      )
        await outcome.reason.resourcesReleased;
  } finally {
    if (timeout) clearTimeout(timeout);
  }
  await Promise.all(deferredCleanup.splice(0));
  vi.mocked(spawn).mockImplementation(control.realSpawn);
  vi.restoreAllMocks();
  await Promise.all(contexts.splice(0).map((context) => context.close()));
});
async function context(files = minimalManagedFiles()) {
  const c = await managedContext(packageManagedFiles(files));
  contexts.push(c);
  return c;
}
function mockChild() {
  const child = Object.assign(new EventEmitter(), {
    stdin: new PassThrough(),
    stdout: new PassThrough(),
    stderr: new PassThrough(),
    exitCode: null as number | null,
    signalCode: null as NodeJS.Signals | null,
    kill: vi.fn((_signal?: NodeJS.Signals) => true),
  });
  child.stdin.resume();
  queueMicrotask(() => child.emit('spawn'));
  return {
    child,
    close(body?: Buffer, code = 0) {
      child.exitCode = code;
      child.stdout.end(body);
      child.stderr.end();
      child.emit('close', code, null);
    },
    asChild: child as unknown as ReturnType<typeof spawn>,
  };
}
function mockRuntimeProbe(host: (options: any) => ReturnType<typeof spawn>, output?: Buffer) {
  vi.mocked(spawn).mockImplementation(((_file: string, args: string[], options: any) => {
    if (args[0] !== '--list-runtimes') return host(options);
    const probe = mockChild();
    queueMicrotask(() =>
      probe.close(
        output ??
          Buffer.from(
            `Microsoft.NETCore.App 10.0.9 [${path.dirname(options.argv0)}/shared/Microsoft.NETCore.App]\n`,
          ),
      ),
    );
    return probe.asChild;
  }) as typeof spawn);
}
function request(c: Awaited<ReturnType<typeof managedContext>>, runtime: string, extra = {}) {
  return {
    tree: c.payload.tree,
    trustedRuntimeRoot: runtime,
    workspace: c.workspace,
    tempRoot: c.tempRoot,
    signal: new AbortController().signal,
    launchBytes: Buffer.from('{}'),
    cancellationKillGraceMs: 20,
    postKillCloseGraceMs: 1000,
    ...extra,
  };
}

describe('managed launcher input and environment boundary', () => {
  test.each(['abort', 'profile', 'duration', 'admission'] as const)(
    'clears the owned snapshot on early %s failure without changing the caller buffer',
    async (mode) => {
      const source = Buffer.from('PRIVATE_SNAPSHOT');
      let snapshot!: Buffer;
      const original = Buffer.from;
      const spy = vi.spyOn(Buffer, 'from').mockImplementation((...args: any[]) => {
        const copy = Reflect.apply(original, Buffer, args) as Buffer;
        if (args[0] === source) snapshot = copy;
        return copy;
      });
      try {
        await expect(
          runManagedHostProcess({
            tree: {} as VerifiedManagedTree,
            trustedRuntimeRoot: '/unused',
            workspace: '/unused',
            tempRoot: '/unused',
            signal: mode === 'abort' ? AbortSignal.abort() : new AbortController().signal,
            launchBytes: source,
            ...(mode === 'profile' ? { requestBudgetProfile: 'untrusted' as never } : {}),
            ...(mode === 'duration' ? { postKillCloseGraceMs: -1 } : {}),
          }),
        ).rejects.toThrow();
        expect(snapshot).toBeDefined();
        expect(snapshot.every((byte) => byte === 0)).toBe(true);
        expect(source.toString()).toBe('PRIVATE_SNAPSHOT');
      } finally {
        spy.mockRestore();
      }
    },
  );
  test('builds an explicit diagnostic-disabled environment without ambient loader or credential authority', () => {
    vi.stubEnv('DOTNET_STARTUP_HOOKS', 'workspace-hook');
    vi.stubEnv('LD_PRELOAD', 'workspace-native');
    vi.stubEnv('GITHUB_TOKEN', 'secret-canary');
    try {
      expect(managedChildEnvironment('/private/scratch')).toEqual({
        TMPDIR: '/private/scratch',
        NO_COLOR: '1',
        DOTNET_NOLOGO: '1',
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
        DOTNET_EnableDiagnostics: '0',
      });
      expect(managedChildEnvironment('/private/scratch', 'measurement')).toHaveProperty(
        'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE',
        'measurement',
      );
    } finally {
      vi.unstubAllEnvs();
    }
  });
  test.each([Buffer.alloc(0), Buffer.alloc(H1_MAXIMUM_LAUNCH_DOCUMENT_BYTES + 1)])(
    'rejects invalid launch bounds before runtime or child admission',
    async (launchBytes) => {
      vi.mocked(spawn).mockClear();
      await expect(
        runManagedHostProcess({
          tree: {} as VerifiedManagedTree,
          trustedRuntimeRoot: '/unused',
          workspace: '/unused',
          tempRoot: '/unused',
          signal: new AbortController().signal,
          launchBytes,
        }),
      ).rejects.toThrow('wrapper_launch_invalid');
      expect(spawn).not.toHaveBeenCalled();
    },
  );
  test('rejects an invalid proof profile before acquiring resources or spawning', async () => {
    vi.mocked(spawn).mockClear();
    await expect(
      runManagedHostProcess({
        tree: {} as VerifiedManagedTree,
        trustedRuntimeRoot: '/unused',
        workspace: '/unused',
        tempRoot: '/unused',
        signal: new AbortController().signal,
        launchBytes: Buffer.from('{}'),
        requestBudgetProfile: 'untrusted' as never,
      }),
    ).rejects.toThrow('wrapper_host_process_failed');
    expect(spawn).not.toHaveBeenCalled();
  });

  test('rejects pre-abort without executing a prerequisite probe', async () => {
    vi.mocked(spawn).mockClear();
    await expect(
      runManagedHostProcess({
        tree: {} as VerifiedManagedTree,
        trustedRuntimeRoot: '/unused',
        workspace: '/unused',
        tempRoot: '/unused',
        signal: AbortSignal.abort(),
        launchBytes: Buffer.from('{}'),
      }),
    ).rejects.toThrow('wrapper_cancelled_before_spawn');
    expect(spawn).not.toHaveBeenCalled();
  });
});

describe.runIf(supported)('managed lease and lifecycle with a deterministic child seam', () => {
  test('snapshots request bytes and scalar selection before awaiting admission; fixed FD args select run explicitly', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base);
    let observed: any;
    const chunks: Buffer[] = [];
    mockRuntimeProbe((options) => {
      observed = options;
      const host = mockChild();
      host.child.stdin.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
      queueMicrotask(() => host.close(encodeFrame(Buffer.from('{"ok":true}'))));
      return host.asChild;
    });
    const input = request(c, runtime, { launchBytes: Buffer.from('{"original":true}') });
    let snapshot!: Buffer;
    const original = Buffer.from;
    const spy = vi.spyOn(Buffer, 'from').mockImplementation((...args: any[]) => {
      const copy = Reflect.apply(original, Buffer, args) as Buffer;
      if (args[0] === input.launchBytes) snapshot = copy;
      return copy;
    });
    const running = runManagedHostProcess(input);
    spy.mockRestore();
    expect(snapshot.toString()).toBe('{"original":true}');
    input.launchBytes.fill(32);
    input.trustedRuntimeRoot = c.workspace;
    input.tempRoot = c.workspace;
    const result = await running;
    expect(snapshot.every((byte) => byte === 0)).toBe(true);
    expect(JSON.parse(result.completionBytes.toString())).toEqual({ ok: true });
    expect(Buffer.concat(chunks).subarray(4).toString()).toBe('{"original":true}');
    expect(observed).toMatchObject({
      argv0: path.join(runtime, 'dotnet'),
      cwd: c.tempRoot,
      shell: false,
    });
    const hostCall = vi
      .mocked(spawn)
      .mock.calls.find((call) => (call[1] as string[])?.[0] === 'exec')!;
    expect(hostCall[0]).toBe('/proc/self/fd/3');
    expect(hostCall[1]).toEqual([
      'exec',
      '--runtimeconfig',
      '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.runtimeconfig.json',
      '--depsfile',
      '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.deps.json',
      '--fx-version',
      '10.0.9',
      '--roll-forward',
      'Disable',
      '/proc/self/fd/4/agentic-pr-review/AgenticPrReview.Runtime.dll',
      'run',
    ]);
    expect(observed.stdio).toHaveLength(5);
    await expect(stat(`/proc/self/fd/${observed.stdio[3]}`)).rejects.toThrow();
  });
  test('retains the authentic tree after an unconfirmed child and releases only on actual late close', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base),
      abort = new AbortController();
    const host = mockChild();
    let started!: () => void;
    const ready = new Promise<void>((resolve) => (started = resolve));
    mockRuntimeProbe(() => {
      started();
      return host.asChild;
    });
    const running = runManagedHostProcess(
      request(c, runtime, {
        signal: abort.signal,
        cancellationKillGraceMs: 5,
        postKillCloseGraceMs: 5,
      }),
    );
    await ready;
    abort.abort();
    const error = await running.catch((error: unknown) => error);
    try {
      expect(error).toBeInstanceOf(ManagedHostProcessTerminationUnconfirmedError);
      expect(host.child.kill.mock.calls.map((call) => call[0])).toEqual(['SIGTERM', 'SIGKILL']);
      await expect(c.payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
      expect(
        await readFile(
          path.join(c.payload.root, 'agentic-pr-review/AgenticPrReview.Runtime.dll'),
          'utf8',
        ),
      ).toContain('mock-only');
      let settled = false;
      void (error as ManagedHostProcessTerminationUnconfirmedError).resourcesReleased.then(
        () => (settled = true),
      );
      await new Promise((resolve) => setTimeout(resolve, 10));
      expect(settled).toBe(false);
    } finally {
      host.close();
      if (error instanceof ManagedHostProcessTerminationUnconfirmedError)
        await error.resourcesReleased;
    }
    await c.payload.dispose();
    expect(await readdir(c.parent)).toEqual([]);
  });
  test.each(['throw', 'error'])(
    'cleans a failed %s start without replacing its business failure',
    async (mode) => {
      const c = await context(),
        runtime = await minimalRuntime(c.base);
      mockRuntimeProbe(() => {
        if (mode === 'throw') throw new Error('PRIVATE_SPAWN_ERROR_CANARY');
        const child = mockChild();
        queueMicrotask(() => child.child.emit('error', new Error('PRIVATE_SPAWN_ERROR_CANARY')));
        return child.asChild;
      });
      // Asynchronous spawn failure must happen before the successful spawn event.
      if (mode === 'error')
        mockRuntimeProbe(() => {
          const child = Object.assign(new EventEmitter(), {
            stdin: new PassThrough(),
            stdout: new PassThrough(),
            stderr: new PassThrough(),
            exitCode: null,
            signalCode: null,
            kill: vi.fn(),
          });
          child.stdin.resume();
          queueMicrotask(() => {
            child.emit('error', new Error('PRIVATE_SPAWN_ERROR_CANARY'));
            child.stdout.end();
            child.stderr.end();
            child.emit('close', null, null);
          });
          return child as unknown as ReturnType<typeof spawn>;
        });
      await expect(runManagedHostProcess(request(c, runtime))).rejects.toThrow(
        'wrapper_host_process_failed',
      );
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    },
  );
  test.each([Buffer.from('wrong inventory'), Buffer.alloc(64 * 1024 + 1, 65)])(
    'rejects bad or oversized runtime probe output before Host admission',
    async (output) => {
      const c = await context(),
        runtime = await minimalRuntime(c.base);
      let hosts = 0;
      mockRuntimeProbe(() => {
        hosts++;
        throw new Error('unreachable');
      }, output);
      await expect(runManagedHostProcess(request(c, runtime))).rejects.toBeInstanceOf(
        ManagedRuntimePrerequisiteError,
      );
      expect(hosts).toBe(0);
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    },
  );
  test('cancellation during asynchronous runtime acquisition cleans guards without launching', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base),
      abort = new AbortController();
    vi.mocked(spawn).mockClear();
    const running = runManagedHostProcess(request(c, runtime, { signal: abort.signal }));
    queueMicrotask(() => abort.abort());
    await expect(running).rejects.toBeDefined();
    expect(spawn).not.toHaveBeenCalled();
    await c.payload.dispose();
    expect(await readdir(c.parent)).toEqual([]);
  });
  test('admits a later installed stable .NET10 patch without permanently freezing the minimum patch', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base);
    await cp(
      path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.9'),
      path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.10'),
      { recursive: true },
    );
    const admitted = await admitManagedRuntime({
      trustedRuntimeRoot: runtime,
      workspace: c.workspace,
      tempRoot: c.tempRoot,
      signal: new AbortController().signal,
    });
    try {
      expect(admitted.frameworkVersion).toBe('10.0.10');
      admitted.validateProbe(
        Buffer.from(`Microsoft.NETCore.App 10.0.10 [${runtime}/shared/Microsoft.NETCore.App]\n`),
      );
      await admitted.validate();
    } finally {
      await admitted.release();
    }
  });

  test('a structural token cannot bypass the real D2 registry', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base);
    let hosts = 0;
    mockRuntimeProbe(() => {
      hosts++;
      throw new Error('unreachable');
    });
    await expect(
      runManagedHostProcess(request(c, runtime, { tree: {} as VerifiedManagedTree })),
    ).rejects.toThrow('wrapper_managed_tree_invalid');
    expect(hosts).toBe(0);
  });
  test.each(['host', 'framework', 'root', 'parent-alias'])(
    'detects observed %s runtime substitution before application launch',
    async (kind) => {
      const c = await context(),
        runtime = await minimalRuntime(c.base);
      const admitted = await admitManagedRuntime({
        trustedRuntimeRoot: runtime,
        workspace: c.workspace,
        tempRoot: c.tempRoot,
        signal: new AbortController().signal,
      });
      try {
        if (kind === 'host') await writeFile(path.join(runtime, 'dotnet'), Buffer.alloc(128));
        else if (kind === 'framework')
          await writeFile(
            path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.9/System.Private.CoreLib.dll'),
            'changed',
          );
        else if (kind === 'root') {
          await rename(runtime, runtime + '-old');
          await minimalRuntime(c.base);
        } else {
          await rename(path.join(runtime, 'host/fxr'), path.join(runtime, 'host/original'));
          await symlink('original', path.join(runtime, 'host/fxr'));
        }
        await expect(admitted.validate()).rejects.toBeInstanceOf(ManagedRuntimePrerequisiteError);
      } finally {
        await admitted.release();
      }
    },
  );
  test('runtime guard cleanup is observable and retryable after transient close failure', async () => {
    const c = await context(),
      runtime = await minimalRuntime(c.base);
    const admitted = await admitManagedRuntime({
      trustedRuntimeRoot: runtime,
      workspace: c.workspace,
      tempRoot: c.tempRoot,
      signal: new AbortController().signal,
    });
    const close = vi
      .spyOn(admitted.executableHandle, 'close')
      .mockRejectedValueOnce(new Error('synthetic close failure'));
    const error = await admitted.release().catch((error: unknown) => error);
    expect(error).toBeInstanceOf(ManagedRuntimeCleanupError);
    expect(close).toHaveBeenCalledOnce();
    await (error as ManagedRuntimeCleanupError).retryCleanup();
    expect(admitted.executableHandle.fd).toBe(-1);
    await admitted.release();
  });
  test.each(['host-arch', 'coreclr-arch', 'empty-coreclr', 'missing-corelib'])(
    'rejects %s runtime prerequisites before any child executes',
    async (kind) => {
      const c = await context(),
        runtime = await minimalRuntime(c.base);
      const framework = path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.9');
      if (kind === 'missing-corelib') await rm(path.join(framework, 'System.Private.CoreLib.dll'));
      else if (kind === 'empty-coreclr')
        await writeFile(path.join(framework, 'libcoreclr.so'), Buffer.alloc(0));
      else {
        const named =
          kind === 'host-arch'
            ? path.join(runtime, 'dotnet')
            : path.join(framework, 'libcoreclr.so');
        const bytes = await readFile(named);
        bytes.writeUInt16LE(183, 18);
        await writeFile(named, bytes);
      }
      vi.mocked(spawn).mockClear();
      await expect(runManagedHostProcess(request(c, runtime))).rejects.toBeInstanceOf(
        ManagedRuntimePrerequisiteError,
      );
      expect(spawn).not.toHaveBeenCalled();
    },
  );

  test.each(['workspace', 'missing', 'writable', 'old'])(
    'rejects an unsuitable %s runtime before any child executes',
    async (kind) => {
      const c = await context(),
        runtime = await minimalRuntime(c.base);
      vi.mocked(spawn).mockClear();
      if (kind === 'writable')
        await chmod(path.join(runtime, 'host/fxr/10.0.9/libhostfxr.so'), 0o666);
      if (kind === 'old') {
        await rename(
          path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.9'),
          path.join(runtime, 'shared/Microsoft.NETCore.App/10.0.8'),
        );
      }
      await expect(
        runManagedHostProcess(
          request(
            c,
            kind === 'workspace'
              ? c.workspace
              : kind === 'missing'
                ? runtime + '/missing'
                : runtime,
          ),
        ),
      ).rejects.toBeInstanceOf(ManagedRuntimePrerequisiteError);
      expect(spawn).not.toHaveBeenCalled();
    },
  );
  test.each(['primary-hook', 'dev-sidecar', 'native-property', 'duplicate', 'bom', 'oversized'])(
    'rejects D1-valid %s configuration before Host spawn and releases the lease',
    async (kind) => {
      const files = minimalManagedFiles();
      const config = files[1]!;
      if (kind === 'dev-sidecar')
        files.push({
          path: 'AgenticPrReview.Runtime.runtimeconfig.dev.json',
          bytes: Buffer.from('{"runtimeOptions":{"additionalProbingPaths":["/workspace"]}}'),
        });
      else if (kind === 'duplicate')
        config.bytes = Buffer.from(
          config.bytes.toString().replace('"tfm":"net10.0"', '"tfm":"net10.0","tfm":"net10.0"'),
        );
      else if (kind === 'bom')
        config.bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), config.bytes]);
      else if (kind === 'oversized')
        config.bytes = Buffer.concat([Buffer.alloc(256 * 1024, 32), config.bytes]);
      else {
        const parsed = JSON.parse(config.bytes.toString());
        parsed.runtimeOptions.configProperties = {
          [kind === 'primary-hook' ? 'STARTUP_HOOKS' : 'System.Globalization.AppLocalIcu']:
            '/workspace/poison',
        };
        config.bytes = Buffer.from(JSON.stringify(parsed));
      }
      const c = await context(files),
        runtime = await minimalRuntime(c.base);
      let hosts = 0;
      mockRuntimeProbe(() => {
        hosts++;
        throw new Error('unreachable');
      });
      await expect(runManagedHostProcess(request(c, runtime))).rejects.toThrow(
        'wrapper_managed_loading_config_invalid',
      );
      expect(hosts).toBe(0);
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    },
  );
});

let fixture: Awaited<ReturnType<typeof buildLoaderFixture>>;
describe.runIf(actualProof)(
  'actual .NET managed, native and satellite loading',
  { timeout: 30_000 },
  () => {
    beforeAll(async () => {
      fixture = await buildLoaderFixture();
    }, 240_000);
    afterAll(async () => {
      if (activeChildren.size) throw new Error('managed_fixture_termination_unconfirmed');
      await fixture?.close();
    });
    async function realContext(files = fixture.files) {
      const c = await managedContext(packageManagedFiles(files), fixture.workspace);
      contexts.push(c);
      return c;
    }
    const decode = (result: Awaited<ReturnType<typeof runManagedHostProcess>>) =>
      JSON.parse(result.completionBytes.toString());
    test('loads all admitted kinds with the runtime-only FD host, literal run argv and private stderr', async () => {
      const c = await realContext();
      const result = await runManagedHostProcess(request(c, fixture.runtime));
      const body = decode(result);
      expect(body).toMatchObject({
        entry: 'verified-entry',
        managed: 'verified-managed',
        resource: 'verified-resource',
        native: 7001,
        args: ['run'],
        input: {},
      });
      expect(body.assembly).toBe(
        path.join(c.payload.root, 'agentic-pr-review/AgenticPrReview.Runtime.dll'),
      );
      expect(body.framework).toBe(
        path.join(
          fixture.runtime,
          `shared/Microsoft.NETCore.App/${fixture.frameworkVersion}/System.Private.CoreLib.dll`,
        ),
      );
      expect(body.env).toEqual(managedChildEnvironment(c.tempRoot));
      expect(result.trustedProofBudgetReceiptLines).toEqual([]);
      expect(JSON.stringify(result)).not.toContain('PRIVATE_MANAGED_STDERR_CANARY');
      await expect(stat(path.join(fixture.runtime, 'sdk'))).rejects.toThrow();
      await expect(stat(path.join(fixture.runtime, 'packs'))).rejects.toThrow();
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    });
    test('proves hostile entry/dependency/resource/native candidates are effective, then ignores workspace and private-cwd copies', async () => {
      const unsafe = await unsafeManagedControl(fixture.runtime, fixture.poison, Buffer.from('{}'));
      expect(unsafe.code).toBe(0);
      expect(JSON.parse(unsafe.stdout.subarray(4).toString())).toMatchObject({
        entry: 'poison-entry',
        managed: 'poison-managed',
        resource: 'poison-resource',
        native: 9009,
      });
      expect(await readFile(fixture.marker, 'utf8')).toBe('executed');
      await rm(fixture.marker);
      const c = await realContext();
      for (const destination of [fixture.workspace, c.tempRoot])
        for (const name of await readdir(fixture.poison))
          await cp(path.join(fixture.poison, name), path.join(destination, name), {
            recursive: true,
          });
      const body = decode(await runManagedHostProcess(request(c, fixture.runtime)));
      expect(body).toMatchObject({
        entry: 'verified-entry',
        managed: 'verified-managed',
        resource: 'verified-resource',
        native: 7001,
      });
      await expect(stat(fixture.marker)).rejects.toThrow();
    });
    test('startup-hook and native preload positive controls execute; closed production env rejects both and all credential/probing roots', async () => {
      for (const env of [
        { DOTNET_STARTUP_HOOKS: fixture.hook },
        { LD_PRELOAD: path.join(fixture.poison, 'libaprprobe.so') },
      ]) {
        await rm(fixture.marker, { force: true });
        expect(
          (await unsafeManagedControl(fixture.runtime, fixture.verified, Buffer.from('{}'), env))
            .code,
        ).toBe(0);
        expect(await readFile(fixture.marker, 'utf8')).toBe('executed');
      }
      await rm(fixture.marker);
      const c = await realContext();
      const ambient = {
        DOTNET_STARTUP_HOOKS: fixture.hook,
        DOTNET_ADDITIONAL_DEPS: path.join(fixture.poison, 'AgenticPrReview.Runtime.deps.json'),
        DOTNET_SHARED_STORE: fixture.poison,
        DOTNET_ROOT: fixture.poison,
        DOTNET_ROOT_X64: fixture.poison,
        DOTNET_HOST_PATH: path.join(fixture.poison, 'dotnet'),
        DOTNET_HOST_TRACE: '1',
        DOTNET_HOST_TRACEFILE: fixture.marker,
        COREHOST_TRACE: '1',
        COREHOST_TRACEFILE: fixture.marker,
        CORECLR_ENABLE_PROFILING: '1',
        CORECLR_PROFILER_PATH: path.join(fixture.poison, 'libaprprobe.so'),
        COMPlus_ReadyToRun: '0',
        LD_PRELOAD: path.join(fixture.poison, 'libaprprobe.so'),
        LD_LIBRARY_PATH: fixture.poison,
        LD_AUDIT: path.join(fixture.poison, 'libaprprobe.so'),
        PATH: fixture.poison,
        HOME: fixture.workspace,
        MSBuildSDKsPath: fixture.workspace,
        NUGET_PACKAGES: fixture.workspace,
        NuGetPackageSourceCredentials_solus: 'SYNTHETIC_RESTORE_SECRET',
        GITHUB_TOKEN: 'SYNTHETIC_GITHUB_SECRET',
        OPENAI_API_KEY: 'SYNTHETIC_PROVIDER_SECRET',
        ACTIONS_ID_TOKEN_REQUEST_TOKEN: 'SYNTHETIC_OIDC_SECRET',
      };
      for (const [name, value] of Object.entries(ambient)) vi.stubEnv(name, value);
      try {
        const body = decode(await runManagedHostProcess(request(c, fixture.runtime)));
        expect(body).toMatchObject({
          entry: 'verified-entry',
          managed: 'verified-managed',
          resource: 'verified-resource',
          native: 7001,
        });
        expect(body.env).toEqual(managedChildEnvironment(c.tempRoot));
        expect(JSON.stringify(body)).not.toContain('SYNTHETIC_');
        await expect(stat(fixture.marker)).rejects.toThrow();
      } finally {
        vi.unstubAllEnvs();
      }
    });
    test.each(['primary-hook', 'development-sidecar'])(
      'D3-PL01: real D1/D2 %s executes its unsafe control but production refuses before Host startup',
      async (kind) => {
        const files = fixture.files.map((file) => ({ ...file, bytes: Buffer.from(file.bytes) }));
        if (kind === 'development-sidecar')
          files.push({
            path: 'AgenticPrReview.Runtime.runtimeconfig.dev.json',
            bytes: Buffer.from(
              JSON.stringify({
                runtimeOptions: {
                  configProperties: { STARTUP_HOOKS: fixture.hook },
                  additionalProbingPaths: [fixture.workspace],
                },
              }),
            ),
          });
        else {
          const file = files.find(
            (file) => file.path === 'AgenticPrReview.Runtime.runtimeconfig.json',
          )!;
          const config = JSON.parse(file.bytes.toString());
          config.runtimeOptions.configProperties.STARTUP_HOOKS = fixture.hook;
          file.bytes = Buffer.from(JSON.stringify(config));
        }
        const c = await realContext(files);
        const lease = await acquireVerifiedManagedTreeLease(
          c.payload.tree,
          new AbortController().signal,
        );
        try {
          await rm(fixture.marker, { force: true });
          const unsafe = await unsafeManagedControl(
            fixture.runtime,
            path.join(c.payload.root, 'agentic-pr-review'),
            Buffer.from('{}'),
          );
          expect(unsafe.code).toBe(0);
          expect(await readFile(fixture.marker, 'utf8')).toBe('executed');
        } finally {
          lease.release();
        }
        await rm(fixture.marker);
        vi.mocked(spawn).mockClear();
        await expect(runManagedHostProcess(request(c, fixture.runtime))).rejects.toThrow(
          'wrapper_managed_loading_config_invalid',
        );
        expect(
          vi
            .mocked(spawn)
            .mock.calls.filter(
              (call) => call[0] === '/proc/self/fd/3' && (call[1] as string[])[0] === 'exec',
            ),
        ).toEqual([]);
        await expect(stat(fixture.marker)).rejects.toThrow();
        await c.payload.dispose();
        expect(await readdir(c.parent)).toEqual([]);
      },
    );
    test.each([
      'AgenticPrReview.Runtime.dll',
      'AgenticPrReview.Runtime.deps.json',
      'AgenticPrReview.Runtime.runtimeconfig.json',
      'Dependency.dll',
      'fr/Dependency.resources.dll',
      'libaprprobe.so',
    ])(
      'refuses observed non-entry or entry tampering of %s before Host execution',
      async (member) => {
        const c = await realContext();
        const named = path.join(c.payload.root, 'agentic-pr-review', member);
        await chmod(named, 0o600);
        await writeFile(named, 'tampered');
        vi.mocked(spawn).mockClear();
        await expect(runManagedHostProcess(request(c, fixture.runtime))).rejects.toThrow(
          'wrapper_managed_tree_invalid',
        );
        expect(
          vi.mocked(spawn).mock.calls.filter((call) => (call[1] as string[])[0] === 'exec'),
        ).toEqual([]);
      },
    );
    test('real live child keeps late dependency loading available through a finite busy disposal', async () => {
      const c = await realContext(),
        abort = new AbortController();
      const running = runManagedHostProcess(
        request(c, fixture.runtime, {
          launchBytes: Buffer.from('{"mode":"hold"}'),
          signal: abort.signal,
          cancellationKillGraceMs: 1000,
        }),
      );
      void running.catch(() => {});
      try {
        await waitForFile(path.join(c.tempRoot, 'ready'));
        await expect(c.payload.dispose()).rejects.toBeInstanceOf(ManagedTreeInUseError);
        expect(
          await stat(path.join(c.payload.root, 'agentic-pr-review/Dependency.dll')),
        ).toBeDefined();
        await writeFile(path.join(c.tempRoot, 'release'), 'release');
        expect(decode(await running)).toMatchObject({
          managed: 'verified-managed',
          resource: 'verified-resource',
          native: 7001,
        });
      } finally {
        abort.abort();
        await running.catch(() => {});
      }
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    });
    test.each(['cancel', 'hang'])(
      'real %s child confirms cooperative cancellation or SIGKILL before disposal',
      async (mode) => {
        const c = await realContext(),
          abort = new AbortController();
        const running = runManagedHostProcess(
          request(c, fixture.runtime, {
            launchBytes: Buffer.from(JSON.stringify({ mode })),
            signal: abort.signal,
            cancellationKillGraceMs: mode === 'cancel' ? 1000 : 20,
          }),
        );
        const observed = running.then(
          (result) => ({ result }),
          (error: unknown) => ({ error }),
        );
        await waitForFile(path.join(c.tempRoot, 'ready'));
        abort.abort();
        const outcome = await observed;
        if (mode === 'cancel')
          expect(
            decode(
              (outcome as { result: Awaited<ReturnType<typeof runManagedHostProcess>> }).result,
            ).cancelled,
          ).toBe(true);
        else
          expect((outcome as { error: Error }).error.message).toBe('wrapper_host_process_failed');
        await c.payload.dispose();
        expect(await readdir(c.parent)).toEqual([]);
      },
    );
    test('a real failed exec releases the lease and guards without entering managed code', async () => {
      const c = await realContext();
      vi.mocked(spawn).mockImplementation(((file: string, args: string[], options: any) => {
        if (args[0] === 'exec') chmodSync(path.join(fixture.runtime, 'dotnet'), 0o600);
        return trackedSpawn(file, args, options);
      }) as typeof spawn);
      try {
        await expect(runManagedHostProcess(request(c, fixture.runtime))).rejects.toThrow(
          'wrapper_host_process_failed',
        );
      } finally {
        chmodSync(path.join(fixture.runtime, 'dotnet'), 0o755);
      }
      await c.payload.dispose();
      expect(await readdir(c.parent)).toEqual([]);
    });

    test.each(['empty', 'trailing', 'oversized'])(
      'real %s completion preserves framed output bounds and cleanup',
      async (mode) => {
        const c = await realContext();
        await expect(
          runManagedHostProcess(
            request(c, fixture.runtime, { launchBytes: Buffer.from(JSON.stringify({ mode })) }),
          ),
        ).rejects.toThrow('wrapper_host_process_failed');
        await c.payload.dispose();
        expect(await readdir(c.parent)).toEqual([]);
      },
    );
    test.runIf(process.env.APR_CR1_MANAGED_APR_LOADER_PROOF === '1')(
      'actual exact-head APR archive executes its explicit run framed Host without SDK or package credentials',
      async () => {
        const outputParent = await mkdtemp(path.join(tmpdir(), 'apr-managed-loader-proof-'));
        try {
          const source = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
          const output = path.join(outputParent, 'output');
          const proof = execFileSync(
            process.execPath,
            [
              'scripts/release/build-payload.mjs',
              'build-managed',
              '--source',
              source,
              '--version',
              'v0.0.0-internal.1',
              '--output',
              output,
            ],
            { timeout: 600_000, maxBuffer: 4 * 1024 * 1024 },
          ).toString();
          const receipt = JSON.parse(
            await readFile(path.join(output, 'receipt.json'), 'utf8'),
          ) as ManagedReceipt;
          const archive = await readFile(path.join(output, receipt.archiveName));
          expect(receipt.identity.sourceCommit).toBe(source);
          expect(proof).toContain('"roundtrip":true');
          const c = await acquireManagedArchiveThroughFakeRelease(
            { archive, receipt },
            source,
            fixture.workspace,
          );
          contexts.push(c);
          expect(c.seen).toHaveLength(3);
          expect(c.seen.every((entry) => !entry.credential)).toBe(true);
          let bridgeCalls = 0;
          const bridge = await startArtifactBridgeRuntime({
            buildDiscriminator: 'r7-d0',
            executorFactory: async () => {
              bridgeCalls++;
              throw new Error('unsupported_event_must_not_use_bridge');
            },
          });
          const cleanupBridge = async () => {
            await bridge.stopAndDrain();
            await bridge.cleanup();
          };
          let releaseBridge = true;
          try {
            const event = Buffer.from(
              JSON.stringify({
                event_name: 'push',
                repository: { id: 42, full_name: 'SolusQuest/agentic-pr-review' },
                sender: { id: 7, login: 'synthetic' },
              }),
            );
            const eventPath = path.join(c.tempRoot, 'unsupported-event.json');
            await writeFile(eventPath, event);
            const launch = serializeLaunchDocument(
              buildLaunchDocument({
                inputs: [{ name: 'pr-number', value: '1' }],
                runtimeFacts: {
                  eventJsonPath: eventPath,
                  repositoryName: 'SolusQuest/agentic-pr-review',
                  repositoryId: '42',
                  runId: '900',
                  runAttempt: '1',
                  workflowPath: '.github/workflows/review.yml',
                  workflowRef: 'refs/heads/main',
                  workflowSha: source,
                },
                eventJsonSha256: sha256(event),
                prepared: {
                  actionSourceSha: source,
                  payloadSha256: receipt.archiveSha256,
                  buildDiscriminator: 'r7-d0',
                },
                artifactBridgeEndpoint: bridge.endpoint,
                cancellation: 'active',
              }),
            );
            const running = runManagedHostProcess(
              request(c, fixture.runtime, { launchBytes: launch }),
            );
            expect(await stat(bridge.endpoint)).toBeDefined();
            const result = await running;
            expect(await stat(bridge.endpoint)).toBeDefined();
            const completion = parseCompletionDocument(
              result.completionBytes,
              'r7-d0',
              result.exitCode,
            );
            expect(completion).toMatchObject({
              protocol: 'apr.cli.result.cr2',
              outputs: { status: 'skipped_untrusted_event', 'termination-reason': 'not_started' },
              process_exit_code: 0,
            });
            expect(bridgeCalls).toBe(0);
            expect(c.payload.identity).toEqual(receipt.identity);
            console.log(
              JSON.stringify({
                managedLoaderProof: true,
                sourceCommit: source,
                archiveSha256: receipt.archiveSha256,
                verifiedMembers: receipt.members.length,
                framedHostStatus: completion.outputs.status,
                runtimeOnly: true,
                credentialFreeTransport: true,
                syntheticReleaseMap: true,
              }),
            );
          } catch (error) {
            if (error instanceof ManagedHostProcessTerminationUnconfirmedError) {
              releaseBridge = false;
              const cleanup = error.resourcesReleased.then(cleanupBridge);
              deferredCleanup.push(cleanup);
              void cleanup.catch(() => {});
            }
            throw error;
          } finally {
            if (releaseBridge) await cleanupBridge();
          }
          await c.payload.dispose();
          expect(await readdir(c.parent)).toEqual([]);
          await expect(stat(bridge.tempRoot)).rejects.toThrow();
        } finally {
          await rm(outputParent, { recursive: true, force: true });
        }
      },
      660_000,
    );
  },
);
