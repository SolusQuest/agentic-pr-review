import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import ts from 'typescript';

const FORBIDDEN_EXACT = new Set(['fs', 'fs/promises', 'node:fs', 'node:fs/promises']);

async function collectTsFiles(root: string): Promise<string[]> {
  const files: string[] = [];
  async function visit(directory: string): Promise<void> {
    for (const entry of await readdir(directory, { withFileTypes: true })) {
      const fullPath = path.join(directory, entry.name);
      if (entry.isDirectory()) await visit(fullPath);
      else if (
        entry.isFile() &&
        fullPath.endsWith('.ts') &&
        !fullPath.endsWith('.test.ts') &&
        !fullPath.endsWith('.testhelper.ts') &&
        !fullPath.endsWith('.d.ts')
      )
        files.push(fullPath);
    }
  }
  await visit(root);
  return files;
}

function references(source: ts.SourceFile): Array<{ specifier: string; kind: string }> {
  const found: Array<{ specifier: string; kind: string }> = [];
  const visit = (node: ts.Node): void => {
    if (
      (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier)
    )
      found.push({ specifier: node.moduleSpecifier.text, kind: 'static' });
    if (
      ts.isImportEqualsDeclaration(node) &&
      ts.isExternalModuleReference(node.moduleReference) &&
      ts.isStringLiteral(node.moduleReference.expression)
    )
      found.push({ specifier: node.moduleReference.expression.text, kind: 'require' });
    if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      ts.isStringLiteral(node.arguments[0])
    )
      found.push({ specifier: node.arguments[0].text, kind: 'dynamic' });
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'require' &&
      ts.isStringLiteral(node.arguments[0])
    )
      found.push({ specifier: node.arguments[0].text, kind: 'require' });
    ts.forEachChild(node, visit);
  };
  visit(source);
  return found;
}

describe('canonical-json import boundary (AST-based)', () => {
  it('src/canonical-json/**/*.ts does not reach into fs', async () => {
    const files = await collectTsFiles(path.resolve('src/canonical-json'));
    expect(files.length).toBeGreaterThan(0);
    const violations: string[] = [];
    for (const file of files) {
      const source = ts.createSourceFile(
        file,
        await readFile(file, 'utf8'),
        ts.ScriptTarget.Latest,
        true,
        ts.ScriptKind.TS,
      );
      for (const reference of references(source)) {
        if (FORBIDDEN_EXACT.has(reference.specifier)) {
          violations.push(
            `${path.relative(process.cwd(), file)}: ${reference.kind} '${reference.specifier}' is forbidden`,
          );
        }
      }
    }
    expect(violations).toEqual([]);
  });
});
