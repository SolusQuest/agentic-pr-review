// Public evidence is reconstructed from closed scalar observations; no raw text sink.
import { parseCanonical } from '../release/candidate.mjs';
import { shape, requireValue, integer, LIMITS } from './protocol.mjs';

export const OUTPUTS = Object.freeze([
  'status',
  'termination-reason',
  'model-calls',
  'provider-attempts',
  'provider-retries',
  'provider-failed-attempts',
  'provider-unknown-usage-attempts',
  'provider-unknown-cache-partition-attempts',
  'attempt-accounting-completeness',
  'usage-completeness',
  'input-tokens',
  'input-cache-hit-tokens',
  'input-cache-miss-tokens',
  'output-tokens',
]);
const statuses = new Set([
  'reviewed',
  'reviewed_with_inline_warnings',
  'skipped_untrusted_event',
  'skipped_fork',
  'skipped_draft',
  'skipped_closed',
  'configuration_invalid',
  'authorization_failed',
  'credentials_missing',
  'snapshot_incomplete',
  'provider_failed',
  'agent_result_invalid',
  'stale_head',
  'state_conflict',
  'sticky_publication_failed',
  'outcome_ambiguous',
  'cancelled',
  'internal_failure',
  'failed',
]);
const terminations = new Set([
  'review_completed',
  'not_started',
  'provider_failure',
  'cancelled',
  'deadline_exceeded',
  'model_limit',
  'tool_limit',
  'token_limit',
  'request_limit',
  'response_limit',
  'context_limit',
  'resource_limit',
  'invalid_result',
  'host_failure',
]);
const completeness = new Set(['complete', 'partial', 'unavailable']);
const dispositions = ['confirmed', 'absent', 'unknown'];
function outputs(value) {
  requireValue(
    value &&
      typeof value === 'object' &&
      !Array.isArray(value) &&
      Object.keys(value).every((k) => OUTPUTS.includes(k)),
    'outputs_invalid',
  );
  return Object.fromEntries(
    OUTPUTS.map((key) => {
      const raw = value[key];
      const v = raw === undefined || raw === null || raw === '' ? null : raw;
      if (v !== null) {
        requireValue(typeof v === 'string', 'output_type_invalid');
        if (key === 'status') requireValue(statuses.has(v), 'status_invalid');
        else if (key === 'termination-reason')
          requireValue(terminations.has(v), 'termination_invalid');
        else if (key.endsWith('completeness'))
          requireValue(completeness.has(v), 'completeness_invalid');
        else
          requireValue(
            /^(0|[1-9][0-9]{0,18})$/.test(v) && BigInt(v) <= 9223372036854775807n,
            'counter_invalid',
          );
      }
      return [key, v];
    }),
  );
}
// These are checks on known operands from the current Host accounting contract.
// Missing observations stay null: no reconstruction, zero filling or re-pricing.
function consistent(values) {
  const n = (key) => (values[key] === null ? null : BigInt(values[key]));
  const calls = n('model-calls'),
    sends = n('provider-attempts'),
    retries = n('provider-retries');
  const unknown = n('provider-unknown-usage-attempts'),
    cacheUnknown = n('provider-unknown-cache-partition-attempts');
  const input = n('input-tokens'),
    hit = n('input-cache-hit-tokens'),
    miss = n('input-cache-miss-tokens');
  const tokenKeys = [
    'input-tokens',
    'input-cache-hit-tokens',
    'input-cache-miss-tokens',
    'output-tokens',
  ];
  const numericKeys = OUTPUTS.filter(
    (k) =>
      ![
        'status',
        'termination-reason',
        'attempt-accounting-completeness',
        'usage-completeness',
      ].includes(k),
  );
  const valid = (ok) => requireValue(ok, 'accounting_inconsistent');
  // The admitted profile has one physical attempt per logical call, no retries.
  valid(retries === null || retries === 0n);
  if (calls !== null && sends !== null) valid(sends <= calls);
  if (sends !== null) {
    for (const key of [
      'provider-failed-attempts',
      'provider-unknown-usage-attempts',
      'provider-unknown-cache-partition-attempts',
    ]) {
      const value = n(key);
      if (value !== null) valid(value <= sends);
    }
    if (sends === 0n) for (const key of tokenKeys) valid(n(key) === null || n(key) === 0n);
  }
  if (calls === 0n) for (const key of numericKeys) valid(n(key) === null || n(key) === 0n);
  if (input !== null) {
    if (hit !== null) valid(hit <= input);
    if (miss !== null) valid(miss <= input);
    if (hit !== null && miss !== null) valid(hit + miss <= input);
  }
  const attempts = values['attempt-accounting-completeness'],
    usage = values['usage-completeness'];
  if (values['termination-reason'] === 'not_started') {
    for (const key of numericKeys) valid(n(key) === null || n(key) === 0n);
    valid(attempts === null || attempts === 'complete');
    valid(usage === null || usage === 'complete');
  }
  if (
    values['termination-reason'] === 'review_completed' &&
    attempts === 'complete' &&
    sends !== null &&
    n('provider-failed-attempts') !== null
  )
    valid(sends > n('provider-failed-attempts'));

  if (attempts === 'unavailable')
    for (const key of [
      'provider-attempts',
      'provider-failed-attempts',
      'provider-unknown-usage-attempts',
      'provider-unknown-cache-partition-attempts',
    ])
      valid(n(key) === null || n(key) === 0n);
  if (usage === 'unavailable') for (const key of tokenKeys) valid(n(key) === null || n(key) === 0n);
  if (usage === 'complete') {
    valid(attempts === null || attempts === 'complete');
    valid(unknown === null || unknown === 0n);
    valid(cacheUnknown === null || cacheUnknown === 0n);
    if (input !== null && hit !== null && miss !== null) valid(hit + miss === input);
  }
  if (
    attempts !== null &&
    unknown !== null &&
    cacheUnknown !== null &&
    tokenKeys.every((k) => n(k) !== null) &&
    usage !== null
  ) {
    valid(
      (usage === 'complete') === (attempts === 'complete' && unknown === 0n && cacheUnknown === 0n),
    );
  }
  if (attempts === 'complete' && sends === 0n && usage !== null) valid(usage === 'complete');
}
export function projectEvidence(bytes, prepared) {
  const data = parseCanonical(bytes, LIMITS.observation);
  shape(data, ['kind', 'mode', 'observations']);
  requireValue(
    data.kind === 'r7-adoption-observations' && data.mode === prepared.plan.mode,
    'observation_mode_invalid',
  );
  requireValue(
    Array.isArray(data.observations) && data.observations.length <= prepared.plan.runs.length,
    'observations_invalid',
  );
  const seen = new Set();
  let calls = 0n,
    attempts = 0n,
    elapsed = 0;
  const cases = data.observations.map((o) => {
    shape(o, [
      'scenario',
      'workflow',
      'step',
      'outputs',
      'state',
      'acceptance',
      'publication',
      'processClosed',
      'cleanup',
      'elapsedSeconds',
      'billing',
      'head',
      'workflowCommit',
      'runId',
      'runAttempt',
    ]);
    requireValue(
      o.head === prepared.plan.target.head &&
        o.workflowCommit === prepared.plan.target.workflowCommit &&
        typeof o.runId === 'string' &&
        /^[1-9][0-9]{0,18}$/.test(o.runId) &&
        typeof o.runAttempt === 'string' &&
        /^[1-9][0-9]{0,5}$/.test(o.runAttempt),
      'observation_identity_mismatch',
    );
    const run = prepared.plan.runs.find((r) => r.scenario === o.scenario);
    requireValue(run && !seen.has(o.scenario), 'observation_scope_invalid');
    seen.add(o.scenario);
    requireValue(
      ['success', 'failure', 'cancelled', 'unknown'].includes(o.workflow) &&
        ['success', 'failure', 'cancelled', 'not-started', 'unknown'].includes(o.step),
      'conclusion_invalid',
    );
    requireValue(
      [
        'bootstrap-confirmed',
        'restored-confirmed',
        'reset-confirmed',
        'refused',
        'unknown',
      ].includes(o.state),
      'state_invalid',
    );
    requireValue(
      dispositions.includes(o.acceptance) &&
        dispositions.includes(o.publication) &&
        ['confirmed', 'unknown'].includes(o.processClosed) &&
        ['complete', 'retained-for-reconciliation', 'unknown'].includes(o.cleanup) &&
        o.billing === 'unknown',
      'disposition_invalid',
    );
    integer(o.elapsedSeconds, 0, 86400);
    elapsed += o.elapsedSeconds;
    const values = outputs(o.outputs);
    consistent(values);
    for (const [key, assign] of [
      [
        'model-calls',
        (v) => {
          calls += v;
        },
      ],
      [
        'provider-attempts',
        (v) => {
          attempts += v;
        },
      ],
    ]) {
      if (values[key] !== null) assign(BigInt(values[key]));
    }
    for (const [key, cap] of [
      ['model-calls', prepared.plan.invocation.maxModelCalls],
      ['provider-attempts', prepared.plan.invocation.maxProviderAttempts],
      ['provider-failed-attempts', prepared.plan.invocation.maxProviderAttempts],
      ['provider-unknown-usage-attempts', prepared.plan.invocation.maxProviderAttempts],
      ['provider-unknown-cache-partition-attempts', prepared.plan.invocation.maxProviderAttempts],
      ['input-tokens', prepared.plan.invocation.maxTotalInputTokens],
      ['input-cache-hit-tokens', prepared.plan.invocation.maxCachedInputTokens],
      ['input-cache-miss-tokens', prepared.plan.invocation.maxUncachedInputTokens],
      ['output-tokens', prepared.plan.invocation.maxOutputTokens],
    ])
      requireValue(
        values[key] === null || BigInt(values[key]) <= BigInt(cap),
        'invocation_budget_overrun',
      );
    const unknownWork =
      values['provider-attempts'] === null ||
      values['attempt-accounting-completeness'] !== 'complete';
    const settled = o.processClosed === 'confirmed' && o.cleanup === 'complete';
    const reviewed =
      ['reviewed', 'reviewed_with_inline_warnings'].includes(values.status) &&
      values['termination-reason'] === 'review_completed';
    let result = 'inconclusive';
    if (
      settled &&
      o.workflow !== 'unknown' &&
      ['success', 'failure', 'cancelled'].includes(o.step)
    ) {
      const expectedState = {
        bootstrap: 'bootstrap-confirmed',
        continuation: 'restored-confirmed',
        reset: 'reset-confirmed',
      }[o.scenario];
      if (expectedState) {
        if (
          o.workflow === 'success' &&
          o.step === 'success' &&
          reviewed &&
          o.state === expectedState &&
          o.acceptance === 'confirmed' &&
          o.publication === 'confirmed'
        )
          result = 'success';
        else if (
          o.workflow === 'failure' &&
          o.acceptance !== 'unknown' &&
          o.publication !== 'unknown'
        )
          result = 'failed';
      } else {
        const expected =
          o.scenario === 'stale-head'
            ? values.status === 'stale_head'
            : o.scenario === 'incomplete'
              ? ['snapshot_incomplete', 'agent_result_invalid'].includes(values.status) ||
                [
                  'resource_limit',
                  'token_limit',
                  'context_limit',
                  'tool_limit',
                  'model_limit',
                ].includes(values['termination-reason'])
              : ['provider_failed', 'cancelled', 'internal_failure', 'failed'].includes(
                  values.status,
                );
        if (
          expected &&
          o.state === 'refused' &&
          o.acceptance === 'absent' &&
          o.publication === 'absent'
        )
          result = 'success';
        else if (reviewed && o.acceptance === 'confirmed' && o.publication === 'confirmed')
          result = 'failed';
      }
    }
    return {
      scenario: o.scenario,
      basis: run.basis,
      result,
      workflow: o.workflow,
      step: o.step,
      outputs: values,
      state: o.state,
      acceptance: o.acceptance,
      publication: o.publication,
      processClosed: o.processClosed,
      cleanup: o.cleanup,
      elapsedSeconds: o.elapsedSeconds,
      usageScope: 'current-invocation-only',
      unknownWork,
      billing: 'unknown',
      furtherWork: 'requires-separate-authority-and-budget-reconciliation',
    };
  });
  const b = prepared.plan.campaign;
  requireValue(
    calls <= BigInt(b.maxModelCalls) &&
      attempts <= BigInt(b.maxProviderAttempts) &&
      elapsed <= b.wallSeconds,
    'observed_budget_overrun',
  );
  for (const run of prepared.plan.runs)
    if (!seen.has(run.scenario))
      cases.push({
        scenario: run.scenario,
        basis: run.basis,
        result: 'inconclusive',
        observation: 'not-observed',
      });
  const result = cases.some((c) => c.result === 'failed')
    ? 'failed'
    : cases.every((c) => c.result === 'success')
      ? 'success'
      : 'inconclusive';
  return {
    kind: 'r7-adoption-public-evidence',
    mode: data.mode,
    result,
    releaseAuthenticated: false,
    operationallyQualified: false,
    realExecutionAllowed: false,
    candidate: {
      releaseVersion: prepared.pair.payload.identity.releaseVersion,
      pairSha256: prepared.plan.candidate.pairSha256,
      receiptSha256: prepared.plan.candidate.receiptSha256,
    },
    cases,
    billing: 'unknown',
    budget: { ...b },
    limitation: 'reported-observations-only-not-release-adoption-quality-or-billing-authentication',
  };
}
