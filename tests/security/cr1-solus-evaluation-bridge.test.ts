import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import { createHash } from 'node:crypto';
import { once } from 'node:events';
import {
  cp,
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  realpath,
  rm,
  symlink,
  writeFile,
} from 'node:fs/promises';
import { createConnection } from 'node:net';
import path from 'node:path';
import { createInterface, type Interface } from 'node:readline';
import { build } from 'esbuild';
import { afterEach, describe, expect, test } from 'vitest';
import { inspectActionDistribution } from '../../scripts/check-action-distribution.mjs';
import type {
  ArtifactBridgeCommand,
  ArtifactBridgeResult,
} from '../../src/action-wrapper/artifact-bridge/contracts.js';
import { encodeCommandMessage } from '../../src/action-wrapper/artifact-bridge/framing.js';
import { ARTIFACT_BRIDGE_LIMITS } from '../../src/action-wrapper/artifact-bridge/limits.js';

const temporaryRoots: string[] = [];
const resources: Array<{ root: string; child: ChildProcessWithoutNullStreams; lines: Interface }> =
  [];
afterEach(async () => {
  await Promise.all(
    temporaryRoots.splice(0).map((root) => rm(root, { recursive: true, force: true })),
  );
  for (const { root, child, lines } of resources.splice(0)) {
    lines.close();
    if (child.exitCode === null && child.signalCode === null) {
      const closed = once(child, 'close');
      child.kill('SIGTERM');
      await closed;
    }
    await rm(root, { recursive: true, force: true });
  }
});

async function fixture(controller: boolean) {
  const root = await realpath(await mkdtemp('/tmp/apr-v1-bridge-'));
  const staging = path.join(root, 'staging');
  const control = path.join(root, 'control');
  await mkdir(path.join(staging, 'csharp', 'op'), { recursive: true, mode: 0o700 });
  await mkdir(control, { mode: 0o700 });
  const bundle = path.join(root, 'bridge.mjs');
  await build({
    entryPoints: [
      controller
        ? 'runtime/tests/SolusEvaluationFixture/bridge.mjs'
        : 'src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts',
    ],
    outfile: bundle,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
  });
  const endpoint = path.join(root, 'bridge.sock');
  const child = spawn(process.execPath, [bundle, endpoint, 'v1-test', staging, control], {
    env: { PATH: process.env.PATH },
    stdio: ['pipe', 'pipe', 'pipe'],
  });
  child.stderr.resume();
  const lines = createInterface({ input: child.stdout });
  resources.push({ root, child, lines });
  const exited = once(child, 'close');
  const line = () =>
    Promise.race([
      once(lines, 'line'),
      exited.then(([code]) => {
        throw new Error('fixture_bridge_exit_' + code);
      }),
    ]);
  expect((await line())[0]).toBe('READY');
  const next = async (index: number) => {
    const response = line();
    child.stdin.write('NEXT ' + index + '\n');
    expect((await response)[0]).toBe('INVOCATION_READY ' + index);
  };
  const exchange = async (payload: ArtifactBridgeCommand): Promise<ArtifactBridgeResult> => {
    const connection = createConnection(endpoint);
    const chunks: Buffer[] = [];
    connection.on('data', (chunk: Buffer) => chunks.push(chunk));
    const closed = once(connection, 'close');
    connection.write(encodeCommandMessage({ build_discriminator: 'v1-test', payload }));
    await closed;
    const bytes = Buffer.concat(chunks);
    expect(bytes.length).toBeGreaterThan(4);
    const length = bytes.readUInt32BE(0);
    expect(bytes.length).toBe(length + 4);
    return (JSON.parse(bytes.subarray(4).toString('utf8')) as { payload: ArtifactBridgeResult })
      .payload;
  };
  const bytes = Buffer.from('synthetic encrypted carrier bytes; never review authority');
  await writeFile(path.join(staging, 'csharp', 'op', 'source.bin'), bytes, { mode: 0o600 });
  const upload = (correlation: string): ArtifactBridgeCommand => ({
    operation: 'upload_immutable',
    correlation_id: correlation,
    name: 'opaque-v1-state',
    source_relative_path: 'csharp/op/source.bin',
    encrypted_object_digest: createHash('sha256').update(bytes).digest('hex'),
    minimum_expires_at_unix_seconds: '2000',
  });
  return { root, endpoint, staging, control, child, next, exchange, upload, bytes };
}

describe('V1 independent Host bridge lifetimes', () => {
  test('keeps the existing S3/S4 CLI producer, metadata and exact-delete semantics', async () => {
    const f = await fixture(false);
    const first = await f.exchange(f.upload('first'));
    expect(first).toMatchObject({
      failure: 'none',
      mutation_state: 'committed',
      metadata: { producing_run_id: '7001', producing_run_attempt: '2' },
    });
    await writeFile(
      path.join(f.control, 'producing-run.json'),
      JSON.stringify({ id: '7002', attempt: '3' }),
    );
    const second = await f.exchange(f.upload('second'));
    expect(second.metadata).toMatchObject({ producing_run_id: '7002', producing_run_attempt: '3' });
    const wrong = await f.exchange({
      operation: 'delete_exact',
      correlation_id: 'wrong-delete',
      expected: { ...first.metadata!, producing_run_id: '7002' },
    });
    expect(wrong.failure).toBe('conflict');
    const deleted = await f.exchange({
      operation: 'delete_exact',
      correlation_id: 'delete',
      expected: first.metadata!,
    });
    expect(deleted).toMatchObject({ failure: 'none', mutation_state: 'committed' });
    const retained = await f.exchange({
      operation: 'metadata',
      correlation_id: 'retained',
      name: second.metadata!.name,
      object_id: second.metadata!.object_id,
    });
    expect(retained.metadata).toEqual(second.metadata);
  });

  test('preserves exact stored bytes across invocations while refusing same-invocation replay and saturation', async () => {
    const f = await fixture(true);
    await writeFile(
      path.join(f.control, 'producing-run.json'),
      JSON.stringify({ id: '10001', attempt: '2' }),
    );
    await f.next(1);
    const uploaded = await f.exchange(f.upload('same-correlation'));
    expect(uploaded.failure).toBe('none');
    const duplicate = await f.exchange(f.upload('same-correlation'));
    expect(duplicate).toMatchObject({
      failure: 'outcome_unknown',
      mutation_state: 'outcome_unknown',
    });
    await f.next(2);
    await writeFile(
      path.join(f.control, 'producing-run.json'),
      JSON.stringify({ id: '10002', attempt: '2' }),
    );
    const metadata = await f.exchange({
      operation: 'metadata',
      correlation_id: 'same-correlation',
      name: uploaded.metadata!.name,
      object_id: uploaded.metadata!.object_id,
    });
    expect(metadata.metadata).toEqual(uploaded.metadata);
    await writeFile(path.join(f.staging, 'csharp', 'op', 'destination.bin'), Buffer.alloc(0), {
      mode: 0o600,
    });
    const downloaded = await f.exchange({
      operation: 'download',
      correlation_id: 'download',
      expected: uploaded.metadata!,
      destination_relative_path: 'csharp/op/destination.bin',
      maximum_bytes: '1024',
    });
    expect(downloaded.failure).toBe('none');
    expect(await readFile(path.join(f.staging, 'csharp', 'op', 'destination.bin'))).toEqual(
      f.bytes,
    );
    expect(ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations).toBe(4096);
    for (let index = 2; index < ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations; index++) {
      const listed = await f.exchange({
        operation: 'list_exact',
        correlation_id: 'fill-' + index,
        name: uploaded.metadata!.name,
        maximum_objects: '8',
      });
      expect(listed.failure).toBe('none');
    }
    const saturated = await f.exchange({
      operation: 'list_exact',
      correlation_id: 'over',
      name: uploaded.metadata!.name,
      maximum_objects: '8',
    });
    expect(saturated).toMatchObject({ failure: 'invalid', complete: false });
    await f.next(3);
    const preserved = await f.exchange({
      operation: 'metadata',
      correlation_id: 'over',
      name: uploaded.metadata!.name,
      object_id: uploaded.metadata!.object_id,
    });
    expect(preserved.metadata).toEqual(uploaded.metadata);
  }, 60000);

  test('refuses a new invocation while the official registry has active work', async () => {
    const f = await fixture(true);
    await f.next(1);
    const marker = path.join(f.control, 'stall-list');
    await writeFile(marker, '');
    const connection = createConnection(f.endpoint);
    connection.write(
      encodeCommandMessage({
        build_discriminator: 'v1-test',
        payload: {
          operation: 'list_exact',
          correlation_id: 'active',
          name: 'opaque-v1-state',
          maximum_objects: '8',
        },
      }),
    );
    try {
      let consumed = false;
      for (let count = 0; count < 200; count++) {
        consumed = await lstat(marker).then(
          () => false,
          (error) => {
            if (error.code !== 'ENOENT') throw error;
            return true;
          },
        );
        if (consumed) break;
        await new Promise((resolve) => setTimeout(resolve, 5));
      }
      expect(consumed).toBe(true);
      const failure = once(f.child.stderr, 'data');
      const closed = once(f.child, 'close');
      f.child.stdin.write('NEXT 2\n');
      expect(String((await failure)[0])).toContain('APR_SOLUS_EVALUATION_BRIDGE_FAILED');
      connection.destroy();
      expect((await closed)[0]).toBe(1);
    } finally {
      connection.destroy();
    }
  });

  test('rejects the extracted synthetic executor if it enters the production Action bundle', async () => {
    const root = await realpath(await mkdtemp('/tmp/apr-v1-bundle-'));
    temporaryRoots.push(root);
    for (const name of ['src', 'scripts', '.github'])
      await cp(name, path.join(root, name), { recursive: true });
    for (const name of ['package.json', 'package-lock.json']) await cp(name, path.join(root, name));
    await symlink(path.resolve('node_modules'), path.join(root, 'node_modules'), 'dir');
    const entry = path.join(root, 'src/action-wrapper/index.ts');
    await writeFile(
      entry,
      "import { SyntheticArtifactExecutor } from './artifact-bridge/synthetic-artifact-executor.fixture.js';\nconsole.log(SyntheticArtifactExecutor);\n" +
        (await readFile(entry, 'utf8')),
    );
    expect(await inspectActionDistribution(root)).toContainEqual({
      relativePath: 'src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.ts',
      kind: 'test-source-in-action-bundle',
    });
  }, 15000);

  test.each(['NEXT 0', 'NEXT 2', 'NEXT 1\nNEXT 1', 'RESET 1'])(
    'rejects an invalid invocation boundary %s',
    async (command) => {
      const f = await fixture(true);
      const closed = once(f.child, 'close');
      if (command.includes('\n')) {
        await f.next(1);
        command = 'NEXT 1';
      }
      f.child.stdin.write(command + '\n');
      expect((await closed)[0]).toBe(1);
    },
  );
});
