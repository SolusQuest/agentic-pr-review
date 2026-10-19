// Supporting historical producer uses the same official SDK bridge, with the
// isolated external service owning original ZIP bytes and producing-run facts.
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { createInterface } from 'node:readline';
import { createProductionArtifactExecutor } from '../../../../src/action-wrapper/index.ts';
import { startArtifactBridgeRuntime } from '../../../../src/action-wrapper/launcher/bridge-runtime.ts';
import { OfficialCallTracker } from '../../../../src/action-wrapper/launcher/official-calls.ts';
import { ArtifactRestRequestBudget } from '../../../../src/action-wrapper/artifact-bridge/artifact-rest-request-budget.ts';

let bridge;
try {
  const launch = JSON.parse(await readFile(process.argv[2], 'utf8'));
  const tracker = new OfficialCallTracker();
  const budget = ArtifactRestRequestBudget.forVerifiedPreparedPayload({
    buildDiscriminator: launch.build_discriminator,
  });
  bridge = await startArtifactBridgeRuntime({
    buildDiscriminator: launch.build_discriminator,
    executorFactory: (stagingRoot) =>
      createProductionArtifactExecutor(
        {
          githubToken: launch.inputs.github_token,
          repositoryName: launch.repository_name,
          runId: launch.run_id,
          runAttempt: launch.run_attempt,
          stagingRoot,
          verifiedPreparedPayload: { buildDiscriminator: launch.build_discriminator },
          artifactRestRequestBudget: budget,
        },
        tracker,
      ),
  });
  process.stdout.write('R6_BRIDGE_READY ' + JSON.stringify({ endpoint: bridge.endpoint }) + '\n');
  const lines = createInterface({ input: process.stdin });
  for await (const line of lines) {
    assert.equal(line, 'STOP');
    await bridge.stopAndDrain();
    assert.equal(await tracker.awaitQuiescence(30_000), true);
    await bridge.cleanup();
    bridge = undefined;
    process.stdout.write('R6_BRIDGE_STOPPED\n');
    break;
  }
} catch {
  process.stderr.write('APR_R6_SUPPORTING_BRIDGE_FAILED\n');
  process.exitCode = 1;
} finally {
  if (bridge) {
    await bridge.stopAndDrain();
    await bridge.cleanup();
  }
}
