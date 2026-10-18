// Installed-package proof; no source references, credentials, provider network or
// live GitHub mutations. One real bridge survives multiple fresh Host processes.
import assert from 'node:assert/strict';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { mkdtemp, mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join, resolve } from 'node:path';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { build } from 'esbuild';
import { SOLUS_VERSION } from '../../../scripts/release/solus-package-contract.mjs';

let stage = 'environment';
let root;
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  const os = await readFile('/etc/os-release', 'utf8');
  assert.match(os, /^ID=ubuntu$/m);
  assert.match(os, /^VERSION_ID="24\.04"$/m);
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  assert.match(head, /^[a-f0-9]{40}$/);
  root = await mkdtemp(join(resolve(process.env.RUNNER_TEMP), 'apr-s3-'));
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
    'runtime/tests/SolusFreshStateCommitFixture/AgenticPrReview.Runtime.SolusFreshStateCommitFixture.csproj';
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
        const result = spawnSync('dotnet', [dll, work, mode, id, String(now), corpus], {
          env,
          encoding: 'utf8',
          timeout: 180_000,
          maxBuffer: 1024 * 1024,
        });
        if (result.status !== expectedExit) {
          const failures = (result.stderr || '')
            .split(/\r?\n/)
            .filter((line) => /^APR_SOLUS_STATE_COMMIT_FAILED [a-z0-9_-]+$/.test(line));
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
      await execute({ run, operations, work });
    } finally {
      lines.close();
      bridge.kill('SIGTERM');
      if (bridge.exitCode === null && bridge.signalCode === null) await once(bridge, 'exit');
    }
  };
  const immutable = async (operations) => {
    const all = await operations();
    for (const kind of ['candidate', 'acceptance']) {
      const uploads = all.filter(
        (item) => item.operation === 'upload' && item.kind === kind && item.committed,
      );
      assert.equal(uploads.length, 1, 'unique immutable ' + kind);
    }
    assert.equal(all.filter((item) => item.operation === 'sticky' && item.written).length, 1);
    assert.equal(all.filter((item) => item.kind === 'context_claim').length, 0);
  };
  for (const id of ['cs-safe', 'cs-defect', 'sticky-only', 'no-required-tool']) {
    stage = 'fresh_' + id;
    await scenario(id, async ({ run, operations }) => {
      const fresh = run('success');
      assert.equal(fresh.accepted, true);
      assert.equal(fresh.capture, true);
      assert.ok(fresh.sends > 0);
      const recovered = run('recover');
      assert.equal(recovered.accepted, true);
      assert.equal(recovered.recovered, true);
      assert.equal(recovered.sends, 0);
      assert.equal(recovered.findings, fresh.findings);
      run('readback');
      run('reject-compat');
      await immutable(operations);
    });
  }
  stage = 'inline_projection_recovery';
  await scenario('cs-defect', async ({ run, operations }) => {
    const fresh = run('success-inline');
    assert.equal(fresh.status, 'Reviewed');
    const recovered = run('recover-inline');
    assert.equal(recovered.status, 'ReviewedWithInlineWarnings');
    assert.equal(recovered.findings, 1);
    assert.equal(recovered.sends, 0);
    run('readback-inline');
    await immutable(operations);
    assert.equal((await operations()).filter((item) => item.operation === 'inline').length, 1);
  });
  for (const boundary of [
    'candidate-upload',
    'candidate-readback',
    'publication_intent-upload',
    'publication_intent-readback',
    'sticky-upload',
    'sticky-readback',
    'acceptance-upload',
    'acceptance-readback',
    'cleanup',
    'accepted-cleanup',
  ]) {
    stage = 'restart_' + boundary;
    await scenario('cs-defect', async ({ run, operations, work }) => {
      run('crash-' + boundary, 73);
      assert.equal(await readFile(join(work, 'fault-triggered'), 'utf8'), boundary);
      const uncertainIntent = boundary.startsWith('publication_intent-') || boundary === 'cleanup';
      const recovered = run(uncertainIntent ? 'recover-unknown' : 'recover');
      assert.equal(recovered.sends, 0);
      if (uncertainIntent) {
        assert.equal(recovered.accepted, false);
        const all = await operations();
        assert.equal(
          all.filter(
            (item) => item.operation === 'upload' && item.kind === 'candidate' && item.committed,
          ).length,
          1,
        );
        assert.equal(all.filter((item) => item.operation === 'sticky').length, 0);
        assert.equal(
          all.filter((item) => item.operation === 'upload' && item.kind === 'acceptance').length,
          0,
        );
      } else {
        assert.equal(recovered.accepted, true);
        assert.equal(recovered.findings, 1);
        run('readback');
        await immutable(operations);
      }
    });
  }
  for (const mode of [
    'capture-unavailable',
    'capture-missing',
    'capture-duplicate',
    'capture-throw',
    'capture-wrong-format',
    'capture-oversized',
    'stale-head',
    'malformed',
    'compat-mismatch',
    'fail-candidate',
    'fail-acceptance',
    'fail-cleanup-upload',
    'unknown-unreadable-candidate',
    'unknown-unwritten-sticky',
    'cancel-provider',
    'cancel-before',
    'cancel-tool',
    'cancel-capture',
    'cancel-candidate-before-upload',
    'cancel-candidate-upload',
    'cancel-publication_intent-upload',
  ]) {
    stage = 'failure_' + mode;
    await scenario('cs-defect', async ({ run }) => assert.equal(run(mode).accepted, false));
  }
  for (const mode of [
    'unknown-candidate',
    'unknown-acceptance',
    'unknown-sticky',
    'fail-cleanup',
    'cancel-acceptance-upload',
    'cancel-sticky-upload',
    'cancel-sticky-readback',
    'cancel-accepted-cleanup',
  ]) {
    stage = 'reconcile_' + mode;
    await scenario('cs-defect', async ({ run }) => assert.equal(run(mode).accepted, true));
  }
  console.log(
    JSON.stringify({
      proof: 'APR_SOLUS_FRESH_STATE_COMMIT',
      source: head,
      package: SOLUS_VERSION,
      scenarios,
      hosts,
      live_calls: 0,
      credential_free: true,
      process_restart: true,
      official_bridge: true,
    }),
  );
} catch (error) {
  console.error(
    JSON.stringify({
      proof: 'APR_SOLUS_FRESH_STATE_COMMIT',
      stage,
      failed: true,
      code: error?.code || 'verification_failed',
    }),
  );
  process.exitCode = 1;
} finally {
  if (root) await rm(root, { recursive: true, force: true });
}
