// NEW R7-D0 exact-executable supervisor. No linked Host, fake provider or test
// composition is loaded into the child. Complete successful review is V2-owned.
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { setTimeout as delay } from 'node:timers/promises';

const [mode, executable, assembly] = process.argv.slice(2);
assert.ok(mode === 'framework' || mode === 'native');
assert.ok(executable && (mode !== 'framework' || assembly));
const command = resolve(executable);
const prefix = mode === 'framework' ? [resolve(assembly)] : [];
const root = await mkdtemp(join(tmpdir(), 'apr-d0-exact-'));
const providerCanary = 'PRIVATE_D0_PROVIDER_CANARY';
const stateCanary = Buffer.alloc(32, 71).toString('base64');
const privateCanary = 'PRIVATE_D0_LAUNCH_CANARY';
const canaries = [providerCanary, stateCanary, privateCanary, root];
const eventBytes = Buffer.from(
  JSON.stringify({
    inputs: { 'pr-number': '341' },
    repository: { id: 42, full_name: 'SolusQuest/agentic-pr-review' },
    sender: { id: 7, login: 'maintainer' },
  }),
);
const eventPath = join(root, 'event.json');
const launch = {
  protocol: 'apr.cli.run.cr2',
  build_discriminator: 'r7-d0',
  inputs: [
    { name: 'github-token', value: '' },
    { name: 'provider-api-key', value: providerCanary },
    { name: 'state-key', value: stateCanary },
    { name: 'previous-state-key', value: '' },
    { name: 'config-path', value: privateCanary },
    { name: 'pr-number', value: '341' },
    { name: 'state-mode', value: '' },
  ],
  facts: {
    event_json_path: eventPath,
    event_json_sha256: createHash('sha256').update(eventBytes).digest('hex'),
    repository_name: 'SolusQuest/agentic-pr-review',
    repository_id: '42',
    run_id: '900',
    run_attempt: '1',
    workflow_path: '.github/workflows/r4-trusted-proof.yml',
    workflow_ref:
      'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main',
    workflow_sha: 'a'.repeat(40),
    action_source_sha: 'b'.repeat(40),
    payload_sha256: 'f'.repeat(64),
  },
  cancellation: 'active',
  upload_endpoint: `http://127.0.0.1:1/${privateCanary}`,
};
const numericFields = [
  'model_calls',
  'provider_attempts',
  'provider_retries',
  'provider_failed_attempts',
  'provider_unknown_usage_attempts',
  'provider_unknown_cache_partition_attempts',
  'input_tokens',
  'input_cache_hit_tokens',
  'input_cache_miss_tokens',
  'output_tokens',
];
let cases = 0;
let signalCases = 0;

function frame(bytes) {
  const header = Buffer.alloc(4);
  header.writeUInt32BE(bytes.length);
  return Buffer.concat([header, bytes]);
}

function launchFrame(value = launch) {
  return frame(Buffer.from(JSON.stringify(value)));
}

async function run(bytes, { signal, args = ['run'] } = {}) {
  // Native execution has neither PATH nor DOTNET_ROOT. The supervisor's SDK
  // and Node dependencies are CI tools, not dependencies of the native child.
  const env = { TMPDIR: root, TMP: root, TEMP: root };
  if (process.platform === 'win32') {
    env.SystemRoot = process.env.SystemRoot;
    env.WINDIR = process.env.WINDIR;
  }
  const child = spawn(command, [...prefix, ...args], {
    cwd: root,
    env,
    stdio: ['pipe', 'pipe', 'pipe'],
    windowsHide: true,
  });
  const stdout = [];
  const stderr = [];
  let captured = 0;
  let excessive = false;
  const capture = (sink) => (chunk) => {
    captured += chunk.length;
    if (captured > 20 * 1024) {
      excessive = true;
      child.kill('SIGKILL');
    } else sink.push(chunk);
  };
  child.stdout.on('data', capture(stdout));
  child.stderr.on('data', capture(stderr));
  child.stdin.on('error', () => {});
  const closed = new Promise((resolveClose, reject) => {
    child.once('error', reject);
    child.once('close', (code, exitSignal) => resolveClose({ code, exitSignal }));
  });
  let timedOut = false;
  const timeout = setTimeout(() => {
    timedOut = true;
    child.kill('SIGKILL');
  }, 15_000);
  try {
    if (signal) {
      // A partial header keeps the actual binary blocked in boundary framing.
      child.stdin.write(bytes);
      await delay(1000);
      assert.equal(child.exitCode, null);
      child.kill(signal);
    } else child.stdin.end(bytes);
    const result = await closed;
    assert.equal(timedOut, false);
    assert.equal(excessive, false);
    const out = Buffer.concat(stdout);
    const error = Buffer.concat(stderr).toString('utf8');
    for (const canary of canaries) {
      assert.equal(out.includes(Buffer.from(canary)), false, 'private data in stdout');
      assert.equal(error.includes(canary), false, 'private data in stderr');
    }
    assert.equal(result.exitSignal, null);
    cases++;
    return { ...result, out, error };
  } finally {
    clearTimeout(timeout);
    child.stdin.destroy();
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
  }
}

function assertCompletion(result, status) {
  assert.equal(result.code, 1);
  assert.equal(result.error, '');
  assert.ok(result.out.length > 4);
  const length = result.out.readUInt32BE();
  assert.ok(length > 0 && length <= 16 * 1024);
  assert.equal(result.out.length, length + 4);
  const completion = JSON.parse(result.out.subarray(4).toString('utf8'));
  assert.deepEqual(Object.keys(completion).sort(), [
    'annotations',
    'build_discriminator',
    'outputs',
    'process_exit_code',
    'protocol',
    'summary',
  ]);
  assert.equal(completion.protocol, 'apr.cli.result.cr2');
  assert.equal(completion.build_discriminator, 'r7-d0');
  assert.equal(completion.outputs.status, status);
  assert.equal(completion.process_exit_code, result.code);
  assert.equal(completion.outputs['termination-reason'], 'not_started');
  assert.equal(typeof completion.summary, 'string');
  assert.ok(completion.summary.includes('State disposition: not_committed\n'));
  assert.ok(completion.summary.includes('Publication URL: Not available\n'));
  assert.deepEqual(
    Object.keys(completion.outputs).sort(),
    [
      'status',
      'termination-reason',
      ...numericFields.map((name) => name.replaceAll('_', '-')),
      'attempt-accounting-completeness',
      'usage-completeness',
    ].sort(),
  );
  for (const name of numericFields)
    assert.equal(completion.outputs[name.replaceAll('_', '-')], '0');
  assert.equal(completion.outputs['attempt-accounting-completeness'], 'complete');
  assert.equal(completion.outputs['usage-completeness'], 'complete');
  assert.equal(completion.annotations.length, 1);
  assert.deepEqual(Object.keys(completion.annotations[0]).sort(), ['code', 'message', 'severity']);
  assert.equal(completion.annotations[0].code, status);
  assert.equal(completion.annotations[0].severity, 'error');
  assert.equal(typeof completion.annotations[0].message, 'string');
}

try {
  await writeFile(eventPath, eventBytes);
  assertCompletion(await run(launchFrame()), 'credentials_missing', 'authorization');
  assertCompletion(
    await run(launchFrame({ ...launch, cancellation: 'requested' })),
    'cancelled',
    'cancellation',
  );
  assertCompletion(
    await run(
      launchFrame({ ...launch, facts: { ...launch.facts, event_json_sha256: '0'.repeat(64) } }),
    ),
    'authorization_failed',
    'authorization',
  );
  assertCompletion(
    await run(launchFrame({ ...launch, inputs: [] })),
    'authorization_failed',
    'authorization',
  );
  for (const inputs of [
    [
      { name: 'pr-number', value: '341' },
      { name: 'config-path', value: '' },
      { name: 'state-mode', value: '' },
    ],
  ])
    assertCompletion(
      await run(launchFrame({ ...launch, inputs })),
      'credentials_missing',
      'authorization',
    );
  const invalidProductInputs = [
    ...['0', '01', '+1', '-1', '1.0', '9223372036854775808', ' 1 '].map((value) => [
      { name: 'pr-number', value },
    ]),
    [{ name: 'state-mode', value: 'AUTO' }],
    [{ name: 'config-path', value: 'bad\u0000path' }],
    [{ name: 'state-key', value: 'é'.repeat(2049) }],
    ['github-token', 'provider-api-key', 'state-key', 'previous-state-key'].map((name) => ({
      name,
      value: 'x'.repeat(4096),
    })),
    [{ name: 'unknown', value: '' }],
    [
      { name: 'state-mode', value: '' },
      { name: 'state-mode', value: 'auto' },
    ],
    Array.from({ length: 8 }, () => ({ name: 'state-mode', value: '' })),
  ];
  for (const inputs of invalidProductInputs)
    assertCompletion(
      await run(launchFrame({ ...launch, inputs })),
      'configuration_invalid',
      'contract',
    );
  const valid = launchFrame();
  const oversized = Buffer.alloc(4);
  oversized.writeUInt32BE(159_543);
  const text = JSON.stringify(launch);
  const invalid = [
    Buffer.alloc(0),
    Buffer.from([0, 0, 1]),
    Buffer.alloc(4),
    oversized,
    valid.subarray(0, valid.length - 1),
    Buffer.concat([valid, Buffer.from([0])]),
    Buffer.concat([valid, valid]),
    frame(Buffer.from([0xff])),
    frame(Buffer.from(privateCanary)),
    frame(Buffer.from(`{"unknown":"${privateCanary}",${text.slice(1)}`)),
    frame(
      Buffer.from(
        text.replace(
          '"build_discriminator":"r7-d0"',
          '"build_discriminator":"r7-d0","build_discriminator":"r7-d0"',
        ),
      ),
    ),
    launchFrame({ ...launch, protocol: 'wrong-protocol' }),
    launchFrame({ ...launch, inputs: [{ name: 'state-key', value: null }] }),
    launchFrame({ ...launch, inputs: [{ name: 'state-key', value: 1 }] }),
    launchFrame({ ...launch, inputs: [{ name: 'state-key' }] }),
    launchFrame({ ...launch, inputs: [{ name: 'state-key', value: '\ud800' }] }),
    launchFrame({
      ...launch,
      facts: { ...launch.facts, run_id: '0' },
      inputs: [{ name: 'pr-number', value: '0' }],
    }),
    launchFrame({ ...launch, build_discriminator: 'wrong-build' }),
    launchFrame({ ...launch, build_discriminator: 'r4-w2' }),
    launchFrame({ ...launch, build_discriminator: 'r4-h1' }),
  ];
  for (const bytes of invalid) {
    const result = await run(bytes);
    assert.equal(result.code, 1);
    assert.equal(result.out.length, 0);
    assert.equal(result.error.trim(), 'APR_ACTION_HOST_INPUT_INVALID');
  }
  for (const args of [
    [],
    ['run', '--extra'],
    ['run', 'run'],
    ['barrier'],
    ['supervise'],
    ['action-host', '--fake-provider'],
  ]) {
    const result = await run(Buffer.alloc(0), { args });
    assert.equal(result.code, 2);
    assert.equal(result.out.length, 0);
    assert.equal(result.error.startsWith('APR_USAGE_INVALID:'), true);
  }
  if (process.platform === 'linux') {
    for (const signal of ['SIGTERM', 'SIGINT']) {
      const result = await run(Buffer.from([0]), { signal });
      assert.equal(result.code, 1);
      assert.equal(result.out.length, 0);
      assert.equal(result.error.trim(), 'APR_ACTION_HOST_CANCELLED');
      signalCases++;
    }
  } else console.log('D0_SIGNAL_CASES_SKIPPED_NON_LINUX');
  console.log(`D0_EXACT_EXECUTABLE mode=${mode} cases=${cases} signal_cases=${signalCases}`);
} finally {
  await rm(root, { recursive: true, force: true });
}
