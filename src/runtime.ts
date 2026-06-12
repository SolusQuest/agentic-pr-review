import { spawn } from 'node:child_process';
import { cp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  type ActionConfig,
  type Phase,
  type RestoredState,
  type RuntimeResult,
  type RuntimeUsage,
} from './types.js';
import { ensureDir, sha256, truncateText, walkFiles, writeTextFile } from './utils.js';

export interface RuntimeRunOptions {
  config: ActionConfig;
  phase: Phase;
  stateKey: string;
  prompt: string;
  promptHash: string;
  restoredState?: RestoredState;
  workspace: string;
  tempDir: string;
  runtimeDir: string;
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
    const sessionName =
      options.restoredState?.sessionName ?? `agentic-pr-review-${options.stateKey}`;
    const transcriptPath = path.join(
      options.runtimeDir,
      'sessions',
      options.stateKey,
      `${sessionId}.jsonl`,
    );
    const reviewMarkdown = [
      '## Agentic PR Review',
      '',
      'No findings in synthetic test runtime.',
      '',
      `- Phase: ${options.phase}`,
      `- Session: ${sessionId}`,
      `- Prompt hash: ${options.promptHash}`,
    ].join('\n');

    await writeTextFile(
      transcriptPath,
      `${JSON.stringify({
        type: 'assistant',
        session_id: sessionId,
        session_name: sessionName,
        state_key: options.stateKey,
        phase: options.phase,
        prompt_hash: options.promptHash,
      })}\n${JSON.stringify({ type: 'result', session_id: sessionId, result: reviewMarkdown })}\n`,
    );

    return { sessionId, sessionName, reviewMarkdown, debugFiles: [] };
  }
}

export class ClaudeCodeRuntime implements ReviewRuntime {
  async run(options: RuntimeRunOptions): Promise<RuntimeResult> {
    const cliPath = await installClaudeCode(options.config.claudeCodeVersion!, options.tempDir);
    const sessionName =
      options.restoredState?.sessionName ?? `agentic-pr-review-${options.stateKey}`;
    const configDir = path.join(options.runtimeDir, 'config');
    const outputPath = path.join(
      options.tempDir,
      'outputs',
      `claude-output-${Date.now()}.stream-jsonl`,
    );
    const debugFiles: string[] = [];
    const rawDebugDir = options.config.debugCaptureRawApiBodies
      ? path.join(options.tempDir, 'raw-provider-debug', 'raw-api-bodies')
      : undefined;

    await ensureDir(configDir);
    await ensureDir(path.dirname(outputPath));
    if (rawDebugDir) {
      await ensureDir(rawDebugDir);
    }

    const args = [
      '-p',
      '--output-format',
      'stream-json',
      '--verbose',
      '--max-turns',
      '3',
      '--model',
      options.config.modelName!,
      '--exclude-dynamic-system-prompt-sections',
      '--tools',
      '',
    ];

    if (options.phase === 'incremental') {
      const sessionId = options.restoredState?.sessionId;
      if (!sessionId) {
        throw new Error('incremental phase requires a restored Claude session id');
      }
      args.push('--resume', sessionId);
    } else {
      args.push('--name', sessionName);
    }

    args.push('Run the PR review instructions from stdin. Do not edit files.');

    const env = buildClaudeEnv(options.config, process.env, configDir, rawDebugDir);
    const result = await runProcess(cliPath, args, {
      cwd: options.workspace,
      env,
      stdin: options.prompt,
      timeoutMs: 20 * 60 * 1000,
    });
    await writeFile(outputPath, result.stdout, 'utf8');

    if (result.exitCode !== 0) {
      throw new Error(
        `claude-code-cli exited with ${result.exitCode}: ${summarizeDiagnostic(
          result.stderr || result.stdout,
          env,
        )}`,
      );
    }

    if (rawDebugDir) {
      const rawFiles = await walkFiles(rawDebugDir);
      if (rawFiles.length === 0) {
        const notePath = path.join(rawDebugDir, 'diagnostic-note.txt');
        await writeTextFile(notePath, 'No raw provider body files were emitted by this run.\n');
        rawFiles.push(notePath);
      }
      debugFiles.push(...rawFiles);
    }

    return {
      sessionId: await discoverSessionId(outputPath, options.runtimeDir),
      sessionName,
      reviewMarkdown: await extractReviewMarkdown(outputPath, options.config.maxReviewChars),
      debugFiles,
      usage: await parseUsage(outputPath),
    };
  }
}

function buildClaudeEnv(
  config: ActionConfig,
  baseEnv: NodeJS.ProcessEnv,
  configDir: string,
  rawDebugDir: string | undefined,
): NodeJS.ProcessEnv {
  const env = { ...baseEnv };
  delete env.AGENTIC_REVIEW_API_KEY;
  env.ANTHROPIC_BASE_URL = config.modelBaseUrl;
  env.ANTHROPIC_MODEL = config.modelName;
  env.ANTHROPIC_DEFAULT_OPUS_MODEL = config.modelName;
  env.ANTHROPIC_DEFAULT_SONNET_MODEL = config.modelName;
  env.ANTHROPIC_DEFAULT_HAIKU_MODEL = config.smallModelName ?? config.modelName;
  env.CLAUDE_CODE_SUBAGENT_MODEL = config.smallModelName ?? config.modelName;
  env.CLAUDE_CONFIG_DIR = configDir;
  env.CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC = '1';
  env.DISABLE_TELEMETRY = '1';
  env.DO_NOT_TRACK = '1';
  if (config.disablePromptCaching) {
    env.DISABLE_PROMPT_CACHING = '1';
  }
  if (config.apiKeyMode === 'auth-token' || config.apiKeyMode === 'both') {
    env.ANTHROPIC_AUTH_TOKEN = config.apiKey;
  } else {
    delete env.ANTHROPIC_AUTH_TOKEN;
  }
  if (config.apiKeyMode === 'api-key' || config.apiKeyMode === 'both') {
    env.ANTHROPIC_API_KEY = config.apiKey;
  } else {
    delete env.ANTHROPIC_API_KEY;
  }
  if (rawDebugDir) {
    env.CLAUDE_CODE_ENABLE_TELEMETRY = '1';
    env.OTEL_LOGS_EXPORTER = 'console';
    env.OTEL_LOG_RAW_API_BODIES = `file:${rawDebugDir}`;
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

function runProcess(
  command: string,
  args: string[],
  options: {
    cwd: string;
    env: NodeJS.ProcessEnv;
    timeoutMs: number;
    stdin?: string;
  },
): Promise<{ exitCode: number | null; stdout: string; stderr: string }> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: [options.stdin === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let stdout = '';
    let stderr = '';
    const timeout = setTimeout(() => {
      child.kill('SIGTERM');
      reject(new Error(`process timed out after ${options.timeoutMs}ms: ${command}`));
    }, options.timeoutMs);
    child.stdout?.setEncoding('utf8');
    child.stderr?.setEncoding('utf8');
    child.stdout?.on('data', (chunk: string) => {
      stdout += chunk;
    });
    child.stderr?.on('data', (chunk: string) => {
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
    if (options.stdin !== undefined && child.stdin) {
      child.stdin.end(options.stdin);
    }
  });
}

export async function restoreRuntimeState(
  restoreDir: string | undefined,
  runtimeProvider: ActionConfig['runtimeProvider'],
  runtimeDir: string,
): Promise<void> {
  await rm(runtimeDir, { recursive: true, force: true });
  await mkdir(runtimeDir, { recursive: true });
  if (!restoreDir) {
    return;
  }
  const source = path.join(restoreDir, 'runtime', runtimeProvider);
  try {
    await cp(source, runtimeDir, { recursive: true, force: true });
  } catch (error) {
    if ((error as NodeJS.ErrnoException).code !== 'ENOENT') {
      throw error;
    }
  }
}

export async function copyRuntimeStateToBundle(
  runtimeDir: string,
  runtimeProvider: ActionConfig['runtimeProvider'],
  bundleDir: string,
): Promise<string[]> {
  const destination = path.join(bundleDir, 'runtime', runtimeProvider);
  await rm(destination, { recursive: true, force: true });
  await mkdir(destination, { recursive: true });
  await cp(runtimeDir, destination, { recursive: true, force: true });
  return await walkFiles(destination);
}

async function discoverSessionId(outputPath: string, runtimeDir: string): Promise<string> {
  const output = await readFile(outputPath, 'utf8').catch(() => '');
  for (const line of output.split(/\r?\n/)) {
    const parsed = safeParseJson(line);
    const sessionId = parsed ? findStringKey(parsed, ['session_id', 'sessionId']) : undefined;
    if (sessionId) {
      return sessionId;
    }
  }

  const jsonlFiles = (await walkFiles(runtimeDir)).filter((file) => file.endsWith('.jsonl'));
  const withStats = await Promise.all(
    jsonlFiles.map(async (file) => ({
      file,
      stat: await stat(file),
    })),
  );
  const newest = withStats.sort((left, right) => right.stat.mtimeMs - left.stat.mtimeMs)[0];
  if (!newest) {
    throw new Error('Unable to discover Claude session id from output or session files');
  }

  return path.basename(newest.file, '.jsonl');
}

export async function extractReviewMarkdown(outputPath: string, maxChars: number): Promise<string> {
  const output = await readFile(outputPath, 'utf8');
  let resultText: string | undefined;
  const assistantTexts: string[] = [];

  for (const line of output.split(/\r?\n/)) {
    const parsed = safeParseJson(line);
    if (!parsed || typeof parsed !== 'object') {
      continue;
    }
    const record = parsed as Record<string, unknown>;
    if (record.type === 'result' && typeof record.result === 'string' && record.result.trim()) {
      resultText = record.result;
    } else if (record.type === 'assistant') {
      assistantTexts.push(...findTextBlocks(record));
    }
  }

  const selected = (resultText ?? assistantTexts.join('\n\n')).trim();
  return truncateText(selected || 'No review text was emitted by the runtime.', maxChars);
}

export async function parseUsage(outputPath: string): Promise<RuntimeUsage | undefined> {
  const output = await readFile(outputPath, 'utf8').catch(() => '');
  let promptCacheHitTokens: number | undefined;
  let cacheReadInputTokens: number | undefined;
  let inputTokens: number | undefined;
  let outputTokens: number | undefined;

  for (const line of output.split(/\r?\n/)) {
    const parsed = safeParseJson(line);
    if (!parsed) {
      continue;
    }
    promptCacheHitTokens = preferLatestMeaningful(
      promptCacheHitTokens,
      findNumberKey(parsed, ['prompt_cache_hit_tokens', 'promptCacheHitTokens']),
    );
    cacheReadInputTokens = preferLatestMeaningful(
      cacheReadInputTokens,
      findNumberKey(parsed, ['cache_read_input_tokens', 'cacheReadInputTokens']),
    );
    inputTokens = preferLatestMeaningful(
      inputTokens,
      findNumberKey(parsed, ['input_tokens', 'inputTokens']),
    );
    outputTokens = preferLatestMeaningful(
      outputTokens,
      findNumberKey(parsed, ['output_tokens', 'outputTokens']),
    );
  }

  promptCacheHitTokens ??= cacheReadInputTokens;
  return promptCacheHitTokens === undefined &&
    cacheReadInputTokens === undefined &&
    inputTokens === undefined &&
    outputTokens === undefined
    ? undefined
    : {
        promptCacheHitTokens,
        cacheReadInputTokens,
        inputTokens,
        outputTokens,
      };
}

function preferLatestMeaningful(
  current: number | undefined,
  next: number | undefined,
): number | undefined {
  if (next === undefined) {
    return current;
  }
  if (current === undefined) {
    return next;
  }
  if (next === 0 && current !== 0) {
    return current;
  }
  return next;
}

function summarizeDiagnostic(value: string, env: NodeJS.ProcessEnv): string {
  let sanitized = value;
  for (const secret of [env.ANTHROPIC_API_KEY, env.ANTHROPIC_AUTH_TOKEN].filter(
    (item): item is string => Boolean(item && item.length > 4),
  )) {
    sanitized = sanitized.replaceAll(secret, '***');
  }
  sanitized = sanitized
    .replace(/Authorization:\s*Bearer\s+\S+/gi, 'Authorization: Bearer ***')
    .replace(/x-api-key:\s*\S+/gi, 'x-api-key: ***')
    .trim();

  for (const line of sanitized.split(/\r?\n/).reverse()) {
    const parsed = safeParseJson(line);
    if (parsed && typeof parsed === 'object' && 'type' in parsed) {
      const record = parsed as Record<string, unknown>;
      return JSON.stringify({
        type: record.type,
        subtype: record.subtype,
        stop_reason: record.stop_reason,
        terminal_reason: record.terminal_reason,
        session_id: record.session_id,
        errors: record.errors,
      });
    }
  }

  return sanitized.replace(/\s+/g, ' ').slice(-600) || 'no stderr/stdout captured';
}

function findTextBlocks(value: unknown): string[] {
  if (!value || typeof value !== 'object') {
    return [];
  }
  const record = value as Record<string, unknown>;
  const result: string[] = [];
  if (record.type === 'text' && typeof record.text === 'string' && record.text.trim()) {
    result.push(record.text);
  }
  for (const child of Object.values(record)) {
    if (Array.isArray(child)) {
      for (const item of child) {
        result.push(...findTextBlocks(item));
      }
    } else {
      result.push(...findTextBlocks(child));
    }
  }
  return result;
}

function safeParseJson(value: string): unknown | undefined {
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return undefined;
  }
}

function findStringKey(value: unknown, keys: string[]): string | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  for (const key of keys) {
    const candidate = record[key];
    if (typeof candidate === 'string' && candidate.length > 0) {
      return candidate;
    }
  }
  for (const child of Object.values(record)) {
    const candidate = findStringKey(child, keys);
    if (candidate) {
      return candidate;
    }
  }
  return undefined;
}

function findNumberKey(value: unknown, keys: string[]): number | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  for (const key of keys) {
    const candidate = record[key];
    if (typeof candidate === 'number') {
      return candidate;
    }
  }
  for (const child of Object.values(record)) {
    const candidate = findNumberKey(child, keys);
    if (candidate !== undefined) {
      return candidate;
    }
  }
  return undefined;
}

export function createRuntime(provider: ActionConfig['runtimeProvider']): ReviewRuntime {
  return provider === 'test' ? new TestRuntime() : new ClaudeCodeRuntime();
}

export function defaultTempDir(): string {
  return process.env.RUNNER_TEMP || os.tmpdir();
}
