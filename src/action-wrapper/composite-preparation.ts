import * as core from '@actions/core';
import { fileURLToPath } from 'node:url';
import { prepareActionCandidate, cleanupActionCandidate } from './launcher/action-candidate.js';
import { RuntimePrerequisiteTerminationUnconfirmedError } from './launcher/runtime-prerequisite.js';

function required(value: string | undefined): string {
  if (!value) throw new Error('wrapper_candidate_invalid');
  return value;
}

/** Actual Node preparation Action: no product inputs or SDK execution. */
export async function runCompositePreparation(actionRoot: URL, build: string): Promise<number> {
  const cancellation = new AbortController();
  const abort = () => cancellation.abort();
  process.on('SIGINT', abort);
  process.on('SIGTERM', abort);
  const dispose = () => {
    process.off('SIGINT', abort);
    process.off('SIGTERM', abort);
  };
  let retained: Promise<void> | undefined;
  try {
    const candidate = await prepareActionCandidate(
      {
        actionRoot: fileURLToPath(actionRoot),
        build,
        runId: required(process.env.GITHUB_RUN_ID),
        runAttempt: required(process.env.GITHUB_RUN_ATTEMPT),
        runnerTemp: required(process.env.RUNNER_TEMP),
        workspace: required(process.env.GITHUB_WORKSPACE),
        signal: cancellation.signal,
      },
      (ownership) => core.saveState('apr-candidate', ownership),
    );
    core.setOutput('candidate', candidate);
    return 0;
  } catch (error) {
    if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError)
      retained = error.resourcesReleased;
    core.setFailed('Unable to prepare the managed review payload.');
    return 1;
  } finally {
    if (retained) void retained.then(dispose, dispose);
    else dispose();
  }
}

/** Always post has no product inputs; claimed/uncertain launch resources stay retained. */
export async function runCompositePreparationCleanup(): Promise<number> {
  try {
    const candidate = core.getState('apr-candidate');
    if (candidate)
      await cleanupActionCandidate(
        candidate,
        required(process.env.RUNNER_TEMP),
        required(process.env.GITHUB_WORKSPACE),
      );
    return 0;
  } catch {
    core.setFailed('Unable to confirm managed preparation cleanup.');
    return 1;
  }
}
