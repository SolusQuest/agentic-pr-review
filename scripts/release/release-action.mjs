import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import {
  chmod,
  lstat,
  mkdir,
  mkdtemp,
  readFile,
  readdir,
  realpath,
  rm,
  writeFile,
} from 'node:fs/promises';
import { delimiter, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalJson } from './build-payload.format.mjs';
import * as staging from './stage-candidate.mjs';
import * as promotion from './promote-candidate.mjs';

const retainedOriginals = [
  {
    id: 13474437693,
    name: 'r7-p1-payload-42155761876-1',
    size_in_bytes: 1914235,
    digest: 'sha256:7cfbef35b82176d3a8370e87dda785bcef0989259013365e4966da7ed26e75a1',
    created_at: '2026-11-06T03:23:26Z',
    expires_at: '2026-11-13T03:23:25Z',
    workflow_run: {
      id: 42155761876,
      repository_id: 1263978368,
      head_repository_id: 1263978368,
      head_branch: 'main',
      head_sha: 'da487ad3fc04ebba6b36efaad4b613669f6eea40',
    },
  },
  {
    id: 13474377959,
    name: 'r7-p1-metadata-42155761876-1',
    size_in_bytes: 5768,
    digest: 'sha256:c2cfd0bfd2013c32103d90d844d873600233934577276231cc153b25ae0bc795',
    created_at: '2026-11-06T03:23:46Z',
    expires_at: '2026-11-13T03:23:45Z',
    workflow_run: {
      id: 42155761876,
      repository_id: 1263978368,
      head_repository_id: 1263978368,
      head_branch: 'main',
      head_sha: 'da487ad3fc04ebba6b36efaad4b613669f6eea40',
    },
  },
  {
    id: 13474631449,
    name: 'r7-p2-provenance-42155761876-1',
    size_in_bytes: 8460,
    digest: 'sha256:22b80042a0ead0257a3f06c63836415f07da4f1bee1f836f2849d9ee5c1c87b4',
    created_at: '2026-11-06T03:24:13Z',
    expires_at: '2026-11-13T03:24:12Z',
    workflow_run: {
      id: 42155761876,
      repository_id: 1263978368,
      head_repository_id: 1263978368,
      head_branch: 'main',
      head_sha: 'da487ad3fc04ebba6b36efaad4b613669f6eea40',
    },
  },
  {
    id: 13483914599,
    name: 'r7-v2-qualification-42170346041-1',
    size_in_bytes: 41768,
    digest: 'sha256:6a249bc96d970f6ece2bf65b3149ddc614397560476fb2e67d3d231aac8d7e02',
    created_at: '2026-11-06T07:14:25Z',
    expires_at: '2026-11-13T07:14:23Z',
    workflow_run: {
      id: 42170346041,
      repository_id: 1263978368,
      head_repository_id: 1263978368,
      head_branch: 'main',
      head_sha: 'f8f9d78e7cb3ec3b9ac8e452a245197d18fca248',
    },
  },
];

const retentionStages = new WeakMap();
const retentionStageNames = new Set([
  'context-admission',
  'original-metadata',
  'download',
  'raw-bytecheck',
  'upload',
  'retained-metadata',
  'cleanup',
]);
export function originalStorageDiagnostic(error) {
  const stage = retentionStages.get(error);
  return (
    'R7_ORIGINAL_STORAGE_COPY_FAILED stage=' +
    (retentionStageNames.has(stage) ? stage : 'unknown') +
    ' originals_unchanged=true no_automatic_retry=true\n'
  );
}
async function opaqueArtifactCall(operation) {
  const out = process.stdout.write,
    err = process.stderr.write;
  const discard = (_chunk, encoding, callback) => {
    if (typeof encoding === 'function') encoding();
    else if (typeof callback === 'function') callback();
    return true;
  };
  // The SDK's raw output/errors stay opaque. Only our closed stage codes and
  // verified public artifact records are emitted after the operation settles.
  process.stdout.write = discard;
  process.stderr.write = discard;
  try {
    return await operation();
  } finally {
    process.stdout.write = out;
    process.stderr.write = err;
  }
}
export async function retainPublicStorage(env = process.env) {
  let stage = 'context-admission';
  let work, identity;
  try {
    assert.equal(env.GITHUB_EVENT_NAME, 'workflow_dispatch');
    assert.equal(env.GITHUB_REPOSITORY, 'SolusQuest/agentic-pr-review');
    assert.equal(env.GITHUB_REPOSITORY_ID, '1263978368');
    assert.equal(env.GITHUB_ACTOR_ID, '16307884');
    assert.equal(env.GITHUB_REF, 'refs/heads/codex/r7-v4-premerge-validation-361');
    assert.equal(env.GITHUB_JOB, 'retain-public-storage');
    assert.equal(env.GITHUB_RUN_ATTEMPT, '1');
    assert.equal(env.EXPECTED_HEAD, env.GITHUB_SHA);
    assert.equal(env.WORKFLOW_HEAD, env.GITHUB_SHA);
    assert.match(env.GITHUB_SHA ?? '', /^[a-f0-9]{40}$/u);
    const context = {
      githubTokenPresent: Boolean(env.GITHUB_TOKEN),
      artifactRuntimeTokenPresent: Boolean(env.ACTIONS_RUNTIME_TOKEN),
      artifactResultsURLPresent: Boolean(env.ACTIONS_RESULTS_URL),
    };
    process.stdout.write('R7_ORIGINAL_STORAGE_CONTEXT ' + JSON.stringify(context) + '\n');
    assert.ok(Object.values(context).every(Boolean));
    const { getOctokit } = await import('@actions/github');
    const { DefaultArtifactClient } = await import('@actions/artifact');
    const { setSecret } = await import('@actions/core');
    setSecret(env.GITHUB_TOKEN);
    const api = getOctokit(env.GITHUB_TOKEN),
      client = new DefaultArtifactClient();
    const repository = { owner: 'SolusQuest', repo: 'agentic-pr-review' };
    const digest = (bytes) => 'sha256:' + createHash('sha256').update(bytes).digest('hex');
    const temporary = await realpath(env.RUNNER_TEMP);
    work = await mkdtemp(join(temporary, 'r7-retained-originals-'));
    await chmod(work, 0o700);
    identity = await lstat(work);
    const end = Math.min(Date.now() + 20 * 60_000, Date.parse('2026-12-10T03:10:06.000Z'));
    const check = () => assert.ok(Date.now() < end, 'retention_copy_time_exhausted');
    for (const expected of retainedOriginals) {
      stage = 'original-metadata';
      check();
      const { data: original } = await opaqueArtifactCall(() =>
        api.rest.actions.getArtifact({
          ...repository,
          artifact_id: expected.id,
        }),
      );
      for (const key of [
        'id',
        'name',
        'size_in_bytes',
        'digest',
        'created_at',
        'expires_at',
        'workflow_run',
      ])
        assert.deepEqual(original[key], expected[key]);
      assert.equal(original.expired, false);
      const directory = join(work, String(expected.id));
      await mkdir(directory, { mode: 0o700 });
      stage = 'download';
      const downloaded = await opaqueArtifactCall(() =>
        client.downloadArtifact(expected.id, {
          path: directory,
          skipDecompress: true,
          expectedHash: expected.digest.slice(7),
          findBy: {
            token: env.GITHUB_TOKEN,
            repositoryOwner: repository.owner,
            repositoryName: repository.repo,
            workflowRunId: expected.workflow_run.id,
          },
        }),
      );
      stage = 'raw-bytecheck';
      assert.notEqual(downloaded.digestMismatch, true);
      const entries = await readdir(directory);
      assert.equal(entries.length, 1);
      const input = join(directory, entries[0]),
        fileIdentity = await lstat(input);
      assert.ok(fileIdentity.isFile() && !fileIdentity.isSymbolicLink());
      assert.equal(fileIdentity.size, expected.size_in_bytes);
      const bytes = await readFile(input);
      assert.equal(digest(bytes), expected.digest);
      const file = join(work, 'retained-original-' + expected.id + '.zip');
      await writeFile(file, bytes, { mode: 0o600, flag: 'wx' });
      stage = 'upload';
      check();
      const uploaded = await opaqueArtifactCall(() =>
        client.uploadArtifact('retained-original-' + expected.id + '.zip', [file], work, {
          skipArchive: true,
          retentionDays: 90,
        }),
      );
      stage = 'retained-metadata';
      assert.ok(Number.isSafeInteger(uploaded.id) && uploaded.id > 0);
      assert.equal(uploaded.digest, expected.digest.slice(7));
      assert.equal(uploaded.size, expected.size_in_bytes);
      const { data: retained } = await opaqueArtifactCall(() =>
        api.rest.actions.getArtifact({
          ...repository,
          artifact_id: uploaded.id,
        }),
      );
      assert.equal(retained.expired, false);
      assert.equal(retained.name, 'retained-original-' + expected.id + '.zip');
      assert.equal(retained.digest, expected.digest);
      assert.equal(retained.size_in_bytes, expected.size_in_bytes);
      assert.equal(retained.workflow_run.id, Number(env.GITHUB_RUN_ID));
      assert.equal(retained.workflow_run.repository_id, 1263978368);
      assert.equal(retained.workflow_run.head_repository_id, 1263978368);
      assert.equal(retained.workflow_run.head_sha, env.GITHUB_SHA);
      const usable = Date.parse(retained.expires_at) >= Date.now() + (72 + 6) * 3_600_000;
      process.stdout.write(
        'R7_ORIGINAL_STORAGE_RETAINED ' +
          JSON.stringify({
            original: expected,
            retained,
            requestedRetentionDays: 90,
            usableForFullPairWindowPlus72h: usable,
            originalBytesUnchanged: true,
            originalProducerNotReplaced: true,
          }) +
          '\n',
      );
      assert.ok(usable, 'retained_storage_does_not_cover_one_full_pair_window_plus_72h');
    }
    stage = 'cleanup';
    const current = await lstat(work);
    assert.equal(await realpath(work), work);
    assert.equal(current.dev, identity.dev);
    assert.equal(current.ino, identity.ino);
    await rm(work, { recursive: true, force: false });
    process.stdout.write('R7_ORIGINAL_STORAGE_COPY_COMPLETE count=4 retention_requested_days=90\n');
    return { retainedOriginalStorage: true, count: 4 };
  } catch {
    // Never inspect SDK error properties/getters or publish their output.
    // Failed/unknown operations retain their owned local inputs for reconciliation.
    const controlled = new Error('original_storage_retention_failed');
    retentionStages.set(controlled, stage);
    throw controlled;
  }
}

export async function main(env = process.env) {
  const phase = env.INPUT_PHASE;
  if (phase === 'retain-public-storage') return retainPublicStorage(env);
  if (
    !(
      (phase === 'stage' &&
        ['stage', 'recover-stage', 'complete-stage', 'finish-stage'].includes(
          env.RELEASE_OPERATION,
        )) ||
      (phase === 'promote' && ['promote', 'recover-promote'].includes(env.RELEASE_OPERATION))
    )
  )
    throw new Error('release_action_denied');

  // SDK children inherit this private copy; gh inherits the scrubbed ambient env.
  const operationEnv = { ...env };
  delete process.env.ACTIONS_RUNTIME_TOKEN;
  delete process.env.ACTIONS_RESULTS_URL;
  const prefix = phase === 'stage' ? 'r7-p4' : 'r7-p5';
  operationEnv.PATH = join(env.RUNNER_TEMP, prefix + '-gh') + delimiter + (env.PATH ?? '');
  process.env.PATH = operationEnv.PATH;
  const args = [
    phase === 'stage'
      ? env.RELEASE_OPERATION === 'finish-stage'
        ? 'workflow-finish-stage'
        : env.RELEASE_OPERATION === 'complete-stage'
          ? 'workflow-complete-stage'
          : 'workflow-stage'
      : 'workflow-promote',
    '--source',
    env.INPUT_SOURCE,
    '--version',
    env.INPUT_VERSION,
    '--output',
    join(env.RUNNER_TEMP, prefix + (phase === 'stage' ? '-staged' : '-published')),
  ];
  return (phase === 'stage' ? staging : promotion).main(args, operationEnv);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((result) => process.stdout.write(canonicalJson(result)))
    .catch((error) => {
      process.stderr.write(
        process.env.INPUT_PHASE === 'retain-public-storage'
          ? originalStorageDiagnostic(error)
          : process.env.INPUT_PHASE === 'stage'
            ? staging.stagingDiagnostic(error)
            : process.env.INPUT_PHASE === 'promote'
              ? 'candidate_promotion_denied\n'
              : 'candidate_release_denied\n',
      );
      process.exitCode = 1;
    });
}
