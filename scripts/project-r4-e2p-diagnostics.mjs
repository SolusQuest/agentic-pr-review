import fs from 'node:fs';
import { dirname, join } from 'node:path';

const fallback = 'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed';
const executionStages = new Set([
  'bundle',
  'host-unit-oracles',
  'publish-payload',
  'smoke',
  'payload-intermediates',
  'publish-verifier',
  'verifier-intermediates',
  'architecture',
  'malformed-frame',
  'build-supervisor',
  'supervisor',
  'normalize-evidence',
  'receipt',
  'two-root-preparation',
  'complete',
]);
const allowed = {
  stage: new Set(['production-smoke', 'synthetic-route', 'proof']),
  case: new Set([
    'aggregate',
    'supervisor',
    'dispatch-bootstrap',
    'dispatch-continuation',
    'stale-head',
    'state-visibility-diagnostic',
    'stale-unauthorized-follow-on',
    'unknown',
    'clean-source',
  ]),
  code: new Set([
    'smoke-failed',
    'no-evidence',
    'invalid-evidence',
    'case-failed',
    'warning-policy',
    'child-failed',
  ]),
  status: new Set([
    'failed',
    'reviewed',
    'reviewed_with_inline_warnings',
    'stale_head',
    'authorization_failed',
    'credentials_missing',
    'agent_result_invalid',
    'provider_failed',
    'outcome_ambiguous',
    'null',
    'unknown',
  ]),
};

// Only fixed boolean predicates from the private supervisor diagnostic leave
// the builder. Paths, bodies, numeric counters and unknown keys are discarded.
const checks = new Set([
  'exited',
  'expected_outcome',
  'current_state_setup_boundary',
  'barrier_after',
  'canary_safe',
  'environment_recorded',
  'reordered_history_rejected',
  'state_read_clock_advanced',
  'outputs_match_summary',
  'process_group_quiet',
  'platform_quiet',
  'canonical_request_events',
  'continuation_observed',
  'successful_continuation',
  'tool_sequence_satisfied',
  'signal_gate_reached',
  'host_initialization_observed',
  'no_provider_satisfied',
  'provider_count_satisfied',
  'sticky_count_satisfied',
  'scenario_evidence_satisfied',
  'state_reconciliation_diagnostic_satisfied',
  'archive_transport_satisfied',
  'trusted_proof_request_budget_satisfied',
  'artifact_rest_budget_satisfied',
  'state_operation_satisfied',
  'global_evidence_satisfied',
  'no_unexpected_github_request',
]);
function supervisor(path) {
  if (fs.statSync(path).size > 8 * 1024 * 1024) return [];
  const value = JSON.parse(fs.readFileSync(path, 'utf8'));
  const failed = Array.isArray(value?.cases)
    ? value.cases.filter((item) => item?.Passed !== true)
    : [];
  const lines = [];
  for (const item of failed.slice(0, 5)) {
    const id = allowed.case.has(item?.Name) ? item.Name : 'unknown';
    const raw = item?.ActualStatus === null ? 'null' : item?.ActualStatus;
    const status = allowed.status.has(raw) ? raw : 'unknown';
    lines.push(
      `APR_R4_E2P_V2_FAILURE stage=synthetic-route case=${id} code=case-failed status=${status}`,
    );
    if (id === 'unknown') continue;
    try {
      const diagnosticPath = join(dirname(path), id, 'case-diagnostic.json');
      if (fs.statSync(diagnosticPath).size > 32768) continue;
      const diagnostic = JSON.parse(fs.readFileSync(diagnosticPath, 'utf8'));
      for (const check of checks)
        if (diagnostic[check] === false)
          lines.push(`APR_R4_E2P_V2_DIAGNOSTIC case=${id} check=${check} result=failed`);
    } catch {}
  }
  return lines.length
    ? lines
    : [
        'APR_R4_E2P_V2_FAILURE stage=synthetic-route case=supervisor code=invalid-evidence status=failed',
      ];
}

function project(path) {
  if (!path || !fs.existsSync(path) || fs.statSync(path).size > 8 * 1024 * 1024) return [];
  const pattern =
    /^APR_R4_E2P(?:_V2)?_FAILURE stage=([a-z-]+) case=([a-z-]+) code=([a-z-]+) status=([a-z_]+)$/u;
  const projected = [];
  let lastStage;
  const predicates = [];
  for (const line of fs.readFileSync(path, 'utf8').split(/\r?\n/u)) {
    const stage = /^APR_R4_E2P_V2_DIAGNOSTIC stage=([a-z-]+)$/u.exec(line);
    if (stage && executionStages.has(stage[1])) lastStage = stage[1];
    const predicate =
      /^APR_R4_E2P_V2_DIAGNOSTIC case=([a-z-]+) check=([a-z_]+) result=failed$/u.exec(line);
    if (
      predicate &&
      allowed.case.has(predicate[1]) &&
      checks.has(predicate[2]) &&
      !predicates.includes(line) &&
      predicates.length < 32
    )
      predicates.push(line);
    const match = pattern.exec(line);
    if (
      match &&
      allowed.stage.has(match[1]) &&
      allowed.case.has(match[2]) &&
      allowed.code.has(match[3]) &&
      allowed.status.has(match[4]) &&
      !projected.includes(line) &&
      projected.length < 3
    ) {
      projected.push(line);
    }
  }
  if (lastStage) {
    if (projected.length === 0) projected.push(fallback);
    projected.push(`APR_R4_E2P_V2_DIAGNOSTIC stage=${lastStage}`);
  }
  return [...projected, ...predicates];
}

let projected = [];
try {
  projected =
    process.argv.length === 4 && process.argv[2] === '--supervisor'
      ? supervisor(process.argv[3])
      : process.argv.length === 3
        ? project(process.argv[2])
        : [];
} catch {
  projected = [];
}
process.stdout.write(`${(projected.length === 0 ? [fallback] : projected).join('\n')}\n`);
