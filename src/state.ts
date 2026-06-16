import { readFile, rm, stat, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { copyRuntimeStateToBundle } from './runtime.js';
import {
  type ActionConfig,
  type LoadedBlock,
  type Phase,
  type RestoredState,
  type ReviewTarget,
  type RuntimeResult,
  type RuntimeUsage,
} from './types.js';
import {
  ensureDir,
  readJsonFile,
  relativePosix,
  walkFiles,
  writeJsonFile,
  writeTextFile,
} from './utils.js';

interface StateManifest {
  version: 1;
  workflow: 'agentic-pr-review';
  stateKey: string;
  phase: Phase;
  runtimeProvider: ActionConfig['runtimeProvider'];
  toolMode: ActionConfig['toolMode'];
  allowedTools: string[];
  sessionId: string;
  sessionName: string;
  reviewedHeadSha?: string;
  promptSha256: string;
  createdAt: string;
  updatedAt: string;
  usage?: RuntimeUsage;
  usageBudgetStatus: RuntimeResult['usageBudgetStatus'];
  contextBlocks: Array<Pick<LoadedBlock, 'name' | 'source' | 'bytes' | 'sha256'>>;
  target: {
    mode: ReviewTarget['mode'];
    prNumber?: number;
    baseSha: string;
    headSha: string;
    changedFiles: number;
  };
}

const SECRET_FILE_PATTERN =
  /(^|[\\/])(\.env|credentials?|secrets?|tokens?|settings\.local)(\.|[\\/]|$)/i;
const SECRET_CONTENT_PATTERN = /(ghp_|github_pat_|sk-[a-zA-Z0-9]|authorization:\s*bearer)/i;
const AUTH_HEADER_KEYS = new Set(['authorization', 'x-api-key', 'x-api-token']);
const HIGH_RISK_TOKEN_PATTERN = /(ghp_|github_pat_|sk-[a-zA-Z0-9])\S*/g;

export async function readRestoredState(root: string): Promise<RestoredState> {
  const manifestPath = path.join(root, 'manifest.json');
  const manifest = await readJsonFile<StateManifest>(manifestPath);
  if (manifest.workflow !== 'agentic-pr-review') {
    throw new Error('restored state manifest has unexpected workflow');
  }
  return {
    stateKey: manifest.stateKey,
    sessionId: manifest.sessionId,
    sessionName: manifest.sessionName ?? `agentic-pr-review-${manifest.stateKey}`,
    runtimeProvider: manifest.runtimeProvider,
    reviewedHeadSha: manifest.reviewedHeadSha,
    createdAt: manifest.createdAt,
    usage: manifest.usage,
    manifestPath,
  };
}

export async function writeStateBundle(options: {
  bundleDir: string;
  config: ActionConfig;
  target: ReviewTarget;
  stateKey: string;
  phase: Phase;
  promptSha256: string;
  blocks: LoadedBlock[];
  runtimeResult: RuntimeResult;
  runtimeDir: string;
  createdAt?: string;
}): Promise<string[]> {
  await rm(options.bundleDir, { recursive: true, force: true });
  await ensureDir(options.bundleDir);
  await copyRuntimeStateToBundle(
    options.runtimeDir,
    options.config.runtimeProvider,
    options.bundleDir,
  );

  await sanitizeRuntimeFiles(path.join(options.bundleDir, 'runtime'), knownSecrets(options.config));

  const now = new Date().toISOString();
  const manifest: StateManifest = {
    version: 1,
    workflow: 'agentic-pr-review',
    stateKey: options.stateKey,
    phase: options.phase,
    runtimeProvider: options.config.runtimeProvider,
    toolMode: options.runtimeResult.toolMode,
    allowedTools: options.runtimeResult.allowedTools,
    sessionId: options.runtimeResult.sessionId,
    sessionName: options.runtimeResult.sessionName,
    reviewedHeadSha: options.target.headSha,
    promptSha256: options.promptSha256,
    createdAt: options.createdAt ?? now,
    updatedAt: now,
    usage: options.runtimeResult.usage,
    usageBudgetStatus: options.runtimeResult.usageBudgetStatus,
    contextBlocks: options.blocks.map((block) => ({
      name: block.name,
      source: block.source,
      bytes: block.bytes,
      sha256: block.sha256,
    })),
    target: {
      mode: options.target.mode,
      prNumber: options.target.prNumber,
      baseSha: options.target.baseSha,
      headSha: options.target.headSha,
      changedFiles: options.target.changedFiles.length,
    },
  };

  await writeJsonFile(path.join(options.bundleDir, 'manifest.json'), manifest);
  await writeTextFile(
    path.join(options.bundleDir, 'review.md'),
    options.runtimeResult.reviewMarkdown,
  );
  await sanitizeStateBundle(options.bundleDir, options.config);
  return await walkFiles(options.bundleDir);
}

export async function sanitizeStateBundle(
  bundleDir: string,
  config: Pick<ActionConfig, 'apiKey' | 'githubToken'>,
): Promise<void> {
  const secrets = knownSecrets(config);
  const files = await walkFiles(bundleDir);
  for (const file of files) {
    const rel = relativePosix(bundleDir, file);
    const lower = rel.toLowerCase();
    const isRuntimeFile = lower.startsWith('runtime/');
    if (lower.includes('raw') || lower.includes('debug')) {
      throw new Error(`normal state artifact cannot include diagnostic file: ${rel}`);
    }
    if (!isRuntimeFile && SECRET_FILE_PATTERN.test(rel)) {
      throw new Error(`normal state artifact cannot include sensitive-looking file: ${rel}`);
    }

    const fileStat = await stat(file);
    if (fileStat.size > 1024 * 1024) {
      throw new Error(`normal state artifact file is too large to scan safely: ${rel}`);
    }

    const content = await readFile(file, 'utf8').catch(() => '');
    const contentWithoutRedactions = content.replaceAll('***REDACTED***', '');
    if (!isRuntimeFile && SECRET_CONTENT_PATTERN.test(contentWithoutRedactions)) {
      throw new Error(`normal state artifact contains sensitive-looking content: ${rel}`);
    }
    if (hasUnredactedAuthHeader(content)) {
      throw new Error(`normal state artifact contains unredacted auth header: ${rel}`);
    }
    for (const secret of secrets) {
      if (content.includes(secret)) {
        throw new Error(`normal state artifact contains a configured secret: ${rel}`);
      }
    }
  }
}

async function sanitizeRuntimeFiles(runtimeRoot: string, secrets: string[]): Promise<void> {
  const files = await walkFiles(runtimeRoot);
  for (const file of files) {
    const fileStat = await stat(file);
    if (fileStat.size > 1024 * 1024) {
      continue;
    }
    const content = await readFile(file, 'utf8').catch(() => '');
    if (!content) {
      continue;
    }
    const sanitized = content
      .split(/\r?\n/)
      .map((line) => sanitizeLine(line, secrets))
      .join('\n');
    if (sanitized !== content) {
      await writeFile(file, sanitized, 'utf8');
    }
  }
}

function sanitizeLine(line: string, secrets: string[]): string {
  if (!line.trim()) {
    return line;
  }
  const parsed = safeParseJson(line);
  if (parsed !== undefined) {
    return JSON.stringify(sanitizeJsonValue(parsed, secrets));
  }
  return sanitizeText(line, secrets);
}

function sanitizeJsonValue(value: unknown, secrets: string[]): unknown {
  if (typeof value === 'string') {
    return sanitizeText(value, secrets);
  }
  if (Array.isArray(value)) {
    return value.map((item) => sanitizeJsonValue(item, secrets));
  }
  if (value && typeof value === 'object') {
    const result: Record<string, unknown> = {};
    for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
      if (AUTH_HEADER_KEYS.has(key.toLowerCase()) && typeof child === 'string') {
        result[key] = '***REDACTED***';
      } else {
        result[key] = sanitizeJsonValue(child, secrets);
      }
    }
    return result;
  }
  return value;
}

function sanitizeText(value: string, secrets: string[]): string {
  let result = value;
  for (const secret of secrets) {
    result = result.replaceAll(secret, '***REDACTED***');
  }
  return result
    .replace(HIGH_RISK_TOKEN_PATTERN, '***REDACTED***')
    .replace(
      /(Authorization|x-api-key|x-api-token)(["\s]*:?\s*(?:Bearer\s+)?)(?!\*\*\*)\S+/gi,
      '$1$2***REDACTED***',
    );
}

function hasUnredactedAuthHeader(content: string): boolean {
  for (const line of content.split(/\r?\n/)) {
    const parsed = safeParseJson(line);
    if (parsed !== undefined && hasUnredactedJsonAuthHeader(parsed)) {
      return true;
    }
    const sanitized = sanitizeText(line, []);
    if (sanitized !== line && !line.includes('***REDACTED***')) {
      return true;
    }
  }
  return false;
}

function hasUnredactedJsonAuthHeader(value: unknown): boolean {
  if (!value || typeof value !== 'object') {
    return false;
  }
  if (Array.isArray(value)) {
    return value.some(hasUnredactedJsonAuthHeader);
  }
  for (const [key, child] of Object.entries(value as Record<string, unknown>)) {
    if (AUTH_HEADER_KEYS.has(key.toLowerCase())) {
      if (typeof child === 'string' && child !== '***REDACTED***') {
        return true;
      }
    } else if (hasUnredactedJsonAuthHeader(child)) {
      return true;
    }
  }
  return false;
}

function knownSecrets(config: Pick<ActionConfig, 'apiKey' | 'githubToken'>): string[] {
  return [config.apiKey, config.githubToken].filter((value): value is string =>
    Boolean(value && value.length >= 8),
  );
}

function safeParseJson(value: string): unknown | undefined {
  try {
    return JSON.parse(value) as unknown;
  } catch {
    return undefined;
  }
}

export function stateArtifactName(stateKey: string): string {
  return `agentic-pr-review-state-${stateKey}`;
}

export function debugArtifactName(stateKey: string): string {
  return `agentic-pr-review-raw-debug-${stateKey}`;
}
