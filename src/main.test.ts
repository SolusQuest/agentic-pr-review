import { mkdir, mkdtemp, rm, stat } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { stateArtifactName } from './state.js';

const mocks = vi.hoisted(() => {
  const inputs: Record<string, string> = {};
  const summary = {
    addRaw: vi.fn(),
    write: vi.fn(async () => undefined),
  };
  summary.addRaw.mockReturnValue(summary);
  const listFiles = vi.fn();
  const listComments = vi.fn();
  const createComment = vi.fn();
  const updateComment = vi.fn();
  const octokit = {
    paginate: vi.fn(),
    rest: {
      pulls: {
        get: vi.fn(),
        listFiles,
      },
      issues: {
        listComments,
        createComment,
        updateComment,
      },
      repos: {
        compareCommitsWithBasehead: vi.fn(),
      },
    },
  };
  return {
    inputs,
    summary,
    octokit,
    listFiles,
    listComments,
    createComment,
    updateComment,
    getInput: vi.fn((name: string) => inputs[name] ?? ''),
    setOutput: vi.fn(),
    setSecret: vi.fn(),
    setFailed: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
    getOctokit: vi.fn(() => octokit),
  };
});

vi.mock('@actions/core', () => ({
  getInput: mocks.getInput,
  setOutput: mocks.setOutput,
  setSecret: mocks.setSecret,
  setFailed: mocks.setFailed,
  warning: mocks.warning,
  info: mocks.info,
  summary: mocks.summary,
}));

vi.mock('@actions/github', () => ({
  context: {
    eventName: 'workflow_dispatch',
    repo: { owner: 'example', repo: 'repo' },
    payload: {},
    sha: 'head-sha',
    runId: 123,
    runAttempt: 1,
  },
  getOctokit: mocks.getOctokit,
}));

describe('run', () => {
  const originalEnv = { ...process.env };
  let root: string;
  let workspace: string;
  let tempRoot: string;
  let artifactRoot: string;

  beforeEach(async () => {
    vi.clearAllMocks();
    for (const key of Object.keys(mocks.inputs)) {
      delete mocks.inputs[key];
    }
    root = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-main-'));
    workspace = path.join(root, 'workspace');
    tempRoot = path.join(root, 'runner-temp');
    artifactRoot = path.join(root, 'artifacts');
    await mkdir(workspace, { recursive: true });
    process.env = {
      ...originalEnv,
      GITHUB_TOKEN: 'github-token',
      GITHUB_WORKSPACE: workspace,
      RUNNER_TEMP: tempRoot,
      AGENTIC_REVIEW_LOCAL_ARTIFACT_DIR: artifactRoot,
    };
    Object.assign(mocks.inputs, {
      runtime_provider: 'test',
      target_mode: 'pull-request',
      review_mode: 'bootstrap',
      pr_number: '1',
      state_key: 'comment-fail',
      post_comment: 'true',
      artifact_retention_days: '7',
      test_runtime_fixture: 'valid',
    });
    mocks.octokit.rest.pulls.get.mockResolvedValue({
      data: {
        title: 'Synthetic PR',
        body: 'Synthetic body',
        base: { ref: 'main', sha: 'base-sha' },
        head: {
          ref: 'branch',
          sha: 'head-sha',
          repo: { full_name: 'example/repo' },
        },
        draft: false,
        html_url: 'https://github.com/example/repo/pull/1',
      },
    });
    mocks.octokit.paginate.mockImplementation(async (endpoint: unknown) => {
      if (endpoint === mocks.listFiles) {
        return [
          {
            filename: 'src/file.ts',
            status: 'modified',
            additions: 1,
            deletions: 0,
            changes: 1,
            patch: '@@ -1 +1 @@\n+change',
          },
        ];
      }
      return [];
    });
    mocks.createComment.mockRejectedValue(new Error('comment denied'));
  });

  afterEach(async () => {
    process.env = originalEnv;
    await rm(root, { recursive: true, force: true });
  });

  it('does not upload restorable state when required sticky comment posting fails', async () => {
    const { run } = await import('./main.js');

    await expect(run()).rejects.toThrow(/comment denied/);

    expect(mocks.createComment).toHaveBeenCalledTimes(1);
    await expect(
      stat(path.join(artifactRoot, stateArtifactName('comment-fail'))),
    ).rejects.toThrow();
    expect(mocks.setOutput).not.toHaveBeenCalledWith('reviewed_head_sha', 'head-sha');
  });
});
