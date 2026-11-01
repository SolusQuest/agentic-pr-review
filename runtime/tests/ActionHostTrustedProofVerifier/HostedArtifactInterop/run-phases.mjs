// Process rehearsal of the unchanged installed internal entries. Actual Runner uses the workflow's composite.
import { spawn } from 'node:child_process';
import { cp, mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { inspectCompositeNetwork } from '../../../../tests/distribution/composite-network-audit.mjs';
import {
  actionSource,
  inspectAssets,
  repository,
  requireProof,
} from '../../../../scripts/cr2-hosted-artifact-policy.mjs';

export function inspectProofNetwork(text, port, observationRequired = true) {
  // .NET's dual-mode socket representation denotes the same exact loopback address.
  const normalized = text.replaceAll('"::ffff:127.0.0.1"', '"127.0.0.1"');
  const result = inspectCompositeNetwork(normalized, port);
  if (observationRequired) requireProof(result.inetOperations > 0);
  return result;
}

function values(text) {
  const rows = text.trimEnd().split('\n'),
    result = {};
  if (!text) return result;
  for (let i = 0; i < rows.length; i += 3) {
    const [name, delimiter] = rows[i].split('<<');
    requireProof(name && delimiter && rows[i + 2] === delimiter && !Object.hasOwn(result, name));
    result[name] = rows[i + 1];
  }
  return result;
}
export async function runPhases(configPath) {
  const config = JSON.parse(await readFile(configPath, 'utf8'));
  requireProof(config.mode === 'rehearsal' && process.platform === 'linux');
  const directory = join(config.work, 'phase-' + config.phase);
  for (const name of ['workspace', 'runner', 'installed'])
    await mkdir(join(directory, name), { recursive: true, mode: 0o700 });
  const installed = join(directory, 'installed');
  requireProof(typeof config.historicalActionRoot === 'string');
  inspectAssets(config.repo, undefined, config.historicalActionRoot);
  await cp(config.historicalActionRoot, installed, { recursive: true });
  inspectAssets(config.repo, installed, config.historicalActionRoot);
  const output = join(directory, 'outputs'),
    state = join(directory, 'state'),
    prepareOutput = join(directory, 'prepared'),
    summary = join(directory, 'summary');
  for (const file of [output, state, prepareOutput, summary])
    await writeFile(file, '', { mode: 0o600 });
  const preload = join(
    config.repo,
    'runtime/tests/ActionHostTrustedProofVerifier/HostedArtifactInterop/preload.mjs',
  );
  const env = {
    PATH: process.env.PATH,
    ...config.syntheticEnvironment,
    GITHUB_ACTION_REPOSITORY: repository,
    GITHUB_ACTION_REF: actionSource,
    RUNNER_TEMP: join(directory, 'runner'),
    GITHUB_WORKSPACE: join(directory, 'workspace'),
    GITHUB_OUTPUT: output,
    GITHUB_STATE: state,
    GITHUB_STEP_SUMMARY: summary,
    APR_CR2_H1_CONFIG: configPath,
    NODE_OPTIONS: '--import ' + preload,
    'INPUT_GITHUB-TOKEN': 'cr2-h1-rest-token-canary',
    'INPUT_STATE-KEY': 'cr2-h1-state-key-canary',
    'INPUT_PROVIDER-API-KEY': 'cr2-h1-provider-canary',
    TMPDIR: '/tmp',
    TMP: '/tmp',
    TEMP: '/tmp',
  };
  const mechanical = { ...env };
  for (const key of Object.keys(mechanical)) if (key.startsWith('INPUT_')) delete mechanical[key];
  const statuses = [];
  async function phase(entry, environment) {
    const trace = join(directory, entry + '.network');
    const child = spawn(
      'strace',
      [
        '-f',
        '-s',
        '0',
        '-e',
        'trace=network',
        '-o',
        trace,
        process.execPath,
        join(installed, 'internal/dist', entry),
      ],
      { cwd: directory, env: environment, stdio: ['ignore', 'pipe', 'pipe'] },
    );
    const chunks = [];
    let size = 0;
    for (const stream of [child.stdout, child.stderr])
      stream.on('data', (chunk) => {
        size += chunk.length;
        if (size > 131072) child.kill('SIGKILL');
        else chunks.push(chunk);
      });
    const code = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (code) => resolve(code));
    });
    requireProof(size <= 131072);
    const captured = Buffer.concat(chunks);
    // Mask commands are the toolkit's admitted secret channel; no private captured text is persisted.
    const publicText = captured
      .toString()
      .split('\n')
      .filter((line) => !line.startsWith('::add-mask::'))
      .join('\n');
    for (const canary of [
      config.runtimeToken,
      'CR2-H1-SYNTHETIC-OPAQUE-V1',
      'cr2-h1-rest-token-canary',
      'cr2-h1-runtime-token-canary',
      'cr2-h1-state-key-canary',
      'cr2-h1-provider-canary',
      'signed-url-canary',
      'download-url-canary',
    ])
      requireProof(!publicText.includes(canary));
    captured.fill(0);
    const network = inspectProofNetwork(
      await readFile(trace, 'utf8'),
      Number(new URL(config.origin).port),
      entry !== 'cleanup.js' && process.env.APR_CR2_H1_EMULATED_DEVELOPMENT !== '1',
    );
    if (entry !== 'cleanup.js' && network.inetOperations === 0) {
      // An emulated strace can return an empty observation. Never call it a zero-egress pass.
      requireProof(process.env.APR_CR2_H1_EMULATED_DEVELOPMENT === '1');
      network.externalServiceDestinations = null;
      network.observation = 'unavailable-emulated-development';
    }
    const diagnostics = [
      ...new Set(publicText.match(/\b(?:wrapper_[a-z_]+|APR_[A-Z_]+|cr2_[a-z_]+)\b/gu) ?? []),
    ];
    statuses.push({ phase: entry, code, network, diagnostics });
    return code;
  }
  const prepared = await phase('prepare.js', { ...mechanical, GITHUB_OUTPUT: prepareOutput });
  let launched = prepared;
  if (prepared === 0) {
    const result = values(await readFile(prepareOutput, 'utf8'));
    requireProof(Object.keys(result).join() === 'candidate');
    launched = await phase('index.js', { ...env, APR_PREPARED_CANDIDATE: result.candidate });
  }
  const saved = values(await readFile(state, 'utf8'));
  const post = await phase('cleanup.js', {
    ...mechanical,
    'STATE_apr-candidate': saved['apr-candidate'] ?? '',
  });
  for (const file of [output, state, prepareOutput, summary]) {
    const content = await readFile(file, 'utf8');
    for (const canary of [
      config.runtimeToken,
      'CR2-H1-SYNTHETIC-OPAQUE-V1',
      'cr2-h1-rest-token-canary',
      'cr2-h1-state-key-canary',
      'cr2-h1-provider-canary',
      'signed-url-canary',
      'download-url-canary',
    ])
      requireProof(!content.includes(canary));
  }
  // Persist only fixed phase labels, integer exits and destination counts.
  await writeFile(join(directory, 'phases.json'), JSON.stringify(statuses), { mode: 0o600 });
  console.log('CR2_H1_PHASES ' + JSON.stringify(statuses));
  return { prepared, launched, post, statuses };
}
