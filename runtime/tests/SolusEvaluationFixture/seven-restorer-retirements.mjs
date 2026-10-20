import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-seven-restorer-retirements.json';
const rawPolicySha256 = '90cd80973c67253859625ea2076fa294b147e6f799bd2f941c4564f352781a46';
const canonicalPolicySha256 = '236a68846825f9b7e46a1623ae3aa7dce0c2079e81a20f73c1a12f8df177385a';
const inputSha256 = '3beee3facd49735a0c5ac1658f95af2c52fb584231f94b75482cb0df0664ab35';
const currentSha256 = 'e29e299f5f08fb92d23657ea309fd06806ac6fac7f2af5d91f680229ea9e69be';
const names = [
  'CurrentBytesCannotDowngradeToBootstrap',
  'HostLocatorTableDoesNotParseCandidateBytes',
  'RestoreFailurePrecedenceIsDeterministic',
  'ScopeProducerAndTransitionPrecedeRawRecordConversion',
  'EarlierSessionFailuresPrecedeWrongContinuationTokens',
  'RepresentativeOldM4PayloadNeverReachesCurrentParser',
  'EveryContinuationSlotPositionRoundTripsPhysically',
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
  assert.equal(sha(bytes), rawPolicySha256, 'exact seven restorer-consumer record bytes');
  return JSON.parse(bytes);
}
export function verifySevenRestorerContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact seven restorer-consumer assertion dispositions',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.original_identity_origin.commit, 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2');
  assert.equal(policy.original_identity_origin.tree, '484c40c6aae3087428fb5169a18685eb744aa590');
  assert.equal(policy.original_identity_origin.path, policy.input_source.path);
  assert.equal(policy.original_v1_membership, false);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.input_source.sha256, inputSha256);
  assert.equal(policy.current_source.sha256, currentSha256);
  assert.equal(policy.input_source.attributed_methods, 36);
  assert.equal(policy.current_source.attributed_methods, 29);
  assert.deepEqual(
    policy.rows.map((row) => row.method),
    names,
  );
  assert.deepEqual(
    policy.rows.map((row) => row.assertions.length),
    [2, 1, 7, 4, 8, 3, 7],
  );
  const expressions = policy.rows.flatMap((row) => row.assertions);
  assert.equal(
    expressions.filter((row) => row.disposition === 'retained-generic-execution').length,
    4,
  );
  assert.equal(
    expressions.filter((row) => row.disposition === 'obsolete-native-format-only').length,
    28,
  );
  assert.equal(policy.remaining_test_methods.length, 29);
  assert.equal(new Set(policy.remaining_test_methods.map((row) => row.method)).size, 29);
  for (const row of policy.rows) {
    assert.equal(row.original.source, policy.original_identity_origin.path);
    assert.equal(row.original.source_sha256, policy.original_identity_origin.sha256);
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
export function verifySevenRestorerSourceLink(repository, supplied) {
  const policy = supplied ?? load(repository);
  verifySevenRestorerContract(policy);
  const bytes = read(repository, policy.current_source.path);
  assert.equal(sha(bytes), currentSha256, 'exact seven-declaration removal source');
  for (const name of names)
    assert.equal(
      bytes.toString('utf8').includes(name),
      false,
      'retired native restorer declaration is absent',
    );
  for (const row of policy.remaining_methods) {
    const body = bytes.subarray(row.utf8_start, row.utf8_start + row.utf8_length);
    assert.equal(body.length, row.utf8_length);
    assert.equal(sha(body), row.body_sha256, 'remaining declaration and helpers are exact');
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
  const source = read(repository, policy.callback.source).toString('utf8');
  const start = source.indexOf('request =>');
  const end = source.indexOf('\n                }),', start);
  assert.ok(start > 0 && end > start, 'actual current callback');
  const reversed = source
    .slice(start, end)
    .replace('actualResponse = new ProjectChatResponse(', 'return new ProjectChatResponse(')
    .replace('\n                    return actualResponse;', '')
    .replaceAll('PhysicalSlotFinishJson', 'FinishJson');
  assert.equal(
    sha(reversed),
    policy.callback.original_sha256,
    'original callback after capture-only reversal',
  );
  return { retirementInputSha256: inputSha256, currentSourceSha256: currentSha256 };
}
export function verifySevenRestorerRetirements(repository) {
  const policy = load(repository);
  verifySevenRestorerSourceLink(repository, policy);
  for (const record of policy.immutable_records)
    assert.equal(
      sha(read(repository, record.path)),
      record.sha256,
      'prior disposition record is immutable',
    );
  return {
    methods: 7,
    assertionExpressions: 32,
    retainedGenericAssertions: 4,
    formatRetiredAssertions: 28,
    remainingMethods: 29,
    sdkContextImportQualified: false,
  };
}
