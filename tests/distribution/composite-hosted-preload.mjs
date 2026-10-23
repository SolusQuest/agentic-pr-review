// Explicit trusted proof preload. Never imported by a product bundle or selected by product inputs.
import fs from 'node:fs';
import { dirname, join } from 'node:path';
import { createHash } from 'node:crypto';
import { spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';
import { syntheticRuntimeToken } from './managed-generated-official-platform.mjs';
import { inspectCompositeNetwork } from './composite-network-audit.mjs';
import { assets, cases, repository, actionSource } from './composite-proof-source.mjs';
function requireProof(value) {
  if (!value) throw new Error('composite_hosted_proof_context_invalid');
}
const config = JSON.parse(fs.readFileSync(process.env.APR_A1_COMPOSITE_CONFIG, 'utf8'));
const name = process.env.APR_A1_CASE;
requireProof(
  cases.includes(name) &&
    process.platform === 'linux' &&
    process.arch === 'x64' &&
    process.versions.node.split('.')[0] === '24',
);
requireProof(
  process.env.GITHUB_ACTION_REPOSITORY === repository &&
    process.env.GITHUB_ACTION_REF === actionSource &&
    config.actionSource === actionSource,
);
requireProof(process.env.NODE_OPTIONS === '--import ' + config.preload && !process.env.NODE_PATH);
const phase = process.argv[1].split('/').at(-1);
requireProof(['index.js', 'prepare.js', 'cleanup.js'].includes(phase));
const root = dirname(dirname(dirname(fs.realpathSync(process.argv[1]))));
const mapBytes = Buffer.from(JSON.stringify(config.map));
let originalMapObserved = false;
for (const relative of assets) {
  const bytes = fs.readFileSync(join(root, relative));
  if (relative === 'payload-map.json' && bytes.equals(mapBytes)) continue;
  const hash = createHash('sha256').update(bytes).digest('hex');
  requireProof(hash === config.assetSha256[relative]);
  if (relative === 'payload-map.json') {
    requireProof(JSON.parse(bytes) === null);
    originalMapObserved = true;
  }
}
// Only the copied test map changes. The checked source remains unbound; all executable assets match the fixed commit.
if (phase === 'prepare.js')
  fs.writeFileSync(join(root, 'payload-map.json'), name === 'unbound' ? 'null\n' : mapBytes);
const runtimeTokenPresent =
  typeof process.env.ACTIONS_RUNTIME_TOKEN === 'string' &&
  process.env.ACTIONS_RUNTIME_TOKEN.length > 0;
const resultsUrlPresent =
  typeof process.env.ACTIONS_RESULTS_URL === 'string' && process.env.ACTIONS_RESULTS_URL.length > 0;
if (phase !== 'cleanup.js') requireProof(runtimeTokenPresent && resultsUrlPresent);
// No real credential is copied, printed, stored or passed to any network operation.
process.env.ACTIONS_RUNTIME_TOKEN = syntheticRuntimeToken;
process.env.ACTIONS_RESULTS_URL = config.origin;
if (name === 'missing-capability' && phase === 'index.js') delete process.env.ACTIONS_RUNTIME_TOKEN;
delete process.env.NODE_OPTIONS;
for (const key of [
  'HTTP_PROXY',
  'HTTPS_PROXY',
  'NO_PROXY',
  'http_proxy',
  'https_proxy',
  'no_proxy',
])
  delete process.env[key];
const directory = join(config.work, name);
process.env.APR_D4_FIXTURE_CONFIG = join(directory, 'fixture.json');
process.env.APR_FIXTURE_PHASE =
  phase === 'prepare.js' ? 'prepare' : phase === 'cleanup.js' ? 'post' : 'launch';
Object.assign(process.env, {
  RUNNER_TEMP: join(directory, 'runner-temp'),
  GITHUB_WORKSPACE: join(directory, 'workspace'),
  GITHUB_EVENT_PATH: join(directory, 'event.json'),
  GITHUB_REPOSITORY: repository,
  GITHUB_REPOSITORY_ID: '42',
  GITHUB_RUN_ID: '900',
  GITHUB_RUN_ATTEMPT: '1',
  GITHUB_WORKFLOW_REF: repository + '/.github/workflows/r4-trusted-proof.yml@refs/heads/main',
  GITHUB_WORKFLOW_SHA: 'a'.repeat(40),
  TMPDIR: join(directory, 'temp'),
  TMP: join(directory, 'temp'),
  TEMP: join(directory, 'temp'),
});
const trace = join(directory, phase + '-' + process.pid + '.network');
// Observe the actual Runner-started process and descendants without replacing the SDK executable/argv/environment.
const observer = spawn(
  '/usr/bin/sudo',
  [
    '-n',
    '/usr/bin/strace',
    '-f',
    '-s',
    '0',
    '-e',
    'trace=network',
    '-p',
    String(process.pid),
    '-o',
    trace,
  ],
  { stdio: ['ignore', 'ignore', 'pipe'] },
);
await new Promise((resolve, reject) => {
  const timer = setTimeout(
    () => reject(new Error('composite_network_observer_unavailable')),
    5_000,
  );
  observer.stderr.on('data', (chunk) => {
    if (chunk.toString().includes('Process ' + process.pid + ' attached')) {
      clearTimeout(timer);
      resolve();
    }
  });
  observer.once('error', () => {
    clearTimeout(timer);
    reject(new Error('composite_network_observer_unavailable'));
  });
  observer.once('exit', () => {
    clearTimeout(timer);
    reject(new Error('composite_network_observer_unavailable'));
  });
});
observer.unref();
observer.stderr.unref?.();
fs.appendFileSync(
  join(directory, 'runner.jsonl'),
  JSON.stringify({
    phase,
    actionSource,
    workflowSource: config.workflowSource,
    runtimeTokenPresent,
    resultsUrlPresent,
    originalMapObserved,
    fixedAssetsMatched: assets.length - 1,
    syntheticBeforeNetwork: true,
    trace,
    pid: process.pid,
  }) + '\n',
);
await import(
  pathToFileURL(join(config.repo, 'tests/distribution/managed-generated-action-preload.mjs')).href
);

if (phase === 'cleanup.js')
  process.once('beforeExit', () => {
    let valid = (process.exitCode ?? 0) === 0;
    try {
      inspectCompositeNetwork(fs.readFileSync(trace, 'utf8'), Number(new URL(config.origin).port));
      const encoded = process.env['STATE_apr-candidate'];
      if (encoded) {
        const owner = JSON.parse(encoded);
        requireProof(dirname(owner.tempRoot) === join(directory, 'runner-temp'));
        valid &&= !fs.existsSync(owner.tempRoot);
      }
    } catch {
      valid = false;
    }
    if (!valid) process.exitCode = 1;
    console.log(
      'A1_COMPOSITE_POST ' +
        JSON.stringify({
          case: name,
          actionSource,
          completed: valid,
          externalServiceDestinations: 0,
        }),
    );
  });
