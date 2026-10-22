// Only the pinned supporting historical producer uses this old six-operation
// capability. The ordinary Action does not import it or select this protocol.
import { DefaultArtifactClient } from '@actions/artifact';
import { getOctokit } from '@actions/github';
import { ArtifactBridgeStaging } from './staging.js';
import { ArtifactCacheLedger } from './historical-artifact-cache-ledger.fixture.js';
import { createArtifactActionsRestClient } from './historical-actions-rest-client.fixture.js';
import { OfficialArtifactOperations } from './historical-artifact-operations.fixture.js';
import type { ArtifactRestRequestBudget } from './historical-artifact-rest-request-budget.fixture.js';
import type { OfficialCallTracker } from '../launcher/official-calls.js';

export async function createHistoricalArtifactExecutor(
  context: {
    githubToken: string;
    repositoryName: string;
    runId: string;
    runAttempt: string;
    stagingRoot: string;
    artifactRestRequestBudget: ArtifactRestRequestBudget;
  },
  tracker: OfficialCallTracker,
) {
  const [owner, repository] = context.repositoryName.split('/');
  if (!owner || !repository || !context.githubToken)
    throw new Error('historical_fixture_context_invalid');
  const ledger = new ArtifactCacheLedger();
  return new OfficialArtifactOperations({
    owner,
    repository,
    currentRunId: context.runId,
    currentRunAttempt: context.runAttempt,
    staging: await ArtifactBridgeStaging.create(context.stagingRoot),
    cacheLedger: ledger,
    artifactRestRequestBudget: context.artifactRestRequestBudget,
    actions: tracker.wrap(
      createArtifactActionsRestClient(
        getOctokit(context.githubToken),
        context.artifactRestRequestBudget,
        undefined,
        ledger,
      ),
    ),
    artifactClient: tracker.wrap(new DefaultArtifactClient()),
  });
}
