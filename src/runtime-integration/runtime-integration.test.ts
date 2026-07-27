import { lstat, mkdtemp, readFile, readdir, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import type { RuntimeInvocationError } from '../runtime-invocation/runtime-errors.js';
import { invokeRuntime } from '../runtime-invocation/invoke-runtime.js';
import { readBootstrapInput } from '../runtime-invocation/invoke-runtime.test-helpers.js';
import { BYTE_LIMITS } from '../runtime-invocation/runtime-files.js';
import { invokeRuntimeForTests } from '../runtime-invocation/invoke-runtime.test-support.js';

const enabled = Boolean(process.env.APR_RUNTIME_INTEGRATION_ROOT);
const aotOnly = process.env.APR_RUNTIME_INTEGRATION_MODE === 'aot';

const integration = enabled ? describe : describe.skip;

integration('runtime integration', () => {
  const originalEnv = { ...process.env };
  let root: string;

  beforeEach(async () => {
    root = await mkdtemp(path.join(os.tmpdir(), 'agentic-pr-review-integration-'));
    process.env = {
      ...originalEnv,
    };
  });

  afterEach(async () => {
    process.env = originalEnv;
    await rm(root, { recursive: true, force: true });
  });

  it.skipIf(aotOnly)(
    'keeps repeated production runtime invocations byte-for-byte deterministic',
    async () => {
      const command = {
        executablePath: process.env.APR_RUNTIME_DOTNET ?? '',
        prefixArgs: JSON.parse(process.env.APR_RUNTIME_PREFIX_ARGS_JSON ?? '[]') as string[],
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
    },
  );

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

  it.skipIf(aotOnly).each([
    ['invalid-json', 'runtime-exit', 'contract', 'APR_INPUT_JSON_INVALID', 'absent', 'absent'],
    [
      'schema-invalid-input',
      'runtime-exit',
      'contract',
      'APR_INPUT_SCHEMA_INVALID',
      'absent',
      'absent',
    ],
    [
      'protocol-version',
      'runtime-exit',
      'contract',
      'APR_PROTOCOL_VERSION_UNSUPPORTED',
      'absent',
      'absent',
    ],
    ['exit-2', 'runtime-exit', 'usage', 'APR_USAGE_INVALID', 'absent', 'absent'],
    ['exit-10', 'runtime-exit', 'contract', 'APR_INPUT_SCHEMA_INVALID', 'absent', 'absent'],
    ['exit-20', 'runtime-exit', 'runtime', 'APR_RUNTIME_INTERNAL', 'absent', 'absent'],
    ['exit-30', 'runtime-exit', 'provider', 'APR_PROVIDER_FAILED', 'absent', 'absent'],
    ['exit-40', 'runtime-exit', 'file-io', 'APR_RESULT_WRITE_FAILED', 'absent', 'absent'],
    ['unknown-exit', 'unknown-exit', '', '', 'absent', 'absent'],
    ['missing-result', 'missing-output', '', '', 'absent', 'present'],
    ['missing-trace', 'missing-output', '', '', 'present', 'absent'],
    ['partial-result', 'result-invalid', '', '', 'present', 'present'],
    ['partial-trace', 'trace-invalid', '', '', 'present', 'present'],
    ['truncated-result', 'result-invalid', '', '', 'present', 'present'],
    ['truncated-trace', 'trace-invalid', '', '', 'present', 'present'],
    ['schema-invalid-result', 'result-invalid', '', '', 'present', 'present'],
    ['schema-invalid-trace', 'trace-invalid', '', '', 'present', 'present'],
    ['semantic-invalid-result', 'result-invalid', '', '', 'present', 'present'],
    ['missing-result-inputsha', 'process-contract-violation', '', '', 'present', 'present'],
    ['missing-result-trace', 'process-contract-violation', '', '', 'present', 'present'],
    ['missing-result-trace-sha', 'process-contract-violation', '', '', 'present', 'present'],
    ['result-trace-path', 'process-contract-violation', '', '', 'present', 'present'],
    ['trace-result-sha', 'process-contract-violation', '', '', 'present', 'present'],
    ['input-hash-mismatch', 'hash-mismatch', '', '', 'present', 'present'],
    ['trace-hash-mismatch', 'hash-mismatch', '', '', 'present', 'present'],
    ['version-mismatch', 'version-mismatch', '', '', 'present', 'present'],
    ...(process.platform === 'linux'
      ? [
          ['unsafe-result-directory', 'unsafe-output-file', '', '', 'rejected', 'present'],
          ['unsafe-trace-directory', 'unsafe-output-file', '', '', 'present', 'rejected'],
          ['unsafe-result-symlink', 'unsafe-output-file', '', '', 'rejected', 'present'],
          ['unsafe-trace-symlink', 'unsafe-output-file', '', '', 'present', 'rejected'],
          ['unsafe-result-non-regular', 'unsafe-output-file', '', '', 'rejected', 'present'],
          ['unsafe-trace-non-regular', 'unsafe-output-file', '', '', 'present', 'rejected'],
          ['unsafe-result-oversized', 'unsafe-output-file', '', '', 'rejected', 'present'],
          ['unsafe-trace-oversized', 'unsafe-output-file', '', '', 'present', 'rejected'],
        ]
      : []),
    ['timeout', 'timed-out', '', '', 'absent', 'absent'],
  ])(
    'maps fixture scenario %s to %s with tuple %s/%s/%s/%s',
    async (
      scenario,
      expectedKind,
      expectedExitClass,
      expectedDiagnosticCode,
      resultState,
      traceState,
    ) => {
      const shortSocketPath = scenario.includes('non-regular');
      const tempRoot = await mkdtemp(
        path.join(
          shortSocketPath ? os.tmpdir() : root,
          shortSocketPath ? 'apr-nr-' : `adapter-${scenario}-`,
        ),
      );
      const command = {
        executablePath:
          process.env.APR_RUNTIME_FIXTURE_DOTNET ?? process.env.APR_RUNTIME_DOTNET ?? '',
        prefixArgs: [process.env.APR_RUNTIME_FIXTURE_DLL ?? '', '--scenario', scenario],
      };
      let observed: { result: string; trace: string } | undefined;
      const classifyFile = async (filePath: string, cap: number): Promise<string> => {
        try {
          const info = await lstat(filePath);
          return info.isSymbolicLink() || !info.isFile() || info.size > cap
            ? 'rejected'
            : 'present';
        } catch {
          return 'absent';
        }
      };
      const invocation = invokeRuntimeForTests(
        {
          command,
          input: readBootstrapInput(),
          timeoutMs: scenario === 'timeout' ? 1_000 : 15_000,
          tempRoot,
        },
        {
          onBeforeCleanup: async (invocationDir) => {
            observed = {
              result: await classifyFile(
                path.join(invocationDir, 'result.json'),
                BYTE_LIMITS.result,
              ),
              trace: await classifyFile(path.join(invocationDir, 'trace.json'), BYTE_LIMITS.trace),
            };
          },
        },
      );
      try {
        await expect(invocation).rejects.toMatchObject({
          kind: expectedKind,
          exitClass: expectedExitClass || undefined,
          diagnosticCode: expectedDiagnosticCode || undefined,
        });
        expect(observed).toEqual({ result: resultState, trace: traceState });
      } finally {
        if (shortSocketPath) await rm(tempRoot, { recursive: true, force: true });
      }
    },
  );

  it.skipIf(aotOnly)(
    'proves the child environment allowlist and diagnostic sanitization',
    async () => {
      const probePath = path.join(root, 'env-probe.json');
      process.env.GITHUB_TOKEN = 'ghp_parent_secret';
      process.env.AGENTIC_REVIEW_API_KEY = 'sk-parent-secret';
      process.env.AGENTIC_REVIEW_DEEPSEEK_API_KEY = 'deepseek-parent-secret';
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
        agenticReviewDeepSeekApiKey: false,
        anthropicApiKey: false,
        sentinel: false,
      });

      let error: RuntimeInvocationError;
      let privacyFiles: { result: string; trace: string } | undefined;
      try {
        process.env.GITHUB_TOKEN = 'ghp_integration_fixture_token';
        await invokeRuntimeForTests(
          {
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
          },
          {
            onBeforeCleanup: async (invocationDir) => {
              const hasFile = async (filePath: string) => {
                try {
                  await lstat(filePath);
                  return 'present';
                } catch {
                  return 'absent';
                }
              };
              privacyFiles = {
                result: await hasFile(path.join(invocationDir, 'result.json')),
                trace: await hasFile(path.join(invocationDir, 'trace.json')),
              };
            },
          },
        );
        throw new Error('privacy fixture unexpectedly succeeded');
      } catch (value) {
        if (!value || typeof value !== 'object' || !('kind' in value)) throw value;
        error = value as RuntimeInvocationError;
      }
      expect(error.kind).toBe('runtime-exit');
      expect(error.exitClass).toBe('runtime');
      expect(error.diagnosticCode).toBe('APR_RUNTIME_INTERNAL');
      expect(error.failureTraceDiagnostics).toEqual([
        {
          code: 'APR_RUNTIME_INTERNAL',
          message: 'Authorization: *** token: *** path: <path>',
          level: 'error',
        },
      ]);
      expect(error.stderrSnippet).not.toContain('privacy_authorization_secret');
      expect(error.stderrSnippet).not.toContain('ghp_privacy_fixture_token');
      expect(error.stderrSnippet).not.toContain('C:\\private\\raw.json');
      expect(privacyFiles).toEqual({ result: 'absent', trace: 'present' });
    },
  );
});
