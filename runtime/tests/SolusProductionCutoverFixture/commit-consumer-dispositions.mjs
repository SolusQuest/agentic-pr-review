import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
const relative =
  'runtime/tests/SolusProductionCutoverFixture/s5-native-commit-consumer-dispositions.json';
const expected = {
  kind: 'apr-cr1-s5-three-native-commit-consumers-v1',
  owner_issue: 409,
  sdk_context_import_qualified: false,
  execution_qualification:
    'Source and body bindings do not qualify SDK execution. Original archive execution must independently pass on the reviewed source.',
  baseline_source_commit: 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2',
  rows: [
    {
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
      method: 'RealTransactionCommitsTwoEncryptedGenerations',
      original_body_sha256: '5e0d63dac825c5dc26716d7e404970b1c71aa74f5062a7bf2db3e78673e46b88',
      disposition: 'migrated-to-actual-sdk-proof',
      current_method: null,
      triggers: [
        'actual bootstrap generation0',
        'actual successor generation1',
        'exact scope and predecessor envelope',
        'accepted session/envelope hashes and time/TTL',
        'disk excludes prior fact/current prompt/provider key/base64 state key/raw state key',
        'two original archives execute production argv; no passive receipt',
      ],
      oracle_ids: ['actual-two-generation', 'actual-disk-privacy'],
      variants: [],
      current: null,
    },
    {
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
      method: 'IndependentLineageCannotBeSelfPromotedIntoBootstrapCandidate',
      original_body_sha256: '5e5498dea804b8c19bd87a824b605391d4132fd9e134167122f7fb165ea37489',
      disposition: 'migrated-current-test-consumer',
      current_method: 'IndependentLineageCannotBeSelfPromotedIntoBootstrapCandidate',
      triggers: [
        'genuine accepted source generation0 and exact head',
        'current restored authority and consumed source established',
        'fresh preparer cannot promote restored authority',
        'unclaimed source refuses restored preparation',
        'zero new provider/acceptance/publication',
      ],
      oracle_ids: ['current-authority'],
      variants: ['fresh-preparer', 'unclaimed'],
      current: {
        source:
          'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
        method: 'IndependentLineageCannotBeSelfPromotedIntoBootstrapCandidate',
        source_sha256: '1fb5445e25e662c68c3ddc18e4450dd658e358fbb6799917a9c111b28db22a02',
        utf8_start: 12334,
        utf8_length: 643,
        body_sha256: '06ecbd887e814a5b75064777270a066de65a45188cd09a310330ef902cea29be',
      },
    },
    {
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
      method: 'PreflightAndFactoryFailuresZeroEveryOwnedPlaintext',
      original_body_sha256: '4a08d48f306bbe18ed0391d3713b271fb19ab08bbed11ede13f2d293bfbf9255',
      disposition: 'migrated-current-test-consumer',
      current_method: 'PreflightAndFactoryFailuresZeroEveryOwnedPlaintext',
      triggers: [
        'factory throws; caller plaintext zero',
        'transaction observes real production clone then throws',
        'caller and transaction clone zero',
        'prepare/reconcile/accept/sink counts and no accepted identity',
        'native builder/admitted-session copies explicitly obsolete',
      ],
      oracle_ids: ['factory-zeroing', 'transaction-zeroing'],
      variants: [],
      current: {
        source:
          'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
        method: 'PreflightAndFactoryFailuresZeroEveryOwnedPlaintext',
        source_sha256: '1fb5445e25e662c68c3ddc18e4450dd658e358fbb6799917a9c111b28db22a02',
        utf8_start: 12977,
        utf8_length: 407,
        body_sha256: '16db5975f160aa0ef9c2a6efdd9425823881590cce852a9ad186f6bd549589e0',
      },
    },
  ],
  oracles: [
    {
      id: 'actual-two-generation',
      source: 'runtime/tests/SolusProductionCutoverFixture/Program.cs',
      method: 'Main',
      source_sha256: '4a553f610504da7edf9d6e724db825ce6559a59039659db79d859414de3921ec',
      utf8_start: 722,
      utf8_length: 10101,
      body_sha256: 'f0d292216ad72236f90962689c7995c8376967a56c9a81d371e53ef719c1ed0f',
    },
    {
      id: 'actual-disk-privacy',
      source: 'runtime/tests/SolusProductionCutoverFixture/Program.cs',
      method: 'RequireEncryptedState',
      source_sha256: '4a553f610504da7edf9d6e724db825ce6559a59039659db79d859414de3921ec',
      utf8_start: 12113,
      utf8_length: 942,
      body_sha256: 'eaa880bd4c39b33c3c924301c88c9f287f308a5b4131a6a627a4cb62d10365b3',
    },
    {
      id: 'current-authority',
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/ActionHostCompositionTests.SolusRestoredReview.cs',
      method: 'SolusSuccessorRequiresExactConsumedSourceAndNewCheckpoint',
      source_sha256: 'd1ac0b4005ef34415c88a900f8504a5315834369ed4fdae96316811140ecaae1',
      utf8_start: 582,
      utf8_length: 9401,
      body_sha256: '7d12493f4e1b3ddb6a7771d2bc765f90505f4814337af5a9b5c14fa6d10f17e2',
    },
    {
      id: 'factory-zeroing',
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
      method: 'CurrentPreparedFactoryFailureZerosCallerPlaintext',
      source_sha256: '1fb5445e25e662c68c3ddc18e4450dd658e358fbb6799917a9c111b28db22a02',
      utf8_start: 14443,
      utf8_length: 986,
      body_sha256: '1d3b9159e2475c8961ba390415c7e8f4b1041e2d6cfe5b5ee93513678fd940bd',
    },
    {
      id: 'transaction-zeroing',
      source:
        'runtime/tests/AgenticPrReview.Runtime.Tests/Application/LiveAgentStateCommitTests.cs',
      method: 'CurrentPreparedTransactionExceptionZerosBothOwnedPlaintextCopies',
      source_sha256: '1fb5445e25e662c68c3ddc18e4450dd658e358fbb6799917a9c111b28db22a02',
      utf8_start: 13384,
      utf8_length: 1059,
      body_sha256: '6a4286a4a90b4d962ca4a1605192ed63ff5c6567bdd4268a4bd4c66a037acb68',
    },
  ],
};

const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
export function verifyCommitConsumerContract(policy) {
  assert.deepEqual(policy, expected, 'exact original triggers and actual oracle owners/bodies');
}
export function verifyCommitConsumerSources(repository, policy) {
  verifyCommitConsumerContract(policy);
  for (const oracle of [
    ...policy.oracles,
    ...policy.rows.flatMap((row) => (row.current ? [row.current] : [])),
  ]) {
    const bytes = readFileSync(join(repository, oracle.source));
    assert.equal(sha(bytes), oracle.source_sha256, oracle.source);
    const body = bytes.subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length);
    assert.ok(body.length > 0 && body.length === oracle.utf8_length);
    assert.equal(sha(body), oracle.body_sha256, oracle.id);
  }
}
export function verifyCommitConsumerDispositions(repository) {
  const policy = JSON.parse(readFileSync(join(repository, relative)));
  verifyCommitConsumerSources(repository, policy);
  const removed = policy.rows.find((row) => row.current === null);
  assert.ok(
    removed &&
      !readFileSync(join(repository, removed.source), 'utf8').includes(
        'public async Task ' + removed.method + '(',
      ),
    'migrated SDK owner cannot be labelled retained native method',
  );
  return policy.rows.length;
}
