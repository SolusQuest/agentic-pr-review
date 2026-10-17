import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const fixture =
  'runtime/tests/SolusContextAuthorityFixture/AgenticPrReview.Runtime.SolusContextAuthorityFixture.csproj';
function check(value) {
  if (!value) throw new Error('APR_SOLUS_CONTEXT_VERIFY_FAILED');
}
try {
  check(process.platform === 'linux' && process.arch === 'x64');
  const os = readFileSync('/etc/os-release', 'utf8');
  check(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os));
  const root = resolve(process.env.RUNNER_TEMP, 'apr-solus-s2');
  mkdirSync(root, { mode: 0o700 });
  for (const directory of ['home', 'tmp']) mkdirSync(join(root, directory), { mode: 0o700 });
  // Explicit allowlist: no restore token, provider credential, plugin or ambient
  // NuGet home reaches any build, test, loader or controlled provider process.
  const env = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(root, 'home'),
    DOTNET_CLI_HOME: join(root, 'home'),
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: resolve(process.env.NUGET_PACKAGES),
    TMPDIR: join(root, 'tmp'),
  };
  const properties = [
    '-p:APRManagedPackage=true',
    '-p:APRReleaseVersion=v0.0.0-internal.1',
    '-p:APRBuildId=' + '0'.repeat(64),
    '-p:NuGetAudit=false',
  ];
  const run = (args) => {
    const result = spawnSync('dotnet', args, {
      env,
      encoding: 'utf8',
      timeout: 300_000,
      maxBuffer: 16 * 1024 * 1024,
    });
    if (result.status !== 0) {
      const codes = [
        ...new Set(
          ((result.stdout || '') + (result.stderr || '')).match(/\b(?:CS|MSB|NU|NETSDK)\d{4}\b/g) ||
            [],
        ),
      ];
      const phase = args[0] === 'restore' || args[0] === 'build' ? args[0] : 'proof';
      const failures = (result.stderr || '')
        .split(/\r?\n/)
        .filter((line) =>
          /^APR_SOLUS_CONTEXT_(?:PHASE [a-z-]+|FAILED [A-Za-z]+(?: [a-z_]+)?)$/.test(line),
        )
        .slice(0, 4);
      console.error(
        JSON.stringify({
          stage: 'S2',
          phase,
          status: result.status,
          signal: result.signal,
          codes,
          failures,
        }),
      );
      if (args[0] === 'build') {
        const diagnostics = [
          ...new Set(
            (result.stdout || '')
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
  const proof = run([join(output, 'AgenticPrReview.Runtime.SolusContextAuthorityFixture.dll')]);
  check(
    /^APR_SOLUS_CONTEXT_PROOF negative_checks=23 races=3 winners=3 shared_lease=true pending_checks=3 import_burn=true third_process_spent=true credential_free=true http_calls=0\s*$/.test(
      proof,
    ),
  );
  console.log(proof.trim());
} catch {
  console.error('APR_SOLUS_CONTEXT_VERIFY_FAILED');
  process.exitCode = 1;
}
