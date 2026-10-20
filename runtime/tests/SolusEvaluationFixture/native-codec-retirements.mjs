import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-native-codec-retirements.json';
const rawPolicySha256 = 'ffeb259555035f8d9ac856b2347105e63ef290b04249048a72b0ec44779fb57f';
const canonicalPolicySha256 = 'ca7bd8a0ac8915137acab87b5386834a614f397f675dc62823e63c088b2cabea';
const originalMethods = [
  {
    method: 'CanonicalWritersEnforceExactRecordAndSessionCaps',
    original_body_sha256: '24d950684362d6e3e37bd36a9e9f2433bcf1a49dc2ca324e78952ae31cbd5f67',
    input_body_sha256: '24d950684362d6e3e37bd36a9e9f2433bcf1a49dc2ca324e78952ae31cbd5f67',
    attributes: ['Fact'],
    assertions_sha256: '97c63b0df4f8a865f803e7ac09de3bd4cedcf8dea45cbb2b434596d818b71b62',
  },
  {
    method: 'MalformedDeclaredContinuationSlotUsesContinuationCode',
    original_body_sha256: 'db9274ccb0f3986a7d1d4b6f90ddec0dbcf6e84ef6e229c3acc15afbda3219a4',
    input_body_sha256: 'db9274ccb0f3986a7d1d4b6f90ddec0dbcf6e84ef6e229c3acc15afbda3219a4',
    attributes: ['Fact'],
    assertions_sha256: 'da608b4b4afbb7592a8946316ec2117b93c7fd5d26a8bc87216bfdd5abca28d1',
  },
  {
    method: 'ClassificationAndContinuationMutationsFailWithStableCodes',
    original_body_sha256: '3510799a8740f493f4bf0579044bf41420b569e45df9a6e5c2cdc7160124ce40',
    input_body_sha256: '9ab14dce5a0a070da5587623072d0309d27bd3c0d4b3569bd0da228b3d3a8cba',
    attributes: ['Fact'],
    assertions_sha256: 'f3d802209ad9ac2aec7e8b8721c62f646b62b35f94b9c1fbd5a84865eb1591a7',
  },
  {
    method: 'SyntheticContinuationVariantsRoundTripValueExactly',
    original_body_sha256: '353056cb44d4004d189c1a1923d3a997744b84de2caf40373010b91f7c7fc431',
    input_body_sha256: '903a0e28445268df56c81196effdcc98b0b70750e8e027552f4d42e88616114c',
    attributes: ['Fact'],
    assertions_sha256: '8f9c372e6d83c6157762448b5c2771d8e0fc3c5d5bcad2638150d8215777a25f',
  },
  {
    method: 'SyntheticStructuredCodecPreservesSignatureAndOrderedFields',
    original_body_sha256: 'a7d5492c679435003cbd7b87f4611d85894df163a1801a81e6c4aca00ffd7563',
    input_body_sha256: 'a7d5492c679435003cbd7b87f4611d85894df163a1801a81e6c4aca00ffd7563',
    attributes: ['Fact'],
    assertions_sha256: '3cc6a68f8aea6da967ed586925d59016669d8462a719c0b58d355ea631ff0df9',
  },
  {
    method: 'ContinuationItemAndAggregateCapsAreExact',
    original_body_sha256: '88b9632d20dba35e43435d2d3341b7ec4327622798893a92e1d7076bc3b21210',
    input_body_sha256: '88b9632d20dba35e43435d2d3341b7ec4327622798893a92e1d7076bc3b21210',
    attributes: ['Fact'],
    assertions_sha256: '403f7ce3c35aedf71a9c85c10952c29fb1890556304c6067da734c26b7da05ed',
  },
];
const inputSourceSha256 = '0a3c0edc5047b603f63ddba372cc2d8f9864257af5c89a3a00b51ccd9de5303d';
const remainingSourceSha256 = '9b9ac67363bf07648f1849b9d639f5a0e2d6f89d155335d1b564c9cc774bb6df';
const sha = (value) => createHash('sha256').update(value).digest('hex');
function read(repository, path) {
  assert.ok(typeof path === 'string' && path.startsWith('runtime/') && !path.includes('..'));
  const info = lstatSync(join(repository, path));
  assert.ok(info.isFile() && !info.isSymbolicLink() && info.size > 0 && info.size <= 1048576);
  return readFileSync(join(repository, path));
}
function load(repository) {
  const bytes = read(repository, relative);
  assert.equal(sha(bytes), rawPolicySha256, 'exact six-codec disposition bytes');
  return JSON.parse(bytes);
}
export function verifyNativeCodecContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact six-codec assertion dispositions',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.original_v1_membership, false);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.rows.length, 6);
  assert.equal(policy.retirement_input_source.sha256, inputSourceSha256);
  assert.equal(policy.current_source.sha256, remainingSourceSha256);
  assert.equal(policy.retirement_input_source.attributed_methods, 48);
  assert.equal(policy.current_source.attributed_methods, 42);
  assert.equal(
    policy.rows.reduce((count, row) => count + row.assertions.length, 0),
    43,
  );
  assert.deepEqual(
    policy.rows.map((row) => ({
      method: row.method,
      original_body_sha256: row.original.body_sha256,
      input_body_sha256: row.retirement_input.body_sha256,
      attributes: row.original.attributes,
      assertions_sha256: sha(JSON.stringify(row.assertions.map((item) => item.exact_expression))),
    })),
    originalMethods,
    'independent six original declarations and all 43 assertion expressions',
  );
  for (const row of policy.rows) {
    assert.equal(row.disposition, 'obsolete-native-format-only');
    assert.deepEqual(row.original.attributes, ['Fact']);
    assert.deepEqual(row.retirement_input.attributes, row.original.attributes);
    assert.ok(row.retirement_reason.length > 80 && row.retained_generic_obligation.length > 80);
    row.assertions.forEach((item, index) => {
      assert.equal(item.index, index);
      assert.equal(item.disposition, 'obsolete-native-format-only');
      assert.equal(item.reason, row.retirement_reason);
    });
  }
  assert.equal(new Set(policy.remaining_test_methods.map((row) => row.method)).size, 42);
}
// A bounded link from the unchanged earlier five-method record to this later exact source.
// Neither historical record nor its 53 -> 48 counts is rewritten.
export function verifyNativeCodecSourceLink(repository) {
  const policy = load(repository);
  verifyNativeCodecContract(policy);
  const bytes = read(repository, policy.current_source.path);
  assert.equal(sha(bytes), remainingSourceSha256, 'exact six-codec remaining source');
  for (const row of policy.rows)
    assert.equal(
      bytes.toString('utf8').includes(row.method),
      false,
      'retired codec declaration is absent',
    );
  for (const row of policy.remaining_test_methods) {
    const body = bytes.subarray(row.utf8_start, row.utf8_start + row.utf8_length);
    assert.equal(body.length, row.utf8_length);
    assert.equal(sha(body), row.body_sha256, 'exact retained attributed method body');
  }
  return { retirementInputSha256: inputSourceSha256, currentSourceSha256: remainingSourceSha256 };
}
export function verifyNativeCodecRetirements(repository, supplied) {
  const policy = supplied ?? load(repository);
  verifyNativeCodecContract(policy);
  verifyNativeCodecSourceLink(repository);
  for (const row of [...policy.retained_generic_sources, ...policy.independent_current_sources])
    assert.equal(
      sha(read(repository, row.path)),
      row.sha256,
      'generic and independent current owners are preserved',
    );
  return {
    methods: 6,
    assertionExpressions: 43,
    remainingMethods: 42,
    sdkContextImportQualified: false,
  };
}
