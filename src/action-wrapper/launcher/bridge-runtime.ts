import { chmod, mkdir, mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import type { Server, Socket } from 'node:net';

import {
  createArtifactBridgeServer,
  type ArtifactBridgeExecutor,
} from '../artifact-bridge/index.js';
import { fail } from './validation.js';

const SOCKET_PATH_LIMIT = 100;
const CONNECTION_DRAIN_MS = 2_000;

export interface ArtifactBridgeRuntime {
  readonly endpoint: string;
  readonly stagingRoot: string;
  readonly tempRoot: string;
  stopAndDrain(): Promise<void>;
  cleanup(): Promise<void>;
}

export async function startArtifactBridgeRuntime(input: {
  readonly buildDiscriminator: string;
  readonly executorFactory: (stagingRoot: string) => Promise<ArtifactBridgeExecutor>;
}): Promise<ArtifactBridgeRuntime> {
  if (process.platform !== 'linux') fail('wrapper_platform_unsupported');
  const tempRoot = await mkdtemp(path.join(tmpdir(), 'apr-w1-'));
  await chmod(tempRoot, 0o700);
  const endpoint = path.join(tempRoot, 'bridge.sock');
  if (Buffer.byteLength(endpoint, 'utf8') > SOCKET_PATH_LIMIT) {
    await rm(tempRoot, { recursive: true, force: true });
    fail('wrapper_bridge_invalid');
  }
  const stagingRoot = path.join(tempRoot, 'artifact-staging');
  try {
    await mkdir(stagingRoot, { mode: 0o700 });
    await chmod(stagingRoot, 0o700);
  } catch {
    await rm(tempRoot, { recursive: true, force: true });
    fail('wrapper_bridge_invalid');
  }
  let executorPromise: Promise<ArtifactBridgeExecutor> | undefined;
  const lazyExecutor: ArtifactBridgeExecutor = {
    execute: async (command, signal) => {
      executorPromise ??= input.executorFactory(stagingRoot);
      return await (await executorPromise).execute(command, signal);
    },
  };
  const server = createArtifactBridgeServer({
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
    await rm(tempRoot, { recursive: true, force: true });
    fail('wrapper_bridge_invalid');
  }
  let stopped = false;
  let cleaned = false;
  return {
    endpoint,
    stagingRoot,
    tempRoot,
    stopAndDrain: async () => {
      if (stopped) return;
      stopped = true;
      await stopAndDrain(server, sockets);
    },
    cleanup: async () => {
      if (cleaned) return;
      cleaned = true;
      await rm(tempRoot, { recursive: true, force: true });
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
