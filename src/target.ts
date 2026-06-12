import { type ActionConfig, type ChangedFile, type ReviewTarget } from './types.js';
import { truncateText } from './utils.js';

export interface GitHubContextLike {
  repo: { owner: string; repo: string };
  payload: { pull_request?: { number?: number } };
  sha: string;
}

export async function resolveTarget(
  config: ActionConfig,
  octokit: any,
  context: GitHubContextLike,
): Promise<ReviewTarget> {
  if (config.targetMode === 'synthetic-fixture') {
    return {
      mode: 'synthetic-fixture',
      title: 'Synthetic agentic PR review fixture',
      baseSha: 'synthetic-base-sha',
      headSha: context.sha || 'synthetic-head-sha',
      changedFiles: [
        {
          filename: 'synthetic-review-fixture.md',
          status: 'modified',
          patch: [
            '@@ -1,3 +1,7 @@',
            ' # Synthetic fixture',
            '+',
            '+This deterministic fixture validates action wiring, prompt construction,',
            '+runtime execution, state artifact upload, and state restore behavior.',
          ].join('\n'),
        },
      ],
    };
  }

  const prNumber = config.prNumber ?? context.payload.pull_request?.number;
  if (!prNumber) {
    throw new Error('pr_number is required unless the event payload contains a pull request');
  }

  const { owner, repo } = context.repo;
  const pull = await octokit.rest.pulls.get({ owner, repo, pull_number: prNumber });
  const files = (await octokit.paginate(octokit.rest.pulls.listFiles, {
    owner,
    repo,
    pull_number: prNumber,
    per_page: 100,
  })) as Array<{ filename: string; status: string; patch?: string }>;

  let remainingPatchChars = config.maxPatchChars;
  const changedFiles: ChangedFile[] = files.map((file) => {
    const patch = file.patch
      ? truncateText(file.patch, Math.max(0, remainingPatchChars))
      : undefined;
    if (patch) {
      remainingPatchChars = Math.max(0, remainingPatchChars - patch.length);
    }
    return {
      filename: file.filename,
      status: file.status,
      patch,
    };
  });

  return {
    mode: 'pull-request',
    prNumber,
    title: String(pull.data.title ?? `PR #${prNumber}`),
    baseSha: String(pull.data.base.sha),
    headSha: String(pull.data.head.sha),
    changedFiles,
    htmlUrl: pull.data.html_url,
  };
}

export function deriveStateKey(config: ActionConfig, target: ReviewTarget): string {
  if (config.stateKey) {
    return config.stateKey;
  }
  if (target.mode === 'pull-request') {
    return `pr-${target.prNumber}-${config.runtimeProvider}`;
  }
  return `synthetic-${config.runtimeProvider}`;
}
