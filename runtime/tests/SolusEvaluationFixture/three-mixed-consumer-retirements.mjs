import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-three-mixed-consumer-retirements.json';
const rawPolicySha256 = '9fe3086eda950f154313616be8a1734f829e01c8ceef8ab335b423222f3857fb';
const canonicalPolicySha256 = '7228b05468e6db85909f9eef77b717202f98926925b9c5cb0b0f23f39ce2daaa';
const inputSha256 = '9b9ac67363bf07648f1849b9d639f5a0e2d6f89d155335d1b564c9cc774bb6df';
const currentSha256 = 'e171bf7f42600914f2981d208f291b99ec973b5e30b6fc0ab589882614fb823c';
const names = [
  'MultipartCurrentContextCompletesLoopButProducesNoSession',
  'StableControlPrefixIsDerivedOnlyFromPolicyBytes',
  'DeepSeekLimitAuthorityRejectsCrossedPairsBeforeMaterializationAndRestore',
];
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
function read(repository, path) {
  assert.ok(typeof path === 'string' && path.startsWith('runtime/') && !path.includes('..'));
  const info = lstatSync(join(repository, path));
  assert.ok(info.isFile() && !info.isSymbolicLink() && info.size > 0 && info.size <= 1048576);
  return readFileSync(join(repository, path));
}
function load(repository) {
  const bytes = read(repository, relative);
  assert.equal(sha(bytes), rawPolicySha256, 'exact three mixed-consumer record bytes');
  return JSON.parse(bytes);
}
export function verifyThreeMixedContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact three mixed-consumer assertion dispositions',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.original_v1_membership, false);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.input_source.sha256, inputSha256);
  assert.equal(policy.current_source.sha256, currentSha256);
  assert.equal(policy.input_source.attributed_methods, 42);
  assert.equal(policy.current_source.attributed_methods, 39);
  assert.deepEqual(
    policy.rows.map((row) => row.method),
    names,
  );
  assert.deepEqual(
    policy.rows.map((row) => row.assertions.length),
    [3, 14, 17],
  );
  const expressions = policy.rows.flatMap((row) => row.assertions);
  assert.equal(
    expressions.filter((row) => row.disposition === 'retained-generic-execution').length,
    24,
  );
  assert.equal(
    expressions.filter((row) => row.disposition === 'obsolete-native-format-only').length,
    10,
  );
  assert.equal(policy.remaining_test_methods.length, 39);
  assert.equal(new Set(policy.remaining_test_methods.map((row) => row.method)).size, 39);
  for (const row of policy.rows) {
    assert.deepEqual(row.original.attributes, row.retirement_input.attributes);
    row.assertions.forEach((item, index) => {
      assert.equal(item.ordinal, index + 1);
      assert.equal(item.exact_expression, row.retirement_input.assertions[index]);
      assert.equal(item.original_exact_expression, row.original.assertions[index]);
      assert.equal(
        item.original_exact_expression.replaceAll(
          'AgentSessionHeadTransition',
          'ReviewedHeadTransition',
        ),
        item.exact_expression,
      );
      if (item.disposition === 'retained-generic-execution') {
        const owner = policy.current_owners.find(
          (candidate) =>
            candidate.source === item.owner.source &&
            candidate.method === item.owner.method &&
            candidate.body_sha256 === item.owner.body_sha256,
        );
        assert.ok(
          owner && owner.assertions.includes(item.exact_expression),
          'exact generic assertion owner',
        );
      } else assert.equal(item.owner, null);
    });
  }
  assert.deepEqual(
    policy.current_owners.map((row) => row.attributes),
    [
      ['Theory', 'InlineData(2)', 'InlineData(AgentLimits.PartsPerMessage)'],
      ['Fact'],
      ['Theory', 'InlineData(0)', 'InlineData(1)', 'InlineData(2)', 'InlineData(3)'],
      ['Fact'],
    ],
  );
}
export function verifyThreeMixedSourceLink(repository, supplied) {
  const policy = supplied ?? load(repository);
  verifyThreeMixedContract(policy);
  const bytes = read(repository, policy.current_source.path);
  assert.equal(sha(bytes), currentSha256, 'exact three-declaration removal source');
  for (const name of names)
    assert.equal(
      bytes.toString('utf8').includes(name),
      false,
      'retired mixed declaration is absent',
    );
  for (const row of policy.remaining_methods) {
    const body = bytes.subarray(row.utf8_start, row.utf8_start + row.utf8_length);
    assert.equal(body.length, row.utf8_length);
    assert.equal(sha(body), row.body_sha256, 'remaining declaration and helpers are exact');
  }
  for (const row of policy.current_owners) {
    const source = read(repository, row.source);
    assert.equal(sha(source), row.source_sha256, 'actual generic execution owner source');
    assert.equal(
      sha(source.subarray(row.utf8_start, row.utf8_start + row.utf8_length)),
      row.body_sha256,
      'actual generic execution owner declaration',
    );
  }
  return { retirementInputSha256: inputSha256, currentSourceSha256: currentSha256 };
}
export function verifyThreeMixedRetirements(repository) {
  const policy = load(repository);
  verifyThreeMixedSourceLink(repository, policy);
  for (const record of policy.immutable_records)
    assert.equal(
      sha(read(repository, record.path)),
      record.sha256,
      'prior disposition record is immutable',
    );
  return {
    methods: 3,
    assertionExpressions: 34,
    retainedGenericAssertions: 24,
    formatRetiredAssertions: 10,
    remainingMethods: 39,
    sdkContextImportQualified: false,
  };
}
