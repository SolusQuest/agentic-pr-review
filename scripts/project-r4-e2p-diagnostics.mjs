import fs from 'node:fs';
import { basename, dirname, isAbsolute, join, relative, resolve } from 'node:path';

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
// the builder, alongside bounded request counts. Paths, bodies and unknown
// keys or values are discarded.
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
const metrics = new Set([
  'synthetic_primary_remaining',
  'host_rest',
  'artifact_rest',
  'artifact_primary',
]);
const boundedCount = (value) => Number.isSafeInteger(value) && value >= 0 && value <= 1_000_000;
function supervisor(path) {
  if (fs.statSync(path).size > 8 * 1024 * 1024) return [];
  const value = JSON.parse(fs.readFileSync(path, 'utf8'));
  const failed = Array.isArray(value?.cases)
    ? value.cases.filter((item) => item?.Passed !== true)
    : [];
  const lines = [];
  if (failed.length === 0)
    lines.push(
      'APR_R4_E2P_V2_FAILURE stage=synthetic-route case=supervisor code=invalid-evidence status=failed',
    );
  for (const item of (Array.isArray(value?.cases) ? value.cases : []).slice(0, 5)) {
    if (!allowed.case.has(item?.Name)) continue;
    for (const [metric, field] of [
      ['host_rest', 'GitHubRequests'],
      ['artifact_rest', 'ArtifactRestRequests'],
      ['artifact_primary', 'ArtifactRestPrimary'],
    ]) {
      if (boundedCount(item[field]))
        lines.push(
          `APR_R4_E2P_V2_DIAGNOSTIC case=${item.Name} metric=${metric} value=${item[field]}`,
        );
    }
  }
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
      if (boundedCount(diagnostic.synthetic_primary_remaining))
        lines.push(
          `APR_R4_E2P_V2_DIAGNOSTIC case=${id} metric=synthetic_primary_remaining value=${diagnostic.synthetic_primary_remaining}`,
        );
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
  const counts = [];
  for (const line of fs.readFileSync(path, 'utf8').split(/\r?\n/u)) {
    const stage = /^APR_R4_E2P_V2_DIAGNOSTIC stage=([a-z-]+)$/u.exec(line);
    if (stage && executionStages.has(stage[1])) lastStage = stage[1];
    const count =
      /^APR_R4_E2P_V2_DIAGNOSTIC case=([a-z-]+) metric=([a-z_]+) value=([0-9]{1,7})$/u.exec(line);
    if (
      count &&
      allowed.case.has(count[1]) &&
      metrics.has(count[2]) &&
      boundedCount(Number(count[3])) &&
      !counts.includes(line) &&
      counts.length < 20
    )
      counts.push(line);
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
  return [...projected, ...predicates, ...counts];
}

// This observer is current reviewed control code outside the historical checkout.
// Its predicates describe bytes seen before cleanup, never proof acceptance.
const legacyStates = new Set(['readable', 'missing', 'oversized', 'unavailable']);
const legacyErrors = new Set(['restore', 'compiler', 'sdk', 'native', 'unknown']);
const legacyPredicates = new Set([true, false, 'unknown']);
const legacyKeys = [
  'version',
  'child_exit',
  'clean_source',
  'publish',
  'verifier',
  'clean_source_error',
  'publish_error',
  'verifier_error',
  'warning_policy',
  'malformed_frame',
  'receipt',
  'receipt_single_line',
  'receipt_prefix',
];

function ownedDirectory(path) {
  const stat = fs.lstatSync(path);
  if (
    !stat.isDirectory() ||
    stat.uid !== process.getuid() ||
    (stat.mode & 0o077) !== 0 ||
    fs.realpathSync(path) !== resolve(path)
  )
    throw new Error('unavailable');
  return stat;
}

function boundedFile(path, limit) {
  let descriptor;
  try {
    const before = fs.lstatSync(path);
    if (!before.isFile() || before.uid !== process.getuid() || before.nlink !== 1)
      return { state: 'unavailable' };
    if (before.size > limit) return { state: 'oversized' };
    descriptor = fs.openSync(
      path,
      fs.constants.O_RDONLY | fs.constants.O_NOFOLLOW | fs.constants.O_NONBLOCK,
    );
    const stat = fs.fstatSync(descriptor);
    if (
      !stat.isFile() ||
      stat.uid !== process.getuid() ||
      stat.nlink !== 1 ||
      stat.dev !== before.dev ||
      stat.ino !== before.ino
    )
      return { state: 'unavailable' };
    if (stat.size > limit) return { state: 'oversized' };
    const buffer = Buffer.alloc(limit + 1);
    let length = 0;
    while (length < buffer.length) {
      const count = fs.readSync(descriptor, buffer, length, buffer.length - length, length);
      if (count === 0) break;
      length += count;
    }
    return length > limit
      ? { state: 'oversized' }
      : { state: 'readable', text: buffer.subarray(0, length).toString('utf8') };
  } catch (error) {
    return { state: error?.code === 'ENOENT' ? 'missing' : 'unavailable' };
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
  }
}

function legacyError(text = '') {
  for (const [family, code] of [
    ['restore', 'NU'],
    ['compiler', 'CS'],
    ['sdk', 'NETSDK'],
    ['native', 'IL'],
  ]) {
    if (new RegExp(`\\berror ${code}[0-9]{4}:`, 'u').test(text)) return family;
  }
  return 'unknown';
}

function legacyCapture(root, output, status) {
  if (!/^(?:0|[1-9][0-9]{0,2})$/u.test(status) || Number(status) > 255)
    throw new Error('unavailable');
  const before = ownedDirectory(root);
  if (!/^apr-r4-e2p\.[A-Za-z0-9]{8}$/u.test(basename(root))) throw new Error('unavailable');
  const outside = relative(resolve(root), resolve(output));
  if (!isAbsolute(outside) && outside !== '..' && !outside.startsWith('../'))
    throw new Error('unavailable');
  ownedDirectory(dirname(output));
  const clean = boundedFile(join(root, 'clean-source.log'), 8 * 1024 * 1024);
  const publish = boundedFile(join(root, 'publish.log'), 8 * 1024 * 1024);
  const verifier = boundedFile(join(root, 'verifier-publish.log'), 8 * 1024 * 1024);
  const receipt = boundedFile(join(root, 'receipt.line'), 32768);
  const value = {
    version: 1,
    child_exit: Number(status),
    clean_source: clean.state,
    publish: publish.state,
    verifier: verifier.state,
    clean_source_error: legacyError(clean.text),
    publish_error: legacyError(publish.text),
    verifier_error: legacyError(verifier.text),
    warning_policy:
      clean.state === 'readable'
        ? /^APR_R4_E2P_(?:VERIFIER_)?AOT_WARNING_ALLOWLIST_INVALID$/mu.test(clean.text)
        : 'unknown',
    malformed_frame:
      clean.state === 'readable'
        ? /^APR_R4_E2P_AOT_MALFORMED_FRAME_ACCEPTED$/mu.test(clean.text)
        : 'unknown',
    receipt: receipt.state,
    receipt_single_line:
      receipt.state === 'readable' ? /^[^\n]*\n$/u.test(receipt.text) : 'unknown',
    receipt_prefix:
      receipt.state === 'readable' ? /^APR_R4_E2P_RECEIPT \{.*\}$/mu.test(receipt.text) : 'unknown',
  };
  const after = ownedDirectory(root);
  if (before.dev !== after.dev || before.ino !== after.ino) throw new Error('unavailable');
  const bytes = JSON.stringify(value) + '\n';
  if (Buffer.byteLength(bytes) > 1024) throw new Error('unavailable');
  fs.writeFileSync(output, bytes, { flag: 'wx', mode: 0o600 });
}

function legacyReport(file, role) {
  if (!['first', 'second'].includes(role)) return;
  const unavailable = `APR_R4_E2P_OBSERVER role=${role} observer=unavailable\n`;
  try {
    const snapshot = boundedFile(file, 1024);
    if (snapshot.state !== 'readable') throw new Error('unavailable');
    const value = JSON.parse(snapshot.text);
    if (
      !value ||
      Array.isArray(value) ||
      JSON.stringify(Object.keys(value).sort()) !== JSON.stringify([...legacyKeys].sort()) ||
      value.version !== 1 ||
      !Number.isInteger(value.child_exit) ||
      value.child_exit < 0 ||
      value.child_exit > 255 ||
      !['clean_source', 'publish', 'verifier', 'receipt'].every((key) =>
        legacyStates.has(value[key]),
      ) ||
      !['clean_source_error', 'publish_error', 'verifier_error'].every((key) =>
        legacyErrors.has(value[key]),
      ) ||
      !['warning_policy', 'malformed_frame', 'receipt_single_line', 'receipt_prefix'].every((key) =>
        legacyPredicates.has(value[key]),
      )
    )
      throw new Error('unavailable');
    const prefix = `APR_R4_E2P_OBSERVER role=${role}`;
    const lines = [`${prefix} observer=available child_exit=${value.child_exit}`];
    for (const log of ['clean_source', 'publish', 'verifier'])
      lines.push(`${prefix} log=${log} state=${value[log]} error=${value[`${log}_error`]}`);
    lines.push(
      `${prefix} warning_policy=${value.warning_policy} malformed_frame=${value.malformed_frame}`,
    );
    lines.push(
      `${prefix} receipt=${value.receipt} single_line=${value.receipt_single_line} prefix=${value.receipt_prefix}`,
    );
    process.stdout.write(lines.join('\n') + '\n');
  } catch {
    process.stdout.write(unavailable);
  }
}

function legacyMode() {
  if (process.argv[2] === '--legacy-hook' && process.argv.length === 3) {
    process.stdout.write(`if [[ "\${BASH_EXECUTION_STRING:-}" == execute_proof ]]; then
  apr_r4_observer_exit() {
    local apr_r4_observer_status="$1"
    trap - EXIT
    node "\${APR_R4_OBSERVER_HELPER:-}" --legacy-capture "\${temporary_root:-}" "\${APR_R4_OBSERVER_OUTPUT:-}" "$apr_r4_observer_status" >/dev/null 2>&1 || :
    exit "$apr_r4_observer_status"
  }
  trap 'apr_r4_observer_exit "$?"' EXIT
fi
`);
  } else if (process.argv[2] === '--legacy-capture' && process.argv.length === 6) {
    try {
      legacyCapture(...process.argv.slice(3));
    } catch {}
  } else if (process.argv[2] === '--legacy-report' && process.argv.length === 5) {
    legacyReport(process.argv[3], process.argv[4]);
  }
}

if (process.argv[2]?.startsWith('--legacy-')) {
  legacyMode();
} else {
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
}
