import { spawn } from 'node:child_process';
import { cp, mkdir, readFile, rm, stat, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import {
  type ActionConfig,
  type Phase,
  type RestoredState,
  type RuntimeLineageTotals,
  type RuntimeResult,
  type RuntimeUsage,
  type RuntimeUsageTotals,
  type ToolMode,
  type UsageBudgetLimits,
  type UsageBudgetStatus,
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

    const lineageZeroUsage: RuntimeUsageTotals = {
      inputTokens: 0,
      cacheReadInputTokens: 0,
      cacheCreationInputTokens: 0,
      outputTokens: 0,
    };
    return {
      sessionId,
      sessionName,
      reviewMarkdown,
      debugFiles: [],
      toolMode: options.config.toolMode,
      allowedTools: [],
      observedTurns: 0,
      observedTurnSource: 'not_applicable',
      usage: null,
      usageBudgetStatus: {
        status: 'not_applicable',
        limits: options.config.usageBudgetLimits,
        usageRecordsObserved: 0,
      },
      lineageTotals: {
        observedTurns: 0,
        usage: lineageZeroUsage,
        source: 'current_run_only',
        partial: false,
      },
    };
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

    const allowedTools = allowedToolsForMode(options.config.toolMode);
    const usageTracker = new UsageTracker(options.config.usageBudgetLimits);
    const turnIds = new Set<string>();
    const args = buildClaudeArgs({
      config: options.config,
      phase: options.phase,
      restoredState: options.restoredState,
      sessionName,
      allowedTools,
    });

    const env = buildClaudeEnv(options.config, process.env, configDir, rawDebugDir);
    const result = await runProcess(cliPath, args, {
      cwd: options.workspace,
      env,
      stdin: options.prompt,
      timeoutMs: 20 * 60 * 1000,
      onStdoutLine: (line) => {
        usageTracker.observeLine(line);
        const parsed = safeParseJson(line);
        if (parsed && typeof parsed === 'object') {
          const record = parsed as Record<string, unknown>;
          if (record.type === 'assistant') {
            const msg = record.message;
            if (msg && typeof msg === 'object') {
              const msgObj = msg as Record<string, unknown>;
              const id = msgObj.id;
              if (typeof id === 'string') {
                turnIds.add(id);
              }
            }
          }
        }
      },
    });
    await writeFile(outputPath, result.stdout, 'utf8');

    if (result.streamError) {
      throw result.streamError;
    }

    if (result.exitCode !== 0) {
      throw new Error(
        `claude-code-cli exited with ${result.exitCode}: ${summarizeDiagnostic(
          result.stderr || result.stdout,
          env,
        )}`,
      );
    }

    const usage = usageTracker.getUsage();
    const usageBudgetStatus = usageTracker.getStatus();
    if (usageBudgetStatus.status === 'exceeded') {
      throw new UsageBudgetExceededError(usageBudgetStatus);
    }
    if (usageBudgetStatus.status === 'within_limit' && usageTracker.recordsObserved === 0) {
      throw new Error(
        'usage_budget_exceeded: usage budgets are configured but claude-code-cli did not expose usage records',
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

    const observedTurns = turnIds.size > 0 ? turnIds.size : null;
    const observedTurnSource: RuntimeResult['observedTurnSource'] =
      turnIds.size > 0 ? 'unique_assistant_message_ids' : 'unavailable';
    const lineageTotals = computeLineageTotals(options.restoredState, observedTurns, usage);

    return {
      sessionId: await discoverSessionId(outputPath, options.runtimeDir),
      sessionName,
      reviewMarkdown: await extractReviewMarkdown(outputPath, options.config.maxReviewChars),
      debugFiles,
      toolMode: options.config.toolMode,
      allowedTools,
      observedTurns,
      observedTurnSource,
      usage,
      usageBudgetStatus,
      lineageTotals,
    };
  }
}

export function computeLineageTotals(
  restoredState: RestoredState | undefined,
  currentObservedTurns: number | null,
  currentUsage: RuntimeUsage | null,
): RuntimeLineageTotals {
  const zeroTotals: RuntimeUsageTotals = {
    inputTokens: 0,
    cacheReadInputTokens: 0,
    cacheCreationInputTokens: 0,
    outputTokens: 0,
  };

  const priorLineage = restoredState?.lineageTotals;

  // Bootstrap without prior lineage: current run only
  if (!priorLineage) {
    // Check for legacy manifest (restored state exists but no lineage data)
    if (restoredState) {
      return {
        observedTurns: currentObservedTurns,
        usage: currentUsage
          ? {
              inputTokens: currentUsage.inputTokens,
              cacheReadInputTokens: currentUsage.cacheReadInputTokens,
              cacheCreationInputTokens: currentUsage.cacheCreationInputTokens,
              outputTokens: currentUsage.outputTokens,
            }
          : zeroTotals,
        source: 'legacy_manifest_fallback',
        partial: true,
      };
    }
    return {
      observedTurns: currentObservedTurns,
      usage: currentUsage
        ? {
            inputTokens: currentUsage.inputTokens,
            cacheReadInputTokens: currentUsage.cacheReadInputTokens,
            cacheCreationInputTokens: currentUsage.cacheCreationInputTokens,
            outputTokens: currentUsage.outputTokens,
          }
        : zeroTotals,
      source: 'current_run_only',
      partial: false,
    };
  }

  // Prior lineage exists: add current to prior
  const curTokens =
    currentUsage ??
    ({
      inputTokens: 0,
      cacheReadInputTokens: 0,
      cacheCreationInputTokens: 0,
      outputTokens: 0,
    } as RuntimeUsage);

  return {
    observedTurns: (priorLineage.observedTurns ?? 0) + (currentObservedTurns ?? 0),
    usage: {
      inputTokens: priorLineage.usage.inputTokens + curTokens.inputTokens,
      cacheReadInputTokens:
        priorLineage.usage.cacheReadInputTokens + curTokens.cacheReadInputTokens,
      cacheCreationInputTokens:
        priorLineage.usage.cacheCreationInputTokens + curTokens.cacheCreationInputTokens,
      outputTokens: priorLineage.usage.outputTokens + curTokens.outputTokens,
    },
    source: 'restored_manifest_plus_current_run',
    partial: priorLineage.partial,
  };
}

export function allowedToolsForMode(toolMode: ToolMode): string[] {
  return toolMode === 'readonly' ? ['Read', 'Glob', 'Grep'] : [];
}

export function buildClaudeArgs(input: {
  config: Pick<ActionConfig, 'modelName' | 'claudeMaxTurns' | 'toolMode'>;
  phase: Phase;
  restoredState?: RestoredState;
  sessionName: string;
  allowedTools: string[];
}): string[] {
  const args = [
    '-p',
    '--output-format',
    'stream-json',
    '--verbose',
    '--max-turns',
    String(input.config.claudeMaxTurns),
    '--model',
    input.config.modelName!,
    '--exclude-dynamic-system-prompt-sections',
    '--disable-slash-commands',
    '--strict-mcp-config',
    '--tools',
    input.allowedTools.join(','),
  ];

  if (input.phase === 'incremental') {
    const sessionId = input.restoredState?.sessionId;
    if (!sessionId) {
      throw new Error('incremental phase requires a restored Claude session id');
    }
    args.push('--resume', sessionId);
  } else {
    args.push('--name', input.sessionName);
  }

  args.push('Run the PR review instructions from stdin. Do not edit files.');
  return args;
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
    onStdoutLine?: (line: string) => void;
  },
): Promise<{ exitCode: number | null; stdout: string; stderr: string; streamError?: Error }> {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, {
      cwd: options.cwd,
      env: options.env,
      stdio: [options.stdin === undefined ? 'ignore' : 'pipe', 'pipe', 'pipe'],
      windowsHide: true,
    });
    let stdout = '';
    let stderr = '';
    let pendingStdoutLine = '';
    let streamError: Error | undefined;
    const timeout = setTimeout(() => {
      child.kill('SIGTERM');
      reject(new Error(`process timed out after ${options.timeoutMs}ms: ${command}`));
    }, options.timeoutMs);
    child.stdout?.setEncoding('utf8');
    child.stderr?.setEncoding('utf8');
    child.stdout?.on('data', (chunk: string) => {
      stdout += chunk;
      if (options.onStdoutLine && !streamError) {
        pendingStdoutLine += chunk;
        const lines = pendingStdoutLine.split(/\r?\n/);
        pendingStdoutLine = lines.pop() ?? '';
        for (const line of lines) {
          try {
            options.onStdoutLine(line);
          } catch (error) {
            streamError = error instanceof Error ? error : new Error(String(error));
            child.kill('SIGTERM');
            break;
          }
        }
      }
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
      if (options.onStdoutLine && pendingStdoutLine && !streamError) {
        try {
          options.onStdoutLine(pendingStdoutLine);
        } catch (error) {
          streamError = error instanceof Error ? error : new Error(String(error));
        }
      }
      resolve({ exitCode, stdout, stderr, streamError });
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

export async function parseUsage(outputPath: string): Promise<RuntimeUsage | null> {
  const output = await readFile(outputPath, 'utf8').catch(() => '');
  const tracker = new UsageTracker({
    maxUncachedInputTokens: 0,
    maxCachedInputTokens: 0,
    maxOutputTokens: 0,
  });
  for (const line of output.split(/\r?\n/)) {
    tracker.observeLine(line);
  }
  return tracker.getUsage();
}

interface UsageRecord {
  inputTokens?: number;
  cachedInputTokens?: number;
  cacheReadInputTokens?: number;
  cacheCreationInputTokens?: number;
  outputTokens?: number;
  cumulative: boolean;
}

export class UsageBudgetExceededError extends Error {
  constructor(readonly status: UsageBudgetStatus) {
    const exceeded = status.exceeded;
    super(
      exceeded
        ? `usage_budget_exceeded: ${exceeded.category} tokens ${exceeded.observed} exceeded limit ${exceeded.limit}`
        : 'usage_budget_exceeded',
    );
  }
}

export class UsageTracker {
  private deltaUsage: Required<Omit<RuntimeUsage, 'recordsObserved'>> = {
    inputTokens: 0,
    cacheReadInputTokens: 0,
    cacheCreationInputTokens: 0,
    outputTokens: 0,
  };
  private cumulativeUsage: Required<Omit<RuntimeUsage, 'recordsObserved'>> | undefined;
  private exceeded: UsageBudgetStatus['exceeded'];
  recordsObserved = 0;

  constructor(private readonly limits: UsageBudgetLimits) {}

  observeLine(line: string): void {
    const parsed = safeParseJson(line);
    if (!parsed) {
      return;
    }
    const record = extractUsageRecord(parsed);
    if (!record) {
      return;
    }
    this.recordsObserved += 1;
    if (record.cumulative) {
      this.cumulativeUsage = mergeCumulativeUsage(this.cumulativeUsage, record);
    } else {
      this.deltaUsage.inputTokens += record.inputTokens ?? 0;
      this.deltaUsage.cacheReadInputTokens += record.cacheReadInputTokens ?? 0;
      this.deltaUsage.cacheCreationInputTokens += record.cacheCreationInputTokens ?? 0;
      this.deltaUsage.outputTokens += record.outputTokens ?? 0;
    }
    const exceeded = this.findExceededBudget();
    if (exceeded) {
      this.exceeded = exceeded;
      throw new UsageBudgetExceededError(this.getStatus());
    }
  }

  getUsage(): RuntimeUsage | null {
    if (this.recordsObserved === 0) {
      return null;
    }
    const source = this.cumulativeUsage ?? this.deltaUsage;
    return {
      inputTokens: source.inputTokens,
      cacheReadInputTokens: source.cacheReadInputTokens,
      cacheCreationInputTokens: source.cacheCreationInputTokens,
      outputTokens: source.outputTokens,
      recordsObserved: this.recordsObserved,
    };
  }

  getStatus(): UsageBudgetStatus {
    if (!hasUsageBudget(this.limits)) {
      return {
        status: 'disabled',
        limits: this.limits,
        usageRecordsObserved: this.recordsObserved,
      };
    }
    return {
      status: this.exceeded ? 'exceeded' : 'within_limit',
      limits: this.limits,
      usageRecordsObserved: this.recordsObserved,
      exceeded: this.exceeded,
    };
  }

  private findExceededBudget(): UsageBudgetStatus['exceeded'] | undefined {
    const usage = this.getUsage();
    if (!usage) {
      return undefined;
    }
    const checks: Array<UsageBudgetStatus['exceeded']> = [
      {
        category: 'uncached_input',
        limit: this.limits.maxUncachedInputTokens,
        observed: usage.inputTokens,
      },
      {
        category: 'cached_input',
        limit: this.limits.maxCachedInputTokens,
        observed: usage.cacheReadInputTokens,
      },
      {
        category: 'output',
        limit: this.limits.maxOutputTokens,
        observed: usage.outputTokens,
      },
    ];
    return checks.find((check) => check && check.limit > 0 && check.observed > check.limit);
  }
}

function hasUsageBudget(limits: UsageBudgetLimits): boolean {
  return (
    limits.maxUncachedInputTokens > 0 ||
    limits.maxCachedInputTokens > 0 ||
    limits.maxOutputTokens > 0
  );
}

function mergeCumulativeUsage(
  current: Required<Omit<RuntimeUsage, 'recordsObserved'>> | undefined,
  record: UsageRecord,
): Required<Omit<RuntimeUsage, 'recordsObserved'>> {
  return {
    inputTokens: record.inputTokens ?? current?.inputTokens ?? 0,
    cacheReadInputTokens: record.cacheReadInputTokens ?? current?.cacheReadInputTokens ?? 0,
    cacheCreationInputTokens:
      record.cacheCreationInputTokens ?? current?.cacheCreationInputTokens ?? 0,
    outputTokens: record.outputTokens ?? current?.outputTokens ?? 0,
  };
}

function extractUsageRecord(value: unknown): UsageRecord | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  const usageRoot = findUsageRoot(record) ?? record;
  const inputTokens = findNumberKey(usageRoot, ['input_tokens', 'inputTokens']);
  const cacheReadInputTokens = findNumberKey(usageRoot, [
    'cache_read_input_tokens',
    'cacheReadInputTokens',
  ]);
  const promptCacheHitTokens = findNumberKey(usageRoot, [
    'prompt_cache_hit_tokens',
    'promptCacheHitTokens',
  ]);
  const cachedInputTokens = maxDefined(cacheReadInputTokens, promptCacheHitTokens);
  const cacheCreationInputTokens = findNumberKey(usageRoot, [
    'cache_creation_input_tokens',
    'cacheCreationInputTokens',
  ]);
  const outputTokens = findNumberKey(usageRoot, ['output_tokens', 'outputTokens']);

  if (
    inputTokens === undefined &&
    cachedInputTokens === undefined &&
    cacheCreationInputTokens === undefined &&
    outputTokens === undefined
  ) {
    return undefined;
  }

  return {
    inputTokens,
    cachedInputTokens,
    cacheReadInputTokens: cachedInputTokens,
    cacheCreationInputTokens,
    outputTokens,
    cumulative: record.type === 'result' || record.subtype === 'success',
  };
}

function findUsageRoot(value: unknown): unknown | undefined {
  if (!value || typeof value !== 'object') {
    return undefined;
  }
  const record = value as Record<string, unknown>;
  if (record.usage && typeof record.usage === 'object') {
    return record.usage;
  }
  for (const child of Object.values(record)) {
    if (Array.isArray(child)) {
      for (const item of child) {
        const candidate = findUsageRoot(item);
        if (candidate) {
          return candidate;
        }
      }
    } else {
      const candidate = findUsageRoot(child);
      if (candidate) {
        return candidate;
      }
    }
  }
  return undefined;
}

function maxDefined(...values: Array<number | undefined>): number | undefined {
  const defined = values.filter((value): value is number => value !== undefined);
  return defined.length === 0 ? undefined : Math.max(...defined);
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
