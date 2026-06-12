import { spawn } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { type ActionConfig, type Phase, type RestoredState, type RuntimeResult } from './types.js';
import { ensureDir, sha256, truncateText } from './utils.js';

export interface RuntimeRunOptions {
  config: ActionConfig;
  phase: Phase;
  stateKey: string;
  prompt: string;
  promptHash: string;
  restoredState?: RestoredState;
  workspace: string;
  tempDir: string;
}

export interface ReviewRuntime {
  run(options: RuntimeRunOptions): Promise<RuntimeResult>;
}

export class TestRuntime implements ReviewRuntime {
  async run(options: RuntimeRunOptions): Promise<RuntimeResult> {
    const sessionId =
      options.phase === 'incremental' && options.restoredState
        ? options.restoredState.sessionId
        : `test-${sha256(`${options.stateKey}:${options.promptHash}`).slice(0, 20)}`;
    const reviewMarkdown = [
      '## Agentic PR Review',
      '',
      'No findings in synthetic test runtime.',
      '',
      `- Phase: ${options.phase}`,
      `- Session: ${sessionId}`,
      `- Prompt hash: ${options.promptHash}`,
    ].join('\n');
    return { sessionId, reviewMarkdown, debugFiles: [] };
  }
}

export class ClaudeCodeRuntime implements ReviewRuntime {
  async run(options: RuntimeRunOptions): Promise<RuntimeResult> {
    const cliPath = await installClaudeCode(options.config.claudeCodeVersion!, options.tempDir);
    const debugFiles: string[] = [];
    const args = ['--bare'];

    if (options.phase === 'incremental' && options.restoredState?.sessionId) {
      args.push('--resume', options.restoredState.sessionId);
    }

    if (options.config.debugCaptureRawApiBodies) {
      const debugFile = path.join(options.tempDir, 'raw-provider-debug', 'claude-debug.log');
      await ensureDir(path.dirname(debugFile));
      debugFiles.push(debugFile);
      args.push('--debug', 'api', '--debug-file', debugFile);
    }

    args.push(
      '--tools',
      '',
      '--max-turns',
      '3',
      '--model',
      options.config.modelName!,
      '-p',
      options.prompt,
      '--output-format',
      'json',
    );

    const env = buildClaudeEnv(options.config, process.env);
    const result = await runProcess(cliPath, args, {
      cwd: options.workspace,
      env,
      timeoutMs: 20 * 60 * 1000,
    });

    if (result.exitCode !== 0) {
      throw new Error(
        `claude-code-cli exited with ${result.exitCode}: ${truncateText(result.stderr, 2000)}`,
      );
    }

    const parsed = parseClaudeJson(result.stdout);
    const reviewMarkdown = truncateText(
      parsed.result || result.stdout,
      options.config.maxReviewChars,
    );
    const sessionId =
      parsed.session_id ||
      parsed.sessionId ||
      options.restoredState?.sessionId ||
      `claude-${sha256(`${options.stateKey}:${options.promptHash}:${result.stdout}`).slice(0, 20)}`;

    return { sessionId, reviewMarkdown, debugFiles };
  }
}

function buildClaudeEnv(config: ActionConfig, baseEnv: NodeJS.ProcessEnv): NodeJS.ProcessEnv {
  const env = { ...baseEnv };
  delete env.AGENTIC_REVIEW_API_KEY;
  env.ANTHROPIC_BASE_URL = config.modelBaseUrl;
  env.ANTHROPIC_MODEL = config.modelName;
  env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1';
  env.DISABLE_TELEMETRY = '1';
  env.DO_NOT_TRACK = '1';
  if (config.smallModelName) {
    env.ANTHROPIC_SMALL_FAST_MODEL = config.smallModelName;
  }
  if (config.apiKeyMode === 'auth-token' || config.apiKeyMode === 'both') {
    env.ANTHROPIC_AUTH_TOKEN = config.apiKey;
  }
  if (config.apiKeyMode === 'api-key' || config.apiKeyMode === 'both') {
    env.ANTHROPIC_API_KEY = config.apiKey;
  }
  return env;
}

async function installClaudeCode(version: string, tempDir: string): Promise<string> {
  const installDir = path.join(tempDir, 'claude-code-cli');
  await ensureDir(installDir);
  const install = await runProcess(
    process.platform === 'win32' ? 'npm.cmd' : 'npm',
    [
      'install',
      '--prefix',
      installDir,
      `@anthropic-ai/claude-code@${version}`,
      '--no-audit',
      '--no-fund',
    ],
    { cwd: tempDir, env: process.env, timeoutMs: 5 * 60 * 1000 },
  );
  if (install.exitCode !== 0) {
    throw new Error(
      `failed to install claude-code-cli ${version}: ${truncateText(install.stderr, 2000)}`,
    );
  }
  const binName = process.platform === 'win32' ? 'claude.cmd' : 'claude';
  return path.join(installDir, 'node_modules', '.bin', binName);
}

function parseClaudeJson(stdout: string): {
  result?: string;
  session_id?: string;
  sessionId?: string;
} {
  try {
    return JSON.parse(stdout) as { result?: string; session_id?: string; sessionId?: string };
  } catch {
    return { result: stdout };
  }
}

function runProcess(
  command: string,
  args: string[],
  options: { cwd: string; env: NodeJS.ProcessEnv; timeoutMs: number },
): Promise<{ exitCode: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill('SIGTERM');
      reject(new Error(`process timed out after ${options.timeoutMs}ms: ${command}`));
    }, options.timeoutMs);
    child.stdout.setEncoding('utf8');
    child.stderr.setEncoding('utf8');
    child.stdout.on('data', (chunk: string) => {
      stdout += chunk;
    });
    child.stderr.on('data', (chunk: string) => {
      stderr += chunk;
    });
    child.on('error', (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.on('close', (exitCode) => {
      clearTimeout(timeout);
      resolve({ exitCode, stdout, stderr });
    });
  });
}

export function createRuntime(provider: ActionConfig['runtimeProvider']): ReviewRuntime {
  return provider === 'test' ? new TestRuntime() : new ClaudeCodeRuntime();
}

export function defaultTempDir(): string {
  return process.env.RUNNER_TEMP || os.tmpdir();
}
