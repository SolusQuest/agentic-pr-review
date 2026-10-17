import { spawnSync } from 'node:child_process';
import { mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { SOLUS_VERSION } from '../../../scripts/release/solus-package-contract.mjs';

const fixture =
  'runtime/tests/SolusProviderAccountingFixture/AgenticPrReview.Runtime.SolusProviderAccountingFixture.csproj';
function check(value) {
  if (!value) throw new Error('APR_SOLUS_PROVIDER_VERIFY_FAILED');
}
try {
  check(process.platform === 'linux' && process.arch === 'x64');
  const os = readFileSync('/etc/os-release', 'utf8');
  check(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os));
  const root = resolve(process.env.RUNNER_TEMP, 'apr-solus-r4');
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
          ((result.stdout || '') + (result.stderr || '')).match(/\b(?:CS|MSB|NU)\d{4}\b/g) || [],
        ),
      ];
      console.error(JSON.stringify({ stage: 'R4', codes }));
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
  // Compare actual loaded DLLs with original already-verified archives, not
  // assembly-version labels or metadata alone. No private bytes are emitted.
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
  const proof = run([join(output, 'AgenticPrReview.Runtime.SolusProviderAccountingFixture.dll')]);
  check(
    /^APR_SOLUS_PROVIDER_PROOF checks=\d+ controlled_sends=\d+ live_calls=0 credential_free=true candidate_gate=true\s*$/.test(
      proof,
    ),
  );
  const counts = proof.match(/checks=(\d+) controlled_sends=(\d+)/);
  check(counts && Number(counts[1]) >= 80 && Number(counts[2]) >= 20);
  console.log(proof.trim());
} catch {
  console.error('APR_SOLUS_PROVIDER_VERIFY_FAILED');
  process.exitCode = 1;
}
