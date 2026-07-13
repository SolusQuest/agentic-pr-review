import { mkdir, mkdtemp, readFile, rm, stat, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  deterministicStateArtifactName,
  deterministicStateKey,
  stateArtifactName,
} from './state.js';
import { LocalArtifactStore } from './artifacts.js';
import { type PullRequestDiffSnapshotV1 } from './types.js';
import { sha256 } from './utils.js';

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
    eventName: 'pull_request',
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

  function currentSnapshot(
    options: {
      headSha?: string;
      files?: PullRequestDiffSnapshotV1['files'];
    } = {},
  ): PullRequestDiffSnapshotV1 {
    return {
      version: 1,
      source: 'github-pulls-list-files',
      baseSha: 'base-sha',
      headSha: options.headSha ?? 'old-head-sha',
      files: options.files ?? [
        {
          filename: 'src/file.ts',
          status: 'modified',
          additions: 1,
          deletions: 0,
          changes: 1,
          patchAvailable: true,
          patchSha256: sha256('@@ -1 +1 @@\n+change'),
        },
      ],
    };
  }

  async function writeRestoredArtifact(
    stateKey: string,
    snapshot?: PullRequestDiffSnapshotV1,
    runtimeBackend: 'legacy' | 'deterministic-csharp' = 'legacy',
  ): Promise<void> {
    const artifactDir = path.join(
      artifactRoot,
      runtimeBackend === 'deterministic-csharp'
        ? deterministicStateArtifactName(stateKey)
        : stateArtifactName(stateKey),
    );
    await mkdir(path.join(artifactDir, 'runtime', 'test'), { recursive: true });
    await writeFile(
      path.join(artifactDir, 'manifest.json'),
      JSON.stringify(
        {
          version: 1,
          workflow: 'agentic-pr-review',
          stateKey,
          phase: 'bootstrap',
          ...(runtimeBackend === 'deterministic-csharp'
            ? { runtimeBackend: 'deterministic-csharp' }
            : {}),
          runtimeProvider: 'test',
          toolMode: 'none',
          allowedTools: [],
          sessionId: 'session-1',
          sessionName: 'session-name',
          reviewedHeadSha: 'old-head-sha',
          promptSha256: sha256('prompt-hash'),
          createdAt: '2026-01-01T00:00:00.000Z',
          updatedAt: '2026-01-01T00:00:00.000Z',
          usage: null,
          observedTurns: 0,
          observedTurnSource: 'not_applicable',
          lineageTotals: {
            observedTurns: 0,
            usage: {
              inputTokens: 0,
              cacheReadInputTokens: 0,
              cacheCreationInputTokens: 0,
              outputTokens: 0,
            },
            source: 'current_run_only',
            partial: false,
          },
          usageBudgetStatus: {
            status: 'disabled',
            limits: {
              maxUncachedInputTokens: 0,
              maxCachedInputTokens: 0,
              maxOutputTokens: 0,
            },
            usageRecordsObserved: 0,
          },
          structuredOutput: {
            status: 'valid',
            inputFindingCount: 0,
            postFindingCapCount: 0,
            renderedFindingCount: 0,
            findingsTruncated: false,
          },
          contextBlocks: [],
          target: {
            mode: 'pull-request',
            prNumber: 1,
            headRepository: 'example/repo',
            baseSha: 'base-sha',
            headSha: 'old-head-sha',
            changedFiles: snapshot?.files.length ?? 1,
            ...(snapshot ? { pullRequestDiffSnapshot: snapshot } : {}),
          },
        },
        null,
        2,
      ),
      'utf8',
    );
  }

  it('does not upload restorable state when required sticky comment posting fails', async () => {
    const { run } = await import('./main.js');

    await expect(run()).rejects.toThrow(/comment denied/);

    expect(mocks.createComment).toHaveBeenCalledTimes(1);
    await expect(
      stat(path.join(artifactRoot, stateArtifactName('comment-fail'))),
    ).rejects.toThrow();
    expect(mocks.setOutput).not.toHaveBeenCalledWith('reviewed_head_sha', 'head-sha');
  });

  it('short-circuits deterministic identical reviews without command configuration', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'incremental',
      state_key: 'deterministic-identical',
    });
    const logicalStateKey = deterministicStateKey('deterministic-identical');
    await writeRestoredArtifact(
      logicalStateKey,
      currentSnapshot({ headSha: 'head-sha' }),
      'deterministic-csharp',
    );

    const { run } = await import('./main.js');
    await expect(run()).resolves.toBeUndefined();

    const outputNames = mocks.setOutput.mock.calls.map(([name]) => name);
    expect(outputNames).toContain('runtime_backend');
    expect(outputNames).toContain('runtime_version');
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_version', '');
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_trace_sha256', '');
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    expect(mocks.createComment).not.toHaveBeenCalled();
    const manifest = JSON.parse(
      await readFile(
        path.join(artifactRoot, deterministicStateArtifactName(logicalStateKey), 'manifest.json'),
        'utf8',
      ),
    ) as { sessionName: string };
    expect(manifest.sessionName).toBe(`agentic-pr-review-${logicalStateKey}`);
  });

  it('runs the guarded deterministic backend through the fake runtime', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'bootstrap',
      state_key: 'deterministic-smoke',
    });
    process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.execPath;
    process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
      path.join(process.cwd(), 'src/runtime-invocation/__test-fixtures__/fake-runtime.mjs'),
    ]);
    process.env.FAKE_RUNTIME_SCENARIO = 'success';

    const { run } = await import('./main.js');
    await expect(run()).resolves.toBeUndefined();

    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_backend', 'deterministic-csharp');
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    expect(mocks.setOutput).toHaveBeenCalledWith(
      'usage_budget_status',
      'not_applicable (records=0)',
    );
    await expect(
      stat(
        path.join(
          artifactRoot,
          deterministicStateArtifactName(deterministicStateKey('deterministic-smoke')),
        ),
      ),
    ).resolves.toBeTruthy();
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).not.toContain(tempRoot);
  });

  it('does not upload an artifact when deterministic sticky publishing fails', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'true',
      review_mode: 'bootstrap',
      state_key: 'deterministic-comment-failure',
    });
    process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.execPath;
    process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
      path.join(process.cwd(), 'src/runtime-invocation/__test-fixtures__/fake-runtime.mjs'),
    ]);
    process.env.FAKE_RUNTIME_SCENARIO = 'success';

    const { run } = await import('./main.js');
    await expect(run()).rejects.toThrow(/deterministic sticky comment could not be published/);

    expect(mocks.createComment).toHaveBeenCalledTimes(1);
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', 'rendering-invalid');
    const summary = mocks.summary.addRaw.mock.calls.at(-1)?.[0] as string;
    expect(summary).toContain('Sticky comment written: false');
    expect(summary).toContain('State artifact upload: not attempted');
    expect(summary).toContain('Failure classification: rendering-invalid');
    expect(summary).not.toContain('State artifact upload: failed');
    await expect(
      stat(
        path.join(
          artifactRoot,
          deterministicStateArtifactName(deterministicStateKey('deterministic-comment-failure')),
        ),
      ),
    ).rejects.toThrow();
  });

  it('keeps a successful deterministic sticky comment when state upload fails', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'true',
      review_mode: 'bootstrap',
      state_key: 'deterministic-comment-upload-failure',
    });
    mocks.createComment.mockResolvedValue({
      data: { html_url: 'https://github.com/example/repo/pull/1#issuecomment-1' },
    });
    process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.execPath;
    process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
      path.join(process.cwd(), 'src/runtime-invocation/__test-fixtures__/fake-runtime.mjs'),
    ]);
    process.env.FAKE_RUNTIME_SCENARIO = 'success';
    const uploadSpy = vi
      .spyOn(LocalArtifactStore.prototype, 'upload')
      .mockRejectedValueOnce(new Error('blocked state upload'));

    const { run } = await import('./main.js');
    try {
      await expect(run()).rejects.toThrow('state-invalid: state artifact upload failed');
    } finally {
      uploadSpy.mockRestore();
    }

    expect(mocks.createComment).toHaveBeenCalledTimes(1);
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).toContain('Sticky comment written: true');
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).toContain('State artifact upload: failed');
  });

  it('keeps runtime success outputs and writes a bounded summary when state upload fails', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'bootstrap',
      state_key: 'deterministic-upload-failure',
    });
    process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.execPath;
    process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
      path.join(process.cwd(), 'src/runtime-invocation/__test-fixtures__/fake-runtime.mjs'),
    ]);
    process.env.FAKE_RUNTIME_SCENARIO = 'success';
    const uploadSpy = vi
      .spyOn(LocalArtifactStore.prototype, 'upload')
      .mockRejectedValueOnce(new Error('blocked state upload'));

    const { run } = await import('./main.js');
    try {
      await expect(run()).rejects.toThrow('state-invalid: state artifact upload failed');
    } finally {
      uploadSpy.mockRestore();
    }
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).toContain('State artifact upload: failed');
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).not.toContain(tempRoot);
  });

  it('preserves skipped-identical upload failures instead of rewriting them as snapshot failures', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'incremental',
      state_key: 'deterministic-skipped-upload-failure',
    });
    const logicalStateKey = deterministicStateKey('deterministic-skipped-upload-failure');
    await writeRestoredArtifact(
      logicalStateKey,
      currentSnapshot({ headSha: 'head-sha' }),
      'deterministic-csharp',
    );
    process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.execPath;
    process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
      path.join(process.cwd(), 'src/runtime-invocation/__test-fixtures__/fake-runtime.mjs'),
    ]);
    process.env.FAKE_RUNTIME_SCENARIO = 'success';
    const uploadSpy = vi
      .spyOn(LocalArtifactStore.prototype, 'upload')
      .mockRejectedValueOnce(new Error('blocked state upload'));

    const { run } = await import('./main.js');
    try {
      await expect(run()).rejects.toThrow('state-invalid: state artifact upload failed');
    } finally {
      uploadSpy.mockRestore();
    }

    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    const summary = mocks.summary.addRaw.mock.calls.at(-1)?.[0] as string;
    expect(summary).toContain('State artifact upload: failed');
    expect(summary).not.toContain('incremental snapshot unusable');
  });

  it('preserves skipped-identical stale-head failures instead of rewriting them as snapshot failures', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'incremental',
      state_key: 'deterministic-skipped-stale-head',
    });
    const logicalStateKey = deterministicStateKey('deterministic-skipped-stale-head');
    await writeRestoredArtifact(
      logicalStateKey,
      currentSnapshot({ headSha: 'head-sha' }),
      'deterministic-csharp',
    );
    mocks.octokit.rest.pulls.get
      .mockResolvedValueOnce({
        data: {
          title: 'Synthetic PR',
          body: 'Synthetic body',
          base: { ref: 'main', sha: 'base-sha' },
          head: { ref: 'branch', sha: 'head-sha', repo: { full_name: 'example/repo' } },
          draft: false,
          html_url: 'https://github.com/example/repo/pull/1',
        },
      })
      .mockResolvedValueOnce({ data: { head: { sha: 'new-head-sha' } } });

    const { run } = await import('./main.js');
    await expect(run()).rejects.toThrow('state-invalid: target head could not be confirmed');

    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', 'state-invalid');
    expect(mocks.summary.addRaw).not.toHaveBeenCalledWith(
      expect.stringContaining('incremental snapshot unusable'),
    );
  });

  it('reports bounded command errors before deterministic side effects', async () => {
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      post_comment: 'false',
      review_mode: 'bootstrap',
      state_key: 'deterministic-command-failure',
    });
    delete process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE;
    delete process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON;

    const { run } = await import('./main.js');
    await expect(run()).rejects.toThrow(/AGENTIC_REVIEW_RUNTIME_EXECUTABLE/);

    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', 'command-unavailable');
    await expect(
      stat(
        path.join(
          artifactRoot,
          deterministicStateArtifactName(deterministicStateKey('deterministic-command-failure')),
        ),
      ),
    ).rejects.toThrow();
  });

  it('skips inline comments when sticky comment posting is disabled', async () => {
    Object.assign(mocks.inputs, {
      post_comment: 'false',
      inline_comments: 'true',
      state_key: 'inline-without-sticky',
      test_runtime_fixture: 'inline_commentable',
    });
    const { run } = await import('./main.js');

    await run();

    expect(mocks.createComment).not.toHaveBeenCalled();
    expect(mocks.setOutput).toHaveBeenCalledWith('inline_comments_enabled', 'true');
    expect(mocks.setOutput).toHaveBeenCalledWith('inline_comments_skipped_count', '1');
    expect(mocks.setOutput).not.toHaveBeenCalledWith('runtime_backend', expect.anything());
    expect(mocks.setOutput).not.toHaveBeenCalledWith('usage_budget_status', expect.anything());
    expect(mocks.warning).toHaveBeenCalledWith(
      'Inline PR review comments require post_comment=true so the sticky review remains the source of truth.',
    );
    await expect(
      stat(path.join(artifactRoot, stateArtifactName('inline-without-sticky'))),
    ).resolves.toBeTruthy();
  });

  it('auto bootstraps when restored PR state lacks a compatible diff snapshot', async () => {
    Object.assign(mocks.inputs, {
      review_mode: 'auto',
      post_comment: 'false',
      state_key: 'legacy-pr-state',
      test_runtime_fixture: 'no_findings',
    });
    await writeRestoredArtifact('legacy-pr-state');
    const { run } = await import('./main.js');

    await run();

    expect(mocks.setOutput).toHaveBeenCalledWith('phase', 'bootstrap');
    expect(mocks.warning).toHaveBeenCalledWith(expect.stringContaining('not snapshot-compatible'));
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).toContain(
      'Phase reason: snapshot_state_incompatible',
    );
    expect(mocks.octokit.rest.repos.compareCommitsWithBasehead).not.toHaveBeenCalled();
  });

  it('restores legacy manifests without deterministic provenance fields', async () => {
    Object.assign(mocks.inputs, {
      review_mode: 'incremental',
      post_comment: 'false',
      state_key: 'legacy-old-manifest',
    });
    await writeRestoredArtifact('legacy-old-manifest', currentSnapshot());
    const manifestPath = path.join(
      artifactRoot,
      stateArtifactName('legacy-old-manifest'),
      'manifest.json',
    );
    const manifest = JSON.parse(await readFile(manifestPath, 'utf8')) as any;
    delete manifest.target.headRepository;
    await writeFile(manifestPath, JSON.stringify(manifest), 'utf8');

    const { run } = await import('./main.js');
    await run();

    expect(mocks.setOutput).toHaveBeenCalledWith('phase', 'incremental');
    expect(mocks.setOutput).toHaveBeenCalledWith('review_phase', 'skipped-identical');
  });

  it('forced incremental bootstraps without snapshot-compatible state and records fallback metadata', async () => {
    Object.assign(mocks.inputs, {
      review_mode: 'incremental',
      post_comment: 'false',
      state_key: 'missing-snapshot-state',
      test_runtime_fixture: 'no_findings',
    });
    const { run } = await import('./main.js');

    await run();

    expect(mocks.setOutput).toHaveBeenCalledWith('review_mode', 'incremental');
    expect(mocks.setOutput).toHaveBeenCalledWith('phase', 'bootstrap');
    expect(mocks.summary.addRaw.mock.calls.at(-1)?.[0]).toContain(
      'Phase reason: snapshot_state_missing',
    );
    const manifest = JSON.parse(
      await readFile(
        path.join(artifactRoot, stateArtifactName('missing-snapshot-state'), 'manifest.json'),
        'utf8',
      ),
    ) as any;
    expect(manifest.review).toMatchObject({
      requestedMode: 'incremental',
      executedPhase: 'bootstrap',
      phaseReason: 'snapshot_state_missing',
      effectiveDiffSource: 'bootstrap_pr_files',
    });
  });

  it('skips provider calls when previous and current PR diff snapshots are equal', async () => {
    Object.assign(mocks.inputs, {
      review_mode: 'incremental',
      post_comment: 'false',
      state_key: 'snapshot-equal',
      test_runtime_fixture: 'no_findings',
    });
    await writeRestoredArtifact('snapshot-equal', currentSnapshot());
    const runtime = await import('./runtime.js');
    const runSpy = vi.spyOn(runtime.TestRuntime.prototype, 'run');
    const { run } = await import('./main.js');

    try {
      await run();
      expect(runSpy).not.toHaveBeenCalled();
      expect(mocks.setOutput).toHaveBeenCalledWith('phase', 'incremental');
      expect(mocks.setOutput).toHaveBeenCalledWith('review_phase', 'skipped-identical');
      expect(mocks.octokit.rest.repos.compareCommitsWithBasehead).not.toHaveBeenCalled();
      await run();
      expect(runSpy).not.toHaveBeenCalled();
    } finally {
      runSpy.mockRestore();
    }
  });

  it('does not skip provider calls when patch-unavailable current PR files change sha', async () => {
    Object.assign(mocks.inputs, {
      review_mode: 'incremental',
      post_comment: 'false',
      state_key: 'snapshot-binary-changed',
      test_runtime_fixture: 'no_findings',
    });
    mocks.octokit.paginate.mockImplementation(async (endpoint: unknown) => {
      if (endpoint === mocks.listFiles) {
        return [
          {
            sha: 'new-binary-sha',
            filename: 'assets/generated.bin',
            status: 'modified',
            additions: 0,
            deletions: 0,
            changes: 0,
          },
        ];
      }
      return [];
    });
    await writeRestoredArtifact(
      'snapshot-binary-changed',
      currentSnapshot({
        files: [
          {
            filename: 'assets/generated.bin',
            status: 'modified',
            additions: 0,
            deletions: 0,
            changes: 0,
            fileSha: 'old-binary-sha',
            patchAvailable: false,
            patchSha256: null,
          },
        ],
      }),
    );
    const runtime = await import('./runtime.js');
    const runSpy = vi.spyOn(runtime.TestRuntime.prototype, 'run');
    const { run } = await import('./main.js');

    try {
      await run();
      expect(runSpy).toHaveBeenCalledTimes(1);
      expect(mocks.setOutput).toHaveBeenCalledWith('phase', 'incremental');
      expect(mocks.setOutput).toHaveBeenCalledWith('review_phase', 'incremental');
      const prompt = runSpy.mock.calls[0][0].prompt;
      expect(prompt).toContain('assets/generated.bin');
      expect(prompt).toContain('## Bounded Current PR Patch Context\n- none');
    } finally {
      runSpy.mockRestore();
    }
  });

  it('uses only changed current PR diff entries for snapshot-changed incremental prompts', async () => {
    const unchangedPatch = 'UNCHANGED_PATCH_BODY'.repeat(100);
    const currentPatch = '@@ -1 +1 @@\n-old\n+new';
    Object.assign(mocks.inputs, {
      review_mode: 'incremental',
      post_comment: 'false',
      state_key: 'snapshot-changed',
      test_runtime_fixture: 'no_findings',
    });
    mocks.octokit.paginate.mockImplementation(async (endpoint: unknown) => {
      if (endpoint === mocks.listFiles) {
        return [
          {
            filename: 'docs/current-change.md',
            status: 'modified',
            additions: 1,
            deletions: 1,
            changes: 2,
            patch: currentPatch,
          },
          {
            filename: 'src/unchanged.ts',
            status: 'modified',
            additions: 1,
            deletions: 0,
            changes: 1,
            patch: unchangedPatch,
          },
        ];
      }
      return [];
    });
    mocks.octokit.rest.repos.compareCommitsWithBasehead.mockResolvedValue({
      data: {
        files: [
          {
            filename: 'src/base-only.ts',
            status: 'modified',
            additions: 1,
            deletions: 0,
            changes: 1,
            patch: '+base only',
          },
        ],
      },
    });
    await writeRestoredArtifact(
      'snapshot-changed',
      currentSnapshot({
        files: [
          {
            filename: 'docs/current-change.md',
            status: 'modified',
            additions: 1,
            deletions: 1,
            changes: 2,
            patchAvailable: true,
            patchSha256: sha256('old patch'),
          },
          {
            filename: 'src/unchanged.ts',
            status: 'modified',
            additions: 1,
            deletions: 0,
            changes: 1,
            patchAvailable: true,
            patchSha256: sha256(unchangedPatch),
          },
        ],
      }),
    );
    const runtime = await import('./runtime.js');
    const runSpy = vi.spyOn(runtime.TestRuntime.prototype, 'run');
    const { run } = await import('./main.js');

    try {
      await run();
      expect(runSpy).toHaveBeenCalledTimes(1);
      const prompt = runSpy.mock.calls[0][0].prompt;
      expect(prompt).toContain('docs/current-change.md');
      expect(prompt).toContain('+new');
      expect(prompt).toContain('src/unchanged.ts');
      expect(prompt).not.toContain(unchangedPatch);
      expect(prompt).not.toContain('src/base-only.ts');
      expect(mocks.octokit.rest.repos.compareCommitsWithBasehead).not.toHaveBeenCalled();
    } finally {
      runSpy.mockRestore();
    }
  });
});
