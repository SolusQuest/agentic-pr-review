import { readFileSync, rmSync, writeFileSync, mkdirSync, symlinkSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, test, vi } from 'vitest';
import {
  stageCandidate,
  singleRawFile,
  verifyStagedCandidate,
} from '../../scripts/release/stage-candidate.mjs';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { intentName, parseStageRequest } from '../../scripts/release/stage-candidate.format.mjs';
import { stagingFixture } from './staging.fixtures.mjs';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const r of roots.splice(0)) rmSync(r, { recursive: true, force: true });
});
async function fixture() {
  const x = await stagingFixture(vi);
  roots.push(x.f.root);
  return x;
}
const run = (x: any, input = x.input, extra = {}) =>
  stageCandidate(input, { ...x.env, ...extra }, { transport: x.transport });

describe('P4 synthetic qualified original staging and durable at-most-once recovery', () => {
  test('composes full P3 gate then uploads exactly fourteen original assets and adopts exact IDs read-only', async () => {
    const x = await fixture();
    const original = [
      x.input.archivePath,
      x.input.candidatePath,
      x.input.mapPath,
      x.input.bundlePath,
      x.input.qualificationArchivePath,
    ].map((p) => sha256(readFileSync(p)));
    const result = await run(x);
    expect(x.state.writes.map((w) => w.method)).toEqual(['INTENT', ...Array(15).fill('POST')]);
    expect(x.state.assets.size).toBe(14);
    expect(x.state.releases[0]).toMatchObject({
      draft: true,
      prerelease: true,
      target_commitish: x.f.source,
      make_latest: 'false',
      generate_release_notes: false,
    });
    const record = JSON.parse(readFileSync(join(x.input.output, 'staged-pair.json'), 'utf8'));
    expect(record.descriptor.pairSha256).toBe(x.request.pairSha256);
    expect(record.assets.map((a: any) => a.id)).toHaveLength(14);
    expect(x.state.intentBytes.get('601').toString()).not.toContain('SECRET_CANARY');
    expect(readFileSync(join(x.input.output, 'staged-pair.json'), 'utf8')).not.toContain(
      'SECRET_CANARY',
    );
    const uploaded = new Map([...x.state.assets.values()].map((a: any) => [a.name, a.bytes]));
    expect(uploaded.get(x.f.p.receipt.archiveName)).toEqual(readFileSync(x.input.archivePath));
    expect(uploaded.get('qualification.zip')).toEqual(
      readFileSync(x.input.qualificationArchivePath),
    );
    expect(uploaded.get('action-payload-map.json')).toEqual(readFileSync(x.input.mapPath));
    expect(original).toEqual(
      [
        x.input.archivePath,
        x.input.candidatePath,
        x.input.mapPath,
        x.input.bundlePath,
        x.input.qualificationArchivePath,
      ].map((p) => sha256(readFileSync(p))),
    );
    x.state.stageRun.status = 'completed';
    x.state.stageRun.conclusion = 'failure';
    const writes = x.state.writes.length;
    await expect(run(x, x.again(), { RELEASE_OPERATION: 'recover-stage' })).resolves.toEqual(
      result,
    );
    expect(x.state.writes).toHaveLength(writes);
    expect(
      x.state.reads
        .filter((r) => r.path.startsWith('/releases'))
        .every((r) => r.auth === 'Bearer WRITE_SECRET_CANARY'),
    ).toBe(true);
  });
  test.each(Array.from({ length: 15 }, (_, n) => n + 1))(
    'lost committed reply at mutation %s reconciles once without POST replay',
    async (n) => {
      const x = await fixture();
      x.state.faultAt = n;
      await expect(run(x)).resolves.toHaveProperty('releaseId', '801');
      expect(x.state.writes.filter((w) => w.method === 'POST')).toHaveLength(15);
    },
  );
  test.each(Array.from({ length: 15 }, (_, n) => n + 1))(
    'unknown uncommitted reply at mutation %s remains fenced after restart',
    async (n) => {
      const x = await fixture();
      x.state.faultAt = n;
      x.state.fault = 'absent';
      await expect(run(x)).rejects.toThrow();
      const writes = x.state.writes.length;
      expect(writes).toBe(n + 1);
      expect(x.state.intents).toHaveLength(1);
      await expect(run(x, x.again())).rejects.toThrow();
      expect(x.state.writes).toHaveLength(writes);
    },
  );
  test.each([
    'missing-token',
    'approval',
    'activation',
    'environment',
    'wrong-env-id',
    'expired',
    'wrong-control',
    'wrong-source',
    'readonly-visibility',
    'wrong-operation',
    'aliased-read-token',
  ])('denies authority before any persistent write: %s', async (kind) => {
    const x = await fixture(),
      e: any = {};
    if (kind === 'missing-token') e.R7_STAGING_TOKEN = '';
    if (kind === 'approval') e.R7_STAGE_REQUEST_SHA256 = 'f'.repeat(64);
    if (kind === 'activation') e.R7_STAGING_ENABLED = 'false';
    if (kind === 'environment') x.state.environment = null;
    if (kind === 'wrong-env-id') x.state.environment.id = '799';
    if (kind === 'expired') {
      x.request.validUntil = new Date(x.f.now - 1000).toISOString();
      x.approved();
    }
    if (kind === 'wrong-control') {
      x.request.controlCommit = x.f.action;
      x.approved();
    }
    if (kind === 'wrong-source') x.input.sourceCommit = x.f.workflow;
    if (kind === 'readonly-visibility') x.state.repository.permissions.push = false;
    if (kind === 'wrong-operation') e.RELEASE_OPERATION = 'prepare';
    if (kind === 'aliased-read-token') e.GITHUB_TOKEN = x.env.R7_STAGING_TOKEN;
    await expect(run(x, x.input, e)).rejects.toThrow();
    expect(x.state.writes).toHaveLength(0);
  });
  test.each([
    'B',
    'candidate',
    'map',
    'bundle',
    'pair',
    'zip',
    'receipt',
    'Q',
    'expired-storage',
    'qualifier-failed',
    'wrong-cert',
    'saved-pass',
  ])('full P3 gate refuses original qualification drift before intent upload: %s', async (kind) => {
    const x = await fixture();
    const paths: any = {
      B: 'archivePath',
      candidate: 'candidatePath',
      map: 'mapPath',
      bundle: 'bundlePath',
      pair: 'pairPath',
      zip: 'qualificationArchivePath',
    };
    if (paths[kind]) writeFileSync(x.input[paths[kind]], '{}');
    if (kind === 'receipt') {
      x.request.receiptSha256 = 'f'.repeat(64);
      x.approved();
    }
    if (kind === 'Q') {
      x.request.qualifierCommit = x.f.action;
      x.approved();
    }
    if (kind === 'expired-storage') x.f.artifacts[0].expired = true;
    if (kind === 'qualifier-failed') x.f.qRun.conclusion = 'failure';
    if (kind === 'wrong-cert') {
      x.f.cert.runnerEnvironment = 'self-hosted';
      x.f.saveGh();
    }
    if (kind === 'saved-pass') {
      rmSync(x.input.qualificationArchivePath);
      writeFileSync(join(x.f.root, 'PASS.json'), '{"qualified":true}');
    }
    await expect(run(x)).rejects.toThrow();
    expect(x.state.writes).toHaveLength(0);
  });
  test('write token is erased before the actual gh child inherits ambient variables', async () => {
    const x = await fixture();
    writeFileSync(
      join(x.f.bin, 'gh'),
      `#!${process.execPath}\nimport fs from 'node:fs';\nif(process.env.R7_STAGING_TOKEN){process.exit(99);}if(process.argv[2]==='--version')console.log('gh version 2.103.3 (synthetic)');else process.stdout.write(fs.readFileSync(${JSON.stringify(join(x.f.root, 'gh-result.json'))}));\n`,
    );
    vi.stubEnv('R7_STAGING_TOKEN', 'WRITE_SECRET_CANARY');
    await expect(run(x)).resolves.toHaveProperty('releaseId');
    expect(process.env.R7_STAGING_TOKEN).toBeUndefined();
  });
  test.each(['starter', 'wrong-response-id', 'malformed'])(
    'write reply %s never causes replay/delete/replacement',
    async (fault) => {
      const x = await fixture();
      x.state.faultAt = 2;
      x.state.fault = fault;
      if (fault === 'malformed') await expect(run(x)).resolves.toHaveProperty('releaseId');
      else await expect(run(x)).rejects.toThrow();
      expect(x.state.writes.filter((w) => w.method === 'POST').length).toBeLessThanOrEqual(15);
      expect(x.state.writes.every((w) => ['POST', 'INTENT'].includes(w.method))).toBe(true);
    },
  );
  test('lost durable-intent reply never authorizes initial release write, subsequent invocation stays read-only', async () => {
    const x = await fixture();
    x.state.fenceUploadFail = true;
    await expect(run(x)).rejects.toThrow('stage_intent_write_unknown');
    await expect(run(x, x.again())).rejects.toThrow('stage_recovery_incomplete');
    expect(x.state.writes).toHaveLength(1);
  });
  test('a durable local execution claim prevents a second uploader invocation after uncertain intent failure', async () => {
    const x = await fixture(),
      upload = vi.fn(async () => {
        throw new Error('unknown');
      });
    const adapters = { transport: { ...x.transport, upload } };
    await expect(stageCandidate(x.input, { ...x.env }, adapters)).rejects.toThrow(
      'stage_intent_write_unknown',
    );
    await expect(stageCandidate(x.again(), { ...x.env }, adapters)).rejects.toThrow();
    expect(upload).toHaveBeenCalledTimes(1);
    expect(x.state.writes).toHaveLength(0);
  });
  test('expiry observed during pre-read stops before the next reserved POST', async () => {
    const x = await fixture();
    x.request.validUntil = new Date(x.f.now + 600000).toISOString();
    x.approved();
    x.state.postReadHook = () => {
      if (x.state.writes.length === 2) vi.spyOn(Date, 'now').mockReturnValue(x.f.now + 600001);
    };
    await expect(run(x)).rejects.toThrow('stage_authority_expired');
    expect(x.state.writes.map((w) => w.method)).toEqual(['INTENT', 'POST']);
  });
  test('P5 read-only verifier binds independently selected staged-record bytes/hash and full original evidence', async () => {
    const x = await fixture();
    await run(x);
    const bytes = readFileSync(join(x.input.output, 'staged-pair.json'));
    const input = { ...x.again(), expectedRecordBytes: bytes, expectedRecordSha256: sha256(bytes) };
    const before = x.state.writes.length;
    await expect(
      verifyStagedCandidate(input, { ...x.env }, { transport: x.transport }),
    ).resolves.toHaveProperty('releaseId', '801');
    const record = JSON.parse(bytes.toString());
    record.releaseId = '1999';
    const changed = Buffer.from(canonicalJson(record));
    await expect(
      verifyStagedCandidate(
        { ...x.again(), expectedRecordBytes: changed, expectedRecordSha256: sha256(changed) },
        { ...x.env },
        { transport: x.transport },
      ),
    ).rejects.toThrow('stage_record_drift');
    expect(x.state.writes).toHaveLength(before);
  });
  test.each([
    'extra',
    'duplicate-name',
    'zero',
    'digest',
    'bytes',
    'id',
    'foreign-url',
    'published',
    'foreign-body',
    'duplicate-release',
    'missing-fence',
    'duplicate-fence',
    'expired-fence',
    'foreign-run',
    'payload-tag',
    'consumer-tag',
  ])('exact remote adoption refuses conflicting state without mutations: %s', async (kind) => {
    const x = await fixture();
    await run(x);
    const a: any = [...x.state.assets.values()][0];
    if (kind === 'extra') x.state.assets.set('1999', { ...a, id: '1999', name: 'unexpected' });
    if (kind === 'duplicate-name') {
      const other: any = [...x.state.assets.values()][1];
      other.name = a.name;
    }
    if (kind === 'zero') a.size = 0;
    if (kind === 'digest') a.digest = 'sha256:' + 'f'.repeat(64);
    if (kind === 'bytes') a.bytes = Buffer.alloc(a.size, 1);
    if (kind === 'id') a.id = [...x.state.assets.values()][1].id;
    if (kind === 'foreign-url') a.url = 'https://evil.invalid/asset';
    if (kind === 'published') x.state.releases[0].draft = false;
    if (kind === 'foreign-body') x.state.releases[0].body = '{}';
    if (kind === 'duplicate-release') x.state.releases.push({ ...x.state.releases[0], id: '802' });
    if (kind === 'missing-fence') x.state.intents.length = 0;
    if (kind === 'duplicate-fence') x.state.intents.push({ ...x.state.intents[0], id: '602' });
    if (kind === 'expired-fence') x.state.intents[0].expired = true;
    if (kind === 'foreign-run') x.state.stageRun.event = 'pull_request';
    if (kind === 'payload-tag')
      x.state.payloadTag = {
        ref: 'refs/tags/payload-' + x.input.releaseVersion,
        object: { type: 'commit', sha: x.f.action },
      };
    if (kind === 'consumer-tag') x.state.consumer = { ref: 'refs/tags/' + x.input.releaseVersion };
    const writes = x.state.writes.length;
    await expect(run(x, x.again())).rejects.toThrow();
    expect(x.state.writes).toHaveLength(writes);
  });
  test('rejects canonical request extension/duplicate/oversize and raw directory symlink or multiple files', async () => {
    const x = await fixture();
    for (const bytes of [
      Buffer.from('{}'),
      Buffer.from('{"x":1,"x":2}'),
      Buffer.alloc(32769),
      Buffer.from(canonicalJson({ ...x.request, endpoint: 'https://evil.invalid' })),
    ])
      expect(() => parseStageRequest(bytes, sha256(bytes))).toThrow();
    const d = join(x.f.root, 'raw');
    mkdirSync(d);
    writeFileSync(join(d, 'arbitrary-name'), 'original');
    await expect(singleRawFile(d, sha256('original'), 100)).resolves.toMatchObject({
      bytes: Buffer.from('original'),
    });
    writeFileSync(join(d, 'extra'), 'extra');
    await expect(singleRawFile(d, sha256('original'), 100)).rejects.toThrow();
    rmSync(join(d, 'extra'));
    rmSync(join(d, 'arbitrary-name'));
    symlinkSync(x.input.archivePath, join(d, 'link'));
    await expect(singleRawFile(d, sha256('original'), 100)).rejects.toThrow();
    expect(intentName(x.request)).toMatch(/^r7-p4-intent-v.*\.json$/);
  });
});
