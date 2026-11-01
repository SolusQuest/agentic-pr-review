import { describe, expect, test } from 'vitest';
import fs from 'node:fs';
import { execFileSync, spawnSync } from 'node:child_process';
import YAML from 'yaml';
import {
  discoverTests,
  planShards,
  verifyPartition,
  verifySelection,
} from '../../scripts/ci/source-tests.mjs';
import {
  sourceDependencies,
  verifySourceWorkflow,
  verifySourceMutations,
  verifyAggregateOutcomes,
} from '../../scripts/ci/workflow.mjs';

const workflow = () => YAML.parse(fs.readFileSync('.github/workflows/ci.yml', 'utf8'));
const files = Array.from({ length: 31 }, (_, i) => `tests/group-${i}.test.ts`);

describe('complete bounded source sharding', () => {
  test('balances heavy files deterministically and admits every newly discovered test once', () => {
    const weights = { [files[0]]: 90000, [files[1]]: 60000 };
    const plan = planShards(files, weights);
    expect(planShards([...files].reverse(), weights)).toEqual(plan);
    verifyPartition(files, plan.shards);
    expect(plan.shards.find((s) => s.includes(files[0]))).not.toContain(files[1]);
    const added = [...files, 'tests/new-source.test.ts'];
    const next = planShards(added, weights);
    verifyPartition(added, next.shards);
    expect(next.shards.flat().filter((f) => f === 'tests/new-source.test.ts')).toHaveLength(1);
  });
  test('rejects missing, extra, duplicated, empty and unknown shard membership', () => {
    for (const mutate of [
      (s) => s.pop(),
      (s) => s.push([]),
      (s) => s[0].pop(),
      (s) => s[0].push('tests/unknown.test.ts'),
      (s) => s[1].push(s[0][0]),
      (s) => s[0].splice(0),
    ]) {
      const shards = planShards(files).shards;
      mutate(shards);
      expect(() => verifyPartition(files, shards)).toThrow();
    }
    for (const invalid of [
      [],
      files.slice(0, 5),
      [...files, files[0]],
      ['../escape.test.ts', ...files],
      ['/absolute.test.ts', ...files],
    ])
      expect(() => planShards(invalid)).toThrow();
    for (const value of [-1, 0, NaN, Infinity, '3'])
      expect(() => planShards(files, { [files[0]]: value })).toThrow();
  });
  test('checks actual Vitest discovery and selector precision against the entire tracked source census', () => {
    const actual = discoverTests();
    const tracked = execFileSync(
      'git',
      ['ls-files', '--cached', '--others', '--exclude-standard', '-z'],
      { encoding: 'utf8' },
    )
      .split('\0')
      .filter((f) => /\.(test|spec)\.[cm]?[jt]sx?$/u.test(f))
      .sort();
    expect(actual).toEqual(tracked);
    expect(actual.some((f) => f.startsWith('.local/') || f.startsWith('.scratch/'))).toBe(false);
    const plan = planShards(actual);
    verifyPartition(actual, plan.shards);
    for (const shard of plan.shards) verifySelection(shard, discoverTests(process.cwd(), shard));
    expect(() =>
      verifySelection(['tests/a.test.ts'], ['tests/a.test.ts', 'tests/a.test.ts.extra.test.ts']),
    ).toThrow();
    expect(() => verifySelection(['tests/a.test.ts'], [])).toThrow();
    expect(() =>
      verifySelection(['tests/a.test.ts'], ['tests/a.test.ts', 'tests/a.test.ts']),
    ).toThrow();
  });
  test('refuses unknown indices, extra filters and empty selection arguments before executing tests', () => {
    for (const args of [
      ['--shard', '0'],
      ['--shard', '7'],
      ['--shard', '1/6'],
      ['--shard', '1', '--passWithNoTests'],
      [],
    ]) {
      const result = spawnSync(process.execPath, ['scripts/ci/source-tests.mjs', ...args], {
        encoding: 'utf8',
      });
      expect(result.status).not.toBe(0);
      expect(result.stderr).toContain('Usage:');
    }
  });
});

describe('source CI actual DAG admission', () => {
  test('requires all actual workers, literal source, complete commands and unmasked results', () => {
    expect(verifySourceMutations(workflow())).toBeGreaterThan(100);
  });
  test('executes the actual source aggregate against each dependency failure, skip, cancel and missing result', () => {
    const program = verifySourceWorkflow(workflow());
    expect(verifyAggregateOutcomes(program, sourceDependencies, 'SOURCE_CI_NEEDS')).toBeGreaterThan(
      100,
    );
    expect(() =>
      verifyAggregateOutcomes(
        program.replace('process.exit(1)', 'process.exit(0)'),
        sourceDependencies,
        'SOURCE_CI_NEEDS',
      ),
    ).toThrow();
  });
});
