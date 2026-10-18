import assert from 'node:assert/strict';
import { spawn, spawnSync, execFileSync } from 'node:child_process';
import { createHash, randomUUID } from 'node:crypto';
import { once } from 'node:events';
import { chmod, lstat, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { createInterface } from 'node:readline';

let stage = 'environment',
  root,
  passed = false,
  sequence = 0,
  hostCount = 0,
  readbacks = 0,
  controlled = 0,
  adversarial = 0;
const maximum = 2 * 1024 * 1024;
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
const removeRoot = async (directory) => {
  const info = await lstat(directory);
  if (info.isDirectory() && !info.isSymbolicLink()) {
    await chmod(directory, 0o700);
    for (const entry of await readdir(directory, { withFileTypes: true }))
      if (entry.isDirectory() && !entry.isSymbolicLink())
        await removeRoot(join(directory, entry.name));
  }
  await rm(directory, { recursive: true, force: true });
};
try {
  assert.equal(process.platform, 'linux');
  assert.equal(process.arch, 'x64');
  const os = await readFile('/etc/os-release', 'utf8');
  assert.match(os, /^ID=ubuntu$/mu);
  assert.match(os, /^VERSION_ID="24\.04"$/mu);
  const cache = resolve(process.env.NUGET_PACKAGES);
  root = await mkdtemp(join(resolve(process.env.RUNNER_TEMP), 'apr-v2-'));
  await chmod(root, 0o700);
  for (const name of ['home', 'tmp', 'captures', 'traces'])
    await mkdir(join(root, name), { mode: 0o700 });
  const env = Object.fromEntries(
    Object.entries({
      PATH: process.env.PATH,
      DOTNET_ROOT: process.env.DOTNET_ROOT,
      HOME: join(root, 'home'),
      DOTNET_CLI_HOME: join(root, 'home'),
      TMPDIR: join(root, 'tmp'),
      TMP: join(root, 'tmp'),
      TEMP: join(root, 'tmp'),
      NUGET_PACKAGES: cache,
      DOTNET_EnableDiagnostics: '0',
      DOTNET_CLI_USE_MSBUILD_SERVER: '0',
      MSBUILDDISABLENODEREUSE: '1',
      DOTNET_CLI_TELEMETRY_OPTOUT: '1',
      DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: '1',
      DOTNET_NOLOGO: '1',
      LANG: 'C.UTF-8',
      LC_ALL: 'C.UTF-8',
    }).filter(([, value]) => value !== undefined),
  );
  // Project helpers and every build/runtime/bridge child see this allowlist.
  for (const key of Object.keys(process.env)) delete process.env[key];
  Object.assign(process.env, env);
  const { build, stop } = await import('esbuild');
  const { SOLUS_VERSION } = await import('../../../scripts/release/solus-package-contract.mjs');
  const { cases, verifyReport } = await import('./oracle.mjs');
  const { auditNetwork } = await import('./network-audit.mjs');
  const git = (...args) =>
    execFileSync('git', args, { env, encoding: 'utf8', timeout: 30_000 }).trim();
  const head = git('rev-parse', 'HEAD'),
    tree = git('rev-parse', 'HEAD^{tree}');
  assert.match(head, /^[a-f0-9]{40}$/u);
  assert.equal(git('status', '--porcelain', '--untracked-files=normal'), '');
  assert.equal(SOLUS_VERSION, '0.1.0-internal.4');
  const fixture =
    'runtime/tests/SolusEconomicsFixture/AgenticPrReview.Runtime.SolusEconomicsFixture.csproj';
  const config = join(root, 'offline.nuget.config');
  await writeFile(
    config,
    '<configuration><packageSources><clear /></packageSources><fallbackPackageFolders><clear /></fallbackPackageFolders></configuration>\n',
  );
  const properties = [
    '-p:APRManagedPackage=true',
    '-p:APRReleaseVersion=v0.0.0-internal.1',
    '-p:APRBuildId=' + '0'.repeat(64),
    '-p:NuGetAudit=false',
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false',
    '-maxcpucount:1',
  ];
  const output = join(root, 'proof');
  const tool = (args) => {
    const result = spawnSync('dotnet', args, {
      env,
      encoding: 'utf8',
      timeout: 300_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    writeFileSync(join(root, 'captures', stage + '.stdout'), result.stdout || '');
    writeFileSync(join(root, 'captures', stage + '.stderr'), result.stderr || '');
    if (result.status !== 0)
      console.error(
        JSON.stringify({
          stage,
          codes: [
            ...new Set(
              ((result.stdout || '') + (result.stderr || '')).match(
                /\b(?:CS|MSB|NU|NETSDK)\d{4}\b/gu,
              ) || [],
            ),
          ],
        }),
      );
    assert.equal(result.status, 0);
  };
  stage = 'offline_restore';
  tool(['restore', fixture, '-r', 'linux-x64', '--configfile', config, ...properties]);
  stage = 'managed_build';
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
  const dll = join(output, 'AgenticPrReview.Runtime.SolusEconomicsFixture.dll');
  stage = 'installed_bytes';
  for (const name of [
    'SolusAgent.Api',
    'SolusAgent.Tools.Api',
    'SolusAgent.Runtime.Api',
    'SolusAgent.Runtime',
    'SolusAgent.Providers.DeepSeek',
  ]) {
    const id = name.toLowerCase();
    const original = spawnSync(
      'unzip',
      [
        '-p',
        join(cache, id, SOLUS_VERSION, `${id}.${SOLUS_VERSION}.nupkg`),
        `lib/net10.0/${name}.dll`,
      ],
      { env, timeout: 30_000, maxBuffer: 64 * 1024 * 1024 },
    );
    assert.equal(original.status, 0);
    assert.ok(original.stdout.equals(await readFile(join(output, name + '.dll'))));
  }
  const corpus = resolve('runtime/tests/fixtures/agent/r5/quality/bundle');
  const manifest = await readFile(join(corpus, 'manifest.json'));
  assert.equal(
    digest(manifest),
    '55a4fa8cca7f081169c4f1f09503c8a5c8f23197c1960ca09fb6266bcb7375b3',
  );
  const script = JSON.parse(await readFile(join(corpus, 'cases/cs-defect/provider.json'), 'utf8'));
  const seedFinal = script.turns[4].tool_calls[0].arguments_json;
  const target = JSON.parse(seedFinal);
  target.summary = 'Authored current caller review using the historical nullable lookup.';
  const selection = {
    source: {
      commit: head,
      tree,
      clean: true,
      build_sha256: digest(await readFile(dll)),
      corpus_sha256: digest(manifest),
    },
    seedFinal,
    targetFinal: JSON.stringify(target),
  };
  const run = async (args, expected = 0, expectedNetwork = false) => {
    const id = ++sequence,
      trace = join(root, 'traces', 't' + id);
    const result = spawnSync(
      'strace',
      [
        '--seccomp-bpf',
        '-ff',
        '-qq',
        '-s',
        '256',
        '-e',
        'trace=network,process,close',
        '-o',
        trace,
        'dotnet',
        dll,
        ...args,
      ],
      { env, encoding: 'utf8', timeout: 180_000, maxBuffer: maximum },
    );
    await writeFile(join(root, 'captures', id + '.stdout'), result.stdout || '');
    await writeFile(join(root, 'captures', id + '.stderr'), result.stderr || '');
    if (result.status !== expected)
      console.error(
        JSON.stringify({
          stage,
          status: result.status,
          failures: (result.stderr || '')
            .split(/\r?\n/u)
            .filter((line) => /^APR_SOLUS_ECONOMICS_FAILED [a-z0-9_]+$/u.test(line))
            .slice(0, 2),
        }),
      );
    assert.equal(result.status, expected);
    assert.equal(result.stderr || '', '');
    const files = (await readdir(join(root, 'traces'))).filter((name) =>
      name.startsWith('t' + id + '.'),
    );
    assert.ok(files.length > 0 && files.length <= 512);
    const traces = [];
    for (const name of files) {
      const bytes = await readFile(join(root, 'traces', name));
      assert.ok(bytes.length <= 8 * 1024 * 1024);
      traces.push(bytes.toString('utf8'));
    }
    assert.equal(auditNetwork(traces).activity, expectedNetwork);
    return { text: result.stdout || '', trace, files };
  };
  stage = 'audit_positive_control';
  assert.equal(
    (await run(['network-probe'], 0, true)).text.trim(),
    'APR_SOLUS_ECONOMICS_NETWORK_PROBE',
  );
  assert.equal(
    (await run(['network-probe-datagram'], 0, true)).text.trim(),
    'APR_SOLUS_ECONOMICS_NETWORK_PROBE',
  );
  assert.equal((await run(['unix-probe'])).text.trim(), 'APR_SOLUS_ECONOMICS_UNIX_PROBE');
  stage = 'required_self_tests';
  const self = (await run(['self-test'])).text.trim();
  assert.match(
    self,
    /^APR_SOLUS_ECONOMICS_SELF_TEST suites=contracts,nullable,pricing,comparison,wire,freeze,retry checks=\d+$/u,
  );
  assert.ok(Number(self.split('checks=')[1]) >= 40);
  const bridgeBundle = join(root, 'bridge.mjs');
  await build({
    entryPoints: ['src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts'],
    outfile: bridgeBundle,
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
  });
  await stop();
  const writeReport = async (name, value) => {
    const file = join(root, name + '.json');
    await writeFile(file, JSON.stringify(value));
    return file;
  };
  const admit = async (file, valid = true) => {
    const result = await run(['validate', file], valid ? 0 : 1);
    assert.equal(result.text.trim(), valid ? 'APR_SOLUS_ECONOMICS_REPORT_VALID' : '');
  };
  const rebind = async (name, value) => {
    const file = await writeReport(name, value);
    const original = (await run(['rebind', file])).text;
    await writeFile(file, original);
    return { file, value: JSON.parse(original) };
  };
  const compare = async (left, right) => JSON.parse((await run(['compare', left, right])).text);
  const usedPids = new Set(),
    usedStartups = new Set();
  const scenario = async (id, execute) => {
    const work = join(root, 'case-' + id);
    for (const part of ['', 'bridge-staging', 'control'])
      await mkdir(join(work, part), { mode: 0o700 });
    const now = Math.floor(Date.now() / 1000);
    await writeFile(join(work, 'control/now'), String(now));
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
    let bridgeError = '';
    bridge.stderr.on('data', (chunk) => {
      bridgeError += chunk;
      if (bridgeError.length > maximum) bridge.kill('SIGKILL');
    });
    try {
      const ready = await Promise.race([
        once(lines, 'line').then(([line]) => line),
        new Promise((_, reject) =>
          setTimeout(() => reject(new Error('bridge_timeout')), 20_000).unref(),
        ),
      ]);
      assert.equal(ready, 'READY');
      const host = async (mode, seed = null) => {
        await writeFile(
          join(work, 'control/producing-run.json'),
          JSON.stringify({
            id: mode === 'seed' ? '7001' : '7002',
            attempt: mode === 'recovery' ? '3' : '2',
          }),
        );
        const receipt = await run([work, mode, String(now), corpus]);
        assert.equal(receipt.text.trim().split(/\r?\n/u).length, 1);
        assert.ok((await readdir(work)).every((name) => !name.startsWith('snapshot-')));
        const report = JSON.parse(receipt.text);
        hostCount++;
        controlled += report.wire.length;
        assert.ok(!usedPids.has(report.process_id) && !usedStartups.has(report.startup_sha256));
        usedPids.add(report.process_id);
        usedStartups.add(report.startup_sha256);
        const processTrace = receipt.trace + '.' + report.process_id;
        assert.ok(receipt.files.includes('t' + sequence + '.' + report.process_id));
        assert.ok((await readFile(processTrace, 'utf8')).includes('execve('));
        assert.ok(
          (await readFile(processTrace, 'utf8')).includes(
            'AgenticPrReview.Runtime.SolusEconomicsFixture.dll',
          ),
        );
        // Preserve the original document bytes, including decimal precision;
        // JSON.parse/stringify is only for deliberate hostile candidates.
        const file = join(root, id + '-' + mode + '.json');
        await writeFile(file, receipt.text);
        await admit(file);
        verifyReport(report, selection, mode, seed);
        if (mode === 'seed') await writeFile(join(work, 'seed-report.json'), receipt.text);
        return { report, file };
      };
      const readback = async (expected) => {
        const result = await run(['readback', work, String(now), corpus, expected.file]);
        assert.equal(result.text.trim(), 'APR_SOLUS_ECONOMICS_READBACK');
        readbacks++;
      };
      await execute({ host, readback, work });
    } finally {
      lines.close();
      bridge.kill('SIGTERM');
      if (bridge.exitCode === null && bridge.signalCode === null)
        await Promise.race([
          once(bridge, 'exit'),
          new Promise((_, reject) =>
            setTimeout(() => {
              bridge.kill('SIGKILL');
              reject(new Error('bridge_reap'));
            }, 10_000).unref(),
          ),
        ]);
      assert.equal(bridgeError, '');
    }
  };
  let known, knownSeed;
  const scenarioReports = new Map();
  for (const mode of cases) {
    stage = mode;
    await scenario(mode, async ({ host, readback, work }) => {
      const seed = await host('seed');
      assert.equal((await compare(seed.file, seed.file)).prefix_stable, true);
      await readback(seed);
      const sticky = digest(await readFile(join(work, 'sticky.txt')));
      const current = await host(mode, seed.report);
      scenarioReports.set(mode, { current, seed });
      if (current.report.durable_accepted) await readback(current);
      else assert.equal(digest(await readFile(join(work, 'sticky.txt'))), sticky);
      const comparison = await compare(current.file, current.file);
      assert.equal(comparison.billing, 'unknown');
      assert.equal(comparison.cache_savings, 'not_evidenced');
      assert.equal(comparison.quality, 'inconclusive');
      if (current.report.domain && current.report.wire.length)
        assert.equal(comparison.prefix_stable, true);
      else assert.equal(comparison.prefix_stable, null);
      if (mode === 'known') {
        known = current;
        knownSeed = seed;
        assert.equal(comparison.economics_eligibility, 'comparable');
        assert.equal(comparison.reference_difference, 0);
        await host('recovery', seed.report);
      }
      if (
        ['total-only', 'hit-only', 'miss-only', 'missing-usage', 'transport-failure'].includes(mode)
      )
        assert.equal(comparison.reference_difference, null);
    });
  }
  stage = 'prework_denial';
  await scenario('prework', async ({ host }) => {
    const seed = await host('seed');
    await host('missing-key', seed.report);
    await host('known', seed.report);
  });
  stage = 'hostile_reports';
  const mutations = [
    (r) => {
      r.admission = null;
      r.history_reported = false;
      r.history = [];
    },
    (r) => {
      r.admission = 'Fresh';
      r.history = [];
    },
    (r) => {
      r.execution_kind = 'live';
    },
    (r) => {
      r.extra = 'V2_RESTRICTED_REASONING_CANARY';
    },
    (r) => {
      r.current.input.observed_tokens++;
    },
    (r) => {
      r.current.continued_calls = 1;
    },
    (r) => {
      r.current.attempts[1].physical_attempt_id = r.current.attempts[0].physical_attempt_id;
    },
    (r) => {
      r.current.attempts[0].attempt_number = 2;
    },
    (r) => {
      r.current.execution_id = randomUUID();
    },
    (r) => {
      r.history[0].usage = r.current;
    },
    (r) => {
      r.exchanges[0].observation.logical_call_id = randomUUID();
    },
    (r) => {
      r.wire.pop();
    },
    (r) => {
      r.wire[0].physical_attempt_id = randomUUID();
    },
    (r) => {
      r.wire[0].messages[11].reasoning_sha256 = null;
    },
    (r) => {
      r.price.observed.total = 0;
    },
    (r) => {
      r.price.reference = 'current-market-price';
    },
  ];
  for (const mutate of mutations) {
    const value = structuredClone(known.report);
    mutate(value);
    // Unknown fields and explicit canaries go directly to the strict reader.
    const candidate = value.extra
      ? { file: await writeReport('unknown-' + adversarial, value) }
      : await rebind('bad-' + adversarial, value);
    await admit(candidate.file, false);
    adversarial++;
  }
  const original = JSON.stringify(known.report);
  const duplicate = join(root, 'duplicate.json');
  await writeFile(duplicate, original.replace('{', '{"format":"duplicate",'));
  await admit(duplicate, false);
  adversarial++;
  const escaped = join(root, 'escaped.json');
  await writeFile(
    escaped,
    original.replace('{', '{"extra":"\\u00562_RESTRICTED_REASONING_CANARY",'),
  );
  await admit(escaped, false);
  adversarial++;
  const domains = [
    (r) => {
      r.source.commit = 'a'.repeat(40);
    },
    (r) => {
      r.source.tree = 'a'.repeat(40);
    },
    (r) => {
      r.source.clean = false;
    },
    (r) => {
      r.source.corpus_sha256 = 'a'.repeat(64);
    },
    (r) => {
      r.domain.package_version = '0.1.0-internal.5';
    },
    (r) => {
      r.domain.profile = 'different-profile';
    },
    (r) => {
      r.domain.runtime_configuration_sha256 = 'a'.repeat(64);
    },
    (r) => {
      r.history[0].round_id = randomUUID();
    },
  ];
  for (const mutate of domains) {
    const value = structuredClone(known.report);
    mutate(value);
    const changed = await rebind('domain-' + adversarial, value);
    await admit(changed.file);
    const result = await compare(known.file, changed.file);
    assert.equal(result.prefix_stable, null);
    assert.equal(result.reference_difference, null);
    adversarial++;
  }
  for (const mutate of [
    (r) => {
      r.wire[0].messages[10].token.sha256 = 'a'.repeat(64);
    },
    (r) => {
      [r.wire[0].messages[9], r.wire[0].messages[10]] = [
        r.wire[0].messages[10],
        r.wire[0].messages[9],
      ];
    },
    (r) => {
      r.wire[0].messages[11].reasoning_sha256 = 'a'.repeat(64);
    },
    (r) => {
      r.wire[0].messages[10].tool_association_sha256 = 'a'.repeat(64);
    },
    (r) => {
      r.wire[0].settings.sha256 = 'a'.repeat(64);
    },
  ]) {
    const value = structuredClone(known.report);
    mutate(value);
    const changed = await rebind('prefix-' + adversarial, value);
    await admit(changed.file);
    assert.notEqual((await compare(known.file, changed.file)).prefix_stable, true);
    assert.throws(() => verifyReport(changed.value, selection, 'known', knownSeed.report));
    adversarial++;
  }
  const coherent = structuredClone(known.report);
  for (const [index, delta] of [
    [0, 1],
    [1, -1],
  ]) {
    const attempt = coherent.current.attempts[index];
    attempt.input_tokens += delta;
    attempt.details.find((d) => d.kind === 'UncachedInput').value += delta;
    coherent.exchanges[index].observation = structuredClone(attempt);
  }
  const rebound = await rebind('coherent-golden-tamper', coherent);
  await admit(rebound.file);
  assert.throws(() => verifyReport(rebound.value, selection, 'known', knownSeed.report));
  adversarial++;
  for (const outcome of ['Failed', 'Cancelled']) {
    const invalid = structuredClone(scenarioReports.get('invalid-usage').current.report);
    invalid.exchanges[0].outcome = outcome;
    invalid.exchanges[0].error = 'InvalidResponse';
    const changed = await rebind('invalid-result-' + adversarial, invalid);
    await admit(changed.file, false);
    adversarial++;
  }
  for (const [mode, outcome, error] of [
    ['invalid-usage', 'Succeeded', 'None'],
    ['invalid-final', 'Rejected', 'InvalidResponse'],
    ['transport-failure', 'Rejected', 'InvalidResponse'],
    ['missing-usage', 'Rejected', 'InvalidResponse'],
    ['cancel-after-usage', 'Cancelled', 'Cancelled'],
  ]) {
    const { current, seed } = scenarioReports.get(mode);
    const invalid = structuredClone(current.report);
    const exchange = invalid.exchanges.at(-1);
    exchange.outcome = outcome;
    exchange.error = error;
    const changed = await rebind('wrong-scenario-result-' + adversarial, invalid);
    await admit(changed.file);
    assert.throws(() => verifyReport(changed.value, selection, mode, seed.report));
    adversarial++;
  }
  stage = 'source_and_cleanup';
  assert.equal(git('rev-parse', 'HEAD'), head);
  assert.equal(git('rev-parse', 'HEAD^{tree}'), tree);
  assert.equal(git('status', '--porcelain', '--untracked-files=normal'), '');
  const allTraces = await readdir(join(root, 'traces'));
  assert.ok(allTraces.length <= 4096);
  let traceBytes = 0;
  for (const name of allTraces) traceBytes += (await lstat(join(root, 'traces', name))).size;
  assert.ok(traceBytes <= 128 * 1024 * 1024);
  assert.equal(adversarial, 39);
  assert.equal(hostCount, 30);
  assert.equal(readbacks, 18);
  assert.equal(controlled, 92);
  passed = true;
  await removeRoot(root);
  assert.equal(
    await lstat(root).then(
      () => true,
      () => false,
    ),
    false,
  );
  console.log(
    `APR_SOLUS_ECONOMICS_PROOF scenarios=${cases.length + 1} hosts=${hostCount} readbacks=${readbacks} adversarial=${adversarial} host_http_entries=${controlled} live_calls=0 installed_bytes=true private_state=absent`,
  );
} catch (error) {
  // Retain failed private evidence. No child output, request, exception or path
  // is printed; the outer bounded supervisor owns timeout/group reaping.
  console.error('APR_SOLUS_ECONOMICS_VERIFY_FAILED stage=' + stage);
  const location = error?.stack?.match(
    /runtime\/tests\/SolusEconomicsFixture\/(?:oracle|verify)\.mjs:\d+:\d+/u,
  )?.[0];
  if (location) console.error('APR_SOLUS_ECONOMICS_ORACLE location=' + location);
  process.exitCode = 1;
}
if (!passed) process.exitCode = 1;
