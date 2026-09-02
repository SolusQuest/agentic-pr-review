import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
export {
  ENROLLMENT_CONTRACT,
  bindEnrollmentRecord,
  canonicalRecordText,
  canonicalAuthorizationManifest,
  createGhTransport,
  executeDurableEnrollmentPhase,
  readEnrollmentJournal,
  recordSha256,
  runEnrollmentCli,
  validateEnrollmentRecord,
} from './execute-r4-trusted-proof-enrollment.mjs';

// This module owns deterministic local object construction only. Exact live
// identities belong in the host-restricted post-merge enrollment record.
export const REFRESH_CONTRACT = Object.freeze({
  // Retained only as a local object-materialization helper. The executable
  // enrollment contract is exported above and deliberately has a different
  // host-record kind from the workflow variable manifest.
  kind: 'apr-r4-trusted-proof-fixture-refresh-v4',
  canaryPath: 'proof/apr178-path-canary.txt',
  canaryMode: '100644',
  initialCanary: Buffer.from('APR178_TOOL_DATA_CANARY\n', 'utf8'),
  advancedCanary: Buffer.from('APR178_TOOL_DATA_CANARY_STALE\n', 'utf8'),
  normal: Object.freeze({
    message: 'test: refresh normal trusted-proof fixture\n',
    date: '2026-08-30T00:00:00+00:00',
  }),
  stale: Object.freeze({
    message: 'test: refresh stale trusted-proof fixture\n',
    date: '2026-08-30T00:00:01+00:00',
  }),
  advance: Object.freeze({
    message: 'test: advance stale trusted-proof fixture canary\n',
    date: '2026-08-30T00:00:02+00:00',
  }),
  identity: Object.freeze({ name: 'Codex', email: 'codex@solusquest.local' }),
  variable: 'R4_TRUSTED_PROOF_AUTHORIZATION',
});

const hex40 = /^[0-9a-f]{40}$/u;
const utf8 = new TextDecoder('utf-8', { fatal: true });
const initialCanaryBlob = '6fb1e09fc322bc85611172c171f4e3fce8bdee1c';

function fail(code) {
  throw new Error(`APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID ${code}`);
}
function json(value) {
  return JSON.stringify(value);
}
function objectId(value, code) {
  if (typeof value !== 'string' || !hex40.test(value)) fail(code);
  return value;
}
function exec(repositoryRoot, args, input, env = {}) {
  try {
    return execFileSync('git', args, {
      cwd: repositoryRoot,
      encoding: 'buffer',
      input,
      windowsHide: true,
      maxBuffer: 64 * 1024 * 1024,
      env: { ...process.env, ...env },
    });
  } catch {
    fail('git-object-unavailable');
  }
}
function text(run, args, input) {
  const result = run(args, input);
  if (!Buffer.isBuffer(result)) fail('git-output');
  try {
    return utf8.decode(result).trim();
  } catch {
    fail('git-encoding');
  }
}
function environment(objectDirectory, indexFile, alternateObjects) {
  return {
    GIT_OBJECT_DIRECTORY: objectDirectory,
    GIT_INDEX_FILE: indexFile,
    GIT_ALTERNATE_OBJECT_DIRECTORIES: alternateObjects,
    GIT_CONFIG_NOSYSTEM: '1',
    GIT_CONFIG_GLOBAL: path.join(path.dirname(objectDirectory), 'empty-global-config'),
    GIT_TERMINAL_PROMPT: '0',
  };
}
function commit(repositoryRoot, tree, parents, metadata, env) {
  const identity = REFRESH_CONTRACT.identity;
  if (
    !Array.isArray(parents) ||
    parents.length === 0 ||
    parents.some((value) => !hex40.test(value))
  )
    fail('fixture-parent-shape');
  return objectId(
    text(
      (args, input) =>
        exec(repositoryRoot, args, input, {
          ...env,
          GIT_AUTHOR_NAME: identity.name,
          GIT_AUTHOR_EMAIL: identity.email,
          GIT_AUTHOR_DATE: metadata.date,
          GIT_COMMITTER_NAME: identity.name,
          GIT_COMMITTER_EMAIL: identity.email,
          GIT_COMMITTER_DATE: metadata.date,
        }),
      ['commit-tree', tree, ...parents.flatMap((parent) => ['-p', parent])],
      Buffer.from(metadata.message, 'utf8'),
    ),
    'fixture-commit',
  );
}
function writeTree(run, baseTree, blob) {
  text(run, ['read-tree', baseTree]);
  text(run, [
    'update-index',
    '--add',
    '--cacheinfo',
    `${REFRESH_CONTRACT.canaryMode},${blob},${REFRESH_CONTRACT.canaryPath}`,
  ]);
  return objectId(text(run, ['write-tree']), 'fixture-tree');
}
function parents(run, head) {
  return text(run, ['show', '-s', '--format=%P', head]).split(/\s+/u).filter(Boolean);
}
function assertParentArray(run, head, expected, code) {
  if (json(parents(run, head)) !== json(expected)) fail(code);
}
function assertAddOnly(run, parent, head, blob) {
  const actual = text(run, ['diff-tree', '--no-commit-id', '--raw', '-r', parent, head]);
  const expected = `:000000 ${REFRESH_CONTRACT.canaryMode} ${'0'.repeat(40)} ${blob} A\t${REFRESH_CONTRACT.canaryPath}`;
  if (actual !== expected) fail('fixture-canary-delta');
}
function assertAdvanceOnly(run, initial, advanced, advancedBlob) {
  const initialBlob = text(run, ['rev-parse', `${initial}:${REFRESH_CONTRACT.canaryPath}`]);
  const actual = text(run, ['diff-tree', '--no-commit-id', '--raw', '-r', initial, advanced]);
  const expected = `:100644 100644 ${initialBlob} ${advancedBlob} M\t${REFRESH_CONTRACT.canaryPath}`;
  if (actual !== expected) fail('fixture-advanced-delta');
}

function exactCanaryEntry(run, tree) {
  const actual = text(run, ['ls-tree', tree, '--', REFRESH_CONTRACT.canaryPath]);
  const expected = [
    `${REFRESH_CONTRACT.canaryMode} blob`,
    `${initialCanaryBlob}\t${REFRESH_CONTRACT.canaryPath}`,
  ].join(' ');
  return actual === expected;
}

function exactCanaryDelta(run, base, head) {
  const actual = text(run, ['diff-tree', '--no-commit-id', '--raw', '-r', base, head]);
  const expected = `:000000 ${REFRESH_CONTRACT.canaryMode} ${'0'.repeat(40)} ${initialCanaryBlob} A\t${REFRESH_CONTRACT.canaryPath}`;
  return actual === expected;
}

const fixtureMessages = new Set(
  [REFRESH_CONTRACT.normal, REFRESH_CONTRACT.stale, REFRESH_CONTRACT.advance].map(({ message }) =>
    message.trim(),
  ),
);

function hasFixtureCommitIntent(runGit, commit) {
  const fields = text(runGit, [
    'show',
    '-s',
    '--format=%an%x00%ae%x00%cn%x00%ce%x00%B',
    commit,
  ]).split('\0');
  return (
    fields.length === 5 &&
    fields[0] === REFRESH_CONTRACT.identity.name &&
    fields[1] === REFRESH_CONTRACT.identity.email &&
    fields[2] === REFRESH_CONTRACT.identity.name &&
    fields[3] === REFRESH_CONTRACT.identity.email &&
    fixtureMessages.has(fields[4])
  );
}

/** Resolve only the ordinary, exact fixture-head, and GitHub merge-checkout shapes. */
export function resolveTestedMainCheckout({ runGit, head }) {
  if (typeof runGit !== 'function') fail('checkout-runner');
  const checkoutHead = objectId(head, 'checkout-head');
  const checkoutTree = objectId(
    text(runGit, ['rev-parse', `${checkoutHead}^{tree}`]),
    'checkout-tree',
  );
  const canaryEntry = text(runGit, ['ls-tree', checkoutTree, '--', REFRESH_CONTRACT.canaryPath]);
  const checkoutParents = parents(runGit, checkoutHead);
  const hasFixtureIntent =
    canaryEntry !== '' ||
    [checkoutHead, ...checkoutParents].some((commit) => hasFixtureCommitIntent(runGit, commit));
  if (canaryEntry === '') {
    if (hasFixtureIntent) fail('checkout-canary');
    return Object.freeze({
      testedMainHead: checkoutHead,
      testedMainTree: checkoutTree,
      disposition: 'ordinary',
      includeWorktree: true,
    });
  }
  if (
    !exactCanaryEntry(runGit, checkoutTree) ||
    !Buffer.from(
      runGit(['cat-file', 'blob', `${checkoutHead}:${REFRESH_CONTRACT.canaryPath}`]),
    ).equals(REFRESH_CONTRACT.initialCanary)
  ) {
    fail('checkout-canary');
  }

  if (checkoutParents.length !== 2) fail('checkout-parent-shape');
  const testedMainHead = objectId(checkoutParents[0], 'checkout-first-parent');
  const testedMainTree = objectId(
    text(runGit, ['rev-parse', `${testedMainHead}^{tree}`]),
    'checkout-first-parent-tree',
  );
  if (
    text(runGit, ['ls-tree', testedMainTree, '--', REFRESH_CONTRACT.canaryPath]) !== '' ||
    !exactCanaryDelta(runGit, testedMainHead, checkoutHead)
  ) {
    fail('checkout-first-parent-delta');
  }

  const secondParent = objectId(checkoutParents[1], 'checkout-second-parent');
  const secondTree = objectId(
    text(runGit, ['rev-parse', `${secondParent}^{tree}`]),
    'checkout-second-parent-tree',
  );
  const secondParents = parents(runGit, secondParent);
  const secondIsCurrentFixture =
    secondParents.length === 2 &&
    secondParents[0] === testedMainHead &&
    exactCanaryEntry(runGit, secondTree) &&
    exactCanaryDelta(runGit, testedMainHead, secondParent);
  if (secondIsCurrentFixture && checkoutTree !== secondTree) {
    fail('checkout-merge-tree');
  }
  if (!secondIsCurrentFixture && checkoutTree === secondTree) {
    fail('checkout-topology-ambiguous');
  }

  return Object.freeze({
    testedMainHead,
    testedMainTree,
    disposition: secondIsCurrentFixture ? 'synthetic-merge' : 'fixture-head',
    includeWorktree: false,
  });
}

/** Build all object identities before any live ref is mutated. */
export function materializeRefreshedFixtures({
  repositoryRoot = path.resolve(import.meta.dirname, '..'),
  mergeSha,
  priorNormalHead,
  priorStaleHead,
  sourceRunGit,
} = {}) {
  const source = sourceRunGit ?? ((args, input) => exec(repositoryRoot, args, input));
  const requestedHead = objectId(
    mergeSha ?? text(source, ['rev-parse', 'HEAD^{commit}']),
    'merge-sha',
  );
  const resolved = resolveTestedMainCheckout({
    runGit: source,
    head: requestedHead,
  });
  const merge = resolved.testedMainHead;
  const normalPrior = objectId(priorNormalHead, 'normal-prior-head');
  const stalePrior = objectId(priorStaleHead, 'stale-prior-head');
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-fixture-refresh-'));
  const objects = path.join(temporaryRoot, 'objects');
  const index = path.join(temporaryRoot, 'index');
  fs.mkdirSync(objects);
  try {
    const common = text(source, ['rev-parse', '--git-common-dir']);
    const env = environment(objects, index, path.resolve(repositoryRoot, common, 'objects'));
    const run = (args, input) => exec(repositoryRoot, args, input, env);
    text(run, ['cat-file', '-e', `${merge}^{commit}`]);
    text(run, ['cat-file', '-e', `${normalPrior}^{commit}`]);
    text(run, ['cat-file', '-e', `${stalePrior}^{commit}`]);
    const mergeTree = objectId(text(run, ['rev-parse', `${merge}^{tree}`]), 'merge-tree');
    if (text(run, ['ls-tree', mergeTree, '--', REFRESH_CONTRACT.canaryPath]) !== '')
      fail('fixture-canary-already-present');
    const initialBlob = objectId(
      text(run, ['hash-object', '-w', '--stdin'], REFRESH_CONTRACT.initialCanary),
      'fixture-canary',
    );
    const advancedBlob = objectId(
      text(run, ['hash-object', '-w', '--stdin'], REFRESH_CONTRACT.advancedCanary),
      'fixture-canary',
    );
    if (initialBlob === advancedBlob) fail('fixture-canary-distinct');
    const initialTree = writeTree(run, mergeTree, initialBlob);
    const normalHead = commit(
      repositoryRoot,
      initialTree,
      [merge, normalPrior],
      REFRESH_CONTRACT.normal,
      env,
    );
    const staleHead = commit(
      repositoryRoot,
      initialTree,
      [merge, stalePrior],
      REFRESH_CONTRACT.stale,
      env,
    );
    const advancedTree = writeTree(run, mergeTree, advancedBlob);
    const advancedHead = commit(
      repositoryRoot,
      advancedTree,
      [staleHead],
      REFRESH_CONTRACT.advance,
      env,
    );
    if (
      text(run, ['rev-parse', `${normalHead}^{tree}`]) !== initialTree ||
      text(run, ['rev-parse', `${staleHead}^{tree}`]) !== initialTree
    )
      fail('fixture-tree-identity');
    assertParentArray(run, normalHead, [merge, normalPrior], 'fixture-parent-order');
    assertParentArray(run, staleHead, [merge, stalePrior], 'fixture-parent-order');
    assertAddOnly(run, merge, normalHead, initialBlob);
    assertAddOnly(run, merge, staleHead, initialBlob);
    if (text(run, ['rev-parse', `${advancedHead}^{tree}`]) !== advancedTree)
      fail('fixture-advanced-tree');
    assertParentArray(run, advancedHead, [staleHead], 'fixture-advanced-parent-order');
    assertAdvanceOnly(run, staleHead, advancedHead, advancedBlob);
    const expected = Object.freeze({
      merge_sha: merge,
      merge_tree: mergeTree,
      normal: {
        prior_head: normalPrior,
        head: normalHead,
        tree: initialTree,
        parents: [merge, normalPrior],
      },
      stale: {
        prior_head: stalePrior,
        head: staleHead,
        tree: initialTree,
        parents: [merge, stalePrior],
        advanced_head: advancedHead,
        advanced_tree: advancedTree,
        advanced_parents: [staleHead],
      },
      canary: {
        path: REFRESH_CONTRACT.canaryPath,
        initial_blob: initialBlob,
        advanced_blob: advancedBlob,
      },
    });
    return Object.freeze({
      expected,
      runGit: run,
      dispose() {
        fs.rmSync(temporaryRoot, { recursive: true, force: true });
      },
    });
  } catch (error) {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
    if (
      error instanceof Error &&
      error.message.startsWith('APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID')
    )
      throw error;
    fail('fixture-materialization');
  }
}
