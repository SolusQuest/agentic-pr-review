// Offline preparation only. No dispatcher, credential reader or remote verifier.
import { canonicalJson, sha256 } from '../release/build-payload.format.mjs';
import { hex, parseCanonical, timestamp } from '../release/candidate.mjs';
import {
  exact,
  parseFinalizedPair,
  inspectQualificationArchive,
  inspectQualificationContent,
} from '../release/finalized-pair.format.mjs';

export const LIMITS = Object.freeze({ plan: 32768, observation: 32768, runs: 16 });
export const SCENARIOS = Object.freeze([
  'bootstrap',
  'continuation',
  'reset',
  'stale-head',
  'incomplete',
  'failure',
]);
export function requireValue(ok, code) {
  if (!ok) throw new Error('adoption_' + code);
}
export function shape(value, keys) {
  exact(value, keys, 'adoption_shape_invalid');
}
export function integer(value, minimum, maximum) {
  requireValue(
    Number.isSafeInteger(value) && value >= minimum && value <= maximum,
    'integer_invalid',
  );
}
function digest(value) {
  requireValue(hex(value, 64), 'digest_invalid');
}
function commit(value) {
  requireValue(hex(value, 40), 'commit_invalid');
}
function id(value) {
  requireValue(typeof value === 'string' && /^[1-9][0-9]{0,18}$/.test(value), 'id_invalid');
}
function reference(value) {
  requireValue(
    typeof value === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.:@/-]{0,127}$/.test(value),
    'reference_invalid',
  );
}
export function parsePlan(bytes, approvedSha256, inputs, now = Date.now()) {
  digest(approvedSha256);
  requireValue(sha256(bytes) === approvedSha256, 'approval_digest_mismatch');
  const p = parseCanonical(bytes, LIMITS.plan);
  shape(p, [
    'formatVersion',
    'kind',
    'mode',
    'candidate',
    'target',
    'content',
    'credentials',
    'invocation',
    'campaign',
    'authority',
    'runs',
  ]);
  requireValue(
    p.formatVersion === 1 &&
      p.kind === 'r7-adoption-preparation' &&
      ['synthetic', 'operational-inputs'].includes(p.mode),
    'plan_invalid',
  );
  const c = p.candidate;
  shape(c, [
    'pairSha256',
    'qualificationSha256',
    'receiptSha256',
    'qualifierCommit',
    'qualificationArtifactId',
    'publicationReleaseId',
    'publicationRecordSha256',
    'actionTag',
    'actionCommit',
  ]);
  for (const key of [
    'pairSha256',
    'qualificationSha256',
    'receiptSha256',
    'publicationRecordSha256',
  ])
    digest(c[key]);
  commit(c.qualifierCommit);
  commit(c.actionCommit);
  id(c.qualificationArtifactId);
  id(c.publicationReleaseId);
  const pair = parseFinalizedPair(inputs.pairBytes, c.pairSha256);
  const files = inspectQualificationArchive(inputs.qualificationBytes, c.qualificationSha256);
  const checked = inspectQualificationContent({
    pairBytes: inputs.pairBytes,
    pairSha256: c.pairSha256,
    receiptSha256: c.receiptSha256,
    expectedChecksBytes: inputs.expectedChecksBytes,
    ...files,
  });
  requireValue(
    checked.receipt.result === 'pass' &&
      checked.receipt.producer.workflowCommit === c.qualifierCommit &&
      pair.action.commit === c.actionCommit &&
      c.actionTag === pair.payload.identity.releaseVersion,
    'candidate_identity_mismatch',
  );
  requireValue(
    p.mode === 'synthetic' || checked.receipt.executionKind === 'original-candidate',
    'synthetic_qualification',
  );
  const t = p.target;
  shape(t, [
    'repository',
    'repositoryId',
    'pr',
    'head',
    'workflowCommit',
    'workflowPath',
    'template',
    'templateSha256',
    'configSha256',
    'instructionsSha256',
  ]);
  requireValue(
    typeof t.repository === 'string' &&
      /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/.test(t.repository) &&
      t.repository.length <= 200,
    'target_invalid',
  );
  id(t.repositoryId);
  id(t.pr);
  commit(t.head);
  commit(t.workflowCommit);
  requireValue(
    t.workflowPath === '.github/workflows/agentic-pr-review.yml' &&
      ['manual', 'automatic'].includes(t.template),
    'route_invalid',
  );
  for (const key of ['templateSha256', 'configSha256', 'instructionsSha256']) digest(t[key]);
  requireValue(
    inputs.templateBytes && sha256(inputs.templateBytes) === t.templateSha256,
    'template_digest_mismatch',
  );
  // The caller supplies the repository-owned exact template, not user-authored YAML.
  const expectedTemplate = inputs.templates?.[t.template];
  requireValue(
    Buffer.isBuffer(expectedTemplate) &&
      expectedTemplate
        .toString('utf8')
        .replaceAll('__ACTION_SOURCE_SHA__', c.actionCommit)
        .replaceAll('\r\n', '\n') ===
        inputs.templateBytes.toString('utf8').replaceAll('\r\n', '\n'),
    'template_invalid',
  );
  requireValue(p.mode === 'synthetic' || t.template === 'manual', 'automatic_campaign_not_bounded');
  shape(p.content, [
    'approvalRef',
    'manifestSha256',
    'head',
    'source',
    'diff',
    'instructions',
    'tools',
    'continuation',
  ]);
  reference(p.content.approvalRef);
  digest(p.content.manifestSha256);
  requireValue(
    p.content.head === t.head &&
      ['source', 'diff', 'instructions', 'tools', 'continuation'].every(
        (k) => p.content[k] === true,
      ),
    'content_scope_missing',
  );
  shape(p.credentials, [
    'owner',
    'environmentOwner',
    'githubTokenRef',
    'providerKeyRef',
    'stateKeyRef',
    'previousStateKeyRef',
  ]);
  for (const key of Object.keys(p.credentials)) {
    if (key === 'previousStateKeyRef' && p.credentials[key] === null) continue;
    reference(p.credentials[key]);
  }
  shape(p.invocation, [
    'profile',
    'contextFormat',
    'compatibility',
    'maxModelCalls',
    'maxProviderAttempts',
    'maxWorkUnits',
    'maxToolInvocations',
    'maxToolsPerBatch',
    'timeoutSeconds',
    'maxUncachedInputTokens',
    'maxCachedInputTokens',
    'maxTotalInputTokens',
    'maxOutputTokens',
  ]);
  const v = p.invocation;
  requireValue(
    v.profile === 'cr1-solus-internal4-dev' && v.contextFormat === 2 && v.compatibility === 1,
    'profile_invalid',
  );
  for (const key of ['maxModelCalls', 'maxProviderAttempts', 'maxWorkUnits', 'maxToolInvocations'])
    integer(v[key], 1, 6);
  integer(v.maxToolsPerBatch, 1, 3);
  integer(v.timeoutSeconds, 1, 120);
  for (const key of ['maxUncachedInputTokens', 'maxCachedInputTokens', 'maxTotalInputTokens'])
    integer(v[key], 1, 65536);
  integer(v.maxOutputTokens, 1, 8192);
  // Only five review fields are configurable. Match the actual Solus request,
  // rather than accepting an approval ceiling H1 cannot independently enforce.
  requireValue(
    v.maxProviderAttempts === v.maxModelCalls &&
      v.maxWorkUnits === v.maxModelCalls &&
      v.maxToolInvocations === 6 &&
      v.maxToolsPerBatch === 3 &&
      v.maxTotalInputTokens === Math.min(65536, v.maxUncachedInputTokens + v.maxCachedInputTokens),
    'unsupported_effective_limits',
  );
  shape(p.campaign, [
    'maxRuns',
    'maxModelCalls',
    'maxProviderAttempts',
    'wallSeconds',
    'currency',
    'costCeiling',
    'unknownSpend',
  ]);
  const b = p.campaign;
  integer(b.maxRuns, 1, LIMITS.runs);
  integer(b.maxModelCalls, 1, LIMITS.runs * 6);
  integer(b.maxProviderAttempts, 1, LIMITS.runs * 6);
  integer(b.wallSeconds, 1, 86400);
  requireValue(
    b.currency === 'USD' &&
      typeof b.costCeiling === 'string' &&
      /^(?:0|[1-9][0-9]{0,5})\.[0-9]{6}$/.test(b.costCeiling) &&
      b.costCeiling !== '0.000000' &&
      b.unknownSpend === 'stop',
    'cost_authority_missing',
  );
  shape(p.authority, [
    'approvalRef',
    'approvedBy',
    'validUntil',
    'stopOwner',
    'cleanupOwner',
    'stop',
    'cleanup',
  ]);
  for (const key of ['approvalRef', 'approvedBy', 'stopOwner', 'cleanupOwner'])
    reference(p.authority[key]);
  requireValue(
    timestamp(p.authority.validUntil) > now && timestamp(p.authority.validUntil) - now <= 86400000,
    'approval_expired',
  );
  requireValue(
    p.authority.stop === 'disable-and-settle' &&
      p.authority.cleanup === 'scoped-host-reconciliation',
    'cleanup_authority_missing',
  );
  requireValue(
    Array.isArray(p.runs) && p.runs.length > 0 && p.runs.length <= b.maxRuns,
    'runs_invalid',
  );
  const names = new Set();
  for (const run of p.runs) {
    shape(run, ['scenario', 'stateMode', 'basis']);
    requireValue(SCENARIOS.includes(run.scenario) && !names.has(run.scenario), 'scenario_invalid');
    names.add(run.scenario);
    requireValue(
      run.stateMode === (run.scenario === 'reset' ? 'reset' : 'auto') &&
        ['synthetic', 'existing-deterministic-proof', 'separately-authorized-live'].includes(
          run.basis,
        ),
      'scenario_authority_invalid',
    );
    requireValue(
      p.mode !== 'synthetic' || run.basis !== 'separately-authorized-live',
      'synthetic_live_basis',
    );
    requireValue(t.template !== 'automatic' || run.stateMode === 'auto', 'reset_requires_manual');
  }
  requireValue(
    p.runs.length * v.maxModelCalls <= b.maxModelCalls &&
      p.runs.length * v.maxProviderAttempts <= b.maxProviderAttempts &&
      p.runs.length * v.timeoutSeconds <= b.wallSeconds,
    'campaign_allocation_insufficient',
  );
  return {
    plan: p,
    pair,
    receipt: checked.receipt,
    contentVerified: true,
    operationallyQualified: false,
    realExecutionAllowed: false,
  };
}
export function preparationSummary(prepared) {
  return {
    kind: 'r7-adoption-preparation-result',
    mode: prepared.plan.mode,
    inputContentVerified: true,
    releaseAuthenticated: false,
    operationallyQualified: false,
    realExecutionAllowed: false,
    scenarios: prepared.plan.runs.map((r) => r.scenario),
    requiredExternalChecks: [
      'published-original-release',
      'authenticated-p3-v2-originals',
      'current-target-and-content-approval',
      'credential-environment-ownership',
      'finite-budget-and-stop-authority',
    ],
  };
}
export { canonicalJson, sha256 };
