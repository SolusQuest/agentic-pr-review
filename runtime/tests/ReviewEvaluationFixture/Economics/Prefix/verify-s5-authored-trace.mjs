import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const policyPath =
  'runtime/tests/ReviewEvaluationFixture/Economics/Prefix/s5-authored-trace-dispositions.json';
const rawPolicySha256 = '5db523104e5dd9ed356ba774c1aaa37a919c1795a8f8d9bb125fb3ac7582cce5';
const testSource =
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs';
const boundarySource =
  'runtime/tests/ReviewEvaluationFixture/Economics/Prefix/PrefixObservation.cs';
const originalMethods = [
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'ActualLoopRestoreAndTypedCallbackAgreeWithIndependentLogicalSegmentContract',
    body_sha256: '4d3dd4cdb475c9592d4832c16eb28e7f61b4fc499298b01b9daee7f2c8ecebc0',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'ValidSuffixGrowthAndDynamicCallIdsPreserveFixedHistoricalPrefix',
    body_sha256: '5576906c4f56414007dac31e054c3c435e04854fcb361579a0ca78557e1597fd',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'SameBoundaryMutationsChangeRelevantActualSegments',
    body_sha256: 'ffd8cb21cdab1322aa5964c4961a359998d8472fd74915d499104265bd964bcd',
    attributes: [
      'Theory',
      'InlineData("policy")',
      'InlineData("tools")',
      'InlineData("tool-order")',
      'InlineData("old-content")',
      'InlineData("old-id")',
      'InlineData("role")',
      'InlineData("order")',
      'InlineData("reasoning")',
      'InlineData("reasoning-id-deadline")',
    ],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'ContinuationPlacementAcrossHistoricalAndCurrentAssistantsCannotHideMutation',
    body_sha256: '8cc64daa1a1cd999045ba6d206853f255048242406f0234cc40263d5d18dc1c1',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'InvalidContinuationPositionsAndAssociationsAreRejectedWithoutRawDiagnostics',
    body_sha256: '47d2334985b74ac17cd6bdf86f4b3984fe33cff7f69b9f39d09ad54f03f2097a',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'LogicalMetadataMutationIsObservableAndMalformedReplayIsNotCacheEvidence',
    body_sha256: '23a533c2b60b47ef66d3d317d7c521ddf51488175a692e4c01f83cff9781ace6',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'ComparisonDomainInvalidatesEqualBytesAndRecordsActualStablePlanIdentity',
    body_sha256: '10bd68593efa7df6ce73a6d4d90024d0f0c3ae90af7d25222cafc4276975d7cc',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'BoundsEmptyHistoryAndFailurePathsRetainOnlySafeEvidence',
    body_sha256: 'f9b957ebe0eac3c568638c6512951bbbfa3f9e1580e5d81535d71ad2dcbabd14',
    attributes: ['Fact'],
  },
  {
    source: 'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R6PrefixProjectionTests.cs',
    method: 'InvalidArgumentsOrUnadmittedResultsCannotSupplyPositiveSuffixEvidence',
    body_sha256: '284b0312dfeb50e21aa890fdf605b3b76bcf5b9f8d4e6f2479fe67ba7aa98a41',
    attributes: [
      'Theory',
      'InlineData(true, null, 0)',
      'InlineData(false, AgentFailureCodes.ToolIoFailed, 1)',
    ],
  },
];
const fixtureIdentitySha256 = '33a0b8b98d66bcc0dde76ca86b322cd72829072d62b17a6b81c72a3145c89708';
const fixtureBodySha256 = '6ef8f119b26339e81f1649281062accfb18388e1a84addb9b1a2ebffb64d07cf';
const factoryBodySha256 = '9e4c60a82f7a0a1a165859b2b052052bbc391d476e9897e9562a0fbab0e581d2';
const originalBoundarySha256 = 'eaf39e93aed7b9817f218bd814e17459e4005fc4621d6ab5ae6520b5e64c6348';
const unchangedSkeletonSha256 = '7d16f096be601905afe0d0d54975033dc6eff3a7f89182e89c0d174f2632ebed';
const genericSeedPrefixSha256 = '688dc975102af8e5252db06db8ddf5a0fcdf82f7e0f81b5d192d39359d306bca';
const oldFacadeGuard =
  '        Assert.Throws<PrefixObservationException>(() => PrefixBoundary.Restored(Trusted, AgentSessionRestoreResult.Failure("bad")));';
const newTraceGuards =
  '        // The old failed-RestoreResult facade is retired; these are authored trace input guards.\n        Assert.Throws<PrefixObservationException>(() => PrefixBoundary.AuthoredThreeMessageHistoryForTests(Trusted, run));\n        var (trace, _) = await Fixture();\n        foreach (var invalidTrace in new[] {\n            trace.RunRequest with { StablePlan = trace.RunRequest.StablePlan with { PriorSessionSha256 = null } },\n            trace.RunRequest with { StablePlan = trace.RunRequest.StablePlan with { PriorSessionSha256 = "not-a-hash" } },\n            trace.RunRequest with { InitialMessages = trace.RunRequest.InitialMessages[..^1] },\n            trace.RunRequest with { InitialMessages = [.. trace.RunRequest.InitialMessages.Take(2), User("wrong historical role"), .. trace.RunRequest.InitialMessages.Skip(3)] } })\n            Assert.Throws<PrefixObservationException>(() => PrefixBoundary.AuthoredThreeMessageHistoryForTests(Trusted, invalidTrace));';
const oldBoundaryComment =
  '// The caller supplies production admission results, never a favorable prefix length.\n// Raw run/SESSION objects are used only while deriving the fixed boundary.';
const newBoundaryComment =
  '// Logical tracing only: Restored consumes its existing admission result; the\n// finite authored fixture grants no state authority. No factory accepts a prefix length.';
const traceDeclaration =
  '\n    private sealed record AuthoredTrace(AgentRunRequest RunRequest, string TraceSha256);\n';
const oldFirstMethod =
  'ActualLoopRestoreAndTypedCallbackAgreeWithIndependentLogicalSegmentContract';
const newFirstMethod =
  'AuthoredTypedHistoryAndActualLoopCallbackAgreeWithIndependentLogicalSegmentContract';
const sha = (value) => createHash('sha256').update(value).digest('hex');
function read(repository, path) {
  const file = join(repository, path);
  const stat = lstatSync(file);
  assert.ok(stat.isFile() && !stat.isSymbolicLink() && stat.size > 0 && stat.size <= 1048576);
  return readFileSync(file);
}
function slice(bytes, record) {
  assert.ok(Number.isSafeInteger(record.utf8_start) && record.utf8_start >= 0);
  assert.ok(
    Number.isSafeInteger(record.utf8_length) &&
      record.utf8_length > 0 &&
      record.utf8_start + record.utf8_length <= bytes.length,
  );
  const body = bytes.subarray(record.utf8_start, record.utf8_start + record.utf8_length);
  assert.equal(sha(body), record.body_sha256);
  return new TextDecoder('utf-8', { fatal: true }).decode(body);
}
function once(text, part, reason = 'exact approved source fragment') {
  assert.equal(text.split(part).length, 2, reason);
}
function normalizeNames(text) {
  return text
    .replaceAll(newFirstMethod, oldFirstMethod)
    .replaceAll('trace.TraceSha256', 'restored.Artifact!.SessionSha256')
    .replace(/\btrace\b/gu, 'restored');
}

// Exported separately so source mutations re-signed in an authored policy still
// have to preserve every original generic predicate and the independent fixture.
export function verifyR6PrefixSourceCorrespondence(repository, policy) {
  assert.deepEqual(Object.keys(policy).sort(), [
    'accepted_state_authority',
    'current_sdk_responsibility',
    'current_sources',
    'expanded_cases',
    'factory',
    'fixture_helper',
    'fixture_identity',
    'fixture_identity_sha256',
    'generic_seed_prefix_sha256',
    'kind',
    'methods',
    'original_source_commit',
    'original_source_sha256',
    'owner_issue',
    'retired_private_setup',
    'retirement_input_boundary_sha256',
    'retirement_input_source_commit',
    'retirement_input_source_sha256',
    'rows',
    'sdk_context_import_qualified',
    'unchanged_test_source_skeleton_sha256',
  ]);
  assert.equal(policy.original_source_commit, 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2');
  assert.equal(policy.retirement_input_source_commit, 'ed5fd322cb56882747879b27979433f9ed2a59d9');
  assert.equal(
    policy.original_source_sha256,
    '433a9a0599f33ab20f291009286c41d43252f2ac380e81a3ba8addd9117c5cbc',
  );
  assert.equal(
    policy.retirement_input_source_sha256,
    '26e4da69a98caf6e3b67fc8a87203d1bad737d09996afd479d5f1d2caa16674f',
  );
  assert.equal(policy.retirement_input_boundary_sha256, originalBoundarySha256);
  assert.equal(policy.unchanged_test_source_skeleton_sha256, unchangedSkeletonSha256);
  assert.equal(policy.generic_seed_prefix_sha256, genericSeedPrefixSha256);
  assert.equal(policy.fixture_identity_sha256, fixtureIdentitySha256);
  assert.equal(policy.kind, 'apr-cr1-s5-r6-prefix-fixture-dispositions-v1');
  assert.equal(policy.owner_issue, 409);
  assert.equal(policy.sdk_context_import_qualified, false);
  assert.equal(policy.accepted_state_authority, false);
  assert.equal(sha(JSON.stringify(policy.fixture_identity)), fixtureIdentitySha256);
  assert.equal(policy.methods, 9);
  assert.equal(policy.expanded_cases, 18);
  assert.deepEqual(
    policy.rows.map((row) => row.original),
    originalMethods,
  );
  assert.deepEqual(
    policy.current_sources.map((row) => row.path),
    [testSource, boundarySource],
  );
  const tests = read(repository, testSource),
    boundary = read(repository, boundarySource);
  assert.equal(sha(tests), policy.current_sources[0].sha256);
  assert.equal(sha(boundary), policy.current_sources[1].sha256);
  const testText = new TextDecoder('utf-8', { fatal: true }).decode(tests);
  for (const retired of [
    'AgentSessionBuilder',
    'AgentSessionRestorer',
    'AgentSessionRestoreResult',
    'DeepSeekReasoningContinuationCodec',
  ])
    assert.ok(!testText.includes(retired), retired);
  for (const row of policy.rows) {
    assert.equal(row.current.source, testSource);
    assert.equal(
      row.current.method,
      row.original.method === oldFirstMethod ? newFirstMethod : row.original.method,
    );
    const count =
      row.original.attributes.filter((value) => value.startsWith('InlineData(')).length || 1;
    assert.equal(row.expanded_cases, count);
    let body = slice(tests, row.current);
    for (const attribute of row.original.attributes)
      assert.ok(body.includes('[' + attribute + ']'));
    if (row.original.method === 'BoundsEmptyHistoryAndFailurePathsRetainOnlySafeEvidence') {
      once(body, newTraceGuards, row.original.method);
      body = body.replace(newTraceGuards, oldFacadeGuard);
    }
    assert.equal(sha(normalizeNames(body)), row.original.body_sha256, row.original.method);
  }
  assert.equal(
    policy.rows.reduce((total, row) => total + row.expanded_cases, 0),
    18,
  );
  assert.equal(policy.fixture_helper.source, testSource);
  assert.equal(policy.fixture_helper.method, 'Fixture');
  const fixture = slice(tests, policy.fixture_helper);
  assert.equal(sha(fixture), fixtureBodySha256, 'fixed generic seed fixture body');
  const start = '        var trusted = Trusted;',
    end = '        Assert.True(outcome.CompletedSessionEligible);';
  once(fixture, start);
  once(fixture, end);
  const seed = fixture
    .slice(fixture.indexOf(start), fixture.indexOf(end) + end.length)
    .replace(/\bseed\b/gu, 'run');
  assert.equal(sha(seed), genericSeedPrefixSha256);
  let skeleton = testText;
  once(skeleton, fixture);
  skeleton = skeleton.replace(fixture, '');
  once(skeleton, traceDeclaration);
  skeleton = skeleton.replace(traceDeclaration, '');
  once(skeleton, newTraceGuards);
  skeleton = skeleton.replace(newTraceGuards, oldFacadeGuard);
  assert.equal(
    sha(normalizeNames(skeleton)),
    unchangedSkeletonSha256,
    'all generic helpers, callbacks, fields, bounds and privacy predicates remain',
  );
  assert.equal(policy.factory.source, boundarySource);
  assert.equal(policy.factory.method, 'AuthoredThreeMessageHistoryForTests');
  const factory = slice(boundary, policy.factory);
  assert.equal(sha(factory), factoryBodySha256, 'fixed authored factory body');
  let boundaryText = new TextDecoder('utf-8', { fatal: true }).decode(boundary);
  once(boundaryText, factory);
  boundaryText = boundaryText.replace(factory, '');
  once(boundaryText, newBoundaryComment);
  boundaryText = boundaryText.replace(newBoundaryComment, oldBoundaryComment);
  assert.equal(
    sha(boundaryText),
    originalBoundarySha256,
    'original Stable/AgentRequestBounds/Restored and every other boundary predicate remain',
  );
  return { methods: 9, expandedCases: 18, sdkContextImportQualified: false };
}
export function verifyR6PrefixFixtureDispositions(repository) {
  const raw = read(repository, policyPath);
  assert.equal(sha(raw), rawPolicySha256);
  return verifyR6PrefixSourceCorrespondence(repository, JSON.parse(raw));
}
