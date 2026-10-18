import assert from 'node:assert/strict';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, lstat, mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { build } from 'esbuild';
import {
  SOLUS_VERSION,
  SOLUS_RELEASE_SOURCE,
  SOLUS_FEED,
} from '../../../scripts/release/solus-package-contract.mjs';

const project =
  'runtime/tests/SolusCapacityFixture/AgenticPrReview.Runtime.SolusCapacityFixture.csproj';
const assembly = 'AgenticPrReview.Runtime.SolusCapacityFixture';
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
let root;
let stage = 'environment';
const receipts = [];
// Only this newly allocated private proof tree is made removable. Never follow
// snapshot symlinks or loosen permissions on another task's directories.
async function removeRoot(path) {
  const info = await lstat(path);
  if (info.isDirectory() && !info.isSymbolicLink()) {
    await chmod(path, 0o700);
    for (const entry of await readdir(path, { withFileTypes: true }))
      if (entry.isDirectory() && !entry.isSymbolicLink()) await removeRoot(join(path, entry.name));
  }
  await rm(path, { recursive: true, force: true });
}
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  const os = await readFile('/etc/os-release', 'utf8');
  assert.match(os, /^ID=ubuntu$/m);
  assert.match(os, /^VERSION_ID="24\.04"$/m);
  assert.equal(execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }), '');
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  const tree = execFileSync('git', ['rev-parse', 'HEAD^{tree}'], { encoding: 'utf8' }).trim();
  const packages = resolve(process.env.NUGET_PACKAGES);
  const cold = JSON.parse(await readFile(join(dirname(packages), 'metadata.json'), 'utf8'));
  assert.equal(cold.sourceCommit, head);
  assert.equal(cold.mode, 'verify');
  assert.equal(cold.coldCache, true);
  assert.equal(cold.constructed, true);
  assert.equal(cold.version, SOLUS_VERSION);
  assert.equal(cold.publishedSourceAnchor, SOLUS_RELEASE_SOURCE);
  root = await mkdtemp(join(resolve(process.env.RUNNER_TEMP), 'apr-v3-'));
  for (const part of ['home', 'tmp']) await mkdir(join(root, part), { mode: 0o700 });
  // Restore has already consumed the token. No ambient credential, NuGet home,
  // plugin or arbitrary option reaches any build, loader, bridge or Host child.
  const env = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(root, 'home'),
    DOTNET_CLI_HOME: join(root, 'home'),
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    TMPDIR: join(root, 'tmp'),
    NUGET_PACKAGES: packages,
  };
  const properties = [
    '-p:APRManagedPackage=true',
    '-p:APRReleaseVersion=v0.0.0-internal.1',
    '-p:APRBuildId=' + '0'.repeat(64),
    '-p:NuGetAudit=false',
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false',
    '-maxcpucount:1',
  ];
  const tool = (args) => {
    const result = spawnSync('dotnet', args, {
      env,
      encoding: 'utf8',
      timeout: 300_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.status !== 0) {
      const output = (result.stdout || '') + (result.stderr || '');
      console.error(
        JSON.stringify({
          stage,
          status: result.status,
          codes: [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)\d{4}\b/g) || [])],
        }),
      );
    }
    assert.equal(result.status, 0);
    return result.stdout;
  };
  stage = 'restore';
  tool([
    'restore',
    project,
    '-r',
    'linux-x64',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    ...properties,
  ]);
  const output = join(root, 'output');
  stage = 'build';
  tool([
    'build',
    project,
    '-r',
    'linux-x64',
    '-c',
    'Release',
    '--no-restore',
    '--nologo',
    '-o',
    output,
    ...properties,
  ]);
  stage = 'installed_identity';
  const lockBytes = await readFile(
    'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json',
  );
  const lock = JSON.parse(lockBytes).dependencies['net10.0'];
  const identities = [];
  for (const name of [
    'SolusAgent.Api',
    'SolusAgent.Tools.Api',
    'SolusAgent.Runtime.Api',
    'SolusAgent.Runtime',
    'SolusAgent.Providers.DeepSeek',
  ]) {
    const id = name.toLowerCase();
    const directory = join(packages, id, SOLUS_VERSION);
    const archive = await readFile(join(directory, `${id}.${SOLUS_VERSION}.nupkg`));
    assert.equal(lock[name].resolved, SOLUS_VERSION);
    assert.equal(createHash('sha512').update(archive).digest('base64'), lock[name].contentHash);
    const metadata = JSON.parse(await readFile(join(directory, '.nupkg.metadata'), 'utf8'));
    assert.equal(metadata.source.replace(/\/$/, ''), SOLUS_FEED);
    const original = spawnSync(
      '/usr/bin/unzip',
      ['-p', join(directory, `${id}.${SOLUS_VERSION}.nupkg`), `lib/net10.0/${name}.dll`],
      { env, maxBuffer: 64 * 1024 * 1024 },
    );
    assert.equal(original.status, 0);
    const loaded = await readFile(join(output, name + '.dll'));
    assert.equal(sha(original.stdout), sha(loaded));
    identities.push({
      id,
      version: SOLUS_VERSION,
      archive_sha256: sha(archive),
      assembly_sha256: sha(loaded),
    });
  }
  const dll = join(output, assembly + '.dll');
  stage = 'portable_installed_probes';
  const portable = JSON.parse(tool([dll, 'portable']));
  assert.equal(portable.proof, 'v3-portable');
  assert.ok(portable.checks >= 30);
  assert.equal(portable.live_calls, 0);
  assert.equal(portable.probes.length, 6);
  assert.deepEqual(
    portable.probes.map((probe) => probe.kind),
    [
      'selected-request',
      'selected-request',
      'selected-request',
      'cumulative-attempts-diagnostic',
      'candidate-records-diagnostic',
      'retained-bytes-diagnostic',
    ],
  );
  const byteProbe = portable.probes[5];
  assert.equal(byteProbe.refused_capacity, byteProbe.required_capacity - 1);
  assert.ok(byteProbe.required_capacity > byteProbe.actual_encoded_bytes);
  assert.equal(byteProbe.response_bound, 16384);
  stage = 'bridge';
  const bridgeBundle = join(root, 'bridge.mjs');
  await build({
    entryPoints: ['src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts'],
    outfile: bridgeBundle,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
  });
  const corpus = resolve('runtime/tests/fixtures/agent/r5/quality/bundle');
  const corpusHash = sha(await readFile(join(corpus, 'manifest.json')));
  assert.equal(corpusHash, '55a4fa8cca7f081169c4f1f09503c8a5c8f23197c1960ca09fb6266bcb7375b3');
  let scenarios = 0;
  const scenario = async (id, execute) => {
    const work = join(root, 'case' + scenarios++);
    for (const part of ['', 'bridge-staging', 'control'])
      await mkdir(join(work, part), { mode: 0o700 });
    const now = Math.floor(Date.now() / 1000);
    await writeFile(join(work, 'control', 'now'), String(now));
    const bridge = spawn(
      process.execPath,
      [
        bridgeBundle,
        join(work, 'bridge.sock'),
        'r4-h2-tests',
        join(work, 'bridge-staging'),
        join(work, 'control'),
      ],
      { env, stdio: ['ignore', 'pipe', 'pipe'] },
    );
    const lines = createInterface({ input: bridge.stdout });
    try {
      assert.equal(
        await Promise.race([
          once(lines, 'line').then(([line]) => line),
          new Promise((_, reject) =>
            setTimeout(() => reject(new Error('bridge_timeout')), 20000).unref(),
          ),
        ]),
        'READY',
      );
      const operations = async () => {
        try {
          return (await readFile(join(work, 'operations.jsonl'), 'utf8'))
            .trim()
            .split('\n')
            .filter(Boolean)
            .map(JSON.parse);
        } catch (error) {
          if (error.code === 'ENOENT') return [];
          throw error;
        }
      };
      const run = (
        round,
        mode = round === 0 ? 'seed' : 'review',
        config = 'omitted',
        variant = 'useful',
      ) => {
        writeFileSync(
          join(work, 'control', 'producing-run.json'),
          JSON.stringify({ id: String(7001 + round), attempt: '2' }),
        );
        const child = spawnSync(
          'dotnet',
          [dll, work, mode, id, String(now), corpus, String(round), config, variant],
          { env, encoding: 'utf8', timeout: 180000, maxBuffer: 1024 * 1024 },
        );
        if (child.status !== 0)
          console.error(
            JSON.stringify({
              stage,
              id,
              round,
              mode,
              variant,
              status: child.status,
              failures: (child.stderr || '')
                .split(/\r?\n/)
                .filter((line) => /^APR_SOLUS_CAPACITY_FAILED [a-z0-9_-]+$/.test(line)),
            }),
          );
        assert.equal(child.status, 0);
        const receipt = JSON.parse(child.stdout);
        assert.equal(receipt.native_creates, 0);
        assert.equal(receipt.round, round);
        receipt.group = stage;
        receipt.scenario_index = scenarios - 1;
        receipts.push(receipt);
        return receipt;
      };
      const captured = async (round) =>
        JSON.parse(await readFile(join(work, `capture-r${round}.json`), 'utf8'));
      await execute({ run, operations, captured });
    } finally {
      lines.close();
      if (bridge.exitCode === null) {
        bridge.kill('SIGTERM');
        await once(bridge, 'exit');
      }
      assert.equal(bridge.exitCode, 0);
    }
  };
  function completed(receipt, correction = false) {
    assert.equal(receipt.accepted, true);
    assert.equal(receipt.clean_review, true);
    assert.equal(receipt.captured, true);
    assert.equal(receipt.current_attempts, receipt.sends);
    assert.equal(receipt.current_logical_calls, receipt.sends);
    assert.equal(receipt.current_model_work_units, receipt.sends);
    assert.equal(receipt.current_input_tokens, receipt.sends * 8192);
    assert.equal(receipt.current_output_tokens, receipt.sends * 512);
    assert.equal(receipt.input_allowance, 65536);
    assert.equal(receipt.output_allowance, 8192);
    assert.equal(receipt.input_remaining, 65536 - receipt.current_input_tokens);
    assert.equal(receipt.output_remaining, 8192 - receipt.current_output_tokens);
    assert.equal(receipt.input_reservation, 8192);
    assert.equal(receipt.output_reservation, 1024);
    if (correction) {
      assert.equal(receipt.tools_admitted, 6);
      assert.equal(receipt.tools_invoked, 4);
    } else assert.equal(receipt.tools_admitted, receipt.tools_invoked);
    assert.ok(receipt.retained.candidate_record_inventory <= 64);
    assert.equal(
      receipt.retained.candidate_record_inventory,
      receipt.retained.ordinary_records +
        receipt.retained.candidate_episodes +
        receipt.retained.candidate_submissions +
        receipt.retained.recovery_actions,
    );
    assert.ok(receipt.retained.encoded_bytes <= 196608);
    for (const exchange of receipt.exchanges) {
      assert.equal(exchange.response_allowance, 16384);
      assert.ok(exchange.inputs <= 32);
    }
    assert.equal(receipt.wire.length, receipt.sends);
    for (const wire of receipt.wire) {
      assert.equal(wire.roles.system, 1);
      assert.ok(wire.messages > 1);
    }
  }
  function prefix(before, after) {
    assert.deepEqual(after.Records.slice(1, before.Records.length), before.Records.slice(1));
    for (const key of ['Rounds', 'Members'])
      assert.deepEqual(after[key].slice(0, before[key].length), before[key]);
    for (const key of ['Episodes', 'Submissions'])
      assert.deepEqual(
        after.Candidates[key].slice(0, before.Candidates[key].length),
        before.Candidates[key],
      );
    assert.equal(after.Records[0].Kind, before.Records[0].Kind);
    assert.notDeepEqual(after.Info, before.Info);
  }
  stage = 'useful_fresh_and_restored';
  for (const id of ['cs-defect', 'cs-safe', 'ts-defect', 'ts-safe', 'repository-rule'])
    await scenario(id, async ({ run, captured }) => {
      const first = run(0);
      completed(first);
      const second = run(1);
      completed(second);
      assert.equal(second.admission, 'Supplied');
      assert.equal(second.historical_attempts, first.current_attempts);
      assert.equal(second.historical_input_tokens, first.current_input_tokens);
      assert.equal(second.historical_output_tokens, first.current_output_tokens);
      assert.equal(second.retained.attempts, first.current_attempts + second.current_attempts);
      assert.ok(second.wire_request_bytes > first.wire_request_bytes);
      assert.ok(second.retained.encoded_bytes > first.retained.encoded_bytes);
      assert.ok(second.exchanges[0].historical_tool_results > 0);
      prefix(await captured(0), await captured(1));
    });
  stage = 'consecutive_useful_growth';
  await scenario('repository-rule', async ({ run, operations, captured }) => {
    let prior;
    let successes = 0;
    let refusal;
    for (let round = 0; round < 4; round++) {
      const before = await operations();
      const receipt = run(round);
      if (!receipt.accepted) {
        assert.equal(receipt.clean_review, false);
        assert.ok(
          receipt.admission === 'Rejected' || receipt.termination,
          'a state/bridge fixture failure is not capacity evidence',
        );
        assert.equal(receipt.controlled_transport_failure, 'none');
        assert.ok(
          ['StateConflict', 'AgentResultInvalid', 'ProviderFailed'].includes(receipt.status),
        );
        const added = (await operations()).slice(before.length);
        assert.equal(
          added.filter(
            (row) => row.operation === 'upload' && row.kind === 'acceptance' && row.committed,
          ).length,
          0,
        );
        refusal = receipt;
        break;
      }
      completed(receipt);
      const current = await captured(round);
      if (prior) prefix(prior, current);
      prior = current;
      successes++;
    }
    assert.ok(successes >= 2);
    assert.ok(refusal, 'bounded retained growth must expose a real refusal');
  });
  stage = 'tool_pressure';
  for (const variant of [
    'six-tools',
    'seven-tools',
    'escaped',
    'result-over',
    'batch-three',
    'batch-four',
    'seven-calls',
    'correction',
  ])
    await scenario('cs-defect', async ({ run, captured }) => {
      const receipt = run(0, 'seed', 'omitted', variant);
      if (variant === 'six-tools' || variant === 'escaped') {
        completed(receipt);
        assert.equal(receipt.sends, 6);
        assert.equal(receipt.tools_invoked, 6);
        if (variant === 'escaped') {
          assert.ok(receipt.exchanges.some((row) => row.largest_tool_result_bytes > 2500));
          assert.ok(
            receipt.wire.some(
              (row) =>
                row.tool_message_wire_value_bytes >
                row.tool_message_content_bytes + 2 * row.tool_messages,
            ),
          );
        }
      } else if (variant === 'batch-three') {
        completed(receipt);
        assert.equal(receipt.sends, 3);
        assert.equal(receipt.tools_invoked, 4);
      } else if (variant === 'correction') {
        completed(receipt, true);
        assert.equal(receipt.sends, 6);
        const context = JSON.stringify(await captured(0));
        assert.ok(context.includes('_apr_rejected'));
        assert.ok(!context.includes('V3_REJECTED_ARG'));
        assert.ok(!context.includes(sha(Buffer.from('{"path":"../V3_REJECTED_ARG"}'))));
      } else {
        assert.equal(receipt.accepted, false);
        assert.equal(receipt.clean_review, false);
        if (variant === 'result-over') {
          assert.equal(receipt.tools_invoked, 5);
          assert.equal(receipt.current_observations, 4);
        }
        if (variant === 'seven-tools') assert.equal(receipt.tools_invoked, 4);
        if (variant === 'batch-four') assert.equal(receipt.tools_invoked, 0);
        if (variant === 'seven-calls') {
          assert.equal(receipt.sends, 6);
          assert.equal(receipt.tools_invoked, 6);
        }
      }
    });
  stage = 'explicit_profile_admission';
  const bad = [
    '{"maxModelCalls":7}',
    '{"timeoutSeconds":121}',
    '{"maxUncachedInputTokens":65537}',
    '{"maxCachedInputTokens":65537}',
    '{"maxOutputTokens":8193}',
    '{"maxModelCalls":1,"maxOutputTokens":8193}',
    '{"maxModelCalls":null}',
    '{"maxModelCalls":1.5}',
    '{"maxModelCalls":1,"maxModelCalls":2}',
    '{"profile":"current"}',
  ];
  for (const mode of ['ordinary', 'seed', 'review'])
    for (const config of bad)
      await scenario('cs-defect', async ({ run, operations }) => {
        if (mode === 'review') completed(run(0));
        const before = await operations();
        const receipt = run(mode === 'review' ? 1 : 0, mode, config);
        assert.equal(receipt.accepted, false);
        assert.equal(receipt.clean_review, false);
        assert.equal(receipt.sends, 0);
        if (bad.indexOf(config) < 6) {
          if (mode === 'review') {
            // Changed policy can select a different lineage before new-work
            // validation. Preserve recovery/classification priority.
            assert.ok(
              receipt.preparation_failure === 'InputProfileRejected' ||
                receipt.status === 'StateConflict',
            );
          } else assert.equal(receipt.input_failure, 'InputProfileRejected');
        }
        const added = (await operations()).slice(before.length);
        assert.equal(
          added.filter(
            (row) =>
              row.operation === 'upload' &&
              ['context_claim', 'candidate', 'acceptance'].includes(row.kind) &&
              row.committed,
          ).length,
          0,
        );
        if (mode === 'review') completed(run(1));
      });
  stage = 'explicit_exact_profile';
  await scenario('cs-defect', async ({ run, captured }) => {
    const config =
      '{"maxModelCalls":6,"timeoutSeconds":120,"maxUncachedInputTokens":65536,"maxCachedInputTokens":65536,"maxOutputTokens":8192}';
    completed(run(0, 'seed', config));
    completed(run(1, 'review', config));
    prefix(await captured(0), await captured(1));
  });
  stage = 'lower_exact_and_exhaustion';
  for (const [config, expectedSends] of [
    ['{"maxModelCalls":1}', 1],
    ['{"maxOutputTokens":1024}', 1],
    ['{"maxUncachedInputTokens":4096,"maxCachedInputTokens":4096}', 1],
    ['{"maxUncachedInputTokens":1,"maxCachedInputTokens":1}', 0],
    ['{"maxOutputTokens":1}', 0],
    ['{"maxUncachedInputTokens":1}', 1],
  ])
    await scenario('cs-defect', async ({ run }) => {
      const receipt = run(0, 'seed', config);
      assert.equal(receipt.sends, expectedSends);
      assert.equal(receipt.accepted, false);
      assert.equal(receipt.clean_review, false);
      assert.equal(
        receipt.termination,
        config.includes('maxModelCalls') ? 'ResourceLimit' : 'TokenLimit',
      );
      if (config.includes('1024')) assert.equal(receipt.output_allowance, 1024);
      if (config.includes('4096')) assert.equal(receipt.input_allowance, 8192);
    });
  stage = 'cancellation_and_input_boundaries';
  for (const variant of [
    'cancel-before',
    'cancel-provider',
    'cancel-tool',
    'cancel-capture',
    'deadline',
    'data-over',
    'input-count-over',
  ])
    for (const restored of [false, true])
      await scenario('cs-defect', async ({ run, operations }) => {
        const config = variant === 'deadline' ? '{"timeoutSeconds":1}' : 'omitted';
        if (restored) completed(run(0, 'seed', config));
        const before = await operations();
        const receipt = run(restored ? 1 : 0, restored ? 'review' : 'seed', config, variant);
        assert.equal(receipt.accepted, false);
        if (variant !== 'cancel-capture') assert.equal(receipt.clean_review, false);
        if (['cancel-before', 'data-over', 'input-count-over'].includes(variant))
          assert.equal(receipt.sends, 0);
        assert.equal(
          (await operations())
            .slice(before.length)
            .filter(
              (row) => row.operation === 'upload' && row.kind === 'acceptance' && row.committed,
            ).length,
          0,
        );
      });
  await scenario('cs-defect', async ({ run }) =>
    completed(run(0, 'seed', 'omitted', 'data-exact')),
  );
  stage = 'existing_commit_recovery_priority';
  for (const pendingCleanup of [false, true])
    await scenario('cs-defect', async ({ run, operations }) => {
      completed(run(0));
      completed(run(1, pendingCleanup ? 'fail-cleanup' : 'review'));
      const before = await operations();
      const recovered = run(1, 'recover', 'omitted', 'recovery-input-over');
      assert.equal(recovered.accepted, true);
      assert.equal(recovered.recovered_without_work, true);
      assert.equal(recovered.sends, 0);
      assert.equal(recovered.imports, 0);
      assert.equal(recovered.accounting_zero, true);
      assert.equal(
        (await operations())
          .slice(before.length)
          .filter(
            (row) => row.operation === 'upload' && row.kind === 'context_claim' && row.committed,
          ).length,
        0,
      );
    });
  assert.equal(execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim(), head);
  assert.equal(execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }), '');
  assert.equal(scenarios, 68, 'complete authored scenario inventory');
  assert.ok(receipts.length >= 107 && receipts.length <= 108);
  const report = {
    proof: 'APR_SOLUS_V3_CAPACITY',
    source_commit: head,
    source_tree: tree,
    pin: SOLUS_VERSION,
    published_source: SOLUS_RELEASE_SOURCE,
    profile: 'cr1-solus-internal4-dev',
    cold_cache: true,
    lock_sha256: sha(lockBytes),
    corpus_sha256: corpusHash,
    packages: identities,
    portable,
    scenarios,
    receipts,
  };
  const serialized = JSON.stringify(report);
  assert.ok(!/PRIVATE|classified|reasoning_content|arguments_json/.test(serialized));
  console.log(serialized);
  if (process.env.R7_CAPACITY_REPORT_DIR) {
    await mkdir(resolve(process.env.R7_CAPACITY_REPORT_DIR), { recursive: true });
    await writeFile(
      join(resolve(process.env.R7_CAPACITY_REPORT_DIR), 'managed.json'),
      serialized + '\n',
    );
  }
  await removeRoot(root);
  root = undefined;
  console.log('APR_R7_CAPACITY_MANAGED_PASSED installed=true live_calls=0 cleanup=verified');
} catch {
  console.error(JSON.stringify({ stage, last_receipt: receipts.at(-1) }));
  console.error('APR_SOLUS_CAPACITY_GATE_FAILED stage=' + stage);
  process.exitCode = 1;
} finally {
  if (root) await removeRoot(root);
}
