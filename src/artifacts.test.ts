import { describe, expect, it } from 'vitest';
import { GitHubArtifactStore } from './artifacts.js';

function run(overrides: Record<string, unknown> = {}) {
  return {
    id: 99,
    workflow_id: 7,
    path: '.github/workflows/review.yml',
    event: 'pull_request',
    conclusion: 'success',
    head_sha: 'current-head',
    head_repository: { full_name: 'SolusQuest/agentic-pr-review' },
    pull_requests: [{ number: 34 }],
    ...overrides,
  };
}

describe('GitHubArtifactStore provenance filtering', () => {
  it('rejects newer artifacts from another workflow before choosing an older trusted artifact', async () => {
    const runs = new Map<number, unknown>([
      [99, run()],
      [10, run({ id: 10, head_sha: 'old-head' })],
      [11, run({ id: 11, workflow_id: 88, head_sha: 'attacker-head' })],
    ]);
    const octokit = {
      request: async (route: string, params: { run_id?: number }) => {
        if (route.includes('/actions/runs/{run_id}')) {
          return { data: runs.get(params.run_id ?? -1) };
        }
        return {
          data: {
            artifacts: [
              {
                id: 1,
                name: 'state',
                expired: false,
                created_at: '2026-07-12T00:00:00Z',
                workflow_run: { id: 10 },
              },
              {
                id: 2,
                name: 'state',
                expired: false,
                created_at: '2026-07-13T00:00:00Z',
                workflow_run: { id: 11 },
              },
            ],
          },
        };
      },
    };
    const store = new GitHubArtifactStore(octokit, 'token', 'SolusQuest', 'agentic-pr-review', 99, {
      targetMode: 'pull-request',
      prNumber: 34,
    });

    await expect(store.findStateArtifact('state')).resolves.toEqual({
      id: 1,
      name: 'state',
      workflowRunId: 10,
      runHeadSha: 'old-head',
    });
  });

  it('rejects unsuccessful and unrelated pull-request runs', async () => {
    const runs = new Map<number, unknown>([
      [99, run()],
      [10, run({ id: 10, conclusion: 'failure' })],
      [11, run({ id: 11, pull_requests: [{ number: 35 }] })],
    ]);
    const octokit = {
      request: async (route: string, params: { run_id?: number }) =>
        route.includes('/actions/runs/{run_id}')
          ? { data: runs.get(params.run_id ?? -1) }
          : {
              data: {
                artifacts: [
                  {
                    id: 1,
                    name: 'state',
                    expired: false,
                    created_at: '2026-07-13',
                    workflow_run: { id: 10 },
                  },
                  {
                    id: 2,
                    name: 'state',
                    expired: false,
                    created_at: '2026-07-14',
                    workflow_run: { id: 11 },
                  },
                ],
              },
            },
    };
    const store = new GitHubArtifactStore(octokit, 'token', 'SolusQuest', 'agentic-pr-review', 99, {
      targetMode: 'pull-request',
      prNumber: 34,
    });

    await expect(store.findStateArtifact('state')).resolves.toBeUndefined();
  });

  it('does not let an explicit run id bypass target pull-request association', async () => {
    const runs = new Map<number, unknown>([
      [99, run()],
      [42, run({ id: 42, pull_requests: [{ number: 35 }] })],
    ]);
    const octokit = {
      request: async (route: string, params: { run_id?: number }) =>
        route.includes('/actions/runs/{run_id}')
          ? { data: runs.get(params.run_id ?? -1) }
          : {
              data: {
                artifacts: [
                  {
                    id: 5,
                    name: 'state',
                    expired: false,
                    created_at: '2026-07-14',
                    workflow_run: { id: 42 },
                  },
                ],
              },
            },
    };
    const store = new GitHubArtifactStore(octokit, 'token', 'SolusQuest', 'agentic-pr-review', 99, {
      targetMode: 'pull-request',
      prNumber: 34,
    });

    await expect(store.findStateArtifact('state', 42)).resolves.toBeUndefined();
  });

  it('fails closed when a workflow run omits provenance metadata', async () => {
    const octokit = {
      request: async (route: string) =>
        route.includes('/actions/runs/{run_id}')
          ? { data: run({ head_sha: undefined }) }
          : { data: { artifacts: [] } },
    };
    const store = new GitHubArtifactStore(octokit, 'token', 'SolusQuest', 'agentic-pr-review', 99, {
      targetMode: 'synthetic-fixture',
    });

    await expect(store.findStateArtifact('state')).rejects.toThrow(
      /provenance metadata is incomplete/,
    );
  });
});
