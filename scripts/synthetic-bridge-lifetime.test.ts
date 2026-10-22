import { afterEach, describe, expect, it, vi } from 'vitest';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { connect, createServer, type Server, type Socket } from 'node:net';
import { randomUUID } from 'node:crypto';
import { spawn, type ChildProcess } from 'node:child_process';
import { EventEmitter, once } from 'node:events';
import { build } from 'esbuild';
import { ArtifactBridgeStaging } from '../src/action-wrapper/artifact-bridge/historical-staging.fixture.js';
import { SyntheticArtifactExecutor } from '../src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.js';
import { SyntheticHostBridgeLifetime } from '../src/action-wrapper/artifact-bridge/synthetic-bridge-lifetime.fixture.js';
import { ArtifactBridgeCorrelationRegistry } from '../src/action-wrapper/artifact-bridge/correlations.js';
import { ARTIFACT_BRIDGE_LIMITS } from '../src/action-wrapper/artifact-bridge/limits.js';
import { encodeCommandMessage } from '../src/action-wrapper/artifact-bridge/historical-upload-framing.fixture.js';
import type {
  ArtifactBridgeCommand,
  ArtifactBridgeResult,
} from '../src/action-wrapper/artifact-bridge/historical-upload-contracts.fixture.js';
import { digestBytes } from '../src/action-wrapper/artifact-bridge/historical-transport-envelope.fixture.js';
import { nextHostBridge } from '../runtime/tests/ActionHostFixture/bridge-host-control.mjs';

const cleanups: (() => Promise<void>)[] = [];
afterEach(async () => {
  for (const cleanup of cleanups.splice(0)) await cleanup();
});

async function setup() {
  const root = await mkdtemp(join(tmpdir(), 'apr-synthetic-lifetime-'));
  const control = join(root, 'control');
  const stagingRoot = join(root, 'staging');
  const endpoint = join(root, 'bridge.sock');
  await mkdir(control, { mode: 0o700 });
  await mkdir(join(stagingRoot, 'csharp', 'op'), { recursive: true, mode: 0o700 });
  const encrypted = Buffer.from('synthetic-encrypted-state-lifetime');
  await writeFile(join(stagingRoot, 'csharp/op/source.bin'), encrypted, { mode: 0o600 });
  await writeFile(join(stagingRoot, 'csharp/op/destination.bin'), Buffer.alloc(0), { mode: 0o600 });
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, control);
  const servers: Server[] = [];
  const registries: ArtifactBridgeCorrelationRegistry[] = [];
  const sockets = new Set<Socket>();
  const lifetime = new SyntheticHostBridgeLifetime(
    { endpoint, buildDiscriminator: 'lifetime-test', executor },
    (server, registry) => {
      servers.push(server);
      registries.push(registry);
      server.on('connection', (socket) => {
        sockets.add(socket);
        socket.once('close', () => sockets.delete(socket));
      });
    },
  );
  cleanups.push(async () => {
    for (const socket of sockets) socket.destroy();
    for (const server of servers)
      if (server.listening) await new Promise<void>((resolve) => server.close(() => resolve()));
    await rm(root, { recursive: true, force: true });
  });
  return {
    root,
    control,
    stagingRoot,
    endpoint,
    encrypted,
    lifetime,
    servers,
    registries,
    sockets,
  };
}

async function send(
  endpoint: string,
  command: ArtifactBridgeCommand,
  capture?: (socket: Socket) => void,
): Promise<ArtifactBridgeResult> {
  const socket = connect(endpoint);
  capture?.(socket);
  socket.write(encodeCommandMessage({ build_discriminator: 'lifetime-test', payload: command }));
  const chunks = [];
  for await (const chunk of socket) chunks.push(Buffer.from(chunk));
  const bytes = Buffer.concat(chunks);
  expect(bytes.length).toBeGreaterThan(4);
  const result = JSON.parse(bytes.subarray(4, 4 + bytes.readUInt32BE(0)).toString('utf8')).payload;
  // Let the actual server socket complete close before asking to retire it.
  await new Promise((resolve) => setImmediate(resolve));
  await new Promise((resolve) => setImmediate(resolve));
  return result;
}

function list(correlation_id = randomUUID()): ArtifactBridgeCommand {
  return { operation: 'list_exact', correlation_id, name: 'state-lifetime', maximum_objects: '4' };
}

async function spawnFixture() {
  const fixture = await setup();
  const bundle = join(fixture.root, 'bridge.mjs');
  await build({
    entryPoints: ['src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts'],
    outfile: bundle,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
  });
  const child = spawn(
    process.execPath,
    [
      bundle,
      fixture.endpoint,
      'lifetime-test',
      fixture.stagingRoot,
      fixture.control,
      '--per-host-bridge',
    ],
    { env: {}, stdio: ['ignore', 'pipe', 'ignore', 'ipc'] },
  );
  cleanups.unshift(async () => {
    if (child.connected) child.disconnect();
    if (child.exitCode === null && child.signalCode === null) {
      child.kill('SIGTERM');
      await once(child, 'exit');
    }
  });
  const [ready] = await once(child.stdout!, 'data', { signal: AbortSignal.timeout(3000) });
  expect(ready.toString()).toBe('READY\n');
  return { ...fixture, child };
}

async function control(child: ChildProcess, message: unknown) {
  const result = once(child, 'message', { signal: AbortSignal.timeout(3000) });
  child.send!(message as string);
  return (await result)[0];
}

async function disconnect(child: ChildProcess) {
  const exited = once(child, 'exit', { signal: AbortSignal.timeout(3000) });
  child.disconnect();
  return await exited;
}

describe('test-only sequential Host bridge lifetime', () => {
  it('acknowledges actual IPC only after a distinct bound listener preserves the stored object', async () => {
    const fixture = await spawnFixture();
    const first = await send(fixture.endpoint, {
      operation: 'upload_immutable',
      correlation_id: 'ipc-source',
      name: 'state-lifetime',
      source_relative_path: 'csharp/op/source.bin',
      encrypted_object_digest: digestBytes(fixture.encrypted),
      minimum_expires_at_unix_seconds: '1000',
    });
    expect(first.failure).toBe('none');
    await expect(nextHostBridge(fixture.child)).resolves.toBeUndefined();
    const metadata = first.metadata!;
    const read = await send(fixture.endpoint, {
      operation: 'metadata',
      correlation_id: 'ipc-read',
      name: metadata.name,
      object_id: metadata.object_id,
      maximum_bytes: '4096',
    });
    expect(read.metadata).toEqual(metadata);
    expect(await disconnect(fixture.child)).toEqual([0, null]);
  });

  it.each(['unknown', { next: 'next-host', secret: 'private-canary' }, null])(
    'actual IPC rejects unknown control %j with nonzero teardown',
    async (message) => {
      const fixture = await spawnFixture();
      expect(await control(fixture.child, message)).toBe('failed');
      expect(await disconnect(fixture.child)).toEqual([4, null]);
    },
  );

  it('actual IPC refuses a duplicate replacement before any next Host operation', async () => {
    const fixture = await spawnFixture();
    await send(fixture.endpoint, list());
    await expect(nextHostBridge(fixture.child)).resolves.toBeUndefined();
    await expect(nextHostBridge(fixture.child)).rejects.toThrow('bridge_host_lifetime_failed');
    expect(await disconnect(fixture.child)).toEqual([4, null]);
  });

  it.each(['unknown', 'failed', { ready: true, secret: 'private-canary' }])(
    'driver rejects response %j without echo or listener leaks',
    async (response) => {
      const child = Object.assign(new EventEmitter(), {
        connected: true,
        send: (_message: string, callback: (error?: Error) => void) => callback(),
      });
      const pending = nextHostBridge(child);
      child.emit('message', response);
      await expect(pending).rejects.toThrow(/^bridge_host_lifetime_failed$/);
      expect(child.eventNames()).toEqual([]);
    },
  );

  it('driver timeout is bounded, removes listeners and cannot accept late readiness', async () => {
    vi.useFakeTimers();
    try {
      const child = Object.assign(new EventEmitter(), {
        connected: true,
        send: (_message: string, callback: (error?: Error) => void) => callback(),
      });
      const pending = nextHostBridge(child);
      const rejected = expect(pending).rejects.toThrow(/^bridge_host_lifetime_failed$/);
      await vi.advanceTimersByTimeAsync(20_000);
      await rejected;
      expect(child.eventNames()).toEqual([]);
      expect(vi.getTimerCount()).toBe(0);
      child.emit('message', 'ready');
      await expect(pending).rejects.toThrow(/^bridge_host_lifetime_failed$/);
    } finally {
      vi.useRealTimers();
    }
  });

  it.each(['exit', 'error', 'send-throw', 'send-error', 'disconnected'])(
    'driver fails closed on %s',
    async (failure) => {
      const child = Object.assign(new EventEmitter(), {
        connected: failure !== 'disconnected',
        send: (_message: string, callback: (error?: Error) => void) => {
          if (failure === 'send-throw') throw new Error('private-canary');
          callback(failure === 'send-error' ? new Error('private-canary') : undefined);
        },
      });
      const pending = nextHostBridge(child);
      if (failure === 'exit') child.emit('exit', 0, null);
      if (failure === 'error') child.emit('error', new Error('private-canary'));
      await expect(pending).rejects.toThrow(/^bridge_host_lifetime_failed$/);
      expect(child.eventNames()).toEqual([]);
    },
  );

  it('retains saturated old registry, exact ciphertext/metadata/object IDs and faults across servers', async () => {
    const fixture = await setup();
    await fixture.lifetime.start();
    const upload: ArtifactBridgeCommand = {
      operation: 'upload_immutable',
      correlation_id: 'first-upload',
      name: 'state-lifetime',
      source_relative_path: 'csharp/op/source.bin',
      encrypted_object_digest: digestBytes(fixture.encrypted),
      minimum_expires_at_unix_seconds: '1000',
    };
    const first = await send(fixture.endpoint, upload);
    expect(first.failure).toBe('none');
    const metadata = first.metadata!;
    expect(metadata.object_id).toBe('1000');
    const old = fixture.registries[0];
    for (let index = old.terminalCount; index < 4096; index++) {
      expect(old.admit(`old-${index}`).accepted).toBe(true);
      old.complete(`old-${index}`);
    }
    expect(ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations).toBe(4096);
    expect(old.terminalCount).toBe(4096);
    expect((await send(fixture.endpoint, list())).failure).toBe('invalid');
    await writeFile(join(fixture.control, 'may-commit-upload'), '');
    await fixture.lifetime.nextHost('next-host');
    expect(fixture.servers[0].listening).toBe(false);
    expect(fixture.servers[1].address()).toBe(fixture.endpoint);
    expect(old.terminalCount).toBe(4096);
    expect(old.admit('first-upload')).toEqual({ accepted: false, reason: 'duplicate' });
    expect(old.admit('never-evicted')).toEqual({ accepted: false, reason: 'saturated' });
    expect(fixture.registries[1].terminalCount).toBe(0);
    const observed = await send(fixture.endpoint, {
      operation: 'metadata',
      correlation_id: randomUUID(),
      name: metadata.name,
      object_id: metadata.object_id,
      maximum_bytes: '4096',
    });
    expect(observed.metadata).toEqual(metadata);
    const downloaded = await send(fixture.endpoint, {
      operation: 'download',
      correlation_id: randomUUID(),
      expected: metadata,
      destination_relative_path: 'csharp/op/destination.bin',
      maximum_bytes: '4096',
    });
    expect(downloaded.failure).toBe('none');
    expect(downloaded.metadata).toEqual(metadata);
    expect(await readFile(join(fixture.stagingRoot, 'csharp/op/destination.bin'))).toEqual(
      fixture.encrypted,
    );
    const fault = await send(fixture.endpoint, {
      ...upload,
      correlation_id: 'second-upload',
      name: 'another-state',
    });
    expect(fault.failure).toBe('io');
    expect(fault.mutation_state).toBe('committed');
    expect(fault.metadata!.object_id).toBe('1001');
    expect(fault.metadata!.encrypted_object_digest).toBe(metadata.encrypted_object_digest);
    expect(old.terminalCount).toBe(4096);
    const duplicated = await send(fixture.endpoint, {
      ...upload,
      correlation_id: 'second-upload',
      name: 'another-state',
    });
    expect(duplicated.failure).toBe('outcome_unknown');
    expect(fixture.registries[1].terminalCount).toBe(3);
    await fixture.lifetime.close();
    expect(fixture.servers.every((server) => !server.listening)).toBe(true);
    expect(old.terminalCount).toBe(4096);
  });

  it.each(['unknown', { command: 'next-host', secret: 'private-canary' }, null])(
    'rejects unknown control %j without replacement',
    async (message) => {
      const fixture = await setup();
      await fixture.lifetime.start();
      await send(fixture.endpoint, list());
      await expect(fixture.lifetime.nextHost(message)).rejects.toThrow(
        'synthetic_bridge_lifetime_failed',
      );
      await expect(fixture.lifetime.nextHost('next-host')).rejects.toThrow(
        'synthetic_bridge_lifetime_failed',
      );
      expect(fixture.registries).toHaveLength(1);
    },
  );

  it('rejects an unused registry and immediate repeated control after a successful replacement', async () => {
    const fixture = await setup();
    await fixture.lifetime.start();
    await send(fixture.endpoint, list());
    await fixture.lifetime.nextHost('next-host');
    await expect(fixture.lifetime.nextHost('next-host')).rejects.toThrow(
      'synthetic_bridge_lifetime_failed',
    );
    expect(fixture.registries).toHaveLength(2);
    expect(fixture.registries[0].terminalCount).toBe(1);
    expect(fixture.registries[1].terminalCount).toBe(0);
  });

  it('rejects a pending old connection and does not move it to a new server', async () => {
    const fixture = await setup();
    await fixture.lifetime.start();
    await send(fixture.endpoint, list());
    const socket = connect(fixture.endpoint);
    await new Promise<void>((resolve) => socket.once('connect', resolve));
    await new Promise((resolve) => setImmediate(resolve));
    try {
      await expect(fixture.lifetime.nextHost('next-host')).rejects.toThrow(
        'synthetic_bridge_lifetime_failed',
      );
      expect(fixture.registries).toHaveLength(1);
    } finally {
      socket.destroy();
    }
  });

  it('rejects an active operation, overlapping controls and externally failed teardown', async () => {
    const active = await setup();
    await active.lifetime.start();
    await send(active.endpoint, list());
    await writeFile(join(active.control, 'stall-list'), '');
    const blocked = connect(active.endpoint);
    blocked.write(encodeCommandMessage({ build_discriminator: 'lifetime-test', payload: list() }));
    for (let attempt = 0; attempt < 100 && active.registries[0].activeCount === 0; attempt++)
      await new Promise((resolve) => setTimeout(resolve, 1));
    expect(active.registries[0].activeCount).toBe(1);
    await expect(active.lifetime.nextHost('next-host')).rejects.toThrow(
      'synthetic_bridge_lifetime_failed',
    );
    expect(active.registries).toHaveLength(1);
    await expect(active.lifetime.close()).rejects.toThrow('synthetic_bridge_lifetime_failed');
    blocked.destroy();
    for (let attempt = 0; attempt < 100 && active.registries[0].activeCount !== 0; attempt++)
      await new Promise((resolve) => setTimeout(resolve, 1));
    expect(active.registries[0].activeCount).toBe(0);
    expect(active.registries[0].terminalCount).toBe(2);
    const overlap = await setup();
    await overlap.lifetime.start();
    await send(overlap.endpoint, list());
    const pending = overlap.lifetime.nextHost('next-host');
    const duplicate = overlap.lifetime.nextHost('next-host');
    const outcomes = await Promise.allSettled([pending, duplicate]);
    expect(outcomes.every((result) => result.status === 'rejected')).toBe(true);
    expect(overlap.registries).toHaveLength(1);
    const teardown = await setup();
    await teardown.lifetime.start();
    await new Promise<void>((resolve) => teardown.servers[0].close(() => resolve()));
    await expect(teardown.lifetime.close()).rejects.toThrow('synthetic_bridge_lifetime_failed');
  });

  it('does not report ready when the requested endpoint cannot be bound', async () => {
    const fixture = await setup();
    const blocker = createServer();
    blocker.listen(fixture.endpoint);
    await once(blocker, 'listening');
    try {
      await expect(fixture.lifetime.start()).rejects.toThrow('synthetic_bridge_lifetime_failed');
      await expect(fixture.lifetime.nextHost('next-host')).rejects.toThrow(
        'synthetic_bridge_lifetime_failed',
      );
      expect(fixture.registries).toHaveLength(1);
      expect(fixture.servers[0].listening).toBe(false);
    } finally {
      await new Promise<void>((resolve) => blocker.close(() => resolve()));
    }
  });

  it('cannot revive a closed old socket or use a mutated retired registry', async () => {
    const fixture = await setup();
    await fixture.lifetime.start();
    let oldClient: Socket | undefined;
    await send(fixture.endpoint, list('old-connection'), (socket) => {
      oldClient = socket;
    });
    await fixture.lifetime.nextHost('next-host');
    expect(fixture.servers[0].listening).toBe(false);
    expect([...fixture.sockets].every((socket) => socket.destroyed)).toBe(true);
    expect(oldClient!.destroyed).toBe(true);
    oldClient!.once('error', () => {});
    const refused = await new Promise<Error | null | undefined>((resolve) =>
      oldClient!.write(
        encodeCommandMessage({
          build_discriminator: 'lifetime-test',
          payload: list('old-late-request'),
        }),
        resolve,
      ),
    );
    expect(['EPIPE', 'ERR_STREAM_DESTROYED']).toContain((refused as NodeJS.ErrnoException).code);
    expect(fixture.registries[1].terminalCount).toBe(0);
    await send(fixture.endpoint, list());
    expect(fixture.registries[0].admit('mutated-retired').accepted).toBe(true);
    fixture.registries[0].complete('mutated-retired');
    await expect(fixture.lifetime.nextHost('next-host')).rejects.toThrow(
      'synthetic_bridge_lifetime_failed',
    );
    expect(fixture.registries).toHaveLength(2);
  });
});
