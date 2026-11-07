import * as core from '@actions/core';
import { fileURLToPath } from 'node:url';
import { prepareActionCandidate } from './launcher/action-candidate.js';
import { RuntimePrerequisiteTerminationUnconfirmedError } from './launcher/runtime-prerequisite.js';

const preparationCodes = new Set([
  'wrapper_candidate_invalid',
  'wrapper_platform_unsupported',
  'wrapper_payload_invalid',
  'wrapper_payload_map_invalid',
  'wrapper_payload_map_unbound',
  'wrapper_action_source_invalid',
  'wrapper_payload_cancelled',
  'wrapper_release_payload_invalid',
  'wrapper_managed_tree_invalid',
  'wrapper_managed_scratch_creation_failed',
  'wrapper_managed_scratch_cleanup_failed',
  'wrapper_managed_runtime_prerequisite',
]);

function preparationCode(error: unknown): string {
  // Never format an exception, path, cause or user-defined getter. Even known
  // error classes may carry private transport/filesystem details.
  try {
    if (error !== null && typeof error === 'object') {
      const message = Object.getOwnPropertyDescriptor(error, 'message')?.value;
      if (typeof message === 'string' && preparationCodes.has(message)) return message;
    }
  } catch {
    // A hostile thrown Proxy cannot turn diagnostics into another failure.
  }
  return 'unclassified';
}

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
  let stage: 'environment' | 'candidate_preparation' | 'candidate_output' = 'environment';
  try {
    const context = {
      actionRoot: fileURLToPath(actionRoot),
      build,
      runId: required(process.env.GITHUB_RUN_ID),
      runAttempt: required(process.env.GITHUB_RUN_ATTEMPT),
      runnerTemp: required(process.env.RUNNER_TEMP),
      workspace: required(process.env.GITHUB_WORKSPACE),
      signal: cancellation.signal,
    };
    stage = 'candidate_preparation';
    const candidate = await prepareActionCandidate(context, (ownership) =>
      core.saveState('apr-candidate', ownership),
    );
    stage = 'candidate_output';
    core.setOutput('candidate', candidate);
    return 0;
  } catch (error) {
    if (error instanceof RuntimePrerequisiteTerminationUnconfirmedError)
      retained = error.resourcesReleased;
    try {
      core.error(
        'APR_PREPARATION_DIAGNOSTIC ' +
          JSON.stringify({
            version: 1,
            stage,
            code: retained ? 'runtime_termination_unconfirmed' : preparationCode(error),
          }),
      );
    } catch {
      // Reporting never replaces the original failure or its retained lifetime.
    }
    core.setFailed('Unable to prepare the managed review payload.');
    return 1;
  } finally {
    if (retained) void retained.then(dispose, dispose);
    else dispose();
  }
}
