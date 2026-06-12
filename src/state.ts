import { readFile } from 'node:fs/promises';
import path from 'node:path';
import {
  type ActionConfig,
  type LoadedBlock,
  type Phase,
  type RestoredState,
  type ReviewTarget,
  type RuntimeResult,
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
  sessionId: string;
  reviewedHeadSha?: string;
  promptSha256: string;
  generatedAt: string;
  contextBlocks: Array<Pick<LoadedBlock, 'name' | 'source' | 'bytes' | 'sha256'>>;
}

export async function readRestoredState(root: string): Promise<RestoredState> {
  const manifestPath = path.join(root, 'manifest.json');
  const manifest = await readJsonFile<StateManifest>(manifestPath);
  if (manifest.workflow !== 'agentic-pr-review') {
    throw new Error('restored state manifest has unexpected workflow');
  }
  return {
    stateKey: manifest.stateKey,
    sessionId: manifest.sessionId,
    runtimeProvider: manifest.runtimeProvider,
    reviewedHeadSha: manifest.reviewedHeadSha,
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
}): Promise<string[]> {
  await ensureDir(options.bundleDir);
  const manifest: StateManifest = {
    version: 1,
    workflow: 'agentic-pr-review',
    stateKey: options.stateKey,
    phase: options.phase,
    runtimeProvider: options.config.runtimeProvider,
    sessionId: options.runtimeResult.sessionId,
    reviewedHeadSha: options.target.headSha,
    promptSha256: options.promptSha256,
    generatedAt: new Date().toISOString(),
    contextBlocks: options.blocks.map((block) => ({
      name: block.name,
      source: block.source,
      bytes: block.bytes,
      sha256: block.sha256,
    })),
  };

  await writeJsonFile(path.join(options.bundleDir, 'manifest.json'), manifest);
  await writeTextFile(
    path.join(options.bundleDir, 'review.md'),
    options.runtimeResult.reviewMarkdown,
  );
  await sanitizeStateBundle(options.bundleDir, options.config.apiKey);
  return await walkFiles(options.bundleDir);
}

export async function sanitizeStateBundle(
  bundleDir: string,
  apiKey: string | undefined,
): Promise<void> {
  const files = await walkFiles(bundleDir);
  for (const file of files) {
    const rel = relativePosix(bundleDir, file).toLowerCase();
    if (rel.includes('raw') || rel.includes('debug')) {
      throw new Error(`normal state artifact cannot include diagnostic file: ${rel}`);
    }
    const content = await readFile(file, 'utf8');
    if (apiKey && apiKey.length >= 8 && content.includes(apiKey)) {
      throw new Error(`normal state artifact contains a configured secret: ${rel}`);
    }
    if (content.includes('ANTHROPIC_API_KEY') || content.includes('ANTHROPIC_AUTH_TOKEN')) {
      throw new Error(`normal state artifact contains runtime credential names: ${rel}`);
    }
  }
}

export function stateArtifactName(stateKey: string): string {
  return `agentic-pr-review-state-${stateKey}`;
}

export function debugArtifactName(stateKey: string): string {
  return `agentic-pr-review-raw-debug-${stateKey}`;
}
