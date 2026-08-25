import { execFileSync } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { residualReferenceRules } from './residual-reference-allowlist.js';

const root = process.cwd();

function trackedFiles(...pathspecs: string[]) {
  return execFileSync(
    'git',
    ['ls-files', '--cached', '--others', '--exclude-standard', '-z', '--', ...pathspecs],
    {
      cwd: root,
      encoding: 'utf8',
    },
  )
    .split('\0')
    .filter((relative) => relative !== '' && existsSync(path.join(root, relative)))
    .sort();
}

async function text(relative: string) {
  return readFile(path.join(root, relative), 'utf8');
}

function sourceClass(relative: string) {
  if (/^src\/action-wrapper\/.*\.test\.ts$/u.test(relative)) return 'wrapper-test';
  if (/^src\/action-wrapper\/.*\.ts$/u.test(relative)) return 'thin-wrapper-or-bridge';
  if (relative === 'src/artifact-provenance-vectors.ts') return 's2-negative-evidence';
  if (relative === 'src/residual-reference-allowlist.ts') return 'residual-inventory';
  if (/^src\/.*\.test\.ts$/u.test(relative)) return 'conformance-test';
  return undefined;
}

describe('R4 W13 closed migration inventory', () => {
  test('classifies every surviving TypeScript or JavaScript source', () => {
    const sources = trackedFiles('src/*.ts', 'src/*.js', 'src/**/*.ts', 'src/**/*.js');
    expect(
      sources.map((relative) => [relative, sourceClass(relative)]).filter(([, owner]) => !owner),
    ).toEqual([]);
    expect(new Set(sources.map(sourceClass))).toEqual(
      new Set([
        'wrapper-test',
        'thin-wrapper-or-bridge',
        's2-negative-evidence',
        'residual-inventory',
        'conformance-test',
      ]),
    );
  });

  test('pins package scripts, workflow files, schema files, and fixture roots', async () => {
    const packageDocument = JSON.parse(await text('package.json')) as {
      scripts: Record<string, string>;
    };
    expect(packageDocument.scripts).toEqual({
      'build:action': 'node scripts/build-action.mjs',
      check: 'npm run format:check && npm run typecheck && npm test',
      'dist:check': 'node scripts/check-action-distribution.mjs',
      'format:check': 'prettier --check .',
      format: 'prettier --write .',
      'r3:live:policy': 'node scripts/check-r3-live-proof.mjs',
      'runtime:verify': 'bash runtime/scripts/verify-runtime.sh all',
      'runtime:integration': 'node scripts/run-runtime-integration.mjs',
      typecheck: 'tsc --noEmit',
      test: 'vitest run',
    });
    expect(trackedFiles('scripts/*')).toEqual([
      'scripts/assemble-r4-trusted-proof-evidence.mjs',
      'scripts/build-action.mjs',
      'scripts/check-action-distribution.mjs',
      'scripts/check-r3-live-proof.mjs',
      'scripts/check-r3-live-proof.test.ts',
      'scripts/check-r4-e2p-managed-architecture.mjs',
      'scripts/check-r4-e2p-preflight.mjs',
      'scripts/check-r4-e2p-preflight.test.ts',
      'scripts/check-r4-e2p-receipt-v2.mjs',
      'scripts/check-r4-e2p-receipt-v2.test.ts',
      'scripts/check-r4-e2p-receipt.mjs',
      'scripts/check-r4-trusted-proof.mjs',
      'scripts/check-r4-trusted-proof.test.ts',
      'scripts/compose-r4-e2-receipt.mjs',
      'scripts/compose-r4-e2-receipt.test.ts',
      'scripts/compose-r4-e2p-receipt-v2.mjs',
      'scripts/compose-r4-e2p-receipt.mjs',
      'scripts/compose-r4-e2p-receipt.test.ts',
      'scripts/generate-r4-trusted-proof-cleanup-plan.mjs',
      'scripts/project-r4-e2p-diagnostics.mjs',
      'scripts/project-r4-e2p-diagnostics.test.ts',
      'scripts/project-r4-trusted-proof-evidence.mjs',
      'scripts/project-r4-trusted-proof-evidence.test.ts',
      'scripts/r4-trusted-proof-contract.mjs',
      'scripts/run-clean-source-proof.mjs',
      'scripts/run-clean-source-proof.test.ts',
      'scripts/run-runtime-integration.mjs',
    ]);
    expect(trackedFiles('.github/workflows/*')).toEqual([
      '.github/workflows/ci.yml',
      '.github/workflows/r3-live-proof.yml',
      '.github/workflows/r4-trusted-proof.yml',
      '.github/workflows/runtime-ci.yml',
    ]);
    expect(trackedFiles('protocol/schemas/*')).toEqual([
      'protocol/schemas/provider-session-ledger.v1.json',
      'protocol/schemas/review-input.v1.json',
      'protocol/schemas/review-result.v1.json',
      'protocol/schemas/review-trace.v1.json',
    ]);
    expect([
      ...new Set(trackedFiles('protocol/fixtures/**').map((relative) => relative.split('/')[2])),
    ]).toEqual(['prefix-contract', 'v1']);
  });

  test('keeps only permanent residual ownership and the exact S2 evidence unit', () => {
    expect(
      residualReferenceRules.filter(({ lifecycleClass }) =>
        ['protocol-migration', 'state-migration', 'credential-canary'].includes(lifecycleClass),
      ),
    ).toEqual([]);
    expect(
      residualReferenceRules
        .filter(({ id }) => ['RR-001', 'RR-002', 'RR-009', 'RR-031'].includes(id))
        .map(({ id, lifecycleClass }) => [id, lifecycleClass]),
    ).toEqual([
      ['RR-001', 'conformance'],
      ['RR-002', 'conformance'],
      ['RR-009', 'conformance'],
      ['RR-031', 'conformance'],
    ]);
    expect(trackedFiles('src/artifact-provenance-vectors*')).toEqual([
      'src/artifact-provenance-vectors.test.ts',
      'src/artifact-provenance-vectors.ts',
    ]);
  });

  test('pins the governing handoff to the exact active residual inventory and scan boundary', async () => {
    const handoffPaths = trackedFiles('docs/20_architecture/r1-*-removal-handoff.md');
    expect(handoffPaths).toHaveLength(1);
    const handoff = await text(handoffPaths[0] ?? '');
    const section = handoff.split('## Residual-reference ownership\n')[1]?.split('\n## ')[0];
    expect(section).toBeDefined();

    const documentedIds = [
      ...(section ?? '').matchAll(/^\| (RR-\d{3}(?:\.\.\d{3})?)\s*\|/gmu),
    ].flatMap(([, cell]) => {
      const [first, last = first] = cell
        .split('..')
        .map((value) => Number(value.replace('RR-', '')));
      return Array.from(
        { length: last - first + 1 },
        (_, offset) => `RR-${String(first + offset).padStart(3, '0')}`,
      );
    });
    expect(documentedIds).toEqual(residualReferenceRules.map(({ id }) => id));

    expect(section).toContain('git ls-files --cached --others --exclude-standard');
    expect(section).toContain('cached tracked files plus non-ignored untracked files');
    expect(section).toContain('binary or invalid-UTF-8 content');
    expect(section).toContain('.github/actions/agentic-pr-review/dist/index.js');
    expect(section).toContain('src/residual-reference-allowlist.ts');
    expect(section).toContain('src/residual-reference-guard.test.ts');
    expect(section).toContain('src/root-shared-module-retirement.test.ts');
  });

  test('keeps every retired migration family and orphan resolver corpus absent', () => {
    const tracked = trackedFiles();
    const retired = [
      'protocol/fixtures/schema-resolver-conformance/',
      'src/canonical-json',
      'src/comments.',
      'src/inline-comments.',
      'src/ledger-csharp.',
      'src/live-provider/',
      'src/live-runtime-invocation/',
      'src/prefix-contract/',
      'src/protocol/',
      'src/provider-metadata/',
      'src/runtime-invocation/',
      'src/state-acceptance/',
      'src/state-v2/',
      'src/structured.',
      'src/target.',
      'src/types.ts',
      'src/utils.ts',
    ];
    expect(
      retired.flatMap((prefix) => tracked.filter((relative) => relative.startsWith(prefix))),
    ).toEqual([]);
  });

  test('pins the current handoff and rejects superseded ownership claims', async () => {
    const handoff = await text('docs/20_architecture/r4-migration-cutover-handoff.md');
    for (const heading of [
      '## Current implementation boundary',
      '## Contract and fixture inventory',
      '## Residual and historical ownership',
      '## E1 exact-source identity',
      '## Post-merge closure order',
      '## Validation',
    ]) {
      expect(handoff).toContain(heading);
    }

    const currentSurfaces = await Promise.all([
      text('README.md'),
      text('docs/00_project/project-context.md'),
      text('docs/20_architecture/agent-runtime-rebaseline.md'),
      text('docs/20_architecture/distribution.md'),
      text('docs/20_architecture/r4-actionhost-wrapper-plan.md'),
      text('docs/90_roadmap/roadmap-seed.md'),
      text('protocol/schemas/review-input.v1.json'),
      text('protocol/schemas/review-result.v1.json'),
      text('protocol/schemas/review-trace.v1.json'),
    ]).then((documents) => documents.join('\n'));
    for (const staleClaim of [
      'TypeScript host for a future review runtime',
      'back to the TypeScript publisher',
      'TS interfaces are convenience only',
      'prospective R4 product contract',
      'R4 is active only through its docs gate',
      'No implementation issue is published until that docs contract is merged',
      'Do not create R4 implementation or deletion issues until the R4 docs contract merges',
      'Remaining TypeScript canonicalization',
      'src/no-public-action.test.ts',
    ]) {
      expect(currentSurfaces).not.toContain(staleClaim);
    }
  });
});
