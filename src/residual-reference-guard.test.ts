import { execFileSync } from 'node:child_process';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  residualReferenceDiscovery,
  residualReferenceRules,
} from './residual-reference-allowlist.js';
const scanExclusions = new Set([
  // W2 dist:check owns this derived file's exact bytes and bounded source graph.
  '.github/actions/agentic-pr-review/dist/index.js',
  'src/residual-reference-allowlist.ts',
  'src/residual-reference-guard.test.ts',
  // W15 owns these synthetic legacy-module references as detector test vectors.
  'src/root-shared-module-retirement.test.ts',
]);

function trackedFiles(root: string): string[] {
  return execFileSync('git', ['ls-files', '-z'], {
    cwd: root,
    encoding: 'utf8',
  })
    .split('\0')
    .filter((relative) => relative !== '')
    .sort();
}

function decodeTrackedText(bytes: Uint8Array): string | undefined {
  if (bytes.includes(0)) return undefined;
  try {
    return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  } catch {
    return undefined;
  }
}

function ownersFor(relative: string, line: string) {
  return residualReferenceRules.filter((rule) => rule.path.test(relative) && rule.term.test(line));
}

describe('R1 residual reference allowlist', () => {
  it('owns every executable, contract, and documentary residual exactly once', async () => {
    const root = process.cwd();
    const hitCounts = new Map(residualReferenceRules.map((rule) => [rule.id, 0]));
    const unowned: string[] = [];
    const multiplyOwned: string[] = [];

    for (const relative of trackedFiles(root)) {
      if (scanExclusions.has(relative)) continue;
      const bytes = await readFile(path.join(root, relative));
      const text = decodeTrackedText(bytes);
      if (text === undefined) continue;
      const lines = text.split(/\r?\n/u);
      for (const [index, line] of lines.entries()) {
        if (!residualReferenceDiscovery.test(line)) continue;
        const owners = ownersFor(relative, line);
        const location = `${relative}:${index + 1}`;
        if (owners.length === 0) unowned.push(location);
        if (owners.length > 1) multiplyOwned.push(`${location} => ${owners.map(({ id }) => id)}`);
        for (const owner of owners) hitCounts.set(owner.id, (hitCounts.get(owner.id) ?? 0) + 1);
      }
    }

    expect(unowned).toEqual([]);
    expect(multiplyOwned).toEqual([]);
    expect([...hitCounts.entries()].filter(([, count]) => count === 0)).toEqual([]);
  }, 15_000);

  it('requires complete lifecycle ownership metadata', () => {
    expect(new Set(residualReferenceRules.map(({ id }) => id)).size).toBe(
      residualReferenceRules.length,
    );
    for (const rule of residualReferenceRules) {
      expect(rule.owner).not.toBe('');
      expect(rule.interpretation).not.toBe('');
      if ('deletionGate' in rule) {
        expect(rule.currentConsumer).not.toBe('');
        expect(rule.deletionGate).not.toBe('');
        expect(['R2', 'R4']).toContain(rule.milestone);
      } else {
        expect(rule.status).not.toBe('');
        expect(rule.supersessionRule).not.toBe('');
        expect(['governing', 'historical', 'conformance']).toContain(rule.lifecycleClass);
      }
    }
  });

  it('discovers human-readable and executable Claude-specific spellings', () => {
    expect('The Claude Code CLI path has been removed.').toMatch(residualReferenceDiscovery);
    expect('const config = process.env.CLAUDE_CONFIG_DIR;').toMatch(residualReferenceDiscovery);
    expect("import '@anthropic-ai/claude-code';").toMatch(residualReferenceDiscovery);
  });

  it('discovers and singly owns the real tracked CLAUDE.md entrypoint', async () => {
    const relative = 'CLAUDE.md';
    const text = decodeTrackedText(await readFile(path.join(process.cwd(), relative)));
    expect(text).toBeDefined();

    const matches = (text ?? '')
      .split(/\r?\n/u)
      .filter((line) => residualReferenceDiscovery.test(line));
    expect(matches.length).toBeGreaterThan(0);
    for (const line of matches) {
      expect(ownersFor(relative, line).map(({ id }) => id)).toEqual(['RR-034']);
    }
  });
});
