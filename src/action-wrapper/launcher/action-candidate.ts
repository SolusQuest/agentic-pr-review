import { lstat, mkdir, realpath, writeFile } from 'node:fs/promises';
import path from 'node:path';
import {
  readBoundedFile,
  sha256,
  inspectManagedPackage,
} from '../../../scripts/release/build-payload.format.mjs';
import { resolveInstalledManagedActionSource } from './action-source.js';
import { parseManagedPayloadMap, MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES } from './payload-map.js';
import { parseStrictJson } from './strict-json.js';
import { exactRecord, fail, boundedStructure } from './validation.js';
import {
  createManagedScratch,
  reopenManagedScratch,
  type ManagedScratchIdentity,
} from './managed-scratch-owner.js';
import { resolveManagedReleasePayload } from './release-payload.js';
import { reopenManagedPayload } from './payload-staging.js';
import {
  acquireRuntimePrerequisite,
  RuntimePrerequisiteTerminationUnconfirmedError,
} from './runtime-prerequisite.js';
import type { ManagedActionPayload } from '../index.js';

interface CandidateContext {
  readonly actionRoot: string;
  readonly build: string;
  readonly runId: string;
  readonly runAttempt: string;
  readonly runnerTemp: string;
  readonly workspace: string;
  readonly signal: AbortSignal;
}
const identityKeys = ['tempRoot', 'parentDev', 'parentIno', 'rootDev', 'rootIno'];
const candidateKeys = [
  'mapSha256',
  'actionSourceSha',
  'build',
  'runId',
  'runAttempt',
  'payloadRoot',
  'runtimeRoot',
  'runtimeArchive',
  'payloadDev',
  'payloadIno',
  'runtimeDev',
  'runtimeIno',
];

function identity(encoded: string): ManagedScratchIdentity {
  if (!boundedStructure(encoded, 4096)) fail('wrapper_candidate_invalid');
  const row = exactRecord(parseStrictJson(Buffer.from(encoded), 4096), identityKeys);
  if (
    !identityKeys.every((key) => boundedStructure(row[key], 2048)) ||
    !identityKeys.slice(1).every((key) => /^[0-9]+$/u.test(row[key] as string))
  )
    fail('wrapper_candidate_invalid');
  return row as unknown as ManagedScratchIdentity;
}
async function installed(context: CandidateContext) {
  if (process.platform !== 'linux' || process.arch !== 'x64') fail('wrapper_platform_unsupported');
  if (context.build !== 'r7-d0') fail('wrapper_payload_invalid');
  const mapBytes = await readBoundedFile(
    path.join(context.actionRoot, 'payload-map.json'),
    MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES,
  );
  if (parseStrictJson(mapBytes, MANAGED_PAYLOAD_MAP_MAXIMUM_BYTES) === null)
    fail('wrapper_payload_map_unbound');
  const map = parseManagedPayloadMap(mapBytes);
  const actionSourceSha = await resolveInstalledManagedActionSource({
    actionRoot: context.actionRoot,
    mapBytes,
    actionRepository: process.env.GITHUB_ACTION_REPOSITORY,
    actionRef: process.env.GITHUB_ACTION_REF,
    signal: AbortSignal.any([context.signal, AbortSignal.timeout(60_000)]),
  });
  return { map, mapBytes, actionSourceSha };
}
async function storage(
  context: Pick<CandidateContext, 'runnerTemp' | 'workspace'>,
): Promise<string> {
  if (!path.isAbsolute(context.runnerTemp) || !path.isAbsolute(context.workspace))
    fail('wrapper_candidate_invalid');
  const root = await realpath(context.runnerTemp),
    workspace = await realpath(context.workspace);
  if (root === workspace || root.startsWith(workspace + path.sep))
    fail('wrapper_candidate_invalid');
  for (let parent = root; ; parent = path.dirname(parent)) {
    const stat = await lstat(parent);
    if (
      !stat.isDirectory() ||
      (stat.uid !== 0 && stat.uid !== process.getuid?.()) ||
      ((stat.mode & 0o022) !== 0 && !(stat.uid === 0 && (stat.mode & 0o1000) !== 0))
    )
      fail('wrapper_candidate_invalid');
    if (path.dirname(parent) === parent) break;
  }
  return root;
}
function child(root: string, relative: string): string {
  if (
    path.isAbsolute(relative) ||
    !relative ||
    relative.split('/').some((part) => !part || part === '.' || part === '..')
  )
    fail('wrapper_candidate_invalid');
  return path.join(root, relative);
}
async function claim(root: string): Promise<boolean> {
  try {
    await mkdir(path.join(root, 'claimed'), { mode: 0o700 });
    return true;
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'EEXIST') return false;
    throw error;
  }
}

/** Preparation has no product inputs. Saved ownership is only for its own secret-free post. */
export async function prepareActionCandidate(
  context: CandidateContext,
  saveOwnership: (encoded: string) => void,
): Promise<string> {
  const source = await installed(context);
  const owner = await createManagedScratch(await storage(context));
  const encoded = JSON.stringify(owner.identity);
  let handedOff = false;
  let retained = false;
  let payload: Awaited<ReturnType<typeof resolveManagedReleasePayload>> | undefined;
  let runtime: Awaited<ReturnType<typeof acquireRuntimePrerequisite>> | undefined;
  try {
    saveOwnership(encoded);
    const parent = path.join(owner.tempRoot, 'payload');
    await mkdir(parent, { mode: 0o700 });
    payload = await resolveManagedReleasePayload({
      ...source,
      stagingParent: parent,
      workspace: context.workspace,
      signal: context.signal,
      retainArchive: true,
    });
    runtime = await acquireRuntimePrerequisite({
      runnerTemp: owner.tempRoot,
      workspace: context.workspace,
      signal: context.signal,
      retainArchive: true,
    });
    if (!runtime.archivePath) fail('wrapper_candidate_invalid');
    const payloadStat = await lstat(payload.root, { bigint: true }),
      runtimeStat = await lstat(runtime.trustedRuntimeRoot, { bigint: true });
    const record = {
      mapSha256: sha256(source.mapBytes),
      actionSourceSha: source.actionSourceSha,
      build: context.build,
      runId: context.runId,
      runAttempt: context.runAttempt,
      payloadRoot: path.relative(owner.tempRoot, payload.root),
      runtimeRoot: path.relative(owner.tempRoot, runtime.trustedRuntimeRoot),
      runtimeArchive: path.relative(owner.tempRoot, runtime.archivePath),
      payloadDev: payloadStat.dev.toString(),
      payloadIno: payloadStat.ino.toString(),
      runtimeDev: runtimeStat.dev.toString(),
      runtimeIno: runtimeStat.ino.toString(),
    };
    await writeFile(path.join(owner.tempRoot, 'candidate.json'), JSON.stringify(record), {
      flag: 'wx',
      mode: 0o400,
    });
    await payload.detachForHandoff();
    runtime.detachForHandoff();
    await owner.detachForHandoff();
    handedOff = true;
    return encoded;
  } catch (error) {
    if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError) {
      retained = true;
      await claim(owner.tempRoot);
    }
    throw error;
  } finally {
    if (!handedOff) {
      const settled = await Promise.allSettled([payload?.dispose(), runtime?.dispose()]);
      if (settled.some((result) => result.status === 'rejected')) retained = true;
      if (retained) {
        await claim(owner.tempRoot);
        await owner.detachForHandoff();
      } else await owner.cleanup();
    }
  }
}

/** The old step's JSON supplies candidates, never source/loading authority. */
export async function acquireActionCandidate(
  context: CandidateContext,
  encoded: string,
): Promise<ManagedActionPayload> {
  const expected = identity(encoded);
  if (path.dirname(expected.tempRoot) !== (await storage(context)))
    fail('wrapper_candidate_invalid');
  const source = await installed(context);
  const owner = await reopenManagedScratch(expected);
  let payload: Awaited<ReturnType<typeof reopenManagedPayload>> | undefined;
  let claimed = false;
  try {
    claimed = await claim(owner.tempRoot);
    if (!claimed) fail('wrapper_candidate_claimed');
    const filename = path.join(owner.tempRoot, 'candidate.json');
    const stat = await lstat(filename);
    if (
      !stat.isFile() ||
      stat.nlink !== 1 ||
      stat.uid !== process.getuid?.() ||
      (stat.mode & 0o7777) !== 0o400
    )
      fail('wrapper_candidate_invalid');
    const row = exactRecord(
      parseStrictJson(await readBoundedFile(filename, 16 * 1024), 16 * 1024),
      candidateKeys,
    );
    if (
      !candidateKeys.every((key) => boundedStructure(row[key], 2048)) ||
      row.mapSha256 !== sha256(source.mapBytes) ||
      row.actionSourceSha !== source.actionSourceSha ||
      row.build !== context.build ||
      row.runId !== context.runId ||
      row.runAttempt !== context.runAttempt
    )
      fail('wrapper_candidate_invalid');
    const root = child(owner.tempRoot, row.payloadRoot as string),
      runtimeRoot = child(owner.tempRoot, row.runtimeRoot as string);
    const runtimeArchivePath = child(owner.tempRoot, row.runtimeArchive as string);
    const payloadStat = await lstat(root, { bigint: true }),
      runtimeStat = await lstat(runtimeRoot, { bigint: true });
    if (
      payloadStat.dev.toString() !== row.payloadDev ||
      payloadStat.ino.toString() !== row.payloadIno ||
      runtimeStat.dev.toString() !== row.runtimeDev ||
      runtimeStat.ino.toString() !== row.runtimeIno
    )
      fail('wrapper_candidate_invalid');
    const archive = await readBoundedFile(
      path.join(owner.tempRoot, 'payload/payload.tar.gz'),
      source.map.payload.archiveSize,
    );
    inspectManagedPackage(archive, source.map.payload);
    payload = await reopenManagedPayload({
      existingRoot: root,
      map: source.map,
      actionSourceSha: source.actionSourceSha,
      stagingParent: path.dirname(root),
      workspace: context.workspace,
      signal: context.signal,
    });
    await owner.checkIdentity();
    const admitted = payload;
    return {
      tree: admitted.tree,
      actionSourceSha: source.actionSourceSha,
      payloadSha256: source.map.payload.archiveSha256,
      buildDiscriminator: context.build,
      trustedRuntimeRoot: runtimeRoot,
      runtimeArchivePath,
      runnerStorage: { runnerTemp: context.runnerTemp, workspace: context.workspace },
      dispose: async () => {
        await admitted.dispose();
        await owner.cleanup();
      },
    };
  } catch (error) {
    // A failed admission has no child. Reopened payload cleanup still obeys its own identity checks.
    try {
      await payload?.dispose();
      if (claimed) await owner.cleanup();
      else await owner.detachForHandoff();
    } catch (cleanupError) {
      if (error instanceof Error)
        Object.defineProperty(error, 'cleanupError', { value: cleanupError });
    }
    throw error;
  }
}

/** Preparation post never races a claimed launch or tries to recover its uncertain leases. */
export async function cleanupActionCandidate(
  encoded: string,
  runnerTemp: string,
  workspace: string,
): Promise<void> {
  const expected = identity(encoded);
  if (path.dirname(expected.tempRoot) !== (await storage({ runnerTemp, workspace })))
    fail('wrapper_candidate_invalid');
  try {
    await lstat(expected.tempRoot);
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code === 'ENOENT') return;
    throw error;
  }
  const owner = await reopenManagedScratch(expected);
  if (!(await claim(owner.tempRoot))) {
    await owner.detachForHandoff();
    return;
  }
  await owner.cleanup();
}
