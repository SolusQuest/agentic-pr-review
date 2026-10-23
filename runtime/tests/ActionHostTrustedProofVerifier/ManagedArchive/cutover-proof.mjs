import { prepareHistoricalSessionProducer } from './historical-session-producer.mjs';
import { projectCutoverFailureCode } from './cutover-failure-code.mjs';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
import { awaitFixtureGroupClose, monotonicMilliseconds } from './role-deadline.mjs';
import { basename, join } from 'node:path';

export class CutoverSupportFailure extends Error {
  constructor(code) {
    super('r6_supporting_process_failed');
    this.proofCode = code;
  }
}

// Only fixed failure-envelope categories leave this function. Assertion code
// admission belongs to the separate closed projector and takes priority.
export function classifyCutoverFailureEnvelope(result) {
  const invalid = 'supporting_output_invalid';
  try {
    if (result === null || typeof result !== 'object') return invalid;
    if (!result.error && !result.signal && result.status === 0) return null;
    const decode = (value) => {
      if (value === null || value === undefined) return '';
      if (typeof value === 'string') {
        if (Buffer.byteLength(value) > 65536) throw new Error();
        return value;
      }
      if (!(value instanceof Uint8Array) || value.byteLength > 65536) throw new Error();
      return new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(value);
    };
    const output = decode(result.stdout) + decode(result.stderr);
    if (
      Buffer.byteLength(output) > 65536 ||
      /[^\x20-\x7e\r\n]/u.test(output) ||
      /\r(?!\n)/u.test(output)
    )
      return invalid;
    if (result.error) return 'supporting_process_launch_error';
    if (result.signal) return 'supporting_process_signal_exit';
    const line = output.endsWith('\r\n')
      ? output.slice(0, -2)
      : output.endsWith('\n')
        ? output.slice(0, -1)
        : output;
    if (line.length === 0) return 'supporting_output_empty';
    if (/[\r\n]/u.test(line)) return 'supporting_output_multiline';
    if (line.length > 256) return invalid;
    return /^APR_SOLUS_(?:CUTOVER|DIRECT_FAULTS)_FAILED [a-zA-Z0-9_]+$/u.test(line)
      ? 'supporting_output_unmapped_singleline'
      : 'supporting_output_noncanonical';
  } catch {
    return invalid;
  }
}

function run(command, args, options = {}) {
  const { roleDeadline, ...processOptions } = options;
  const maximum = processOptions.timeout ?? 300_000;
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    maxBuffer: 2 * 1024 * 1024,
    ...processOptions,
    timeout: roleDeadline ? roleDeadline.supportingMilliseconds(maximum) : maximum,
    ...(roleDeadline ? { killSignal: 'SIGKILL' } : {}),
  });
  if (result.error || result.status !== 0) {
    const output = (result.stdout ?? '') + (result.stderr ?? '');
    // Preparation runs before isolated archive diagnostics. Publish only the
    // same bounded compiler-code vocabulary used by the preparation probe;
    // paths, compiler text and child output remain private.
    if (command === 'dotnet' && args[0] === 'publish') {
      const target = basename(args[args.indexOf('-o') + 1] ?? '');
      const label = [
        'cutover-inputs',
        'cutover-faults',
        'cutover-historical',
        'cutover-pinned-action',
      ].includes(target)
        ? target
        : 'unknown';
      const codes =
        [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)[0-9]{4}\b/gu) ?? [])]
          .slice(0, 8)
          .join(',') || 'none';
      process.stderr.write(
        'APR_SOLUS_CUTOVER_BUILD_FAILED target=' + label + ' codes=' + codes + '\n',
      );
    }
    const code = /^APR_SOLUS_CUTOVER_FAILED (reset_(?:oracle_[a-z0-9_]+|retry_inventory))$/mu.exec(
      output,
    )?.[1];
    throw new CutoverSupportFailure(
      code ??
        projectCutoverFailureCode(output) ??
        classifyCutoverFailureEnvelope(result) ??
        'supporting_process_failed',
    );
  }
  assert.equal(result.error, undefined, 'r6_cutover_process_error');
  assert.equal(result.status, 0, 'r6_cutover_process_failed');
  return result.stdout;
}

async function supportingInventory(directory, sha256) {
  const members = [];
  async function walk(relative) {
    for (const item of await readdir(join(directory, relative), { withFileTypes: true })) {
      const name = relative ? relative + '/' + item.name : item.name;
      assert.equal(item.isSymbolicLink(), false);
      if (item.isDirectory()) await walk(name);
      else {
        assert.equal(item.isFile(), true);
        const bytes = await readFile(join(directory, name));
        members.push({ path: name, size: bytes.length, sha256: sha256(bytes) });
      }
    }
  }
  await walk('');
  members.sort((a, b) => a.path.localeCompare(b.path));
  return sha256(Buffer.from(JSON.stringify(members)));
}

// Supporting input/observation tools have their own outputs and identities.
// Neither is installed in or selected as an entry of the managed archive.
export async function prepareCutoverSupport(root, repo, builderEnv) {
  const { build } = await import('esbuild');
  await build({
    entryPoints: [
      join(repo, 'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/cutover-bridge.mjs'),
    ],
    outfile: join(root, 'cutover-bridge.mjs'),
    bundle: true,
    platform: 'node',
    format: 'esm',
    target: 'node24',
    banner: {
      js: "import { createRequire } from 'node:module'; const require = createRequire(import.meta.url);",
    },
  });
  for (const [name, project] of [
    ['cutover-inputs', 'SolusProductionCutoverFixture'],
    ['cutover-faults', 'SolusProductionCutoverFixture'],
  ]) {
    run(
      'dotnet',
      [
        'publish',
        join(repo, `runtime/tests/${project}/AgenticPrReview.Runtime.${project}.csproj`),
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '--artifacts-path',
        join(root, 'support-artifacts-' + name),
        '-o',
        join(root, name),
        '-p:PublishAot=false',
        '-p:SelfContained=false',
        '-p:UseAppHost=false',
        '-p:UseSharedCompilation=false',
        '-p:NuGetAudit=false',
        '-p:RestoreSources=https://api.nuget.org/v3/index.json',
        ...(name === 'cutover-faults'
          ? [
              '-p:APRManagedPackage=true',
              '-p:APRReleaseVersion=v0.0.0-internal.1',
              '-p:APRBuildId=' + '0'.repeat(64),
            ]
          : []),
      ],
      { cwd: repo, env: { ...builderEnv, NUGET_PACKAGES: process.env.NUGET_PACKAGES } },
    );
  }
  await prepareHistoricalSessionProducer(root, repo, builderEnv, run);
}

export async function runCutoverArchive(root, archivePath, inspected, sha256, roleDeadline) {
  const extracted = join(root, 'cutover-original-archive');
  await mkdir(extracted, { mode: 0o700 });
  run('/usr/bin/tar', ['-xzf', archivePath, '-C', extracted], { env: {}, roleDeadline });
  const verify = async () => {
    for (const file of inspected.files)
      assert.equal(sha256(await readFile(join(extracted, file.path))), sha256(file.bytes));
  };
  await verify();
  const entry = join(extracted, 'agentic-pr-review/AgenticPrReview.Runtime.dll');
  const fixture = join(
    root,
    'cutover-inputs/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll',
  );
  const historical = join(
    root,
    'cutover-historical/AgenticPrReview.Runtime.LiveAgentVerifierFixture.dll',
  );
  const dotnet = join(root, 'runtime/dotnet');
  const redaction = spawnSync(dotnet, [fixture, 'redaction-probe'], {
    env: {},
    encoding: 'utf8',
    timeout: roleDeadline ? roleDeadline.supportingMilliseconds(30_000) : 30_000,
    ...(roleDeadline ? { killSignal: 'SIGKILL' } : {}),
    maxBuffer: 8192,
  });
  assert.equal(redaction.status, 1);
  assert.equal(redaction.stdout, '');
  assert.equal(redaction.stderr.trim(), 'APR_SOLUS_CUTOVER_FAILED invalid_operation');
  const proof = run(dotnet, [fixture, dotnet, entry, historical, join(root, 'cutover-data')], {
    roleDeadline,
    cwd: root,
    env: { DOTNET_ROOT: join(root, 'runtime'), HOME: root, TMPDIR: join(root, 'tmp') },
  });
  assert.equal(
    proof.trim(),
    'APR_SOLUS_CUTOVER direct_processes=9 fresh=true restored=true old_session_rejected=true false_absence_rejected=true same_head_rejected=true invocation_reuse_rejected=true spent_source_rejected=true selected_current_missing_rejected=true',
  );
  await verify();
  const managedFixture = join(
    root,
    'cutover-faults/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll',
  );
  const solus = inspected.files.filter((file) =>
    /^SolusAgent\..+\.dll$/u.test(basename(file.path)),
  );
  assert.equal(solus.length, 5);
  for (const file of solus)
    assert.equal(
      sha256(await readFile(join(root, 'cutover-faults', basename(file.path)))),
      sha256(file.bytes),
    );
  const faults = run(dotnet, [managedFixture, 'managed-faults', join(root, 'cutover-fault-data')], {
    roleDeadline,
    cwd: root,
    env: { DOTNET_ROOT: join(root, 'runtime'), HOME: root, TMPDIR: join(root, 'tmp') },
  });
  assert.equal(
    faults.trim(),
    'APR_SOLUS_DIRECT_FAULTS capture_cases=5 import_burn=true repeated_claim_refused=true incompatible_no_work=true',
  );
  return {
    kind: 'apr-r6-direct-original-archive-v1',
    direct_processes: 9,
    selected_current_missing_rejected: true,
    entry_dll_sha256: sha256(await readFile(entry)),
    supporting_fixture_sha256: sha256(await readFile(fixture)),
    supporting_historical_producer_sha256: sha256(await readFile(historical)),
    original_members_unchanged: true,
    capture_fault_cases: 5,
    import_failure_burned: true,
    incompatible_context_rejected_before_work: true,
    supporting_managed_fixture_sha256: sha256(await readFile(managedFixture)),
    supporting_loaded_package_bytes_match_archive: true,
    identifier_exception_message_redacted: true,
    old_session_rejected_before_provider: true,
    live_provider_requests: 0,
  };
}

// A current-source nonmanaged DLL receives the exact launch admitted for the
// real Action. It cannot run review.
export async function runNonmanagedNoWork(root, launch, sha256, roleDeadline) {
  assert.ok(launch && typeof launch === 'object');
  const entry = join(root, 'cutover-inputs/AgenticPrReview.Runtime.dll');
  const { code, completion } = await supportingHost(
    root,
    launch,
    [entry, 'run'],
    join(root, 'native-no-work-tmp'),
    roleDeadline,
  );
  assert.equal(code, 1);
  assert.equal(completion.outputs.status, 'internal_failure');
  assert.equal(completion.protocol, 'apr.cli.result.cr2');
  assert.equal(completion.outputs['termination-reason'], 'not_started');
  assert.ok(completion.summary.includes('State disposition: not_committed\n'));
  assert.ok(completion.summary.includes('Publication URL: Not available\n'));
  assert.equal(completion.outputs['attempt-accounting-completeness'], 'complete');
  assert.equal(completion.outputs['usage-completeness'], 'complete');
  for (const [key, value] of Object.entries(completion.outputs))
    if (key !== 'status' && key !== 'termination-reason' && !key.endsWith('completeness'))
      assert.equal(value, '0');
  return {
    kind: 'apr-r6-supporting-native-no-work-v1',
    supporting_dll_sha256: sha256(await readFile(entry)),
    supporting_publish_inventory_sha256: await supportingInventory(
      join(root, 'cutover-inputs'),
      sha256,
    ),
    termination_reason: completion.outputs['termination-reason'],
    status: completion.outputs.status,
    accounting_complete_zero: true,
    archive_qualification: false,
  };
}

async function supportingHost(root, launch, args, temp, roleDeadline, maximum = 45_000) {
  await mkdir(temp, { mode: 0o700 });
  const document = Buffer.from(JSON.stringify(launch));
  const header = Buffer.alloc(4);
  header.writeUInt32BE(document.length);
  const milliseconds = roleDeadline ? roleDeadline.supportingMilliseconds(maximum) : maximum;
  const child = spawn(join(root, 'runtime/dotnet'), args, {
    cwd: root,
    env: { TMPDIR: temp, DOTNET_EnableDiagnostics: '0', DOTNET_NOLOGO: '1' },
    stdio: ['pipe', 'pipe', 'pipe'],
    detached: true,
  });
  const output = [],
    errors = [];
  let bytes = 0;
  for (const [stream, destination] of [
    [child.stdout, output],
    [child.stderr, errors],
  ])
    stream.on('data', (chunk) => {
      bytes += chunk.length;
      if (bytes > 32768) child.kill('SIGKILL');
      else destination.push(chunk);
    });
  child.stdin.end(Buffer.concat([header, document]));
  child.stdin.on('error', () => {});
  let code;
  try {
    const result = await awaitFixtureGroupClose(child, milliseconds);
    assert.equal(result.watchdogFired, false, 'r6_support_watchdog');
    assert.equal(result.signal, null, 'r6_native_no_work_signal');
    code = result.code;
  } finally {
    document.fill(0);
  }
  const frame = Buffer.concat(output);
  assert.ok(frame.length >= 4 && frame.length <= 16388);
  assert.equal(frame.readUInt32BE(0), frame.length - 4);
  const completion = JSON.parse(frame.subarray(4).toString('utf8'));
  assert.equal(code, completion.process_exit_code);
  assert.equal(Buffer.concat(errors).length, 0);
  return { code, completion };
}

export async function runHistoricalAction(root, launch, environment, sha256, roleDeadline) {
  const maximum = roleDeadline ? roleDeadline.supportingMilliseconds(90_000) : 90_000;
  const end = monotonicMilliseconds() + maximum;
  const remaining = () => {
    const milliseconds = end - monotonicMilliseconds();
    if (milliseconds <= 0) throw new Error('r6_historical_watchdog');
    return milliseconds;
  };
  // This exact pinned producer retains its historical typed/flat ABI. Keep
  // conversion confined to this named supporting fixture, never production.
  assert.equal(launch.protocol, 'apr.cli.run.cr2');
  assert.equal(launch.facts.run_id, environment.GITHUB_RUN_ID);
  const raw = new Map(launch.inputs.map(({ name, value }) => [name, value]));
  const historical = {
    ...launch.facts,
    build_discriminator: launch.build_discriminator,
    cancellation: launch.cancellation,
    artifact_bridge_endpoint: launch.upload_endpoint,
    inputs: {
      github_token: raw.get('github-token') || null,
      provider_api_key: raw.get('provider-api-key') || null,
      state_key: raw.get('state-key') || null,
      previous_state_key: raw.get('previous-state-key') || null,
      config_path: raw.get('config-path') || null,
      pr_number: raw.get('pr-number') || null,
      state_mode: raw.get('state-mode') || 'auto',
    },
  };
  const launchPath = join(root, 'historical-action-launch.json');
  await writeFile(launchPath, JSON.stringify(historical), { mode: 0o600, flag: 'wx' });
  const bridgeMilliseconds = remaining();
  const bridge = spawn(process.execPath, [join(root, 'cutover-bridge.mjs'), launchPath], {
    cwd: root,
    env: environment,
    stdio: ['pipe', 'pipe', 'pipe'],
    detached: true,
  });
  const lines = createInterface({ input: bridge.stdout });
  const received = [];
  let size = 0;
  bridge.stderr.on('data', (bytes) => {
    size += bytes.length;
    if (size > 32768) bridge.kill('SIGKILL');
  });
  lines.on('line', (line) => {
    size += line.length;
    if (size > 32768) bridge.kill('SIGKILL');
    received.push(line);
  });
  const closed = awaitFixtureGroupClose(bridge, bridgeMilliseconds);
  void closed.catch(() => {});
  try {
    let ready;
    while (true) {
      const line = received.find((line) => line.startsWith('R6_BRIDGE_READY '));
      if (line) {
        ready = JSON.parse(line.slice('R6_BRIDGE_READY '.length));
        break;
      }
      await Promise.race([
        once(lines, 'line'),
        closed.then(() => {
          throw new Error('r6_bridge_exit');
        }),
      ]);
    }
    const producer = join(
      root,
      'cutover-pinned-action/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll',
    );
    const actual = { ...historical, artifact_bridge_endpoint: ready.endpoint };
    const { code, completion } = await supportingHost(
      root,
      actual,
      [producer, 'historical-action'],
      join(root, 'historical-action-tmp'),
      roleDeadline,
      Math.min(45_000, remaining()),
    );
    if (code !== 0)
      process.stderr.write('APR_R6_HISTORICAL_PRODUCER_FAILURE status=' + completion.status + '\n');
    assert.equal(code, 0);
    assert.equal(completion.status, 'reviewed');
    assert.equal(completion.summary.state_disposition, 'accepted');
    assert.equal(completion.accounting.model_calls, '2');
    bridge.stdin.end('STOP\n');
    const result = await closed;
    assert.equal(result.watchdogFired, false, 'r6_historical_watchdog');
    assert.equal(result.code, 0);
    assert.equal(result.signal, null);
    assert.ok(received.includes('R6_BRIDGE_STOPPED'));
    return {
      kind: 'apr-r6-genuine-native-session-producer-v1',
      logicalNames: JSON.parse(
        received
          .find((line) => line.startsWith('R6_BRIDGE_NAMES '))
          .slice('R6_BRIDGE_NAMES '.length),
      ),
      supporting_fixture_sha256: sha256(await readFile(producer)),
      supporting_publish_inventory_sha256: await supportingInventory(
        join(root, 'cutover-pinned-action'),
        sha256,
      ),
      supporting_bridge_sha256: sha256(await readFile(join(root, 'cutover-bridge.mjs'))),
      genuine_native_session: true,
      historical_source: JSON.parse(
        await readFile(join(root, 's5-historical-producer.json'), 'utf8'),
      ),
    };
  } finally {
    lines.close();
    if (bridge.exitCode === null && bridge.signalCode === null) {
      try {
        process.kill(-bridge.pid, 'SIGKILL');
      } catch {}
    }
    await closed;
  }
}

export async function verifyResetRetryInventory(root, before, after, names, sha256, roleDeadline) {
  const entries = (snapshot) =>
    snapshot.map(([id, item]) => {
      const name = names.find((value) =>
        item.metadata.name.startsWith(
          'apr-object-' + sha256(Buffer.from('apr-artifact-family\0' + value)) + '-',
        ),
      );
      assert.equal(typeof name, 'string');
      return {
        id: String(id),
        name,
        archive: item.bytes.toString('base64'),
        expires: Math.floor(Date.parse(item.metadata.expires_at) / 1000),
      };
    });
  const path = join(root, 'reset-inventory.json');
  await writeFile(path, JSON.stringify({ before: entries(before), after: entries(after) }), {
    mode: 0o600,
    flag: 'wx',
  });
  const proof = run(
    join(root, 'runtime/dotnet'),
    [
      join(root, 'cutover-inputs/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll'),
      'reset-inventory',
      path,
    ],
    {
      roleDeadline,
      cwd: root,
      env: { DOTNET_ROOT: join(root, 'runtime'), HOME: root, TMPDIR: join(root, 'tmp') },
    },
  );
  assert.equal(
    proof.trim(),
    'APR_SOLUS_RESET_RETRY logical_identity_unchanged=true accepted_records_unchanged=true unexpected_mutations_rejected=true',
  );
}
