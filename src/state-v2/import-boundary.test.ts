import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const FORBIDDEN_SPECIFIERS = ['node:fs', 'node:fs/promises', 'fs', 'fs/promises'] as const;

const FORBIDDEN_SIBLING_PATHS = [
  '../main.js',
  '../main',
  '../runtime.js',
  '../runtime',
  '../runtime-invocation',
  '../runtime-integration',
  '../state.js',
  '../state',
] as const;

// Extremely small AST-ish scanner: matches ES `import` / `export ... from`
// statements (ignoring block comments and line comments) so we do not confuse
// string mentions inside comments or unrelated code.
const IMPORT_FROM_REGEX =
  /(?:^|\n|;)\s*(?:import|export)\b[^"'\n;]*from\s*(?<quote>["'])(?<spec>[^"'\n]+)\k<quote>/g;

async function collectTsFiles(root: string): Promise<string[]> {
  const out: string[] = [];
  async function visit(dir: string): Promise<void> {
    const entries = await readdir(dir, { withFileTypes: true });
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) {
        await visit(full);
      } else if (entry.isFile()) {
        if (
          full.endsWith('.ts') &&
          !full.endsWith('.test.ts') &&
          !full.endsWith('.testhelper.ts') &&
          !full.endsWith('.d.ts')
        ) {
          out.push(full);
        }
      }
    }
  }
  await visit(root);
  return out;
}

function stripComments(text: string): string {
  // block comments
  let stripped = text.replace(/\/\*[\s\S]*?\*\//g, '');
  // line comments
  stripped = stripped.replace(/(^|[^:])\/\/[^\n]*/g, '$1');
  return stripped;
}

async function specifiersIn(file: string): Promise<string[]> {
  const raw = await readFile(file, 'utf8');
  const clean = stripComments(raw);
  const out: string[] = [];
  for (const match of clean.matchAll(IMPORT_FROM_REGEX)) {
    out.push(match.groups!.spec);
  }
  return out;
}

describe('state-v2 import boundary', () => {
  it('src/state-v2/**/*.ts does not import node:fs or the legacy state module', async () => {
    const root = path.resolve('src/state-v2');
    const files = await collectTsFiles(root);
    expect(files.length).toBeGreaterThan(0);
    const violations: string[] = [];
    for (const file of files) {
      const specs = await specifiersIn(file);
      for (const spec of specs) {
        if (FORBIDDEN_SPECIFIERS.includes(spec as (typeof FORBIDDEN_SPECIFIERS)[number])) {
          violations.push(`${file} imports forbidden module '${spec}'`);
        }
        for (const forbidden of FORBIDDEN_SIBLING_PATHS) {
          if (spec === forbidden) {
            violations.push(`${file} imports forbidden sibling '${spec}'`);
          }
        }
      }
    }
    expect(violations).toEqual([]);
  });

  it('src/canonical-json/**/*.ts does not import node:fs', async () => {
    const root = path.resolve('src/canonical-json');
    const files = await collectTsFiles(root);
    expect(files.length).toBeGreaterThan(0);
    const violations: string[] = [];
    for (const file of files) {
      const specs = await specifiersIn(file);
      for (const spec of specs) {
        if (FORBIDDEN_SPECIFIERS.includes(spec as (typeof FORBIDDEN_SPECIFIERS)[number])) {
          violations.push(`${file} imports forbidden module '${spec}'`);
        }
      }
    }
    expect(violations).toEqual([]);
  });

  it('src/state-v2 exists at expected path', async () => {
    const s = await stat(path.resolve('src/state-v2'));
    expect(s.isDirectory()).toBe(true);
  });
});
