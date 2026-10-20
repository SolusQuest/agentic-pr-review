import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { verifySevenRestorerSourceLink } from './seven-restorer-retirements.mjs';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-three-tool-format-retirements.json';
const rawPolicySha256 = '7d6765e7c73d0f227d8c44840055e7896179b74d9dabcc999c360c46ba3a354a';
const canonicalPolicySha256 = '5e372f3bd58ffe2143f71f45eb74680131b1052cfbb819559cbb705ae52dc503';
const inputSha256 = 'e171bf7f42600914f2981d208f291b99ec973b5e30b6fc0ab589882614fb823c';
const currentSha256 = '3beee3facd49735a0c5ac1658f95af2c52fb584231f94b75482cb0df0664ab35';
const names = [
  'StoredR3ToolArgumentsRequireCanonicalDefaultsAndNulls',
  'StoredToolArgumentsRequireNormalizedCanonicalBytes',
  'ClosedKindsOrderingOrdinalsAndAssociationsRejectMutation',
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
  assert.equal(sha(bytes), rawPolicySha256, 'exact three tool-format record bytes');
  return JSON.parse(bytes);
}
export function verifyThreeToolFormatContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact three tool-format assertion dispositions',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.original_v1_membership, false);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.input_source.sha256, inputSha256);
  assert.equal(policy.current_source.sha256, currentSha256);
  assert.equal(policy.input_source.attributed_methods, 39);
  assert.equal(policy.current_source.attributed_methods, 36);
  assert.deepEqual(
    policy.rows.map((row) => row.method),
    names,
  );
  assert.deepEqual(
    policy.rows.map((row) => row.assertions.length),
    [2, 11, 12],
  );
  const expressions = policy.rows.flatMap((row) => row.assertions);
  assert.equal(
    expressions.filter((row) => row.disposition === 'retained-generic-execution').length,
    5,
  );
  assert.equal(
    expressions.filter((row) => row.disposition === 'obsolete-native-format-only').length,
    20,
  );
  assert.equal(policy.remaining_test_methods.length, 36);
  assert.equal(new Set(policy.remaining_test_methods.map((row) => row.method)).size, 36);
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
    [['Fact']],
  );
}
export function verifyThreeToolFormatSourceLink(repository, supplied) {
  const policy = supplied ?? load(repository);
  verifyThreeToolFormatContract(policy);
  const bytes = read(repository, policy.current_source.path);
  let exact = sha(bytes) === currentSha256;
  if (!exact) {
    try {
      const later = verifySevenRestorerSourceLink(repository);
      exact =
        later.retirementInputSha256 === currentSha256 && later.currentSourceSha256 === sha(bytes);
    } catch {
      /* The later exact source and all its generic/current owners must verify. */
    }
  }
  assert.ok(exact, 'exact three-declaration removal source');
  for (const name of names)
    assert.equal(
      bytes.toString('utf8').includes(name),
      false,
      'retired mixed declaration is absent',
    );
  if (sha(bytes) === currentSha256) {
    for (const row of policy.remaining_methods) {
      const body = bytes.subarray(row.utf8_start, row.utf8_start + row.utf8_length);
      assert.equal(body.length, row.utf8_length);
      assert.equal(sha(body), row.body_sha256, 'remaining declaration and helpers are exact');
    }
  }
  for (const row of [...policy.current_owners, ...policy.independent_safety_owners]) {
    const source = read(repository, row.source);
    assert.equal(sha(source), row.source_sha256, 'actual generic execution owner source');
    assert.equal(
      sha(source.subarray(row.utf8_start, row.utf8_start + row.utf8_length)),
      row.body_sha256,
      'actual generic execution owner declaration',
    );
  }
  for (const row of policy.retained_generic_sources)
    assert.equal(
      sha(read(repository, row.path)),
      row.sha256,
      'retained generic tool and loop source',
    );
  return { retirementInputSha256: inputSha256, currentSourceSha256: sha(bytes) };
}
export function verifyThreeToolFormatRetirements(repository) {
  const policy = load(repository);
  verifyThreeToolFormatSourceLink(repository, policy);
  for (const record of policy.immutable_records)
    assert.equal(
      sha(read(repository, record.path)),
      record.sha256,
      'prior disposition record is immutable',
    );
  return {
    methods: 3,
    assertionExpressions: 25,
    retainedGenericAssertions: 5,
    formatRetiredAssertions: 20,
    remainingMethods: 36,
    sdkContextImportQualified: false,
  };
}
