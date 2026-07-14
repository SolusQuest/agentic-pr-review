import { readFile, readdir, stat } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import ts from 'typescript';

/**
 * Enforce the state-v2 / canonical-json import boundary using the real
 * TypeScript compiler API. This catches every kind of module reference:
 *   - `import x from 'spec'`
 *   - `import 'spec'` (side-effect only)
 *   - `import type ... from 'spec'`
 *   - `export ... from 'spec'`
 *   - `export * from 'spec'`
 *   - `import('spec')` dynamic import
 *   - `require('spec')` (rejected outright inside these modules)
 *
 * Both exact specifiers and directory prefixes are forbidden — e.g. any
 * import whose specifier resolves under `../runtime-invocation/`, not just
 * `../runtime-invocation` exactly.
 */

const FORBIDDEN_EXACT = new Set<string>(['fs', 'fs/promises', 'node:fs', 'node:fs/promises']);

// Forbidden sibling *prefixes*. A specifier is forbidden when, after
// dropping any trailing `.js` / `.ts` and normalizing slashes, it either
// equals the prefix or starts with the prefix followed by `/`.
const FORBIDDEN_SIBLING_PREFIXES = [
  '../main',
  '../runtime',
  '../runtime-invocation',
  '../runtime-integration',
  '../state',
] as const;

function normalizeSpecifier(spec: string): string {
  let s = spec.replace(/\\/g, '/');
  s = s.replace(/\.(?:js|mjs|cjs|ts|mts|cts)$/, '');
  return s;
}

function siblingIsForbidden(spec: string): string | null {
  const norm = normalizeSpecifier(spec);
  for (const prefix of FORBIDDEN_SIBLING_PREFIXES) {
    if (norm === prefix || norm.startsWith(`${prefix}/`)) {
      return prefix;
    }
  }
  return null;
}

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
          !full.endsWith('.mts') &&
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

interface Reference {
  specifier: string;
  kind: 'static' | 'dynamic' | 'require';
}

function collectReferences(source: ts.SourceFile): Reference[] {
  const refs: Reference[] = [];
  const visit = (node: ts.Node): void => {
    // Static import / export ... from 'x'
    if (
      (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) &&
      node.moduleSpecifier &&
      ts.isStringLiteral(node.moduleSpecifier)
    ) {
      refs.push({ specifier: node.moduleSpecifier.text, kind: 'static' });
    }
    // `import x = require('spec')`
    if (
      ts.isImportEqualsDeclaration(node) &&
      ts.isExternalModuleReference(node.moduleReference) &&
      ts.isStringLiteral(node.moduleReference.expression)
    ) {
      refs.push({ specifier: node.moduleReference.expression.text, kind: 'require' });
    }
    // Dynamic import('spec')
    if (
      ts.isCallExpression(node) &&
      node.expression.kind === ts.SyntaxKind.ImportKeyword &&
      node.arguments.length >= 1 &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      refs.push({ specifier: node.arguments[0].text, kind: 'dynamic' });
    }
    // require('spec')
    if (
      ts.isCallExpression(node) &&
      ts.isIdentifier(node.expression) &&
      node.expression.text === 'require' &&
      node.arguments.length >= 1 &&
      ts.isStringLiteral(node.arguments[0])
    ) {
      refs.push({ specifier: node.arguments[0].text, kind: 'require' });
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return refs;
}

async function parseFile(file: string): Promise<ts.SourceFile> {
  const text = await readFile(file, 'utf8');
  return ts.createSourceFile(
    file,
    text,
    ts.ScriptTarget.Latest,
    /*setParentNodes*/ true,
    ts.ScriptKind.TS,
  );
}

describe('state-v2 import boundary (AST-based)', () => {
  it('src/state-v2/**/*.ts does not reach into fs, legacy state, or runtime layers', async () => {
    const root = path.resolve('src/state-v2');
    const files = await collectTsFiles(root);
    expect(files.length).toBeGreaterThan(0);
    const violations: string[] = [];
    for (const file of files) {
      const source = await parseFile(file);
      for (const ref of collectReferences(source)) {
        if (FORBIDDEN_EXACT.has(ref.specifier)) {
          violations.push(
            `${path.relative(process.cwd(), file)}: ${ref.kind} '${ref.specifier}' is forbidden`,
          );
        }
        const prefixMatch = siblingIsForbidden(ref.specifier);
        if (prefixMatch) {
          violations.push(
            `${path.relative(process.cwd(), file)}: ${ref.kind} '${ref.specifier}' reaches into forbidden layer '${prefixMatch}'`,
          );
        }
      }
    }
    expect(violations).toEqual([]);
  });

  it('src/canonical-json/**/*.ts does not reach into fs', async () => {
    const root = path.resolve('src/canonical-json');
    const files = await collectTsFiles(root);
    expect(files.length).toBeGreaterThan(0);
    const violations: string[] = [];
    for (const file of files) {
      const source = await parseFile(file);
      for (const ref of collectReferences(source)) {
        if (FORBIDDEN_EXACT.has(ref.specifier)) {
          violations.push(
            `${path.relative(process.cwd(), file)}: ${ref.kind} '${ref.specifier}' is forbidden`,
          );
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
