import { createConnection, type Server } from 'node:net';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, expect, it, vi } from 'vitest';
import { createUploadServer } from './upload-server.js';
import {
  encodeUploadFrame,
  UPLOAD_PROTOCOL,
  type UploadReply,
  type UploadRequest,
} from './upload-protocol.js';
import { readUploadFrame } from './upload-framing.js';

const roots: string[] = [];
const servers: Server[] = [];
afterEach(async () => {
  await Promise.all(
    servers
      .splice(0)
      .map((server) => new Promise<void>((resolve) => server.close(() => resolve()))),
  );
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
const request: UploadRequest = {
  protocol: UPLOAD_PROTOCOL,
  build_discriminator: 'test',
  correlation_id: 'once',
  run_id: '1',
  run_attempt: '1',
  physical_name: `apr-object-${'a'.repeat(64)}-${'b'.repeat(64)}`,
  source_relative_path: 'source',
  source_size: '1',
  source_sha256: 'a'.repeat(64),
  retention_days: '1',
  timeout_ms: '1000',
};
async function fixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-upload-wire-'));
  roots.push(root);
  const endpoint = path.join(root, 'bridge.sock');
  const execute = vi.fn(
    async (input: UploadRequest): Promise<UploadReply> => ({
      protocol: UPLOAD_PROTOCOL,
      build_discriminator: 'test',
      correlation_id: input.correlation_id,
      dispatch: 'dispatched',
      failure: 'none',
      receipt: { artifact_id: '1', archive_sha256: 'a'.repeat(64), archive_size: '512' },
    }),
  );
  const server = createUploadServer({
    endpoint,
    buildDiscriminator: 'test',
    executor: {
      execute,
      sealAdmission() {},
      awaitQuiescence: async () => true,
      dispose: async () => {},
    },
  });
  servers.push(server);
  await new Promise<void>((resolve) => server.listen(endpoint, resolve));
  const exchange = async (bytes: Buffer): Promise<unknown> => {
    const socket = createConnection({ path: endpoint, allowHalfOpen: true });
    const reply = readUploadFrame(socket, AbortSignal.timeout(2000));
    socket.end(bytes);
    try {
      return await reply;
    } finally {
      socket.destroy();
    }
  };
  return { exchange, execute };
}
it('reads a reply after request write-half EOF and refuses the correlation replay', async () => {
  const f = await fixture();
  expect(await f.exchange(encodeUploadFrame(request))).toMatchObject({
    dispatch: 'dispatched',
    failure: 'none',
  });
  expect(await f.exchange(encodeUploadFrame(request))).toMatchObject({
    dispatch: 'not_dispatched',
    failure: 'invalid',
  });
  expect(f.execute).toHaveBeenCalledOnce();
});
it.each([Buffer.from([0]), encodeUploadFrame(request)])(
  'rejects trailing bytes or a second frame before dispatch',
  async (tail) => {
    const f = await fixture();
    await expect(f.exchange(Buffer.concat([encodeUploadFrame(request), tail]))).rejects.toThrow();
    expect(f.execute).not.toHaveBeenCalled();
  },
);
it('consumes an identifiable invalid correlation before a corrected request', async () => {
  const f = await fixture();
  expect(await f.exchange(encodeUploadFrame({ ...request, purpose: 'recovery' }))).toMatchObject({
    failure: 'invalid',
  });
  expect(await f.exchange(encodeUploadFrame(request))).toMatchObject({ failure: 'invalid' });
  expect(f.execute).not.toHaveBeenCalled();
});
it('retains every failed correlation and closes admission at the invocation cap', async () => {
  const f = await fixture();
  for (let index = 0; index < 4096; index++) {
    expect(
      await f.exchange(
        encodeUploadFrame({ ...request, correlation_id: String(index), purpose: 'invalid' }),
      ),
    ).toMatchObject({ failure: 'invalid' });
  }
  expect(
    await f.exchange(encodeUploadFrame({ ...request, correlation_id: 'new-after-cap' })),
  ).toMatchObject({ dispatch: 'not_dispatched', failure: 'invalid' });
  expect(await f.exchange(encodeUploadFrame({ ...request, correlation_id: '0' }))).toMatchObject({
    dispatch: 'not_dispatched',
    failure: 'invalid',
  });
  expect(f.execute).not.toHaveBeenCalled();
}, 30_000);
