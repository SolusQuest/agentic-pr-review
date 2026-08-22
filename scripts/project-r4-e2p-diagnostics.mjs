import fs from 'node:fs';

const fallback = 'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed';
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
  code: new Set(['smoke-failed', 'no-evidence', 'invalid-evidence', 'case-failed', 'child-failed']),
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
    /^APR_R4_E2P_FAILURE stage=([a-z-]+) case=([a-z-]+) code=([a-z-]+) status=([a-z_]+)$/u;
  const projected = [];
  for (const line of fs.readFileSync(path, 'utf8').split(/\r?\n/u)) {
    const match = pattern.exec(line);
    if (
      match &&
      allowed.stage.has(match[1]) &&
      allowed.case.has(match[2]) &&
      allowed.code.has(match[3]) &&
      allowed.status.has(match[4]) &&
      !projected.includes(line)
    ) {
      projected.push(line);
      if (projected.length === 3) break;
    }
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
