import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';

const repoRoot = process.cwd();
const localArtifactDir = mkdtempSync(path.join(tmpdir(), 'agentic-pr-review-artifacts-'));

function runSynthetic(reviewMode) {
  const env = {
    ...process.env,
    ACTIONS_RUNTIME_TOKEN: process.env.ACTIONS_RUNTIME_TOKEN ?? 'local-runtime-token',
    AGENTIC_REVIEW_LOCAL_ARTIFACT_DIR: localArtifactDir,
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: 'local/agentic-pr-review',
    GITHUB_RUN_ID: '1001',
    GITHUB_RUN_ATTEMPT: '1',
    GITHUB_SHA: 'local-head-sha',
    GITHUB_WORKSPACE: repoRoot,
    GITHUB_TOKEN: 'local-token',
    INPUT_RUNTIME_PROVIDER: 'test',
    INPUT_TARGET_MODE: 'synthetic-fixture',
    INPUT_REVIEW_MODE: reviewMode,
    INPUT_STATE_KEY: 'local-synthetic',
    INPUT_ARTIFACT_RETENTION_DAYS: '1',
  };

  const result = spawnSync('node', ['.github/actions/agentic-pr-review/dist/index.js'], {
    cwd: repoRoot,
    env,
    encoding: 'utf8',
  });

  if (result.status !== 0) {
    process.stdout.write(result.stdout);
    process.stderr.write(result.stderr);
    throw new Error(`local synthetic ${reviewMode} run failed with exit ${result.status}`);
  }
}

try {
  runSynthetic('bootstrap');
  runSynthetic('incremental');
} finally {
  rmSync(localArtifactDir, { recursive: true, force: true });
}
