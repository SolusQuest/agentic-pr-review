import { spawnSync, execFileSync } from 'node:child_process';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { SOLUS_VERSION } from '../../../scripts/release/solus-package-contract.mjs';

const fixture =
  'runtime/tests/ActionHostTrustedProofVerifier/SolusFresh/AgenticPrReview.Runtime.SolusFreshHostProof.csproj';
const cases = [
  'cs-defect',
  'cs-safe',
  'ts-defect',
  'ts-safe',
  'repository-rule',
  'sticky-only',
  'no-required-tool',
  'irrelevant-tool',
  'wrong-evidence',
  'wrong-location',
  'safe-invention',
  'duplicate-proposal',
  'pathless-proposal',
];
const evaluations = [
  ...Array(6).fill('Scored'),
  'RequiredToolMissing',
  'RequiredObservationMissing',
  'ExecutionFailed',
  'ExpectedFindingMissing',
  'ProhibitedFinding',
  'DuplicateObservation',
  'ExecutionFailed',
];
function check(value) {
  if (!value) throw new Error('APR_SOLUS_FRESH_HOST_VERIFY_FAILED');
}

let stage = 'environment';
try {
  check(process.platform === 'linux' && process.arch === 'x64');
  const os = readFileSync('/etc/os-release', 'utf8');
  check(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os));
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  check(/^[a-f0-9]{40}$/.test(head));
  stage = 'workspace';
  const root = resolve(process.env.RUNNER_TEMP, 'apr-solus-r5');
  mkdirSync(root, { mode: 0o700 });
  for (const directory of ['home', 'tmp']) mkdirSync(join(root, directory), { mode: 0o700 });
  const env = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(root, 'home'),
    DOTNET_CLI_HOME: join(root, 'home'),
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: resolve(process.env.NUGET_PACKAGES),
    TMPDIR: join(root, 'tmp'),
    APR_R5_SOURCE_SHA: head,
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
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
  const run = (args) => {
    const result = spawnSync('dotnet', args, {
      env,
      encoding: 'utf8',
      timeout: 300_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.status !== 0) {
      const output = (result.stdout || '') + (result.stderr || '');
      const codes = [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)\d{4}\b/g) || [])];
      const failures = output
        .split(/\r?\n/)
        .filter((line) => /^APR_SOLUS_FRESH_HOST_FAILED [a-z0-9_-]+$/.test(line));
      console.error(
        JSON.stringify({
          stage: 'R5',
          status: result.status,
          signal: result.signal,
          codes,
          failures,
        }),
      );
      if (args[0] === 'build') {
        const diagnostics = [
          ...new Set(
            output
              .split(/\r?\n/)
              .filter((line) => line.includes('/runtime/') && /error CS\d{4}:/.test(line))
              .map((line) => line.slice(line.indexOf('/runtime/') + 1, 2048)),
          ),
        ];
        console.error(JSON.stringify({ diagnostics: diagnostics.slice(0, 20) }));
      }
    }
    check(result.status === 0);
    return result.stdout;
  };
  stage = 'restore';
  run([
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
  run([
    'build',
    fixture,
    '-c',
    'Release',
    '-r',
    'linux-x64',
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
    const id = name.toLowerCase();
    const archive = join(env.NUGET_PACKAGES, id, SOLUS_VERSION, `${id}.${SOLUS_VERSION}.nupkg`);
    const original = spawnSync('/usr/bin/unzip', ['-p', archive, `lib/net10.0/${name}.dll`], {
      env,
      maxBuffer: 64 * 1024 * 1024,
    });
    check(
      original.status === 0 && original.stdout.equals(readFileSync(join(output, name + '.dll'))),
    );
  }
  stage = 'execution';
  const proof = run([
    join(output, 'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll'),
    'runtime/tests/fixtures/agent/r5/quality/bundle',
  ]);
  stage = 'output_lines';
  const lines = proof.trim().split(/\r?\n/);
  check(lines.length === cases.length + 1);
  const summary = lines.pop();
  stage = 'summary_shape';
  check(
    /^APR_SOLUS_FRESH_HOST_PROOF cases=13 checks=\d+ controlled_sends=\d+ live_calls=0 effects=0 durable_acceptance=false credential_free=true$/.test(
      summary,
    ),
  );
  stage = 'summary_counts';
  const counts = summary.match(/checks=(\d+) controlled_sends=(\d+)/);
  check(counts && Number(counts[1]) >= 500 && Number(counts[2]) >= 90);
  const fields = [
    'case_id',
    'corpus_manifest_sha256',
    'source_sha',
    'profile',
    'package',
    'evaluation',
    'admission',
    'runtime',
    'terminal_accepted',
    'capture',
    'head',
    'durable_accepted',
    'logical_calls',
    'physical_attempts',
    'tools_invoked',
    'tools_admitted',
    'measured_input',
    'measured_output',
    'request_wire_bytes',
    'response_wire_bytes',
    'encoded_context_bytes',
    'logical_request_bytes',
    'logical_response_bytes',
    'provider_inputs',
    'retained_records',
    'retained_attempts',
    'input_reservation',
    'output_reservation',
  ].sort();
  const rows = lines.map((line, index) => {
    stage = `row_${index}_fields`;
    const row = JSON.parse(line);
    check(JSON.stringify(Object.keys(row).sort()) === JSON.stringify(fields));
    stage = `row_${index}_identity`;
    check(
      row.case_id === cases[index] &&
        row.evaluation === evaluations[index] &&
        row.source_sha === head &&
        row.corpus_manifest_sha256 ===
          '55a4fa8cca7f081169c4f1f09503c8a5c8f23197c1960ca09fb6266bcb7375b3' &&
        row.profile === 'cr1-solus-internal4-dev' &&
        row.package === SOLUS_VERSION &&
        row.durable_accepted === false &&
        typeof row.terminal_accepted === 'boolean',
    );
    stage = `row_${index}_admission`;
    check(['Fresh', 'NotAttempted'].includes(row.admission));
    stage = `row_${index}_runtime`;
    check(['Completed', 'Partial', 'Failed', 'Cancelled', 'ResourceLimit'].includes(row.runtime));
    if (row.runtime === 'Partial')
      check(
        row.evaluation === 'ExecutionFailed' &&
          row.terminal_accepted === false &&
          row.head === null,
      );
    stage = `row_${index}_capture`;
    check(['NotRequested', 'Unavailable', 'Delivered', 'Failed'].includes(row.capture));
    stage = `row_${index}_head`;
    check(row.head === null || row.head === 'Exact');
    stage = `row_${index}_numbers`;
    for (const key of fields.filter(
      (key) =>
        key.endsWith('_bytes') ||
        key.endsWith('_reservation') ||
        [
          'logical_calls',
          'physical_attempts',
          'tools_invoked',
          'tools_admitted',
          'measured_input',
          'measured_output',
          'provider_inputs',
          'retained_records',
          'retained_attempts',
        ].includes(key),
    ))
      check(
        row[key] === null ||
          (Number.isSafeInteger(row[key]) && row[key] >= 0 && row[key] <= 10_000_000),
      );
    return row;
  });
  // Print only the validated closed numeric/identity schema, never raw child output.
  for (const row of rows) console.log(JSON.stringify(row));
  console.log(summary);
} catch {
  console.error('APR_SOLUS_FRESH_HOST_VERIFY_FAILED ' + stage);
  process.exitCode = 1;
}
