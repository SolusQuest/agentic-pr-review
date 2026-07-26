import { readdir, readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { residualReferenceRules } from './residual-reference-allowlist.js';

const discovery =
  /ClaudeCodeRuntime|ANTHROPIC_|claude_code|claude-code-cli|--resume|stream-json|runtime_backend|runtime_provider|live_provider|legacy/iu;
const metadataPaths = new Set([
  'src/residual-reference-allowlist.ts',
  'src/residual-reference-guard.test.ts',
]);

async function sourceFiles(root: string): Promise<string[]> {
  const result: string[] = [];
  const visit = async (relative: string): Promise<void> => {
    const absolute = path.join(root, relative);
    for (const entry of await readdir(absolute, { withFileTypes: true })) {
      const child = path.posix.join(relative.replaceAll('\\', '/'), entry.name);
      if (entry.isDirectory()) await visit(child);
      else result.push(child);
    }
  };
  for (const directory of ['src', 'scripts', '.github', 'protocol']) {
    await visit(directory);
  }
  return result;
}

describe('R1 residual reference allowlist', () => {
  it('owns every residual match exactly once and every temporary entry still matches', async () => {
    const root = process.cwd();
    const hitCounts = new Map(residualReferenceRules.map((rule) => [rule.id, 0]));
    const unowned: string[] = [];
    const multiplyOwned: string[] = [];

    for (const relative of await sourceFiles(root)) {
      if (metadataPaths.has(relative)) continue;
      const lines = (await readFile(path.join(root, relative), 'utf8')).split(/\r?\n/u);
      for (const [index, line] of lines.entries()) {
        if (!discovery.test(line)) continue;
        const owners = residualReferenceRules.filter(
          (rule) => rule.path.test(relative) && rule.term.test(line),
        );
        const location = `${relative}:${index + 1}`;
        if (owners.length === 0) unowned.push(location);
        if (owners.length > 1) multiplyOwned.push(`${location} => ${owners.map(({ id }) => id)}`);
        for (const owner of owners) hitCounts.set(owner.id, (hitCounts.get(owner.id) ?? 0) + 1);
      }
    }

    expect(unowned).toEqual([]);
    expect(multiplyOwned).toEqual([]);
    expect([...hitCounts.entries()].filter(([, count]) => count === 0)).toEqual([]);
  });

  it('requires complete lifecycle ownership metadata', () => {
    expect(new Set(residualReferenceRules.map(({ id }) => id)).size).toBe(
      residualReferenceRules.length,
    );
    for (const rule of residualReferenceRules) {
      expect(rule.currentConsumer).not.toBe('');
      expect(rule.owner).not.toBe('');
      expect(rule.interpretation).not.toBe('');
      expect(rule.deletionGate).not.toBe('');
      expect(['R2', 'R4']).toContain(rule.milestone);
    }
  });
});
