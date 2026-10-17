import { execFileSync, spawnSync } from 'node:child_process';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { join, resolve, sep } from 'node:path';
// Capture restore authority before loading project modules or running commands.
// Child environments are still explicit: deleting only the parent token would
// leave other inherited NuGet/GitHub credentials available to new call sites.
const token = process.env.GITHUB_TOKEN;
const actor = process.env.GITHUB_ACTOR;
for (const key of Object.keys(process.env)) {
  if (/^(GITHUB_TOKEN|GH_TOKEN|NUGET_AUTH_TOKEN|NuGetPackageSourceCredentials_.*)$/i.test(key))
    delete process.env[key];
}
const commandEnv = { PATH: process.env.PATH, DOTNET_ROOT: process.env.DOTNET_ROOT };
const { SOLUS_VERSION, SOLUS_RELEASE_SOURCE, SOLUS_FEED, PUBLIC_FEED, SOLUS_IDS, packageHashes } =
  await import('./solus-package-contract.mjs');
const { managedDependencyPolicy, verifyDependencies } = await import('./build-payload.mjs');

const version = SOLUS_VERSION,
  privateFeed = SOLUS_FEED,
  publicFeed = PUBLIC_FEED,
  ids = SOLUS_IDS;
function requireThat(value, code) {
  if (!value) throw new Error(code);
}

// Metadata collection is preparatory evidence, never final package/startup qualification.
// No package bytes or raw subprocess diagnostics are emitted or uploaded.
async function collect(output, verify) {
  requireThat(process.platform === 'linux' && process.arch === 'x64', 'APR_SOLUS_RUNNER');
  const os = await readFile('/etc/os-release', 'utf8');
  requireThat(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'APR_SOLUS_RUNNER');
  const head = execFileSync('git', ['rev-parse', 'HEAD'], {
    env: commandEnv,
    encoding: 'utf8',
  }).trim();
  requireThat(head === process.env.EXPECTED_HEAD && /^[a-f0-9]{40}$/.test(head), 'APR_SOLUS_HEAD');
  requireThat(
    execFileSync('git', ['status', '--porcelain'], { env: commandEnv, encoding: 'utf8' }) === '',
    'APR_SOLUS_DIRTY',
  );
  requireThat(token && actor && /^[\w-]+(?:\[bot\])?$/.test(actor), 'APR_SOLUS_AUTH_UNAVAILABLE');
  output = resolve(output);
  requireThat(output.startsWith(resolve(process.env.RUNNER_TEMP) + sep), 'APR_SOLUS_OUTPUT');
  await mkdir(output, { mode: 0o700 });
  for (const directory of ['packages', 'http', 'plugins', 'home', 'tmp'])
    await mkdir(join(output, directory), { mode: 0o700 });
  const env = {
    ...commandEnv,
    HOME: join(output, 'home'),
    DOTNET_CLI_HOME: join(output, 'home'),
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: join(output, 'packages'),
    NUGET_HTTP_CACHE_PATH: join(output, 'http'),
    NUGET_PLUGINS_CACHE_PATH: join(output, 'plugins'),
    TMPDIR: join(output, 'tmp'),
  };
  const lock = join(output, 'actual-apr.lock.json');
  const checkedLock = 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json';
  if (verify) await writeFile(lock, await readFile(checkedLock), { flag: 'wx' });
  const args = [
    'restore',
    'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
    '-r',
    'linux-x64',
    '--no-http-cache',
    verify ? '--locked-mode' : '--force-evaluate',
    '--configfile',
    'scripts/release/build-payload.managed.nuget.config',
    '-p:APRManagedPackage=true',
    '-p:RestoreLockedMode=' + verify,
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
    requireThat(
      /^[a-z0-9][a-z0-9.-]{0,100}$/.test(lower) && library.path === lower + '/' + resolvedVersion,
      'APR_SOLUS_PATH',
    );
    if (lower.startsWith('solusagent.'))
      requireThat(ids.includes(lower) && resolvedVersion === version, 'APR_SOLUS_PIN');
    const root = join(env.NUGET_PACKAGES, library.path);
    const metadata = JSON.parse(await readFile(join(root, '.nupkg.metadata'), 'utf8'));
    const expectedFeed = ids.includes(lower) ? privateFeed : publicFeed;
    requireThat(metadata.source?.replace(/\/$/, '') === expectedFeed, 'APR_SOLUS_SOURCE');
    const bytes = await readFile(join(root, `${lower}.${resolvedVersion}.nupkg`));
    const archiveSha512 = (
      await readFile(join(root, `${lower}.${resolvedVersion}.nupkg.sha512`), 'utf8')
    ).trim();
    const hashes = packageHashes(bytes, metadata.contentHash, library.sha512, archiveSha512);
    packages.push({
      id: lower,
      version: resolvedVersion,
      ...hashes,
      archiveSha512,
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
  let constructed = false;
  const packageRepositories = [];
  if (verify) {
    const policy = managedDependencyPolicy(
      JSON.parse(await readFile('scripts/release/build-payload.inputs.json', 'utf8')),
    );
    await verifyDependencies(env.NUGET_PACKAGES, policy, assets);
    requireThat(
      (await readFile(lock)).equals(await readFile(checkedLock)),
      'APR_SOLUS_LOCK_CHANGED',
    );
    const fixture =
      'runtime/tests/SolusStartupFixture/AgenticPrReview.Runtime.SolusStartupFixture.csproj';
    const properties = [
      '-p:APRManagedPackage=true',
      '-p:APRReleaseVersion=v0.0.0-internal.1',
      '-p:APRBuildId=' + '0'.repeat(64),
      '-p:NuGetAudit=false',
    ];
    const cleanRun = (args, code) => {
      const result = spawnSync('dotnet', args, {
        env,
        encoding: 'utf8',
        maxBuffer: 16 * 1024 * 1024,
        timeout: 300_000,
      });
      if (result.status !== 0) {
        const errors = [
          ...new Set(
            ((result.stdout || '') + (result.stderr || '')).match(
              /\b(?:CS|MSB|NU)\d{4}\b|APR_SOLUS_[A-Z_]+/g,
            ) || [],
          ),
        ];
        console.error(JSON.stringify({ stage: code, errorCodes: errors }));
        // The build is credential-free. Emit only bounded C# diagnostics from
        // the APR-owned source tree, never restore output or arbitrary logs.
        if (code === 'APR_SOLUS_FIXTURE_BUILD') {
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
      requireThat(result.status === 0, code);
      return result.stdout;
    };
    cleanRun(
      [
        'restore',
        fixture,
        '-r',
        'linux-x64',
        '--configfile',
        'scripts/release/build-payload.managed.nuget.config',
        ...properties,
      ],
      'APR_SOLUS_FIXTURE_RESTORE',
    );
    const startup = join(output, 'startup');
    cleanRun(
      [
        'build',
        fixture,
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '--no-restore',
        '--nologo',
        '-o',
        startup,
        ...properties,
      ],
      'APR_SOLUS_FIXTURE_BUILD',
    );
    const proof = cleanRun(
      [join(startup, 'AgenticPrReview.Runtime.SolusStartupFixture.dll')],
      'APR_SOLUS_STARTUP',
    );
    requireThat(
      proof.includes('constructed=true dispatches=0 negative_checks=8 credential_free=true'),
      'APR_SOLUS_STARTUP',
    );
    const names = [
      'SolusAgent.Api',
      'SolusAgent.Tools.Api',
      'SolusAgent.Runtime.Api',
      'SolusAgent.Runtime',
      'SolusAgent.Providers.DeepSeek',
    ];
    for (const name of names) {
      const archive = join(
        env.NUGET_PACKAGES,
        name.toLowerCase(),
        version,
        `${name.toLowerCase()}.${version}.nupkg`,
      );
      const original = execFileSync('/usr/bin/unzip', ['-p', archive, `lib/net10.0/${name}.dll`], {
        env,
        maxBuffer: 64 * 1024 * 1024,
      });
      requireThat(
        original.equals(await readFile(join(startup, name + '.dll'))),
        'APR_SOLUS_ASSEMBLY_BYTES',
      );
      // Signed archive identity is checked above. Bind emitted repository commit
      // metadata as well; when absent, keep publisher/tag provenance explicit.
      const nuspec = execFileSync('/usr/bin/unzip', ['-p', archive, '*.nuspec'], {
        env,
        encoding: 'utf8',
        maxBuffer: 256 * 1024,
      });
      const repositories = [...nuspec.matchAll(/<repository\b[^>]*\/?\s*>/g)];
      requireThat(repositories.length === 1, 'APR_SOLUS_RELEASE_SOURCE');
      const declaration = repositories[0][0];
      const url = /\burl=["']([^"']+)["']/.exec(declaration)?.[1];
      const commit = /\bcommit=["']([^"']+)["']/.exec(declaration)?.[1];
      requireThat(
        url === 'https://github.com/SolusQuest/solus-agent' &&
          (commit === undefined || commit === SOLUS_RELEASE_SOURCE),
        'APR_SOLUS_RELEASE_SOURCE',
      );
      packageRepositories.push({
        id: name.toLowerCase(),
        repository: url,
        commit: commit ?? null,
        provenance: commit ? 'nuspec-commit' : 'publisher-tag',
      });
    }
    const native = JSON.parse(
      cleanRun(
        [
          'msbuild',
          'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
          '-nologo',
          '-p:APRManagedPackage=false',
          '-getItem:PackageReference,Compile',
        ],
        'APR_SOLUS_NATIVE_SELECTION',
      ),
    );
    requireThat(
      !native.Items.PackageReference.some((item) =>
        item.Identity.toLowerCase().startsWith('solusagent.'),
      ) && !native.Items.Compile.some((item) => /(^|[/\\])Solus[/\\]/.test(item.Identity)),
      'APR_SOLUS_NATIVE_CLOSURE',
    );
    for (const property of [
      'PublishAot=true',
      'SelfContained=true',
      'APRReleasePackage=true',
      'SolusAgentVersion=0.1.0-internal.2',
    ]) {
      const rejected = spawnSync(
        'dotnet',
        [
          'msbuild',
          'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj',
          '-nologo',
          '-t:ValidateSolusManagedProfile',
          '-p:APRManagedPackage=true',
          '-p:' + property,
        ],
        { env, encoding: 'utf8', timeout: 30_000 },
      );
      requireThat(
        rejected.status !== 0 && rejected.stdout.includes('APR_SOLUS_MANAGED_PROFILE_UNSUPPORTED'),
        'APR_SOLUS_PROPERTY_REFUSAL',
      );
    }
    requireThat(
      execFileSync('git', ['status', '--porcelain'], { env, encoding: 'utf8' }) === '',
      'APR_SOLUS_DIRTY',
    );
    constructed = true;
  }
  const facts = {
    qualification: false,
    mode: verify ? 'verify' : 'collect',
    sourceCommit: head,
    publishedSourceAnchor: SOLUS_RELEASE_SOURCE,
    version,
    coldCache: true,
    constructed,
    producerProof: false,
    packages,
    packageRepositories,
    actualAprLock: JSON.parse(await readFile(lock, 'utf8')),
  };
  await writeFile(join(output, 'metadata.json'), JSON.stringify(facts, null, 2) + '\n', {
    flag: 'wx',
  });
  console.log(
    JSON.stringify({
      qualification: false,
      mode: verify ? 'verify' : 'collect',
      sourceCommit: head,
      publishedSourceAnchor: SOLUS_RELEASE_SOURCE,
      version,
      coldCache: true,
      packages: packages.length,
      constructed,
      producerProof: false,
    }),
  );
}
try {
  requireThat(
    ['collect', 'verify'].includes(process.argv[2]) && process.argv.length === 4,
    'APR_SOLUS_USAGE',
  );
  await collect(process.argv[3], process.argv[2] === 'verify');
} catch (error) {
  // Fixed code only: never echo credential-bearing subprocess output or arbitrary exceptions.
  console.error(
    /^APR_SOLUS_[A-Z_]+$/.test(error.message) ? error.message : 'APR_SOLUS_PROOF_FAILED',
  );
  process.exitCode = 1;
}
