import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { mkdir, readFile } from 'node:fs/promises';
import { join } from 'node:path';

function run(command, args, options) {
  const result = spawnSync(command, args, {
    encoding: 'utf8',
    timeout: 300_000,
    maxBuffer: 2 * 1024 * 1024,
    ...options,
  });
  if (result.error || result.status !== 0) {
    const output = (result.stdout ?? '') + (result.stderr ?? '');
    const diagnostics = [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)[0-9]{4}\b/gu) ?? [])];
    const failures = output.match(/^APR_SOLUS_CUTOVER_FAILED [a-zA-Z0-9_]+$/gmu) ?? [];
    process.stderr.write(JSON.stringify({ stage: 'r6-cutover', diagnostics, failures }) + '\n');
  }
  assert.equal(result.error, undefined, 'r6_cutover_process_error');
  assert.equal(result.status, 0, 'r6_cutover_process_failed');
  return result.stdout;
}

// Supporting input/observation tools have their own outputs and identities.
// Neither is installed in or selected as an entry of the managed archive.
export async function prepareCutoverSupport(root, repo, builderEnv) {
  for (const [name, project] of [
    ['cutover-inputs', 'SolusProductionCutoverFixture'],
    ['cutover-historical', 'LiveAgentVerifierFixture'],
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
      ],
      { cwd: repo, env: { ...builderEnv, NUGET_PACKAGES: process.env.NUGET_PACKAGES } },
    );
  }
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
  const proof = run(dotnet, [fixture, dotnet, entry, historical, join(root, 'cutover-data')], {
    cwd: root,
    env: { DOTNET_ROOT: join(root, 'runtime'), HOME: root, TMPDIR: join(root, 'tmp') },
  });
  assert.equal(
    proof.trim(),
    'APR_SOLUS_CUTOVER direct_processes=8 fresh=true restored=true old_session_rejected=true false_absence_rejected=true same_head_rejected=true invocation_reuse_rejected=true spent_source_rejected=true',
  );
  await verify();
  return {
    kind: 'apr-r6-direct-original-archive-v1',
    direct_processes: 8,
    entry_dll_sha256: sha256(await readFile(entry)),
    supporting_fixture_sha256: sha256(await readFile(fixture)),
    supporting_historical_producer_sha256: sha256(await readFile(historical)),
    original_members_unchanged: true,
    old_session_rejected_before_provider: true,
    live_provider_requests: 0,
  };
}
