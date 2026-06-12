import { type ChangedFile, type LoadedBlock, type Phase, type ReviewTarget } from './types.js';
import { sha256, truncateText } from './utils.js';

export interface BuiltPrompt {
  text: string;
  sha256: string;
}

function formatFile(file: ChangedFile): string {
  const patch = file.patch ? `\n\n${file.patch}` : '\n\n[patch unavailable]';
  return `### ${file.filename}\nStatus: ${file.status}${patch}`;
}

export function buildReviewPrompt(
  target: ReviewTarget,
  phase: Phase,
  blocks: LoadedBlock[],
  maxPatchChars: number,
): BuiltPrompt {
  const sections = [
    '# Agentic PR Review Task',
    '',
    `Phase: ${phase}`,
    `Target mode: ${target.mode}`,
    target.prNumber ? `Pull request: #${target.prNumber}` : undefined,
    `Title: ${target.title}`,
    `Base SHA: ${target.baseSha}`,
    `Head SHA: ${target.headSha}`,
    '',
    'Review the supplied pull request context. Return concise Markdown with actionable findings first. If there are no findings, say so clearly and mention residual test or validation risk.',
  ].filter(Boolean);

  for (const block of blocks) {
    sections.push('', `## ${block.name}`, block.text);
  }

  const files = target.changedFiles.map(formatFile).join('\n\n');
  sections.push('', '## Changed Files', truncateText(files, maxPatchChars));

  const text = `${sections.join('\n')}\n`;
  return { text, sha256: sha256(text) };
}
