import { mkdir } from 'node:fs/promises';

import { createArtifactBridgeServer } from './bridge-server.js';
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
  const server = createArtifactBridgeServer({
    endpoint,
    buildDiscriminator,
    executor,
  });
  server.once('error', () => {
    process.exitCode = 3;
  });
  server.listen(endpoint, () => {
    process.stdout.write('READY\n');
  });
}

void main();
