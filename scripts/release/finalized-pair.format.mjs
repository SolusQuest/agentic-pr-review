import {
  canonicalJson,
  sha256,
  MANAGED_LIMITS,
  validateReleaseVersion,
} from './build-payload.format.mjs';
import {
  CAPS,
  REPOSITORY,
  decimal,
  hex,
  parseCanonical,
  requireThat,
  validateProducer,
  validateQualificationProducer,
  validateStorage,
} from './candidate.mjs';
import { GH_VERSION, PREDICATE_TYPE, SIGNER } from './verify-provenance.mjs';

// P1's receipt parser remains the authority for original archive identity.
// This release-only projection is checked against its real producer in fixtures.
export function managedArchiveName(version) {
  return 'agentic-pr-review-' + validateReleaseVersion(version) + '-linux-x64-managed.tar.gz';
}

export const PAIR_LIMITS = Object.freeze({
  pair: 16 * 1024,
  request: 4096,
  receipt: 16 * 1024,
  expectedChecks: 32 * 1024,
  results: 64 * 1024,
  evidence: 4096,
  cases: 128,
  qualificationArtifact: 1024 * 1024,
});
export const ACTION_ROOT = '.github/actions/agentic-pr-review';
export const CHECK_MANIFEST_PATH = 'scripts/release/qualification-checks.json';
export const ACTION_ASSETS = Object.freeze(
  [
    ['action.yml', 64 * 1024],
    ['payload-map.json', CAPS.map],
    ['internal/action.yml', 64 * 1024],
    ['internal/prepare/action.yml', 64 * 1024],
    ['internal/dist/index.js', 8 * 1024 * 1024],
    ['internal/dist/sdk-upload.js', 8 * 1024 * 1024],
    ['internal/dist/prepare.js', 8 * 1024 * 1024],
    ['internal/dist/cleanup.js', 8 * 1024 * 1024],
  ].map(Object.freeze),
);
export const CHECK_GROUPS = Object.freeze([
  'original-archive',
  'composite-assets',
  'trusted-runtime-no-sdk',
  'managed-loading',
  'fresh-restored-review',
  'read-only-tools',
  'publication-state-ordering',
  'reset-stale-denied',
  'incomplete-capacity-cancel-failure',
  'outputs-accounting-parity',
  'external-confinement',
]);
export const QUALIFICATION_SCOPE = Object.freeze({
  id: 'r7-v2-managed-pair',
  profile: 'cr1-solus-internal4-dev',
  contextFormat: 2,
  contextCompatibility: 1,
  groups: CHECK_GROUPS,
});
export function exact(value, names, code = 'finalization_shape_invalid') {
  requireThat(
    value &&
      typeof value === 'object' &&
      !Array.isArray(value) &&
      Object.keys(value).sort().join('|') === [...names].sort().join('|'),
    code,
  );
}
function same(a, b) {
  return canonicalJson(a) === canonicalJson(b);
}
function positive(value, cap) {
  return Number.isSafeInteger(value) && value > 0 && value <= cap;
}
export function checkId(value) {
  return typeof value === 'string' && /^[a-z][a-z0-9-]{0,63}$/.test(value);
}
function digest(bytes, expected, cap) {
  requireThat(
    Buffer.isBuffer(bytes) &&
      bytes.length > 0 &&
      bytes.length <= cap &&
      hex(expected, 64) &&
      sha256(bytes) === expected,
    'finalization_digest_mismatch',
  );
}
export function parseFinalizationRequest(bytes) {
  const r = parseCanonical(bytes, PAIR_LIMITS.request);
  exact(r, [
    'candidateSha256',
    'workflowCommit',
    'actionCommit',
    'bindingPr',
    'payloadId',
    'payloadSha256',
    'metadataId',
    'metadataSha256',
    'provenanceId',
    'provenanceSha256',
  ]);
  requireThat(
    hex(r.workflowCommit, 40) && hex(r.actionCommit, 40) && r.workflowCommit !== r.actionCommit,
    'finalization_revision_invalid',
  );
  for (const key of ['candidateSha256', 'payloadSha256', 'metadataSha256', 'provenanceSha256'])
    requireThat(hex(r[key], 64), 'finalization_digest_invalid');
  for (const key of ['bindingPr', 'payloadId', 'metadataId', 'provenanceId'])
    requireThat(
      typeof r[key] === 'string' && decimal(r[key]) === r[key],
      'finalization_id_invalid',
    );
  requireThat(
    new Set([r.payloadId, r.metadataId, r.provenanceId]).size === 3,
    'finalization_artifact_alias',
  );
  return r;
}
export function validateRuntimePolicy(policy) {
  exact(policy, ['version', 'url', 'sha512', 'archiveBytes']);
  requireThat(
    typeof policy.version === 'string' &&
      /^10\.0\.(0|[1-9][0-9]{0,3})$/.test(policy.version) &&
      Number(policy.version.split('.')[2]) >= 9 &&
      policy.url ===
        `https://builds.dotnet.microsoft.com/dotnet/Runtime/${policy.version}/dotnet-runtime-${policy.version}-linux-x64.tar.gz` &&
      hex(policy.sha512, 128) &&
      positive(policy.archiveBytes, 64 * 1024 * 1024),
    'finalization_runtime_policy_invalid',
  );
  return policy;
}
export function validatePair(pair) {
  exact(pair, [
    'formatVersion',
    'kind',
    'repository',
    'qualification',
    'payload',
    'producer',
    'storage',
    'provenance',
    'action',
  ]);
  requireThat(
    pair.formatVersion === 1 &&
      pair.kind === 'r7-finalized-pair' &&
      pair.repository === REPOSITORY &&
      same(pair.qualification, { state: 'unqualified', ...QUALIFICATION_SCOPE }),
    'finalization_pair_invalid',
  );
  validateProducer(pair.producer);
  const p = pair.payload;
  exact(p, [
    'identity',
    'archiveName',
    'archiveSize',
    'archiveSha256',
    'candidateSha256',
    'proposedMapSha256',
    'inventorySha256',
    'buildInputsSha256',
  ]);
  exact(p.identity, [
    'releaseVersion',
    'platform',
    'sourceCommit',
    'sourceTree',
    'buildId',
    'informationalVersion',
  ]);
  const i = p.identity;
  validateReleaseVersion(i.releaseVersion);
  requireThat(
    i.platform === 'linux-x64' &&
      hex(i.sourceCommit, 40) &&
      hex(i.sourceTree, 40) &&
      hex(i.buildId, 64) &&
      i.informationalVersion === `${i.releaseVersion}+build.${i.buildId}` &&
      p.archiveName === managedArchiveName(i.releaseVersion) &&
      positive(p.archiveSize, MANAGED_LIMITS.archive),
    'finalization_payload_invalid',
  );
  for (const k of [
    'archiveSha256',
    'candidateSha256',
    'proposedMapSha256',
    'inventorySha256',
    'buildInputsSha256',
  ])
    requireThat(hex(p[k], 64), 'finalization_payload_invalid');
  exact(pair.storage, ['payload', 'metadata', 'provenance']);
  for (const role of ['payload', 'metadata', 'provenance'])
    validateStorage(pair.storage[role], pair.producer, role);
  requireThat(
    new Set(Object.values(pair.storage).map((x) => x.artifactId)).size === 3,
    'finalization_artifact_alias',
  );
  exact(pair.provenance, ['bundleSha256', 'signer', 'predicateType', 'verifier']);
  requireThat(
    hex(pair.provenance.bundleSha256, 64) &&
      pair.provenance.signer === SIGNER &&
      pair.provenance.predicateType === PREDICATE_TYPE &&
      pair.provenance.verifier === `gh-${GH_VERSION}`,
    'finalization_provenance_invalid',
  );
  const a = pair.action;
  exact(a, [
    'commit',
    'tree',
    'bindingPr',
    'assets',
    'assetsSha256',
    'runtime',
    'runtimePolicySha256',
  ]);
  requireThat(
    hex(a.commit, 40) &&
      hex(a.tree, 40) &&
      a.commit !== i.sourceCommit &&
      a.commit !== pair.producer.workflowCommit &&
      typeof a.bindingPr === 'string' &&
      decimal(a.bindingPr) === a.bindingPr &&
      Array.isArray(a.assets) &&
      a.assets.length === ACTION_ASSETS.length,
    'finalization_action_invalid',
  );
  a.assets.forEach((asset, n) => {
    exact(asset, ['path', 'mode', 'size', 'sha256', 'gitBlobSha']);
    requireThat(
      asset.path === ACTION_ASSETS[n][0] &&
        asset.mode === '100644' &&
        positive(asset.size, ACTION_ASSETS[n][1]) &&
        hex(asset.sha256, 64) &&
        hex(asset.gitBlobSha, 40),
      'finalization_action_invalid',
    );
  });
  requireThat(
    a.assets[1].sha256 === p.proposedMapSha256 &&
      sha256(canonicalJson(a.assets)) === a.assetsSha256,
    'finalization_asset_digest_mismatch',
  );
  exact(a.runtime, ['policy', 'sourceSha256']);
  validateRuntimePolicy(a.runtime.policy);
  requireThat(
    hex(a.runtime.sourceSha256, 64) && sha256(canonicalJson(a.runtime)) === a.runtimePolicySha256,
    'finalization_runtime_policy_invalid',
  );
  return pair;
}
export function parseFinalizedPair(bytes, expectedSha256) {
  digest(bytes, expectedSha256, PAIR_LIMITS.pair);
  return validatePair(parseCanonical(bytes, PAIR_LIMITS.pair));
}
export function qualificationBindings(pair) {
  validatePair(pair);
  return {
    sourceCommit: pair.payload.identity.sourceCommit,
    sourceTree: pair.payload.identity.sourceTree,
    workflowCommit: pair.producer.workflowCommit,
    actionCommit: pair.action.commit,
    archiveSha256: pair.payload.archiveSha256,
    candidateSha256: pair.payload.candidateSha256,
    mapSha256: pair.payload.proposedMapSha256,
    inventorySha256: pair.payload.inventorySha256,
    buildInputsSha256: pair.payload.buildInputsSha256,
    provenanceBundleSha256: pair.provenance.bundleSha256,
    assetsSha256: pair.action.assetsSha256,
    runtimePolicySha256: pair.action.runtimePolicySha256,
  };
}
export function parseExpectedChecks(bytes) {
  const manifest = parseCanonical(bytes, PAIR_LIMITS.expectedChecks);
  exact(manifest, ['formatVersion', 'kind', 'scope', 'checks']);
  requireThat(
    manifest.formatVersion === 1 &&
      manifest.kind === 'r7-qualification-checks' &&
      same(manifest.scope, QUALIFICATION_SCOPE) &&
      Array.isArray(manifest.checks) &&
      manifest.checks.length >= CHECK_GROUPS.length &&
      manifest.checks.length <= PAIR_LIMITS.cases,
    'qualification_checks_invalid',
  );
  manifest.checks.forEach((check, index) => {
    exact(check, ['id', 'group']);
    requireThat(
      checkId(check.id) &&
        CHECK_GROUPS.includes(check.group) &&
        (index === 0 || manifest.checks[index - 1].id < check.id),
      'qualification_checks_invalid',
    );
  });
  requireThat(
    CHECK_GROUPS.every((group) => manifest.checks.some((check) => check.group === group)),
    'qualification_scope_incomplete',
  );
  return manifest;
}

// Content inspection alone never yields operational qualification. The caller
// must separately authenticate Q/run/attempt and the exact retained artifact.
export function inspectQualificationContent({
  pairBytes,
  pairSha256,
  receiptBytes,
  receiptSha256,
  expectedChecksBytes,
  resultBytes,
  evidence,
}) {
  const pair = parseFinalizedPair(pairBytes, pairSha256);
  digest(receiptBytes, receiptSha256, PAIR_LIMITS.receipt);
  const receipt = parseCanonical(receiptBytes, PAIR_LIMITS.receipt);
  exact(receipt, [
    'formatVersion',
    'kind',
    'pairSha256',
    'bindings',
    'scope',
    'executionKind',
    'producer',
    'expectedChecksSha256',
    'caseResultsSha256',
    'checkCount',
    'result',
    'refusalReason',
  ]);
  requireThat(
    receipt.formatVersion === 1 &&
      receipt.kind === 'r7-qualification-receipt' &&
      receipt.pairSha256 === pairSha256 &&
      same(receipt.bindings, qualificationBindings(pair)) &&
      same(receipt.scope, QUALIFICATION_SCOPE) &&
      ['synthetic', 'original-candidate'].includes(receipt.executionKind),
    'qualification_receipt_identity_drift',
  );
  validateQualificationProducer(receipt.producer);
  const expected = parseExpectedChecks(expectedChecksBytes);
  requireThat(
    sha256(expectedChecksBytes) === receipt.expectedChecksSha256 &&
      receipt.checkCount === expected.checks.length,
    'qualification_expected_checks_drift',
  );
  digest(resultBytes, receipt.caseResultsSha256, PAIR_LIMITS.results);
  const results = parseCanonical(resultBytes, PAIR_LIMITS.results);
  exact(results, [
    'formatVersion',
    'kind',
    'pairSha256',
    'scope',
    'expectedChecksSha256',
    'checks',
  ]);
  requireThat(
    results.formatVersion === 1 &&
      results.kind === 'r7-qualification-case-results' &&
      results.pairSha256 === pairSha256 &&
      same(results.scope, QUALIFICATION_SCOPE) &&
      results.expectedChecksSha256 === receipt.expectedChecksSha256 &&
      Array.isArray(results.checks) &&
      results.checks.length === expected.checks.length &&
      evidence instanceof Map,
    'qualification_results_invalid',
  );
  let performed = 0;
  results.checks.forEach((result, index) => {
    exact(result, ['id', 'group', 'status', 'evidenceSha256']);
    const check = expected.checks[index];
    requireThat(
      result.id === check.id &&
        result.group === check.group &&
        ['pass', 'refused', 'not-run'].includes(result.status),
      'qualification_case_coverage_invalid',
    );
    if (result.status === 'not-run') {
      requireThat(
        result.evidenceSha256 === null && !evidence.has(result.id),
        'qualification_evidence_invalid',
      );
      return;
    }
    const bytes = evidence.get(result.id);
    digest(bytes, result.evidenceSha256, PAIR_LIMITS.evidence);
    const record = parseCanonical(bytes, PAIR_LIMITS.evidence);
    exact(record, [
      'formatVersion',
      'kind',
      'checkId',
      'group',
      'pairSha256',
      'outcome',
      'observationSha256',
    ]);
    requireThat(
      record.formatVersion === 1 &&
        record.kind === 'r7-qualification-case-evidence' &&
        record.checkId === result.id &&
        record.group === result.group &&
        record.pairSha256 === pairSha256 &&
        record.outcome === result.status &&
        hex(record.observationSha256, 64),
      'qualification_evidence_invalid',
    );
    performed++;
  });
  requireThat(evidence.size === performed, 'qualification_evidence_inventory_invalid');
  const passed = results.checks.every((check) => check.status === 'pass');
  const reasons = [
    'check-refused',
    'not-run',
    'cancelled',
    'capacity-refused',
    'identity-refused',
    'confinement-refused',
    'runtime-refused',
  ];
  requireThat(
    (receipt.result === 'pass' && passed && receipt.refusalReason === null) ||
      (receipt.result === 'refusal' && !passed && reasons.includes(receipt.refusalReason)),
    'qualification_outcome_invalid',
  );
  return { contentVerified: true, operationallyQualified: false, receipt, pair };
}
