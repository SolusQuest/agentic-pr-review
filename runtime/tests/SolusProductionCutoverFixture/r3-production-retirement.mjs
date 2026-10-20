import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const relative = 'runtime/tests/SolusProductionCutoverFixture/s5-r3-production-retirement.json';
const rawPolicySha256 = 'bac815f3c652c49bd545353e5fa11a508bce1155a80a70f37fab1045bfa413f1';
const canonicalPolicySha256 = '38d62dafc531194b830d69c902e791598d99d219c47e9d4d1f213590670b831a';
const affectedMethods = [
  'LiveAgentVerifierProductionRouteAndProviderCompositionAreExact',
  'LiveAgentVerifierProductionRouteOracleRejectsRestorationMutations',
  'R3LiveApplicationCallsOnlyTheRestoreStateOperation',
  'LiveAgentHostCommitHasAnExactStateMutationAllowlist',
  'R3CapabilityScannersCheckAsyncAndClosedCapabilityFamilies',
  'LiveHandoffTypesHaveNoPublicSerializationSurface',
  'FreshProcessProductionRootInventoryIsExact',
  'DeterministicTransportStoresNoCredential',
  'FreshProcessExactAllowlistsRejectPrivilegedAdditions',
  'LiveAgentVerifierCannotRecomposeOrUseTheSingleShotPath',
];
const unaffectedMethods = [
  'LiveAgentVerifierRetiredSingleShotDefinitionsAreAbsent',
  'R3LiveApplicationDoesNotReferenceRetiredOrBroadCapabilities',
  'RuntimeEntrypointsDoNotReachDirectlyIntoR3LiveApplication',
  'ProductionEnvironmentCallsHaveAnExactTemporaryAllowlist',
  'FreshProcessTypesHaveOnlyClosedCapabilities',
  'LiveAgentVerifierFreshProcessCommandHasOneExternalRuntimeDispatch',
  'FreshProcessCommandHasOnePreauthorizationRead',
  'LiveAgentVerifierFreshProcessFamilyHasNoOtherRuntimeReverseDependencies',
  'FreshProcessScannerTraversesHiddenCapabilityShapes',
  'LiveAgentVerifierOwnsOnlyTheApprovedTypedScriptSeam',
  'LiveAgentVerifierStoresNoCredentialOrSecret',
  'LiveAgentVerifierGoldenIsStablePublicEvidence',
  'LiveAgentVerifierNegativeMatrixIsExact',
  'LiveAgentVerifierNegativeMatrixRowsHaveIntegratedScenarios',
  'LiveAgentVerifierTypedOracleBindsTheCompleteToolContracts',
  'LiveAgentVerifierAggregateRejectsMissingOrFabricatedReceipts',
  'LiveAgentVerifierBuildPairRejectsSubstitutedMembers',
  'LiveAgentVerifierArchitectureRejectsMethodReferenceMutations',
  'LiveAgentVerifierArchitectureCountsInheritedImplementations',
  'LiveAgentVerifierContinuationSeedIsDistinctAndRuntimeRandom',
  'ContinuationOnlyReviewAdmissionIsFailClosed',
  'LiveAgentVerifierTransportProofIsFailClosed',
  'LiveAgentVerifierAcceptedIdentityMutationsFailClosed',
  'LiveAgentVerifierReplacementPublicationProofFailsClosed',
  'ActualShellReceiptRelocationPreservesPrivateObserverAdmissionAndRejectsExistingPublicParents',
  'LiveAgentVerifierMaterializedPhaseCreatesPrivateObserverParent',
  'ControlledTypedObserverRejectsCoordinatedReceiptAndTraceForgeries',
];
const replayAttributes = [
  ['Fact'],
  [
    'Theory',
    'InlineData("first_reasoning")',
    'InlineData("first_call_id")',
    'InlineData("first_arguments")',
    'InlineData("first_result_id")',
    'InlineData("finish_reasoning")',
    'InlineData("finish_call_id")',
    'InlineData("finish_arguments")',
    'InlineData("finish_result_id")',
    'InlineData("historical_user_fact")',
    'InlineData("fresh_user_fact")',
    'InlineData("fresh_public_fact")',
    'InlineData("unapproved_reasoning")',
  ],
];
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
function read(repository, path) {
  assert.ok(
    typeof path === 'string' &&
      (path.startsWith('runtime/') || path === 'src/residual-reference-allowlist.ts') &&
      !path.includes('..'),
  );
  const stat = lstatSync(join(repository, path));
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= 1048576);
  return readFileSync(join(repository, path));
}
function load(repository) {
  const bytes = read(repository, relative);
  assert.equal(sha(bytes), rawPolicySha256, 'exact R3 production retirement policy bytes');
  return JSON.parse(bytes);
}
export function verifyR3ProductionContract(policy) {
  assert.equal(
    sha(JSON.stringify(policy)),
    canonicalPolicySha256,
    'exact R3 production declarations and obligations',
  );
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.architecture.attributed_methods, 37);
  assert.deepEqual(
    policy.architecture.affected.map((row) => row.before.method),
    affectedMethods,
  );
  assert.deepEqual(
    policy.architecture.retained.map((row) => row.input.method),
    unaffectedMethods,
  );
  for (const row of policy.architecture.retained) {
    assert.equal(row.current.body_sha256, row.input.body_sha256);
    assert.deepEqual(row.current.attributes, row.input.attributes);
  }
  for (const row of policy.architecture.affected) {
    assert.equal(row.current, undefined);
    assert.equal(row.before.method, row.after.method);
    assert.deepEqual(row.before.attributes, row.after.attributes);
    assert.ok(row.disposition.length > 80);
  }
  assert.equal(policy.durable_preservation.count, 31);
  assert.equal(policy.durable_preservation.current.length, 31);
  policy.durable_preservation.input.forEach((row, index) => {
    const current = policy.durable_preservation.current[index];
    assert.equal(current.method, row.method);
    assert.equal(current.body_sha256, row.body_sha256, 'exact shared durable method');
  });
  assert.equal(policy.durable_preservation.service_current.method, 'Service');
  assert.equal(policy.prepared_component_methods.length, 16);
  for (const row of policy.prepared_component_methods) {
    assert.deepEqual(row.input.attributes, row.current.attributes);
    assert.deepEqual(
      row.input.assertions,
      row.current.assertions,
      'all existing prepared-engine assertions',
    );
    if (row.input.method !== 'CurrentPreparedFactoryFailureZerosCallerPlaintext')
      assert.equal(row.input.body_sha256, row.current.body_sha256);
  }
  assert.equal(policy.authored_replay.expanded_cases, 13);
  assert.deepEqual(
    policy.authored_replay.methods.map((row) => row.input.attributes),
    replayAttributes,
  );
  assert.equal(policy.authored_replay.predicate_current.length, 5);
  for (const row of policy.authored_replay.methods) {
    assert.deepEqual(row.input.attributes, row.current.attributes);
    assert.deepEqual(
      row.input.assertions.map((text) =>
        text.replaceAll(
          'LiveAgentFreshProcessDeterministicChat',
          'AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution.AuthoredReplayShapePredicate',
        ),
      ),
      row.current.assertions,
    );
  }
  assert.equal(policy.new_guard_methods.length, 2);
  assert.equal(policy.actual_admission_identity.requires_actual_archive_execution, true);
  assert.equal(policy.actual_admission_identity.method.method, 'VerifyCurrentAdmissionIdentity');
  assert.equal(policy.retired_residual_rule.id, 'RR-054');
  assert.equal(policy.retired_residual_rule.path, 'src/residual-reference-allowlist.ts');
}
export function verifyR3ProductionSources(repository, policy) {
  verifyR3ProductionContract(policy);
  const residual = read(repository, policy.retired_residual_rule.path);
  assert.equal(
    sha(residual),
    policy.retired_residual_rule.current_source_sha256,
    'exact residual-rule retirement',
  );
  assert.equal(residual.toString('utf8').includes("'RR-054'"), false);
  for (const row of policy.removed_sources)
    assert.equal(
      existsSync(join(repository, row.path)),
      false,
      'retired native execution source absent',
    );
  for (const row of [...policy.current_sources, ...policy.retained_current_sources])
    assert.equal(
      sha(read(repository, row.path)),
      row.sha256,
      'exact current or retained R3 source',
    );
  const slices = [
    ...policy.durable_preservation.current,
    policy.durable_preservation.service_current,
    ...policy.architecture.affected.map((row) => row.after),
    ...policy.architecture.retained.map((row) => row.current),
    ...policy.prepared_component_methods.map((row) => row.current),
    ...policy.authored_replay.methods.map((row) => row.current),
    ...policy.authored_replay.predicate_current,
    ...policy.new_guard_methods,
    policy.actual_admission_identity.method,
  ];
  for (const row of slices) {
    const bytes = read(repository, row.source);
    const body = bytes.subarray(row.utf8_start, row.utf8_start + row.utf8_length);
    assert.equal(body.length, row.utf8_length);
    assert.equal(sha(body), row.body_sha256, 'exact actual current method body');
  }
  assert.equal(
    sha(read(repository, 'runtime/tests/SolusEvaluationFixture/native-assertions.json')),
    policy.original_v1_sha256,
  );
  assert.equal(
    sha(
      read(repository, 'runtime/tests/SolusEvaluationFixture/s5-original-method-identities.json'),
    ),
    policy.original_identities_sha256,
  );
  assert.equal(
    sha(read(repository, 'runtime/tests/SolusEvaluationFixture/s5-format-retirements.json')),
    policy.original_five_format_sha256,
  );
}
export function verifyR3ProductionRetirement(repository, supplied) {
  const policy = supplied ?? load(repository);
  verifyR3ProductionSources(repository, policy);
  return {
    durableMethods: 31,
    architectureAffected: 10,
    architectureUnchanged: 27,
    authoredReplayCases: 13,
    sdkContextImportQualified: false,
  };
}
