import { createServer } from 'node:net';
import { mkdtemp, mkdir, rm, readFile } from 'node:fs/promises';
import { join } from 'node:path';
import { createHash } from 'node:crypto';
import { afterEach, expect, test, vi } from 'vitest';
import { createUploadServer } from '../../src/action-wrapper/artifact-bridge/upload-server.js';
import { encodeUploadFrame } from '../../src/action-wrapper/artifact-bridge/upload-protocol.js';
import {
  probeGeneratedUpload,
  exchangeGeneratedUpload,
  generatedUploadFrame,
} from './generated-upload-probe.mjs';
const roots: string[] = [];
const servers: ReturnType<typeof createServer>[] = [];
afterEach(async () => {
  await Promise.all(
    servers.splice(0).map((s) => new Promise<void>((done) => s.close(() => done()))),
  );
  await Promise.all(roots.splice(0).map((r) => rm(r, { force: true, recursive: true })));
});
test('current generated oracle exercises real flat upload server EOF, closed refusals and replay', async () => {
  const root = await mkdtemp('/tmp/apr-generated-wire-');
  roots.push(root);
  const endpoint = join(root, 'bridge.sock');
  await mkdir(join(root, 'artifact-staging'));
  const bytes = Buffer.from('unit-only SDK archive stand-in; no actual SDK qualification');
  const hash = createHash('sha256').update(bytes).digest('hex');
  const calls: string[] = [];
  let name = '';
  const execute = vi.fn(async (request) => {
    name = request.physical_name;
    const source = await readFile(join(root, 'artifact-staging', request.source_relative_path));
    expect(createHash('sha256').update(source).digest('hex')).toBe(request.source_sha256);
    expect(String(source.length)).toBe(request.source_size);
    calls.push('/CreateArtifact', 'blob:block', '/FinalizeArtifact');
    return {
      protocol: 'apr.sdk.upload.cr2' as const,
      build_discriminator: 'r7-d0',
      correlation_id: request.correlation_id,
      dispatch: 'dispatched' as const,
      failure: 'none' as const,
      receipt: { artifact_id: '1001', archive_sha256: hash, archive_size: String(bytes.length) },
    };
  });
  const server = createUploadServer({
    endpoint,
    buildDiscriminator: 'r7-d0',
    executor: {
      execute,
      sealAdmission() {},
      awaitQuiescence: async () => true,
      dispose: async () => {},
    },
  });
  servers.push(server);
  await new Promise<void>((done) => server.listen(endpoint, done));
  await probeGeneratedUpload(
    { upload_endpoint: endpoint },
    { calls, uploaded: () => ({ bytes, metadata: { id: 1001, name, digest: 'sha256:' + hash } }) },
  );
  expect(execute).toHaveBeenCalledOnce();
  expect(calls).toEqual(['/CreateArtifact', 'blob:block', '/FinalizeArtifact']);
  const old = {
    build_discriminator: 'r7-d0',
    payload: { operation: 'list_exact', correlation_id: 'old' },
  };
  const oldFrame = Buffer.concat([generatedUploadFrame(old), Buffer.alloc(4)]);
  expect(await exchangeGeneratedUpload(endpoint, oldFrame)).toBeNull();
  expect(execute).toHaveBeenCalledOnce();
  expect(generatedUploadFrame(old)).toEqual(encodeUploadFrame(old));
});
test('oracle rejects an old nested receipt even if a peer returns a valid frame', async () => {
  const root = await mkdtemp('/tmp/apr-generated-peer-');
  roots.push(root);
  const endpoint = join(root, 'bridge.sock');
  const server = createServer({ allowHalfOpen: true }, (socket) => {
    socket.resume();
    socket.once('end', () =>
      socket.end(
        generatedUploadFrame({ build_discriminator: 'r7-d0', payload: { failure: 'invalid' } }),
      ),
    );
  });
  servers.push(server);
  await new Promise<void>((done) => server.listen(endpoint, done));
  await expect(
    probeGeneratedUpload({ upload_endpoint: endpoint }, { calls: [] }),
  ).rejects.toThrow();
});
