import { chmod, lstat, mkdir, mkdtemp, realpath, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { Server, Socket } from 'node:net';

import {
  createArtifactBridgeServer,
  type ArtifactBridgeExecutor,
} from '../artifact-bridge/index.js';
import { fail } from './validation.js';
import { createManagedScratch } from './managed-scratch-owner.js';

const SOCKET_PATH_LIMIT = 100;
const CONNECTION_DRAIN_MS = 2_000;

export interface ArtifactBridgeRuntime {
  readonly endpoint: string;
  readonly stagingRoot: string;
  readonly tempRoot: string;
  /** Managed callers require this capability before launching a child. */
  sealAdmission?(): void;
  stopAndDrain(): Promise<void>;
  cleanup(): Promise<void>;
}

export async function startArtifactBridgeRuntime(input: {
  readonly buildDiscriminator: string;
  readonly executorFactory: (stagingRoot: string) => Promise<ArtifactBridgeExecutor>;
  /** Compiled historical fixture only; ordinary callers always use the T1 server. */
  readonly historicalServerFactory?: typeof createArtifactBridgeServer;
  readonly runnerStorage?: { readonly runnerTemp: string; readonly workspace: string };
}): Promise<ArtifactBridgeRuntime> {
  if (process.platform !== 'linux') fail('wrapper_platform_unsupported');
  let parent = tmpdir();
  if (input.runnerStorage) {
    const { runnerTemp, workspace } = input.runnerStorage;
    if (!path.isAbsolute(runnerTemp) || !path.isAbsolute(workspace)) fail('wrapper_bridge_invalid');
    parent = await realpath(runnerTemp);
    const reviewedWorkspace = await realpath(workspace);
    if (parent === reviewedWorkspace || parent.startsWith(reviewedWorkspace + path.sep))
      fail('wrapper_bridge_invalid');
    for (let ancestor = parent; ; ancestor = path.dirname(ancestor)) {
      const storage = await lstat(ancestor);
      if (
        !storage.isDirectory() ||
        (storage.uid !== 0 && storage.uid !== process.getuid?.()) ||
        ((storage.mode & 0o022) !== 0 && !(storage.uid === 0 && (storage.mode & 0o1000) !== 0))
      )
        fail('wrapper_bridge_invalid');
      if (path.dirname(ancestor) === ancestor) break;
    }
  }
  const managedScratch = input.runnerStorage ? await createManagedScratch(parent) : undefined;
  const tempRoot = managedScratch?.tempRoot ?? (await mkdtemp(path.join(parent, 'apr-w1-')));
  const removeScratch = async (): Promise<void> => {
    if (managedScratch) await managedScratch.cleanup();
    else await rm(tempRoot, { recursive: true, force: true });
  };
  if (!managedScratch) await chmod(tempRoot, 0o700);
  const endpoint = path.join(tempRoot, 'bridge.sock');
  if (Buffer.byteLength(endpoint, 'utf8') > SOCKET_PATH_LIMIT) {
    await removeScratch();
    fail('wrapper_bridge_invalid');
  }
  const stagingRoot = path.join(tempRoot, 'artifact-staging');
  try {
    await mkdir(stagingRoot, { mode: 0o700 });
    await chmod(stagingRoot, 0o700);
  } catch {
    await removeScratch();
    fail('wrapper_bridge_invalid');
  }
  let executorPromise: Promise<ArtifactBridgeExecutor> | undefined;
  let executorAccepting = true;
  const lazyExecutor: ArtifactBridgeExecutor = {
    execute: async (command, signal) => {
      if (!executorAccepting) throw new Error('artifact_bridge_executor_stopped');
      executorPromise ??= input.executorFactory(stagingRoot);
      return await (await executorPromise).execute(command, signal);
    },
    dispose: async () => {
      if (!executorPromise) return;
      await (await executorPromise).dispose?.();
    },
    stopAndDrain: async () => {
      if (!executorPromise) return;
      await (await executorPromise).stopAndDrain?.();
    },
  };
  const server = (input.historicalServerFactory ?? createArtifactBridgeServer)({
    endpoint,
    buildDiscriminator: input.buildDiscriminator,
    executor: lazyExecutor,
  });
  const sockets = new Set<Socket>();
  server.on('connection', (socket) => {
    sockets.add(socket);
    socket.once('close', () => sockets.delete(socket));
  });
  try {
    await listen(server, endpoint);
    await chmod(endpoint, 0o600);
  } catch {
    server.close();
    await removeScratch();
    fail('wrapper_bridge_invalid');
  }
  let stopped = false;
  let cleaned = false;
  let managedCleanup: Promise<void> | undefined;
  const sealAdmission = (): void => {
    // Do not stop admitted reconciliation, seal tracked SDK calls or release storage here.
    executorAccepting = false;
  };
  return {
    endpoint,
    stagingRoot,
    tempRoot,
    sealAdmission,
    stopAndDrain: async () => {
      if (stopped) return;
      stopped = true;
      sealAdmission();
      // Close listener admission and executor command admission together.
      // Existing connections may finish framing while the socket drain runs,
      // but their executor calls now fail before any lifecycle work starts.
      const socketDrain = stopAndDrain(server, sockets);
      const executorDrain = lazyExecutor.stopAndDrain?.();
      await Promise.all([socketDrain, executorDrain]);
    },
    cleanup: async () => {
      if (managedScratch) {
        // The caller confirms child termination and official-call quiescence first.
        // Cache rejection too: a later caller must not report a failed cleanup as success.
        return await (managedCleanup ??= (async () => {
          sealAdmission();
          const failures: unknown[] = [];
          try {
            await lazyExecutor.dispose?.();
          } catch (error) {
            failures.push(error);
          }
          try {
            await removeScratch();
          } catch (error) {
            failures.push(error);
          }
          if (failures.length)
            throw new AggregateError(failures, 'wrapper_managed_bridge_cleanup_failed');
        })());
      }
      if (cleaned) return;
      cleaned = true;
      sealAdmission();
      try {
        await lazyExecutor.dispose?.();
      } finally {
        await rm(tempRoot, { recursive: true, force: true });
      }
    },
  };
}

function listen(server: Server, endpoint: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const onError = (): void => reject(new Error('wrapper_bridge_invalid'));
    server.once('error', onError);
    server.listen(endpoint, () => {
      server.off('error', onError);
      resolve();
    });
  });
}

async function stopAndDrain(server: Server, sockets: Set<Socket>): Promise<void> {
  if (!server.listening) {
    for (const socket of sockets) socket.destroy();
    return;
  }
  const closed = new Promise<void>((resolve) => server.close(() => resolve()));
  let timer: NodeJS.Timeout | undefined;
  const forced = new Promise<void>((resolve) => {
    timer = setTimeout(() => {
      for (const socket of sockets) socket.destroy();
      resolve();
    }, CONNECTION_DRAIN_MS);
  });
  await Promise.race([closed, forced]);
  if (timer) clearTimeout(timer);
  for (const socket of sockets) socket.destroy();
  await closed;
}
