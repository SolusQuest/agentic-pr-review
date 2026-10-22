import { mkdir } from 'node:fs/promises';
import { closeSync, constants, openSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { createArtifactBridgeServer } from './historical-bridge-server.fixture.js';
import { ArtifactBridgeCorrelationRegistry } from './correlations.js';
import { ArtifactBridgeStaging } from './historical-staging.fixture.js';
import { SyntheticArtifactExecutor } from './synthetic-artifact-executor.fixture.js';
import { SyntheticHostBridgeLifetime } from './synthetic-bridge-lifetime.fixture.js';
import type { Server } from 'node:net';

async function main(): Promise<void> {
  const [endpoint, buildDiscriminator, stagingRoot, controlRoot, lifetimeMode] =
    process.argv.slice(2);
  if (
    !endpoint ||
    !buildDiscriminator ||
    !stagingRoot ||
    !controlRoot ||
    (lifetimeMode !== undefined && lifetimeMode !== '--per-host-bridge') ||
    (lifetimeMode === '--per-host-bridge' && !process.send)
  ) {
    process.exitCode = 2;
    return;
  }
  await mkdir(controlRoot, { recursive: true });
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, controlRoot);
  const observe = (server: Server, correlations: ArtifactBridgeCorrelationRegistry) => {
    server.once('error', () => {
      process.exitCode = 3;
    });
    server.on('connection', (stream) => {
      stream.once('close', () => {
        let descriptor: number | undefined;
        try {
          descriptor = openSync(
            join(controlRoot, 'correlation-observation.json'),
            constants.O_WRONLY | constants.O_CREAT | constants.O_TRUNC | constants.O_NOFOLLOW,
            0o600,
          );
          writeFileSync(
            descriptor,
            JSON.stringify({
              active: correlations.activeCount,
              terminal: correlations.terminalCount,
            }),
          );
        } catch {
          // Diagnostic absence never changes the actual bridge result.
        } finally {
          if (descriptor !== undefined) {
            try {
              closeSync(descriptor);
            } catch {
              /* Diagnostic cleanup only. */
            }
          }
        }
      });
    });
  };
  if (lifetimeMode === '--per-host-bridge') {
    const lifetime = new SyntheticHostBridgeLifetime(
      { endpoint, buildDiscriminator, executor },
      observe,
    );
    try {
      await lifetime.start();
    } catch {
      process.exitCode = 3;
      return;
    }
    process.on('message', (message: unknown) => {
      void lifetime.nextHost(message).then(
        () => process.send!('ready'),
        () => {
          process.exitCode = 4;
          process.send!('failed');
        },
      );
    });
    process.once('disconnect', () => {
      void lifetime.close().catch(() => {
        process.exitCode = 4;
      });
    });
    process.stdout.write('READY\n');
    return;
  }
  const correlations = new ArtifactBridgeCorrelationRegistry();
  const server = createArtifactBridgeServer({
    endpoint,
    buildDiscriminator,
    executor,
    correlations,
  });
  observe(server, correlations);
  server.listen(endpoint, () => {
    process.stdout.write('READY\n');
  });
}

void main();
