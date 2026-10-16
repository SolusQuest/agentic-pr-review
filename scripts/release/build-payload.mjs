import { execFileSync, spawnSync } from 'node:child_process';
import {
  SOLUS_IDS,
  SOLUS_VERSION,
  dependencyVersionAllowed,
  packageSource,
  assertSolusAssetPins,
} from './solus-package-contract.mjs';
import { createHash } from 'node:crypto';
import { existsSync, realpathSync } from 'node:fs';
import { chmod, lstat, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import { basename, dirname, join, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  LIMITS,
  MANAGED_LIMITS,
  MANAGED_TARGET,
  collectManagedPublishTree,
  encodeManagedPackage,
  inspectManagedPackage,
  materializeManagedPackage,
  canonicalJson,
  compiledIdentity,
  encodePackage,
  inspectPackage,
  materializePackage,
  readBoundedFile,
  sha256,
  validateReleaseVersion,
} from './build-payload.format.mjs';

const closure = ['global.json', 'runtime', 'scripts/release', 'protocol/schemas'];
const managedClosure = [...closure, 'protocol/fixtures/v1/cases/bootstrap/input.json'];
const projectPath = 'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj';
const lockPath = 'runtime/src/AgenticPrReview.Runtime/packages.release.lock.json';
const policyPath = 'scripts/release/build-payload.inputs.json';
const configPath = 'scripts/release/build-payload.nuget.config';
const managedConfigPath = 'scripts/release/build-payload.managed.nuget.config';
const managedLockPath = 'runtime/src/AgenticPrReview.Runtime/packages.managed.lock.json';

function assert(condition, code) {
  if (!condition) throw new Error(code);
}
function run(command, args, options = {}) {
  try {
    return execFileSync(command, args, {
      maxBuffer: 64 * 1024 * 1024,
      timeout: 600_000,
      ...options,
      // Source admission and tool discovery run before buildEnvironment exists.
      // Never inherit caller restore credentials in these default helper calls.
      env: options.env ?? { PATH: process.env.PATH },
    });
  } catch (error) {
    const diagnostics = [error.stdout, error.stderr]
      .filter(Buffer.isBuffer)
      .map((bytes) => bytes.subarray(Math.max(0, bytes.length - 32 * 1024)).toString('utf8'))
      .join('\n');
    throw new Error(
      'build_command_failed: ' + basename(command) + ' exit=' + error.status + '\n' + diagnostics,
    );
  }
}
function text(command, args, options = {}) {
  return run(command, args, options).toString('utf8').trim();
}
export function admitSource(repo, sourceCommit, paths = closure) {
  assert(/^[a-f0-9]{40}$/.test(sourceCommit), 'invalid_source_commit');
  assert(
    text('git', ['rev-parse', 'HEAD'], { cwd: repo }) === sourceCommit,
    'source_head_mismatch',
  );
  assert(
    run('git', ['status', '--porcelain=v1', '-z', '--untracked-files=all'], { cwd: repo })
      .length === 0,
    'dirty_source',
  );
  const tree = text('git', ['rev-parse', sourceCommit + '^{tree}'], { cwd: repo });
  const entries = run('git', ['ls-tree', '-rz', '--full-tree', sourceCommit, '--', ...paths], {
    cwd: repo,
  })
    .toString('utf8')
    .split('\0')
    .filter(Boolean);
  assert(entries.length > 0, 'missing_source_closure');
  for (const entry of entries) {
    const match = /^(100644|100755) blob [a-f0-9]{40}\t([^\x00-\x1f\\]+)$/.exec(entry);
    assert(
      match && match[2].split('/').every((part) => part && part !== '.' && part !== '..'),
      'unsafe_source_entry',
    );
  }
  return tree;
}
export function admitOutput(repo, output) {
  const sourceRoot = realpathSync(resolve(repo));
  const destination = join(realpathSync(dirname(resolve(output))), basename(output));
  assert(
    destination !== sourceRoot && !destination.startsWith(sourceRoot + sep),
    'output_inside_source',
  );
  assert(!existsSync(destination), 'output_already_exists');
  return destination;
}
function tool(name, repo) {
  const path = realpathSync(text('/usr/bin/which', [name]));
  assert(
    !path.startsWith(resolve(repo) + '/') &&
      run('/usr/bin/head', ['-c', '4', path]).equals(Buffer.from('7f454c46', 'hex')),
    'unsupported_build_tool',
  );
  return path;
}
function buildEnvironment(work, dotnet, ...extraTools) {
  return {
    PATH: [
      ...new Set([dirname(dotnet), ...extraTools.map((path) => dirname(path)), '/usr/bin', '/bin']),
    ].join(':'),
    HOME: join(work, 'home'),
    DOTNET_CLI_HOME: join(work, 'home'),
    DOTNET_ROOT: dirname(dotnet),
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
    DOTNET_NOLOGO: '1',
    NUGET_PACKAGES: join(work, 'packages'),
    TMPDIR: work,
    MSBuildUserExtensionsPath: join(work, 'home', 'msbuild'),
    MSBuildEnableWorkloadResolver: 'false',
    LANG: 'C.UTF-8',
    LC_ALL: 'C.UTF-8',
  };
}
function packageProps(source, version, buildId) {
  return [
    '-p:APRReleasePackage=true',
    '-p:_IsPublishing=true',
    '-p:PublishAotUsingRuntimePack=true',
    '-p:APRReleaseVersion=' + version,
    '-p:APRBuildId=' + buildId,
    '-p:DirectoryBuildPropsPath=' + join(source, 'runtime/Directory.Build.props'),
    '-p:ImportDirectoryBuildTargets=false',
    '-p:ImportDirectoryPackagesProps=false',
  ];
}
export function admitToolchain(sdk, clangOutput, policy) {
  assert(
    sdk === policy.sdk &&
      policy.sdk === '10.0.109' &&
      policy.clang === '18.1.3' &&
      /\bclang version 18\.1\.3\b/.test(clangOutput),
    'toolchain_version_mismatch',
  );
}
export function admitPublishInventory(names) {
  assert(
    canonicalJson([...names].sort()) === canonicalJson(['AgenticPrReview.Runtime']),
    'unexpected_publish_inventory',
  );
}
export async function verifyDependencies(packages, policy, assets, seededSource = null) {
  if (policy.solusVersion) assertSolusAssetPins(assets);
  const expected = new Map();
  assert(
    Array.isArray(policy.dependencies) &&
      policy.dependencies.length > 0 &&
      policy.dependencies.length <= 16,
    'invalid_dependency_policy',
  );
  for (const item of policy.dependencies) {
    assert(
      typeof item.id === 'string' &&
        /^[a-z0-9][a-z0-9.-]{0,100}$/.test(item.id) &&
        typeof item.version === 'string' &&
        dependencyVersionAllowed(item.id, item.version, policy.solusVersion) &&
        !expected.has(item.id),
      'invalid_dependency_policy',
    );
    expected.set(item.id, item);
  }
  const pinnedKey = (id, version) => {
    assert(
      typeof id === 'string' && typeof version === 'string',
      'unexpected_resolved_dependencies',
    );
    const item = expected.get(id.toLowerCase());
    assert(item && item.version === version, 'unexpected_resolved_dependencies');
    return item.id + '/' + version;
  };
  const libraryKey = (key) => {
    const parts = key.split('/');
    assert(parts.length === 2, 'unexpected_resolved_dependencies');
    return pinnedKey(parts[0], parts[1]);
  };
  // The private cache is fresh: every top-level ID and version directory is a build input.
  // PackageDownload/runtime packs need not appear in assets.libraries.
  assert((await lstat(packages)).isDirectory(), 'unexpected_restored_inventory');
  const ids = await readdir(packages, { withFileTypes: true });
  assert(ids.length === expected.size, 'unexpected_restored_inventory');
  for (const id of ids) {
    assert(id.isDirectory() && expected.has(id.name), 'unexpected_restored_inventory');
    const versions = await readdir(join(packages, id.name), { withFileTypes: true });
    assert(
      versions.length === 1 &&
        versions[0].isDirectory() &&
        versions[0].name === expected.get(id.name).version,
      'unexpected_restored_inventory',
    );
  }
  const cache = resolve(packages);
  assert(
    assets?.version === 3 &&
      assets.project?.restore?.packagesPath === cache &&
      canonicalJson(Object.keys(assets.packageFolders || {})) === canonicalJson([cache]) &&
      (!assets.project.restore.fallbackFolders ||
        assets.project.restore.fallbackFolders.length === 0),
    'unexpected_package_folders',
  );
  const resolved = new Set(),
    libraries = new Set(),
    targets = new Set();
  assert(
    assets.libraries && Object.keys(assets.libraries).length > 0,
    'unexpected_resolved_dependencies',
  );
  for (const [key, item] of Object.entries(assets.libraries)) {
    const identity = libraryKey(key);
    assert(
      item.type === 'package' &&
        item.path === identity &&
        item.sha512 === expected.get(identity.split('/')[0]).contentHash &&
        !libraries.has(identity),
      'unexpected_resolved_dependencies',
    );
    libraries.add(identity);
    resolved.add(identity);
  }
  assert(
    assets.targets && Object.keys(assets.targets).length > 0,
    'unexpected_resolved_dependencies',
  );
  for (const target of Object.values(assets.targets)) {
    for (const [key, item] of Object.entries(target)) {
      const identity = libraryKey(key);
      assert(
        item.type === 'package' && Object.hasOwn(assets.libraries, key),
        'unexpected_resolved_dependencies',
      );
      for (const [id, version] of Object.entries(item.dependencies || {})) {
        assert(libraries.has(pinnedKey(id, version)), 'unexpected_resolved_dependencies');
      }
      targets.add(identity);
    }
  }
  assert(
    canonicalJson([...targets].sort()) === canonicalJson([...libraries].sort()),
    'unexpected_resolved_dependencies',
  );
  assert(
    assets.project.frameworks && Object.keys(assets.project.frameworks).length > 0,
    'unexpected_resolved_dependencies',
  );
  for (const framework of Object.values(assets.project.frameworks)) {
    const downloads = framework.downloadDependencies || [];
    assert(Array.isArray(downloads), 'unexpected_resolved_dependencies');
    const seen = new Set();
    for (const item of downloads) {
      assert(typeof item.version === 'string', 'unexpected_resolved_dependencies');
      const version = /^\[([^,\[\]\s]+)(?:,\s*\1)?\]$/.exec(item.version);
      assert(version, 'unexpected_resolved_dependencies');
      const identity = pinnedKey(item.name, version[1]);
      assert(!seen.has(identity), 'unexpected_resolved_dependencies');
      seen.add(identity);
      resolved.add(identity);
    }
  }
  assert(
    canonicalJson([...resolved].sort()) ===
      canonicalJson(policy.dependencies.map((item) => item.id + '/' + item.version).sort()),
    'unexpected_resolved_dependencies',
  );
  const inventory = [];
  for (const item of policy.dependencies) {
    const root = join(packages, item.id, item.version);
    const archive = await readBoundedFile(
      join(root, item.id + '.' + item.version + '.nupkg'),
      128 * 1024 * 1024,
    );
    const metadata = JSON.parse(await readBoundedFile(join(root, '.nupkg.metadata'), 64 * 1024));
    if (policy.solusVersion)
      assert(
        metadata.source?.replace(/\/$/, '') === (seededSource ?? packageSource(item.id)),
        'dependency_source_mismatch',
      );
    const rawContentHash = (
      await readBoundedFile(join(root, item.id + '.' + item.version + '.nupkg.sha512'), 1024)
    )
      .toString('utf8')
      .trim();
    const archiveHash = sha256(archive);
    assert(
      archiveHash === item.sha256 &&
        metadata.contentHash === item.contentHash &&
        rawContentHash === createHash('sha512').update(archive).digest('base64'),
      'dependency_hash_mismatch',
    );
    inventory.push({ ...item, sha256: archiveHash, contentHash: metadata.contentHash });
  }
  return inventory;
}
async function seedArchives(work, policy, cache) {
  // Raw archives only. Never copy extracted cache entries into the compilation cache.
  const paths = policy.dependencies.map((item) =>
    join(cache, item.id, item.version, item.id + '.' + item.version + '.nupkg'),
  );
  if (!paths.every((path) => existsSync(path))) return false;
  const feed = join(work, 'feed');
  await mkdir(feed);
  for (let index = 0; index < paths.length; index++) {
    const bytes = await readBoundedFile(paths[index], 128 * 1024 * 1024);
    const item = policy.dependencies[index];
    if (policy.solusVersion) {
      const metadata = JSON.parse(
        await readBoundedFile(join(cache, item.id, item.version, '.nupkg.metadata'), 64 * 1024),
      );
      assert(
        metadata.source?.replace(/\/$/, '') === packageSource(item.id) &&
          metadata.contentHash === item.contentHash,
        'cached_archive_source_mismatch',
      );
    }
    assert(sha256(bytes) === item.sha256, 'cached_archive_hash_mismatch');
    await writeFile(join(feed, item.id + '.' + item.version + '.nupkg'), bytes, {
      flag: 'wx',
      mode: 0o600,
    });
  }
  return true;
}
async function noticesFrom(source, packages, policy) {
  let notices = await readFile(join(source, 'scripts/release/build-payload.licenses.txt'), 'utf8');
  for (const item of policy.dependencies) {
    const directory = join(packages, item.id, item.version);
    const names = (await readdir(directory))
      .filter((name) => /^(OSMFEULA\.txt|THIRD-PARTY-NOTICES\.TXT|LICENSE\.txt)$/i.test(name))
      .sort();
    for (const name of names) {
      const bytes = await readBoundedFile(join(directory, name), LIMITS.notices);
      notices += '\n\n' + item.id + ' ' + item.version + ' [' + item.role + '] / ' + name + '\n';
      notices += new TextDecoder('utf-8', { fatal: true }).decode(bytes).replace(/\r\n/g, '\n');
    }
  }
  const bytes = Buffer.from(notices);
  assert(bytes.length > 0 && bytes.length <= LIMITS.notices, 'notices_too_large');
  return bytes;
}
function nativeInventory(binary, env) {
  const dynamic = text('/usr/bin/readelf', ['-d', binary], { env });
  const needed = [...dynamic.matchAll(/\(NEEDED\).*\[([^\]]+)\]/g)].map((match) => match[1]).sort();
  assert(
    needed.length > 0 && !needed.some((name) => /coreclr|hostfxr|hostpolicy/i.test(name)),
    'unexpected_managed_runtime_dependency',
  );
  const program = text('/usr/bin/readelf', ['-l', binary], { env });
  assert(
    program.includes('Requesting program interpreter: /lib64/ld-linux-x86-64.so.2'),
    'unsupported_elf_interpreter',
  );
  // .NET loads ICU and OpenSSL at runtime; these are not all visible in ELF NEEDED.
  return {
    needed,
    runtimeLoaded: ['libcrypto.so.3', 'libicui18n.so.74', 'libicuuc.so.74', 'libssl.so.3'],
  };
}
async function proveIdentity(binary, source, identity, work) {
  const input = join(work, 'identity-input.json');
  const result = join(work, 'identity-result.json');
  const trace = join(work, 'identity-trace.json');
  await writeFile(
    input,
    await readFile(join(source, 'protocol/fixtures/v1/cases/bootstrap/input.json')),
    { flag: 'wx' },
  );
  // The child sees no SDK/runtime location, PATH, credential or inherited environment.
  const env = { TMPDIR: work, TMP: work, TEMP: work };
  run(binary, ['review', '--input', input, '--output', result, '--trace', trace], {
    cwd: work,
    env,
  });
  const observed = JSON.parse(await readFile(result, 'utf8'));
  const observedTrace = JSON.parse(await readFile(trace, 'utf8'));
  assert(
    observed.runtimeVersion === identity && observedTrace.runtimeVersion === identity,
    'compiled_identity_mismatch',
  );
}
export async function buildPayload({ repo, sourceCommit, releaseVersion, output }) {
  repo = resolve(repo);
  output = resolve(output);
  validateReleaseVersion(releaseVersion);
  const sourceTree = admitSource(repo, sourceCommit);
  assert(
    process.platform === 'linux' &&
      process.arch === 'x64' &&
      process.versions.node.startsWith('24.'),
    'unsupported_builder',
  );
  const os = await readFile('/etc/os-release', 'utf8');
  assert(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'unsupported_builder_os');
  output = admitOutput(repo, output);
  const policy = JSON.parse(await readFile(join(repo, policyPath), 'utf8'));
  assert(
    policy.formatVersion === 1 &&
      policy.platform === 'linux-x64' &&
      policy.sdk === '10.0.109' &&
      policy.clang === '18.1.3' &&
      policy.runtime === '10.0.9',
    'invalid_build_policy',
  );
  const cache = process.env.NUGET_PACKAGES || join(homedir(), '.nuget/packages');
  const dotnet = tool('dotnet', repo),
    clang = tool('clang', repo),
    linker = tool('ld', repo);
  const work = await mkdtemp(join(tmpdir(), 'apr-payload-build-'));
  try {
    await chmod(work, 0o700);
    const source = join(work, 'source');
    await mkdir(source);
    await mkdir(join(work, 'home'));
    const snapshot = run('git', ['archive', '--format=tar', sourceCommit, ...closure], {
      cwd: repo,
    });
    run('/usr/bin/tar', ['--no-same-owner', '-xf', '-', '-C', source], { input: snapshot });
    const env = buildEnvironment(work, dotnet, clang, linker);
    const sdk = text(dotnet, ['--version'], { cwd: source, env });
    const clangOutput = text(clang, ['--version'], { env });
    admitToolchain(sdk, clangOutput, policy);
    const names = [
      'binutils',
      'clang',
      'libc6',
      'libgcc-s1',
      'libicu74',
      'libssl3t64',
      'zlib1g',
      'zlib1g-dev',
    ];
    const versions = text('/usr/bin/dpkg-query', ['-W', '-f=${Package}\t${Version}\n', ...names], {
      env,
    });
    const systemPackages = versions
      .split('\n')
      .map((line) => {
        const [name, version] = line.split('\t');
        return { name, version };
      })
      .sort((a, b) => a.name.localeCompare(b.name));
    const toolchain = {
      sdk,
      sdkDriverSha256: sha256(await readBoundedFile(dotnet, 32 * 1024 * 1024)),
      clang: policy.clang,
      clangSha256: sha256(await readBoundedFile(clang, 256 * 1024 * 1024)),
      linker: text(linker, ['--version'], { env }).split('\n')[0],
      linkerSha256: sha256(await readBoundedFile(linker, 32 * 1024 * 1024)),
      node: process.versions.node,
      zlib: process.versions.zlib,
      os: 'ubuntu-24.04-x64',
      systemPackages,
    };
    const props = packageProps(source, releaseVersion, '0'.repeat(64));
    const seeded = await seedArchives(work, policy, cache);
    const restoreArgs = [
      'restore',
      join(source, projectPath),
      '-r',
      'linux-x64',
      '--locked-mode',
      '--nologo',
      '--configfile',
      join(source, configPath),
      ...props,
    ];
    if (seeded) restoreArgs.push('--source', join(work, 'feed'));
    process.stdout.write(
      'Restoring fixed release inputs (' +
        (seeded ? 'verified raw archive seed' : 'nuget.org') +
        ')\n',
    );
    run(dotnet, restoreArgs, { cwd: source, env, stdio: ['ignore', 'pipe', 'pipe'] });
    const assetsPath = join(source, 'runtime/src/AgenticPrReview.Runtime/obj/project.assets.json');
    const readAssets = async () => JSON.parse(await readBoundedFile(assetsPath, 4 * 1024 * 1024));
    const dependencies = await verifyDependencies(env.NUGET_PACKAGES, policy, await readAssets());
    const notices = await noticesFrom(source, env.NUGET_PACKAGES, { dependencies });
    const buildInputs = {
      releaseVersion,
      platform: policy.platform,
      sourceCommit,
      sourceTree,
      policySha256: sha256(await readFile(join(source, policyPath))),
      lockSha256: sha256(await readFile(join(source, lockPath))),
      noticesSha256: sha256(notices),
      toolchain,
      dependencies,
    };
    const buildId = sha256(canonicalJson(buildInputs));
    const publish = join(work, 'publish');
    process.stdout.write('Publishing fixed Native AOT source ' + sourceCommit + '\n');
    run(
      dotnet,
      [
        'publish',
        join(source, projectPath),
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '--self-contained',
        'true',
        '--no-restore',
        '--nologo',
        '-o',
        publish,
        ...packageProps(source, releaseVersion, buildId),
      ],
      { cwd: source, env },
    );
    // Publish must retain the same admitted restore inputs; no late package additions.
    await verifyDependencies(env.NUGET_PACKAGES, policy, await readAssets());
    admitPublishInventory(await readdir(publish));
    const binary = join(publish, 'AgenticPrReview.Runtime');
    const executable = await readBoundedFile(binary, LIMITS.executable);
    const nativeDependencies = nativeInventory(binary, env);
    const packaged = encodePackage(executable, notices, buildInputs, nativeDependencies);
    const extracted = await materializePackage(packaged.archive, packaged.receipt, work);
    await proveIdentity(
      extracted.executable,
      repo,
      compiledIdentity(releaseVersion, buildId),
      work,
    );
    const fixture = join(repo, 'runtime/tests/ActionHostFixture/verify-entrypoint.mjs');
    run(process.execPath, [fixture, 'native', extracted.executable], {
      cwd: work,
      env: { TMPDIR: work, TMP: work, TEMP: work },
    });
    assert(admitSource(repo, sourceCommit) === sourceTree, 'source_changed_during_build');
    // Output becomes visible only after the actual archived member has passed proof.
    await mkdir(output, { mode: 0o700 });
    const archivePath = join(output, packaged.receipt.archiveName);
    await writeFile(archivePath, packaged.archive, { flag: 'wx', mode: 0o600 });
    const readback = await readBoundedFile(archivePath, LIMITS.archive);
    inspectPackage(readback, packaged.receipt);
    await writeFile(join(output, 'receipt.json'), canonicalJson(packaged.receipt) + '\n', {
      flag: 'wx',
      mode: 0o600,
    });
    process.stdout.write(
      JSON.stringify({
        archivePath,
        sourceCommit,
        buildId,
        compiledIdentity: compiledIdentity(releaseVersion, buildId),
        archiveSha256: packaged.receipt.archiveSha256,
        archiveSize: packaged.receipt.archiveSize,
      }) + '\n',
    );
    return packaged.receipt;
  } finally {
    await rm(work, { recursive: true, force: true });
  }
}
function argumentsFor(args, required) {
  assert(args.length === required.length * 2, 'invalid_arguments');
  const result = {};
  for (let index = 0; index < args.length; index += 2) {
    const key = args[index];
    assert(
      required.includes(key) && !Object.hasOwn(result, key) && args[index + 1],
      'invalid_arguments',
    );
    result[key] = args[index + 1];
  }
  return result;
}
async function main() {
  const [command, ...args] = process.argv.slice(2);
  if (command === 'build') {
    const parsed = argumentsFor(args, ['--source', '--version', '--output']);
    await buildPayload({
      repo: process.cwd(),
      sourceCommit: parsed['--source'],
      releaseVersion: parsed['--version'],
      output: parsed['--output'],
    });
  } else if (command === 'build-managed') {
    const parsed = argumentsFor(args, ['--source', '--version', '--output']);
    await buildManagedPayload({
      repo: process.cwd(),
      sourceCommit: parsed['--source'],
      releaseVersion: parsed['--version'],
      output: parsed['--output'],
    });
  } else if (command === 'inspect-managed') {
    const parsed = argumentsFor(args, ['--archive', '--receipt']);
    const receipt = JSON.parse(await readBoundedFile(parsed['--receipt'], MANAGED_LIMITS.manifest));
    const bytes = await readBoundedFile(parsed['--archive'], MANAGED_LIMITS.archive);
    process.stdout.write(canonicalJson(inspectManagedPackage(bytes, receipt).receipt) + '\n');
  } else if (command === 'inspect') {
    const parsed = argumentsFor(args, ['--archive', '--receipt']);
    const receipt = JSON.parse(await readBoundedFile(parsed['--receipt'], LIMITS.manifest));
    const bytes = await readBoundedFile(parsed['--archive'], LIMITS.archive);
    process.stdout.write(canonicalJson(inspectPackage(bytes, receipt).receipt) + '\n');
  } else
    throw new Error(
      'Usage: node scripts/release/build-payload.mjs build --source SHA --version VERSION --output NEW_DIRECTORY | inspect --archive FILE --receipt TRUSTED_RECEIPT | build-managed --source SHA --version VERSION --output NEW_DIRECTORY | inspect-managed --archive FILE --receipt TRUSTED_RECEIPT',
    );
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write((error instanceof Error ? error.message : 'build_payload_failed') + '\n');
    process.exitCode = 1;
  });
}

function managedProps(source, version, buildId, lock) {
  return [
    '-p:APRManagedPackage=true',
    '-p:APRReleasePackage=false',
    '-p:_IsPublishing=true',
    '-p:APRReleaseVersion=' + version,
    '-p:APRBuildId=' + buildId,
    '-p:DirectoryBuildPropsPath=' + join(source, 'runtime/Directory.Build.props'),
    '-p:ImportDirectoryBuildTargets=false',
    '-p:ImportDirectoryPackagesProps=false',
    '-p:RestorePackagesWithLockFile=true',
    '-p:NuGetLockFilePath=' + lock,
    ...Object.entries(MANAGED_TARGET).map(([key, value]) => '-p:' + key + '=' + value),
  ];
}
export function managedDependencyPolicy(policy) {
  const selected = policy.managed;
  assert(
    policy.sdk === '10.0.109' &&
      policy.platform === 'linux-x64' &&
      selected?.formatVersion === 'managed-v1' &&
      selected.targetFramework === 'net10.0' &&
      selected.runtimeMinimum === '10.0.9' &&
      Array.isArray(selected.dependencyIds) &&
      selected.dependencyIds.length > 0 &&
      new Set(selected.dependencyIds).size === selected.dependencyIds.length,
    'invalid_managed_build_policy',
  );
  assert(
    selected.solusVersion === SOLUS_VERSION &&
      Array.isArray(selected.additionalDependencies) &&
      selected.additionalDependencies.length === SOLUS_IDS.length &&
      SOLUS_IDS.every((id) =>
        selected.additionalDependencies.some(
          (item) => item.id === id && item.version === SOLUS_VERSION && item.role === 'managed',
        ),
      ),
    'invalid_managed_build_policy',
  );
  const catalog = [...policy.dependencies, ...selected.additionalDependencies];
  assert(
    new Set(catalog.map((item) => item.id)).size === catalog.length,
    'invalid_managed_build_policy',
  );
  const dependencies = selected.dependencyIds
    .map((id) => {
      const matches = catalog.filter((item) => item.id === id && item.role === 'managed');
      assert(matches.length === 1, 'invalid_managed_build_policy');
      return matches[0];
    })
    .sort((a, b) => (a.id < b.id ? -1 : 1));
  assert(
    SOLUS_IDS.every((id) => dependencies.some((item) => item.id === id)),
    'invalid_managed_build_policy',
  );
  return { dependencies, solusVersion: selected.solusVersion };
}
export async function buildManagedPayload({ repo, sourceCommit, releaseVersion, output }) {
  repo = resolve(repo);
  output = resolve(output);
  validateReleaseVersion(releaseVersion);
  const sourceTree = admitSource(repo, sourceCommit, managedClosure);
  assert(
    process.platform === 'linux' &&
      process.arch === 'x64' &&
      process.versions.node.startsWith('24.'),
    'unsupported_managed_builder',
  );
  const os = await readFile('/etc/os-release', 'utf8');
  assert(/^ID=ubuntu$/m.test(os) && /^VERSION_ID="24\.04"$/m.test(os), 'unsupported_builder_os');
  output = admitOutput(repo, output);
  const policyBytes = await readFile(join(repo, policyPath));
  const policy = JSON.parse(policyBytes),
    managedPolicy = managedDependencyPolicy(policy);
  const cache = process.env.NUGET_PACKAGES || join(homedir(), '.nuget/packages');
  const dotnet = tool('dotnet', repo);
  const work = await mkdtemp(join(tmpdir(), 'apr-managed-build-'));
  try {
    await chmod(work, 0o700);
    const source = join(work, 'source');
    await mkdir(source);
    await mkdir(join(work, 'home'));
    const snapshot = run('git', ['archive', '--format=tar', sourceCommit, ...managedClosure], {
      cwd: repo,
    });
    run('/usr/bin/tar', ['--no-same-owner', '-xf', '-', '-C', source], { input: snapshot });
    const env = buildEnvironment(work, dotnet),
      project = join(source, projectPath);
    const sdk = text(dotnet, ['--version'], { cwd: source, env });
    assert(sdk === policy.sdk, 'toolchain_version_mismatch');
    const lock = join(work, 'managed.lock.json');
    await writeFile(lock, await readBoundedFile(join(source, managedLockPath), 256 * 1024), {
      flag: 'wx',
    });
    const props = managedProps(source, releaseVersion, '0'.repeat(64), lock);
    const evaluated = JSON.parse(
      text(
        dotnet,
        [
          'msbuild',
          project,
          '-nologo',
          '-getProperty:' + Object.keys(MANAGED_TARGET).join(','),
          ...props,
        ],
        { cwd: source, env },
      ),
    ).Properties;
    const target = Object.fromEntries(
      Object.entries(MANAGED_TARGET).map(([key, value]) => {
        assert(evaluated[key] === String(value), 'managed_effective_target_mismatch');
        return [key, typeof value === 'boolean' ? evaluated[key] === 'true' : evaluated[key]];
      }),
    );
    const seeded = await seedArchives(work, managedPolicy, cache);
    let restoreConfig = join(source, managedConfigPath);
    if (seeded) {
      restoreConfig = join(work, 'verified-seed.nuget.config');
      const feed = join(work, 'feed')
        .replaceAll('&', '&amp;')
        .replaceAll('"', '&quot;')
        .replaceAll('<', '&lt;');
      await writeFile(
        restoreConfig,
        `<?xml version="1.0" encoding="utf-8"?><configuration><packageSources><clear/><add key="verified-seed" value="${feed}"/></packageSources><packageSourceMapping><clear/><packageSource key="verified-seed"><package pattern="*"/></packageSource></packageSourceMapping><fallbackPackageFolders><clear/></fallbackPackageFolders></configuration>`,
        { flag: 'wx' },
      );
    }
    const restore = [
      'restore',
      project,
      '-r',
      'linux-x64',
      '--locked-mode',
      '--nologo',
      '--configfile',
      restoreConfig,
      '-p:RestoreLockedMode=true',
      ...props,
    ];
    let restoreEnv = env;
    if (!seeded) {
      assert(
        typeof process.env.NuGetPackageSourceCredentials_solus === 'string' &&
          process.env.NuGetPackageSourceCredentials_solus.length > 0,
        'managed_restore_auth_unavailable',
      );
      restoreEnv = {
        ...env,
        NuGetPackageSourceCredentials_solus: process.env.NuGetPackageSourceCredentials_solus,
      };
    }
    // Never attach credential-bearing restore diagnostics to a public error.
    const restored = spawnSync(dotnet, restore, {
      cwd: source,
      env: restoreEnv,
      maxBuffer: 16 * 1024 * 1024,
      timeout: 600_000,
    });
    assert(restored.status === 0, 'managed_restore_failed');
    const assetsPath = join(
      source,
      'runtime/src/AgenticPrReview.Runtime/obj/managed/project.assets.json',
    );
    const assetsBytes = await readBoundedFile(assetsPath, 4 * 1024 * 1024);
    const readAssets = async () => JSON.parse(await readBoundedFile(assetsPath, 4 * 1024 * 1024));
    const dependencies = await verifyDependencies(
      env.NUGET_PACKAGES,
      managedPolicy,
      JSON.parse(assetsBytes),
      seeded ? join(work, 'feed') : null,
    );
    const notices = await noticesFrom(source, env.NUGET_PACKAGES, managedPolicy);
    const buildInputs = {
      releaseVersion,
      platform: policy.platform,
      sourceCommit,
      sourceTree,
      policySha256: sha256(policyBytes),
      lockSha256: sha256(await readBoundedFile(lock, 256 * 1024)),
      assetsSha256: sha256(assetsBytes),
      noticesSha256: sha256(notices),
      target,
      toolchain: {
        sdk,
        sdkDriverSha256: sha256(await readBoundedFile(dotnet, 32 * 1024 * 1024)),
        node: process.versions.node,
        zlib: process.versions.zlib,
        os: 'ubuntu-24.04-x64',
      },
      dependencies,
    };
    const buildId = sha256(canonicalJson(buildInputs)),
      publish = join(work, 'publish');
    run(
      dotnet,
      [
        'publish',
        project,
        '-c',
        'Release',
        '-r',
        'linux-x64',
        '--self-contained',
        'false',
        '--no-restore',
        '--nologo',
        '-o',
        publish,
        ...managedProps(source, releaseVersion, buildId, lock),
      ],
      { cwd: source, env },
    );
    await verifyDependencies(
      env.NUGET_PACKAGES,
      managedPolicy,
      await readAssets(),
      seeded ? join(work, 'feed') : null,
    );
    assert(
      sha256(await readBoundedFile(assetsPath, 4 * 1024 * 1024)) === buildInputs.assetsSha256 &&
        sha256(await readBoundedFile(lock, 256 * 1024)) === buildInputs.lockSha256,
      'managed_restore_changed',
    );
    // The original census comes from the actual fresh publish tree, not the manifest.
    const original = await collectManagedPublishTree(publish);
    const packaged = encodeManagedPackage(original, notices, buildInputs);
    const inspected = inspectManagedPackage(packaged.archive, packaged.receipt);
    const extracted = await materializeManagedPackage(packaged.archive, packaged.receipt, work);
    const payload = inspected.files.filter(
      (file) =>
        !['agentic-pr-review/manifest.json', 'agentic-pr-review/THIRD-PARTY-NOTICES.txt'].includes(
          file.path,
        ),
    );
    assert(payload.length === original.length, 'managed_roundtrip_inventory_mismatch');
    for (const file of original) {
      const decoded = payload.find((item) => item.path === 'agentic-pr-review/' + file.path);
      assert(
        decoded &&
          decoded.bytes.equals(file.bytes) &&
          (await readFile(join(extracted.root, 'agentic-pr-review', file.path))).equals(file.bytes),
        'managed_roundtrip_bytes_mismatch',
      );
    }
    // Verify compiled identity with a bounded bootstrap only; this is not D3/D5 loading qualification.
    const input = join(work, 'identity-input.json'),
      result = join(work, 'identity-result.json'),
      trace = join(work, 'identity-trace.json');
    await writeFile(
      input,
      await readFile(join(source, 'protocol/fixtures/v1/cases/bootstrap/input.json')),
      { flag: 'wx' },
    );
    run(
      dotnet,
      [extracted.entrypoint, 'review', '--input', input, '--output', result, '--trace', trace],
      { cwd: work, env: { DOTNET_ROOT: dirname(dotnet), TMPDIR: work, TMP: work, TEMP: work } },
    );
    for (const path of [result, trace])
      assert(
        JSON.parse(await readFile(path, 'utf8')).runtimeVersion ===
          compiledIdentity(releaseVersion, buildId),
        'compiled_identity_mismatch',
      );
    assert(
      admitSource(repo, sourceCommit, managedClosure) === sourceTree,
      'source_changed_during_build',
    );
    await mkdir(output, { mode: 0o700 });
    const archivePath = join(output, packaged.receipt.archiveName);
    await writeFile(archivePath, packaged.archive, { flag: 'wx', mode: 0o600 });
    inspectManagedPackage(
      await readBoundedFile(archivePath, MANAGED_LIMITS.archive),
      packaged.receipt,
    );
    await writeFile(join(output, 'receipt.json'), canonicalJson(packaged.receipt) + '\n', {
      flag: 'wx',
      mode: 0o600,
    });
    process.stdout.write(
      'CR1_D1_MANAGED_ARCHIVE ' +
        canonicalJson({
          sourceCommit,
          sourceTree,
          buildId,
          archiveSha256: packaged.receipt.archiveSha256,
          archiveSize: packaged.receipt.archiveSize,
          publishFiles: original.length,
          roundtrip: true,
          compiledIdentity: compiledIdentity(releaseVersion, buildId),
          runtimeMinimum: target.RuntimeFrameworkVersion,
          executionQualified: false,
        }) +
        '\n',
    );
    return packaged.receipt;
  } finally {
    await rm(work, { recursive: true, force: true });
  }
}
