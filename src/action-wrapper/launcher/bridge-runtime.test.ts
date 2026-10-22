import net from 'node:net';
import { chmod, mkdir, mkdtemp, realpath, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { encodeCommandMessage } from '../artifact-bridge/historical-upload-framing.fixture.js';
import { ArtifactBridgeStaging } from '../artifact-bridge/historical-staging.fixture.js';
import { startArtifactBridgeRuntime, type ArtifactBridgeRuntime } from './bridge-runtime.js';
import { OfficialCallTracker } from './official-calls.js';

let runtime: ArtifactBridgeRuntime | undefined;
const roots: string[] = [];

afterEach(async () => {
  if (runtime) {
    await runtime.stopAndDrain().catch(() => undefined);
    await runtime.cleanup().catch(() => undefined);
    runtime = undefined;
  }
  vi.unstubAllEnvs();
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

describe.runIf(process.platform === 'linux')('W1 S2 bridge composition', () => {
  it('listens before use, constructs S2 lazily once, and returns opaque results', async () => {
    let factories = 0;
    let executions = 0;
    let disposals = 0;
    let drains = 0;
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => {
        factories += 1;
        return {
          execute: async (command) => {
            executions += 1;
            return {
              operation: 'list_exact',
              correlation_id: command.correlation_id,
              failure: 'none',
              complete: true,
              objects: [],
            };
          },
          dispose: () => {
            disposals += 1;
          },
          stopAndDrain: async () => {
            drains += 1;
          },
        };
      },
    });
    expect(factories).toBe(0);
    const stagingStat = await stat(runtime.stagingRoot);
    expect(stagingStat.isDirectory()).toBe(true);
    expect(stagingStat.mode & 0o777).toBe(0o700);
    await expect(ArtifactBridgeStaging.create(runtime.stagingRoot)).resolves.toBeInstanceOf(
      ArtifactBridgeStaging,
    );
    await expect(executeListExact(runtime)).resolves.toEqual({
      operation: 'list_exact',
      correlation_id: 'w1-correlation',
      failure: 'none',
      complete: true,
      objects: [],
    });
    expect(factories).toBe(1);
    expect(executions).toBe(1);
    await runtime.stopAndDrain();
    await runtime.cleanup();
    expect(drains).toBe(1);
    expect(disposals).toBe(1);
    await expect(rm(runtime.tempRoot)).rejects.toThrow();
    runtime = undefined;
  });

  it('removes the private temp root even when executor disposal fails', async () => {
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => ({
        execute: async (command) => ({
          operation: 'list_exact',
          correlation_id: command.correlation_id,
          failure: 'none',
          complete: true,
          objects: [],
        }),
        dispose: () => {
          throw new Error('dispose-failure');
        },
      }),
    });
    const activeRuntime = runtime;
    const tempRoot = activeRuntime.tempRoot;
    await expect(executeListExact(activeRuntime)).resolves.toEqual({
      operation: 'list_exact',
      correlation_id: 'w1-correlation',
      failure: 'none',
      complete: true,
      objects: [],
    });
    await activeRuntime.stopAndDrain();
    await expect(activeRuntime.cleanup()).rejects.toThrow('dispose-failure');
    await expect(stat(tempRoot)).rejects.toThrow();
    runtime = undefined;
  });

  it('refuses a command sent by an already connected idle socket after stop begins', async () => {
    let executions = 0;
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => ({
        execute: async () => {
          executions += 1;
          return {
            operation: 'list_exact',
            correlation_id: 'unexpected',
            failure: 'none',
            complete: true,
            objects: [],
          };
        },
      }),
    });
    const activeRuntime = runtime;
    const socket = net.createConnection(activeRuntime.endpoint);
    await new Promise<void>((resolve, reject) => {
      socket.once('connect', resolve);
      socket.once('error', reject);
    });

    const stopping = activeRuntime.stopAndDrain();
    socket.end(
      encodeCommandMessage({
        build_discriminator: 'r4-h1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'after-stop',
          name: 'opaque-name',
          maximum_objects: '8',
        },
      }),
    );
    await collect(socket).catch(() => Buffer.alloc(0));
    await stopping;

    expect(executions).toBe(0);
    await activeRuntime.cleanup();
    runtime = undefined;
  });

  it('seals new commands on idle and new sockets without releasing the retained bridge', async () => {
    let executions = 0;
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => ({
        execute: async (command) => {
          executions += 1;
          return {
            operation: 'list_exact',
            correlation_id: command.correlation_id,
            failure: 'none',
            complete: true,
            objects: [],
          };
        },
      }),
    });
    const activeRuntime = runtime;
    await expect(executeListExact(activeRuntime, 'before-seal')).resolves.toMatchObject({
      failure: 'none',
    });
    expect(executions).toBe(1);
    const idle = net.createConnection(activeRuntime.endpoint);
    await new Promise<void>((resolve, reject) => {
      idle.once('connect', resolve);
      idle.once('error', reject);
    });
    expect(activeRuntime.sealAdmission).toBeTypeOf('function');
    activeRuntime.sealAdmission!();
    activeRuntime.sealAdmission!();
    const closed = new Promise<void>((resolve) => idle.once('close', () => resolve()));
    idle.end(
      encodeCommandMessage({
        build_discriminator: 'r4-h1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'idle-after-seal',
          name: 'opaque-name',
          maximum_objects: '8',
        },
      }),
    );
    await closed;
    await expect(executeListExact(activeRuntime, 'new-after-seal')).rejects.toThrow();
    expect(executions).toBe(1);
    expect((await stat(activeRuntime.endpoint)).isSocket()).toBe(true);
    expect((await stat(activeRuntime.stagingRoot)).isDirectory()).toBe(true);
    await activeRuntime.stopAndDrain();
    await activeRuntime.cleanup();
    await expect(stat(activeRuntime.tempRoot)).rejects.toThrow();
    runtime = undefined;
  });

  it('preserves already admitted reconciliation calls while new admission is sealed', async () => {
    let started!: () => void;
    const admitted = new Promise<void>((resolve) => {
      started = resolve;
    });
    let release!: () => void;
    const pending = new Promise<void>((resolve) => {
      release = resolve;
    });
    let officialCalls = 0;
    const tracker = new OfficialCallTracker();
    const client = tracker.wrap({
      call: async () => {
        officialCalls += 1;
      },
    });
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => ({
        execute: async (command) => {
          await client.call();
          started();
          await pending;
          // An admitted workflow can still issue its existing reconciliation call.
          await client.call();
          return {
            operation: 'list_exact',
            correlation_id: command.correlation_id,
            failure: 'none',
            complete: true,
            objects: [],
          };
        },
      }),
    });
    const activeRuntime = runtime;
    const response = executeListExact(activeRuntime, 'admitted-before-seal');
    try {
      await admitted;
      activeRuntime.sealAdmission!();
      expect(officialCalls).toBe(1);
      expect((await stat(activeRuntime.endpoint)).isSocket()).toBe(true);
      release();
      await expect(response).resolves.toMatchObject({ failure: 'none' });
      expect(officialCalls).toBe(2);
      await activeRuntime.stopAndDrain();
      expect(await tracker.awaitQuiescence(100)).toBe(true);
      await activeRuntime.cleanup();
      runtime = undefined;
    } finally {
      release();
    }
  });

  it('uses canonical runner storage despite hostile generic temporary-directory environment', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-bridge-storage-'));
    roots.push(root);
    const runnerTemp = path.join(root, 'runner');
    const workspace = path.join(root, 'workspace');
    await mkdir(runnerTemp, { mode: 0o700 });
    await mkdir(workspace, { mode: 0o700 });
    for (const name of ['TMPDIR', 'TMP', 'TEMP']) vi.stubEnv(name, workspace);
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r7-d0',
      runnerStorage: { runnerTemp, workspace },
      executorFactory: async () => {
        throw new Error('must stay lazy');
      },
    });
    expect(path.dirname(runtime.tempRoot)).toBe(await realpath(runnerTemp));
    expect((await stat(runtime.tempRoot)).mode & 0o777).toBe(0o700);
    expect((await stat(runtime.endpoint)).mode & 0o777).toBe(0o600);
  });

  it('removes managed sealed scratch after disposal failure and keeps concurrent cleanup rejected', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-bridge-storage-'));
    roots.push(root);
    const runnerTemp = path.join(root, 'runner');
    const workspace = path.join(root, 'workspace');
    await mkdir(runnerTemp, { mode: 0o700 });
    await mkdir(workspace, { mode: 0o700 });
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      runnerStorage: { runnerTemp, workspace },
      executorFactory: async () => ({
        execute: async (command) => ({
          operation: 'list_exact',
          correlation_id: command.correlation_id,
          failure: 'none',
          complete: true,
          objects: [],
        }),
        dispose: () => {
          throw new Error('dispose-failure');
        },
      }),
    });
    await executeListExact(runtime);
    const sealed = path.join(runtime.tempRoot, 'sealed');
    await mkdir(sealed, { mode: 0o500 });
    await runtime.stopAndDrain();
    const results = await Promise.allSettled([runtime.cleanup(), runtime.cleanup()]);
    for (const result of results) {
      expect(result.status).toBe('rejected');
      if (result.status === 'rejected') {
        expect(result.reason).toBeInstanceOf(AggregateError);
        expect(result.reason.errors[0].message).toBe('dispose-failure');
      }
    }
    await expect(stat(runtime.tempRoot)).rejects.toMatchObject({ code: 'ENOENT' });
    await expect(runtime.cleanup()).rejects.toThrow('wrapper_managed_bridge_cleanup_failed');
    runtime = undefined;
  });

  it('rejects reviewed-workspace and shared-writable runner storage before bridge creation', async () => {
    const root = await mkdtemp(path.join(tmpdir(), 'apr-bridge-storage-'));
    roots.push(root);
    const runnerTemp = path.join(root, 'runner');
    const workspace = path.join(root, 'workspace');
    await mkdir(runnerTemp, { mode: 0o700 });
    await mkdir(workspace, { mode: 0o700 });
    const start = (parent: string) =>
      startArtifactBridgeRuntime({
        buildDiscriminator: 'r7-d0',
        runnerStorage: { runnerTemp: parent, workspace },
        executorFactory: async () => {
          throw new Error('must stay lazy');
        },
      });
    await expect(start(workspace)).rejects.toThrow('wrapper_bridge_invalid');
    await chmod(runnerTemp, 0o777);
    await expect(start(runnerTemp)).rejects.toThrow('wrapper_bridge_invalid');
    await chmod(runnerTemp, 0o700);
    await chmod(root, 0o777);
    await expect(start(runnerTemp)).rejects.toThrow('wrapper_bridge_invalid');
    await chmod(root, 0o700);
  });
});

async function executeListExact(
  activeRuntime: ArtifactBridgeRuntime,
  correlationId = 'w1-correlation',
): Promise<{ readonly failure: string }> {
  const socket = net.createConnection(activeRuntime.endpoint);
  await new Promise<void>((resolve, reject) => {
    socket.once('connect', resolve);
    socket.once('error', reject);
  });
  const response = collect(socket);
  socket.write(
    encodeCommandMessage({
      build_discriminator: 'r4-h1',
      payload: {
        operation: 'list_exact',
        correlation_id: correlationId,
        name: 'opaque-name',
        maximum_objects: '8',
      },
    }),
  );
  const bytes = await response;
  const length = bytes.readUInt32BE(0);
  const result = JSON.parse(bytes.subarray(4, 4 + length).toString('utf8')) as {
    readonly payload: { readonly failure: string };
  };
  return result.payload;
}

function collect(socket: net.Socket): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    socket.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
    socket.once('end', () => resolve(Buffer.concat(chunks)));
    socket.once('error', reject);
  });
}
