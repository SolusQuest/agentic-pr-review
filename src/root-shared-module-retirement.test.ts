import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import ts from 'typescript';

const MODULE_SOURCE = /\.(?:[cm]?[jt]sx?)$/u;
const MODULE_EXTENSION = /\.(?:js|jsx|mjs|cjs|ts|tsx|mts|cts)$/u;
const RETIRED_STEMS = new Set(
  ['src/types', 'src/utils'].map((relative) => canonical(path.resolve(relative))),
);

interface ModuleReference {
  kind: 'dynamic' | 'import' | 'import-equals' | 'import-type' | 'require' | 'export';
  specifier: string;
}

function canonical(value: string): string {
  const resolved = path.normalize(value);
  return process.platform === 'win32' ? resolved.toLowerCase() : resolved;
}

function trackedFiles(): string[] {
  return execFileSync('git', ['ls-files', '-z'], { encoding: 'utf8' })
    .split('\0')
    .filter((relative) => relative !== '' && existsSync(relative))
    .sort();
}

function scriptKind(file: string): ts.ScriptKind {
  if (file.endsWith('.tsx')) return ts.ScriptKind.TSX;
  if (file.endsWith('.jsx')) return ts.ScriptKind.JSX;
  if (/\.(?:js|mjs|cjs)$/u.test(file)) return ts.ScriptKind.JS;
  return ts.ScriptKind.TS;
}

function parseSource(file: string, text: string): ts.SourceFile {
  return ts.createSourceFile(file, text, ts.ScriptTarget.Latest, true, scriptKind(file));
}

function collectReferences(source: ts.SourceFile): ModuleReference[] {
  const references: ModuleReference[] = [];
  const add = (kind: ModuleReference['kind'], value: ts.Expression | undefined): void => {
    if (value && ts.isStringLiteralLike(value)) {
      references.push({ kind, specifier: value.text });
    }
  };
  const visit = (node: ts.Node): void => {
    if (ts.isImportDeclaration(node)) add('import', node.moduleSpecifier);
    if (ts.isExportDeclaration(node)) add('export', node.moduleSpecifier);
    if (ts.isImportEqualsDeclaration(node) && ts.isExternalModuleReference(node.moduleReference)) {
      add('import-equals', node.moduleReference.expression);
    }
    if (
      ts.isImportTypeNode(node) &&
      ts.isLiteralTypeNode(node.argument) &&
      ts.isStringLiteralLike(node.argument.literal)
    ) {
      references.push({ kind: 'import-type', specifier: node.argument.literal.text });
    }
    if (ts.isCallExpression(node) && node.arguments.length > 0) {
      if (node.expression.kind === ts.SyntaxKind.ImportKeyword) {
        add('dynamic', node.arguments[0]);
      } else if (ts.isIdentifier(node.expression) && node.expression.text === 'require') {
        add('require', node.arguments[0]);
      }
    }
    ts.forEachChild(node, visit);
  };
  visit(source);
  return references;
}

function retiredTarget(importer: string, specifier: string): string | undefined {
  if (!specifier.startsWith('.')) return undefined;
  const stem = canonical(
    path.resolve(path.dirname(importer), specifier.replace(MODULE_EXTENSION, '')),
  );
  return RETIRED_STEMS.has(stem)
    ? path.relative(process.cwd(), stem).replace(/\\/gu, '/')
    : undefined;
}

function stringLeaves(value: unknown): string[] {
  if (typeof value === 'string') return [value];
  if (Array.isArray(value)) return value.flatMap(stringLeaves);
  if (value && typeof value === 'object') {
    return Object.values(value as Record<string, unknown>).flatMap(stringLeaves);
  }
  return [];
}

function packageExposureViolations(files: readonly string[]): string[] {
  const violations: string[] = [];
  for (const file of files.filter((relative) => path.basename(relative) === 'package.json')) {
    const manifest = JSON.parse(readFileSync(file, 'utf8')) as Record<string, unknown>;
    for (const field of ['main', 'module', 'types', 'exports'] as const) {
      for (const target of stringLeaves(manifest[field])) {
        const stem = canonical(
          path.resolve(path.dirname(file), target.replace(MODULE_EXTENSION, '')),
        );
        if (RETIRED_STEMS.has(stem)) violations.push(`${file}: ${field} -> ${target}`);
      }
    }
  }
  return violations;
}

function tsconfigAliasViolations(files: readonly string[]): string[] {
  const violations: string[] = [];
  for (const file of files.filter((relative) =>
    /^tsconfig.*\.json$/u.test(path.basename(relative)),
  )) {
    const parsed = ts.parseConfigFileTextToJson(file, readFileSync(file, 'utf8'));
    if (parsed.error) violations.push(`${file}: invalid TypeScript configuration`);
    const compilerOptions = (parsed.config?.compilerOptions ?? {}) as Record<string, unknown>;
    const base = path.resolve(path.dirname(file), String(compilerOptions.baseUrl ?? '.'));
    const aliases = (compilerOptions.paths ?? {}) as Record<string, unknown>;
    for (const [alias, targets] of Object.entries(aliases)) {
      for (const target of stringLeaves(targets)) {
        const stem = canonical(path.resolve(base, target.replace(MODULE_EXTENSION, '')));
        if (RETIRED_STEMS.has(stem)) violations.push(`${file}: ${alias} -> ${target}`);
      }
    }
  }
  return violations;
}

describe('root shared module retirement', () => {
  it('has no landing-tree consumer or package/config exposure', () => {
    expect(existsSync('src/types.ts')).toBe(false);
    expect(existsSync('src/utils.ts')).toBe(false);

    const files = trackedFiles();
    const violations: string[] = [];
    for (const file of files.filter((relative) => MODULE_SOURCE.test(relative))) {
      const source = parseSource(file, readFileSync(file, 'utf8'));
      for (const reference of collectReferences(source)) {
        const target = retiredTarget(path.resolve(file), reference.specifier);
        if (target)
          violations.push(`${file}: ${reference.kind} '${reference.specifier}' -> ${target}`);
      }
    }

    violations.push(...packageExposureViolations(files), ...tsconfigAliasViolations(files));
    expect(violations).toEqual([]);
  }, 15_000);

  it('recognizes every supported syntax form without requiring the target to exist', () => {
    const source = parseSource(
      'src/example.ts',
      [
        "import type { RuntimeUsage } from './types.js';",
        "import './utils.js';",
        "export { ReviewTarget } from './types.js';",
        "export * from './utils.js';",
        "const dynamicValue = import('./types.js');",
        "import legacy = require('./utils.js');",
        "const required = require('./types.js');",
        "type Imported = import('./utils.js').JsonValue;",
      ].join('\n'),
    );
    expect(
      collectReferences(source)
        .map(({ kind }) => kind)
        .sort(),
    ).toEqual([
      'dynamic',
      'export',
      'export',
      'import',
      'import',
      'import-equals',
      'import-type',
      'require',
    ]);
  });

  it('matches exact root stems and accepts family-local modules', () => {
    const positives = [
      ['src/example.ts', './types'],
      ['src/example.ts', './types.js'],
      ['src/example.ts', './types.ts'],
      ['scripts/example.mjs', '../src/types.js'],
      ['src/example.ts', './utils'],
      ['src/example.ts', './utils.js'],
      ['scripts/example.mjs', '../src/utils.js'],
    ] as const;
    expect(
      positives.map(([file, specifier]) => retiredTarget(path.resolve(file), specifier)),
    ).toEqual([
      'src/types',
      'src/types',
      'src/types',
      'src/types',
      'src/utils',
      'src/utils',
      'src/utils',
    ]);
    expect(retiredTarget(path.resolve('src/provider/example.ts'), './types.js')).toBeUndefined();
    expect(retiredTarget(path.resolve('src/state/example.ts'), './utils.js')).toBeUndefined();
  });
});
