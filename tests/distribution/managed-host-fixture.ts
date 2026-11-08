import { execFileSync, spawn, type ChildProcess } from 'node:child_process';
import {
  chown,
  chmod,
  rmdir,
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
import {
  stageManagedPayload,
  stageRepositoryTestOriginalRuntimeArchive,
  acquireRepositoryTestOriginalRuntimeArchiveLease,
  admitRepositoryTestOriginalRuntimeFixture,
  acquireRepositoryTestOriginalRuntimeFixtureLease,
  originalArchiveFixtureContext,
  type RepositoryTestOriginalArchiveContext,
} from '../../src/action-wrapper/launcher/payload-staging.js';
import {
  runRepositoryTestOriginalRuntimeHostProcess,
  ManagedHostProcessTerminationUnconfirmedError,
} from '../../src/action-wrapper/launcher/host-process.js';
import {
  acquireRuntimePrerequisite,
  RuntimePrerequisiteTerminationUnconfirmedError,
} from '../../src/action-wrapper/launcher/runtime-prerequisite.js';
import { startUploadRuntime } from '../../src/action-wrapper/launcher/upload-runtime.js';
import { UploadStaging } from '../../src/action-wrapper/artifact-bridge/upload-staging.js';
import { UploadOperations } from '../../src/action-wrapper/artifact-bridge/upload-operations.js';
import { SdkProcessOwner } from '../../src/action-wrapper/artifact-bridge/sdk-process-owner.js';
import {
  UPLOAD_PROTOCOL,
  type UploadReply,
  type UploadRequest,
} from '../../src/action-wrapper/artifact-bridge/upload-protocol.js';
import {
  buildLaunchDocument,
  serializeLaunchDocument,
} from '../../src/action-wrapper/launcher/contracts.js';
import { readAndMaskActionInputs } from '../../src/action-wrapper/launcher/inputs.js';
import { readProductionRuntimeFacts } from '../../src/action-wrapper/index.js';
import {
  parseCompletionDocument,
  type ActionHostCompletionDocument,
} from '../../src/action-wrapper/presentation/completion.js';
import {
  createActionsToolkit,
  presentCompletion,
} from '../../src/action-wrapper/presentation/toolkit.js';
import { claimOriginalRuntimePreviewInvocation } from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs';
import { releaseFixture, ACTION_SOURCE } from './release-payload-fixture.js';
import { parseStrictJson } from '../../src/action-wrapper/launcher/strict-json.js';
import { exactRecord, fail } from '../../src/action-wrapper/launcher/validation.js';

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

/** Pure TEST_ONLY arithmetic. It neither authenticates a request nor creates a
 * qualification, dispatch intent, financial reservation, timer or execution right.
 * The fixed genuine consumer must separately bind the Root request and real run.
 */
export interface OriginalRuntimePreviewWindow {
  readonly postedAtMs: number;
  readonly validUntilMs: number;
  readonly earliestStorageExpiryMs: number;
}
export function deriveOriginalRuntimePreviewWindow(
  bytes: Uint8Array,
  expectedRequestSha256: string,
  nowUtcMs: number,
  actualRunCreatedAtMs: number,
): OriginalRuntimePreviewWindow {
  const invalid = (): never => fail('test_only_preview_window_invalid');
  if (
    !/^[a-f0-9]{64}$/.test(expectedRequestSha256) ||
    sha256(bytes) !== expectedRequestSha256 ||
    !Number.isSafeInteger(nowUtcMs) ||
    !Number.isSafeInteger(actualRunCreatedAtMs)
  )
    invalid();
  const root = exactRecord(parseStrictJson(bytes, 4096), [
    'postedAtUtc',
    'validUntilUtc',
    'storageExpiries',
  ]);
  const utc = (value: unknown): number => {
    if (typeof value !== 'string' || !/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value))
      return invalid();
    const parsed = Date.parse(value);
    if (!Number.isSafeInteger(parsed) || new Date(parsed).toISOString() !== value) return invalid();
    return parsed;
  };
  const postedAtMs = utc(root.postedAtUtc),
    validUntilMs = utc(root.validUntilUtc);
  const ids = [13474437693, 13474377959, 13474631449, 13483914599];
  if (!Array.isArray(root.storageExpiries) || root.storageExpiries.length !== 4) invalid();
  const observed = new Set<number>();
  const expiries = (root.storageExpiries as unknown[]).map((value) => {
    const item = exactRecord(value, ['id', 'expiresAtUtc']);
    if (typeof item.id !== 'number' || !ids.includes(item.id) || observed.has(item.id))
      return invalid();
    observed.add(item.id);
    return utc(item.expiresAtUtc);
  });
  const earliestStorageExpiryMs = Math.min(...expiries);
  const hard = Date.parse('2026-11-10T03:10:06.000Z');
  if (
    postedAtMs > nowUtcMs ||
    actualRunCreatedAtMs + 1000 < postedAtMs ||
    actualRunCreatedAtMs - postedAtMs > 300_000 ||
    actualRunCreatedAtMs > nowUtcMs + 1000 ||
    validUntilMs <= nowUtcMs ||
    validUntilMs >
      Math.min(postedAtMs + 21_600_000, hard, earliestStorageExpiryMs - 72 * 3_600_000) ||
    postedAtMs > validUntilMs - 3_600_000
  )
    invalid();
  return Object.freeze({ postedAtMs, validUntilMs, earliestStorageExpiryMs });
}

/** One-shot phase arithmetic only. The two financial units are consumed at the
 * externally admitted POST, including preparation failures; this is not that ledger.
 */
export function createOriginalRuntimePreviewPairSchedule(window: OriginalRuntimePreviewWindow) {
  let next = 0;
  let stopped = false;
  let lastStartedAtMs = window.postedAtMs;
  return Object.freeze({
    beginUnit(unit: 0 | 1, nowUtcMs: number) {
      if (stopped || unit !== next) {
        stopped = true;
        fail('test_only_preview_phase_replayed');
      }
      // Mark before any asynchronous launch, never refund a failed/unknown phase.
      next += 1;
      const remainingRequired = unit === 0 ? 3_180_000 : 1_380_000;
      if (
        !Number.isSafeInteger(nowUtcMs) ||
        nowUtcMs < lastStartedAtMs ||
        nowUtcMs + remainingRequired > window.validUntilMs
      ) {
        stopped = true;
        fail('test_only_preview_phase_window_expired');
      }
      lastStartedAtMs = nowUtcMs;
      return Object.freeze({
        unit,
        startedAtMs: nowUtcMs,
        stopAtMs: nowUtcMs + 1_020_000,
        hardAtMs: nowUtcMs + 1_200_000,
        physicalAttemptLimit: 6,
        providerSecondsLimit: 120,
      });
    },
    stop() {
      stopped = true;
    },
  });
}

interface PreviewSdkFact {
  readonly name: string;
  readonly id: string;
  readonly archiveSha256: string;
  readonly archiveBytes: string;
  readonly carrierSha256: string;
}
interface SealedPreviewInputs {
  readonly archiveBytes: Buffer;
  readonly receiptBytes: Buffer;
  readonly context: RepositoryTestOriginalArchiveContext;
  readonly repo: string;
  readonly stagingParent: string;
  readonly request: Record<string, unknown>;
  readonly handoffSha256: string;
}
const previewBad = (): never => fail('test_only_original_preview_failed');

export class OriginalPreviewUnresolvedOwnershipError extends Error {
  readonly code = 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN';
  constructor(readonly owner: string) {
    super('test_only_original_ownership_unknown');
  }
}
// Retain exact live owners/leases. There is no new cleanup/retry owner after unknown.
const unresolvedPreviewOwners = new Set<unknown>();
async function beforePreviewDeadline<T>(pending: Promise<T>, deadline: number): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      pending,
      new Promise<never>((_, reject) => {
        timer = setTimeout(
          () => reject(new OriginalPreviewUnresolvedOwnershipError('deadline')),
          Math.max(0, deadline - Date.now()),
        );
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}
/** The SDK's original upload owner already performs its bounded stop. No restart or PID guessing. */
export async function originalSdkSettlement(sdk: SdkProcessOwner, deadline: number): Promise<void> {
  try {
    if (
      await beforePreviewDeadline(
        sdk.awaitTermination(Math.max(0, deadline - Date.now())),
        deadline,
      )
    )
      return;
  } catch {
    /* A missing termination fact remains unknown; never restart the owner. */
  }
  unresolvedPreviewOwners.add(sdk);
  throw new OriginalPreviewUnresolvedOwnershipError('sdk');
}
async function ownedPreviewParent(
  parent: string,
  expected: { dev: number; ino: number },
): Promise<void> {
  const current = await lstat(parent);
  if (
    (await realpath(parent)) !== parent ||
    !current.isDirectory() ||
    current.uid !== process.getuid?.() ||
    (current.mode & 0o7777) !== 0o700 ||
    current.dev !== expected.dev ||
    current.ino !== expected.ino
  )
    previewBad();
}

/** Settle this already-spawned detached preparation child only. Never selects an executor. */
export async function settleOriginalPreviewPreparationChild(
  child: ChildProcess,
  stopAt: number,
  hardAt: number,
): Promise<void> {
  let closed = false,
    code: number | null = null,
    signal: NodeJS.Signals | null = null;
  let failed = false,
    stopped = false,
    bytes = 0;
  let naturalCloseUntil: number | undefined;
  let observedNaturalTail = false;
  let members = new Map<number, string>();
  const interrupt = () => {
    failed = true;
  };
  process.once('SIGINT', interrupt);
  process.once('SIGTERM', interrupt);
  child.once('error', () => {
    failed = true;
  });
  child.once('close', (exit, exitSignal) => {
    closed = true;
    code = exit;
    signal = exitSignal;
  });
  child.once('exit', (exit, exitSignal) => {
    if (exit !== 0 || exitSignal !== null) failed = true;
    else if (!failed) naturalCloseUntil = Math.min(stopAt, hardAt, Date.now() + 2000);
  });
  for (const stream of [child.stdout, child.stderr]) {
    stream?.on('error', () => {
      failed = true;
    });
    stream?.on('data', (chunk: Buffer) => {
      bytes += chunk.byteLength;
      if (bytes > 1024 * 1024) failed = true;
    });
  }
  const groupAbsent = () => {
    if (!child.pid) return closed;
    try {
      process.kill(-child.pid, 0);
      return false;
    } catch (error) {
      return (error as NodeJS.ErrnoException).code === 'ESRCH';
    }
  };
  const member = async (pid: number) => {
    try {
      const named = '/proc/' + pid;
      const [identity, raw] = await Promise.all([lstat(named), readFile(named + '/stat', 'utf8')]);
      const fields = raw
        .slice(raw.lastIndexOf(')') + 2)
        .trim()
        .split(/\s+/);
      if (
        identity.uid !== process.getuid?.() ||
        Number(fields[2]) !== child.pid ||
        Number(fields[3]) !== child.pid
      )
        return;
      return fields[19]; // Linux stat field22 starttime, after removing pid/comm.
    } catch {
      return;
    }
  };
  try {
    // Anchor the original leader while it is live. Later descendant stops require
    // an unchanged original-session member, not a recycled numeric PGID.
    if (child.pid) {
      const birth = await beforePreviewDeadline(member(child.pid), hardAt);
      if (birth) members.set(child.pid, birth);
    }
    while (Date.now() < hardAt) {
      if (!members.size && child.pid && child.exitCode === null && child.signalCode === null) {
        const birth = await beforePreviewDeadline(member(child.pid), hardAt);
        if (birth) members.set(child.pid, birth);
      }
      const absent = groupAbsent();
      const naturalExit =
        child.exitCode === 0 && child.signalCode === null && naturalCloseUntil !== undefined;
      if (!failed && naturalExit && !absent) observedNaturalTail = true;
      if (
        Date.now() >= stopAt ||
        (observedNaturalTail && naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
      )
        failed = true;
      if (closed && absent) {
        if (failed || code !== 0 || signal !== null) previewBad();
        return;
      }
      let anchored = false;
      const next = new Map<number, string>();
      if (members.size) {
        const names = await beforePreviewDeadline(
          readdir('/proc').catch(() => []),
          hardAt,
        );
        const table = await beforePreviewDeadline(
          Promise.all(
            names
              .filter((name) => /^[1-9][0-9]*$/.test(name))
              .map(async (name) => {
                const pid = Number(name),
                  birth = await member(pid);
                return { pid, birth };
              }),
          ),
          hardAt,
        );
        anchored = table.some(
          (item) => item.birth !== undefined && members.get(item.pid) === item.birth,
        );
        if (anchored) for (const item of table) if (item.birth) next.set(item.pid, item.birth);
        members = next;
      }
      if (
        !stopped &&
        (failed ||
          Date.now() >= stopAt ||
          ((closed || child.exitCode !== null || child.signalCode !== null) &&
            !absent &&
            !(
              naturalExit &&
              !failed &&
              naturalCloseUntil !== undefined &&
              Date.now() < naturalCloseUntil
            )))
      ) {
        stopped = true;
        failed = true;
        try {
          if (child.pid && anchored) process.kill(-child.pid, 'SIGKILL');
          else if (child.pid && child.exitCode === null && child.signalCode === null)
            child.kill('SIGKILL');
          // Parent already exited with no unchanged anchor: group identity is unknown.
        } catch {
          /* A signal is never an exit/closure fact. */
        }
      }
      await new Promise<void>((resolve) =>
        setTimeout(resolve, Math.max(0, Math.min(20, hardAt - Date.now()))),
      );
    }
    if (
      Date.now() >= stopAt ||
      (observedNaturalTail && naturalCloseUntil !== undefined && Date.now() >= naturalCloseUntil)
    )
      failed = true;
    if (closed && groupAbsent()) {
      if (failed || code !== 0 || signal !== null) previewBad();
      return;
    }
    unresolvedPreviewOwners.add(child);
    throw new OriginalPreviewUnresolvedOwnershipError('preparation-child');
  } catch (error) {
    if (error instanceof OriginalPreviewUnresolvedOwnershipError)
      unresolvedPreviewOwners.add(child);
    throw error;
  } finally {
    process.removeListener('SIGINT', interrupt);
    process.removeListener('SIGTERM', interrupt);
  }
}

/** Fixed reference-only friend build. Provider/state/restore credentials are absent. */
async function fixedPreviewFriendBuild(
  repo: string,
  originalRoot: string,
  work: string,
  output: string,
  context: RepositoryTestOriginalArchiveContext,
  resource: Buffer,
  deadline: number,
): Promise<void> {
  const sdk = await realpath(
    execFileSync('/usr/bin/which', ['dotnet'], {
      encoding: 'utf8',
      timeout: Math.max(1, Math.min(10_000, deadline - Date.now() - 180_000)),
      maxBuffer: 65536,
    }).trim(),
  );
  const environment: NodeJS.ProcessEnv = {
    PATH: process.env.PATH,
    DOTNET_ROOT: path.dirname(sdk),
    HOME: path.join(work, 'home'),
    TMPDIR: path.join(work, 'tmp'),
    DOTNET_CLI_HOME: path.join(work, 'home'),
    DOTNET_NOLOGO: '1',
    DOTNET_CLI_TELEMETRY_OPTOUT: '1',
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE: '1',
    MSBUILDDISABLENODEREUSE: '1',
    DOTNET_CLI_USE_MSBUILD_SERVER: '0',
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE: '1',
    NUGET_PACKAGES: path.join(work, 'empty-packages'),
  };
  for (const name of ['home', 'tmp', 'empty-feed', 'empty-packages'])
    await mkdir(path.join(work, name), { mode: 0o700 });
  if (
    execFileSync(sdk, ['--version'], {
      cwd: repo,
      env: environment,
      encoding: 'utf8',
      timeout: 10_000,
    }).trim() !== '10.0.109'
  )
    previewBad();
  const resourcePath = path.join(work, 'context.json');
  await writeFile(resourcePath, resource, { flag: 'wx', mode: 0o400 });
  const project = path.join(
    repo,
    'runtime/tests/ActionHostTrustedProofPayload/AgenticPrReview.Runtime.ActionHostTrustedProofPayload.csproj',
  );
  const properties = [
    '-p:APRTestOriginalRuntimePreview=true',
    '-p:APRTestOriginalRuntimeRoot=' + path.join(originalRoot, 'agentic-pr-review'),
    '-p:APRTestOriginalRuntimeAssemblySHA256=' + context.assemblySha256,
    '-p:APRTestOriginalRuntimeSourceRole=' + context.sourceRole,
    '-p:APRTestOriginalRuntimeContextPath=' + resourcePath,
    '-p:APRTestOriginalRuntimeContextSHA256=' + sha256(resource),
    '-p:PayloadSourceCommit=' + context.sourceCommit,
    '-p:PayloadSourceTree=' + context.sourceTree,
    '-p:BaseIntermediateOutputPath=' + path.join(work, 'obj/'),
    '-p:MSBuildProjectExtensionsPath=' + path.join(work, 'obj/'),
    '-p:OutputPath=' + output + '/',
    '-p:AppendTargetFrameworkToOutputPath=false',
    '-p:AppendRuntimeIdentifierToOutputPath=false',
    '-p:NuGetAudit=false',
    '-p:UseSharedCompilation=false',
    '-nodeReuse:false',
  ];
  for (const args of [
    [
      'restore',
      project,
      '--source',
      path.join(work, 'empty-feed'),
      '--disable-parallel',
      '--nologo',
      ...properties,
    ],
    ['build', project, '--no-restore', '--nologo', '-m:1', ...properties],
  ]) {
    const remaining = Math.min(120_000, deadline - Date.now() - 180_000);
    if (remaining <= 0) previewBad();
    const child = spawn(sdk, args, {
      cwd: repo,
      env: environment,
      detached: true,
      stdio: ['ignore', 'pipe', 'pipe'],
      shell: false,
    });
    const stopAt = Math.min(deadline - 180_000, Date.now() + remaining);
    await settleOriginalPreviewPreparationChild(
      child,
      stopAt,
      Math.min(deadline, stopAt + 180_000),
    );
  }
}

function previewInputs() {
  const actions = createActionsToolkit();
  const values: Record<string, string> = {
    'github-token': process.env.GITHUB_TOKEN ?? '',
    'provider-api-key': process.env.DEEPSEEK_API_KEY ?? '',
    'state-key': process.env.AGENTIC_PR_REVIEW_STATE_KEY ?? '',
    'previous-state-key': '',
    'config-path': '',
    'pr-number': '3',
    'state-mode': 'auto',
  };
  if (!values['github-token'] || !values['provider-api-key'] || !values['state-key']) previewBad();
  return {
    actions,
    inputs: readAndMaskActionInputs({ ...actions, getInput: (name) => values[name] ?? '' }),
  };
}

/** Called only after the same-process genuine consumption token has been claimed. */
async function prepareOriginalPreview(inputs: SealedPreviewInputs, validUntilMs: number) {
  const { repo, context } = inputs;
  if (
    (await realpath(repo)) !== repo ||
    execFileSync('/usr/bin/git', ['rev-parse', 'HEAD'], { cwd: repo, encoding: 'utf8' }).trim() !==
      context.sourceCommit ||
    execFileSync('/usr/bin/git', ['rev-parse', 'HEAD^{tree}'], {
      cwd: repo,
      encoding: 'utf8',
    }).trim() !== context.sourceTree ||
    execFileSync('/usr/bin/git', ['status', '--porcelain', '--untracked-files=no'], {
      cwd: repo,
      encoding: 'utf8',
    }) !== ''
  )
    previewBad();
  const sdkEntry = path.join(repo, '.github/actions/agentic-pr-review/internal/dist/sdk-upload.js');
  if (
    !(await readFile(sdkEntry)).equals(
      execFileSync(
        '/usr/bin/git',
        [
          'show',
          context.sourceCommit + ':.github/actions/agentic-pr-review/internal/dist/sdk-upload.js',
        ],
        { cwd: repo, maxBuffer: 4 * 1024 * 1024 },
      ),
    )
  )
    previewBad();
  const runnerTemp = await realpath(process.env.RUNNER_TEMP ?? '');
  const signal = AbortSignal.timeout(Math.max(1, Math.min(21_600_000, validUntilMs - Date.now())));
  const original = await stageRepositoryTestOriginalRuntimeArchive({
    archiveBytes: inputs.archiveBytes,
    receiptBytes: inputs.receiptBytes,
    context,
    stagingParent: inputs.stagingParent,
    workspace: repo,
    signal,
  });
  const parentStat = await lstat(inputs.stagingParent);
  let work = '',
    output = '';
  let fixture: Awaited<ReturnType<typeof admitRepositoryTestOriginalRuntimeFixture>> | undefined;
  let runtime: Awaited<ReturnType<typeof acquireRuntimePrerequisite>> | undefined;
  let compilationLease:
    | Awaited<ReturnType<typeof acquireRepositoryTestOriginalRuntimeArchiveLease>>
    | undefined;
  try {
    work = await mkdtemp(path.join(inputs.stagingParent, 'friend-build-'));
    output = await mkdtemp(path.join(inputs.stagingParent, 'friend-entry-'));
    await chmod(work, 0o700);
    await chmod(output, 0o700);
    compilationLease = await acquireRepositoryTestOriginalRuntimeArchiveLease(
      original.archive,
      context,
      signal,
    );
    const resource = Buffer.from(
      JSON.stringify({
        workflowCommit: context.workflowCommit,
        workflowTemplateSha256: inputs.request.workflowTemplateSha256,
        runId: context.runId,
        reviewBaseSha: inputs.request.reviewBaseSha,
        receiptBase64: inputs.receiptBytes.toString('base64'),
      }) + '\n',
    );
    await fixedPreviewFriendBuild(
      repo,
      original.root,
      work,
      output,
      context,
      resource,
      validUntilMs,
    );
    const names = (await readdir(output)).sort();
    const members = [];
    for (const name of names) {
      const bytes = await readFile(path.join(output, name));
      members.push({ path: name, mode: 0o644, size: bytes.length, sha256: sha256(bytes) });
    }
    fixture = await admitRepositoryTestOriginalRuntimeFixture({
      root: output,
      stagingParent: inputs.stagingParent,
      workspace: repo,
      context: originalArchiveFixtureContext(context),
      members,
      signal,
    });
    runtime = await acquireRuntimePrerequisite({
      runnerTemp,
      workspace: repo,
      signal,
      retainArchive: true,
    });
  } catch (error) {
    if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError) {
      try {
        await beforePreviewDeadline(error.resourcesReleased, validUntilMs);
      } catch (unknown) {
        unresolvedPreviewOwners.add({ original, fixture, runtime, compilationLease, work, output });
        throw unknown;
      }
    }
    if (error instanceof OriginalPreviewUnresolvedOwnershipError) {
      unresolvedPreviewOwners.add({ original, fixture, runtime, compilationLease, work, output });
      throw error;
    }
    compilationLease?.release();
    await runtime?.dispose();
    await fixture?.dispose();
    await original.dispose();
    await ownedPreviewParent(inputs.stagingParent, parentStat);
    if (work) await rm(work, { recursive: true, force: false });
    if (output && !fixture) await rm(output, { recursive: true, force: false });
    throw error;
  }
  compilationLease.release();
  const retainedFixture = fixture,
    retainedRuntime = runtime;
  let failed = false;
  let running = false;
  let unresolved = false;
  const runUnit = async (
    phase: 'bootstrap' | 'continuation',
    timing: { stopAtMs: number; hardAtMs: number },
  ) => {
    if (failed || running || !retainedFixture || !retainedRuntime) previewBad();
    running = true;
    let parentLease:
      | Awaited<ReturnType<typeof acquireRepositoryTestOriginalRuntimeArchiveLease>>
      | undefined;
    let fixtureLease:
      | Awaited<ReturnType<typeof acquireRepositoryTestOriginalRuntimeFixtureLease>>
      | undefined;
    let bridge: Awaited<ReturnType<typeof startUploadRuntime>> | undefined;
    const phaseStop = new AbortController();
    const stopTimer = setTimeout(
      () => phaseStop.abort(),
      Math.max(1, timing.stopAtMs - Date.now()),
    );
    let operations: UploadOperations | undefined, sdk: SdkProcessOwner | undefined;
    const uploads: PreviewSdkFact[] = [];
    let completed: ActionHostCompletionDocument | undefined;
    let completedObservation: Buffer | undefined;
    try {
      parentLease = await acquireRepositoryTestOriginalRuntimeArchiveLease(
        original.archive,
        context,
        signal,
      );
      fixtureLease = await acquireRepositoryTestOriginalRuntimeFixtureLease(
        retainedFixture.fixture,
        originalArchiveFixtureContext(context),
        signal,
      );
      bridge = await startUploadRuntime({
        buildDiscriminator: 'r7-d0',
        runnerStorage: { runnerTemp, workspace: repo },
        executorFactory: async (stagingRoot) => {
          sdk = new SdkProcessOwner(sdkEntry, process.env);
          operations = new UploadOperations(
            await UploadStaging.create(stagingRoot),
            sdk,
            String(context.runId),
            String(context.attempt),
          );
          const active = operations;
          return {
            execute: async (
              request: UploadRequest,
              cancellation: AbortSignal,
            ): Promise<UploadReply> => {
              const reply = await active.execute(request, cancellation);
              if (reply.dispatch === 'dispatched' && reply.failure === 'none' && reply.receipt) {
                if (uploads.length >= 128) previewBad();
                uploads.push({
                  name: request.physical_name,
                  id: reply.receipt.artifact_id,
                  archiveSha256: reply.receipt.archive_sha256,
                  archiveBytes: reply.receipt.archive_size,
                  carrierSha256: request.source_sha256,
                });
              }
              return reply;
            },
            sealAdmission: () => active.sealAdmission(),
            awaitQuiescence: (ms) => active.awaitQuiescence(ms),
            dispose: () => active.dispose(),
          };
        },
      });
      const { actions, inputs: rawInputs } = previewInputs();
      const facts = readProductionRuntimeFacts();
      const eventBytes = await readFile(facts.eventJsonPath);
      const launch = serializeLaunchDocument(
        buildLaunchDocument({
          inputs: rawInputs,
          runtimeFacts: facts,
          eventJsonSha256: sha256(eventBytes),
          prepared: {
            actionSourceSha: context.sourceCommit,
            payloadSha256: context.archiveSha256,
            buildDiscriminator: 'r7-d0',
          },
          artifactBridgeEndpoint: bridge.endpoint,
          cancellation: 'active',
        }),
      );
      try {
        const result = await runRepositoryTestOriginalRuntimeHostProcess({
          tree: original.archive,
          fixture: retainedFixture.fixture,
          context,
          phase,
          launchBytes: launch,
          tempRoot: bridge.tempRoot,
          trustedRuntimeRoot: retainedRuntime.trustedRuntimeRoot,
          runtimeArchivePath: retainedRuntime.archivePath,
          workspace: repo,
          signal: AbortSignal.any([signal, phaseStop.signal]),
        });
        completed = parseCompletionDocument(result.completionBytes, 'r7-d0', result.exitCode);
        completedObservation = result.repositoryTestCompletedObservationBytes;
      } catch (error) {
        failed = true;
        if (error instanceof ManagedHostProcessTerminationUnconfirmedError)
          await beforePreviewDeadline(error.resourcesReleased, timing.hardAtMs);
        throw error;
      } finally {
        launch.fill(0);
      }
      await beforePreviewDeadline(bridge.stopAndDrain(), timing.hardAtMs);
      if (
        operations &&
        !(await operations.awaitQuiescence(Math.max(0, timing.hardAtMs - Date.now())))
      ) {
        failed = true;
        if (sdk) await originalSdkSettlement(sdk, timing.hardAtMs);
        previewBad();
      }
      await beforePreviewDeadline(bridge.cleanup(), timing.hardAtMs);
      if (Date.now() > timing.hardAtMs || !completed || completed.process_exit_code !== 0)
        previewBad();
      await presentCompletion(actions, completed);
      return Object.freeze({
        completion: completed,
        completedObservation,
        uploads: Object.freeze(uploads),
        hostClosed: true,
        stateSdkClosed: true,
        bridgeCleaned: true,
      });
    } catch (error) {
      failed = true;
      if (error instanceof OriginalPreviewUnresolvedOwnershipError) unresolved = true;
      throw error;
    } finally {
      clearTimeout(stopTimer);
      let cleanupFailed = false;
      if (bridge)
        try {
          await beforePreviewDeadline(bridge.stopAndDrain(), timing.hardAtMs);
        } catch {
          cleanupFailed = true;
          unresolved = true;
        }
      if (sdk) {
        try {
          await originalSdkSettlement(sdk, timing.hardAtMs);
        } catch {
          unresolved = true;
        }
      }
      if (unresolved) {
        failed = true;
        unresolvedPreviewOwners.add({
          sdk,
          operations,
          bridge,
          parentLease,
          fixtureLease,
          original,
          retainedFixture,
          retainedRuntime,
        });
        running = false;
        throw new OriginalPreviewUnresolvedOwnershipError('unit');
      }
      if (bridge && (!operations || (await operations.awaitQuiescence(0)))) {
        try {
          await beforePreviewDeadline(bridge.cleanup(), timing.hardAtMs);
        } catch {
          cleanupFailed = true;
        }
      } else if (operations) cleanupFailed = true;
      if (cleanupFailed) {
        failed = true;
        unresolved = true;
        running = false;
        unresolvedPreviewOwners.add({
          sdk,
          operations,
          bridge,
          parentLease,
          fixtureLease,
          original,
          retainedFixture,
          retainedRuntime,
        });
        throw new OriginalPreviewUnresolvedOwnershipError('unit-cleanup');
      }
      fixtureLease?.release();
      parentLease?.release();
      running = false;
      if (cleanupFailed) {
        failed = true;
        previewBad();
      }
    }
  };
  return Object.freeze({
    runUnit,
    originalReceipt: original.receipt,
    revalidate: async () => {
      const originalLease = await acquireRepositoryTestOriginalRuntimeArchiveLease(
        original.archive,
        context,
        signal,
      );
      try {
        const friendLease = await acquireRepositoryTestOriginalRuntimeFixtureLease(
          retainedFixture!.fixture,
          originalArchiveFixtureContext(context),
          signal,
        );
        friendLease.release();
      } finally {
        originalLease.release();
      }
    },
    fixtureFiles: Object.freeze(
      await Promise.all(
        (await readdir(output)).sort().map(async (name) => {
          const bytes = await readFile(path.join(output, name));
          return { path: name, bytes: bytes.length, sha256: sha256(bytes) };
        }),
      ),
    ),
    dispose: async () => {
      if (unresolved) throw new OriginalPreviewUnresolvedOwnershipError('prepared');
      if (running) previewBad();
      await retainedRuntime?.dispose();
      await retainedFixture?.dispose();
      await original.dispose();
      await ownedPreviewParent(inputs.stagingParent, parentStat);
      await rm(work, { recursive: true, force: false });
    },
  });
}

async function previewGitHubRead(
  kind: 'run' | 'pull',
  context: RepositoryTestOriginalArchiveContext,
) {
  const endpoint = kind === 'run' ? '/actions/runs/' + context.runId : '/pulls/3';
  const response = await fetch(
    'https://api.github.com/repos/SolusQuest/agentic-pr-review-sandbox' + endpoint,
    {
      method: 'GET',
      redirect: 'error',
      signal: AbortSignal.timeout(30_000),
      headers: {
        Authorization: 'Bearer ' + (process.env.GITHUB_TOKEN ?? ''),
        Accept: 'application/vnd.github+json',
        'X-GitHub-Api-Version': '2022-11-28',
        'User-Agent': 'APR-TEST-ONLY-original-preview',
      },
    },
  );
  if (response.status !== 200 || !response.body) previewBad();
  const reader = response.body!.getReader();
  const parts: Uint8Array[] = [];
  let length = 0;
  try {
    while (true) {
      const item = await reader.read();
      if (item.done) break;
      length += item.value.byteLength;
      if (length > 262144) previewBad();
      parts.push(item.value);
    }
  } finally {
    await reader.cancel().catch(() => {});
    reader.releaseLock();
  }
  const value = parseStrictJson(Buffer.concat(parts), 262144);
  if (!value || typeof value !== 'object' || Array.isArray(value)) previewBad();
  return value as Record<string, any>;
}

/** Validation of the fixed original result channel; caller bytes alone confer no custody authority. */
export function readOriginalPreviewCompletedObservation(
  bytes: Buffer | undefined,
  context: Pick<
    RepositoryTestOriginalArchiveContext,
    'runId' | 'attempt' | 'sourceCommit' | 'workflowCommit'
  >,
  head: string,
  predecessor?: {
    familySha256: string;
    logicalGenerationIdentity: string;
    envelopeSha256: string;
    checkpointSha256: string;
  },
) {
  if (!bytes) previewBad();
  const value = exactRecord(parseStrictJson(bytes!, 4096), [
    'classification',
    'runId',
    'runAttempt',
    'actionSourceSha',
    'workflowSha',
    'reviewedHead',
    'familySha256',
    'logicalGenerationIdentity',
    'envelopeSha256',
    'checkpointSha256',
    'predecessorLogicalGenerationIdentity',
    'predecessorEnvelopeSha256',
    'sourceCheckpointSha256',
  ]);
  if (
    value.runId !== context.runId ||
    value.runAttempt !== context.attempt ||
    value.actionSourceSha !== context.sourceCommit ||
    value.workflowSha !== context.workflowCommit ||
    value.reviewedHead !== head
  )
    previewBad();
  for (const field of [
    'familySha256',
    'logicalGenerationIdentity',
    'envelopeSha256',
    'checkpointSha256',
  ])
    if (typeof value[field] !== 'string' || !/^[a-f0-9]{64}$/.test(value[field] as string))
      previewBad();
  if (!predecessor) {
    if (
      value.classification !== 'FreshAccepted' ||
      value.predecessorLogicalGenerationIdentity !== null ||
      value.predecessorEnvelopeSha256 !== null ||
      value.sourceCheckpointSha256 !== null
    )
      previewBad();
  } else if (
    value.classification !== 'RestoredConsumedAccepted' ||
    value.familySha256 !== predecessor.familySha256 ||
    value.predecessorLogicalGenerationIdentity !== predecessor.logicalGenerationIdentity ||
    value.predecessorEnvelopeSha256 !== predecessor.envelopeSha256 ||
    value.sourceCheckpointSha256 !== predecessor.checkpointSha256 ||
    value.logicalGenerationIdentity === predecessor.logicalGenerationIdentity ||
    value.envelopeSha256 === predecessor.envelopeSha256 ||
    value.checkpointSha256 === predecessor.checkpointSha256
  )
    previewBad();
  return Object.freeze(
    value as {
      classification: 'FreshAccepted' | 'RestoredConsumedAccepted';
      familySha256: string;
      logicalGenerationIdentity: string;
      envelopeSha256: string;
      checkpointSha256: string;
    },
  );
}

function confirmedPreviewReview(
  result: Awaited<ReturnType<Awaited<ReturnType<typeof prepareOriginalPreview>>['runUnit']>>,
  head: string,
  context: RepositoryTestOriginalArchiveContext,
  predecessor?: ReturnType<typeof readOriginalPreviewCompletedObservation>,
) {
  const observed = readOriginalPreviewCompletedObservation(
    result.completedObservation,
    context,
    head,
    predecessor,
  );
  const output = result.completion.outputs;
  if (
    !['reviewed', 'reviewed_with_inline_warnings'].includes(output.status ?? '') ||
    output['termination-reason'] !== 'review_completed' ||
    output['attempt-accounting-completeness'] !== 'complete' ||
    output['usage-completeness'] !== 'complete' ||
    output['provider-unknown-usage-attempts'] !== '0' ||
    output['provider-unknown-cache-partition-attempts'] !== '0' ||
    !result.completion.summary.includes('\nReviewed SHA: ' + head + '\n') ||
    !result.completion.summary.includes('\nState disposition: accepted\n') ||
    result.uploads.length === 0
  )
    previewBad();
  const usage: Record<string, number> = {};
  for (const key of [
    'model-calls',
    'provider-attempts',
    'provider-retries',
    'provider-failed-attempts',
    'input-tokens',
    'input-cache-hit-tokens',
    'input-cache-miss-tokens',
    'output-tokens',
  ] as const) {
    const raw = output[key];
    if (
      typeof raw !== 'string' ||
      !/^(0|[1-9][0-9]{0,15})$/.test(raw) ||
      !Number.isSafeInteger(Number(raw))
    )
      previewBad();
    usage[key] = Number(raw);
  }
  if (usage['model-calls'] < 1 || usage['provider-attempts'] < 1 || usage['provider-attempts'] > 6)
    previewBad();
  const publication =
    /^Publication URL: https:\/\/github\.com\/SolusQuest\/agentic-pr-review-sandbox\/pull\/3#(issuecomment|pullrequestreview)-([1-9][0-9]{0,18})$/m.exec(
      result.completion.summary,
    );
  const count = /^Finding count: (0|[1-9][0-9]{0,3})$/m.exec(result.completion.summary);
  if (!publication || !count) previewBad();
  return Object.freeze({
    head,
    publicationKind: publication![1],
    publicationId: publication![2],
    findingCount: Number(count![1]),
    stateAccepted: true,
    observed,
    usage,
    hostClosed: result.hostClosed,
    stateSdkClosed: result.stateSdkClosed,
    bridgeCleaned: result.bridgeCleaned,
    stateSdkUploads: result.uploads,
  });
}

/** ONE plain, closed metadata record transported by the unchanged fixed SDK protocol.
 * The logical bootstrap-evidence family is distinct from all production state families.
 * Its manifest does not attest to its own later uploader termination.
 */
async function uploadBootstrapEvidence(
  context: RepositoryTestOriginalArchiveContext,
  manifest: object,
  sdkEntry: string,
  outerDeadline: number,
) {
  const hardAt = Math.min(outerDeadline, Date.now() + 300_000);
  if (hardAt - Date.now() < 180_001) previewBad();
  const body = Buffer.from(JSON.stringify(manifest) + '\n');
  if (body.length > 65536) previewBad();
  const root = await mkdtemp(
    path.join(await realpath(process.env.RUNNER_TEMP ?? ''), 'apr-bootstrap-evidence-'),
  );
  await chmod(root, 0o700);
  const original = await lstat(root);
  const source = path.join(root, 'bootstrap-evidence.json');
  await writeFile(source, body, { flag: 'wx', mode: 0o400 });
  const family = sha256(
    'apr.test-only.bootstrap-evidence.v1\0' +
      [1383960589, context.runId, context.attempt, context.sourceCommit, context.sourceRole].join(
        '\0',
      ),
  );
  const name = 'apr-object-' + family + '-' + sha256(body);
  const sdk = new SdkProcessOwner(sdkEntry, process.env);
  const operations = new UploadOperations(
    await UploadStaging.create(root),
    sdk,
    String(context.runId),
    String(context.attempt),
  );
  try {
    // This single application is irrevocably attempted here. There is no retry or new correlation.
    if (hardAt - Date.now() <= 180_000) previewBad();
    const reply = await operations.execute(
      {
        protocol: UPLOAD_PROTOCOL,
        build_discriminator: 'r7-d0',
        correlation_id: 'bootstrap-evidence',
        run_id: String(context.runId),
        run_attempt: String(context.attempt),
        physical_name: name,
        source_relative_path: 'bootstrap-evidence.json',
        source_size: String(body.length),
        source_sha256: sha256(body),
        retention_days: '1',
        timeout_ms: String(Math.min(120_000, hardAt - Date.now() - 180_000)),
      },
      AbortSignal.timeout(Math.max(1, hardAt - Date.now() - 180_000)),
    );
    if (
      !(await beforePreviewDeadline(
        operations.awaitQuiescence(Math.max(0, hardAt - Date.now())),
        hardAt,
      ))
    ) {
      await originalSdkSettlement(sdk, hardAt);
      previewBad();
    }
    if (reply.dispatch !== 'dispatched' || reply.failure !== 'none' || !reply.receipt) previewBad();
    return Object.freeze({
      logicalName: 'bootstrap-evidence',
      physicalName: name,
      ...reply.receipt,
      uploaderClosed: true,
      manifestSha256: sha256(body),
    });
  } finally {
    try {
      await originalSdkSettlement(sdk, hardAt);
    } catch (error) {
      unresolvedPreviewOwners.add({ sdk, operations, root });
      throw error;
    }
    if (await operations.awaitQuiescence(0)) {
      await operations.dispose();
      await ownedPreviewParent(root, original);
      await rm(source, { force: false });
      await rmdir(root);
    } else {
      unresolvedPreviewOwners.add({ sdk, operations, root });
      throw new OriginalPreviewUnresolvedOwnershipError('metadata-operations');
    }
  }
}

/** The only exported real route requires a private live token issued inside the
 * current genuine sealed-once consumption closure. Receipt JSON cannot mint it.
 */
export async function runOriginalRuntimePreviewFromCustody(invocation: object) {
  const claimed = claimOriginalRuntimePreviewInvocation(invocation);
  const request = exactRecord(parseStrictJson(claimed.requestRaw, 16384), [
    'kind',
    'role',
    'sourceCommit',
    'sourceTree',
    'workflowCommit',
    'workflowTemplateSha256',
    'reviewBaseSha',
    'window',
  ]);
  const context = claimed.context as RepositoryTestOriginalArchiveContext;
  if (
    request.kind !== 'apr.test-only.original-runtime.pair.v1' ||
    request.role !== context.sourceRole ||
    request.sourceCommit !== context.sourceCommit ||
    request.sourceTree !== context.sourceTree ||
    request.workflowCommit !== context.workflowCommit ||
    typeof request.workflowTemplateSha256 !== 'string' ||
    !/^[a-f0-9]{64}$/.test(request.workflowTemplateSha256) ||
    typeof request.reviewBaseSha !== 'string' ||
    !/^[a-f0-9]{40}$/.test(request.reviewBaseSha)
  )
    previewBad();
  previewInputs(); // Mask the ordinary job secret inputs before any preparation diagnostics.
  const actual = await previewGitHubRead('run', context);
  if (
    actual.id !== context.runId ||
    actual.run_attempt !== 1 ||
    actual.workflow_id !== 410349377 ||
    actual.repository?.id !== 1383960589 ||
    actual.head_repository?.id !== 1383960589 ||
    actual.head_sha !== context.workflowCommit ||
    actual.head_branch !== 'main' ||
    actual.event !== 'workflow_dispatch' ||
    actual.actor?.id !== 16307884 ||
    actual.triggering_actor?.id !== 16307884 ||
    actual.path !== '.github/workflows/agentic-pr-review.yml'
  )
    previewBad();
  const windowBytes = Buffer.from(JSON.stringify(request.window));
  const window = deriveOriginalRuntimePreviewWindow(
    windowBytes,
    sha256(windowBytes),
    Date.now(),
    Date.parse(actual.created_at),
  );
  const schedule = createOriginalRuntimePreviewPairSchedule(window);
  const targets =
    context.sourceRole === 'pr'
      ? ['dc4faa89877dbc2c9f31071ffe56d0867f854feb', '29fbcd463304677ab3cacf7de6ec957895ba3c4b']
      : ['29fbcd463304677ab3cacf7de6ec957895ba3c4b', 'f4794917c71513c5ab0fa3f7eb820a87f5d07bea'];
  const prepared = await prepareOriginalPreview(
    { ...claimed, context, request },
    window.validUntilMs,
  );
  try {
    const first = confirmedPreviewReview(
      await prepared.runUnit('bootstrap', schedule.beginUnit(0, Date.now())),
      targets[0],
      context,
    );
    // Re-audit both exact inventories before publishing the closed bootstrap facts.
    // No binary, raw state, provider text, error/path/env or uploader self-closure enters this object.
    await prepared.revalidate();
    const manifest = {
      kind: 'apr.test-only.bootstrap-evidence.v1',
      repositoryId: 1383960589,
      runId: context.runId,
      runAttempt: 1,
      sourceRole: context.sourceRole,
      aprSourceCommit: context.sourceCommit,
      aprSourceTree: context.sourceTree,
      harnessCommit: context.workflowCommit,
      reviewBaseSha: request.reviewBaseSha,
      archiveSha256: context.archiveSha256,
      receiptSha256: context.receiptSha256,
      originalAssemblySha256: context.assemblySha256,
      handoffSha256: claimed.handoffSha256,
      fixtureFiles: prepared.fixtureFiles,
      bootstrap: first,
      postedAtUtc: new Date(window.postedAtMs).toISOString(),
      validUntilUtc: new Date(window.validUntilMs).toISOString(),
    };
    const evidence = await uploadBootstrapEvidence(
      context,
      manifest,
      path.join(claimed.repo, '.github/actions/agentic-pr-review/internal/dist/sdk-upload.js'),
      window.validUntilMs,
    );
    process.stdout.write('APR_TEST_ONLY_BOOTSTRAP_EVIDENCE ' + JSON.stringify(evidence) + '\n');
    const waitUntil = Math.min(Date.now() + 300_000, window.validUntilMs - 1_380_000);
    let advanced = false;
    while (Date.now() < waitUntil) {
      const pull = await previewGitHubRead('pull', context);
      if (
        pull.number !== 3 ||
        pull.state !== 'open' ||
        pull.draft ||
        pull.merged ||
        pull.base?.sha !== request.reviewBaseSha ||
        pull.base?.repo?.id !== 1383960589 ||
        pull.head?.repo?.id !== 1383960589
      )
        previewBad();
      if (pull.head?.sha === targets[1]) {
        advanced = true;
        break;
      }
      if (pull.head?.sha !== targets[0]) previewBad();
      await new Promise<void>((resolve) => setTimeout(resolve, 5000));
    }
    if (!advanced) previewBad();
    await prepared.revalidate();
    const second = confirmedPreviewReview(
      await prepared.runUnit('continuation', schedule.beginUnit(1, Date.now())),
      targets[1],
      context,
      first.observed,
    );
    await prepared.revalidate();
    return Object.freeze({
      testOnly: true,
      pairComplete: true,
      restoredConsumption: second.observed.classification,
      sourceRole: context.sourceRole,
      runId: context.runId,
      archiveSha256: context.archiveSha256,
      receiptSha256: context.receiptSha256,
      bootstrapEvidence: evidence,
      bootstrap: first,
      continuation: second,
    });
  } finally {
    schedule.stop();
    await prepared.dispose();
  }
}
