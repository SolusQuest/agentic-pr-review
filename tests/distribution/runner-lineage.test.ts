import { execFileSync } from 'node:child_process';
import {
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import {
  generateActionBundle,
  generatePreparationBundle,
  generateSdkUploadBundle,
} from '../../scripts/build-action.mjs';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  runnerEvidence,
  RUNNER_ACTION,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/qualify-original-pair.mjs';

const roots: string[] = [];
const action = '.github/actions/agentic-pr-review';
const bundleNames = ['index.js', 'sdk-upload.js', 'prepare.js', 'cleanup.js'];
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function fixture() {
  const root = realpathSync(mkdtempSync(join(tmpdir(), 'apr-lineage-test-')));
  roots.push(root);
  execFileSync('git', ['clone', '--shared', '--no-checkout', '--quiet', process.cwd(), root]);
  const git = (...args: string[]) =>
    execFileSync('git', args, { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] })
      .toString()
      .trim();
  git('config', 'user.name', 'Synthetic source regression');
  git('config', 'user.email', 'fixture@example.invalid');
  git('read-tree', RUNNER_ACTION);
  git(
    'checkout',
    RUNNER_ACTION,
    '--',
    'src',
    'scripts',
    'package.json',
    'package-lock.json',
    action,
  );
  symlinkSync(
    resolve('node_modules'),
    join(root, 'node_modules'),
    process.platform === 'win32' ? 'junction' : 'dir',
  );
  const map = Buffer.from(canonicalJson({ synthetic: 'map data only, no operational candidate' }));
  const put = (path: string, bytes: string | Buffer) => writeFileSync(join(root, path), bytes);
  put(`${action}/payload-map.json`, map);
  const commit = (...paths: string[]) => {
    git('add', action, ...paths);
    git('commit', '-qm', 'Synthetic lineage mutation');
    return {
      action: { commit: git('rev-parse', 'HEAD') },
      payload: { proposedMapSha256: sha256(map) },
    };
  };
  const generate = async () => {
    const results = [
      await generateActionBundle(root),
      await generateSdkUploadBundle(root),
      await generatePreparationBundle(root),
      await generatePreparationBundle(root, true),
    ];
    results.forEach((r, n) => put(`${action}/internal/dist/${bundleNames[n]}`, r.bytes));
  };
  return { root, put, git, commit, generate };
}

// These direct source tests run Git fixtures and eight real bundle generations.
// This budget does not change any production consumer or qualification timeout.
describe(
  'fixed historical Runner lineage, never a current Runner execution',
  { timeout: 15_000 },
  () => {
    test('admits only exact regenerated map bytes with honest historical attribution', async () => {
      const f = fixture();
      await f.generate();
      expect(await runnerEvidence(f.root, f.commit())).toEqual({
        unchanged: false,
        reusable: true,
        reuseMode: 'authenticated-map-regeneration',
        actionCommit: RUNNER_ACTION,
        runs: ['39972608852', '40061259888', '40061741768'],
        assetCount: 7,
      });
    });
    test('refuses a changed map with all four stale historical bundles', async () => {
      const f = fixture();
      expect(await runnerEvidence(f.root, f.commit())).toMatchObject({
        unchanged: true,
        reusable: false,
      });
    });
    test.each(bundleNames)('refuses executable and forged stamp changes in %s', async (name) => {
      const f = fixture();
      await f.generate();
      const path = `${action}/internal/dist/${name}`;
      const original = readFileSync(join(f.root, path));
      f.put(path, Buffer.concat([Buffer.from('globalThis.injected = true;\n'), original]));
      expect((await runnerEvidence(f.root, f.commit())).reusable).toBe(false);
      f.put(
        path,
        original.toString().replace(/sha256: [a-f0-9]{64}\n$/, `sha256: ${'f'.repeat(64)}\n`),
      );
      expect((await runnerEvidence(f.root, f.commit())).reusable).toBe(false);
    });
    test.each(['marker', 'duplicate', 'newline', 'trailing'])(
      'refuses malformed %s footer',
      async (kind) => {
        const f = fixture();
        await f.generate();
        const path = `${action}/internal/dist/index.js`,
          original = readFileSync(join(f.root, path), 'utf8');
        const stamp = original.slice(original.lastIndexOf('// Action source inventory'));
        f.put(
          path,
          kind === 'marker'
            ? original.replace(stamp, stamp.replace('Action', 'SDK'))
            : kind === 'duplicate'
              ? original + stamp
              : kind === 'newline'
                ? original.slice(0, -1)
                : original + ' ',
        );
        expect((await runnerEvidence(f.root, f.commit())).reusable).toBe(false);
      },
    );
    test.each(['action.yml', 'internal/action.yml', 'internal/prepare/action.yml'])(
      'refuses changed Runner metadata %s',
      async (name) => {
        const f = fixture();
        await f.generate();
        f.put(`${action}/${name}`, 'changed metadata');
        expect((await runnerEvidence(f.root, f.commit())).reusable).toBe(false);
      },
    );
    test.each([
      'scripts/build-action.mjs',
      'package-lock.json',
      'src/action-wrapper/index.ts',
      'scripts/release/build-payload.format.mjs',
    ])('refuses source drift with otherwise forged-valid output: %s', async (path) => {
      const f = fixture();
      await f.generate();
      f.put(path, readFileSync(join(f.root, path), 'utf8') + '\n');
      expect((await runnerEvidence(f.root, f.commit(path))).reusable).toBe(false);
    });
    test('refuses mismatched map digest and noncanonical bytes', async () => {
      const f = fixture();
      await f.generate();
      const pair = f.commit();
      expect(
        (await runnerEvidence(f.root, { ...pair, payload: { proposedMapSha256: 'f'.repeat(64) } }))
          .reusable,
      ).toBe(false);
      const map = Buffer.from('{ "synthetic": true }\n');
      f.put(`${action}/payload-map.json`, map);
      await expect(
        runnerEvidence(f.root, { ...f.commit(), payload: { proposedMapSha256: sha256(map) } }),
      ).rejects.toThrow();
    });
    test('refuses unsafe source Git modes and missing historical objects', async () => {
      const f = fixture();
      await f.generate();
      const pair = f.commit();
      const path = 'src/action-wrapper/index.ts';
      f.git(
        'update-index',
        '--cacheinfo',
        `120000,${f.git('rev-parse', `${RUNNER_ACTION}:${path}`)},${path}`,
      );
      f.git('commit', '-qm', 'Synthetic unsafe mode');
      await expect(
        runnerEvidence(f.root, { ...pair, action: { commit: f.git('rev-parse', 'HEAD') } }),
      ).rejects.toThrow();
      await expect(
        runnerEvidence(f.root, { ...pair, action: { commit: 'f'.repeat(40) } }),
      ).rejects.toThrow();
    });
  },
);
