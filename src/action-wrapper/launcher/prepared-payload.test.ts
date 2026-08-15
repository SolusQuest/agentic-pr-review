import { createHash } from 'node:crypto';
import { chmod, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import { digestEventJson, verifyPreparedPayload } from './prepared-payload.js';

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(
    roots.splice(0).map(async (root) => await rm(root, { recursive: true, force: true })),
  );
});

describe('W1 prepared payload proof', () => {
  it('verifies one absolute regular executable by stable identity and digest', async () => {
    const fixture = await createFixture();
    const verified = await verifyPreparedPayload(fixture.proof);
    try {
      expect(verified).toMatchObject({
        actionSourceSha: 'a'.repeat(40),
        payloadSha256: fixture.digest,
        buildDiscriminator: 'r4-h1',
      });
      expect(verified.executableHandle.fd).toBeGreaterThanOrEqual(0);
    } finally {
      await verified.executableHandle.close();
    }
  });

  it('rejects digest, build, source, escape, and post-proof identity mismatches', async () => {
    const fixture = await createFixture();
    await expect(
      verifyPreparedPayload({ ...fixture.proof, payloadSha256: '0'.repeat(64) }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
    await expect(
      verifyPreparedPayload({ ...fixture.proof, buildDiscriminator: 'UPPER' }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
    await expect(
      verifyPreparedPayload({ ...fixture.proof, wrapperBuildDiscriminator: 'r4-h2' }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
    await expect(
      verifyPreparedPayload({ ...fixture.proof, actionSourceSha: 'g'.repeat(40) }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
    await expect(
      verifyPreparedPayload({ ...fixture.proof, executableRelativePath: '../host' }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
  });

  it.runIf(process.platform !== 'win32')('rejects a symlink payload', async () => {
    const fixture = await createFixture();
    const linked = path.join(fixture.root, 'linked-host');
    await symlink(fixture.executable, linked);
    await expect(
      verifyPreparedPayload({ ...fixture.proof, executableRelativePath: 'linked-host' }),
    ).rejects.toThrow('wrapper_prepared_payload_invalid');
  });

  it('hashes the exact stable event JSON file and rejects symlinks', async () => {
    const fixture = await createFixture();
    const event = path.join(fixture.root, 'event.json');
    await writeFile(event, '{"pull_request":{}}');
    await expect(digestEventJson(event)).resolves.toBe(
      createHash('sha256').update('{"pull_request":{}}').digest('hex'),
    );
    if (process.platform !== 'win32') {
      const link = path.join(fixture.root, 'event-link.json');
      await symlink(event, link);
      await expect(digestEventJson(link)).rejects.toThrow('wrapper_event_json_invalid');
    }
  });
});

async function createFixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-w1-proof-'));
  roots.push(root);
  const executable = path.join(root, 'host');
  const bytes = Buffer.from('prepared-host');
  await writeFile(executable, bytes);
  if (process.platform !== 'win32') await chmod(executable, 0o700);
  const digest = createHash('sha256').update(bytes).digest('hex');
  return {
    root,
    executable,
    digest,
    proof: {
      trustedRoot: root,
      executableRelativePath: 'host',
      payloadSha256: digest,
      actionSourceSha: 'a'.repeat(40),
      buildDiscriminator: 'r4-h1',
      wrapperBuildDiscriminator: 'r4-h1',
    },
  };
}
