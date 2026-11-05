import { delimiter, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalJson } from './build-payload.format.mjs';
import * as staging from './stage-candidate.mjs';
import * as promotion from './promote-candidate.mjs';

export async function main(env = process.env) {
  const phase = env.INPUT_PHASE;
  if (
    !(
      (phase === 'stage' &&
        ['stage', 'recover-stage', 'complete-stage', 'finish-stage'].includes(
          env.RELEASE_OPERATION,
        )) ||
      (phase === 'promote' && ['promote', 'recover-promote'].includes(env.RELEASE_OPERATION))
    )
  )
    throw new Error('release_action_denied');

  // SDK children inherit this private copy; gh inherits the scrubbed ambient env.
  const operationEnv = { ...env };
  delete process.env.ACTIONS_RUNTIME_TOKEN;
  delete process.env.ACTIONS_RESULTS_URL;
  const prefix = phase === 'stage' ? 'r7-p4' : 'r7-p5';
  operationEnv.PATH = join(env.RUNNER_TEMP, prefix + '-gh') + delimiter + (env.PATH ?? '');
  process.env.PATH = operationEnv.PATH;
  const args = [
    phase === 'stage'
      ? env.RELEASE_OPERATION === 'finish-stage'
        ? 'workflow-finish-stage'
        : env.RELEASE_OPERATION === 'complete-stage'
          ? 'workflow-complete-stage'
          : 'workflow-stage'
      : 'workflow-promote',
    '--source',
    env.INPUT_SOURCE,
    '--version',
    env.INPUT_VERSION,
    '--output',
    join(env.RUNNER_TEMP, prefix + (phase === 'stage' ? '-staged' : '-published')),
  ];
  return (phase === 'stage' ? staging : promotion).main(args, operationEnv);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((result) => process.stdout.write(canonicalJson(result)))
    .catch((error) => {
      process.stderr.write(
        process.env.INPUT_PHASE === 'stage'
          ? staging.stagingDiagnostic(error)
          : process.env.INPUT_PHASE === 'promote'
            ? 'candidate_promotion_denied\n'
            : 'candidate_release_denied\n',
      );
      process.exitCode = 1;
    });
}
