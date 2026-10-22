import fs from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { build } from 'esbuild';
import { afterEach, describe, expect, test } from 'vitest';
import {
  verifyCapacityHelperContract,
  verifyCapacityHelperGraph,
  verifyCapacityHelperIdentity,
  capacityHelperCurrentFiles,
} from '../runtime/tests/SolusCapacityFixture/helper-identity.mjs';

const policyPath = 'runtime/tests/SolusCapacityFixture/s5-helper-identity.json';
const load = () => JSON.parse(fs.readFileSync(policyPath, 'utf8'));
const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { force: true, recursive: true });
});
function copyClosure() {
  const root = fs.mkdtempSync(path.join(tmpdir(), 'apr-s5-helper-'));
  roots.push(root);
  for (const relative of [
    policyPath,
    ...capacityHelperCurrentFiles.map((file: { path: string }) => file.path),
  ]) {
    const target = path.join(root, relative);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.copyFileSync(relative, target);
  }
  return root;
}

describe('S5 capacity executed helper identity', () => {
  test('pins the current fixture graph and preserves original historical references', async () => {
    const policy = load();
    verifyCapacityHelperContract(policy);
    const identity = verifyCapacityHelperIdentity(process.cwd());
    expect(Object.keys(identity)).toEqual([
      'historical_source_commit',
      'fixture_wiring_owner_issue',
      'source_identity_is_execution_qualification',
      'files',
    ]);
    expect(identity.historical_source_commit).toBe('07fe42b2d81eee8d20b401433a21838041ad22d6');
    expect(identity.fixture_wiring_owner_issue).toBe(449);
    expect(identity.source_identity_is_execution_qualification).toBe(false);
    expect(identity.files).toHaveLength(12);
    expect(policy.historical.source_commit).toBe('e08f596b4d195b360312e466d808d1d91ccb4595');
    expect(policy.historical.files[0].sha256).toBe(
      'c3559fbe081cb0b7df6dcaa7f97f43f6de878dece29990ae5b7212fa99fa19cc',
    );
    const bundle = await build({
      entryPoints: [policy.current.entrypoint],
      write: false,
      bundle: true,
      platform: 'node',
      format: 'esm',
      target: 'node24',
      metafile: true,
    });
    expect(() => verifyCapacityHelperGraph(Object.keys(bundle.metafile.inputs))).not.toThrow();
    expect(Object.keys(bundle.metafile.inputs)).not.toContain(
      'src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts',
    );
    expect(verifyCapacityHelperIdentity(copyClosure())).toEqual(identity);
  });

  test.each([
    'kind',
    'owner',
    'sdk-overclaim',
    'role',
    'historical-source',
    'historical-pin',
    'missing-historical',
    'source',
    'tree',
    'entrypoint',
    'missing',
    'duplicate',
    'unknown',
    'escape',
    'digest',
    'extra-field',
    'reorder',
  ])('rejects contract %s against an independent fixed baseline', (mutation) => {
    const policy = load();
    if (mutation === 'kind') policy.kind = 'unverified';
    if (mutation === 'owner') policy.owner_issue = 403;
    if (mutation === 'sdk-overclaim') policy.sdk_context_import_qualified = true;
    if (mutation === 'role') policy.role = 'SDK import qualified';
    if (mutation === 'historical-source')
      policy.historical.source_commit = policy.current.source_commit;
    if (mutation === 'historical-pin')
      policy.historical.files[0].sha256 = policy.current.files[0].sha256;
    if (mutation === 'missing-historical') delete policy.historical;
    if (mutation === 'source') policy.current.source_commit = '0'.repeat(40);
    if (mutation === 'tree') policy.current.source_tree = '0'.repeat(40);
    if (mutation === 'entrypoint') policy.current.entrypoint = policy.historical.files[0].path;
    if (mutation === 'missing') policy.current.files.pop();
    if (mutation === 'duplicate')
      policy.current.files[1] = structuredClone(policy.current.files[0]);
    if (mutation === 'unknown') policy.current.files[0].path = 'unclassified.ts';
    if (mutation === 'escape') policy.current.files[0].path = '../private-canary';
    if (mutation === 'digest') policy.current.files[0].sha256 = '0'.repeat(64);
    if (mutation === 'extra-field') policy.secret = 'private-canary';
    if (mutation === 'reorder') policy.current.files.reverse();
    // No manifest seal/re-signing code runs on this path: fixed original
    // component identities must independently reject the mutated document.
    expect(() => verifyCapacityHelperContract(policy)).toThrow(
      'closed S5 helper identity contract',
    );
  });

  test.each(['missing', 'extra', 'duplicate', 'escape', 'substitute-example'])(
    'rejects actual bundle graph %s',
    (mutation) => {
      const inputs = load().current.files.map((file: { path: string }) => file.path);
      if (mutation === 'missing') inputs.pop();
      if (mutation === 'extra') inputs.push('src/undeclared.ts');
      if (mutation === 'duplicate') inputs[1] = inputs[0];
      if (mutation === 'escape') inputs[0] = '../private-canary';
      if (mutation === 'substitute-example')
        inputs[0] = 'src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts';
      expect(() => verifyCapacityHelperGraph(inputs)).toThrow();
    },
  );

  test.each([
    'source',
    'source-and-manifest',
    'missing-source',
    'symlink',
    'duplicate-json-key',
    'missing-policy',
  ])('rejects physical %s drift without fallback', (mutation) => {
    const root = copyClosure();
    const policy = load();
    const file = policy.current.files[0];
    const target = path.join(root, file.path);
    if (mutation === 'source' || mutation === 'source-and-manifest')
      fs.appendFileSync(target, '\n');
    if (mutation === 'source-and-manifest') {
      file.sha256 = createHash('sha256').update(fs.readFileSync(target)).digest('hex');
      // Re-signing both physical source and the manifest cannot change the
      // independently frozen 07fe component oracle or the current reader.
      expect(() => verifyCapacityHelperContract(policy)).toThrow(
        'closed S5 helper identity contract',
      );
      fs.writeFileSync(path.join(root, policyPath), JSON.stringify(policy, null, 2) + '\n');
    }
    if (mutation === 'missing-source') fs.unlinkSync(target);
    if (mutation === 'symlink') {
      const outside = path.join(root, 'alias');
      fs.copyFileSync(target, outside);
      fs.unlinkSync(target);
      fs.symlinkSync(outside, target);
    }
    if (mutation === 'duplicate-json-key')
      fs.writeFileSync(
        path.join(root, policyPath),
        fs
          .readFileSync(policyPath, 'utf8')
          .replace('"owner_issue": 409,', '"owner_issue": 409, "owner_issue": 409,'),
      );
    if (mutation === 'missing-policy') fs.unlinkSync(path.join(root, policyPath));
    expect(() => verifyCapacityHelperIdentity(root)).toThrow();
  });
});
