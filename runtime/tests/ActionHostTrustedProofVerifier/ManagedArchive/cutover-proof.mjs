import { prepareHistoricalSessionProducer } from './historical-session-producer.mjs';
import { projectCutoverFailureCode } from './cutover-failure-code.mjs';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdir, readFile, readdir, writeFile } from 'node:fs/promises';
import { once } from 'node:events';
import { createInterface } from 'node:readline';
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

function run(command, args, options) {
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    timeout: 300_000,
    maxBuffer: 2 * 1024 * 1024,
    ...options,
  });
  if (result.error || result.status !== 0) {
    const output = (result.stdout ?? '') + (result.stderr ?? '');
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

export async function runCutoverArchive(root, archivePath, inspected, sha256) {
  const extracted = join(root, 'cutover-original-archive');
  await mkdir(extracted, { mode: 0o700 });
  run('/usr/bin/tar', ['-xzf', archivePath, '-C', extracted], { env: {} });
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
    timeout: 30_000,
    maxBuffer: 8192,
  });
  assert.equal(redaction.status, 1);
  assert.equal(redaction.stdout, '');
  assert.equal(redaction.stderr.trim(), 'APR_SOLUS_CUTOVER_FAILED InvalidOperationException');
  const proof = run(dotnet, [fixture, dotnet, entry, historical, join(root, 'cutover-data')], {
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
export async function runNonmanagedNoWork(root, launch, sha256) {
  assert.ok(launch && typeof launch === 'object');
  const entry = join(root, 'cutover-inputs/AgenticPrReview.Runtime.dll');
  const { code, completion } = await supportingHost(
    root,
    launch,
    [entry],
    join(root, 'native-no-work-tmp'),
  );
  assert.equal(code, 1);
  assert.equal(completion.status, 'internal_failure');
  assert.equal(completion.exit_class, 'internal');
  assert.equal(completion.termination_reason, 'not_started');
  assert.equal(completion.summary.state_disposition, 'not_committed');
  assert.equal(completion.summary.publication_url, null);
  assert.equal(completion.accounting.attempt_accounting_completeness, 'complete');
  assert.equal(completion.accounting.usage_completeness, 'complete');
  for (const [key, value] of Object.entries(completion.accounting))
    if (!key.endsWith('_completeness')) assert.equal(value, '0');
  return {
    kind: 'apr-r6-supporting-native-no-work-v1',
    supporting_dll_sha256: sha256(await readFile(entry)),
    supporting_publish_inventory_sha256: await supportingInventory(
      join(root, 'cutover-inputs'),
      sha256,
    ),
    termination_reason: completion.termination_reason,
    status: completion.status,
    accounting_complete_zero: true,
    archive_qualification: false,
  };
}

async function supportingHost(root, launch, args, temp) {
  await mkdir(temp, { mode: 0o700 });
  const document = Buffer.from(JSON.stringify(launch));
  const header = Buffer.alloc(4);
  header.writeUInt32BE(document.length);
  const child = spawn(join(root, 'runtime/dotnet'), args, {
    cwd: root,
    env: { TMPDIR: temp, DOTNET_EnableDiagnostics: '0', DOTNET_NOLOGO: '1' },
    stdio: ['pipe', 'pipe', 'pipe'],
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
  const timeout = setTimeout(() => child.kill('SIGKILL'), 45_000);
  child.stdin.end(Buffer.concat([header, document]));
  child.stdin.on('error', () => {});
  let code;
  try {
    code = await new Promise((resolve, reject) => {
      child.once('error', reject);
      child.once('close', (exit, signal) => {
        if (signal) reject(new Error('r6_native_no_work_signal'));
        else resolve(exit);
      });
    });
  } finally {
    clearTimeout(timeout);
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

export async function runHistoricalAction(root, launch, environment, sha256) {
  const launchPath = join(root, 'historical-action-launch.json');
  await writeFile(launchPath, JSON.stringify(launch), { mode: 0o600, flag: 'wx' });
  const bridge = spawn(process.execPath, [join(root, 'cutover-bridge.mjs'), launchPath], {
    cwd: root,
    env: environment,
    stdio: ['pipe', 'pipe', 'pipe'],
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
  const timeout = setTimeout(() => bridge.kill('SIGKILL'), 90_000);
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
        once(bridge, 'exit').then(() => {
          throw new Error('r6_bridge_exit');
        }),
      ]);
    }
    const producer = join(
      root,
      'cutover-pinned-action/AgenticPrReview.Runtime.SolusProductionCutoverFixture.dll',
    );
    const actual = { ...launch, artifact_bridge_endpoint: ready.endpoint };
    const { code, completion } = await supportingHost(
      root,
      actual,
      [producer, 'historical-action'],
      join(root, 'historical-action-tmp'),
    );
    if (code !== 0)
      process.stderr.write('APR_R6_HISTORICAL_PRODUCER_FAILURE status=' + completion.status + '\n');
    assert.equal(code, 0);
    assert.equal(completion.status, 'reviewed');
    assert.equal(completion.summary.state_disposition, 'accepted');
    assert.equal(completion.accounting.model_calls, '2');
    bridge.stdin.end('STOP\n');
    await once(bridge, 'exit');
    assert.equal(bridge.exitCode, 0);
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
    clearTimeout(timeout);
    lines.close();
    if (bridge.exitCode === null && bridge.signalCode === null) {
      bridge.kill('SIGKILL');
      await once(bridge, 'exit');
    }
  }
}

export async function verifyResetRetryInventory(root, before, after, names, sha256) {
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
      cwd: root,
      env: { DOTNET_ROOT: join(root, 'runtime'), HOME: root, TMPDIR: join(root, 'tmp') },
    },
  );
  assert.equal(
    proof.trim(),
    'APR_SOLUS_RESET_RETRY logical_identity_unchanged=true accepted_records_unchanged=true unexpected_mutations_rejected=true',
  );
}
