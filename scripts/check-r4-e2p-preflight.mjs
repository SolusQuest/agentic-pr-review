import crypto from 'node:crypto';
import fs from 'node:fs';
import vm from 'node:vm';

const startMarker = '          node <<\'APR_R4_E2P_PREFLIGHT\' >> "$GITHUB_OUTPUT"\n';
const endMarker = '          APR_R4_E2P_PREFLIGHT\n';

export function extractPreflight(templateBytes) {
  const template = Buffer.isBuffer(templateBytes) ? templateBytes.toString('utf8') : templateBytes;
  if (Buffer.byteLength(template, 'utf8') > 256 * 1024 || template.includes('\r')) {
    throw new Error('APR_R4_E2P_PREFLIGHT_TEMPLATE_INVALID');
  }
  const start = template.indexOf(startMarker);
  const end = template.indexOf(endMarker, start + startMarker.length);
  if (
    start < 0 ||
    end < 0 ||
    template.indexOf(startMarker, start + 1) >= 0 ||
    template.indexOf(endMarker, end + 1) >= 0
  ) {
    throw new Error('APR_R4_E2P_PREFLIGHT_MARKERS_INVALID');
  }
  const indented = template.slice(start + startMarker.length, end);
  const lines = indented.split('\n');
  if (lines.at(-1) === '') lines.pop();
  if (lines.length === 0 || lines.some((line) => !line.startsWith('          '))) {
    throw new Error('APR_R4_E2P_PREFLIGHT_INDENT_INVALID');
  }
  return `${lines.map((line) => line.slice(10)).join('\n')}\n`;
}

export function authorizationManifest(values) {
  return JSON.stringify({
    kind: 'apr-r4-e2p-authorization-manifest-v1',
    repository_id: values.repositoryId,
    repository: values.repository,
    pr_number: values.prNumber,
    fixture_head_sha: values.fixtureHeadSha,
    operation_id: values.operationId,
    workflow_sha: values.workflowSha,
    action_source_sha: values.actionSourceSha,
    payload_sha256: values.payloadSha256,
  });
}

export function authorizationManifestV2(values) {
  return JSON.stringify({
    kind: 'apr-r4-e2p-authorization-manifest-v2',
    repository_id: values.repositoryId,
    repository: values.repository,
    pr_number: values.prNumber,
    proof_scope: values.proofScope,
    fixture_head_sha: values.fixtureHeadSha,
    operation_id: values.operationId,
    workflow_sha: values.workflowSha,
    action_source_sha: values.actionSourceSha,
    payload_source_sha: values.payloadSourceSha,
    payload_sha256: values.payloadSha256,
  });
}

export function authorizationDigest(values) {
  return crypto.createHash('sha256').update(authorizationManifest(values)).digest('hex');
}

export function authorizationDigestV2(values) {
  return crypto.createHash('sha256').update(authorizationManifestV2(values)).digest('hex');
}

export async function runExtractedPreflight({ source, environment, fetchImpl }) {
  let stdout = '';
  let stderr = '';
  const context = vm.createContext({
    AbortSignal,
    TextDecoder,
    TextEncoder,
    crypto: crypto.webcrypto,
    fetch: fetchImpl,
    process: {
      env: Object.freeze({ ...environment }),
      stdout: { write: (value) => (stdout += value) },
      stderr: { write: (value) => (stderr += value) },
    },
  });
  const result = new vm.Script(source, {
    filename: 'r4-e2p-inline-preflight.mjs',
  }).runInContext(context, { timeout: 15_000 });
  await result;
  return { stdout, stderr };
}

function main() {
  const path = process.argv[2];
  if (process.argv.length !== 3 || !path) {
    process.stderr.write('usage: node scripts/check-r4-e2p-preflight.mjs <template>\n');
    process.exitCode = 2;
    return;
  }
  const source = extractPreflight(fs.readFileSync(path));
  if (
    !source.includes("'apr-r4-e2p-authorization-manifest-v1'") &&
    !source.includes("'apr-r4-e2p-authorization-manifest-v2'")
  ) {
    throw new Error('APR_R4_E2P_PREFLIGHT_CONTRACT_MISSING');
  }
  process.stdout.write('APR_R4_E2P_PREFLIGHT_CONTRACT_OK\n');
}

if (import.meta.url === `file://${process.argv[1]?.replaceAll('\\', '/')}`) {
  main();
}
