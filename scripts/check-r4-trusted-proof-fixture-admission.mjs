import { execFileSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { resolveTestedMainCheckout } from './check-r4-trusted-proof-fixture-refresh.mjs';

export const FROZEN_FIXTURES = Object.freeze({
  normalHead: '1dcec1b90429643338787fdb36fe33dfcac7dfa9',
  staleHead: '5dbda94d459e140aac5d18d2c0405287c62c5682',
  sharedTree: 'cb0737ec25d818d7c8d6a4668c1794dd157e25cf',
});

// The historical #225/#226 heads remain immutable coverage.  This separate
// pre-merge contract deliberately freezes only the canary bytes and the
// construction rule: a PR checkout can prove its final tree is admissible
// without pretending to know the future main-merge commit identity.
export const PROSPECTIVE_FIXTURE_CONTRACT = Object.freeze({
  canaryPath: 'proof/apr178-path-canary.txt',
  canaryMode: '100644',
  canaryBytes: Buffer.from('APR178_TOOL_DATA_CANARY\n', 'utf8'),
  authenticatedRestRequests: 180,
});

// These commits used to be retained only by short-lived proof branches.  The
// admission gate deliberately reconstructs them below rather than treating a
// still-present ref or loose object as an input to a normal CI run.
const FROZEN_FIXTURE_CONSTRUCTION = Object.freeze({
  parent: '1a15b59dd21fe0ff04d0e728680acaebfedb1195',
  canaryPath: 'proof/apr178-path-canary.txt',
  canaryMode: '100644',
  canaryBlob: '6fb1e09fc322bc85611172c171f4e3fce8bdee1c',
  canaryBytes: Buffer.from('APR178_TOOL_DATA_CANARY\n', 'utf8'),
  normal: Object.freeze({
    message: 'test: add normal trusted-proof canary\n',
    date: '2026-08-28T14:41:42+08:00',
  }),
  stale: Object.freeze({
    message: 'test: add stale trusted-proof canary\n',
    date: '2026-08-28T14:41:45+08:00',
  }),
  identity: Object.freeze({ name: 'Codex', email: 'codex@solusquest.local' }),
});

const MEBIBYTE = 1024 * 1024;
const expectedLimits = Object.freeze({
  TrackedPaths: 20_000,
  TreeMetadataBytes: 8 * MEBIBYTE,
  PathBytes: 1024,
  TreeDepth: 64,
  UniqueTreeAndBlobObjects: 4_000,
  GitObjectRequests: 4_096,
  HeadBlobBytes: 8 * MEBIBYTE,
  AggregateHeadBlobBytes: 256 * MEBIBYTE,
  MaterializedRootBytes: 256 * MEBIBYTE,
  GitObjectResponseBytes: 16 * MEBIBYTE,
  AggregateResponseBytes: 512 * MEBIBYTE,
});

const gitTreeJsonEnvelopeBytes = 8192;
const gitTreeEntryJsonEnvelopeBytes = 512;
const gitCommitJsonEnvelopeBytes = 16 * 1024;
const utf8 = new TextDecoder('utf-8', { fatal: true });
const frozenFixtureShape = Object.freeze({
  treeObjectsIncludingRoot: 178,
  regularPaths: 921,
  uniqueRegularBlobs: 910,
  authenticatedRestRequests: 180,
  anonymousCodeloadRequests: 1,
  baselinePerBlobAuthenticatedRestRequests: 1089,
});

function fail(code) {
  throw new Error(`APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID ${code}`);
}

function sha256(value) {
  return crypto.createHash('sha256').update(value).digest('hex');
}

function isObjectId(value) {
  return typeof value === 'string' && /^[0-9a-f]{40}$/u.test(value);
}

function parseProduct(expression, name) {
  const terms = expression
    .replaceAll('L', '')
    .trim()
    .split('*')
    .map((value) => value.trim());
  if (terms.length === 0 || terms.some((value) => !/^\d[\d_]*$/u.test(value))) {
    fail(`limits-expression-${name}`);
  }
  const result = terms.reduce((product, value) => product * Number(value.replaceAll('_', '')), 1);
  if (!Number.isSafeInteger(result) || result <= 0) fail(`limits-value-${name}`);
  return result;
}

export function loadProductionLimits(limitsPath) {
  let source;
  try {
    source = fs.readFileSync(limitsPath, 'utf8');
  } catch {
    fail('limits-unavailable');
  }
  const values = {};
  for (const name of Object.keys(expectedLimits)) {
    const match = source.match(
      new RegExp(`internal\\s+const\\s+(?:int|long)\\s+${name}\\s*=\\s*([^;]+);`, 'u'),
    );
    if (!match) fail(`limits-missing-${name}`);
    values[name] = parseProduct(match[1], name);
    if (values[name] !== expectedLimits[name]) fail(`limits-drift-${name}`);
  }
  return Object.freeze(values);
}

function defaultRunGit(repositoryRoot, args, input, environment = {}) {
  try {
    return execFileSync('git', args, {
      cwd: repositoryRoot,
      encoding: 'buffer',
      input,
      maxBuffer: 64 * MEBIBYTE,
      windowsHide: true,
      env: { ...process.env, ...environment },
    });
  } catch {
    fail('git-object-unavailable');
  }
}

function runTextWithEnvironment(runGit, args, input) {
  return runText(runGit, args, input);
}

function fixtureMaterializationEnvironment({
  objectDirectory,
  indexFile,
  alternateObjectDirectory,
}) {
  return {
    GIT_OBJECT_DIRECTORY: objectDirectory,
    GIT_INDEX_FILE: indexFile,
    GIT_ALTERNATE_OBJECT_DIRECTORIES: alternateObjectDirectory,
    GIT_CONFIG_NOSYSTEM: '1',
    // Keep a local gate independent from whichever user-wide Git identity or
    // configuration happens to be installed on the runner.
    GIT_CONFIG_GLOBAL: path.join(path.dirname(objectDirectory), 'empty-global-config'),
    GIT_TERMINAL_PROMPT: '0',
  };
}

function assertExactConstructedHead({ runGit, head, expected, expectedTree }) {
  if (head !== expected) fail('fixture-commit-identity');
  if (runText(runGit, ['rev-parse', `${head}^{tree}`]) !== expectedTree) {
    fail('fixture-tree-identity');
  }
  if (runText(runGit, ['rev-parse', `${head}^`]) !== FROZEN_FIXTURE_CONSTRUCTION.parent) {
    fail('fixture-parent-identity');
  }
  const rawDelta = runText(runGit, ['diff-tree', '--no-commit-id', '--raw', '-r', head]);
  const expectedRaw = [
    `:000000 ${FROZEN_FIXTURE_CONSTRUCTION.canaryMode}`,
    `${'0'.repeat(40)} ${FROZEN_FIXTURE_CONSTRUCTION.canaryBlob}`,
    `A\t${FROZEN_FIXTURE_CONSTRUCTION.canaryPath}`,
  ].join(' ');
  if (rawDelta !== expectedRaw) fail('fixture-canary-delta');
  const canaryEntry = runText(runGit, [
    'ls-tree',
    expectedTree,
    '--',
    FROZEN_FIXTURE_CONSTRUCTION.canaryPath,
  ]);
  const expectedEntry = [
    `${FROZEN_FIXTURE_CONSTRUCTION.canaryMode} blob`,
    `${FROZEN_FIXTURE_CONSTRUCTION.canaryBlob}\t${FROZEN_FIXTURE_CONSTRUCTION.canaryPath}`,
  ].join(' ');
  if (canaryEntry !== expectedEntry) fail('fixture-canary-mode');
  const canaryBytes = Number(
    runText(runGit, ['cat-file', '-s', FROZEN_FIXTURE_CONSTRUCTION.canaryBlob]),
  );
  if (canaryBytes !== FROZEN_FIXTURE_CONSTRUCTION.canaryBytes.length) fail('fixture-canary-bytes');
}

/**
 * Rebuild the two frozen commits in a throw-away object and index directory.
 * The production gate only receives the returned runner; it never resolves a
 * temporary branch/ref from the working repository.
 */
export function materializeFrozenFixtures({
  repositoryRoot = path.resolve(import.meta.dirname, '..'),
  sourceRunGit = (args, input) => defaultRunGit(repositoryRoot, args, input),
  commitMetadata = (metadata) => metadata,
  identity = FROZEN_FIXTURE_CONSTRUCTION.identity,
} = {}) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-fixture-admission-'));
  const objectDirectory = path.join(temporaryRoot, 'objects');
  const indexFile = path.join(temporaryRoot, 'index');
  fs.mkdirSync(objectDirectory);

  try {
    const commonDirectory = runTextWithEnvironment(sourceRunGit, ['rev-parse', '--git-common-dir']);
    const alternateObjectDirectory = path.resolve(repositoryRoot, commonDirectory, 'objects');
    const environment = fixtureMaterializationEnvironment({
      objectDirectory,
      indexFile,
      alternateObjectDirectory,
    });
    const runGit = (args, input) => defaultRunGit(repositoryRoot, args, input, environment);

    // The only inherited object is the frozen main ancestor.  All new objects
    // live under the temporary GIT_OBJECT_DIRECTORY and are removed in dispose.
    runText(runGit, ['cat-file', '-e', `${FROZEN_FIXTURE_CONSTRUCTION.parent}^{commit}`]);
    runText(runGit, ['read-tree', `${FROZEN_FIXTURE_CONSTRUCTION.parent}^{tree}`]);
    const canaryBlob = runText(
      runGit,
      ['hash-object', '-w', '--stdin'],
      FROZEN_FIXTURE_CONSTRUCTION.canaryBytes,
    );
    if (canaryBlob !== FROZEN_FIXTURE_CONSTRUCTION.canaryBlob) fail('fixture-canary-bytes');
    runText(runGit, [
      'update-index',
      '--add',
      '--cacheinfo',
      [
        FROZEN_FIXTURE_CONSTRUCTION.canaryMode,
        canaryBlob,
        FROZEN_FIXTURE_CONSTRUCTION.canaryPath,
      ].join(','),
    ]);
    const tree = runText(runGit, ['write-tree']);
    if (tree !== FROZEN_FIXTURES.sharedTree) fail('fixture-tree-identity');

    const createCommit = (metadata) => {
      const commitEnvironment = {
        ...environment,
        GIT_AUTHOR_NAME: identity.name,
        GIT_AUTHOR_EMAIL: identity.email,
        GIT_AUTHOR_DATE: metadata.date,
        GIT_COMMITTER_NAME: identity.name,
        GIT_COMMITTER_EMAIL: identity.email,
        GIT_COMMITTER_DATE: metadata.date,
      };
      return runText(
        (args, input) => defaultRunGit(repositoryRoot, args, input, commitEnvironment),
        ['commit-tree', tree, '-p', FROZEN_FIXTURE_CONSTRUCTION.parent],
        Buffer.from(metadata.message, 'utf8'),
      );
    };
    const normalHead = createCommit(commitMetadata(FROZEN_FIXTURE_CONSTRUCTION.normal, 'normal'));
    const staleHead = createCommit(commitMetadata(FROZEN_FIXTURE_CONSTRUCTION.stale, 'stale'));
    assertExactConstructedHead({
      runGit,
      head: normalHead,
      expected: FROZEN_FIXTURES.normalHead,
      expectedTree: tree,
    });
    assertExactConstructedHead({
      runGit,
      head: staleHead,
      expected: FROZEN_FIXTURES.staleHead,
      expectedTree: tree,
    });
    return Object.freeze({
      runGit,
      dispose() {
        fs.rmSync(temporaryRoot, { recursive: true, force: true });
      },
    });
  } catch (error) {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
    if (
      error instanceof Error &&
      error.message.startsWith('APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID')
    ) {
      throw error;
    }
    fail('fixture-materialization');
  }
}

function runText(runGit, args, input) {
  let output;
  try {
    output = runGit(args, input);
  } catch {
    fail('git-object-unavailable');
  }
  if (!Buffer.isBuffer(output)) fail('git-output');
  try {
    return utf8.decode(output).trim();
  } catch {
    fail('git-encoding');
  }
}

function runBytes(runGit, args, input) {
  let output;
  try {
    output = runGit(args, input);
  } catch {
    fail('git-object-unavailable');
  }
  if (!Buffer.isBuffer(output)) fail('git-output');
  return output;
}

function parseTreeEntries(output) {
  if (output.length === 0 || output.at(-1) !== 0) fail('tree-list');
  const entries = [];
  for (const raw of output.subarray(0, -1).toString('binary').split('\0')) {
    const entry = Buffer.from(raw, 'binary');
    const tab = entry.indexOf(0x09);
    if (tab <= 0 || tab === entry.length - 1) fail('tree-entry');
    const parts = entry.subarray(0, tab).toString('ascii').trim().split(/\s+/u);
    if (parts.length !== 4) fail('tree-header');
    const [mode, type, sha, sizeText] = parts;
    const pathname = entry.subarray(tab + 1);
    if (!isObjectId(sha) || pathname.includes(0) || pathname.includes(0x5c)) fail('tree-entry');
    try {
      utf8.decode(pathname);
    } catch {
      fail('tree-path-encoding');
    }
    const slash = pathname.lastIndexOf(0x2f);
    const name = slash < 0 ? pathname : pathname.subarray(slash + 1);
    const parent = slash < 0 ? Buffer.alloc(0) : pathname.subarray(0, slash);
    if (
      name.length === 0 ||
      name.equals(Buffer.from('.')) ||
      name.equals(Buffer.from('..')) ||
      pathname.at(0) === 0x2f
    ) {
      fail('tree-path');
    }
    let size = null;
    if (sizeText !== '-') {
      if (!/^\d+$/u.test(sizeText)) fail('tree-size');
      size = Number(sizeText);
      if (!Number.isSafeInteger(size) || size < 0) fail('tree-size');
    }
    if (
      !(
        (mode === '040000' && type === 'tree' && size === null) ||
        ((mode === '100644' || mode === '100755' || mode === '120000') &&
          type === 'blob' &&
          size !== null) ||
        (mode === '160000' && type === 'commit' && size === null)
      )
    ) {
      fail('tree-shape');
    }
    entries.push({ mode, type, sha, size, pathname, parent, name });
  }
  return entries;
}

function githubTreeJsonUpperBound(entries) {
  return entries.reduce(
    (total, entry) => total + gitTreeEntryJsonEnvelopeBytes + entry.name.length * 6,
    gitTreeJsonEnvelopeBytes,
  );
}

function githubCommitJsonUpperBound(commitSize) {
  if (!Number.isSafeInteger(commitSize) || commitSize < 0) fail('commit-size');
  return gitCommitJsonEnvelopeBytes + commitSize * 12;
}

function batchObjects(runGit, objectIds) {
  const input = Buffer.from(`${objectIds.join('\n')}\n`, 'ascii');
  const output = runText(
    runGit,
    ['cat-file', '--batch-check=%(objectname) %(objecttype) %(objectsize)'],
    input,
  );
  const records = output.split('\n');
  if (records.length !== objectIds.length) fail('object-count');
  const values = new Map();
  for (let index = 0; index < records.length; index += 1) {
    const parts = records[index].trim().split(/\s+/u);
    if (parts.length !== 3 || parts[0] !== objectIds[index]) fail('object-order');
    const [sha, type, sizeText] = parts;
    if (!isObjectId(sha) || (type !== 'commit' && type !== 'tree' && type !== 'blob')) {
      fail('object-unavailable');
    }
    if (!/^\d+$/u.test(sizeText)) fail('object-size');
    const size = Number(sizeText);
    if (!Number.isSafeInteger(size) || size < 0) fail('object-size');
    values.set(sha, { type, size });
  }
  return values;
}

function assertWithin(value, maximum, code) {
  if (!Number.isSafeInteger(value) || value < 0 || value > maximum) fail(code);
}

function analyzeTree({
  head,
  tree,
  entries,
  objectFacts,
  limits,
  runGit,
  archiveTarget = head,
  requireFrozenShape = false,
}) {
  const treeObjects = new Set([tree]);
  const blobObjects = new Map();
  const regularBlobObjects = new Map();
  const paths = new Set();
  const treeEntries = new Map([[Buffer.alloc(0).toString('hex'), []]]);
  let treeMetadataBytes = 0;
  let logicalHeadBlobBytes = 0;
  let materializedRootBytes = 0;
  let maximumBlobBytes = 0;
  let regularPaths = 0;

  for (const entry of entries) {
    const pathnameKey = entry.pathname.toString('hex');
    if (paths.has(pathnameKey)) fail('tree-path-duplicate');
    paths.add(pathnameKey);
    const depth = entry.pathname.filter((value) => value === 0x2f).length + 1;
    assertWithin(entry.pathname.length, limits.PathBytes, 'path-bytes');
    assertWithin(depth, limits.TreeDepth, 'tree-depth');
    treeMetadataBytes += entry.pathname.length;

    const parentKey = entry.parent.toString('hex');
    const direct = treeEntries.get(parentKey);
    if (!direct) fail('tree-parent');
    direct.push(entry);

    if (entry.type === 'tree') {
      if (treeObjects.has(entry.sha)) fail('tree-object-duplicate');
      treeObjects.add(entry.sha);
      treeEntries.set(pathnameKey, []);
      continue;
    }
    if (entry.type === 'blob') {
      const existing = blobObjects.get(entry.sha);
      if (existing !== undefined && existing !== entry.size) fail('blob-size-drift');
      blobObjects.set(entry.sha, entry.size);
      if (entry.mode === '100644' || entry.mode === '100755') {
        regularPaths += 1;
        assertWithin(entry.size, limits.HeadBlobBytes, 'head-blob-bytes');
        logicalHeadBlobBytes += entry.size;
        materializedRootBytes += entry.size;
        maximumBlobBytes = Math.max(maximumBlobBytes, entry.size);
        regularBlobObjects.set(entry.sha, entry.size);
      }
    }
  }

  for (const treeSha of treeObjects) {
    const fact = objectFacts.get(treeSha);
    if (!fact || fact.type !== 'tree') fail('tree-object-unavailable');
  }
  for (const [blobSha, declaredSize] of blobObjects) {
    const fact = objectFacts.get(blobSha);
    if (!fact || fact.type !== 'blob' || fact.size !== declaredSize)
      fail('blob-object-unavailable');
  }

  const uniqueObjects = treeObjects.size + blobObjects.size;
  const authenticatedRestRequests = 1 + treeObjects.size + 1;
  const baselinePerBlobAuthenticatedRestRequests = 1 + treeObjects.size + regularBlobObjects.size;
  const commitFact = objectFacts.get(head);
  if (!commitFact || commitFact.type !== 'commit') fail('commit-object-unavailable');
  const commitJsonResponseBytes = githubCommitJsonUpperBound(commitFact.size);
  let allTreeJsonResponseBytes = 0;
  let maximumResponseBytes = commitJsonResponseBytes;

  for (const [treePath, direct] of treeEntries) {
    const treeSha =
      treePath.length === 0
        ? tree
        : entries.find((entry) => entry.pathname.toString('hex') === treePath)?.sha;
    if (!treeSha || !treeObjects.has(treeSha)) fail('tree-response');
    const responseBytes = githubTreeJsonUpperBound(direct);
    maximumResponseBytes = Math.max(maximumResponseBytes, responseBytes);
    allTreeJsonResponseBytes += responseBytes;
  }
  const archiveBytes = runBytes(runGit, ['archive', '--format=tar.gz', archiveTarget]).length;
  if (archiveBytes === 0) fail('head-archive-empty');
  maximumResponseBytes = Math.max(maximumResponseBytes, archiveBytes);
  const aggregateResponseBytes = commitJsonResponseBytes + allTreeJsonResponseBytes + archiveBytes;

  assertWithin(paths.size, limits.TrackedPaths, 'tracked-paths');
  assertWithin(treeMetadataBytes, limits.TreeMetadataBytes, 'tree-metadata-bytes');
  assertWithin(uniqueObjects, limits.UniqueTreeAndBlobObjects, 'unique-objects');
  assertWithin(authenticatedRestRequests, limits.GitObjectRequests, 'git-object-requests');
  assertWithin(logicalHeadBlobBytes, limits.AggregateHeadBlobBytes, 'aggregate-head-blob-bytes');
  assertWithin(materializedRootBytes, limits.MaterializedRootBytes, 'materialized-root-bytes');
  assertWithin(maximumResponseBytes, limits.GitObjectResponseBytes, 'git-object-response-bytes');
  assertWithin(aggregateResponseBytes, limits.AggregateResponseBytes, 'aggregate-response-bytes');

  const metrics = {
    tracked_paths: paths.size,
    tree_metadata_bytes: treeMetadataBytes,
    unique_tree_and_blob_objects: uniqueObjects,
    tree_objects_including_root: treeObjects.size,
    regular_paths: regularPaths,
    unique_regular_blobs: regularBlobObjects.size,
    admitted_head_source_authenticated_rest_requests: authenticatedRestRequests,
    admitted_head_source_blob_rest_requests: 0,
    admitted_head_source_anonymous_codeload_requests: frozenFixtureShape.anonymousCodeloadRequests,
    admitted_head_source_fallback_requests: 0,
    admitted_head_source_archive_credential_forwarded: false,
    baseline_per_blob_authenticated_rest_requests: baselinePerBlobAuthenticatedRestRequests,
    aggregate_head_blob_bytes: logicalHeadBlobBytes,
    materialized_root_bytes: materializedRootBytes,
    maximum_blob_bytes: maximumBlobBytes,
    head_archive_inflated_regular_blob_bytes: logicalHeadBlobBytes,
    commit_json_response_bytes: commitJsonResponseBytes,
    all_tree_json_response_bytes: allTreeJsonResponseBytes,
    head_archive_compressed_bytes: archiveBytes,
    maximum_head_source_response_bytes: maximumResponseBytes,
    aggregate_head_source_response_bytes: aggregateResponseBytes,
  };
  if (
    requireFrozenShape &&
    (metrics.tree_objects_including_root !== frozenFixtureShape.treeObjectsIncludingRoot ||
      metrics.regular_paths !== frozenFixtureShape.regularPaths ||
      metrics.unique_regular_blobs !== frozenFixtureShape.uniqueRegularBlobs ||
      metrics.admitted_head_source_authenticated_rest_requests !==
        frozenFixtureShape.authenticatedRestRequests ||
      metrics.admitted_head_source_anonymous_codeload_requests !==
        frozenFixtureShape.anonymousCodeloadRequests ||
      metrics.baseline_per_blob_authenticated_rest_requests !==
        frozenFixtureShape.baselinePerBlobAuthenticatedRestRequests)
  ) {
    fail('fixture-shape');
  }
  return metrics;
}

export function admitFrozenFixtures({
  repositoryRoot = path.resolve(import.meta.dirname, '..'),
  fixtures = FROZEN_FIXTURES,
  limits,
  materialize = materializeFrozenFixtures,
  interceptMaterializedGit,
} = {}) {
  if (
    !fixtures ||
    !isObjectId(fixtures.normalHead) ||
    !isObjectId(fixtures.staleHead) ||
    !isObjectId(fixtures.sharedTree) ||
    fixtures.normalHead !== FROZEN_FIXTURES.normalHead ||
    fixtures.staleHead !== FROZEN_FIXTURES.staleHead ||
    fixtures.sharedTree !== FROZEN_FIXTURES.sharedTree
  ) {
    fail('fixture-identity');
  }
  const materialized = materialize({ repositoryRoot });
  try {
    const materializedRunGit = materialized?.runGit;
    if (typeof materializedRunGit !== 'function' || typeof materialized?.dispose !== 'function') {
      fail('fixture-materialization');
    }
    const runGit = interceptMaterializedGit
      ? (args, input) => interceptMaterializedGit(materializedRunGit, args, input)
      : materializedRunGit;
    const productionLimits =
      limits ??
      loadProductionLimits(
        path.join(
          repositoryRoot,
          'runtime',
          'src',
          'AgenticPrReview.Runtime',
          'Host',
          'Action',
          'Snapshot',
          'ReviewedContentLimits.cs',
        ),
      );
    for (const [name, value] of Object.entries(expectedLimits)) {
      if (productionLimits[name] !== value) fail(`limits-drift-${name}`);
    }

    const heads = [fixtures.normalHead, fixtures.staleHead];
    for (const head of heads) {
      const resolvedTree = runText(runGit, ['rev-parse', `${head}^{tree}`]);
      if (resolvedTree !== fixtures.sharedTree) fail('fixture-tree-identity');
    }
    const entries = parseTreeEntries(
      runBytes(runGit, ['ls-tree', '-r', '-t', '-l', '-z', fixtures.sharedTree]),
    );
    if (entries.length === 0) fail('tree-empty');
    const treeAndBlobObjectIds = new Set([fixtures.sharedTree]);
    for (const entry of entries) {
      if (entry.type === 'tree' || entry.type === 'blob') treeAndBlobObjectIds.add(entry.sha);
    }
    const objectFacts = batchObjects(runGit, [...heads, ...[...treeAndBlobObjectIds].sort()]);
    const normal = analyzeTree({
      head: fixtures.normalHead,
      tree: fixtures.sharedTree,
      entries,
      objectFacts,
      limits: productionLimits,
      runGit,
      requireFrozenShape: true,
    });
    const stale = analyzeTree({
      head: fixtures.staleHead,
      tree: fixtures.sharedTree,
      entries,
      objectFacts,
      limits: productionLimits,
      runGit,
      requireFrozenShape: true,
    });
    return {
      kind: 'apr-r4-trusted-proof-frozen-fixture-admission-v2',
      normal_head: fixtures.normalHead,
      stale_head: fixtures.staleHead,
      shared_tree: fixtures.sharedTree,
      limits: {
        head_blob_bytes: productionLimits.HeadBlobBytes,
        git_object_response_bytes: productionLimits.GitObjectResponseBytes,
        aggregate_head_blob_bytes: productionLimits.AggregateHeadBlobBytes,
        materialized_root_bytes: productionLimits.MaterializedRootBytes,
        aggregate_response_bytes: productionLimits.AggregateResponseBytes,
        git_object_requests: productionLimits.GitObjectRequests,
        unique_tree_and_blob_objects: productionLimits.UniqueTreeAndBlobObjects,
        tracked_paths: productionLimits.TrackedPaths,
        tree_metadata_bytes: productionLimits.TreeMetadataBytes,
        path_bytes: productionLimits.PathBytes,
        tree_depth: productionLimits.TreeDepth,
      },
      normal_metrics: normal,
      stale_metrics: stale,
    };
  } finally {
    materialized.dispose();
  }
}

function assertProductionLimits(limits) {
  for (const [name, value] of Object.entries(expectedLimits)) {
    if (limits[name] !== value) fail(`limits-drift-${name}`);
  }
  return limits;
}

function prospectiveMaterializationEnvironment({
  objectDirectory,
  indexFile,
  alternateObjectDirectory,
}) {
  return fixtureMaterializationEnvironment({
    objectDirectory,
    indexFile,
    alternateObjectDirectory,
  });
}

function assertProspectiveCanaryDelta({ runGit, baseTree, admittedTree, canaryBlob }) {
  const rawDelta = runText(runGit, [
    'diff-tree',
    '--no-commit-id',
    '--raw',
    '-r',
    baseTree,
    admittedTree,
  ]);
  const expectedDelta = [
    `:000000 ${PROSPECTIVE_FIXTURE_CONTRACT.canaryMode}`,
    `${'0'.repeat(40)} ${canaryBlob}`,
    `A\t${PROSPECTIVE_FIXTURE_CONTRACT.canaryPath}`,
  ].join(' ');
  if (rawDelta !== expectedDelta) fail('prospective-base-tree-or-path-drift');
  const entry = runText(runGit, [
    'ls-tree',
    admittedTree,
    '--',
    PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
  ]);
  const expectedEntry = [
    `${PROSPECTIVE_FIXTURE_CONTRACT.canaryMode} blob`,
    `${canaryBlob}\t${PROSPECTIVE_FIXTURE_CONTRACT.canaryPath}`,
  ].join(' ');
  if (entry !== expectedEntry) fail('prospective-canary-mode-or-path');
  const canaryBytes = Number(runText(runGit, ['cat-file', '-s', canaryBlob]));
  if (canaryBytes !== PROSPECTIVE_FIXTURE_CONTRACT.canaryBytes.length)
    fail('prospective-canary-bytes');
}

/**
 * Materialize the fixture tree that a post-merge executor will later turn into
 * a two-parent fixture commit.  This does not create or report a commit:
 * pre-merge CI only has authority over the final prospective tree.
 *
 * The temporary index starts at the real current index, then stages the
 * worktree into its own object database.  That makes both staged and
 * unstaged PR changes part of the admitted base tree without mutating the
 * caller's index, refs, or object database.
 */
export function materializeProspectiveFixture({
  repositoryRoot = path.resolve(import.meta.dirname, '..'),
  baseHead,
  sourceRunGit = (args, input) => defaultRunGit(repositoryRoot, args, input),
  interceptMaterializedGit,
} = {}) {
  const temporaryRoot = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-prospective-fixture-'));
  const objectDirectory = path.join(temporaryRoot, 'objects');
  const indexFile = path.join(temporaryRoot, 'index');
  fs.mkdirSync(objectDirectory);
  try {
    const checkoutHead = baseHead ?? runText(sourceRunGit, ['rev-parse', 'HEAD^{commit}']);
    if (!isObjectId(checkoutHead)) fail('prospective-base-head');
    const resolved =
      baseHead === undefined
        ? resolveTestedMainCheckout({ runGit: sourceRunGit, head: checkoutHead })
        : {
            testedMainHead: checkoutHead,
            testedMainTree: runText(sourceRunGit, ['rev-parse', `${checkoutHead}^{tree}`]),
            includeWorktree: false,
          };
    const sourceHead = resolved.testedMainHead;
    const sourceTree = resolved.testedMainTree;
    if (!isObjectId(sourceHead) || !isObjectId(sourceTree)) fail('prospective-base-tree');
    if (
      runText(sourceRunGit, [
        'ls-tree',
        sourceTree,
        '--',
        PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
      ]) !== ''
    ) {
      fail('prospective-canary-already-present');
    }
    const commonDirectory = runText(sourceRunGit, ['rev-parse', '--git-common-dir']);
    const environment = prospectiveMaterializationEnvironment({
      objectDirectory,
      indexFile,
      alternateObjectDirectory: path.resolve(repositoryRoot, commonDirectory, 'objects'),
    });
    const directRunGit = (args, input) => defaultRunGit(repositoryRoot, args, input, environment);
    const runGit = interceptMaterializedGit
      ? (args, input) => interceptMaterializedGit(directRunGit, args, input)
      : directRunGit;

    if (resolved.includeWorktree) {
      const sourceIndexPath = runText(sourceRunGit, ['rev-parse', '--git-path', 'index']);
      if (sourceIndexPath.length === 0) fail('prospective-index-unavailable');
      const resolvedSourceIndex = path.resolve(repositoryRoot, sourceIndexPath);
      if (fs.existsSync(resolvedSourceIndex)) {
        fs.copyFileSync(resolvedSourceIndex, indexFile);
      } else {
        // A fresh checkout without an index still has a well-defined final
        // worktree base. Normal PR runners take the copying branch above.
        runText(runGit, ['read-tree', sourceTree]);
      }
      if (runText(runGit, ['ls-files', '--unmerged']) !== '') fail('prospective-unmerged-index');
      runText(runGit, ['add', '--all']);
    } else {
      // Explicit bases and enrolled fixture/merge checkouts use only the
      // resolved committed main tree. Their canary-bearing index/worktree is
      // never staged as candidate content.
      runText(runGit, ['read-tree', sourceTree]);
    }
    const prospectiveBaseTree = runText(runGit, ['write-tree']);
    if (!isObjectId(prospectiveBaseTree)) fail('prospective-base-tree');
    if (
      runText(runGit, [
        'ls-tree',
        prospectiveBaseTree,
        '--',
        PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
      ]) !== ''
    ) {
      fail('prospective-canary-already-present');
    }

    runText(runGit, ['read-tree', prospectiveBaseTree]);
    const canaryBlob = runText(
      runGit,
      ['hash-object', '-w', '--stdin'],
      PROSPECTIVE_FIXTURE_CONTRACT.canaryBytes,
    );
    if (!isObjectId(canaryBlob)) fail('prospective-canary-bytes');
    runText(runGit, [
      'update-index',
      '--add',
      '--cacheinfo',
      [
        PROSPECTIVE_FIXTURE_CONTRACT.canaryMode,
        canaryBlob,
        PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
      ].join(','),
    ]);
    const admittedTree = runText(runGit, ['write-tree']);
    if (!isObjectId(admittedTree)) fail('prospective-admitted-tree');
    assertProspectiveCanaryDelta({
      runGit,
      baseTree: prospectiveBaseTree,
      admittedTree,
      canaryBlob,
    });

    return Object.freeze({
      sourceHead,
      prospectiveBaseTree,
      admittedTree,
      canaryBlob,
      runGit,
      dispose() {
        fs.rmSync(temporaryRoot, { recursive: true, force: true });
      },
    });
  } catch (error) {
    fs.rmSync(temporaryRoot, { recursive: true, force: true });
    if (
      error instanceof Error &&
      error.message.startsWith('APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID')
    ) {
      throw error;
    }
    fail('prospective-fixture-materialization');
  }
}

export function admitProspectiveFixture({
  repositoryRoot = path.resolve(import.meta.dirname, '..'),
  limits,
  materialize = materializeProspectiveFixture,
  ...options
} = {}) {
  const productionLimits = assertProductionLimits(
    limits ??
      loadProductionLimits(
        path.join(
          repositoryRoot,
          'runtime',
          'src',
          'AgenticPrReview.Runtime',
          'Host',
          'Action',
          'Snapshot',
          'ReviewedContentLimits.cs',
        ),
      ),
  );
  const materialized = materialize({ repositoryRoot, ...options });
  try {
    if (
      !materialized ||
      !isObjectId(materialized.sourceHead) ||
      !isObjectId(materialized.prospectiveBaseTree) ||
      !isObjectId(materialized.admittedTree) ||
      !isObjectId(materialized.canaryBlob) ||
      typeof materialized.runGit !== 'function' ||
      typeof materialized.dispose !== 'function'
    ) {
      fail('prospective-fixture-materialization');
    }
    const entries = parseTreeEntries(
      runBytes(materialized.runGit, ['ls-tree', '-r', '-t', '-l', '-z', materialized.admittedTree]),
    );
    if (entries.length === 0) fail('prospective-tree-empty');
    const objectIds = new Set([materialized.admittedTree]);
    for (const entry of entries) {
      if (entry.type === 'tree' || entry.type === 'blob') objectIds.add(entry.sha);
    }
    const objectFacts = batchObjects(materialized.runGit, [
      materialized.sourceHead,
      ...[...objectIds].sort(),
    ]);
    const metrics = analyzeTree({
      head: materialized.sourceHead,
      tree: materialized.admittedTree,
      entries,
      objectFacts,
      limits: productionLimits,
      runGit: materialized.runGit,
      archiveTarget: materialized.admittedTree,
    });
    if (
      metrics.admitted_head_source_authenticated_rest_requests !==
        PROSPECTIVE_FIXTURE_CONTRACT.authenticatedRestRequests ||
      metrics.admitted_head_source_anonymous_codeload_requests !== 1 ||
      metrics.admitted_head_source_blob_rest_requests !== 0 ||
      metrics.admitted_head_source_archive_credential_forwarded !== false
    ) {
      fail('prospective-request-archive-shape');
    }
    return Object.freeze({
      kind: 'apr-r4-trusted-proof-prospective-fixture-admission-v1',
      prospective_base_tree: materialized.prospectiveBaseTree,
      admitted_tree: materialized.admittedTree,
      canary: Object.freeze({
        path: PROSPECTIVE_FIXTURE_CONTRACT.canaryPath,
        mode: PROSPECTIVE_FIXTURE_CONTRACT.canaryMode,
        bytes: PROSPECTIVE_FIXTURE_CONTRACT.canaryBytes.length,
        sha256: sha256(PROSPECTIVE_FIXTURE_CONTRACT.canaryBytes),
        blob: materialized.canaryBlob,
      }),
      metrics,
    });
  } finally {
    materialized.dispose();
  }
}

export function admitTrustedProofFixtures(options = {}) {
  return Object.freeze({
    kind: 'apr-r4-trusted-proof-fixture-admission-v3',
    historical: admitFrozenFixtures(options),
    prospective: admitProspectiveFixture(options),
  });
}

function main() {
  if (process.argv.length !== 2) fail('usage');
  const receipt = admitTrustedProofFixtures();
  process.stdout.write(`APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION ${JSON.stringify(receipt)}\n`);
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    main();
  } catch (error) {
    process.stderr.write(
      `${error instanceof Error ? error.message : 'APR_R4_TRUSTED_PROOF_FIXTURE_ADMISSION_INVALID'}\n`,
    );
    process.exitCode = 1;
  }
}
