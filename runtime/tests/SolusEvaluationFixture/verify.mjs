// Actual installed-package qualification only. There is no source-reference or
// metadata-only acceptance switch. Every Host is a new credential-free process.
import assert from 'node:assert/strict';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { chmod, lstat, mkdtemp, mkdir, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { build } from 'esbuild';
import { verifyAssertionDispositions } from './assertion-dispositions.mjs';
import { SOLUS_VERSION } from '../../../scripts/release/solus-package-contract.mjs';
import {
  QUALITY,
  CANARIES,
  verifyCase,
  verifyQuality,
  verifySuccessor,
  verifyGrowth,
  verifyCleanup,
  classifyRefusal,
} from './report.mjs';

const repository = resolve(import.meta.dirname, '../../..');
process.chdir(repository);
let stage = 'environment';
let root;
let summary;
let runSequence = 10000;
let hosts = 0;
const started = Date.now();
const elapsedLimit = 20 * 60 * 1000;
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
async function removePrivateRoot(directory) {
  const info = await lstat(directory);
  if (info.isDirectory() && !info.isSymbolicLink()) {
    await chmod(directory, 0o700);
    for (const entry of await readdir(directory, { withFileTypes: true }))
      if (entry.isDirectory() && !entry.isSymbolicLink())
        await removePrivateRoot(join(directory, entry.name));
  }
  await rm(directory, { recursive: true, force: true });
}
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  const os = await readFile('/etc/os-release', 'utf8');
  assert.match(os, /^ID=ubuntu$/mu);
  assert.match(os, /^VERSION_ID="24\.04"$/mu);
  const git = (args) => execFileSync('git', args, { encoding: 'utf8' }).trim();
  const source = {
    commit: git(['rev-parse', 'HEAD']),
    tree: git(['rev-parse', 'HEAD^{tree}']),
    version: SOLUS_VERSION,
  };
  assert.match(source.commit, /^[a-f0-9]{40}$/u);
  assert.match(source.tree, /^[a-f0-9]{40}$/u);
  assert.equal(git(['status', '--porcelain', '--untracked-files=normal']), '');
  assert.ok(process.env.RUNNER_TEMP && process.env.NUGET_PACKAGES);
  const nativeAssertions = verifyAssertionDispositions(repository);
  const assertionDispositions = digest(
    await readFile('runtime/tests/SolusEvaluationFixture/native-assertions.json'),
  );
  const packages = resolve(process.env.NUGET_PACKAGES);
  const names = [
    'SolusAgent.Api',
    'SolusAgent.Tools.Api',
    'SolusAgent.Runtime.Api',
    'SolusAgent.Runtime',
    'SolusAgent.Providers.DeepSeek',
  ];
  for (const name of names)
    await readFile(join(packages, name.toLowerCase(), SOLUS_VERSION, 'lib/net10.0', name + '.dll'));
  root = await mkdtemp(join(resolve(process.env.RUNNER_TEMP), 'apr-v1-'));
  for (const part of ['home', 'tmp']) await mkdir(join(root, part), { mode: 0o700 });
  const runtimeEnv = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(root, 'home'),
    DOTNET_CLI_HOME: join(root, 'home'),
    TMPDIR: join(root, 'tmp'),
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_GENERATE_ASPNET_CERTIFICATE: 'false',
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
  };
  const buildEnv = { ...runtimeEnv, NUGET_PACKAGES: packages };
  const fixture =
    'runtime/tests/SolusEvaluationFixture/AgenticPrReview.Runtime.SolusEvaluationFixture.csproj';
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
      env: buildEnv,
      encoding: 'utf8',
      timeout: 300000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.status !== 0) {
      const output = (result.stdout || '') + (result.stderr || '');
      console.error(
        JSON.stringify({
          stage,
          status: result.status,
          codes: [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)\d{4}\b/gu) || [])],
        }),
      );
    }
    assert.equal(result.status, 0);
  };
  // Only the earlier authorized cold-cache step can restore private packages.
  // This restore has no feed or credential and can consume only that cache.
  const config = join(root, 'offline.nuget.config');
  await writeFile(
    config,
    '<configuration><packageSources><clear /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>',
  );
  stage = 'restore';
  tool([
    'restore',
    fixture,
    '-r',
    'linux-x64',
    '--disable-build-servers',
    '--configfile',
    config,
    ...properties,
  ]);
  const output = join(root, 'output');
  stage = 'build';
  tool([
    'build',
    fixture,
    '-r',
    'linux-x64',
    '-c',
    'Release',
    '--no-restore',
    '--disable-build-servers',
    '-o',
    output,
    ...properties,
  ]);
  stage = 'installed_identity';
  const loaded = [];
  for (const name of names) {
    const actual = digest(await readFile(join(output, name + '.dll')));
    assert.equal(
      actual,
      digest(
        await readFile(
          join(packages, name.toLowerCase(), SOLUS_VERSION, 'lib/net10.0', name + '.dll'),
        ),
      ),
    );
    loaded.push({ assembly: name, sha256: actual });
  }
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
  const receipts = [];
  let worlds = 0;
  const world = async (execute) => {
    const work = join(root, 'w' + worlds++);
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
      { env: runtimeEnv, stdio: ['ignore', 'pipe', 'pipe'] },
    );
    bridge.stderr.resume();
    const lines = createInterface({ input: bridge.stdout });
    try {
      const ready = await Promise.race([
        once(lines, 'line').then(([line]) => line),
        new Promise((_, reject) =>
          setTimeout(() => reject(new Error('bridge_ready_timeout')), 20000).unref(),
        ),
      ]);
      assert.equal(ready, 'READY');
      const run = (mode, id = 'cs-defect', reuseRun) => {
        assert.ok(Date.now() - started < elapsedLimit);
        const runId = reuseRun ?? ++runSequence;
        writeFileSync(
          join(work, 'control', 'producing-run.json'),
          JSON.stringify({ id: String(runId), attempt: '2' }),
        );
        const result = spawnSync(
          'dotnet',
          [dll, work, mode, id, String(now), String(runId), corpus],
          {
            env: runtimeEnv,
            encoding: 'utf8',
            timeout: Math.min(120000, elapsedLimit - (Date.now() - started)),
            maxBuffer: 1024 * 1024,
          },
        );
        hosts++;
        if (result.status !== 0)
          console.error(
            JSON.stringify({
              stage,
              mode,
              status: result.status,
              failures: (result.stderr || '')
                .split(/\r?\n/u)
                .filter((line) => /^APR_SOLUS_EVALUATION_FAILED [a-z0-9_-]+$/u.test(line)),
            }),
          );
        assert.equal(result.status, 0);
        const text = result.stdout.trim();
        assert.ok(text.length < 65536);
        assert.equal(text.split(/\r?\n/u).length, 1);
        for (const canary of CANARIES) assert.ok(!text.includes(canary));
        const row = verifyCase(JSON.parse(text), source, mode, id);
        receipts.push(row);
        return { row, runId };
      };
      const operations = async () => {
        try {
          return (await readFile(join(work, 'operations.jsonl'), 'utf8'))
            .trim()
            .split(/\r?\n/u)
            .filter(Boolean)
            .map((line) => JSON.parse(line));
        } catch (error) {
          if (error.code === 'ENOENT') return [];
          throw error;
        }
      };
      await execute(run, operations);
    } finally {
      lines.close();
      if (bridge.exitCode === null && bridge.signalCode === null) {
        const closed = once(bridge, 'close');
        bridge.kill('SIGTERM');
        await Promise.race([
          closed,
          new Promise((_, reject) =>
            setTimeout(() => reject(new Error('bridge_close_timeout')), 10000).unref(),
          ),
        ]);
      }
    }
    const sticky = await readFile(join(work, 'sticky.txt'), 'utf8').catch((error) => {
      if (error.code === 'ENOENT') return '';
      throw error;
    });
    for (const canary of CANARIES.filter((value) => !value.startsWith('APR242_')))
      assert.ok(!sticky.includes(canary));
  };
  stage = 'quality';
  const quality = [];
  for (const [id] of QUALITY) await world(async (run) => quality.push(run('fresh', id).row));
  verifyQuality(quality, source);
  stage = 'same_head';
  let same;
  await world(async (run, operations) => {
    const seed = run('seed').row;
    assert.equal(seed.durable_accepted, true);
    assert.equal(seed.outcome.code, 'Scored');
    const initial = await operations();
    const review = run('review');
    same = review.row;
    verifySuccessor(seed, same, source);
    const after = await operations();
    assert.equal(initial.filter((o) => o.operation === 'sticky').length, 1);
    assert.equal(after.filter((o) => o.operation === 'sticky').length, 2);
    const recovered = run('recover', 'cs-defect', review.runId).row;
    assert.equal(recovered.recovered_without_work, true);
    assert.equal(recovered.outcome, null);
    assert.equal(recovered.sends, 0);
    assert.deepEqual(await operations(), after);
    assert.equal(run('readback').row.readback_verified, true);
  });
  stage = 'verified_ahead';
  let ahead;
  await world(async (run) => {
    const seed = run('seed').row;
    ahead = run('ahead', 'ts-defect').row;
    verifySuccessor(seed, ahead, source, { ahead: true });
    assert.equal(run('readback').row.readback_verified, true);
  });
  stage = 'refusal_matrix';
  const negatives = [];
  for (const mode of [
    'stale-evidence',
    'malformed-response',
    'missing-usage',
    'cancel-provider',
    'cancel-before',
    'policy-mismatch',
    'tamper-source',
    'unverified-ahead',
    'reject-import',
    'input-over',
    'capture-throw',
    'stale-head',
  ]) {
    await world(async (run, operations) => {
      const seed = run('seed').row;
      const before = await operations();
      const row = run(
        mode,
        ['stale-evidence', 'unverified-ahead'].includes(mode) ? 'ts-defect' : 'cs-defect',
      ).row;
      negatives.push(row);
      assert.equal(row.durable_accepted, false);
      assert.equal(row.recovered_without_work, false);
      if (
        [
          'policy-mismatch',
          'tamper-source',
          'unverified-ahead',
          'reject-import',
          'input-over',
          'cancel-before',
        ].includes(mode)
      )
        assert.equal(row.sends, 0);
      if (['capture-throw', 'stale-head'].includes(mode)) {
        assert.equal(row.outcome.execution_status, 'Completed');
        assert.equal(row.outcome.code, 'Scored');
        if (mode === 'capture-throw') assert.equal(row.capture_valid, false);
        if (mode === 'stale-head') assert.equal(row.candidate_eligible, false);
      } else {
        assert.notEqual(row.outcome.execution_status, 'Completed');
        assert.equal(row.outcome.model_status, 'NotEvaluated');
      }
      const after = await operations();
      assert.equal(
        after.filter((o) => o.operation === 'sticky').length,
        before.filter((o) => o.operation === 'sticky').length,
      );
      {
        const readback = run('readback').row;
        assert.equal(readback.readback_verified, true);
        assert.deepEqual(readback.predecessor, seed.capture);
      }
    });
  }
  stage = 'oracle_mutations';
  for (const mode of ['source-mutation', 'initial-leak', 'policy-leak'])
    await world(async (run) => {
      const row = run(mode).row;
      assert.equal(row.durable_accepted, false);
      assert.notEqual(row.outcome.execution_status, 'Completed');
      assert.equal(row.outcome.model_status, 'NotEvaluated');
    });
  stage = 'repeated_read';
  await world(async (run) => {
    const row = run('repeated-read', 'cs-safe').row;
    assert.equal(row.outcome.code, 'Scored');
    assert.equal(row.current_tools, 3);
    assert.equal(row.sends, 4);
  });
  stage = 'growth';
  let growth;
  await world(async (run, operations) => {
    const rows = [run('seed').row];
    let refusal;
    let injected = false;
    for (let index = 1; index < 16; index++) {
      const row = run('review').row;
      if (!row.durable_accepted) {
        refusal = row;
        break;
      }
      verifySuccessor(rows.at(-1), row, source);
      rows.push(row);
    }
    if (!refusal) {
      injected = true;
      refusal = run('input-over').row;
    }
    verifyGrowth(rows, refusal, source, injected);
    assert.equal(run('readback').row.readback_verified, true);
    const effects = await operations();
    assert.equal(effects.filter((o) => o.operation === 'sticky').length, rows.length);
    growth = {
      accepted_rounds: rows.length,
      refusal: classifyRefusal(refusal, injected),
      context_rejection: refusal.context_rejection,
      capture_status: refusal.capture_status,
      host_status: refusal.host_status,
      stop_reason: refusal.termination,
      rounds: rows.map((row) => ({
        current_attempts: row.current_attempts,
        current_tools: row.current_tools,
        historical_attempts: row.historical_attempts,
        historical_rounds: row.historical_rounds,
        provider_inputs: row.provider_inputs,
        logical_request_bytes: row.logical_request_bytes,
        logical_response_bytes: row.logical_response_bytes,
        wire_request_bytes: row.wire_request_bytes,
        wire_response_bytes: row.wire_response_bytes,
        input_tokens: row.input_tokens,
        output_tokens: row.output_tokens,
        input_reservation: row.input_reservation,
        output_reservation: row.output_reservation,
        context_bytes: row.capture.Bytes,
        records: row.capture.Records,
      })),
      refused_attempt: refusal,
      current_attempts: rows.map((r) => r.current_attempts),
      current_tools: rows.map((r) => r.current_tools),
      retained_attempts: rows.map((r) => r.capture.Attempts),
      retained_records: rows.map((r) => r.capture.Records),
      context_bytes: rows.map((r) => r.capture.Bytes),
      final_predecessor_sha256: rows.at(-1).capture.Payload,
    };
  });
  stage = 'source_recheck';
  assert.equal(git(['rev-parse', 'HEAD']), source.commit);
  assert.equal(git(['rev-parse', 'HEAD^{tree}']), source.tree);
  assert.equal(git(['status', '--porcelain', '--untracked-files=normal']), '');
  summary = {
    schema: 'apr-solus-evaluation-proof-v1',
    native_assertion_methods: nativeAssertions,
    assertion_dispositions_sha256: assertionDispositions,
    source_commit: source.commit,
    source_tree: source.tree,
    package_version: SOLUS_VERSION,
    context_format: 2,
    context_compatibility: 1,
    loaded_assemblies: loaded,
    quality_cases: quality.length,
    quality: quality.map((row) => row.outcome),
    same_head: same,
    ahead,
    negative_results: negatives.map((row) => ({
      mode: row.mode,
      sends: row.sends,
      durable_accepted: row.durable_accepted,
      candidate_eligible: row.candidate_eligible,
      capture_valid: row.capture_valid,
      execution_status: row.outcome.execution_status,
      code: row.outcome.code,
    })),
    quality_verified: true,
    restored_verified: true,
    ahead_verified: true,
    refusal_cases: negatives.length,
    growth,
    hosts,
    live_calls: 0,
    model_quality: 'not_evaluated',
    receipts_sha256: digest(JSON.stringify(receipts)),
  };
} catch (error) {
  const location = /\/(report|verify)\.mjs:(\d+):\d+/u.exec(error.stack || '');
  console.error(
    JSON.stringify({
      stage,
      check_file: location?.[1],
      check_line: location?.[2],
      assertion: error.code === 'ERR_ASSERTION',
      expected:
        typeof error.expected === 'number' || typeof error.expected === 'boolean'
          ? error.expected
          : undefined,
      actual:
        typeof error.actual === 'number' || typeof error.actual === 'boolean'
          ? error.actual
          : undefined,
    }),
  );
  console.error('APR_SOLUS_EVALUATION_PROOF_FAILED ' + stage);
  process.exitCode = 1;
} finally {
  if (root) {
    try {
      await removePrivateRoot(root);
      await lstat(root).then(
        () => {
          throw new Error('retained');
        },
        (error) => {
          assert.equal(error.code, 'ENOENT');
        },
      );
    } catch {
      console.error('APR_SOLUS_EVALUATION_PROOF_FAILED cleanup');
      process.exitCode = 1;
    }
  }
}
if (!process.exitCode && summary) {
  verifyCleanup('removed');
  console.log(JSON.stringify({ ...summary, cleanup: 'removed' }));
}
