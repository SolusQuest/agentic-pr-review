import { once } from 'node:events';
import { rm } from 'node:fs/promises';
import { createArtifactBridgeServer } from '../../../src/action-wrapper/artifact-bridge/historical-bridge-server.fixture.js';
import { ArtifactBridgeCorrelationRegistry } from '../../../src/action-wrapper/artifact-bridge/correlations.js';
import { ARTIFACT_BRIDGE_LIMITS } from '../../../src/action-wrapper/artifact-bridge/limits.js';
import { ArtifactBridgeStaging } from '../../../src/action-wrapper/artifact-bridge/historical-staging.fixture.js';
import { SyntheticArtifactExecutor } from '../../../src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.js';

// One case retains one executor's encrypted object Map and artifact-ID sequence.
// Each independent .NET Host receives a new official server/registry. No limit
// changes and no reset of an active operation are permitted.
async function main() {
  const [endpoint, buildDiscriminator, stagingRoot, controlRoot] = process.argv.slice(2);
  if (!endpoint || !buildDiscriminator || !stagingRoot || !controlRoot || !process.send)
    throw new Error('controller_arguments');
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, controlRoot);
  let server;
  let registry;
  let epoch = 0;
  let pending = Promise.resolve();
  const counts = () => {
    if (!registry) throw new Error('controller_missing_registry');
    return {
      epoch,
      active: registry.activeCount,
      terminal: registry.terminalCount,
      terminal_limit: ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations,
    };
  };
  async function handle(message) {
    if (!Number.isSafeInteger(message?.request) || message.request < 1)
      throw new Error('controller_request');
    let result;
    if (message.operation === 'start') {
      if (server || registry) throw new Error('controller_already_started');
      registry = new ArtifactBridgeCorrelationRegistry();
      server = createArtifactBridgeServer({
        endpoint,
        buildDiscriminator,
        executor,
        correlations: registry,
      });
      server.listen(endpoint);
      await once(server, 'listening');
      epoch++;
      result = counts();
    } else if (message.operation === 'close') {
      if (!server || !registry) throw new Error('controller_not_started');
      const deadline = Date.now() + 5000;
      while (registry.activeCount !== 0) {
        if (Date.now() >= deadline) throw new Error('controller_not_idle');
        await new Promise((resolve) => setTimeout(resolve, 10));
      }
      await new Promise((resolve, reject) =>
        server.close((error) => (error ? reject(error) : resolve())),
      );
      result = counts();
      if (result.active !== 0) throw new Error('controller_close_not_idle');
      server = undefined;
      registry = undefined;
      await rm(endpoint, { force: true });
    } else throw new Error('controller_operation');
    process.send({ request: message.request, ok: true, ...result });
  }
  process.on('message', (message) => {
    pending = pending
      .then(() => handle(message))
      .catch(() => {
        process.send({
          request: message?.request,
          ok: false,
          code: 'controller_failed',
          state: registry ? counts() : null,
        });
        process.exitCode = 1;
      });
  });
  process.stdout.write('READY\n');
}

void main().catch(() => {
  process.stderr.write('APR_V3_BRIDGE_CONTROLLER_FAILED\n');
  process.exitCode = 1;
});
