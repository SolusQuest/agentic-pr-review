import { execFileSync, spawn } from 'node:child_process';
import {
  chown,
  lstat,
  cp,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { createServer, request as httpRequest } from 'node:http';
import { createRepositoryTestManagedPayloadResolver } from '../../src/action-wrapper/launcher/release-payload.js';
import path from 'node:path';
import {
  collectManagedPublishTree,
  encodeManagedPackage,
  inspectHistoricalNativePackageForFixture as inspectPackage,
  MANAGED_TARGET,
  sha256,
  type ManagedReceipt,
} from '../../scripts/release/build-payload.format.mjs';
import { stageManagedPayload } from '../../src/action-wrapper/launcher/payload-staging.js';
import { releaseFixture, ACTION_SOURCE } from './release-payload-fixture.js';

export function packageManagedFiles(files: { path: string; bytes: Buffer }[]) {
  const native = releaseFixture();
  const notices = Buffer.from('Synthetic managed loader fixture; no public release.\n');
  const inputs = inspectPackage(native.archive, native.receipt).manifest.buildInputs;
  return encodeManagedPackage(files, notices, {
    ...inputs,
    assetsSha256: '3'.repeat(64),
    noticesSha256: sha256(notices),
    target: MANAGED_TARGET,
    toolchain: {
      sdk: '10.0.109',
      sdkDriverSha256: 'e'.repeat(64),
      node: '24.0.0',
      zlib: process.versions.zlib,
      os: 'ubuntu-24.04-x64',
    },
  });
}
export function minimalManagedFiles() {
  return [
    {
      path: 'AgenticPrReview.Runtime.dll',
      bytes: Buffer.from('mock-only entry; not an executable proof'),
    },
    {
      path: 'AgenticPrReview.Runtime.runtimeconfig.json',
      bytes: Buffer.from(
        JSON.stringify({
          runtimeOptions: {
            tfm: 'net10.0',
            framework: { name: 'Microsoft.NETCore.App', version: '10.0.9' },
          },
        }),
      ),
    },
    {
      path: 'AgenticPrReview.Runtime.deps.json',
      bytes: Buffer.from(
        JSON.stringify({
          runtimeTarget: { name: '.NETCoreApp,Version=v10.0/linux-x64' },
          targets: {
            '.NETCoreApp,Version=v10.0/linux-x64': {
              'Fixture/1.0.0': { runtime: { 'AgenticPrReview.Runtime.dll': {} } },
            },
          },
        }),
      ),
    },
  ];
}
export function managedMap(receipt: ManagedReceipt) {
  const native = releaseFixture().map;
  return {
    ...native,
    formatVersion: 1 as const,
    repository: 'SolusQuest/agentic-pr-review' as const,
    builder: { ...native.builder, repository: 'SolusQuest/agentic-pr-review' as const },
    tag: `payload-${receipt.identity.releaseVersion}`,
    payload: receipt,
  };
}
export async function managedContext(
  packaged = packageManagedFiles(minimalManagedFiles()),
  workspace?: string,
) {
  const base = await mkdtemp(path.join(tmpdir(), 'apr-managed-process-'));
  const parent = path.join(base, 'staging');
  const tempRoot = path.join(base, 'cwd');
  const reviewedWorkspace = workspace ?? path.join(base, 'workspace');
  await mkdir(parent, { mode: 0o700 });
  await mkdir(tempRoot, { mode: 0o700 });
  if (!workspace) await mkdir(reviewedWorkspace, { mode: 0o700 });
  const payload = await stageManagedPayload({
    ...packaged,
    map: managedMap(packaged.receipt),
    actionSourceSha: ACTION_SOURCE,
    stagingParent: parent,
    workspace: reviewedWorkspace,
    signal: new AbortController().signal,
  });
  return {
    base,
    parent,
    tempRoot,
    workspace: reviewedWorkspace,
    payload,
    async close() {
      await payload.dispose();
      await rm(base, { recursive: true, force: true });
    },
  };
}
export async function minimalRuntime(parent: string) {
  const root = path.join(parent, 'runtime');
  await mkdir(path.join(root, 'host/fxr/10.0.9'), { recursive: true });
  const framework = path.join(root, 'shared/Microsoft.NETCore.App/10.0.9');
  await mkdir(framework, { recursive: true });
  const elf = Buffer.alloc(128);
  Buffer.from('7f454c4602010100', 'hex').copy(elf);
  elf.writeUInt16LE(3, 16);
  elf.writeUInt16LE(62, 18);
  elf.writeUInt32LE(1, 20);
  await writeFile(path.join(root, 'dotnet'), elf, { mode: 0o700 });
  await writeFile(path.join(root, 'host/fxr/10.0.9/libhostfxr.so'), elf);
  for (const name of [
    'libcoreclr.so',
    'libhostpolicy.so',
    'System.Private.CoreLib.dll',
    'Microsoft.NETCore.App.deps.json',
    'Microsoft.NETCore.App.runtimeconfig.json',
  ])
    if (name.endsWith('.so')) await writeFile(path.join(framework, name), elf);
    else if (name.endsWith('.dll')) {
      const pe = Buffer.alloc(128);
      pe.write('MZ');
      await writeFile(path.join(framework, name), pe);
    } else await writeFile(path.join(framework, name), 'mock-only');
  return root;
}
export async function buildLoaderFixture() {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-real-managed-loader-'));
  const workspace = path.join(root, 'workspace');
  await mkdir(workspace, { mode: 0o700 });
  const marker = path.join(workspace, 'poison-marker');
  const sdkRoot = await realpath(process.env.DOTNET_ROOT ?? '/usr/share/dotnet');
  const sdk = path.join(sdkRoot, 'dotnet');
  const runtime = path.join(root, 'runtime');
  await mkdir(runtime, { mode: 0o700 });
  for (const name of ['dotnet', 'host', 'shared'])
    await cp(path.join(sdkRoot, name), path.join(runtime, name), { recursive: true });
  const ownRuntimeCopy = async (named: string): Promise<void> => {
    const stat = await lstat(named);
    if (stat.isSymbolicLink()) throw new Error('unsafe_runtime_fixture_copy');
    await chown(named, process.getuid!(), process.getgid!());
    if (stat.isDirectory())
      for (const name of await readdir(named)) await ownRuntimeCopy(path.join(named, name));
  };
  await ownRuntimeCopy(runtime);
  const installedFrameworks = (await readdir(path.join(runtime, 'shared/Microsoft.NETCore.App')))
    .filter((version) => /^10\.0\.(0|[1-9][0-9]*)$/.test(version))
    .sort((left, right) => Number(right.split('.')[2]) - Number(left.split('.')[2]));
  const frameworkVersion = installedFrameworks[0];
  if (!frameworkVersion || Number(frameworkVersion.split('.')[2]) < 9)
    throw new Error('compatible_runtime_fixture_missing');
  const emptyFeed = path.join(root, 'empty-feed');
  await mkdir(emptyFeed);
  const source = path.resolve('tests/distribution/managed-loader');
  const publish = async (destination: string, poison: boolean) => {
    const projectRoot = path.join(root, poison ? 'poison-source' : 'source');
    await cp(source, projectRoot, { recursive: true });
    if (poison) {
      const resource = path.join(projectRoot, 'Dependency/LoaderValues.fr.resx');
      await writeFile(
        resource,
        (await readFile(resource, 'utf8')).replace('verified-resource', 'poison-resource'),
      );
    }
    execFileSync(
      sdk,
      [
        'publish',
        path.join(projectRoot, 'AgenticPrReview.Runtime.csproj'),
        '--configuration',
        'Release',
        '--runtime',
        'linux-x64',
        '--self-contained',
        'false',
        '--source',
        emptyFeed,
        '--output',
        destination,
        ...(poison ? ['-p:DefineConstants=POISON'] : []),
      ],
      { timeout: 90_000, maxBuffer: 2 * 1024 * 1024 },
    );
    execFileSync('/usr/bin/gcc', [
      '-shared',
      '-fPIC',
      path.join(source, 'native-probe.c'),
      '-o',
      path.join(destination, 'libaprprobe.so'),
      `-DPROBE_VALUE=${poison ? 9009 : 7001}`,
      ...(poison ? [`-DPOISON_MARKER=${JSON.stringify(marker)}`] : []),
    ]);
    const depsPath = path.join(destination, 'AgenticPrReview.Runtime.deps.json');
    const deps = JSON.parse(await readFile(depsPath, 'utf8'));
    const application = Object.values(deps.targets[deps.runtimeTarget.name]).find(
      (value: any) => value.runtime?.['AgenticPrReview.Runtime.dll'],
    ) as any;
    application.native = { 'libaprprobe.so': {} };
    await writeFile(depsPath, JSON.stringify(deps));
  };
  const verified = path.join(root, 'verified');
  const poison = path.join(workspace, 'poison');
  try {
    await publish(verified, false);
    await publish(poison, true);
    const hookRoot = path.join(root, 'hook-source');
    await mkdir(hookRoot);
    await writeFile(
      path.join(hookRoot, 'StartupHook.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>',
    );
    await writeFile(
      path.join(hookRoot, 'StartupHook.cs'),
      `public class StartupHook { public static void Initialize() => System.IO.File.WriteAllText(${JSON.stringify(marker)}, "executed"); }`,
    );
    const hook = path.join(workspace, 'hook');
    execFileSync(
      sdk,
      [
        'publish',
        path.join(hookRoot, 'StartupHook.csproj'),
        '--source',
        emptyFeed,
        '--output',
        hook,
      ],
      { timeout: 60_000, maxBuffer: 2 * 1024 * 1024 },
    );
    const files = await collectManagedPublishTree(verified);
    return {
      root,
      workspace,
      marker,
      runtime,
      frameworkVersion,
      verified,
      poison,
      hook: path.join(hook, 'StartupHook.dll'),
      files,
      packaged: packageManagedFiles(files),
      close: () => rm(root, { recursive: true, force: true }),
    };
  } catch (error) {
    await rm(root, { recursive: true, force: true });
    throw error;
  }
}
export async function waitForFile(named: string) {
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline) {
    try {
      await readFile(named);
      return;
    } catch {
      await new Promise((resolve) => setTimeout(resolve, 10));
    }
  }
  throw new Error('managed_fixture_not_ready');
}

/** Deliberately unsafe repository-only control; never selected by production API. */
export async function unsafeManagedControl(
  runtime: string,
  directory: string,
  launch: Buffer,
  env: NodeJS.ProcessEnv = {},
) {
  const child = spawn(
    path.join(runtime, 'dotnet'),
    ['exec', path.join(directory, 'AgenticPrReview.Runtime.dll')],
    {
      cwd: directory,
      env: {
        TMPDIR: directory,
        NO_COLOR: '1',
        DOTNET_NOLOGO: '1',
        DOTNET_CLI_TELEMETRY_OPTOUT: '1',
        DOTNET_EnableDiagnostics: '0',
        ...env,
      },
      stdio: ['pipe', 'pipe', 'pipe'],
    },
  );
  const header = Buffer.alloc(4);
  header.writeUInt32BE(launch.length);
  const stdout: Buffer[] = [],
    stderr: Buffer[] = [];
  child.stdout.on('data', (value: Buffer) => stdout.push(value));
  child.stderr.on('data', (value: Buffer) => stderr.push(value));
  child.stdin.end(Buffer.concat([header, launch]));
  const deadline = setTimeout(() => child.kill('SIGKILL'), 10_000);
  try {
    const code = await new Promise<number | null>((resolve, reject) => {
      child.once('error', reject);
      child.once('close', resolve);
    });
    return { code, stdout: Buffer.concat(stdout), stderr: Buffer.concat(stderr) };
  } finally {
    clearTimeout(deadline);
  }
}

/** Real production acquisition/staging; only the credential-free remote bytes are simulated. */
export async function acquireManagedArchiveThroughFakeRelease(
  packaged: { archive: Buffer; receipt: ManagedReceipt },
  source: string,
  workspace: string,
) {
  const base = await mkdtemp(path.join(tmpdir(), 'apr-actual-managed-host-'));
  const parent = path.join(base, 'staging');
  const tempRoot = path.join(base, 'cwd');
  await mkdir(parent, { mode: 0o700 });
  await mkdir(tempRoot, { mode: 0o700 });
  const map = managedMap(packaged.receipt);
  const download = `https://github.com/${map.repository}/releases/download/${map.tag}/${packaged.receipt.archiveName}`;
  const seen: { url: string; credential: boolean }[] = [];
  const server = createServer((request, response) => {
    response.setHeader('Content-Type', 'application/json');
    if (request.url?.includes('/tags/'))
      response.end(JSON.stringify({ id: 7, tag_name: map.tag, draft: false }));
    else if (request.url?.endsWith('/assets?per_page=100'))
      response.end(
        JSON.stringify([
          {
            id: 11,
            name: packaged.receipt.archiveName,
            state: 'uploaded',
            size: packaged.archive.length,
            browser_download_url: download,
          },
        ]),
      );
    else {
      response.setHeader('Content-Type', 'application/octet-stream');
      response.end(packaged.archive);
    }
  });
  await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve));
  try {
    const address = server.address();
    if (!address || typeof address === 'string') throw new Error('fake_release_unavailable');
    const acquire = createRepositoryTestManagedPayloadResolver({
      timeoutMs: 10_000,
      transport: (url, options, callback) => {
        const headers = options.headers as Record<string, unknown>;
        seen.push({
          url: url.href,
          credential: Object.keys(headers).some((key) => /authorization|token|cookie/i.test(key)),
        });
        return httpRequest(
          new URL(url.pathname + url.search, `http://127.0.0.1:${address.port}`),
          options,
          callback,
        );
      },
    });
    const payload = await acquire({
      mapBytes: Buffer.from(JSON.stringify(map)),
      actionSourceSha: source,
      stagingParent: parent,
      workspace,
      signal: new AbortController().signal,
    });
    return {
      base,
      parent,
      tempRoot,
      workspace,
      payload,
      seen,
      async close() {
        await payload.dispose();
        await rm(base, { recursive: true, force: true });
      },
    };
  } catch (error) {
    await rm(base, { recursive: true, force: true });
    throw error;
  } finally {
    await new Promise<void>((resolve, reject) =>
      server.close((error) => (error ? reject(error) : resolve())),
    );
  }
}
