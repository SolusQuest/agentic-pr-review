import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  REFRESH_CONTRACT,
  materializeRefreshedFixtures,
} from './check-r4-trusted-proof-fixture-refresh.mjs';

const root = path.resolve(import.meta.dirname, '..');
const git = (args: string[]) =>
  execFileSync('git', args, { cwd: root, encoding: 'utf8', windowsHide: true }).trim();

describe('R4 post-merge fixture refresh contract', () => {
  test('precomputes two-parent initial commits and a single-parent stale advance', () => {
    const merge = git(['rev-parse', 'HEAD']);
    const normalPrior = git(['rev-parse', 'HEAD~1']);
    const stalePrior = git(['rev-parse', 'HEAD~2']);
    const materialized = materializeRefreshedFixtures({
      repositoryRoot: root,
      mergeSha: merge,
      priorNormalHead: normalPrior,
      priorStaleHead: stalePrior,
    });
    try {
      expect(materialized.expected.normal.parents).toEqual([merge, normalPrior]);
      expect(materialized.expected.stale.parents).toEqual([merge, stalePrior]);
      expect(materialized.expected.stale.advanced_parents).toEqual([
        materialized.expected.stale.head,
      ]);
      expect(materialized.expected.normal.tree).toBe(materialized.expected.stale.tree);
      expect(materialized.expected.merge_tree).not.toBe(materialized.expected.normal.tree);
    } finally {
      materialized.dispose();
    }
  }, 15_000);
});
