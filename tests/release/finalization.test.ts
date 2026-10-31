import { readFileSync, rmSync, writeFileSync, mkdirSync, symlinkSync } from 'node:fs';
import { delimiter, join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, test, vi } from 'vitest';
import {
  ACTION_ASSETS,
  ACTION_ROOT,
  PAIR_LIMITS,
  parseFinalizedPair,
  parseFinalizationRequest,
} from '../../scripts/release/finalized-pair.format.mjs';
import {
  finalizeCandidate,
  main,
  verifyFinalizedPair,
} from '../../scripts/release/finalize-candidate.mjs';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { managedInstalledFiles } from '../../src/action-wrapper/launcher/action-source.js';
import { finalizationFixture } from './finalization.fixtures.mjs';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function fixture(overrides = {}) {
  const f = finalizationFixture(overrides);
  roots.push(f.root);
  vi.stubGlobal('fetch', f.fetch);
  vi.stubEnv('PATH', f.bin + delimiter + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  return f;
}
const pairPath = (f: any) => join(f.input.output, 'finalized-pair.json');
const withRequest = (f: any, r: any) => ({
  ...f.input,
  requestBytes: Buffer.from(canonicalJson(r)),
});
function cli(f: any) {
  return [
    '--repo',
    f.repo,
    '--source',
    f.source,
    '--version',
    f.input.releaseVersion,
    '--request',
    f.requestPath,
    '--archive',
    f.paths.archivePath,
    '--candidate',
    f.paths.candidatePath,
    '--map',
    f.paths.mapPath,
    '--bundle',
    f.paths.bundlePath,
  ];
}

describe('P3 original finalized pair, synthetic Git/API/verifier evidence only', () => {
  test('freezes original B/S/W and exact merged T/eight assets, remains unqualified and never mutates remote state', async () => {
    const f = fixture();
    const originals = [
      f.paths.archivePath,
      f.paths.candidatePath,
      f.paths.mapPath,
      f.paths.bundlePath,
    ].map((x: string) => sha256(readFileSync(x)));
    const out = await finalizeCandidate(f.input, {}),
      bytes = readFileSync(pairPath(f));
    const pair = parseFinalizedPair(bytes, out.pairSha256);
    expect(out).toMatchObject({
      qualified: false,
      contentVerified: true,
      storageVerified: true,
      provenanceVerified: true,
    });
    expect(
      new Set([
        pair.payload.identity.sourceCommit,
        pair.producer.workflowCommit,
        pair.action.commit,
      ]).size,
    ).toBe(3);
    expect(pair.action.commit).toBe(f.action);
    expect(pair.action.assets.map((x: any) => x.path)).toEqual(ACTION_ASSETS.map((x) => x[0]));
    expect(ACTION_ASSETS).toEqual(managedInstalledFiles);
    expect(pair.qualification.state).toBe('unqualified');
    expect(pair.payload.inventorySha256).toBe(sha256(canonicalJson(f.p.receipt.members)));
    expect(pair.payload.buildInputsSha256).toBe(
      sha256(canonicalJson(f.record.candidate.buildInputs)),
    );
    expect(pair.action.runtime.policy.version).toBe('10.0.13');
    expect(pair).not.toHaveProperty('receiptSha256');
    expect(f.record.candidate.buildInputs).not.toHaveProperty('actionCommit');
    expect(readFileSync(join(f.input.output, 'finalized-pair.sha256'), 'utf8')).toBe(
      out.pairSha256 + '\n',
    );
    expect(
      [f.paths.archivePath, f.paths.candidatePath, f.paths.mapPath, f.paths.bundlePath].map(
        (x: string) => sha256(readFileSync(x)),
      ),
    ).toEqual(originals);
    expect(f.calls.length).toBeGreaterThan(0);
    expect(
      f.calls.every(
        (x: any) =>
          x.method === 'GET' &&
          x.url.startsWith('https://api.github.com/repos/SolusQuest/agentic-pr-review/'),
      ),
    ).toBe(true);
    const input = { ...f.input, pairPath: pairPath(f), pairSha256: out.pairSha256 };
    await expect(verifyFinalizedPair(input, {})).resolves.toMatchObject({ pair });
    await expect(
      main(['inspect', ...cli(f), '--pair', pairPath(f), '--sha256', out.pairSha256], {}),
    ).resolves.toEqual({
      contentVerified: true,
      storageVerified: false,
      provenanceVerified: false,
      qualified: false,
    });
    await expect(finalizeCandidate(f.input, {})).rejects.toThrow('output_already_exists');
  });
  test('does not execute T and ignores mutable working-tree files when reading exact Git objects', async () => {
    const f = fixture();
    f.write(ACTION_ROOT + '/internal/dist/index.js', 'throw new Error("MUTABLE WORKTREE");');
    f.write('scripts/release/build-payload.inputs.json', 'MUTABLE SOURCE');
    const out = await main(['finalize', ...cli(f), '--output', f.input.output], {});
    expect(out).toMatchObject({ qualified: false });
  });
  test.each(ACTION_ASSETS.map((x) => [x[0]]))('refuses a missing T asset: %s', async (name) => {
    const f = fixture();
    rmSync(join(f.repo, ACTION_ROOT, name));
    const t = f.commit('Synthetic missing asset');
    f.main.object.sha = t;
    f.pr.merge_commit_sha = t;
    await expect(
      finalizeCandidate(withRequest(f, { ...f.request, actionCommit: t }), {}),
    ).rejects.toThrow('finalization_action_inventory_drift');
    expect(() => readFileSync(pairPath(f))).toThrow();
  });
  test.each(['extra', 'symlink', 'mode', 'map', 'runtime-source', 'runtime-bundle'])(
    'refuses changed committed composite closure: %s',
    async (kind) => {
      const f = fixture();
      if (kind === 'extra') f.write(ACTION_ROOT + '/extra.txt', 'unexpected');
      if (kind === 'symlink') {
        rmSync(join(f.repo, ACTION_ROOT, 'internal/action.yml'));
        symlinkSync('../action.yml', join(f.repo, ACTION_ROOT, 'internal/action.yml'));
      }
      if (kind === 'mode')
        f.git('update-index', '--chmod=+x', ACTION_ROOT + '/internal/action.yml');
      if (kind === 'map')
        f.write(
          ACTION_ROOT + '/payload-map.json',
          JSON.stringify(JSON.parse(f.record.mapBytes), null, 2) + '\n',
        );
      if (kind === 'runtime-source')
        f.write(
          'src/action-wrapper/launcher/runtime-prerequisite.ts',
          'export const PINNED_MANAGED_RUNTIME = attacker();',
        );
      if (kind === 'runtime-bundle')
        f.write(
          ACTION_ROOT + '/internal/dist/prepare.js',
          'const PINNED_MANAGED_RUNTIME = Object.freeze({version:"10.0.1",url:"https://evil.invalid",sha512:"bad",archiveBytes:1});',
        );
      // commit() stages files; explicit mode remains a staged Git identity change.
      const t =
        kind === 'mode'
          ? (f.git('commit', '-qm', 'Synthetic mode drift'), f.git('rev-parse', 'HEAD'))
          : f.commit('Synthetic changed composite');
      f.main.object.sha = t;
      f.pr.merge_commit_sha = t;
      await expect(
        finalizeCandidate(withRequest(f, { ...f.request, actionCommit: t }), {}),
      ).rejects.toThrow();
    },
  );
  test.each([
    'unmerged',
    'pr-head',
    'foreign',
    'wrong-id',
    'wrong-base',
    'main-ref',
    'not-main-ancestor',
    'source',
    'W-T-cycle',
    'S-T-cycle',
  ])('requires independently approved merged T and main/source lineage: %s', async (kind) => {
    const f = fixture();
    let input = f.input;
    if (kind === 'unmerged') f.pr.merged = false;
    if (kind === 'pr-head') f.pr.merge_commit_sha = f.qualifier;
    if (kind === 'foreign') f.pr.base.repo.full_name = 'foreign/repo';
    if (kind === 'wrong-id') f.pr.head.repo.id = '9';
    if (kind === 'wrong-base') f.pr.base.ref = 'topic';
    if (kind === 'main-ref') f.main.ref = 'refs/heads/Main';
    if (kind === 'not-main-ancestor') f.main.object.sha = f.workflow;
    if (kind === 'source') input = { ...f.input, sourceCommit: f.workflow };
    if (kind === 'W-T-cycle') input = withRequest(f, { ...f.request, actionCommit: f.workflow });
    if (kind === 'S-T-cycle') input = withRequest(f, { ...f.request, actionCommit: f.source });
    await expect(finalizeCandidate(input, {})).rejects.toThrow();
  });
  test.each(['policySha256', 'lockSha256'])(
    'independently checks admitted source closure despite a consistent signed fixture: %s',
    async (key) => {
      const f = fixture({ [key]: 'f'.repeat(64) });
      await expect(finalizeCandidate(f.input, {})).rejects.toThrow(
        'finalization_build_inputs_drift',
      );
    },
  );
  test('rejects declared dependency pins that differ from admitted S even with consistent candidate bytes', async () => {
    const base = fixture();
    const dependencies = structuredClone(base.record.candidate.buildInputs.dependencies);
    dependencies[0].sha256 = 'f'.repeat(64);
    const f = fixture({ dependencies });
    await expect(finalizeCandidate(f.input, {})).rejects.toThrow('finalization_build_inputs_drift');
  });
  test('recomputing a pair self-digest cannot substitute assets belonging to the approved T', async () => {
    const f = fixture();
    await finalizeCandidate(f.input, {});
    const pair = JSON.parse(readFileSync(pairPath(f), 'utf8'));
    pair.action.assets[0].sha256 = 'f'.repeat(64);
    pair.action.assetsSha256 = sha256(canonicalJson(pair.action.assets));
    const bytes = Buffer.from(canonicalJson(pair));
    writeFileSync(pairPath(f), bytes);
    await expect(
      verifyFinalizedPair({ ...f.input, pairPath: pairPath(f), pairSha256: sha256(bytes) }, {}),
    ).rejects.toThrow('finalization_pair_drift');
  });
  test.each([
    'archive',
    'candidate',
    'map',
    'missing-bundle',
    'saved-report',
    'wrong-cert',
    'tool-fail',
  ])('fails before producing a pair for original/provenance drift: %s', async (kind) => {
    const f = fixture();
    if (kind === 'archive') writeFileSync(f.paths.archivePath, 'changed');
    if (kind === 'candidate') writeFileSync(f.paths.candidatePath, '{}');
    if (kind === 'map') writeFileSync(f.paths.mapPath, '{}');
    if (kind === 'missing-bundle' || kind === 'saved-report') rmSync(f.paths.bundlePath);
    if (kind === 'saved-report')
      writeFileSync(
        join(f.root, 'verification.json'),
        '{"provenanceVerified":true,"completedRunVerified":true}',
      );
    if (kind === 'wrong-cert') {
      f.cert.runnerEnvironment = 'self-hosted';
      f.saveGh();
    }
    if (kind === 'tool-fail') vi.stubEnv('P3_TEST_GH_FAIL', '1');
    await expect(finalizeCandidate(f.input, {})).rejects.toThrow();
    expect(() => readFileSync(pairPath(f))).toThrow();
  });
  test.each([
    'expired',
    'missing',
    'wrong-digest',
    'wrong-run',
    'wrong-name',
    'duplicate',
    'in-progress',
  ])('authenticates separate original provenance storage: %s', async (kind) => {
    const f = fixture();
    if (kind === 'expired') f.artifacts[2].expired = true;
    if (kind === 'missing') f.missing.add('/actions/artifacts/203');
    if (kind === 'wrong-digest') f.artifacts[2].digest = 'sha256:' + 'f'.repeat(64);
    if (kind === 'wrong-run') f.artifacts[2].workflow_run.id = '999';
    if (kind === 'wrong-name') f.artifacts[2].name = 'another-provenance';
    if (kind === 'duplicate') f.artifacts.push({ ...f.artifacts[2], id: '204' });
    if (kind === 'in-progress') {
      f.releaseRun.status = 'in_progress';
      (f.releaseRun as any).conclusion = null;
    }
    await expect(finalizeCandidate(f.input, {})).rejects.toThrow();
  });
  test('requires independent pair digest and never changes unqualified state through JSON', async () => {
    const f = fixture();
    const out = await finalizeCandidate(f.input, {});
    const bytes = readFileSync(pairPath(f));
    expect(() => parseFinalizedPair(bytes, 'f'.repeat(64))).toThrow('finalization_digest_mismatch');
    const pair = JSON.parse(bytes.toString());
    pair.qualification.state = 'pass';
    const changed = Buffer.from(canonicalJson(pair));
    expect(() => parseFinalizedPair(changed, sha256(changed))).toThrow('finalization_pair_invalid');
    const oldDigest = out.pairSha256;
    pair.qualification.state = 'unqualified';
    pair.action.assets[0].sha256 = 'f'.repeat(64);
    writeFileSync(pairPath(f), canonicalJson(pair));
    await expect(
      verifyFinalizedPair({ ...f.input, pairPath: pairPath(f), pairSha256: oldDigest }, {}),
    ).rejects.toThrow();
  });
  test('rejects malformed, duplicate, unknown, aliased or oversized requests without remote access', () => {
    const f = fixture();
    for (const bytes of [
      Buffer.from('{}'),
      Buffer.from('{"candidateSha256":1,"candidateSha256":2}'),
      Buffer.alloc(PAIR_LIMITS.request + 1),
      Buffer.from(canonicalJson({ ...f.request, PRIVATE_CANARY: 'secret' })),
      Buffer.from(canonicalJson({ ...f.request, provenanceId: f.request.payloadId })),
    ])
      expect(() => parseFinalizationRequest(bytes)).toThrow();
    expect(f.calls).toHaveLength(0);
  });
  test('CLI hides raw failure details and has no qualification producer or endpoint override', () => {
    for (const args of [
      ['issue-passing-receipt'],
      ['finalize', '--api-url', 'https://foreign.invalid'],
    ]) {
      const result = spawnSync(
        process.execPath,
        [resolve('scripts/release/finalize-candidate.mjs'), ...args],
        { encoding: 'utf8' },
      );
      expect(result.status).toBe(1);
      expect(result.stdout).toBe('');
      expect(result.stderr).toBe('candidate_finalization_denied\n');
    }
  });
});
