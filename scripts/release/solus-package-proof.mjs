import { execFileSync, spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
import { packageHashes } from './solus-package-contract.mjs';

const version = '0.1.0-internal.1';
const privateFeed = 'https://nuget.pkg.github.com/SolusQuest/index.json';
const publicFeed = 'https://api.nuget.org/v3/index.json';
const ids = [
  'solusagent.api',
  'solusagent.tools.api',
  'solusagent.runtime.api',
  'solusagent.runtime',
  'solusagent.providers.deepseek',
];
function requireThat(value, code) {
  if (!value) throw new Error(code);
}

// Metadata collection is preparatory evidence, never final package/startup qualification.
// No package bytes or raw subprocess diagnostics are emitted or uploaded.
async function collect(output) {
  requireThat(process.platform === 'linux' && process.arch === 'x64', 'APR_SOLUS_RUNNER');
  const os = await readFile('/etc/os-release', 'utf8');
  requireThat(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'APR_SOLUS_RUNNER');
  const head = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  requireThat(head === process.env.EXPECTED_HEAD && /^[a-f0-9]{40}$/.test(head), 'APR_SOLUS_HEAD');
  requireThat(
    execFileSync('git', ['status', '--porcelain'], { encoding: 'utf8' }) === '',
    'APR_SOLUS_DIRTY',
  );
  const token = process.env.GITHUB_TOKEN;
  const actor = process.env.GITHUB_ACTOR;
  requireThat(token && actor && /^[\w-]+(?:\[bot\])?$/.test(actor), 'APR_SOLUS_AUTH_UNAVAILABLE');
  output = resolve(output);
  requireThat(output.startsWith(resolve(process.env.RUNNER_TEMP) + sep), 'APR_SOLUS_OUTPUT');
  await mkdir(output, { mode: 0o700 });
  for (const directory of ['packages', 'http', 'plugins', 'home', 'tmp'])
    await mkdir(join(output, directory), { mode: 0o700 });
  const env = {
    PATH: process.env.PATH,
    DOTNET_ROOT: process.env.DOTNET_ROOT,
    HOME: join(output, 'home'),
    DOTNET_CLI_HOME: join(output, 'home'),
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: join(output, 'packages'),
    NUGET_HTTP_CACHE_PATH: join(output, 'http'),
    NUGET_PLUGINS_CACHE_PATH: join(output, 'plugins'),
    TMPDIR: join(output, 'tmp'),
  };
  const lock = join(output, 'actual-apr.lock.json');
  const args = [
    'restore',
    'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
    '-r',
    'linux-x64',
    '--no-http-cache',
    '--force-evaluate',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    '-p:APRManagedPackage=true',
    '-p:RestoreLockedMode=false',
    '-p:NuGetLockFilePath=' + lock,
    '-p:NuGetAudit=false',
    '-p:RestoreFallbackFolders=',
  ];
  const restored = spawnSync('dotnet', args, {
    env: {
      ...env,
      NuGetPackageSourceCredentials_solus: `Username=${actor};Password=${token};ValidAuthenticationTypes=Basic`,
    },
    encoding: 'utf8',
    maxBuffer: 16 * 1024 * 1024,
    timeout: 300_000,
  });
  requireThat(restored.status === 0, 'APR_SOLUS_RESTORE_FAILED');
  const assets = JSON.parse(
    await readFile('runtime/src/AgenticPrReview.Runtime/obj/managed/project.assets.json', 'utf8'),
  );
  requireThat(
    Object.keys(assets.packageFolders).length === 1 &&
      resolve(Object.keys(assets.packageFolders)[0]) === env.NUGET_PACKAGES,
    'APR_SOLUS_CACHE',
  );
  const packages = [];
  for (const [identity, library] of Object.entries(assets.libraries)) {
    requireThat(library.type === 'package', 'APR_SOLUS_FOREIGN_PROJECT');
    const [id, resolvedVersion] = identity.split('/');
    const lower = id.toLowerCase();
    if (lower.startsWith('solusagent.'))
      requireThat(ids.includes(lower) && resolvedVersion === version, 'APR_SOLUS_PIN');
    const root = join(env.NUGET_PACKAGES, library.path);
    const metadata = JSON.parse(await readFile(join(root, '.nupkg.metadata'), 'utf8'));
    const expectedFeed = ids.includes(lower) ? privateFeed : publicFeed;
    requireThat(metadata.source?.replace(/\/$/, '') === expectedFeed, 'APR_SOLUS_SOURCE');
    const bytes = await readFile(join(root, `${lower}.${resolvedVersion}.nupkg`));
    const hashes = packageHashes(
      bytes,
      metadata.contentHash,
      library.sha512,
      (await readFile(join(root, `${lower}.${resolvedVersion}.nupkg.sha512`), 'utf8')).trim(),
    );
    packages.push({
      id: lower,
      version: resolvedVersion,
      ...hashes,
      role: 'managed',
      source: expectedFeed,
    });
  }
  requireThat(
    ids.every((id) => packages.some((item) => item.id === id)),
    'APR_SOLUS_MISSING',
  );
  requireThat(packages.length <= 16, 'APR_SOLUS_INVENTORY');
  packages.sort((a, b) => (a.id < b.id ? -1 : a.id > b.id ? 1 : 0));
  const facts = {
    qualification: false,
    mode: 'collect',
    sourceCommit: head,
    version,
    coldCache: true,
    packages,
    actualAprLock: JSON.parse(await readFile(lock, 'utf8')),
  };
  await writeFile(join(output, 'metadata.json'), JSON.stringify(facts, null, 2) + '\n', {
    flag: 'wx',
  });
  console.log(
    JSON.stringify({
      qualification: false,
      mode: 'collect',
      sourceCommit: head,
      version,
      coldCache: true,
      packages: packages.length,
    }),
  );
}
try {
  requireThat(process.argv[2] === 'collect' && process.argv.length === 4, 'APR_SOLUS_USAGE');
  await collect(process.argv[3]);
} catch (error) {
  // Fixed code only: never echo credential-bearing subprocess output or arbitrary exceptions.
  console.error(
    /^APR_SOLUS_[A-Z_]+$/.test(error.message) ? error.message : 'APR_SOLUS_PROOF_FAILED',
  );
  process.exitCode = 1;
}
