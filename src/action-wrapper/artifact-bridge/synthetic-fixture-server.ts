import { mkdir } from 'node:fs/promises';
import { constants, writeFileSync } from 'node:fs';
import { join } from 'node:path';

import { createArtifactBridgeServer } from './bridge-server.js';
import { ArtifactBridgeCorrelationRegistry } from './correlations.js';
import { ArtifactBridgeStaging } from './staging.js';
import { SyntheticArtifactExecutor } from './synthetic-artifact-executor.fixture.js';

async function main(): Promise<void> {
  const [endpoint, buildDiscriminator, stagingRoot, controlRoot] = process.argv.slice(2);
  if (!endpoint || !buildDiscriminator || !stagingRoot || !controlRoot) {
    process.exitCode = 2;
    return;
  }
  await mkdir(controlRoot, { recursive: true });
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, controlRoot);
  const correlations = new ArtifactBridgeCorrelationRegistry();
  const server = createArtifactBridgeServer({
    endpoint,
    buildDiscriminator,
    executor,
    correlations,
  });
  server.on('connection', (stream) => {
    stream.once('close', () => {
      try {
        writeFileSync(
          join(controlRoot, 'correlation-observation.json'),
          JSON.stringify({
            active: correlations.activeCount,
            terminal: correlations.terminalCount,
          }),
          {
            mode: 0o600,
            flag: constants.O_WRONLY | constants.O_CREAT | constants.O_TRUNC | constants.O_NOFOLLOW,
          },
        );
      } catch {
        // Diagnostic absence never changes the actual bridge result.
      }
    });
  });
  server.once('error', () => {
    process.exitCode = 3;
  });
  server.listen(endpoint, () => {
    process.stdout.write('READY\n');
  });
}

void main();
