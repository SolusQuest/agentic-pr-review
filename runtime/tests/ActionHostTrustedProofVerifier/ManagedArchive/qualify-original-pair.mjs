// Trusted preparation adapter for the existing original-archive consumer.
// This module never builds B, changes T, or accepts consumer transport overrides.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { copyFile, mkdir, readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import {
  canonicalJson,
  MANAGED_LIMITS,
  readBoundedFile,
  sha256,
} from '../../../../scripts/release/build-payload.format.mjs';
import { CAPS, inspectCandidate } from '../../../../scripts/release/candidate.mjs';
import { ACTION_ROOT, ACTION_ASSETS } from '../../../../scripts/release/finalized-pair.format.mjs';
import { fixtureRuntimePolicy, runIsolatedFixture } from './verify-network.mjs';
import { prepareCutoverSupport } from './cutover-proof.mjs';
import { originalCases, ORIGINAL_PAIR_CASE_COUNT } from './qualification-cases.mjs';

// Accepted actual Runner/H2 installed Action, retained on main. Its seven
// composition assets equal the e96876a Action in Runner run 39972608852.
// Map/B acquisition is deliberately outside this evidence reuse.
export const RUNNER_ACTION = '5207bf8058b3d1301c0a6e805e7f21334832223e';
export const RUNNER_RUNS = Object.freeze(['39972608852', '40061259888', '40061741768']);
export function gitBlob(repo, commit, path, cap) {
  assert.match(commit, /^[a-f0-9]{40}$/);
  const options = {
    cwd: repo,
    env: { PATH: process.env.PATH },
    timeout: 30_000,
    maxBuffer: cap,
    stdio: ['ignore', 'pipe', 'pipe'],
  };
  const row = execFileSync(
    'git',
    ['--no-replace-objects', 'ls-tree', '-z', commit, '--', path],
    options,
  ).toString();
  assert.match(row, /^100644 blob [a-f0-9]{40}\t[^\0]+\0$/);
  assert.equal(row.slice(row.indexOf('\t') + 1, -1), path);
  const bytes = execFileSync(
    'git',
    ['--no-replace-objects', 'cat-file', 'blob', `${commit}:${path}`],
    options,
  );
  assert.ok(bytes.length > 0 && bytes.length <= cap);
  return bytes;
}
export function runnerEvidence(repo, pair) {
  const unchanged = ACTION_ASSETS.filter(([name]) => name !== 'payload-map.json').every(
    ([name, cap]) =>
      gitBlob(repo, pair.action.commit, `${ACTION_ROOT}/${name}`, cap).equals(
        gitBlob(repo, RUNNER_ACTION, `${ACTION_ROOT}/${name}`, cap),
      ),
  );
  return { unchanged, actionCommit: RUNNER_ACTION, runs: RUNNER_RUNS, assetCount: 7 };
}
export function supportsRuntime(pair) {
  return canonicalJson(pair.action.runtime.policy) === canonicalJson(fixtureRuntimePolicy);
}
export async function prepareOriginalPair(root, input, verified, builderEnv) {
  const { pair, pairBytes } = verified;
  const archive = await readBoundedFile(input.archivePath, MANAGED_LIMITS.archive);
  const candidateBytes = await readBoundedFile(input.candidatePath, CAPS.metadata);
  const mapBytes = await readBoundedFile(input.mapPath, CAPS.map);
  const c = inspectCandidate(archive, candidateBytes, mapBytes, pair.payload.candidateSha256);
  assert.equal(sha256(archive), pair.payload.archiveSha256);
  assert.equal(sha256(mapBytes), pair.payload.proposedMapSha256);
  const receiptBytes = await readBoundedFile(input.receiptPath, CAPS.metadata);
  assert.equal(canonicalJson(JSON.parse(receiptBytes)), canonicalJson(c.receipt));
  await mkdir(join(root, 'package'), { mode: 0o700 });
  await copyFile(input.archivePath, join(root, 'package', pair.payload.archiveName));
  await copyFile(input.receiptPath, join(root, 'package/receipt.json'));
  await writeFile(join(root, 'original-pair.json'), pairBytes, { flag: 'wx', mode: 0o600 });
  for (const asset of pair.action.assets) {
    const bytes = gitBlob(
      input.repo,
      pair.action.commit,
      `${ACTION_ROOT}/${asset.path}`,
      ACTION_ASSETS.find(([name]) => name === asset.path)[1],
    );
    assert.equal(sha256(bytes), asset.sha256);
    assert.equal(bytes.length, asset.size);
    const target = join(root, 'action-assets', asset.path);
    await mkdir(join(target, '..'), { recursive: true, mode: 0o700 });
    await writeFile(target, bytes, { flag: 'wx', mode: 0o600 });
  }
  // Only public nonmanaged input/readback utilities and the pinned genuine old
  // SESSION producer. No managed PreparationProbe or linked managed fault build.
  await prepareCutoverSupport(root, input.repo, builderEnv, { originalPair: true });
  await writeFile(
    join(root, 'archive-settings.json'),
    JSON.stringify({
      role: 'first',
      source: pair.payload.identity.sourceCommit,
      tree: pair.payload.identity.sourceTree,
      compiled: { compiledIdentity: pair.payload.identity.informationalVersion },
      repo: input.repo,
      originalPair: { pairSha256: input.pairSha256 },
    }),
    { flag: 'wx', mode: 0o600 },
  );
}
export async function executeOriginalPair(input, verified) {
  assert.ok(supportsRuntime(verified.pair), 'qualification_runtime_evidence_required');
  return runIsolatedFixture({
    childName: 'archive-child.mjs',
    prefix: 'APR_D5_MANAGED_ARCHIVE ',
    maximumSeconds: 5400,
    archiveRole: 'first',
    maximumCases: ORIGINAL_PAIR_CASE_COUNT,
    returnObservation: true,
    prepare: (root, { builderEnv }) => prepareOriginalPair(root, input, verified, builderEnv),
  });
}

const statuses = new Set([
  'reviewed',
  'reviewed_with_inline_warnings',
  'skipped_untrusted_event',
  'skipped_fork',
  'skipped_draft',
  'skipped_closed',
  'configuration_invalid',
  'authorization_failed',
  'credentials_missing',
  'snapshot_incomplete',
  'provider_failed',
  'agent_result_invalid',
  'stale_head',
  'state_conflict',
  'sticky_publication_failed',
  'outcome_ambiguous',
  'cancelled',
  'internal_failure',
  'failed',
]);
export function originalPairObservations(facts, pair, pairSha256) {
  const q = facts?.qualification_pair;
  assert.deepEqual(q, {
    pair_sha256: pairSha256,
    source_commit: pair.payload.identity.sourceCommit,
    source_tree: pair.payload.identity.sourceTree,
    action_commit: pair.action.commit,
    workflow_commit: pair.producer.workflowCommit,
    map_sha256: pair.payload.proposedMapSha256,
    assets_sha256: pair.action.assetsSha256,
    original_bytes_unchanged: true,
    output_summary_parity: true,
  });
  assert.equal(facts.archive_sha256, pair.payload.archiveSha256);
  assert.equal(facts.archive_size, pair.payload.archiveSize);
  assert.equal(facts.archive_inventory_sha256, pair.payload.inventorySha256);
  assert.equal(facts.synthetic_release_projection, false);
  assert.equal(facts.runner, 'ubuntu-24.04-x64');
  assert.equal(facts.runtime, pair.action.runtime.policy.version);
  for (const name of [
    'private_network_namespace',
    'private_mount_namespace',
    'outside_mounts_unchanged',
  ])
    assert.equal(facts[name], true);
  assert.equal(facts.privileged_payload, false);
  assert.equal(facts.external_routes, 0);
  assert.equal(facts.sdk_count, 0);
  assert.equal(facts.live_provider_requests, 0);
  assert.equal(facts.profile, 'cr1-solus-internal4-dev');
  assert.equal(facts.context_format, 2);
  assert.equal(facts.context_compatibility, 1);
  assert.deepEqual(
    facts.checked_action_assets,
    Object.fromEntries(pair.action.assets.map((asset) => [asset.path, asset.sha256])),
  );
  assert.equal(facts.cases.length, originalCases.length);
  assert.equal(new Set(facts.cases.map((value) => value.name)).size, originalCases.length);
  const observations = new Map();
  const pass = (id, observation) => observations.set(id, { status: 'pass', observation });
  for (const check of originalCases) {
    const c = facts.cases.find((value) => value.name === check.name);
    assert.ok(c && statuses.has(c.status));
    assert.ok(c.completion_status === null || statuses.has(c.completion_status));
    assert.ok(c.exit === null || (Number.isInteger(c.exit) && c.exit >= 0 && c.exit <= 255));
    assert.ok(Number.isInteger(c.sends) && c.sends >= 0 && c.sends <= 6);
    assert.equal(typeof c.actual_host, 'boolean');
    // Only the closed, sanitized case projection is hashed, never raw audit,
    // tool contents, credentials, SESSION, prompts or private reasoning.
    pass(check.id, {
      status: c.status,
      completionStatus: c.completion_status,
      exit: c.exit,
      sends: c.sends,
      actualHost: c.actual_host,
    });
  }
  assert.equal(facts.direct_cutover.cli_unknown_usage_cases, 3);
  assert.equal(facts.direct_cutover.direct_processes, 9);
  assert.equal(facts.direct_cutover.original_members_unchanged, true);
  assert.equal(facts.direct_cutover.linked_managed_fault_proof, false);
  pass('original-cli', {
    usageCases: 3,
    directProcesses: 9,
    archiveSha256: pair.payload.archiveSha256,
  });
  pass('composite-eight-assets', {
    assetsSha256: pair.action.assetsSha256,
    mapSha256: q.map_sha256,
  });
  pass('runtime-only', { sdkCount: 0, runtimePolicySha256: pair.action.runtimePolicySha256 });
  pass('full-managed-loading', {
    inventorySha256: pair.payload.inventorySha256,
    buildInputsSha256: pair.payload.buildInputsSha256,
    profile: facts.profile,
    contextFormat: 2,
    compatibility: 1,
  });
  assert.ok(facts.transactions.some((value) => value.case === 'fresh'));
  assert.ok(facts.transactions.some((value) => value.case === 'restored-second-head'));
  pass('host-state-ordering', {
    fresh: true,
    restored: true,
    caseCount: facts.transactions.length,
  });
  const availableCompletions = facts.cases.filter((c) => c.completion_status !== null).length;
  assert.ok(availableCompletions > 0);
  pass('output-summary-parity', {
    comparedAvailableCompletions: availableCompletions,
    unavailableCompletions: originalCases.length - availableCompletions,
    caseCount: originalCases.length,
  });
  pass('denied-external-egress', {
    networkNamespace: true,
    mountNamespace: true,
    outsideMountsUnchanged: true,
    externalRoutes: 0,
    liveProviderRequests: 0,
  });
  assert.ok(Number.isSafeInteger(facts.official_requests) && facts.official_requests > 0);
  assert.ok(Number.isSafeInteger(facts.github_requests) && facts.github_requests > 0);
  pass('service-sdk-settlement', {
    confirmedCloseAndTeardown: true,
    officialRequests: facts.official_requests,
    githubRequests: facts.github_requests,
  });
  return observations;
}
