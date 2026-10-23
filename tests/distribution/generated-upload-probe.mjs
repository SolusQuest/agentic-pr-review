// Independent current generated-Action upload oracle. Never bundled into the Action.
import assert from 'node:assert/strict';
import { createConnection } from 'node:net';
import { createHash } from 'node:crypto';
import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';

const protocol = 'apr.sdk.upload.cr2';
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
export function generatedUploadFrame(value) {
  const bytes = Buffer.from(JSON.stringify(value));
  const frame = Buffer.alloc(bytes.length + 4);
  frame.writeUInt32BE(bytes.length);
  bytes.copy(frame, 4);
  return frame;
}
export function exchangeGeneratedUpload(endpoint, bytes) {
  return new Promise((resolve, reject) => {
    const socket = createConnection({ path: endpoint, allowHalfOpen: true });
    const chunks = [];
    let size = 0;
    socket.setTimeout(15_000, () => socket.destroy(new Error('generated_upload_probe_timeout')));
    socket.once('error', reject);
    socket.once('connect', () => socket.end(bytes));
    socket.on('data', (chunk) => {
      size += chunk.length;
      if (size > 256 * 1024 + 4) socket.destroy(new Error('generated_upload_probe_overflow'));
      else chunks.push(chunk);
    });
    socket.once('end', () => {
      try {
        const reply = Buffer.concat(chunks);
        if (reply.length === 0) resolve(null);
        else {
          assert.ok(reply.length >= 4);
          assert.equal(reply.readUInt32BE(0), reply.length - 4);
          resolve(JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(reply.subarray(4))));
        }
      } catch (error) {
        reject(error);
      } finally {
        socket.destroy();
      }
    });
  });
}
export async function probeGeneratedUpload(launch, platform) {
  let correlation = 0;
  const encryptedBytes = Buffer.from('opaque synthetic encrypted bytes for transport proof');
  const request = {
    protocol,
    build_discriminator: 'r7-d0',
    correlation_id: '',
    run_id: '900',
    run_attempt: '1',
    physical_name: 'apr-object-' + 'a'.repeat(64) + '-' + 'b'.repeat(64),
    source_relative_path: 'proof/source',
    source_size: String(encryptedBytes.length),
    source_sha256: digest(encryptedBytes),
    retention_days: '1',
    timeout_ms: '15000',
  };
  async function command(changes, dispatch = 'not_dispatched', failure = 'invalid') {
    const value = { ...request, correlation_id: 'd4-proof-' + ++correlation, ...changes };
    const reply = await exchangeGeneratedUpload(
      launch.upload_endpoint,
      generatedUploadFrame(value),
    );
    assert.deepEqual(
      Object.keys(reply).sort(),
      [
        'protocol',
        'build_discriminator',
        'correlation_id',
        'dispatch',
        'failure',
        'receipt',
      ].sort(),
    );
    assert.equal(reply.protocol, protocol);
    assert.equal(reply.build_discriminator, 'r7-d0');
    assert.equal(reply.correlation_id, value.correlation_id);
    assert.equal(reply.dispatch, dispatch);
    assert.equal(reply.failure, failure);
    if (dispatch === 'not_dispatched') assert.equal(reply.receipt, null);
    return { value, reply };
  }
  const before = platform.calls.length;
  await command({ build_discriminator: 'r4-w2' });
  await command({ protocol: 'retired' });
  // Former Node read/delete capabilities must be refused, without SDK/network dispatch.
  for (const operation of ['list_exact', 'metadata', 'readback_exact', 'download', 'delete_exact'])
    await command({ operation });
  const valid = generatedUploadFrame(request);
  for (const bytes of [
    Buffer.concat([valid, Buffer.alloc(4)]),
    Buffer.concat([valid, valid]),
    Buffer.from([0, 0, 0, 2, 0x7b, 0x7d]),
  ])
    assert.equal(await exchangeGeneratedUpload(launch.upload_endpoint, bytes), null);
  assert.equal(platform.calls.length, before);
  const operation = join(dirname(launch.upload_endpoint), 'artifact-staging', 'proof');
  await mkdir(operation, { mode: 0o700 });
  await writeFile(join(operation, 'source'), encryptedBytes, { mode: 0o600 });
  const { value, reply } = await command({}, 'dispatched', 'none');
  assert.deepEqual(
    Object.keys(reply.receipt).sort(),
    ['artifact_id', 'archive_sha256', 'archive_size'].sort(),
  );
  assert.match(reply.receipt.artifact_id, /^[1-9][0-9]*$/u);
  assert.match(reply.receipt.archive_sha256, /^[a-f0-9]{64}$/u);
  assert.match(reply.receipt.archive_size, /^[1-9][0-9]*$/u);
  const stored = platform.uploaded();
  assert.equal(String(stored.metadata.id), reply.receipt.artifact_id);
  assert.equal(stored.metadata.name, value.physical_name);
  assert.equal(digest(stored.bytes), reply.receipt.archive_sha256);
  assert.equal(String(stored.bytes.length), reply.receipt.archive_size);
  assert.equal(stored.metadata.digest, 'sha256:' + reply.receipt.archive_sha256);
  const after = platform.calls.length;
  await command({ correlation_id: value.correlation_id });
  assert.equal(platform.calls.length, after);
  assert.equal(platform.calls.filter((route) => route.endsWith('/CreateArtifact')).length, 1);
  assert.equal(platform.calls.filter((route) => route.endsWith('/FinalizeArtifact')).length, 1);
  assert.ok(platform.calls.some((route) => route.startsWith('blob:')));
  assert.ok(
    !platform.calls.some((route) =>
      /List|Download|Delete|signed-download|^DELETE|^GET/u.test(route),
    ),
  );
}
