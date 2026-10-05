import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, realpathSync } from 'node:fs';
import { chmod, mkdir, mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  LIMITS,
  MEMBERS,
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
const projectPath = 'runtime/src/AgenticPrReview.Runtime/AgenticPrReview.Runtime.csproj';
const lockPath = 'runtime/src/AgenticPrReview.Runtime/packages.release.lock.json';
const policyPath = 'scripts/release/build-payload.inputs.json';
const configPath = 'scripts/release/build-payload.nuget.config';

function assert(condition, code) {
  if (!condition) throw new Error(code);
}
function run(command, args, options = {}) {
  return execFileSync(command, args, { maxBuffer: 64 * 1024 * 1024, timeout: 600_000, ...options });
}
function text(command, args, options = {}) {
  return run(command, args, options).toString('utf8').trim();
}
export function admitSource(repo, sourceCommit) {
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
  const entries = run('git', ['ls-tree', '-rz', '--full-tree', sourceCommit, '--', ...closure], {
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
function tool(name, repo) {
  const path = realpathSync(text('/usr/bin/which', [name]));
  assert(
    !path.startsWith(resolve(repo) + '/') &&
      run('/usr/bin/head', ['-c', '4', path]).equals(Buffer.from('7f454c46', 'hex')),
    'unsupported_build_tool',
  );
  return path;
}
function buildEnvironment(work, dotnet, clang, linker) {
  return {
    PATH: [...new Set([dirname(dotnet), dirname(clang), dirname(linker), '/usr/bin', '/bin'])].join(
      ':',
    ),
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
async function verifyDependencies(packages, policy) {
  for (const item of policy.dependencies) {
    const root = join(packages, item.id, item.version);
    const archive = await readBoundedFile(
      join(root, item.id + '.' + item.version + '.nupkg'),
      128 * 1024 * 1024,
    );
    const metadata = JSON.parse(await readFile(join(root, '.nupkg.metadata'), 'utf8'));
    assert(
      sha256(archive) === item.sha256 && metadata.contentHash === item.contentHash,
      'dependency_hash_mismatch',
    );
  }
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
  assert(!existsSync(output), 'output_already_exists');
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
    await verifyDependencies(env.NUGET_PACKAGES, policy);
    const assets = JSON.parse(
      await readFile(
        join(source, 'runtime/src/AgenticPrReview.Runtime/obj/project.assets.json'),
        'utf8',
      ),
    );
    const libraryIds = Object.keys(assets.libraries)
      .map((key) => key.split('/')[0].toLowerCase())
      .sort();
    const expectedLibraries = policy.dependencies
      .filter(
        (item) =>
          ![
            'microsoft.netcore.app.runtime.linux-x64',
            'microsoft.aspnetcore.app.runtime.linux-x64',
          ].includes(item.id),
      )
      .map((item) => item.id)
      .sort();
    assert(
      canonicalJson(libraryIds) === canonicalJson(expectedLibraries),
      'unexpected_resolved_dependencies',
    );
    const notices = await noticesFrom(source, env.NUGET_PACKAGES, policy);
    const buildInputs = {
      releaseVersion,
      platform: policy.platform,
      sourceCommit,
      sourceTree,
      policySha256: sha256(await readFile(join(source, policyPath))),
      lockSha256: sha256(await readFile(join(source, lockPath))),
      noticesSha256: sha256(notices),
      toolchain,
      dependencies: policy.dependencies,
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
  } else if (command === 'inspect') {
    const parsed = argumentsFor(args, ['--archive', '--receipt']);
    const receipt = JSON.parse(await readBoundedFile(parsed['--receipt'], LIMITS.manifest));
    const bytes = await readBoundedFile(parsed['--archive'], LIMITS.archive);
    process.stdout.write(canonicalJson(inspectPackage(bytes, receipt).receipt) + '\n');
  } else
    throw new Error(
      'Usage: node scripts/release/build-payload.mjs build --source SHA --version VERSION --output NEW_DIRECTORY | inspect --archive FILE --receipt TRUSTED_RECEIPT',
    );
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((error) => {
    process.stderr.write((error instanceof Error ? error.message : 'build_payload_failed') + '\n');
    process.exitCode = 1;
  });
}
