// Test-only controller. Encrypted artifact storage survives independent Hosts;
// each Host gets a new official bridge server and its unchanged replay registry.
import assert from 'node:assert/strict';
import { createInterface } from 'node:readline';
import { createArtifactBridgeServer } from '../../../src/action-wrapper/artifact-bridge/historical-bridge-server.fixture.ts';
import { ArtifactBridgeCorrelationRegistry } from '../../../src/action-wrapper/artifact-bridge/correlations.ts';
import { ArtifactBridgeStaging } from '../../../src/action-wrapper/artifact-bridge/historical-staging.fixture.js';
import { SyntheticArtifactExecutor } from '../../../src/action-wrapper/artifact-bridge/synthetic-artifact-executor.fixture.ts';

const [endpoint, buildDiscriminator, stagingRoot, controlRoot] = process.argv.slice(2);
let server;
let registry;
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
try {
  assert.ok(endpoint && buildDiscriminator && stagingRoot && controlRoot);
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, controlRoot);
  let invocation = 0;
  process.stdout.write('READY\n');
  for await (const line of input) {
    assert.ok(line.length <= 16 && /^NEXT [1-9][0-9]?$/u.test(line));
    const next = Number(line.slice(5));
    assert.equal(next, invocation + 1);
    assert.ok(next <= 32);
    if (server) {
      assert.equal(registry.activeCount, 0);
      await new Promise((resolve, reject) =>
        server.close((error) => (error ? reject(error) : resolve())),
      );
    }
    registry = new ArtifactBridgeCorrelationRegistry();
    server = createArtifactBridgeServer({
      endpoint,
      buildDiscriminator,
      executor,
      correlations: registry,
    });
    await new Promise((resolve, reject) => {
      server.once('error', reject);
      server.listen(endpoint, resolve);
    });
    invocation = next;
    process.stdout.write('INVOCATION_READY ' + invocation + '\n');
  }
} catch {
  process.stderr.write('APR_SOLUS_EVALUATION_BRIDGE_FAILED\n');
  process.exitCode = 1;
} finally {
  input.close();
  if (server) await new Promise((resolve) => server.close(resolve));
}
