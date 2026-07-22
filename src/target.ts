import {
  type ActionConfig,
  type ChangedFile,
  type PullRequestDiffSnapshotDeltaV1,
  type PullRequestDiffSnapshotEntryV1,
  type PullRequestDiffSnapshotV1,
  type ReviewTarget,
} from './types.js';
import { normalizeRepoRelativePath, sha256 } from './utils.js';

export interface GitHubContextLike {
  repo: { owner: string; repo: string };
  payload: {
    pull_request?: { number?: number };
    workflow_run?: { pull_requests?: { number?: number }[] };
    inputs?: { pr_number?: string };
  };
  sha: string;
}

export interface PullRequestFileData {
  sha?: string;
  filename: string;
  previous_filename?: string;
  status: string;
  additions: number;
  deletions: number;
  changes: number;
  patch?: string;
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

  const workflowRunPullRequests = context.payload.workflow_run?.pull_requests;
  const prNumber =
    config.prNumber ??
    context.payload.pull_request?.number ??
    (workflowRunPullRequests?.length === 1 ? workflowRunPullRequests[0]?.number : undefined) ??
    parseDispatchPullRequestNumber(context.payload.inputs?.pr_number);
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
  })) as PullRequestFileData[];

  const changedFiles = changedFilesFromPullRequestFiles(files);
  const pullRequestDiffSnapshot = buildPullRequestDiffSnapshot({
    baseSha: String(pull.data.base.sha),
    headSha: String(pull.data.head.sha),
    files,
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
    pullRequestDiffSnapshot,
    htmlUrl: pull.data.html_url,
  };
}

function parseDispatchPullRequestNumber(value: string | undefined): number | undefined {
  return value && /^[1-9][0-9]*$/.test(value) ? Number(value) : undefined;
}

export function buildPullRequestDiffSnapshot(input: {
  baseSha: string;
  headSha: string;
  files: PullRequestFileData[];
}): PullRequestDiffSnapshotV1 {
  return {
    version: 1,
    source: 'github-pulls-list-files',
    baseSha: input.baseSha,
    headSha: input.headSha,
    files: input.files.map(snapshotEntryFromPullRequestFile),
  };
}

export function changedFilesFromPullRequestFiles(files: PullRequestFileData[]): ChangedFile[] {
  return files.map((file) => {
    const patch = typeof file.patch === 'string' ? file.patch : undefined;
    return {
      filename: normalizeRepoRelativePath(String(file.filename)),
      previousFilename: file.previous_filename
        ? normalizeRepoRelativePath(String(file.previous_filename))
        : undefined,
      status: String(file.status),
      additions: Number(file.additions ?? 0),
      deletions: Number(file.deletions ?? 0),
      changes: Number(file.changes ?? 0),
      patch,
    };
  });
}

export function diffPullRequestDiffSnapshots(
  previous: PullRequestDiffSnapshotV1,
  current: PullRequestDiffSnapshotV1,
  currentFiles: ChangedFile[],
): PullRequestDiffSnapshotDeltaV1 {
  const previousByPath = new Map(previous.files.map((entry) => [entry.filename, entry]));
  const currentByPath = new Map(current.files.map((entry) => [entry.filename, entry]));
  const currentPatchByPath = new Map(currentFiles.map((file) => [file.filename, file.patch]));
  const changedEntries: PullRequestDiffSnapshotDeltaV1['changedEntries'] = [];
  let unchangedCount = 0;

  for (const currentEntry of current.files) {
    const previousEntry = previousByPath.get(currentEntry.filename);
    if (!previousEntry) {
      changedEntries.push({
        kind: 'current_changed',
        reason: 'new_file',
        current: currentEntry,
        patch: currentPatchByPath.get(currentEntry.filename),
      });
      continue;
    }
    if (snapshotEntryChanged(previousEntry, currentEntry)) {
      changedEntries.push({
        kind: 'current_changed',
        reason: 'metadata_changed',
        current: currentEntry,
        previous: previousEntry,
        patch: currentPatchByPath.get(currentEntry.filename),
      });
    } else {
      unchangedCount += 1;
    }
  }

  const removedEntries = previous.files
    .filter((entry) => !currentByPath.has(entry.filename))
    .map((entry) => ({ kind: 'removed_from_pr_diff' as const, previous: entry }));

  return {
    version: 1,
    source: 'github-pulls-list-files',
    changedEntries,
    removedEntries,
    unchangedCount,
  };
}

export function pullRequestDiffSnapshotsEquivalent(
  previous: PullRequestDiffSnapshotV1,
  current: PullRequestDiffSnapshotV1,
): boolean {
  return (
    previous.files.length === current.files.length &&
    diffPullRequestDiffSnapshots(previous, current, []).changedEntries.length === 0 &&
    diffPullRequestDiffSnapshots(previous, current, []).removedEntries.length === 0
  );
}

function snapshotEntryFromPullRequestFile(
  file: PullRequestFileData,
): PullRequestDiffSnapshotEntryV1 {
  const patchAvailable = typeof file.patch === 'string';
  const fileSha = typeof file.sha === 'string' && file.sha.trim() ? file.sha.trim() : undefined;
  return {
    filename: normalizeRepoRelativePath(String(file.filename)),
    previousFilename: file.previous_filename
      ? normalizeRepoRelativePath(String(file.previous_filename))
      : undefined,
    status: String(file.status),
    additions: Number(file.additions ?? 0),
    deletions: Number(file.deletions ?? 0),
    changes: Number(file.changes ?? 0),
    fileSha,
    patchSha256: patchAvailable ? sha256(String(file.patch)) : null,
    patchAvailable,
  };
}

function snapshotEntryChanged(
  previous: PullRequestDiffSnapshotEntryV1,
  current: PullRequestDiffSnapshotEntryV1,
): boolean {
  if (previous.fileSha || current.fileSha) {
    if (previous.fileSha !== current.fileSha) {
      return true;
    }
  } else if (!previous.patchAvailable || !current.patchAvailable) {
    return true;
  }
  return (
    previous.status !== current.status ||
    previous.additions !== current.additions ||
    previous.deletions !== current.deletions ||
    previous.changes !== current.changes ||
    previous.patchAvailable !== current.patchAvailable ||
    previous.patchSha256 !== current.patchSha256
  );
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
