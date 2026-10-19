import fs from 'node:fs';

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
    'null',
    'unknown',
  ]),
};

function project(path) {
  if (!path || !fs.existsSync(path) || fs.statSync(path).size > 8 * 1024 * 1024) return [];
  const pattern =
    /^APR_R4_E2P(?:_V2)?_FAILURE stage=([a-z-]+) case=([a-z-]+) code=([a-z-]+) status=([a-z_]+)$/u;
  const projected = [];
  let lastStage;
  for (const line of fs.readFileSync(path, 'utf8').split(/\r?\n/u)) {
    const stage = /^APR_R4_E2P_V2_DIAGNOSTIC stage=([a-z-]+)$/u.exec(line);
    if (stage && executionStages.has(stage[1])) lastStage = stage[1];
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
  return projected;
}

let projected = [];
try {
  projected = process.argv.length === 3 ? project(process.argv[2]) : [];
} catch {
  projected = [];
}
process.stdout.write(`${(projected.length === 0 ? [fallback] : projected).join('\n')}\n`);
