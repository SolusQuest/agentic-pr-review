import { startArtifactBridgeRuntime as startCurrentRuntime } from './bridge-runtime.js';
import { createArtifactBridgeServer } from '../artifact-bridge/historical-bridge-server.fixture.js';
import type { ArtifactBridgeExecutor } from '../artifact-bridge/historical-artifact-operations.fixture.js';
export type { ArtifactBridgeRuntime } from './bridge-runtime.js';

type CurrentInput = Parameters<typeof startCurrentRuntime>[0];
type HistoricalInput = Omit<CurrentInput, 'executorFactory' | 'historicalServerFactory'> & {
  readonly executorFactory: (root: string) => Promise<ArtifactBridgeExecutor>;
};

// Only this named fixture adapts the old six-operation structural types. The
// actual server and executor below agree on the frozen historical wire.
export async function startArtifactBridgeRuntime(input: HistoricalInput) {
  return startCurrentRuntime({
    ...input,
    executorFactory: input.executorFactory as CurrentInput['executorFactory'],
    historicalServerFactory: createArtifactBridgeServer as NonNullable<
      CurrentInput['historicalServerFactory']
    >,
  });
}
