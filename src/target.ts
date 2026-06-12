import {
  type ActionConfig,
  type ChangedFile,
  type PullRequestCompare,
  type ReviewTarget,
} from './types.js';
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
      body: 'Synthetic fixture for action smoke validation.',
      baseRef: 'synthetic-base',
      baseSha: 'synthetic-base-sha',
      headRef: 'synthetic-head',
      headSha: context.sha || 'synthetic-head-sha',
      draft: false,
      changedFiles: [
        {
          filename: 'synthetic-review-fixture.md',
          status: 'modified',
          additions: 4,
          deletions: 0,
          changes: 4,
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
  })) as Array<{
    filename: string;
    status: string;
    additions: number;
    deletions: number;
    changes: number;
    patch?: string;
  }>;

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
      additions: file.additions,
      deletions: file.deletions,
      changes: file.changes,
      patch,
    };
  });
  const headRepoFullName = pull.data.head.repo?.full_name;
  if (!headRepoFullName) {
    throw new Error('Pull request head repository metadata is required for same-repo validation');
  }

  return {
    mode: 'pull-request',
    prNumber,
    title: String(pull.data.title ?? `PR #${prNumber}`),
    body: String(pull.data.body ?? ''),
    baseRef: String(pull.data.base.ref),
    baseSha: String(pull.data.base.sha),
    headRef: String(pull.data.head.ref),
    headSha: String(pull.data.head.sha),
    headRepoFullName,
    draft: Boolean(pull.data.draft),
    changedFiles,
    htmlUrl: pull.data.html_url,
  };
}

export async function fetchTargetCompare(
  octokit: any,
  context: GitHubContextLike,
  baseSha: string,
  headSha: string,
  maxPatchChars: number,
): Promise<PullRequestCompare | undefined> {
  const { owner, repo } = context.repo;
  try {
    const response = await octokit.rest.repos.compareCommitsWithBasehead({
      owner,
      repo,
      basehead: `${baseSha}...${headSha}`,
    });
    let remainingPatchChars = maxPatchChars;
    const changedFiles = (response.data.files ?? []).map((file: any) => {
      const patch = file.patch
        ? truncateText(String(file.patch), Math.max(0, remainingPatchChars))
        : undefined;
      if (patch) {
        remainingPatchChars = Math.max(0, remainingPatchChars - patch.length);
      }
      return {
        filename: String(file.filename),
        status: String(file.status),
        additions: Number(file.additions ?? 0),
        deletions: Number(file.deletions ?? 0),
        changes: Number(file.changes ?? 0),
        patch,
      };
    });
    return {
      baseSha,
      headSha,
      htmlUrl: String(response.data.html_url),
      status: String(response.data.status),
      aheadBy: Number(response.data.ahead_by),
      behindBy: Number(response.data.behind_by),
      changedFiles,
    };
  } catch (error: any) {
    if (error?.status === 404) {
      return undefined;
    }
    throw error;
  }
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
