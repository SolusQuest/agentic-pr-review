import { cleanupActionCandidate } from './launcher/action-candidate.js';

/** Secret-free post: no toolkit HTTP client, product inputs or SDK capability. */
export async function runCompositePreparationCleanup(): Promise<number> {
  try {
    const candidate = process.env['STATE_apr-candidate'];
    if (candidate) {
      const runnerTemp = process.env.RUNNER_TEMP,
        workspace = process.env.GITHUB_WORKSPACE;
      if (!runnerTemp || !workspace) throw new Error('wrapper_candidate_invalid');
      await cleanupActionCandidate(candidate, runnerTemp, workspace);
    }
    return 0;
  } catch {
    process.stdout.write('::error::Unable to confirm managed preparation cleanup.\n');
    return 1;
  }
}
