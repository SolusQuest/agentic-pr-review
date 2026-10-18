// Installed-package proof; no source references, credentials, provider network or
// live GitHub mutations. One real bridge survives multiple fresh Host processes.
import assert from 'node:assert/strict';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, lstat, mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { rmSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { build } from 'esbuild';
import { SOLUS_VERSION } from '../../../scripts/release/solus-package-contract.mjs';

let stage = 'environment';
let root;
let proofSummary;
// Crashed Hosts deliberately cannot dispose sealed snapshots. After every child
// has exited, reopen only this driver's private directories for teardown. Never
// follow a symlink into a location outside the newly created proof root.
const removeProofRoot = async (directory) => {
  const info = await lstat(directory);
  if (info.isDirectory() && !info.isSymbolicLink()) {
    await chmod(directory, 0o700);
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      if (entry.isDirectory() && !entry.isSymbolicLink())
        await removeProofRoot(join(directory, entry.name));
    }
  }
  await rm(directory, { recursive: true, force: true });
};
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  const os = await readFile('/etc/os-release', 'utf8');
  assert.match(os, /^ID=ubuntu$/m);
  assert.match(os, /^VERSION_ID="24\.04"$/m);
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  assert.match(head, /^[a-f0-9]{40}$/);
  root = await mkdtemp(join(resolve(process.env.RUNNER_TEMP), 'apr-s4-'));
  for (const part of ['home', 'tmp']) await mkdir(join(root, part), { mode: 0o700 });
  const env = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(root, 'home'),
    DOTNET_CLI_HOME: join(root, 'home'),
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    TMPDIR: join(root, 'tmp'),
    NUGET_PACKAGES: resolve(process.env.NUGET_PACKAGES),
  };
  const fixture =
    'runtime/tests/ActionHostTrustedProofVerifier/SolusRestored/AgenticPrReview.Runtime.SolusRestoredHostProof.csproj';
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
      const codes = [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)\d{4}\b/g) || [])];
      const diagnostics =
        args[0] === 'build'
          ? [
              ...new Set(
                output
                  .split(/\r?\n/)
                  .filter((line) => line.includes('/runtime/') && /error CS\d{4}:/.test(line))
                  .map((line) => line.slice(line.indexOf('/runtime/') + 1, 2048)),
              ),
            ].slice(0, 30)
          : [];
      console.error(JSON.stringify({ stage, status: result.status, codes, diagnostics }));
    }
    assert.equal(result.status, 0);
  };
  stage = 'restore';
  tool([
    'restore',
    fixture,
    '-r',
    'linux-x64',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    ...properties,
  ]);
  const output = join(root, 'proof');
  stage = 'build';
  tool([
    'build',
    fixture,
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
  stage = 'assembly_identity';
  for (const name of [
    'SolusAgent.Api',
    'SolusAgent.Tools.Api',
    'SolusAgent.Runtime.Api',
    'SolusAgent.Runtime',
    'SolusAgent.Providers.DeepSeek',
  ]) {
    const loaded = await readFile(join(output, name + '.dll'));
    const installed = await readFile(
      join(env.NUGET_PACKAGES, name.toLowerCase(), SOLUS_VERSION, 'lib', 'net10.0', name + '.dll'),
    );
    assert.equal(
      createHash('sha256').update(loaded).digest('hex'),
      createHash('sha256').update(installed).digest('hex'),
    );
  }
  stage = 'bridge_bundle';
  const bridgeBundle = join(root, 'bridge.mjs');
  await build({
    entryPoints: ['src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts'],
    outfile: bridgeBundle,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
  });
  const dll = join(output, 'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll');
  const corpus = resolve('runtime/tests/fixtures/agent/r5/quality/bundle');
  let scenarios = 0,
    hosts = 0;
  const scenario = async (id, execute) => {
    const work = join(root, 'c' + scenarios++);
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
      const ready = await Promise.race([
        once(lines, 'line').then(([line]) => line),
        new Promise((_, reject) =>
          setTimeout(() => reject(new Error('bridge_ready_timeout')), 20_000).unref(),
        ),
      ]);
      assert.equal(ready, 'READY');
      const run = (mode, expectedExit = 0) => {
        hosts++;
        const producerFile = join(work, 'control', 'producing-run.json');
        if (mode === 'seed-default-meta') rmSync(producerFile, { force: true });
        else if (mode === 'invalid-producer-json') writeFileSync(producerFile, '{');
        else if (mode === 'invalid-producer-extra')
          writeFileSync(producerFile, JSON.stringify({ id: '7002', attempt: '2', extra: true }));
        else
          writeFileSync(
            producerFile,
            JSON.stringify({
              id:
                mode === 'invalid-producer'
                  ? '-1'
                  : mode === 'wrong-producer'
                    ? '9009'
                    : mode.startsWith('seed')
                      ? '7001'
                      : '7002',
              attempt: mode === 'retry' ? '3' : '2',
            }),
          );
        const result = spawnSync('dotnet', [dll, work, mode, id, String(now), corpus], {
          env,
          encoding: 'utf8',
          timeout: 180_000,
          maxBuffer: 1024 * 1024,
        });
        if (result.status !== expectedExit) {
          const failures = (result.stderr || '')
            .split(/\r?\n/)
            .filter((line) => /^APR_SOLUS_RESTORED_FAILED [a-z0-9_-]+$/.test(line));
          console.error(JSON.stringify({ stage, mode, status: result.status, failures }));
        }
        assert.equal(result.status, expectedExit);
        if (expectedExit === 73) return null;
        const rows = result.stdout.trim().split(/\r?\n/);
        assert.equal(rows.length, 1);
        const observed = JSON.parse(rows[0]);
        assert.equal(observed.native_creates, 0);
        assert.equal(observed.mode, mode);
        return observed;
      };
      const operations = async () =>
        (await readFile(join(work, 'operations.jsonl'), 'utf8'))
          .trim()
          .split('\n')
          .map((line) => JSON.parse(line));
      const race = async () => {
        writeFileSync(
          join(work, 'control', 'producing-run.json'),
          JSON.stringify({ id: '7002', attempt: '2' }),
        );
        const start = () =>
          new Promise((resolveResult, reject) => {
            hosts++;
            const child = spawn('dotnet', [dll, work, 'race', id, String(now), corpus], {
              env,
              stdio: ['ignore', 'pipe', 'pipe'],
              timeout: 180_000,
            });
            let stdout = '',
              bytes = 0,
              failed = false;
            child.on('error', () => {
              failed = true;
            });
            child.stdout.on('data', (chunk) => {
              bytes += chunk.length;
              if (bytes > 1024 * 1024) {
                failed = true;
                child.kill('SIGKILL');
              } else stdout += chunk;
            });
            child.stderr.on('data', (chunk) => {
              bytes += chunk.length;
              if (bytes > 1024 * 1024) {
                failed = true;
                child.kill('SIGKILL');
              }
            });
            child.on('close', (code) => {
              try {
                assert.equal(failed, false);
                assert.equal(code, 0);
                const observed = JSON.parse(stdout.trim());
                assert.equal(observed.native_creates, 0);
                resolveResult(observed);
              } catch {
                reject(new Error('race_host_failed'));
              }
            });
          });
        const results = await Promise.allSettled([start(), start()]);
        for (const result of results) if (result.status === 'rejected') throw result.reason;
        return results.map((result) => result.value);
      };
      await execute({ run, operations, work, race });
      console.error(JSON.stringify({ proof: 'APR_SOLUS_RESTORED_CASE', stage, passed: true }));
    } catch (error) {
      const events = await readFile(join(work, 'operations.jsonl'), 'utf8').catch(() => '');
      const counts = {};
      for (const line of events.trim().split('\n').filter(Boolean)) {
        const event = JSON.parse(line);
        const key = [
          event.operation,
          event.kind || 'none',
          event.committed ?? event.written ?? 'none',
        ].join(':');
        counts[key] = (counts[key] || 0) + 1;
      }
      console.error(JSON.stringify({ stage, operation_counts: counts }));
      throw error;
    } finally {
      lines.close();
      bridge.kill('SIGTERM');
      if (bridge.exitCode === null && bridge.signalCode === null) await once(bridge, 'exit');
    }
  };
  for (const id of ['repository-rule', 'cs-defect', 'cs-safe', 'ts-defect', 'ts-safe']) {
    stage = 'same_head_' + id;
    await scenario(id, async ({ run, operations, work }) => {
      const first = run('seed');
      assert.equal(first.accepted, true);
      const second = run('review');
      assert.equal(second.accepted, true);
      assert.equal(second.admission, 'Supplied');
      assert.equal(second.historical_attempts, first.current_attempts);
      assert.equal(second.current_attempts, second.sends);
      assert.equal(second.input_reservation, 8192);
      assert.equal(second.output_reservation, 1024);
      assert.equal(second.input_tokens, second.sends * 10);
      assert.equal(second.output_tokens, second.sends * 5);
      assert.ok(second.logical_request_bytes > 0 && second.logical_request_bytes <= 65536);
      assert.ok(second.logical_response_bytes > 0 && second.logical_response_bytes <= 16384);
      assert.ok(second.provider_inputs <= 32);
      assert.ok(second.current_tools > 0);
      assert.ok(second.wire_request_bytes > first.wire_request_bytes);
      const source = JSON.parse(await readFile(join(work, 'source.json'), 'utf8'));
      const target = JSON.parse(await readFile(join(work, 'target.json'), 'utf8'));
      assert.equal(source.rounds, 1);
      assert.equal(target.rounds, 2);
      assert.equal(target.attempts, source.attempts + second.current_attempts);
      assert.ok(target.attempts <= 64 && target.records <= 64);
      assert.ok(target.encoded_bytes <= 196608);
      for (const key of ['records', 'encoded_bytes', 'members', 'episodes', 'submissions'])
        assert.ok(target[key] > source[key], 'retained cumulative ' + key);
      assert.notEqual(target.payload, source.payload);
      assert.notEqual(target.execution, source.execution);
      assert.notEqual(target.round, source.round);
      assert.notEqual(target.work, source.work);
      if (id === 'repository-rule') assert.ok(target.attempts > 6, 'renewed round allowance');
      console.error(
        JSON.stringify({
          proof: 'APR_SOLUS_RESTORED_CAPACITY',
          case_id: id,
          historical_attempts: second.historical_attempts,
          current_attempts: second.current_attempts,
          source_records: source.records,
          retained_records: target.records,
          source_encoded_bytes: source.encoded_bytes,
          retained_encoded_bytes: target.encoded_bytes,
          retained_attempts: target.attempts,
          retained_tool_members: target.members,
          retained_candidate_episodes: target.episodes,
          retained_submissions: target.submissions,
          provider_inputs: second.provider_inputs,
          logical_request_bytes: second.logical_request_bytes,
          logical_response_bytes: second.logical_response_bytes,
          request_wire_bytes: second.wire_request_bytes,
          response_wire_bytes: second.wire_response_bytes,
          input_reservation: second.input_reservation,
          output_reservation: second.output_reservation,
          current_input_tokens: second.input_tokens,
          current_output_tokens: second.output_tokens,
        }),
      );
      run('readback');
      const all = await operations();
      for (const kind of ['candidate', 'acceptance']) {
        const uploads = all.filter(
          (item) => item.operation === 'upload' && item.kind === kind && item.committed,
        );
        const current = uploads.filter((item) => item.from_current_capture);
        // R4 may retain a physical predecessor copy after the current capture.
        // Logical successor identity is established by authenticated readback,
        // never by treating the count of physical uploads as a generation count.
        if (kind === 'acceptance') assert.equal(current.length, 2);
        else assert.ok(current.length >= 2 && current.length <= 3);
        assert.equal(new Set(current.map((item) => item.digest)).size, current.length);
      }
      assert.equal(
        all.filter(
          (item) => item.operation === 'upload' && item.kind === 'context_claim' && item.committed,
        ).length,
        1,
      );
      assert.equal(all.filter((item) => item.operation === 'sticky' && item.written).length, 2);
    });
  }
  // An actual Action run starts a new official bridge correlation registry.
  // This fixture keeps its in-memory synthetic object store alive across Hosts;
  // use independent cases so it never accumulates five runs in one registry.
  for (const mode of ['recover', 'retry']) {
    stage = 'same_run_' + mode;
    await scenario('cs-safe', async ({ run, operations }) => {
      run('seed');
      run('review');
      const before = (await operations()).filter(
        (item) => item.operation === 'upload' && item.kind === 'context_claim',
      ).length;
      const recovered = run(mode);
      assert.equal(recovered.accepted, true);
      assert.equal(recovered.sends, 0);
      assert.equal(
        (await operations()).filter(
          (item) => item.operation === 'upload' && item.kind === 'context_claim',
        ).length,
        before,
      );
    });
  }
  stage = 'competing_same_run_processes';
  await scenario('cs-defect', async ({ run, race, operations }) => {
    run('seed');
    const pair = await race();
    assert.equal(pair.filter((item) => item.sends > 0).length, 1);
    assert.equal(pair.filter((item) => item.sends > 0 && item.accepted).length, 1);
    assert.equal(
      (await operations()).filter(
        (item) => item.operation === 'upload' && item.kind === 'context_claim' && item.committed,
      ).length,
      1,
    );
  });
  stage = 'verified_ahead_useful_current_review';
  await scenario('ahead', async ({ run }) => {
    run('seed');
    const current = run('review');
    assert.equal(current.accepted, true);
    assert.equal(current.findings, 1);
    run('readback');
  });
  for (const mode of ['unverified-ahead', 'stale-evidence']) {
    stage = mode;
    await scenario('ahead', async ({ run }) => {
      run('seed');
      assert.equal(run(mode).accepted, false);
    });
  }
  stage = 'absent_control_default_producer';
  await scenario('repository-rule', async ({ run }) => {
    run('seed-default-meta');
    assert.equal(run('review').accepted, true);
    run('readback');
  });
  stage = 'default_policy_reuses_accepted';
  await scenario('cs-safe', async ({ run, operations }) => {
    run('seed-reuse');
    const reused = run('review-reuse');
    assert.equal(reused.accepted, true);
    assert.equal(reused.sends, 0);
    assert.equal((await operations()).filter((item) => item.kind === 'context_claim').length, 0);
  });
  stage = 'failed_import_burns_claim_without_work';
  await scenario('repository-rule', async ({ run, operations }) => {
    run('seed');
    const rejected = run('reject-import');
    assert.equal(rejected.accepted, false);
    assert.equal(rejected.admission, 'Rejected');
    assert.equal(rejected.sends, 0);
    assert.equal(rejected.current_attempts, null);
    run('spent');
    const all = await operations();
    assert.equal(
      all.filter(
        (item) => item.operation === 'upload' && item.kind === 'context_claim' && item.committed,
      ).length,
      1,
    );
    assert.equal(
      all.filter(
        (item) => item.operation === 'upload' && item.kind === 'candidate' && item.committed,
      ).length,
      1,
    );
    assert.equal(all.filter((item) => item.operation === 'sticky' && item.written).length, 1);
  });
  stage = 'wrong_producer_refused_before_work';
  await scenario('repository-rule', async ({ run }) => {
    run('seed');
    const refused = run('wrong-producer');
    assert.equal(refused.accepted, false);
    assert.equal(refused.sends, 0);
    assert.equal(refused.work, null);
  });
  stage = 'renewed_current_token_reservations';
  await scenario('repository-rule-tight', async ({ run }) => {
    const first = run('seed');
    const second = run('review');
    assert.equal(first.current_attempts, 5);
    assert.equal(second.current_attempts, 5);
    assert.equal(second.output_reservation, 1024);
    assert.equal(second.output_tokens, 25);
    // Four settled attempts (20) plus the last 1024-token reservation exactly
    // meet the trusted 1044 cap in each round. Charging history would stop it.
    assert.equal(second.accepted, true);
  });
  for (const mode of [
    'capture-unavailable',
    'capture-missing',
    'capture-duplicate',
    'capture-throw',
    'capture-wrong-format',
    'capture-oversized',
    'malformed-response',
    'missing-usage',
    'mixed-final',
    'cancel-before',
    'cancel-provider',
    'cancel-tool',
    'cancel-capture',
    'invalid-event',
    'unauthorized-input',
    'input-exact',
    'input-over',
    'input-count-over',
    'input-cumulative',
    'missing-key',
    'capabilities-mismatch',
    'policy-mismatch',
    'stale-head',
    'tamper-source',
  ]) {
    stage = mode;
    await scenario('cs-defect', async ({ run, operations }) => {
      run('seed');
      const observed = run(mode);
      assert.equal(observed.accepted, mode === 'input-exact');
      if (['input-exact', 'input-over', 'input-cumulative'].includes(mode))
        console.error(
          JSON.stringify({
            proof: 'APR_SOLUS_RESTORED_BOUNDARY',
            mode,
            accepted: observed.accepted,
            admission: observed.admission,
            termination: observed.termination,
            current_attempts: observed.current_attempts,
            provider_inputs: observed.provider_inputs,
            logical_request_bytes: observed.logical_request_bytes,
            current_sends: observed.sends,
          }),
        );
      const all = await operations();
      assert.equal(
        all.filter((item) => item.operation === 'sticky' && item.written).length,
        mode === 'input-exact' ? 2 : 1,
      );
      if (
        [
          'unauthorized-input',
          'input-over',
          'input-count-over',
          'missing-key',
          'cancel-before',
          'tamper-source',
        ].includes(mode)
      )
        assert.equal(
          all.filter((item) => item.operation === 'upload' && item.kind === 'context_claim').length,
          0,
        );
      if (
        [
          'capture-unavailable',
          'capture-missing',
          'capture-duplicate',
          'capture-throw',
          'capture-wrong-format',
          'capture-oversized',
          'malformed-response',
          'missing-usage',
          'mixed-final',
          'input-cumulative',
        ].includes(mode)
      )
        run('spent');
    });
  }
  for (const source of ['checkpoint', 'unfinished', 'provider']) {
    stage = 'malformed_source_' + source;
    await scenario('cs-defect', async ({ run }) => {
      run('seed-source-' + source);
      assert.equal(run('malformed-source').accepted, false);
      run('spent');
    });
  }
  for (const mode of ['invalid-producer', 'invalid-producer-json', 'invalid-producer-extra']) {
    stage = mode;
    await scenario('repository-rule', async ({ run }) => {
      run('seed');
      const refused = run(mode);
      assert.equal(refused.accepted, false);
      assert.equal(refused.sends, 0);
      assert.equal(refused.work, null);
    });
  }
  for (const mode of [
    'fail-context_claim',
    'unknown-context_claim',
    'unknown-unreadable-context_claim',
    'cancel-context_claim-upload',
    'fail-candidate',
    'fail-acceptance',
    'unknown-acceptance',
    'unknown-sticky',
    'unknown-unwritten-sticky',
    'fail-cleanup',
    'crash-candidate-upload',
    'crash-publication_intent-upload',
    'crash-sticky-upload',
    'crash-acceptance-upload',
  ]) {
    stage = mode;
    await scenario('cs-defect', async ({ run, operations }) => {
      run('seed');
      const crashed = mode.startsWith('crash-');
      const observed = run(mode, crashed ? 73 : 0);
      const before = await operations();
      if (mode === 'fail-context_claim') {
        assert.equal(observed.work, null);
        assert.equal(observed.sends, 0);
        assert.equal(run('review').accepted, true);
      } else if (
        [
          'unknown-unreadable-context_claim',
          'cancel-context_claim-upload',
          'fail-candidate',
        ].includes(mode)
      ) {
        assert.equal(observed.accepted, false);
        assert.equal(observed.sends, mode === 'fail-candidate' ? 5 : 0);
        run('spent');
      } else {
        const unknown = ['unknown-unwritten-sticky', 'crash-publication_intent-upload'].includes(
          mode,
        );
        const recovered = run(unknown ? 'recover-unknown' : 'recover');
        assert.equal(recovered.accepted, !unknown);
        assert.equal(recovered.sends, 0);
        const after = await operations();
        assert.equal(
          after.filter((item) => item.operation === 'upload' && item.kind === 'context_claim')
            .length,
          before.filter((item) => item.operation === 'upload' && item.kind === 'context_claim')
            .length,
        );
        assert.ok(after.filter((item) => item.operation === 'sticky' && item.written).length <= 2);
      }
    });
  }
  proofSummary = {
    proof: 'APR_SOLUS_RESTORED_HOST',
    source: head,
    package: SOLUS_VERSION,
    scenarios,
    hosts,
    live_calls: 0,
    credential_free: true,
    process_restart: true,
    official_bridge: true,
  };
} catch (error) {
  console.error(
    JSON.stringify({
      proof: 'APR_SOLUS_RESTORED_HOST',
      stage,
      failed: true,
      code: error?.code || 'verification_failed',
    }),
  );
  process.exitCode = 1;
} finally {
  if (root) {
    try {
      await removeProofRoot(root);
    } catch (error) {
      console.error(
        JSON.stringify({
          proof: 'APR_SOLUS_RESTORED_HOST',
          stage: 'cleanup',
          failed: true,
          code: error?.code || 'cleanup_failed',
        }),
      );
      process.exitCode = 1;
    }
  }
}
if (proofSummary && !process.exitCode) console.log(JSON.stringify(proofSummary));
