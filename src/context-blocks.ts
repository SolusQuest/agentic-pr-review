import { type ActionConfig, type LoadedBlock, type Phase } from './types.js';
import { assertWithinLimit, readTextFile, resolveWorkspacePath, sha256 } from './utils.js';

async function loadBlock(
  workspace: string,
  name: LoadedBlock['name'],
  inputText: string | undefined,
  inputPath: string | undefined,
  maxContextChars: number,
): Promise<LoadedBlock | undefined> {
  if (!inputText && !inputPath) {
    return undefined;
  }
  const source = inputPath ? 'path' : 'input';
  const text = inputPath
    ? await readTextFile(resolveWorkspacePath(workspace, inputPath))
    : (inputText ?? '');
  assertWithinLimit(text, maxContextChars, name);
  return {
    name,
    source,
    text,
    bytes: Buffer.byteLength(text, 'utf8'),
    sha256: sha256(text),
  };
}

export async function loadContextBlocks(
  config: ActionConfig,
  workspace: string,
  phase: Phase,
): Promise<LoadedBlock[]> {
  const blocks: LoadedBlock[] = [];
  const instructions = await loadBlock(
    workspace,
    'instructions',
    config.instructions,
    config.instructionsPath,
    config.maxContextChars,
  );
  if (instructions) {
    blocks.push(instructions);
  }

  if (phase === 'bootstrap') {
    const bootstrapContext = await loadBlock(
      workspace,
      'bootstrap_context',
      config.bootstrapContext,
      config.bootstrapContextPath,
      config.maxContextChars,
    );
    if (bootstrapContext) {
      blocks.push(bootstrapContext);
    }
  } else {
    const incrementalContext = await loadBlock(
      workspace,
      'incremental_context',
      config.incrementalContext,
      config.incrementalContextPath,
      config.maxContextChars,
    );
    if (incrementalContext) {
      blocks.push(incrementalContext);
    }
  }

  return blocks;
}
