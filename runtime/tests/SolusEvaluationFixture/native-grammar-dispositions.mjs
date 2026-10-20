import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { verifyNativeCodecSourceLink } from './native-codec-retirements.mjs';

const relative = 'runtime/tests/SolusEvaluationFixture/s5-native-grammar-dispositions.json';
const rawPolicySha256 = 'cb84020be56f72c6d6e08546265b31f3e08509d01f6643d29a15f7692bcda5a1';
const canonicalPolicySha256 = '8ef181e48c99ab46a4eebe8e87fbb1285af9d59c03ef977d63e691493bebe212';
// Independently captured with SDK Roslyn from the immutable original source
// and the actual authorized retirement input; these are real declarations.
const originalBodies = [
  {
    method: 'MinimalFrozenVectorsAreByteExact',
    body_sha256: '4346f15cbf60fdb4612d3af60e2674bce410a3e91eaa21ea9d225b330a8a6d6e',
    current_input_body_sha256: '4346f15cbf60fdb4612d3af60e2674bce410a3e91eaa21ea9d225b330a8a6d6e',
    attributes: ['Fact'],
  },
  {
    method: 'CanonicalFramingAndClosedRootMutationsFail',
    body_sha256: '0037206170c3609905109b391e35cf131f9af555f6590cf344e06cef72178538',
    current_input_body_sha256: '0037206170c3609905109b391e35cf131f9af555f6590cf344e06cef72178538',
    attributes: ['Fact'],
  },
  {
    method: 'RequiredRootFieldsRejectMissingNullTypeAndDomainMutations',
    body_sha256: '3435a2c7b436faec900ab34ce6900abe17af042b0ed1a332bfcd60f5129e5b93',
    current_input_body_sha256: '9fd2d11850600cba630d34941696bc49e4763ecdd9648ad032d74c7691d29af2',
    attributes: ['Fact'],
  },
  {
    method: 'Base64PayloadRejectsUrlUnpaddedAndWhitespaceForms',
    body_sha256: 'a7c4aa148c5b8130cbbcd84b86a9c859a2400cd34344c8443edce176b66202a3',
    current_input_body_sha256: 'a7c4aa148c5b8130cbbcd84b86a9c859a2400cd34344c8443edce176b66202a3',
    attributes: ['Fact'],
  },
  {
    method: 'DeclaredContinuationWrongTokenKindsUseContinuationCode',
    body_sha256: 'ca3bcfa225c83ed11b8d508e8f7c7114824f88c69c1ab7ce0a53d8230dbd8d41',
    current_input_body_sha256: 'ca3bcfa225c83ed11b8d508e8f7c7114824f88c69c1ab7ce0a53d8230dbd8d41',
    attributes: ['Fact'],
  },
];
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');

function readSource(repository, relative) {
  const path = join(repository, relative);
  const stat = lstatSync(path);
  assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'ordinary current source file');
  assert.ok(stat.size > 0 && stat.size <= 1048576, 'bounded source evidence');
  return readFileSync(path);
}

export function verifyNativeGrammarContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact native grammar triggers and actual owners',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.rows.length, 5);
  assert.deepEqual(
    policy.rows.map((row) => ({
      method: row.original.method,
      body_sha256: row.original.body_sha256,
      current_input_body_sha256: row.retirement_input.body_sha256,
      attributes: row.original.attributes,
    })),
    originalBodies,
    'independent original/current-input method bodies',
  );
  assert.equal(new Set(policy.oracles.map((oracle) => oracle.id)).size, policy.oracles.length);
  for (const row of policy.rows) {
    assert.equal(row.disposition, 'obsolete-native-format-only');
    assert.ok(row.triggers.length > 0 && row.independent_current_oracle_ids.length > 0);
    for (const id of row.independent_current_oracle_ids)
      assert.equal(policy.oracles.filter((oracle) => oracle.id === id).length, 1);
  }
}

export function verifyNativeGrammarSources(repository, policy) {
  verifyNativeGrammarContract(policy);
  const current = readSource(repository, policy.current_source.path);
  let exact = sha(current) === policy.current_source.sha256;
  if (!exact) {
    try {
      const later = verifyNativeCodecSourceLink(repository);
      exact =
        later.retirementInputSha256 === policy.current_source.sha256 &&
        later.currentSourceSha256 === sha(current);
    } catch {
      /* An unverified later source never waives the original source binding. */
    }
  }
  assert.ok(exact, 'exact remaining native grammar source');
  const text = current.toString('utf8');
  for (const row of policy.rows)
    assert.equal(
      text.includes(row.original.method),
      false,
      'retired native grammar method cannot be reintroduced or renamed retained',
    );
  for (const oracle of policy.oracles) {
    const bytes = readSource(repository, oracle.source);
    assert.equal(sha(bytes), oracle.source_sha256, oracle.id);
    const body = bytes.subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length);
    assert.ok(body.length > 0 && body.length === oracle.utf8_length);
    assert.equal(sha(body), oracle.body_sha256, oracle.id);
  }
}

export function verifyNativeGrammarDispositions(repository) {
  const raw = readSource(repository, relative);
  assert.equal(sha(raw), rawPolicySha256, 'frozen native grammar disposition bytes');
  const policy = JSON.parse(raw);
  verifyNativeGrammarSources(repository, policy);
  return policy.rows.length;
}
