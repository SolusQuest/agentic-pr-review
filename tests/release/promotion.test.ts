import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { promoteCandidate } from '../../scripts/release/promote-candidate.mjs';
import {
  parsePromotionRequest,
  promotionIntentName,
  PROMOTION_LIMITS,
} from '../../scripts/release/promote-candidate.format.mjs';
import { promotionFixture } from './promotion.fixtures.mjs';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const p of roots.splice(0)) rmSync(p, { recursive: true, force: true });
});
async function fixture() {
  const x = await promotionFixture(vi);
  roots.push(x.f.root);
  return x;
}
const run = (x: any, input = x.input, extra = {}) =>
  promoteCandidate(input, { ...x.env, ...extra }, { transport: x.transport });

describe('P5 synthetic immutable promotion, original bytes and tag-last recovery', () => {
  test('same-run concurrent invocations have one durable local owner and at most one mutation per operation', async () => {
    const x = await fixture();
    const outcomes = await Promise.allSettled([run(x), run(x, x.again())]);
    expect(outcomes.filter((r) => r.status === 'fulfilled')).toHaveLength(1);
    expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT', 'PATCH', 'POST']);
  });
  test('full P3/P4 composition, separate P/R, public reacquisition and runtime proof precede exact T tag', async () => {
    const x = await fixture();
    vi.stubEnv('R7_PUBLISH_TOKEN', x.env.R7_PUBLISH_TOKEN);
    vi.stubEnv('R7_PROTECTION_TOKEN', x.env.R7_PROTECTION_TOKEN);
    const before = new Map([...x.state.assets.values()].map((a: any) => [a.id, sha256(a.bytes)]));
    const result = await run(x);
    expect(x.control).not.toBe(x.request.stageRequest.controlCommit);
    expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT', 'PATCH', 'POST']);
    expect(x.state.writes[1].body).toEqual({
      draft: false,
      prerelease: true,
      make_latest: 'false',
    });
    expect(x.state.writes[2].body).toEqual({
      ref: 'refs/tags/' + x.input.releaseVersion,
      sha: x.f.action,
    });
    expect(x.state.publicReads.sort()).toEqual(x.staged.assets.map((a: any) => a.name).sort());
    expect(new Map([...x.state.assets.values()].map((a: any) => [a.id, sha256(a.bytes)]))).toEqual(
      before,
    );
    const record = JSON.parse(readFileSync(join(x.input.output, 'published-pair.json'), 'utf8'));
    expect(record.assets).toEqual(x.staged.assets);
    expect(record.actionCommit).toBe(x.f.action);
    expect(record.sourceCommit).toBe(x.f.source);
    expect(record.publicProof).toMatchObject({
      bytesVerified: true,
      qualificationVerified: true,
      releaseIntegrityVerified: true,
    });
    const log = readFileSync(x.ghLog, 'utf8')
      .trim()
      .split('\n')
      .map((s) => JSON.parse(s));
    expect(log.filter((l: any) => l.args[0] === 'attestation')).toHaveLength(2);
    expect(
      log.filter((l: any) => l.args[0] === 'release').map((l: any) => l.args.slice(0, 3)),
    ).toEqual([
      ['release', 'verify', 'payload-' + x.input.releaseVersion],
      ['release', 'verify-asset', 'payload-' + x.input.releaseVersion],
    ]);
    expect(log.every((l: any) => !l.publisher && !l.protection)).toBe(true);
    const report = readFileSync(join(x.input.output, 'published-pair.json'), 'utf8');
    expect(report).not.toMatch(/SECRET_CANARY|PRIVATE-CANARY/);
    expect(sha256(report)).toBe(result.publishedSha256);
    const writes = x.state.writes.length;
    x.state.promotionRun.status = 'completed';
    x.state.promotionRun.conclusion = 'failure';
    await expect(
      run(x, x.again(), {
        RELEASE_OPERATION: 'recover-promote',
        GITHUB_RUN_ID: '702',
        GITHUB_RUN_ATTEMPT: '2',
      }),
    ).resolves.toEqual(result);
    expect(x.state.writes).toHaveLength(writes);
  });
  test.each(['publish', 'tag'])(
    'lost committed %s reply reconciles exactly once',
    async (operation) => {
      const x = await fixture();
      x.state.failOperation = operation;
      await expect(run(x)).resolves.toHaveProperty('releaseId', '801');
      expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT', 'PATCH', 'POST']);
    },
  );
  test.each(['publish', 'tag'])(
    'lost uncommitted %s reply consumes entire batch across runner loss',
    async (operation) => {
      const x = await fixture();
      x.state.failOperation = operation;
      x.state.failKind = 'absent';
      await expect(run(x)).rejects.toThrow();
      const count = x.state.writes.length;
      expect(
        x.state.intents.filter((a: any) => a.name === promotionIntentName(x.request)),
      ).toHaveLength(1);
      await expect(run(x, x.again(), { GITHUB_RUN_ID: '702' })).rejects.toThrow();
      expect(x.state.writes).toHaveLength(count);
      expect(x.state.consumer).toBeNull();
    },
  );
  test('unknown intent upload response never authorizes publication; retained fence prevents retry', async () => {
    const x = await fixture();
    x.state.intentUploadFail = true;
    await expect(run(x)).rejects.toThrow('promotion_intent_write_unknown');
    await expect(run(x, x.again())).rejects.toThrow('promotion_recovery_incomplete');
    expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT']);
  });
  test.each(['publish', 'tag'])(
    'contradictory %s response is refused even if committed',
    async (operation) => {
      const x = await fixture();
      x.state.failOperation = operation;
      x.state.failKind = 'wrong-response';
      await expect(run(x)).rejects.toThrow();
      expect(
        x.state.writes.filter(
          (w: any) => w.method === (operation === 'publish' ? 'PATCH' : 'POST'),
        ),
      ).toHaveLength(1);
    },
  );
  test.each([
    'token',
    'observer-token',
    'alias',
    'activation',
    'approval',
    'environment',
    'expired',
    'stage-expired',
    'event',
    'operation',
    'source',
    'control',
    'channel',
    'visibility',
    'private',
  ])('authority refusal before any persistent write: %s', async (kind) => {
    const x = await fixture(),
      env: any = {};
    if (kind === 'token') env.R7_PUBLISH_TOKEN = '';
    if (kind === 'observer-token') env.R7_PROTECTION_TOKEN = '';
    if (kind === 'alias') env.R7_PUBLISH_TOKEN = x.env.GITHUB_TOKEN;
    if (kind === 'activation') env.R7_PUBLICATION_ENABLED = 'false';
    if (kind === 'approval') env.R7_PUBLISH_REQUEST_SHA256 = 'f'.repeat(64);
    if (kind === 'environment') x.state.publicationEnvironment.id = '799';
    if (kind === 'expired') x.request.validUntil = new Date(x.f.now - 1).toISOString();
    if (kind === 'stage-expired') {
      x.request.validUntil = new Date(x.f.now + 7200000).toISOString();
      vi.spyOn(Date, 'now').mockReturnValue(x.f.now + 3600001);
    }
    if (kind === 'event') env.GITHUB_EVENT_NAME = 'push';
    if (kind === 'operation') env.RELEASE_OPERATION = 'verify';
    if (kind === 'source') x.input.sourceCommit = x.f.workflow;
    if (kind === 'control') x.request.controlCommit = x.f.qualifier;
    if (kind === 'channel') x.request.channel = 'DRAFT';
    if (kind === 'visibility') x.state.repository.permissions.push = false;
    if (kind === 'private') x.state.repository.private = true;
    x.approved();
    await expect(run(x, x.input, env)).rejects.toThrow();
    expect(x.state.writes).toHaveLength(0);
  });
  test.each([
    'immutable-off',
    'immutable-unknown',
    'missing-ruleset',
    'disabled',
    'evaluate',
    'branch',
    'foreign',
    'nonmatching',
    'excluded',
    'missing-update',
    'missing-deletion',
    'bypass',
    'redacted',
    'update-parameter',
    'creation',
  ])('effective independent Action/payload protection refuses %s', async (kind) => {
    const x = await fixture(),
      s = x.state.ruleset;
    if (kind === 'immutable-off') x.state.immutable.enabled = false;
    if (kind === 'immutable-unknown') x.state.immutable = {};
    if (kind === 'missing-ruleset') x.state.ruleset = null;
    if (kind === 'disabled' || kind === 'evaluate') s.enforcement = kind;
    if (kind === 'branch') s.target = 'branch';
    if (kind === 'foreign') s.source = 'other/repo';
    if (kind === 'nonmatching') s.conditions.ref_name.include = ['refs/tags/v9-internal.*'];
    if (kind === 'excluded') s.conditions.ref_name.exclude = ['~ALL'];
    if (kind === 'missing-update') s.rules = [{ type: 'deletion' }];
    if (kind === 'missing-deletion') s.rules = [{ type: 'update' }];
    if (kind === 'bypass')
      s.bypass_actors = [{ actor_id: 1, actor_type: 'RepositoryRole', bypass_mode: 'always' }];
    if (kind === 'redacted') delete s.bypass_actors;
    if (kind === 'update-parameter')
      s.rules[0].parameters = { update_allows_fetch_and_merge: true };
    if (kind === 'creation') s.rules.push({ type: 'creation' });
    await expect(run(x)).rejects.toThrow();
    expect(x.state.writes).toHaveLength(0);
  });
  test.each([
    'archive',
    'raw-zip',
    'map',
    'provenance',
    'Q-failed',
    'expired-storage',
    'stage-record',
    'stage-fence',
    'stage-asset',
    'consumer-ref',
    'payload-ref',
  ])(
    'original authenticated evidence cannot be replaced by stored PASS or CI: %s',
    async (kind) => {
      const x = await fixture();
      if (kind === 'archive') writeFileSync(x.input.archivePath, 'wrong archive');
      if (kind === 'raw-zip') writeFileSync(x.input.qualificationArchivePath, 'wrong original ZIP');
      if (kind === 'map') writeFileSync(x.input.mapPath, '{}');
      if (kind === 'provenance') {
        x.f.cert.buildSignerDigest = 'f'.repeat(40);
        x.f.saveGh();
      }
      if (kind === 'Q-failed') x.f.qRun.conclusion = 'failure';
      if (kind === 'expired-storage') x.f.artifacts[0].expired = true;
      if (kind === 'stage-record') {
        x.request.stagedRecord.assets[0].id = '9999';
        x.request.stagedRecordSha256 = sha256(canonicalJson(x.request.stagedRecord));
        x.approved();
      }
      if (kind === 'stage-fence') x.state.intents = [];
      if (kind === 'stage-asset') [...x.state.assets.values()][0].bytes[0] ^= 1;
      if (kind === 'consumer-ref')
        x.state.consumer = {
          ref: 'refs/tags/' + x.input.releaseVersion,
          object: { type: 'commit', sha: x.f.action },
        };
      if (kind === 'payload-ref')
        x.state.payloadTag = {
          ref: 'refs/tags/payload-' + x.input.releaseVersion,
          object: { type: 'commit', sha: x.f.action },
        };
      await expect(run(x)).rejects.toThrow();
      expect(x.state.writes).toHaveLength(0);
    },
  );
  test.each([
    'unavailable',
    'release',
    'asset-id',
    'extra',
    'payload-archive',
    'action-asset',
    'finalized-pair.json',
    'qualification.zip',
    'action-payload-map.json',
    'candidate.json',
    'provenance-bundle.json',
    'runtime-bytes',
    'runtime-unavailable',
    'gh-release',
  ])('public usability failure blocks consumer tag: %s', async (kind) => {
    const x = await fixture();
    if (kind.startsWith('runtime-')) x.state.runtimeFault = kind.slice(8);
    else if (kind === 'gh-release') writeFileSync(x.ghFail, 'synthetic rejection');
    else if (kind === 'payload-archive')
      x.state.publicFault = x.request.stageRequest.pair.payload.archiveName;
    else if (kind === 'action-asset')
      x.state.publicFault = x.staged.assets.find(
        (a: any) => a.name.startsWith('action-') && a.name !== 'action-payload-map.json',
      ).name;
    else x.state.publicFault = kind;
    await expect(run(x)).rejects.toThrow();
    expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT', 'PATCH']);
    expect(x.state.consumer).toBeNull();
    const count = x.state.writes.length;
    await expect(run(x, x.again())).rejects.toThrow();
    expect(x.state.writes).toHaveLength(count);
  });
  test('publication protection drift after publish denies tag and cannot be silently accepted', async () => {
    const x = await fixture();
    x.state.hook = ({ path }: any) => {
      if (path === '/immutable-releases' && !x.state.releases[0].draft)
        x.state.ruleset.bypass_actors = [{ actor_id: 1 }];
    };
    await expect(run(x)).rejects.toThrow('promotion_action_protection_denied');
    expect(x.state.writes.map((w: any) => w.method)).toEqual(['INTENT', 'PATCH']);
  });
  test('complete recovery still rejects moved Action refs, mutable metadata and expired retained fence', async () => {
    const x = await fixture();
    await run(x);
    const count = x.state.writes.length;
    x.state.consumer.object.sha = x.f.source;
    await expect(run(x, x.again())).rejects.toThrow('promotion_action_ref_drift');
    x.state.consumer.object.sha = x.f.action;
    x.state.releases[0].prerelease = false;
    await expect(run(x, x.again())).rejects.toThrow('stage_foreign_release');
    x.state.releases[0].prerelease = true;
    x.state.intents.find((a: any) => a.id === '602').expired = true;
    await expect(run(x, x.again())).rejects.toThrow('stage_intent_identity_denied');
    expect(x.state.writes).toHaveLength(count);
  });
  test('closed canonical request covers nested serialization and rejects extensions, duplicate fields, oversize and fresh-approval reuse', async () => {
    const x = await fixture();
    expect(x.input.promotionRequestBytes.length).toBeLessThan(PROMOTION_LIMITS.request);
    const original = x.input.promotionRequestBytes;
    expect(parsePromotionRequest(original, sha256(original))).toEqual(x.request);
    for (const bytes of [
      Buffer.from(canonicalJson({ ...x.request, extra: true })),
      Buffer.from(
        original.toString().replace('"formatVersion":1', '"formatVersion":1,"formatVersion":1'),
      ),
      Buffer.alloc(PROMOTION_LIMITS.request + 1, 32),
    ])
      expect(() => parsePromotionRequest(bytes, sha256(bytes))).toThrow();
    x.request.approvalId = x.request.stageRequest.approvalId;
    x.approved();
    expect(() =>
      parsePromotionRequest(x.input.promotionRequestBytes, x.env.R7_PUBLISH_REQUEST_SHA256),
    ).toThrow('promotion_fresh_approval_required');
    expect(x.state.writes).toHaveLength(0);
  });
});
