// Process-level fixture only. Actual Runner $/ and output mapping are qualified separately.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { appendFile, readFile, writeFile, chmod } from 'node:fs/promises';
import { join } from 'node:path';
const [repo, action] = process.argv.slice(2);
const config = JSON.parse(await readFile(process.env.APR_D4_FIXTURE_CONFIG, 'utf8'));
const preload = join(repo, 'tests/distribution/managed-generated-action-preload.mjs');
const mechanical = { ...process.env };
for (const key of Object.keys(mechanical)) if (key.startsWith('INPUT_')) delete mechanical[key];
const prepareOutput = process.env.GITHUB_OUTPUT + '.prepare';
const state = process.env.GITHUB_OUTPUT + '.state';
await writeFile(prepareOutput, '');
await writeFile(state, '');
function values(text) {
  const rows = text.trimEnd().split('\n');
  if (!text) return {};
  const result = {};
  for (let index = 0; index < rows.length; index += 3) {
    const [name, delimiter] = rows[index].split('<<');
    assert.equal(rows[index + 2], delimiter);
    assert.ok(!Object.hasOwn(result, name));
    result[name] = rows[index + 1];
  }
  return result;
}
async function phase(entry, env) {
  const child = spawn(
    process.execPath,
    ['--import', preload, join(action, 'internal/dist', entry)],
    {
      env,
      stdio: ['ignore', 'pipe', 'pipe'],
    },
  );
  child.stdout.pipe(process.stdout);
  child.stderr.pipe(process.stderr);
  // The outer fixture owns the whole process group. Cancellation targets the real Node Action PID.
  return new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('close', (code, signal) => resolve(signal || code === null ? 1 : code));
  });
}
const prepared = await phase('prepare.js', {
  ...mechanical,
  APR_FIXTURE_PHASE: 'prepare',
  GITHUB_OUTPUT: prepareOutput,
  GITHUB_STATE: state,
});
await appendFile(
  config.audit,
  JSON.stringify({ kind: 'preparation-close', code: prepared }) + '\n',
);
let launched = prepared;
if (prepared === 0) {
  const output = values(await readFile(prepareOutput, 'utf8'));
  assert.deepEqual(Object.keys(output), ['candidate']);
  const candidate = JSON.parse(output.candidate);
  const recordPath = join(candidate.tempRoot, 'candidate.json');
  const record = JSON.parse(await readFile(recordPath, 'utf8'));
  const fault = config.candidateFault;
  if (fault === 'tree' || fault === 'runtime') {
    const target =
      fault === 'tree'
        ? join(
            candidate.tempRoot,
            record.payloadRoot,
            'agentic-pr-review/AgenticPrReview.Runtime.deps.json',
          )
        : join(candidate.tempRoot, record.runtimeRoot, 'host/fxr/10.0.13/libhostfxr.so');
    const original = await readFile(target);
    original[original.length - 1] ^= 1;
    await chmod(target, 0o600);
    await writeFile(target, original);
    await chmod(target, fault === 'tree' ? 0o400 : 0o644);
  } else if (fault === 'record') {
    record.runAttempt = '99';
    await chmod(recordPath, 0o600);
    await writeFile(recordPath, JSON.stringify(record));
    await chmod(recordPath, 0o400);
  } else if (fault === 'map') {
    const file = join(action, 'payload-map.json');
    const map = JSON.parse(await readFile(file, 'utf8'));
    map.payload.archiveSha256 = '0'.repeat(64);
    await writeFile(file, JSON.stringify(map));
  } else if (fault === 'asset')
    await appendFile(
      join(action, 'internal/dist/prepare.js'),
      '\n// substituted candidate source\n',
    );
  const launchEnvironment = { ...process.env, APR_PREPARED_CANDIDATE: output.candidate };
  if (fault === 'capability') delete launchEnvironment.ACTIONS_RUNTIME_TOKEN;
  launched = await phase('index.js', launchEnvironment);
}
const saved = values(await readFile(state, 'utf8'));
const cleaned = await phase('cleanup.js', {
  ...mechanical,
  APR_FIXTURE_PHASE: 'post',
  'STATE_apr-candidate': saved['apr-candidate'] ?? '',
});
process.exitCode = prepared || launched || cleaned;
