import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
const relative = 'runtime/tests/SolusProductionCutoverFixture/s5-r3-application-dispositions.json';
const rawSha256 = 'c0d71ccdf797aa421f7bfd486c327df29fd105c69fece2e2972155897e4b001f';
const canonicalSha256 = '6b87764cf6c45361877bf2b8920a41a190e9255521eb6d58ae69dcdcd5054e18';
const originalBodies = [
  {
    method: 'BootstrapRunsThroughSixToolAgentLoopAndReturnsExactCandidate',
    original_body_sha256: 'df87b509fdccddaec69b0db688aec44f1db30dc74a72f1cfd2032855689429c3',
    input_body_sha256: 'a250171cfe2b786981f5dcee5962698b57796d002eaab42e884a22555fe05964',
    attributes: ['Fact'],
  },
  {
    method: 'LiveAgentVerifierInnerAuthorizationDenialPrecedesNetworkActivation',
    original_body_sha256: '7585a608cb0bab3b87a53e81bef049287a75628fe65b5acdade09807db48072f',
    input_body_sha256: '7585a608cb0bab3b87a53e81bef049287a75628fe65b5acdade09807db48072f',
    attributes: ['Fact'],
  },
  {
    method: 'LiveAgentVerifierQualityFailureAfterCommitPreservesAcceptedTruth',
    original_body_sha256: '59b1a6f876627505b5a3e2f0cf0abadf42cea143a2eb40ee91d6b2fa7f413028',
    input_body_sha256: '59b1a6f876627505b5a3e2f0cf0abadf42cea143a2eb40ee91d6b2fa7f413028',
    attributes: ['Fact'],
  },
  {
    method: 'FailureAfterCompletedQualityKeepsTheActualPrimaryCause',
    original_body_sha256: '5e7a4c093b138f0dc0dccf137b235e9c1c0fcce025c6f0c785de7f3f4bf3503f',
    input_body_sha256: '5e7a4c093b138f0dc0dccf137b235e9c1c0fcce025c6f0c785de7f3f4bf3503f',
    attributes: [
      'Theory',
      'InlineData("commit_result", AgentSessionCodes.RecordInvalid)',
      'InlineData("commit_exception", R3LiveAgentDiagnosticCodes.StateCommitFailed)',
      'InlineData("result_projection_exception", R3LiveAgentDiagnosticCodes.ResultFailed)',
    ],
  },
  {
    method: 'EveryStableScopeFieldAndTrustFactIsIndependentlyDenied',
    original_body_sha256: 'b70d98f79df8e08ebced82fec981dbf20a1a4eacbce0746520a52d1c394888c7',
    input_body_sha256: 'b70d98f79df8e08ebced82fec981dbf20a1a4eacbce0746520a52d1c394888c7',
    attributes: ['Fact'],
  },
  {
    method: 'StateFailureNeverDowngradesToBootstrap',
    original_body_sha256: 'dac3bb5922d9d4101550c95a2f8cb6525f5fb187c2973fcffb228f4f1a9a1eab',
    input_body_sha256: 'dac3bb5922d9d4101550c95a2f8cb6525f5fb187c2973fcffb228f4f1a9a1eab',
    attributes: ['Fact'],
  },
  {
    method: 'CompositionFailuresExposeEveryStableStageDiagnostic',
    original_body_sha256: 'b49bacaaf269bc42f13eb5cd586258c9190f1b7169cb16f193a5c9fd7a62c39f',
    input_body_sha256: 'b49bacaaf269bc42f13eb5cd586258c9190f1b7169cb16f193a5c9fd7a62c39f',
    attributes: [
      'Theory',
      'InlineData(R3LiveAgentDiagnosticCodes.PreparationFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.StateRestoreFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.SnapshotFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.TransportFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.AgentRunFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.StateCommitFailed)',
      'InlineData(R3LiveAgentDiagnosticCodes.ResultFailed)',
    ],
  },
  {
    method: 'InvalidSnapshotAndCancellationProduceNoCandidateOrProviderCall',
    original_body_sha256: 'f9486172890e2f6cfac4726e6d470a25cdfa112034fad7e7ab030e2e6a252929',
    input_body_sha256: 'f9486172890e2f6cfac4726e6d470a25cdfa112034fad7e7ab030e2e6a252929',
    attributes: ['Fact'],
  },
  {
    method: 'InvalidSecretsAreClearedBeforeStateOrProvider',
    original_body_sha256: '7ec8b2056ee008da61e113ea909fde4c0cd2f44fc42b8810cf28ef09e8d49d50',
    input_body_sha256: '7ec8b2056ee008da61e113ea909fde4c0cd2f44fc42b8810cf28ef09e8d49d50',
    attributes: [
      'Theory',
      'InlineData(null, StateSecret)',
      'InlineData("", StateSecret)',
      'InlineData(ProviderSecret, null)',
      'InlineData(ProviderSecret, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")',
      'InlineData(ProviderSecret, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")',
    ],
  },
  {
    method: 'RestoreUsesExactAdmittedRequestAndZerosRedundantPlaintext',
    original_body_sha256: 'f1eaa957ef0b905bda41b2988ec2c6f6c2c52ebd759f1d6acb54508550daf2a0',
    input_body_sha256: 'f1eaa957ef0b905bda41b2988ec2c6f6c2c52ebd759f1d6acb54508550daf2a0',
    attributes: ['Fact'],
  },
  {
    method: 'FailedRestoredRunZerosEveryAdmittedPlaintextCopy',
    original_body_sha256: '968d0c7634238a2e61c2eb5663334efbbff738bf2882055c534a5ce05f42a0e7',
    input_body_sha256: '968d0c7634238a2e61c2eb5663334efbbff738bf2882055c534a5ce05f42a0e7',
    attributes: ['Fact'],
  },
  {
    method: 'RealEncryptedStateRestoreMatchesAuthoritativeRequestBytes',
    original_body_sha256: 'b586c988a2c8ee1f7f6a26adc227d54304497dc0c82cff1c72920c418f5c58c1',
    input_body_sha256: 'f1742a3edd6f5ab0f4895ffe0dd77d7bcd52946b151bea27644078dc15e570f3',
    attributes: ['Fact'],
  },
  {
    method: 'CandidateHasExactlyEightInternalFieldsAndNoPublicDataSurface',
    original_body_sha256: '18bd1e86b8fbb96509f80fa53f777ff300eef941ff929670f093318462c8245c',
    input_body_sha256: 'b071fff67f512f6fdc51f666624fe75f19db0d3cfa81fb584307aa94d0e3d725',
    attributes: ['Fact'],
  },
];
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
function read(repository, relative) {
  const file = join(repository, relative),
    stat = lstatSync(file);
  assert.ok(stat.isFile() && !stat.isSymbolicLink(), 'ordinary R3 source');
  assert.ok(stat.size > 0 && stat.size <= 1048576, 'bounded R3 source');
  return readFileSync(file);
}
const originalCliBodies = [
  {
    method: 'BootstrapAndContinueRunInTwoFreshProcesses',
    body_sha256: 'c8ffba6584a4e52dd4c51e3f25fd6c467ff46d3998cb6853492b67a01081015b',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
  {
    method: 'AutomaticAbsentOrNonCurrentContinueBootstraps',
    body_sha256: '76b803fd38ff2522abbfae58fe5b55819954489bf972176671595d5a7a614e67',
    attributes: ['Theory', 'InlineData("absent")', 'InlineData("non_current")'],
    expanded_native_cases: 2,
  },
  {
    method: 'ExplicitAbsentOrNonCurrentRetainsExactFailure',
    body_sha256: '1f296164a705be9b0b60b772e6a647d53491377151687ecfb251f2574f2bd34a',
    attributes: ['Theory', 'InlineData("absent")', 'InlineData("non_current")'],
    expanded_native_cases: 2,
  },
  {
    method: 'ReusedInvocationIdentityFailsBeforeStateOrProviderAdvance',
    body_sha256: 'dffaad13f157f201b427cc6644e56ac24caa4eecbe70b021c00ef451aaaa6082',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
  {
    method: 'LiveAgentVerifierInvalidTransitionStopsBeforeProviderConstruction',
    body_sha256: 'f6fc8975675c3b73e7eed3200dd95abd5826ea94ed50072547e093f9d7a68a01',
    attributes: [
      'Theory',
      'InlineData("unknown")',
      'InlineData("diverged")',
      'InlineData("unrelated")',
    ],
    expanded_native_cases: 3,
  },
  {
    method: 'CurrentAutomaticIsRejectedBeforeProviderConstruction',
    body_sha256: 'ca3caf783ccf8df0b91cfc143b5d1f2a4cc24b67d4a9a7d75f4b6ec0ba377d65',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
  {
    method: 'SelectedCurrentMissingRetainsExactFailure',
    body_sha256: '907e5e48543b10622c13f7769c5d83bfac2708d9240f26e662d5510bf65ae99e',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
  {
    method: 'MissingOrMalformedSecretsFailBeforeStateCommit',
    body_sha256: '7b34d646dee9d435e6bbb3feba23aca90322af80e651b7d8680ab93eca51f028',
    attributes: [
      'Theory',
      'InlineData(null, StateKey)',
      'InlineData(ProviderSecret, "not-canonical-base64")',
    ],
    expanded_native_cases: 2,
  },
  {
    method: 'ParentSyncFailureResultCarriesCommittedLineageDigest',
    body_sha256: 'f076a03df3ff9ef02b3a5029aa77d48ba5db9b51b6df30ff266722c1eba66edd',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
  {
    method: 'AuthenticatedLineageProducerExplicitNullFailsClosed',
    body_sha256: '2fdb5dff30ac75c56076fd0a2a8d9ff5e4cc267e7c2dee8f4d5029f9c3632c78',
    attributes: ['Fact'],
    expanded_native_cases: 1,
  },
];

export function verifyR3ApplicationContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalSha256,
    'exact R3 original triggers, current bodies and ownership limits',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.rows.length, 13);
  assert.equal(policy.cli_consumers.length, 10);
  assert.deepEqual(
    policy.cli_consumers.map((r) => ({
      method: r.original.method,
      body_sha256: r.original.body_sha256,
      attributes: r.original.attributes,
      expanded_native_cases: r.expanded_native_cases,
    })),
    originalCliBodies,
    'independent original CLI declarations and variants',
  );
  assert.equal(
    policy.cli_consumers.reduce((n, r) => n + r.expanded_native_cases, 0),
    15,
  );
  assert.equal(policy.cli_source.removed_methods, 10);
  assert.equal(policy.cli_source.removed_expanded_cases, 15);
  assert.equal(
    policy.cli_source.attributed_methods_before - policy.cli_source.attributed_methods_after,
    10,
  );
  assert.equal(policy.cli_source.retained_process_route, 'current-production-argv');
  for (const row of policy.cli_consumers) {
    assert.equal(row.original.source, policy.cli_source.path);
    assert.equal(row.retirement_input.source_sha256, policy.cli_source.retirement_input_sha256);
    assert.equal(row.original.body_sha256, row.retirement_input.body_sha256);
    assert.deepEqual(row.original.attributes, row.retirement_input.attributes);
    assert.equal(row.disposition, 'native-bootstrap-consumer-closed');
    assert.ok(
      row.original_triggers.length > 0 &&
        row.precise_dispositions.length > 1 &&
        row.primary_oracle_ids.length > 0,
    );
    for (const id of row.primary_oracle_ids) {
      const owners = policy.oracles.filter((o) => o.id === id);
      assert.equal(owners.length, 1);
      assert.ok(
        !owners[0].role.startsWith('supporting-') &&
          owners[0].role !== 'production-control-flow-only-not-injected-observations',
      );
    }
  }

  assert.equal(policy.removed_source.attributed_methods, 13);
  assert.deepEqual(
    policy.rows.map((r) => ({
      method: r.original.method,
      original_body_sha256: r.original.body_sha256,
      input_body_sha256: r.retirement_input.body_sha256,
      attributes: r.original.attributes,
    })),
    originalBodies,
    'independent R3 baseline/input bodies',
  );
  assert.deepEqual(policy.shared_members.map((m) => m.name).sort(), ['Identity', 'StateSecret']);
  assert.equal(policy.trusted_profile_consumers.length, 3);
  const consumer = policy.trusted_profile_consumers[0];
  assert.equal(consumer.original.method, 'PostCommitQualityFailureSurvivesTransportProofWrapper');
  assert.equal(
    consumer.original.body_sha256,
    '23575b5eec5e45bebbc8beae4d9f0e488f63a8174e7438ff48c38b0b68f707d4',
  );
  assert.equal(consumer.retirement_input.body_sha256, consumer.original.body_sha256);
  assert.equal(consumer.current_method, 'QualityFailureProjectionSurvivesTransportProofWrapper');
  assert.deepEqual(consumer.primary_oracle_ids, [
    'trusted-quality-projection',
    'current-required-observation',
    'commit',
    'direct',
    'current-file-lineage-receipt',
  ]);
  const inventory = policy.trusted_profile_consumers[1];
  assert.equal(inventory.original.method, 'VerifierOwnsExactlyTheDeterministicAndTrustedProfiles');
  assert.equal(
    inventory.original.body_sha256,
    '726c90a9d49a79fc5935995bfb6d831762c98c45009c2a6728f0e4ed028cd36c',
  );
  assert.equal(inventory.retirement_input.body_sha256, inventory.original.body_sha256);
  assert.equal(
    inventory.current_method,
    'FreshProcessProofContractDoesNotGrantExecutionCapabilities',
  );
  assert.deepEqual(inventory.primary_oracle_ids, ['current-proof-contract']);
  const boundary = policy.trusted_profile_consumers[2];
  assert.equal(boundary.original.method, 'LiveAgentVerifierAuthorizationDenialHasZeroActivation');
  assert.equal(
    boundary.original.body_sha256,
    'ffb1289894038c7d11e9e0cfcc1531cf3629c21b54ce5b41b595aab9c7262ea1',
  );
  assert.equal(boundary.retirement_input.body_sha256, boundary.original.body_sha256);
  assert.equal(boundary.current_method, 'AuthorizationDenialDoesNotReachProtectedBoundaries');
  assert.deepEqual(boundary.primary_oracle_ids, ['gate', 'current-authorization-boundary']);
  for (const row of policy.rows) {
    assert.ok(
      row.original_triggers.length > 0 &&
        row.precise_dispositions.length > 0 &&
        row.primary_oracle_ids.length > 0,
    );
    for (const id of row.primary_oracle_ids) {
      const owner = policy.oracles.filter((o) => o.id === id);
      assert.equal(owner.length, 1);
      assert.notEqual(owner[0].role, 'production-control-flow-only-not-injected-observations');
    }
  }
}
export function verifyR3ApplicationSources(repository, policy) {
  verifyR3ApplicationContract(policy);
  let absent = false;
  try {
    lstatSync(join(repository, policy.removed_source.path));
  } catch (error) {
    if (error.code !== 'ENOENT') throw error;
    absent = true;
  }
  assert.equal(absent, true, 'retired native R3 source remains absent');
  const current = read(repository, policy.current_source.path);
  assert.equal(
    sha(current),
    policy.current_source.sha256,
    'current transaction methods and shared fields',
  );
  for (const m of policy.shared_members) {
    const b = current.subarray(m.current_utf8_start, m.current_utf8_start + m.utf8_length);
    assert.equal(b.length, m.utf8_length);
    assert.equal(sha(b), m.body_sha256, 'original shared field retained');
  }
  for (const o of policy.oracles) {
    const bytes = read(repository, o.source);
    assert.equal(sha(bytes), o.source_sha256, o.id);
    const b = bytes.subarray(o.utf8_start, o.utf8_start + o.utf8_length);
    assert.ok(b.length > 0 && b.length === o.utf8_length);
    assert.equal(sha(b), o.body_sha256, o.id);
  }

  const cliSource = read(repository, policy.cli_source.path);
  assert.equal(
    sha(cliSource),
    policy.cli_source.current_sha256,
    'current CLI declarations and fixture route',
  );
  const cliText = cliSource.toString('utf8');
  for (const row of policy.cli_consumers)
    assert.ok(!cliText.includes(' ' + row.original.method + '('), 'closed old CLI consumer absent');
  assert.ok(
    !cliText.includes('historical-direct'),
    'retained CLI cases never activate the historical route',
  );
  assert.ok(
    cliText.includes('typeof(RuntimeApplication).Assembly.Location'),
    'retained CLI input/layout failures use production argv',
  );
  for (const consumer of policy.trusted_profile_consumers) {
    const trustedSource = read(repository, consumer.original.source).toString('utf8');
    assert.ok(
      !trustedSource.includes(' ' + consumer.original.method + '('),
      'old profile test retired',
    );
    assert.ok(
      !trustedSource.includes('new LiveAgentVerifierProfile('),
      'current tests do not activate old profile',
    );
    assert.ok(
      !trustedSource.includes('new ActivationProbeProfile(') &&
        !trustedSource.includes('class ActivationProbeProfile'),
      'old activation probe is not a current owner',
    );
  }
}
export function verifyR3ApplicationDispositions(repository) {
  const raw = read(repository, relative);
  assert.equal(sha(raw), rawSha256, 'frozen R3 disposition bytes');
  const policy = JSON.parse(raw);
  verifyR3ApplicationSources(repository, policy);
  return policy.rows.length;
}
