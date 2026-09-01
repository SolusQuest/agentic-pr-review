import { execFileSync } from 'node:child_process';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import {
  REFRESH_CONTRACT,
  materializeRefreshedFixtures,
  resolveTestedMainCheckout,
} from './check-r4-trusted-proof-fixture-refresh.mjs';

const root = path.resolve(import.meta.dirname, '..');
const rawGit = (args: string[], input?: Buffer) =>
  execFileSync('git', args, { cwd: root, input, windowsHide: true });
const output = (
  runGit: (args: string[], input?: Buffer) => Buffer,
  args: string[],
  input?: Buffer,
) => runGit(args, input).toString('utf8').trim();
const git = (args: string[]) => output(rawGit, args);

function fixtureBasis() {
  const checkoutHead = git(['rev-parse', 'HEAD']);
  const testedMain = resolveTestedMainCheckout({
    runGit: rawGit,
    head: checkoutHead,
  }).testedMainHead;
  return {
    checkoutHead,
    testedMain,
    normalPrior: git(['rev-parse', `${testedMain}~1`]),
    stalePrior: git(['rev-parse', `${testedMain}~2`]),
  };
}

function commit(
  runGit: (args: string[], input?: Buffer) => Buffer,
  tree: string,
  parents: string[],
  message: string,
) {
  return output(
    runGit,
    [
      '-c',
      'user.name=Codex',
      '-c',
      'user.email=codex@solusquest.local',
      'commit-tree',
      tree,
      ...parents.flatMap((parent) => ['-p', parent]),
    ],
    Buffer.from(`${message}\n`, 'utf8'),
  );
}

function treeWith(
  runGit: (args: string[], input?: Buffer) => Buffer,
  baseTree: string,
  entries: Array<{ mode: string; path: string; bytes: string }>,
) {
  output(runGit, ['read-tree', baseTree]);
  for (const entry of entries) {
    const blob = output(runGit, ['hash-object', '-w', '--stdin'], Buffer.from(entry.bytes, 'utf8'));
    output(runGit, ['update-index', '--add', '--cacheinfo', `${entry.mode},${blob},${entry.path}`]);
  }
  return output(runGit, ['write-tree']);
}

describe('R4 post-merge fixture refresh contract', () => {
  test('precomputes two-parent initial commits and a single-parent stale advance', () => {
    const { checkoutHead, testedMain, normalPrior, stalePrior } = fixtureBasis();
    const materialized = materializeRefreshedFixtures({
      repositoryRoot: root,
      mergeSha: checkoutHead,
      priorNormalHead: normalPrior,
      priorStaleHead: stalePrior,
    });
    try {
      expect(materialized.expected.normal.parents).toEqual([testedMain, normalPrior]);
      expect(materialized.expected.stale.parents).toEqual([testedMain, stalePrior]);
      expect(materialized.expected.stale.advanced_parents).toEqual([
        materialized.expected.stale.head,
      ]);
      expect(materialized.expected.normal.tree).toBe(materialized.expected.stale.tree);
      expect(materialized.expected.merge_tree).not.toBe(materialized.expected.normal.tree);
    } finally {
      materialized.dispose();
    }
  }, 15_000);

  test('resolves exact direct-fixture and GitHub synthetic-merge checkouts', () => {
    const { checkoutHead, testedMain, normalPrior, stalePrior } = fixtureBasis();
    const materialized = materializeRefreshedFixtures({
      repositoryRoot: root,
      mergeSha: checkoutHead,
      priorNormalHead: normalPrior,
      priorStaleHead: stalePrior,
    });
    try {
      const direct = resolveTestedMainCheckout({
        runGit: materialized.runGit,
        head: materialized.expected.normal.head,
      });
      expect(direct).toMatchObject({
        testedMainHead: testedMain,
        disposition: 'fixture-head',
        includeWorktree: false,
      });

      const merge = commit(
        materialized.runGit,
        materialized.expected.normal.tree,
        [testedMain, materialized.expected.normal.head],
        'test: synthetic merge checkout',
      );
      const synthetic = resolveTestedMainCheckout({
        runGit: materialized.runGit,
        head: merge,
      });
      expect(synthetic).toMatchObject({
        testedMainHead: testedMain,
        disposition: 'synthetic-merge',
        includeWorktree: false,
      });
    } finally {
      materialized.dispose();
    }
  }, 15_000);

  test('fails closed for malformed fixture and merge topologies', () => {
    const { checkoutHead, testedMain, normalPrior, stalePrior } = fixtureBasis();
    const materialized = materializeRefreshedFixtures({
      repositoryRoot: root,
      mergeSha: checkoutHead,
      priorNormalHead: normalPrior,
      priorStaleHead: stalePrior,
    });
    try {
      const exactTree = materialized.expected.normal.tree;
      const baseTree = materialized.expected.merge_tree;
      const oneParent = commit(
        materialized.runGit,
        exactTree,
        [testedMain],
        'test: malformed one-parent fixture',
      );
      expect(() =>
        resolveTestedMainCheckout({ runGit: materialized.runGit, head: oneParent }),
      ).toThrow(/checkout-parent-shape/u);

      const wrongOrder = commit(
        materialized.runGit,
        exactTree,
        [materialized.expected.normal.head, testedMain],
        'test: malformed parent order',
      );
      expect(() =>
        resolveTestedMainCheckout({ runGit: materialized.runGit, head: wrongOrder }),
      ).toThrow(/checkout-first-parent-delta/u);

      const extraTree = treeWith(materialized.runGit, baseTree, [
        {
          mode: REFRESH_CONTRACT.canaryMode,
          path: REFRESH_CONTRACT.canaryPath,
          bytes: REFRESH_CONTRACT.initialCanary.toString('utf8'),
        },
        { mode: '100644', path: 'proof/unexpected.txt', bytes: 'unexpected\n' },
      ]);
      const extraPath = commit(
        materialized.runGit,
        extraTree,
        [testedMain, normalPrior],
        'test: malformed extra path',
      );
      expect(() =>
        resolveTestedMainCheckout({ runGit: materialized.runGit, head: extraPath }),
      ).toThrow(/checkout-first-parent-delta/u);

      const mergeMismatch = commit(
        materialized.runGit,
        extraTree,
        [testedMain, materialized.expected.normal.head],
        'test: malformed merge tree',
      );
      expect(() =>
        resolveTestedMainCheckout({ runGit: materialized.runGit, head: mergeMismatch }),
      ).toThrow(/checkout-(first-parent-delta|merge-tree)/u);

      const missingCanary = commit(
        materialized.runGit,
        baseTree,
        [testedMain, normalPrior],
        REFRESH_CONTRACT.normal.message.trim(),
      );
      const wrongPathTree = treeWith(materialized.runGit, baseTree, [
        {
          mode: REFRESH_CONTRACT.canaryMode,
          path: 'proof/apr178-wrong-path.txt',
          bytes: REFRESH_CONTRACT.initialCanary.toString('utf8'),
        },
      ]);
      const wrongPathCanary = commit(
        materialized.runGit,
        wrongPathTree,
        [testedMain, normalPrior],
        REFRESH_CONTRACT.normal.message.trim(),
      );

      for (const [malformedHead, malformedTree] of [
        [missingCanary, baseTree],
        [wrongPathCanary, wrongPathTree],
      ]) {
        expect(() =>
          resolveTestedMainCheckout({
            runGit: materialized.runGit,
            head: malformedHead,
          }),
        ).toThrow(/checkout-canary/u);

        const syntheticMerge = commit(
          materialized.runGit,
          malformedTree,
          [testedMain, malformedHead],
          'test: synthetic merge with malformed fixture intent',
        );
        expect(() =>
          resolveTestedMainCheckout({
            runGit: materialized.runGit,
            head: syntheticMerge,
          }),
        ).toThrow(/checkout-canary/u);
      }

      for (const [mode, bytes] of [
        ['100644', 'WRONG_CANARY\n'],
        ['100755', REFRESH_CONTRACT.initialCanary.toString('utf8')],
      ]) {
        const wrongTree = treeWith(materialized.runGit, baseTree, [
          { mode, path: REFRESH_CONTRACT.canaryPath, bytes },
        ]);
        const wrongCanary = commit(
          materialized.runGit,
          wrongTree,
          [testedMain, normalPrior],
          'test: malformed canary',
        );
        expect(() =>
          resolveTestedMainCheckout({ runGit: materialized.runGit, head: wrongCanary }),
        ).toThrow(/checkout-canary/u);
      }
    } finally {
      materialized.dispose();
    }
  }, 15_000);
});
