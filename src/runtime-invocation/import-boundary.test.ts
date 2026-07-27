import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import ts from 'typescript';

async function productionFiles(root: string): Promise<string[]> {
  return (await readdir(root))
    .filter((name) => name.endsWith('.ts') && !name.includes('.test') && !name.endsWith('.d.ts'))
    .map((name) => path.join(root, name));
}

function moduleSpecifiers(source: ts.SourceFile): string[] {
  const result: string[] = [];
  const visit = (node: ts.Node): void => {
    if (
      (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      result.push(node.moduleSpecifier.text);
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return result;
}

describe('runtime invocation capability boundary', () => {
  it('transitively imports only process/filesystem primitives and protocol validation', async () => {
    const root = path.resolve('src/runtime-invocation');
    const violations: string[] = [];
    for (const file of await productionFiles(root)) {
      const source = ts.createSourceFile(
        file,
        await readFile(file, 'utf8'),
        ts.ScriptTarget.Latest,
        true,
        ts.ScriptKind.TS,
      );
      for (const specifier of moduleSpecifiers(source)) {
        if (specifier.startsWith('node:') || specifier.startsWith('./')) continue;
        if (specifier.startsWith('../protocol/')) continue;
        violations.push(`${path.relative(process.cwd(), file)} imports ${specifier}`);
      }
    }

    expect(violations).toEqual([]);
  });

  it('has no GitHub, Actions toolkit, state, artifact, publisher, or ledger capability', async () => {
    const root = path.resolve('src/runtime-invocation');
    const text = (
      await Promise.all((await productionFiles(root)).map((file) => readFile(file, 'utf8')))
    ).join('\n');

    for (const forbidden of [
      '@actions/',
      'octokit',
      '../comments',
      '../inline-comments',
      '../state',
      '../artifacts',
      '../ledger',
      '../main',
    ]) {
      expect(text).not.toContain(forbidden);
    }
  });
});
