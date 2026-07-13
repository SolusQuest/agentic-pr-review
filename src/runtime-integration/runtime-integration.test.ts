import { mkdtemp, readFile, readdir, rm, stat } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { RuntimeInvocationError } from '../runtime-invocation/runtime-errors.js';
import { invokeRuntime } from '../runtime-invocation/invoke-runtime.js';
import { readBootstrapInput } from '../runtime-invocation/invoke-runtime.test-helpers.js';

const enabled = Boolean(process.env.APR_RUNTIME_INTEGRATION_ROOT);
const aotOnly = process.env.APR_RUNTIME_INTEGRATION_MODE === 'aot';

const mocks = vi.hoisted(() => {
  const inputs: Record<string, string> = {};
  const summary = { addRaw: vi.fn(), write: vi.fn(async () => undefined) };
  summary.addRaw.mockReturnValue(summary);
  const get = vi.fn();
  const listFiles = vi.fn();
  const listComments = vi.fn();
  const createComment = vi.fn();
  const updateComment = vi.fn();
  const octokit = {
    paginate: vi.fn(),
    rest: {
      pulls: { get, listFiles },
      issues: { listComments, createComment, updateComment },
      repos: { compareCommitsWithBasehead: vi.fn() },
    },
  };
  return {
    inputs,
    summary,
    octokit,
    get,
    listFiles,
    listComments,
    createComment,
    updateComment,
    getInput: vi.fn((name: string) => inputs[name] ?? ''),
    setOutput: vi.fn(),
    setSecret: vi.fn(),
    warning: vi.fn(),
    info: vi.fn(),
    getOctokit: vi.fn(() => octokit),
  };
});

vi.mock('@actions/core', () => ({
  getInput: mocks.getInput,
  setOutput: mocks.setOutput,
  setSecret: mocks.setSecret,
  warning: mocks.warning,
  info: mocks.info,
  summary: mocks.summary,
}));

vi.mock('@actions/github', () => ({
  context: {
    eventName: 'pull_request',
    repo: { owner: 'example', repo: 'repo' },
    payload: { pull_request: { number: 1 } },
    sha: 'integration-head-sha',
    runId: 123,
    runAttempt: 1,
  },
  getOctokit: mocks.getOctokit,
}));

const integration = enabled ? describe : describe.skip;

integration('runtime integration', () => {
  const originalEnv = { ...process.env };
  let root: string;
  let artifactRoot: string;
  let runnerTemp: string;
  let workspace: string;

  beforeEach(async () => {
    vi.clearAllMocks();
    for (const key of Object.keys(mocks.inputs)) delete mocks.inputs[key];
    root = await mkdtemp(path.join(os.tmpdir(), 'agentic-pr-review-integration-'));
    artifactRoot = path.join(root, 'artifacts');
    runnerTemp = path.join(root, 'runner-temp');
    workspace = process.env.GITHUB_WORKSPACE ?? process.cwd();
    process.env = {
      ...originalEnv,
      GITHUB_TOKEN: 'integration-github-token',
      GITHUB_WORKSPACE: workspace,
      RUNNER_TEMP: runnerTemp,
      AGENTIC_REVIEW_LOCAL_ARTIFACT_DIR: artifactRoot,
      AGENTIC_REVIEW_RUNTIME_EXECUTABLE: process.env.APR_RUNTIME_DOTNET ?? '',
      AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON:
        process.env.APR_RUNTIME_PREFIX_ARGS_JSON ??
        JSON.stringify(process.env.APR_RUNTIME_DLL ? [process.env.APR_RUNTIME_DLL] : []),
    };
    Object.assign(mocks.inputs, {
      runtime_backend: 'deterministic-csharp',
      runtime_provider: 'test',
      target_mode: 'pull-request',
      review_mode: 'bootstrap',
      pr_number: '1',
      state_key: 'runtime-integration',
      post_comment: 'false',
      artifact_retention_days: '7',
      test_runtime_fixture: 'valid',
    });
    mocks.get.mockResolvedValue({
      data: {
        title: 'Integration PR',
        body: 'Integration fixture body',
        base: { ref: 'main', sha: 'integration-base-sha' },
        head: {
          ref: 'integration',
          sha: 'integration-head-sha',
          repo: { full_name: 'example/repo' },
        },
        draft: false,
        html_url: 'https://github.com/example/repo/pull/1',
      },
    });
    mocks.octokit.paginate.mockImplementation(async () => [
      {
        filename: 'src/integration.ts',
        status: 'modified',
        additions: 1,
        deletions: 0,
        changes: 1,
        patch: '@@ -1 +1 @@\n+integration',
      },
    ]);
    mocks.createComment.mockResolvedValue({
      data: { html_url: 'https://github.com/example/repo/pull/1#issuecomment-1' },
    });
  });

  afterEach(async () => {
    process.env = originalEnv;
    await rm(root, { recursive: true, force: true });
  });

  it('runs the complete source host path with the published framework runtime', async () => {
    const { run } = await import('../main.js');
    await expect(run()).resolves.toBeUndefined();

    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_backend', 'deterministic-csharp');
    expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', '');
    expect(mocks.setOutput).toHaveBeenCalledWith(
      'runtime_trace_sha256',
      expect.stringMatching(/^[a-f0-9]{64}$/),
    );
    expect(mocks.summary.write).toHaveBeenCalledTimes(1);
    expect(mocks.createComment).not.toHaveBeenCalled();

    const artifactNames = await readdir(artifactRoot);
    expect(artifactNames).toHaveLength(1);
    const bundle = path.join(artifactRoot, artifactNames[0]);
    await expect(stat(path.join(bundle, 'manifest.json'))).resolves.toBeTruthy();
    await expect(stat(path.join(bundle, 'structured-result.json'))).resolves.toBeTruthy();
    await expect(stat(path.join(bundle, 'rendered-review.md'))).resolves.toBeTruthy();
    await expect(readdir(path.join(bundle, 'runtime'))).rejects.toThrow();
    const result = await readFile(path.join(bundle, 'structured-result.json'), 'utf8');
    expect(result).toContain('Deterministic fixture runtime completed without findings.');
    expect(result).not.toContain('integration-github-token');
  });

  it('keeps repeated real runtime invocations byte-for-byte deterministic', async () => {
    const command = {
      executablePath:
        process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
      prefixArgs: [process.env.APR_RUNTIME_FIXTURE_DLL ?? '', '--scenario', 'success'],
    };
    const input = readBootstrapInput();
    const first = await invokeRuntime({
      command,
      input,
      timeoutMs: 15_000,
      tempRoot: await mkdtemp(path.join(root, 'determinism-first-')),
    });
    const second = await invokeRuntime({
      command,
      input,
      timeoutMs: 15_000,
      tempRoot: await mkdtemp(path.join(root, 'determinism-second-')),
    });

    expect(first.inputSha256).toBe(second.inputSha256);
    expect(first.runtimeVersion).toBe(second.runtimeVersion);
    expect(first.resultBytes).toEqual(second.resultBytes);
    expect(first.traceBytes).toEqual(second.traceBytes);
    expect(first.result.trace?.sha256).toBe(second.result.trace?.sha256);
  });

  it('cleans a failed invocation before the next isolated success', async () => {
    const failedRoot = await mkdtemp(path.join(root, 'cleanup-failure-'));
    await expect(
      invokeRuntime({
        command: {
          executablePath:
            process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
          prefixArgs: [process.env.APR_RUNTIME_FIXTURE_DLL ?? '', '--scenario', 'malformed-result'],
        },
        input: readBootstrapInput(),
        timeoutMs: 15_000,
        tempRoot: failedRoot,
      }),
    ).rejects.toMatchObject({ kind: 'result-invalid' });
    await expect(readdir(failedRoot)).resolves.toEqual([]);

    const successRoot = await mkdtemp(path.join(root, 'cleanup-success-'));
    await expect(
      invokeRuntime({
        command: {
          executablePath:
            process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
          prefixArgs: [process.env.APR_RUNTIME_FIXTURE_DLL ?? '', '--scenario', 'success'],
        },
        input: readBootstrapInput(),
        timeoutMs: 15_000,
        tempRoot: successRoot,
      }),
    ).resolves.toBeDefined();
    await expect(readdir(successRoot)).resolves.toEqual([]);
  });

  it.skipIf(aotOnly)(
    'proves the failure barrier with an eligible pull request publisher',
    async () => {
      Object.assign(mocks.inputs, {
        post_comment: 'true',
        state_key: 'runtime-integration-failure',
      });
      process.env.AGENTIC_REVIEW_RUNTIME_EXECUTABLE = process.env.APR_RUNTIME_FIXTURE_DOTNET ?? '';
      process.env.AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON = JSON.stringify([
        process.env.APR_RUNTIME_FIXTURE_DLL,
        '--scenario',
        'malformed-result',
      ]);

      const { run } = await import('../main.js');
      await expect(run()).rejects.toThrow(/deterministic runtime failed: result-invalid/);

      expect(mocks.createComment).not.toHaveBeenCalled();
      expect(mocks.updateComment).not.toHaveBeenCalled();
      expect(mocks.setOutput).toHaveBeenCalledWith('runtime_error_kind', 'result-invalid');
      await expect(stat(artifactRoot)).rejects.toThrow();
      expect(mocks.summary.write).not.toHaveBeenCalled();
    },
  );

  it.skipIf(aotOnly).each([
    ['invalid-json', 'runtime-exit'],
    ['schema-invalid-input', 'runtime-exit'],
    ['protocol-version', 'runtime-exit'],
    ['exit-2', 'runtime-exit'],
    ['exit-10', 'runtime-exit'],
    ['exit-20', 'runtime-exit'],
    ['exit-30', 'runtime-exit'],
    ['exit-40', 'runtime-exit'],
    ['unknown-exit', 'unknown-exit'],
    ['missing-result', 'missing-output'],
    ['missing-trace', 'missing-output'],
    ['partial-result', 'result-invalid'],
    ['partial-trace', 'trace-invalid'],
    ['schema-invalid-result', 'result-invalid'],
    ['schema-invalid-trace', 'trace-invalid'],
    ['input-hash-mismatch', 'hash-mismatch'],
    ['trace-hash-mismatch', 'hash-mismatch'],
    ['version-mismatch', 'version-mismatch'],
    ['unsafe-result-directory', 'unsafe-output-file'],
    ['unsafe-trace-directory', 'unsafe-output-file'],
    ['timeout', 'timed-out'],
  ])('maps fixture scenario %s to %s', async (scenario, expectedKind) => {
    const tempRoot = await mkdtemp(path.join(root, `adapter-${scenario}-`));
    const command = {
      executablePath:
        process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
      prefixArgs: [process.env.APR_RUNTIME_FIXTURE_DLL ?? '', '--scenario', scenario],
    };
    await expect(
      invokeRuntime({
        command,
        input: readBootstrapInput(),
        timeoutMs: scenario === 'timeout' ? 1_000 : 15_000,
        tempRoot,
      }),
    ).rejects.toMatchObject({ kind: expectedKind });
  });

  it.skipIf(aotOnly)(
    'proves the child environment allowlist and diagnostic sanitization',
    async () => {
      const probePath = path.join(root, 'env-probe.json');
      process.env.GITHUB_TOKEN = 'ghp_parent_secret';
      process.env.AGENTIC_REVIEW_API_KEY = 'sk-parent-secret';
      process.env.ANTHROPIC_API_KEY = 'anthropic-parent-secret';
      process.env.INTEGRATION_SECRET_SENTINEL = 'sentinel-parent-secret';
      process.env.AGENTIC_REVIEW_ENV_PROBE_PATH = probePath;
      await expect(
        invokeRuntime({
          command: {
            executablePath:
              process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
            prefixArgs: [
              process.env.APR_RUNTIME_FIXTURE_DLL ?? '',
              '--scenario',
              'env-probe',
              '--probe',
              probePath,
            ],
          },
          input: readBootstrapInput(),
          timeoutMs: 15_000,
          tempRoot: await mkdtemp(path.join(root, 'env-probe-')),
        }),
      ).resolves.toBeDefined();
      expect(JSON.parse(await readFile(probePath, 'utf8'))).toEqual({
        githubToken: false,
        githubAction: false,
        agenticReviewApiKey: false,
        anthropicApiKey: false,
        sentinel: false,
      });

      let error: RuntimeInvocationError;
      try {
        process.env.GITHUB_TOKEN = 'ghp_integration_fixture_token';
        await invokeRuntime({
          command: {
            executablePath:
              process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
            prefixArgs: [
              process.env.APR_RUNTIME_FIXTURE_DLL ?? '',
              '--scenario',
              'privacy-diagnostic',
            ],
          },
          input: readBootstrapInput(),
          timeoutMs: 15_000,
          tempRoot: await mkdtemp(path.join(root, 'privacy-')),
        });
        throw new Error('privacy fixture unexpectedly succeeded');
      } catch (value) {
        if (!value || typeof value !== 'object' || !('kind' in value)) throw value;
        error = value as RuntimeInvocationError;
      }
      expect(error.kind).toBe('runtime-exit');
      expect(error.stderrSnippet).not.toContain('ghp_integration_fixture_token');
      expect(error.stderrSnippet).not.toContain('C:\\private\\raw.json');
    },
  );
});
