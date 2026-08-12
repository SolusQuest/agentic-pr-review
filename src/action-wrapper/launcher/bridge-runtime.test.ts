import net from 'node:net';
import { rm, stat } from 'node:fs/promises';
import { afterEach, describe, expect, it } from 'vitest';

import { encodeCommandMessage } from '../artifact-bridge/framing.js';
import { ArtifactBridgeStaging } from '../artifact-bridge/staging.js';
import { startArtifactBridgeRuntime, type ArtifactBridgeRuntime } from './bridge-runtime.js';

let runtime: ArtifactBridgeRuntime | undefined;

afterEach(async () => {
  if (!runtime) return;
  await runtime.stopAndDrain().catch(() => undefined);
  await runtime.cleanup().catch(() => undefined);
  runtime = undefined;
});

describe.runIf(process.platform === 'linux')('W1 S2 bridge composition', () => {
  it('listens before use, constructs S2 lazily once, and returns opaque results', async () => {
    let factories = 0;
    let executions = 0;
    runtime = await startArtifactBridgeRuntime({
      buildDiscriminator: 'r4-h1',
      executorFactory: async () => {
        factories += 1;
        return {
          execute: async (command) => {
            executions += 1;
            return {
              operation: 'list_exact',
              correlation_id: command.correlation_id,
              failure: 'none',
              complete: true,
              objects: [],
            };
          },
        };
      },
    });
    expect(factories).toBe(0);
    const stagingStat = await stat(runtime.stagingRoot);
    expect(stagingStat.isDirectory()).toBe(true);
    expect(stagingStat.mode & 0o777).toBe(0o700);
    await expect(ArtifactBridgeStaging.create(runtime.stagingRoot)).resolves.toBeInstanceOf(
      ArtifactBridgeStaging,
    );
    const socket = net.createConnection(runtime.endpoint);
    await new Promise<void>((resolve, reject) => {
      socket.once('connect', resolve);
      socket.once('error', reject);
    });
    const response = collect(socket);
    socket.end(
      encodeCommandMessage({
        build_discriminator: 'r4-h1',
        payload: {
          operation: 'list_exact',
          correlation_id: 'w1-correlation',
          name: 'opaque-name',
          maximum_objects: '8',
        },
      }),
    );
    const bytes = await response;
    const length = bytes.readUInt32BE(0);
    const result = JSON.parse(bytes.subarray(4, 4 + length).toString('utf8')) as {
      readonly payload: { readonly failure: string };
    };
    expect(result.payload.failure).toBe('none');
    expect(factories).toBe(1);
    expect(executions).toBe(1);
    await runtime.stopAndDrain();
    await runtime.cleanup();
    await expect(rm(runtime.tempRoot)).rejects.toThrow();
    runtime = undefined;
  });
});

function collect(socket: net.Socket): Promise<Buffer> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    socket.on('data', (chunk) => chunks.push(Buffer.from(chunk)));
    socket.once('end', () => resolve(Buffer.concat(chunks)));
    socket.once('error', reject);
  });
}
