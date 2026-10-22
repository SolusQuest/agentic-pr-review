import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

// Fixed independent oracle: each current file was checked against git show at
// this component revision. It is not the reviewed application source revision.
// Historical helper identities remain references to their original bytes.
const expected = {
  kind: 'apr-cr1-s5-capacity-helper-identity-v1',
  owner_issue: 409,
  sdk_context_import_qualified: false,
  role: 'unchanged-executed-helper-source-identity-only',
  historical: {
    source_commit: 'e08f596b4d195b360312e466d808d1d91ccb4595',
    files: [
      {
        path: 'src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts',
        sha256: 'c3559fbe081cb0b7df6dcaa7f97f43f6de878dece29990ae5b7212fa99fa19cc',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.ts',
        sha256: '5eaa89931542b5ae7eb2939f27d4bc8843a2ed7f048e2c00939ff1f5fec90c72',
      },
    ],
  },
  current: {
    source_commit: '07fe42b2d81eee8d20b401433a21838041ad22d6',
    source_tree: '6d0704e6ab0774f7017cc81c567b87c4f76acc1a',
    entrypoint: 'runtime/tests/SolusCapacityFixture/CapacityArtifactServer.mjs',
    files: [
      {
        path: 'runtime/tests/SolusCapacityFixture/CapacityArtifactServer.mjs',
        sha256: 'eff4399c175a7f46490ad8bb283472adb5c30f420d1bec3ba0eade9ebf237ccd',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/bridge-server.ts',
        sha256: '703758ae7e3a37ba360207efc2a6b1d2f297f75d9cc1c9d5778b2afa59325559',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/contracts.ts',
        sha256: 'c6db8cd6cd119f24848d8d989ad5e72b9d8df40e3a4df8b4e8e1c414d5059853',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/correlations.ts',
        sha256: '43f306b1ca553c6273b778ed876b7f1e839c93ca0ece06cb9c9c1ad30b5baf0f',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/framing.ts',
        sha256: '23ad18705bd09f567c776a6d774135adccb19cfda7904540d07f847ebf292d97',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/limits.ts',
        sha256: 'ebdbfa37e972afcfe16c578e2ef3866b0d369c0412d6c413133964f6853b2459',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/operation-budget.ts',
        sha256: '9f20fb4b6544abf6530790722f24672b6bb4c0e92aa5d0b07e7e303e9e45366e',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/staging.ts',
        sha256: '91600767e90d8112bd8492d70813bce924065595807c7e47a3b9249787e84e8f',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/strict-json.ts',
        sha256: '16737770d5f229a770aafaf6530b61ec47827949cd4c9b35912f694e1333e6b9',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.ts',
        sha256: '5eaa89931542b5ae7eb2939f27d4bc8843a2ed7f048e2c00939ff1f5fec90c72',
      },
      {
        path: 'src/action-wrapper/artifact-bridge/transport-envelope.ts',
        sha256: '54911fa3828afd5b565c3de8c7e49732a223b194c3ab9298493639b9a102af0a',
      },
    ],
  },
};

// Preserve the immutable S5 component oracle above. T1's fixture-only import
// relocation has its own exact source graph and makes no execution-equivalence
// claim for these new bytes.
const relocated = new Map([
  [
    'runtime/tests/SolusCapacityFixture/CapacityArtifactServer.mjs',
    [
      'runtime/tests/SolusCapacityFixture/CapacityArtifactServer.mjs',
      'fe0be0ac984fcbcdb5f23da1ca8837b0a118b90ebb1160f4e107a06877d11aff',
    ],
  ],
  [
    'src/action-wrapper/artifact-bridge/bridge-server.ts',
    [
      'src/action-wrapper/artifact-bridge/historical-bridge-server.fixture.ts',
      '5a2720f0a624a5e4ba8d1a2fd011a9617dfcda220f93fb7a5e631cfa6ac15f7d',
    ],
  ],
  [
    'src/action-wrapper/artifact-bridge/contracts.ts',
    [
      'src/action-wrapper/artifact-bridge/historical-contracts.fixture.ts',
      'b6319e6cab8ab550e0873f8d7fcc41fe4559e529acaaf7708c96bdd839c25612',
    ],
  ],
  [
    'src/action-wrapper/artifact-bridge/framing.ts',
    [
      'src/action-wrapper/artifact-bridge/historical-framing.fixture.ts',
      '14566e31d55ccfd50784a166d7fe8b87d0c898e14d206745fbdab6888a93109e',
    ],
  ],
]);
export const capacityHelperCurrentFiles = expected.current.files.map((file) => {
  const replacement = relocated.get(file.path);
  return replacement ? { path: replacement[0], sha256: replacement[1] } : file;
});
// Shared scalar/path checks remain current; only the historical server/result
// interpreter is isolated. esbuild includes this shared dependency explicitly.
capacityHelperCurrentFiles.push({
  path: 'src/action-wrapper/artifact-bridge/contracts.ts',
  sha256: '96e180af6117e2f4df6ab48403aacdba80abf97434fba7e1d9677250f5166ecf',
});

export function verifyCapacityHelperContract(document) {
  assert.deepEqual(document, expected, 'closed S5 helper identity contract');
}

export function verifyCapacityHelperIdentity(repository) {
  const policyPath = join(repository, 'runtime/tests/SolusCapacityFixture/s5-helper-identity.json');
  const policyInfo = lstatSync(policyPath);
  assert.ok(policyInfo.isFile() && !policyInfo.isSymbolicLink() && policyInfo.size <= 65536);
  const text = readFileSync(policyPath, 'utf8');
  // This compares to the independently pinned baseline, not a self-reported seal.
  assert.equal(text, JSON.stringify(expected, null, 2) + '\n', 'fixed helper identity bytes');
  verifyCapacityHelperContract(JSON.parse(text));
  for (const file of capacityHelperCurrentFiles) {
    const path = join(repository, file.path);
    const info = lstatSync(path);
    assert.ok(info.isFile() && !info.isSymbolicLink() && info.size <= 4 * 1024 * 1024);
    assert.equal(
      createHash('sha256').update(readFileSync(path)).digest('hex'),
      file.sha256,
      file.path,
    );
  }
  return structuredClone({
    historical_source_commit: expected.current.source_commit,
    fixture_wiring_owner_issue: 449,
    source_identity_is_execution_qualification: false,
    files: capacityHelperCurrentFiles,
  });
}

export function verifyCapacityHelperGraph(inputs) {
  assert.ok(Array.isArray(inputs));
  assert.deepEqual(
    inputs.slice().sort(),
    capacityHelperCurrentFiles.map((file) => file.path).sort(),
    'actual capacity bundle must use exactly the pinned helper source closure',
  );
}
