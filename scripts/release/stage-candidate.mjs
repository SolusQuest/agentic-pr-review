import { execFile, execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { appendFile, lstat, mkdir, open, readdir, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import {
  canonicalJson,
  MANAGED_LIMITS,
  readBoundedFile,
  sha256,
  validateReleaseVersion,
} from './build-payload.format.mjs';
import { admitOutput } from './build-payload.mjs';
import {
  CAPS,
  decimal,
  githubReader,
  hex,
  REPOSITORY,
  REPOSITORY_ID,
  timestamp,
  validateProducer,
  verifyStorage,
  WORKFLOW,
} from './candidate.mjs';
import { ACTION_ASSETS, ACTION_ROOT, PAIR_LIMITS } from './finalized-pair.format.mjs';
import { verifyQualifiedPair } from './finalize-candidate.mjs';
import { admitWorkflow } from './prepare-candidate.mjs';
import {
  descriptorFor,
  intentFor,
  intentName,
  parseStageRequest,
  parseStagedRecord,
  same,
  STAGE_ENVIRONMENT,
  STAGE_LIMITS,
  COMPLETION_REVIEWER_ID,
  COMPLETION_DIAGNOSTIC,
  completionIntentFor,
  completionIntentName,
  completionOwnerFor,
  parseCompletionRequest,
  parseFinishRequest,
  finishIntentName,
  finishIntentFor,
  FINISH_DISPATCH_BEFORE,
  FINISH_HARD_END,
} from './stage-candidate.format.mjs';
import { stageHttp } from './stage-candidate.http.mjs';
import { GH_VERSION } from './verify-provenance.mjs';

// Only errors created by this module carry public codes. Never inspect error text.
const diagnostics = new WeakMap();
const PUBLIC_CODES = new Set([
  'completion_owner_denied',
  'completion_parent_denied',
  'completion_source_denied',
  'completion_claim_present',
  'completion_claim_write_unknown',
  'completion_record_required',
  'completion_producer_denied',
  'completion_request_denied',
  'finish_snapshot_denied',
  'finish_claim_present',
  'sdk_operation_denied',
  'sdk_readback_denied',
  'stage_action_read_denied',
  'stage_arguments_invalid',
  'stage_asset_bytes_drift',
  'stage_asset_drift',
  'stage_asset_id_drift',
  'stage_asset_projection_invalid',
  'stage_asset_set_drift',
  'stage_authority_denied',
  'stage_authority_expired',
  'stage_claim_denied',
  'stage_consumer_tag_present',
  'stage_create_response_drift',
  'stage_create_unknown',
  'stage_download_id_invalid',
  'stage_draft_visibility_denied',
  'stage_duplicate_release',
  'stage_environment_denied',
  'stage_foreign_release',
  'stage_historical_control_denied',
  'stage_intent_ambiguous',
  'stage_intent_drift',
  'stage_intent_identity_denied',
  'stage_intent_invalid',
  'stage_intent_limit',
  'stage_intent_missing',
  'stage_intent_producer_denied',
  'stage_intent_transport_drift',
  'stage_intent_write_unknown',
  'stage_original_drift',
  'stage_payload_tag_drift',
  'stage_qualification_download_denied',
  'stage_raw_digest_invalid',
  'stage_raw_inventory_invalid',
  'stage_record_drift',
  'stage_record_limit',
  'stage_record_required',
  'stage_recovery_incomplete',
  'stage_release_id_drift',
  'stage_release_missing',
  'stage_release_phase_invalid',
  'stage_release_race',
  'stage_token_isolation_denied',
  'stage_upload_id_drift',
  'stage_upload_response_drift',
]);
const CHECKPOINTS = new Set([
  'completion_owner',
  'completion_parent',
  'unknown',
  'arguments',
  'request',
  'authority',
  'deadline',
  'environment',
  'isolation',
  'scratch',
  'acquire_payload',
  'acquire_metadata',
  'acquire_provenance',
  'acquire_qualification',
  'acquire_pair',
  'dedicated_repository',
  'dedicated_environment',
  'qualification',
  'p3',
  'projection',
  'prewrite',
  'intent_claim',
  'intent_upload',
  'intent_reconcile',
  'release_create',
  'release_readback',
  'asset_upload',
  'asset_readback',
  'final_verify',
  'record_write',
]);
const SDK_CHECKPOINTS = new Set(['config', 'transfer', 'readback', 'result']);
function controlledError(code) {
  const error = new Error(code);
  diagnostics.set(error, { code: PUBLIC_CODES.has(code) ? code : 'unknown' });
  return error;
}
function requireThat(condition, code) {
  if (!condition) throw controlledError(code);
}
function checkpoint(progress, name) {
  if (progress) progress.checkpoint = CHECKPOINTS.has(name) ? name : 'unknown';
}
export function stagingDiagnostic(error) {
  const d = diagnostics.get(error);
  const point = CHECKPOINTS.has(d?.checkpoint) ? d.checkpoint : 'unknown';
  const code = PUBLIC_CODES.has(d?.code) ? d.code : 'unknown';
  const sdkPoint = SDK_CHECKPOINTS.has(d?.sdkCheckpoint)
    ? ` sdk_checkpoint=${d.sdkCheckpoint}`
    : '';
  return `candidate_staging_denied checkpoint=${point}${sdkPoint} code=${code}\n`;
}
function sdkFailure(result, operation) {
  const keys =
    result && typeof result === 'object' && !Array.isArray(result)
      ? Object.keys(result).sort().join(',')
      : '';
  if (keys !== 'checkpoint,code' || !SDK_CHECKPOINTS.has(result.checkpoint)) return null;
  const { checkpoint: point, code } = result;
  if (
    !(
      code === 'unknown' ||
      (code === 'sdk_readback_denied' && point === 'readback' && operation === 'download') ||
      (code === 'sdk_operation_denied' &&
        point === 'config' &&
        !['upload', 'download'].includes(operation))
    )
  )
    return null;
  if (point === 'readback' && operation !== 'download') return null;
  const error = controlledError('stage_artifact_transport_unknown');
  diagnostics.set(error, { code, sdkCheckpoint: point });
  return error;
}

const ROOT = `/repos/${REPOSITORY}`;
const runFile = promisify(execFile);
export async function durableFile(path, bytes) {
  const handle = await open(path, 'wx', 0o600);
  try {
    await handle.writeFile(bytes);
    await handle.sync();
  } finally {
    await handle.close();
  }
  const parent = await open(dirname(path), 'r');
  try {
    await parent.sync();
  } finally {
    await parent.close();
  }
}
async function sdk(config, scratch, env) {
  const slot = join(scratch, 'sdk-' + randomUUID());
  await mkdir(slot, { mode: 0o700 });
  const configPath = join(slot, 'config.json'),
    resultPath = join(slot, 'result.json');
  await writeFile(configPath, canonicalJson(config), { mode: 0o600, flag: 'wx' });
  let failed = null;
  try {
    // No raw SDK stdout/stderr reaches public logs. execFile waits for close on timeout.
    await runFile(
      process.execPath,
      [
        fileURLToPath(new URL('./stage-candidate.sdk.mjs', import.meta.url)),
        configPath,
        resultPath,
      ],
      {
        env: { ...env, R7_STAGING_TOKEN: undefined },
        timeout: 180000,
        killSignal: 'SIGKILL',
        maxBuffer: 1048576,
      },
    );
  } catch (error) {
    failed = error;
  }
  if (failed) {
    // Only normal nonzero exits may supply detail; timeout/signal results stay unknown.
    if (Number.isInteger(failed.code) && failed.code !== 0 && !failed.killed && !failed.signal) {
      let detail;
      try {
        detail = sdkFailure(JSON.parse(await readBoundedFile(resultPath, 256)), config.operation);
      } catch {
        /* Missing, malformed or oversized diagnostics never permit continuation. */
      }
      if (detail) throw detail;
    }
    throw controlledError('stage_artifact_transport_unknown');
  }
  let result;
  try {
    result = JSON.parse(await readBoundedFile(resultPath, 1024));
  } catch {
    throw controlledError('stage_artifact_transport_unknown');
  }
  const keys =
    result && typeof result === 'object' && !Array.isArray(result)
      ? Object.keys(result).join(',')
      : '';
  requireThat(
    config.operation === 'upload'
      ? keys === 'id' && typeof result.id === 'string' && /^[1-9][0-9]*$/.test(result.id)
      : config.operation === 'download' && keys === 'downloaded' && result.downloaded === true,
    'stage_artifact_transport_unknown',
  );
  return result;
}
export async function singleRawFile(directory, expected, cap) {
  const rows = await readdir(directory);
  requireThat(rows.length === 1, 'stage_raw_inventory_invalid');
  const path = join(directory, rows[0]);
  const st = await lstat(path);
  requireThat(st.isFile() && !st.isSymbolicLink(), 'stage_raw_inventory_invalid');
  const bytes = await readBoundedFile(path, cap);
  requireThat(sha256(bytes) === expected, 'stage_raw_digest_invalid');
  return { path, bytes };
}
export function artifactTransport(scratch, env) {
  return {
    async upload(name, bytes, retentionDays) {
      const directory = join(scratch, 'intent-upload');
      await mkdir(directory, { mode: 0o700 });
      const file = join(directory, name);
      await durableFile(file, bytes);
      const r = await sdk(
        { operation: 'upload', name, file, directory, retentionDays },
        scratch,
        env,
      );
      return decimal(r.id);
    },
    async download(a, raw, cap) {
      const directory = join(scratch, 'artifact-' + decimal(a.id) + '-' + randomUUID());
      await mkdir(directory, { mode: 0o700 });
      const runId = decimal(a.workflow_run.id),
        id = decimal(a.id);
      requireThat(
        [runId, id].every((x) => Number.isSafeInteger(Number(x)) && String(Number(x)) === x),
        'stage_download_id_invalid',
      );
      await sdk(
        { operation: 'download', id, runId, directory, raw, sha256: a.digest.slice(7) },
        scratch,
        env,
      );
      return raw ? singleRawFile(directory, a.digest.slice(7), cap) : { directory };
    },
  };
}
export async function preflightEnvironment(request, get) {
  const e = await get(`${ROOT}/environments/${STAGE_ENVIRONMENT}`);
  requireThat(
    e?.name === STAGE_ENVIRONMENT && decimal(e.id) === request.environmentId,
    'stage_environment_denied',
  );
  return {
    environmentId: request.environmentId,
    protectionEvidenceSha256: request.protectionEvidenceSha256,
  };
}
function authority(input, env, r) {
  const producer = admitWorkflow(env, input.repo);
  requireThat(
    ['stage', 'recover-stage'].includes(env.RELEASE_OPERATION) &&
      env.R7_STAGING_ENABLED === 'true' &&
      env.R7_STAGING_ENVIRONMENT === STAGE_ENVIRONMENT &&
      producer.workflowCommit === r.controlCommit &&
      r.pair.payload.identity.sourceCommit === input.sourceCommit &&
      r.pair.payload.identity.releaseVersion === input.releaseVersion,
    'stage_authority_denied',
  );
  validateReleaseVersion(input.releaseVersion);
  return producer;
}
function deadline(r, qualification) {
  return Math.min(
    timestamp(r.validUntil),
    timestamp(qualification.expires_at),
    ...Object.values(r.pair.storage).map((s) => timestamp(s.expiresAt)),
  );
}
function fresh(until) {
  requireThat(Date.now() < until, 'stage_authority_expired');
}
function separateToken(token, env) {
  requireThat(
    typeof token === 'string' &&
      token.length > 0 &&
      typeof env.GITHUB_TOKEN === 'string' &&
      env.GITHUB_TOKEN.length > 0 &&
      token !== env.GITHUB_TOKEN &&
      token !== env.GH_TOKEN &&
      token !== process.env.GH_TOKEN,
    'stage_token_isolation_denied',
  );
}
export async function snapshots(input, r) {
  const result = [];
  function add(name, bytes, expected) {
    requireThat(
      bytes.length > 0 && (!expected || sha256(bytes) === expected),
      'stage_original_drift',
    );
    result.push({ name, size: bytes.length, sha256: sha256(bytes), bytes });
  }
  add(
    r.pair.payload.archiveName,
    await readBoundedFile(input.archivePath, MANAGED_LIMITS.archive),
    r.pair.payload.archiveSha256,
  );
  add(
    r.pair.payload.archiveName + '.sha256',
    Buffer.from(`${r.pair.payload.archiveSha256}  ${r.pair.payload.archiveName}\n`),
  );
  add(
    'candidate.json',
    await readBoundedFile(input.candidatePath, CAPS.metadata),
    r.pair.payload.candidateSha256,
  );
  add('finalized-pair.json', await readBoundedFile(input.pairPath, PAIR_LIMITS.pair), r.pairSha256);
  add(
    'provenance-bundle.json',
    await readBoundedFile(input.bundlePath, 4 * 1024 * 1024),
    r.pair.provenance.bundleSha256,
  );
  add(
    'qualification.zip',
    await readBoundedFile(input.qualificationArchivePath, PAIR_LIMITS.qualificationArtifact),
    r.qualificationSha256,
  );
  for (const [path, cap] of ACTION_ASSETS) {
    let bytes;
    try {
      bytes = execFileSync(
        'git',
        [
          '--no-replace-objects',
          '-C',
          input.repo,
          'cat-file',
          'blob',
          `${r.pair.action.commit}:${ACTION_ROOT}/${path}`,
        ],
        {
          env: { PATH: process.env.PATH },
          timeout: 30000,
          maxBuffer: cap,
          stdio: ['ignore', 'pipe', 'pipe'],
        },
      );
    } catch {
      throw controlledError('stage_action_read_denied');
    }
    add(
      'action-' + path.replaceAll('/', '__'),
      bytes,
      r.pair.action.assets.find((a) => a.path === path).sha256,
    );
  }
  result.sort((a, b) => (a.name < b.name ? -1 : 1));
  requireThat(
    result.length === 14 && new Set(result.map((a) => a.name)).size === 14,
    'stage_asset_projection_invalid',
  );
  return result;
}
async function tags(http, r) {
  const version = r.pair.payload.identity.releaseVersion;
  // validateReleaseVersion includes the leading v; this is the Action consumer tag.
  requireThat(
    (await http.get(`${ROOT}/git/ref/tags/${version}`, true)) === null,
    'stage_consumer_tag_present',
  );
  const ref = await http.get(`${ROOT}/git/ref/tags/payload-${version}`, true);
  if (!ref) return;
  requireThat(ref.ref === `refs/tags/payload-${version}`, 'stage_payload_tag_drift');
  let object = ref.object;
  const visited = new Set();
  for (let n = 0; object?.type === 'tag' && n < 5; n++) {
    requireThat(hex(object.sha, 40) && !visited.has(object.sha), 'stage_payload_tag_drift');
    visited.add(object.sha);
    object = (await http.get(`${ROOT}/git/tags/${object.sha}`))?.object;
  }
  requireThat(
    object?.type === 'commit' && object.sha === r.pair.payload.identity.sourceCommit,
    'stage_payload_tag_drift',
  );
}
export async function releaseState(http, descriptor, expectedId = null, phase = 'draft') {
  requireThat(['draft', 'public'].includes(phase), 'stage_release_phase_invalid');
  const matches = (await http.list(ROOT + '/releases')).filter(
    (r) => r.tag_name === descriptor.tag,
  );
  requireThat(matches.length <= 1, 'stage_duplicate_release');
  if (!matches.length) {
    requireThat(expectedId === null, 'stage_release_missing');
    return null;
  }
  const listed = matches[0],
    id = decimal(listed.id);
  requireThat(expectedId === null || expectedId === id, 'stage_release_id_drift');
  const r = await http.get(`${ROOT}/releases/${id}`);
  const check = (value) =>
    requireThat(
      decimal(value.id) === id &&
        value.tag_name === descriptor.tag &&
        value.draft === (phase === 'draft') &&
        value.prerelease === true &&
        (phase === 'draft'
          ? value.published_at === null
          : typeof value.published_at === 'string' &&
            timestamp(value.published_at) <= Date.now() &&
            value.immutable === true) &&
        value.target_commitish === descriptor.sourceCommit &&
        value.name === descriptor.tag &&
        value.body === canonicalJson(descriptor),
      'stage_foreign_release',
    );
  check(listed);
  check(r);
  const assets = await http.list(`${ROOT}/releases/${id}/assets`);
  return { id, assets };
}
export async function verifyAssets(http, state, expected, cache) {
  requireThat(state && state.assets.length === expected.length, 'stage_asset_set_drift');
  const ids = new Set(),
    names = new Set(),
    records = [];
  for (const a of state.assets) {
    const e = expected.find((e) => e.name === a.name),
      id = decimal(a.id);
    requireThat(
      e &&
        !ids.has(id) &&
        !names.has(a.name) &&
        a.state === 'uploaded' &&
        a.size === e.size &&
        a.size > 0 &&
        a.content_type === 'application/octet-stream' &&
        a.digest === 'sha256:' + e.sha256 &&
        a.url === `https://api.github.com${ROOT}/releases/assets/${id}`,
      'stage_asset_drift',
    );
    ids.add(id);
    names.add(a.name);
    const record = { name: a.name, id, size: a.size, sha256: e.sha256 };
    const old = cache.get(a.name);
    if (old) requireThat(same(old, record), 'stage_asset_id_drift');
    else {
      requireThat((await http.assetHash(id, e.size)) === e.sha256, 'stage_asset_bytes_drift');
      cache.set(a.name, record);
    }
    records.push(record);
  }
  return records.sort((a, b) => (a.name < b.name ? -1 : 1));
}
export async function intentMetadata(
  http,
  name,
  r,
  expected,
  transport,
  until,
  boundedByExpiry = false,
) {
  const all = await http.list(
    `${ROOT}/actions/artifacts?name=${encodeURIComponent(name)}`,
    'artifacts',
  );
  requireThat(all.every((a) => a.name === name) && all.length <= 1, 'stage_intent_ambiguous');
  if (!all.length) return null;
  const a = all[0],
    id = decimal(a.id),
    run = a.workflow_run;
  requireThat(
    a.expired === false &&
      (boundedByExpiry || timestamp(a.expires_at) >= until) &&
      timestamp(a.expires_at) > Date.now() &&
      timestamp(a.created_at) <= Date.now() &&
      a.size_in_bytes > 0 &&
      a.size_in_bytes <= STAGE_LIMITS.intent &&
      /^sha256:[a-f0-9]{64}$/.test(a.digest) &&
      decimal(run.repository_id) === REPOSITORY_ID &&
      decimal(run.head_repository_id) === REPOSITORY_ID &&
      run.head_branch === 'main' &&
      run.head_sha === r.controlCommit,
    'stage_intent_identity_denied',
  );
  const raw = await transport.download(a, true, STAGE_LIMITS.intent);
  requireThat(
    raw.bytes.length === a.size_in_bytes && sha256(raw.bytes) === a.digest.slice(7),
    'stage_intent_transport_drift',
  );
  let intent;
  try {
    intent = JSON.parse(raw.bytes.toString('utf8'));
  } catch {
    throw controlledError('stage_intent_invalid');
  }
  validateProducer(intent.producer);
  requireThat(
    intent.producer.workflowCommit === r.controlCommit &&
      intent.producer.runId === decimal(run.id) &&
      same(intent, { ...expected, producer: intent.producer }) &&
      canonicalJson(intent) === raw.bytes.toString('utf8'),
    'stage_intent_drift',
  );
  const p = intent.producer,
    producerRun = await http.get(`${ROOT}/actions/runs/${p.runId}/attempts/${p.runAttempt}`);
  const repository = (x) => x?.full_name === REPOSITORY && decimal(x.id) === REPOSITORY_ID;
  // Failed/cancelled attempts are precisely where lost-reply recovery is needed.
  requireThat(
    decimal(producerRun.id) === p.runId &&
      decimal(producerRun.run_attempt) === p.runAttempt &&
      repository(producerRun.repository) &&
      repository(producerRun.head_repository) &&
      producerRun.path === WORKFLOW &&
      producerRun.head_sha === p.workflowCommit &&
      producerRun.head_branch === 'main' &&
      producerRun.event === 'workflow_dispatch' &&
      ['in_progress', 'completed', 'queued', 'waiting'].includes(producerRun.status),
    'stage_intent_producer_denied',
  );
  return { artifactId: id, sha256: a.digest.slice(7), producer: p, expiresAt: a.expires_at };
}

function historicalControl(repo, commit, completion = false) {
  try {
    const git = (args) =>
      execFileSync('git', ['--no-replace-objects', '-C', repo, ...args], {
        env: { PATH: process.env.PATH },
        timeout: 30000,
        maxBuffer: 1048576,
        stdio: ['ignore', 'pipe', 'pipe'],
      });
    git(['merge-base', '--is-ancestor', commit, 'HEAD']);
    if (completion === 'failed-completion')
      requireThat(
        sha256(git(['show', commit + ':scripts/release/stage-candidate.mjs'])) ===
          '563c2c880d83f9395923b975a6ca89c13b55bd03ab18da20d7d9a4f458649eba' &&
          sha256(git(['show', commit + ':' + WORKFLOW])) ===
            'd49ef97786f1bd3ba38ef90e37ee99acc4c1045b91aaa2bd2e93da35b25adc89',
        'completion_source_denied',
      );
    if (!completion) {
      // Both reviewed source/workflow pairs put stage_create_unknown before the asset loop.
      const sourceSha256 = sha256(git(['show', commit + ':scripts/release/stage-candidate.mjs']));
      const workflowSha256 = sha256(git(['show', commit + ':' + WORKFLOW]));
      requireThat(
        (sourceSha256 === '1ba9d963cdc4af434156fb6693652c70f4b77bfea47eb7185ec05483ae79e626' &&
          workflowSha256 === 'f329a3389c96e229eaa02783b3cb815859528355c698f3ee2e106163fa67703c') ||
          (sourceSha256 === '1188598e65286d657f05c8161875f3ea05206467bd6a7d1234f8b106a88c2aa1' &&
            workflowSha256 === '54aa5818c816e9963caef4702bc2b83111c223b92d11b59d757602f7eeeaaaea'),
        'completion_source_denied',
      );
    }
  } catch {
    throw controlledError('completion_source_denied');
  }
}
function completionAuthority(input, env, c) {
  const finishing = c.kind === 'r7-stage-finish-request';
  const producer = admitWorkflow(env, input.repo),
    original = finishing ? c.previousRequest : c,
    r = original.stageRequest;
  requireThat(
    env.RELEASE_OPERATION === (finishing ? 'finish-stage' : 'complete-stage') &&
      env.R7_STAGING_ENABLED === 'true' &&
      env.R7_STAGING_ENVIRONMENT === STAGE_ENVIRONMENT &&
      producer.workflowCommit === c.controlCommit &&
      producer.runAttempt === '1' &&
      producer.runId !== c.origin.runId &&
      producer.runId !== original.origin.runId &&
      input.sourceCommit === r.pair.payload.identity.sourceCommit &&
      input.releaseVersion === r.pair.payload.identity.releaseVersion &&
      env.R7_STAGE_REQUEST_SHA256 === original.stageRequestSha256 &&
      same(r, JSON.parse(input.stageRequestBytes)),
    'completion_request_denied',
  );
  return producer;
}
async function completionReview(http, c, requestSha256, producer) {
  requireThat(producer.runAttempt === '1', 'completion_owner_denied');
  if (c.kind === 'r7-stage-finish-request') {
    const run = await http.get(`${ROOT}/actions/runs/${producer.runId}/attempts/1`);
    requireThat(
      runIdentity(run, producer) && timestamp(run.created_at) <= FINISH_DISPATCH_BEFORE,
      'completion_owner_denied',
    );
  }
  const history = await http.get(`${ROOT}/actions/runs/${producer.runId}/approvals`);
  requireThat(Array.isArray(history) && history.length === 1, 'completion_owner_denied');
  const review = history[0];
  requireThat(
    review?.state === 'approved' &&
      decimal(review.user?.id) === COMPLETION_REVIEWER_ID &&
      Array.isArray(review.environments) &&
      review.environments.length === 1 &&
      decimal(review.environments[0].id) === c.environmentId &&
      review.environments[0].name === STAGE_ENVIRONMENT &&
      review.comment === canonicalJson(completionOwnerFor(c, requestSha256, producer)),
    'completion_owner_denied',
  );
}
async function completionAdmission(input, env, token, adapters, progress) {
  checkpoint(progress, 'request');
  const finishing = env.RELEASE_OPERATION === 'finish-stage';
  const requestBytes = finishing ? input.finishRequestBytes : input.completionRequestBytes;
  const c = finishing
    ? parseFinishRequest(requestBytes, env.R7_STAGE_FINISH_REQUEST_SHA256)
    : parseCompletionRequest(requestBytes, env.R7_STAGE_COMPLETION_REQUEST_SHA256);
  checkpoint(progress, 'authority');
  const producer = completionAuthority(input, env, c);
  separateToken(token, env);
  const r = finishing ? c.previousRequest.stageRequest : c.stageRequest;
  fresh(Math.min(timestamp(c.validUntil), timestamp(r.validUntil)));
  const http = adapters.http ?? stageHttp(token);
  checkpoint(progress, 'completion_owner');
  await completionReview(http, c, sha256(requestBytes), producer);
  requireThat(
    env.RUNNER_TEMP && (await lstat(env.RUNNER_TEMP)).isDirectory(),
    'stage_claim_denied',
  );
  // Before even SDK input downloads. This claim is never removed or recreated.
  await durableFile(
    join(env.RUNNER_TEMP, (finishing ? finishIntentName(r) : completionIntentName(r)) + '.claim'),
    Buffer.from(canonicalJson(completionOwnerFor(c, sha256(requestBytes), producer))),
  );
  return { c, producer, http };
}
function runIdentity(run, producer) {
  const repository = (x) => x?.full_name === REPOSITORY && decimal(x.id) === REPOSITORY_ID;
  return (
    decimal(run?.id) === producer.runId &&
    decimal(run.run_attempt) === producer.runAttempt &&
    repository(run.repository) &&
    repository(run.head_repository) &&
    run.path === WORKFLOW &&
    run.head_sha === producer.workflowCommit &&
    run.head_branch === 'main' &&
    run.event === 'workflow_dispatch'
  );
}
async function completionParent(c, input, env, http, failedCompletion = false) {
  historicalControl(
    input.repo,
    c.origin.controlCommit,
    failedCompletion ? 'failed-completion' : false,
  );
  const producer = {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: WORKFLOW,
    workflowCommit: c.origin.controlCommit,
    runId: c.origin.runId,
    runAttempt: c.origin.runAttempt,
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
  const run = await http.get(`${ROOT}/actions/runs/${producer.runId}/attempts/1`);
  requireThat(
    runIdentity(run, producer) && run.status === 'completed' && run.conclusion === 'failure',
    'completion_parent_denied',
  );
  const jobs = (
    await http.list(`${ROOT}/actions/runs/${producer.runId}/attempts/1/jobs`, 'jobs')
  ).filter((j) => j.name === 'stage');
  requireThat(
    jobs.length === 1 && decimal(jobs[0].id) === c.origin.stageJobId,
    'completion_parent_denied',
  );
  const direct = await http.get(`${ROOT}/actions/jobs/${c.origin.stageJobId}`);
  for (const job of [jobs[0], direct])
    requireThat(
      decimal(job.id) === c.origin.stageJobId &&
        decimal(job.run_id) === producer.runId &&
        job.name === 'stage' &&
        job.head_sha === producer.workflowCommit &&
        job.status === 'completed' &&
        job.conclusion === 'failure',
      'completion_parent_denied',
    );
  let raw;
  try {
    const childEnv = {
      PATH: env.PATH ?? process.env.PATH,
      GH_TOKEN: env.GITHUB_TOKEN,
      GH_HOST: 'github.com',
    };
    const version = await runFile('gh', ['--version'], {
      env: childEnv,
      timeout: 30000,
      maxBuffer: 4096,
    });
    requireThat(
      version.stdout.startsWith('gh version ' + GH_VERSION + ' '),
      'completion_parent_denied',
    );
    raw = (
      await runFile(
        'gh',
        [
          'api',
          '--allow-escape-sequences',
          '--method',
          'GET',
          `${ROOT}/actions/jobs/${c.origin.stageJobId}/logs`,
        ],
        { env: childEnv, timeout: 30000, maxBuffer: 65536, encoding: 'buffer' },
      )
    ).stdout;
  } catch {
    throw controlledError('completion_parent_denied');
  }
  requireThat(
    raw.length > 0 &&
      raw.length <= 65536 &&
      sha256(raw) === c.origin.jobLogSha256 &&
      raw.includes(Buffer.from(c.origin.diagnostic)),
    'completion_parent_denied',
  );
}
async function completionCreator(http, c, producer, requestSha256) {
  requireThat(
    producer.runId !== c.origin.runId && producer.runAttempt === '1',
    'completion_producer_denied',
  );
  const run = await http.get(`${ROOT}/actions/runs/${producer.runId}/attempts/1`);
  requireThat(
    runIdentity(run, producer) && run.status === 'completed' && run.conclusion === 'success',
    'completion_producer_denied',
  );
  const jobs = (
    await http.list(`${ROOT}/actions/runs/${producer.runId}/attempts/1/jobs`, 'jobs')
  ).filter((j) => j.name === 'stage');
  requireThat(
    jobs.length === 1 &&
      decimal(jobs[0].run_id) === producer.runId &&
      jobs[0].head_sha === c.controlCommit &&
      jobs[0].status === 'completed' &&
      jobs[0].conclusion === 'success',
    'completion_producer_denied',
  );
  await completionReview(http, c, requestSha256, producer);
}
async function finishHistory(z, input, env, http) {
  await completionParent(z.previousRequest, input, env, http);
  await completionParent(z, input, env, http, true);
  const producer = {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: WORKFLOW,
    workflowCommit: z.origin.controlCommit,
    runId: z.origin.runId,
    runAttempt: '1',
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
  await completionReview(http, z.previousRequest, z.previousRequestSha256, producer);
}
async function finishArtifacts(http, r) {
  const name = finishIntentName(r);
  const all = await http.list(
    `${ROOT}/actions/artifacts?name=${encodeURIComponent(name)}`,
    'artifacts',
  );
  requireThat(all.every((a) => a.name === name) && all.length <= 1, 'finish_claim_present');
  return all;
}
async function supplementaryArtifacts(http, r) {
  const name = completionIntentName(r);
  const all = await http.list(
    `${ROOT}/actions/artifacts?name=${encodeURIComponent(name)}`,
    'artifacts',
  );
  requireThat(all.every((a) => a.name === name) && all.length <= 1, 'completion_claim_present');
  return all;
}
export async function completeStageCandidate(
  input,
  env = process.env,
  adapters = {},
  progress = null,
) {
  const token = env.R7_STAGING_TOKEN;
  delete env.R7_STAGING_TOKEN;
  delete process.env.R7_STAGING_TOKEN;
  const admission = await completionAdmission(input, env, token, adapters, progress);
  return executeCompletion(input, env, adapters, progress, admission);
}
export async function finishStageCandidate(
  input,
  env = process.env,
  adapters = {},
  progress = null,
) {
  const token = env.R7_STAGING_TOKEN;
  delete env.R7_STAGING_TOKEN;
  delete process.env.R7_STAGING_TOKEN;
  const admission = await completionAdmission(input, env, token, adapters, progress);
  return executeFinish(input, env, adapters, progress, admission);
}
function finishSnapshot(z, assets) {
  const existing = assets.filter((a) => a.name !== 'qualification.zip');
  requireThat(
    existing.length === 13 &&
      z.snapshot.every(
        (a, i) =>
          a.name === existing[i].name &&
          a.size === existing[i].size &&
          a.sha256 === existing[i].sha256,
      ),
    'finish_snapshot_denied',
  );
  return existing;
}
async function verifyFinishSnapshot(http, state, z, assets) {
  const existing = finishSnapshot(z, assets);
  const records = await verifyAssets(http, state, existing, new Map());
  requireThat(same(records, z.snapshot), 'finish_snapshot_denied');
}
async function finishFences(z, r, descriptor, input, env, http, transport, until) {
  await finishHistory(z, input, env, http);
  const x = z.previousRequest;
  const parent = await intentMetadata(
    http,
    intentName(r),
    r,
    intentFor(r, x.stageRequestSha256, descriptor, null),
    transport,
    until,
    true,
  );
  requireThat(
    parent &&
      parent.artifactId === x.origin.intentId &&
      parent.sha256 === x.origin.intentSha256 &&
      parent.producer.runId === x.origin.runId &&
      parent.producer.runAttempt === '1',
    'completion_parent_denied',
  );
  const previous = await intentMetadata(
    http,
    completionIntentName(r),
    { ...r, controlCommit: x.controlCommit },
    completionIntentFor(x, z.previousRequestSha256, descriptor, parent, null),
    transport,
    until,
    true,
  );
  requireThat(
    previous &&
      previous.artifactId === z.origin.intentId &&
      previous.sha256 === z.origin.intentSha256 &&
      previous.producer.runId === z.origin.runId &&
      previous.producer.runAttempt === '1',
    'completion_parent_denied',
  );
  return {
    parent,
    previous,
    until: Math.min(until, timestamp(parent.expiresAt), timestamp(previous.expiresAt)),
  };
}
async function executeFinish(input, env, adapters, progress, { c: z, producer, http }) {
  const r = z.previousRequest.stageRequest;
  checkpoint(progress, 'dedicated_repository');
  const repo = await http.get(ROOT);
  requireThat(
    repo?.full_name === REPOSITORY &&
      decimal(repo.id) === REPOSITORY_ID &&
      repo.permissions?.push === true,
    'stage_draft_visibility_denied',
  );
  await preflightEnvironment(z, http.get);
  checkpoint(progress, 'completion_parent');
  await finishHistory(z, input, env, http);
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  let until = Math.min(deadline(r, qualification), timestamp(z.validUntil), FINISH_HARD_END);
  fresh(until);
  checkpoint(progress, 'p3');
  await verifyQualifiedPair(
    {
      ...input,
      requestBytes: Buffer.from(canonicalJson(r.finalization)),
      pairSha256: r.pairSha256,
      qualifierCommit: r.qualifierCommit,
      qualificationId: r.qualificationId,
      qualificationSha256: r.qualificationSha256,
      receiptSha256: r.receiptSha256,
    },
    env,
  );
  const assets = await snapshots(input, r),
    descriptor = descriptorFor(r, assets);
  requireThat(sha256(canonicalJson(descriptor)) === z.descriptorSha256, 'completion_parent_denied');
  finishSnapshot(z, assets);
  const output = admitOutput(input.repo, input.output);
  await mkdir(output, { mode: 0o700 });
  const transport = adapters.transport ?? artifactTransport(output, env);
  const {
    parent,
    previous,
    until: bounded,
  } = await finishFences(z, r, descriptor, input, env, http, transport, until);
  until = bounded;
  await tags(http, r);
  let state = await releaseState(http, descriptor, z.releaseId);
  await verifyFinishSnapshot(http, state, z, assets);
  requireThat((await finishArtifacts(http, r)).length === 0, 'finish_claim_present');
  fresh(until);
  const requestSha256 = sha256(input.finishRequestBytes);
  const expected = finishIntentFor(z, requestSha256, descriptor, parent, previous, producer);
  const bytes = Buffer.from(canonicalJson(expected));
  requireThat(bytes.length <= STAGE_LIMITS.intent, 'stage_intent_limit');
  let uploadedId = null;
  try {
    checkpoint(progress, 'intent_upload');
    uploadedId = await transport.upload(
      finishIntentName(r),
      bytes,
      Math.max(1, Math.ceil((until - Date.now()) / 86400000) + 1),
    );
  } catch {
    /* Unknown consumes this entire independently authorized batch. */
  }
  checkpoint(progress, 'intent_reconcile');
  const fence = await intentMetadata(
    http,
    finishIntentName(r),
    { ...r, controlCommit: z.controlCommit },
    expected,
    transport,
    until,
    true,
  );
  requireThat(
    uploadedId &&
      fence?.artifactId === uploadedId &&
      same(fence.producer, producer) &&
      fence.sha256 === sha256(bytes),
    'completion_claim_write_unknown',
  );
  until = Math.min(until, timestamp(fence.expiresAt));
  fresh(until);
  await completionReview(http, z, requestSha256, producer);
  const again = await finishFences(z, r, descriptor, input, env, http, transport, until);
  requireThat(
    same(parent, again.parent) && same(previous, again.previous),
    'completion_parent_denied',
  );
  checkpoint(progress, 'prewrite');
  await tags(http, r);
  state = await releaseState(http, descriptor, z.releaseId);
  await verifyFinishSnapshot(http, state, z, assets);
  fresh(until);
  // One separately authorized ZIP application, never a resumed fourteen-item loop.
  const zip = assets.find((a) => a.name === 'qualification.zip');
  let response = null;
  try {
    checkpoint(progress, 'asset_upload');
    response = await http.post(
      `${ROOT}/releases/${state.id}/assets?name=qualification.zip`,
      zip.bytes,
      true,
    );
  } catch {
    /* Lost/rejected writes are read back, never replayed. */
  }
  checkpoint(progress, 'asset_readback');
  state = await releaseState(http, descriptor, z.releaseId);
  const records = await verifyAssets(http, state, assets, new Map());
  requireThat(
    z.snapshot.every((a) => records.some((b) => same(a, b))),
    'finish_snapshot_denied',
  );
  const written = records.find((a) => a.name === 'qualification.zip');
  requireThat(
    !response ||
      (decimal(response.id) === written.id &&
        response.name === written.name &&
        response.state === 'uploaded' &&
        response.size === written.size &&
        response.digest === 'sha256:' + written.sha256),
    'stage_upload_response_drift',
  );
  fresh(until);
  await tags(http, r);
  return writeStagedRecord(
    {
      formatVersion: 3,
      kind: 'r7-staged-pair',
      requestSha256: z.previousRequest.stageRequestSha256,
      descriptor,
      intent: parent,
      releaseId: state.id,
      assets: records,
      completion: { request: z, requestSha256, intent: fence },
    },
    input,
    env,
    progress,
    output,
  );
}
async function executeCompletion(input, env, adapters, progress, { c, producer, http }) {
  const r = c.stageRequest;
  checkpoint(progress, 'dedicated_repository');
  const repo = await http.get(ROOT);
  requireThat(
    repo?.full_name === REPOSITORY &&
      decimal(repo.id) === REPOSITORY_ID &&
      repo.permissions?.push === true,
    'stage_draft_visibility_denied',
  );
  await preflightEnvironment(c, http.get);
  checkpoint(progress, 'completion_parent');
  await completionParent(c, input, env, http);
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  let until = Math.min(deadline(r, qualification), timestamp(c.validUntil));
  fresh(until);
  checkpoint(progress, 'p3');
  await verifyQualifiedPair(
    {
      ...input,
      requestBytes: Buffer.from(canonicalJson(r.finalization)),
      pairSha256: r.pairSha256,
      qualifierCommit: r.qualifierCommit,
      qualificationId: r.qualificationId,
      qualificationSha256: r.qualificationSha256,
      receiptSha256: r.receiptSha256,
    },
    env,
  );
  const assets = await snapshots(input, r),
    descriptor = descriptorFor(r, assets),
    cache = new Map();
  requireThat(sha256(canonicalJson(descriptor)) === c.descriptorSha256, 'completion_parent_denied');
  const output = admitOutput(input.repo, input.output);
  await mkdir(output, { mode: 0o700 });
  const transport = adapters.transport ?? artifactTransport(output, env);
  const parent = await intentMetadata(
    http,
    intentName(r),
    r,
    intentFor(r, c.stageRequestSha256, descriptor, null),
    transport,
    until,
    true,
  );
  requireThat(
    parent &&
      parent.artifactId === c.origin.intentId &&
      parent.sha256 === c.origin.intentSha256 &&
      parent.producer.runId === c.origin.runId &&
      parent.producer.runAttempt === '1',
    'completion_parent_denied',
  );
  until = Math.min(until, timestamp(parent.expiresAt));
  await tags(http, r);
  let state = await releaseState(http, descriptor, c.releaseId);
  await verifyAssets(http, state, [], cache);
  requireThat((await supplementaryArtifacts(http, r)).length === 0, 'completion_claim_present');
  fresh(until);
  const expected = completionIntentFor(
      c,
      sha256(input.completionRequestBytes),
      descriptor,
      parent,
      producer,
    ),
    bytes = Buffer.from(canonicalJson(expected));
  requireThat(bytes.length <= STAGE_LIMITS.intent, 'stage_intent_limit');
  let uploadedId = null;
  try {
    checkpoint(progress, 'intent_upload');
    uploadedId = await transport.upload(
      completionIntentName(r),
      bytes,
      Math.max(1, Math.ceil((until - Date.now()) / 86400000) + 1),
    );
  } catch {
    /* Unknown consumes the entire new batch. No asset writes follow. */
  }
  checkpoint(progress, 'intent_reconcile');
  const fence = await intentMetadata(
    http,
    completionIntentName(r),
    { ...r, controlCommit: c.controlCommit },
    expected,
    transport,
    until,
    true,
  );
  requireThat(
    uploadedId &&
      fence?.artifactId === uploadedId &&
      same(fence.producer, producer) &&
      fence.sha256 === sha256(bytes),
    'completion_claim_write_unknown',
  );
  until = Math.min(until, timestamp(fence.expiresAt));
  fresh(until);
  await completionReview(http, c, sha256(input.completionRequestBytes), producer);
  await completionParent(c, input, env, http);
  await tags(http, r);
  state = await releaseState(http, descriptor, c.releaseId);
  await verifyAssets(http, state, [], cache);
  await uploadStageAssets(http, r, descriptor, state, assets, cache, until, progress);
  fresh(until);
  await tags(http, r);
  state = await releaseState(http, descriptor, c.releaseId);
  const records = await verifyAssets(http, state, assets, cache);
  return writeStagedRecord(
    {
      formatVersion: 2,
      kind: 'r7-staged-pair',
      requestSha256: c.stageRequestSha256,
      descriptor,
      intent: parent,
      releaseId: state.id,
      assets: records,
      completion: {
        request: c,
        requestSha256: sha256(input.completionRequestBytes),
        intent: fence,
      },
    },
    input,
    env,
    progress,
    output,
  );
}

async function uploadStageAssets(http, r, descriptor, state, assets, cache, until, progress) {
  for (let n = 0; n < assets.length; n++) {
    fresh(until);
    checkpoint(progress, 'prewrite');
    await tags(http, r);
    checkpoint(progress, 'asset_readback');
    state = await releaseState(http, descriptor, state.id);
    await verifyAssets(http, state, assets.slice(0, n), cache);
    fresh(until);
    const a = assets[n];
    let response = null;
    try {
      checkpoint(progress, 'asset_upload');
      response = await http.post(
        `${ROOT}/releases/${state.id}/assets?name=${encodeURIComponent(a.name)}`,
        a.bytes,
        true,
      );
    } catch {
      /* No replay on 422, starter, lost response, malformed JSON or 5xx. */
    }
    checkpoint(progress, 'asset_readback');
    state = await releaseState(http, descriptor, state.id);
    const records = await verifyAssets(http, state, assets.slice(0, n + 1), cache);
    requireThat(
      !response || decimal(response.id) === records.find((x) => x.name === a.name).id,
      'stage_upload_id_drift',
    );
    if (response)
      requireThat(
        response.name === a.name &&
          response.state === 'uploaded' &&
          response.size === a.size &&
          response.digest === 'sha256:' + a.sha256,
        'stage_upload_response_drift',
      );
  }
  return state;
}
export async function stageCandidate(input, env = process.env, adapters = {}, progress = null) {
  // Remove write authority BEFORE P3's gh subprocess or the artifact SDK can inherit it.
  const token = env.R7_STAGING_TOKEN;
  delete env.R7_STAGING_TOKEN;
  delete process.env.R7_STAGING_TOKEN;
  checkpoint(progress, 'request');
  const r = parseStageRequest(input.stageRequestBytes, env.R7_STAGE_REQUEST_SHA256);
  checkpoint(progress, 'authority');
  const producer = authority(input, env, r);
  checkpoint(progress, 'isolation');
  separateToken(token, env);
  const http = adapters.http ?? stageHttp(token);
  checkpoint(progress, 'dedicated_repository');
  const repo = await http.get(ROOT);
  requireThat(
    repo?.full_name === REPOSITORY &&
      decimal(repo.id) === REPOSITORY_ID &&
      repo.permissions?.push === true,
    'stage_draft_visibility_denied',
  );
  checkpoint(progress, 'dedicated_environment');
  await preflightEnvironment(r, http.get);
  checkpoint(progress, 'qualification');
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`),
    until = deadline(r, qualification);
  fresh(until);
  const output = admitOutput(input.repo, input.output);
  // Always execute the complete existing P3 authentication; no dependency-injected gate.
  checkpoint(progress, 'p3');
  await verifyQualifiedPair(
    {
      ...input,
      requestBytes: Buffer.from(canonicalJson(r.finalization)),
      pairSha256: r.pairSha256,
      qualifierCommit: r.qualifierCommit,
      qualificationId: r.qualificationId,
      qualificationSha256: r.qualificationSha256,
      receiptSha256: r.receiptSha256,
    },
    env,
  );
  checkpoint(progress, 'projection');
  const assets = await snapshots(input, r),
    descriptor = descriptorFor(r, assets),
    cache = new Map();
  checkpoint(progress, 'prewrite');
  await mkdir(output, { mode: 0o700 });
  const transport = adapters.transport ?? artifactTransport(output, env);
  const expectedIntent = intentFor(r, sha256(input.stageRequestBytes), descriptor, producer),
    name = intentName(r);
  checkpoint(progress, 'prewrite');
  requireThat((await supplementaryArtifacts(http, r)).length === 0, 'completion_record_required');
  await tags(http, r);
  let state = await releaseState(http, descriptor);
  checkpoint(progress, 'intent_reconcile');
  let fence = await intentMetadata(http, name, r, expectedIntent, transport, until);
  if (state) {
    requireThat(fence, 'stage_intent_missing');
    await verifyAssets(http, state, assets, cache);
  } else {
    requireThat(env.RELEASE_OPERATION === 'stage' && !fence, 'stage_recovery_incomplete');
    // Workflow concurrency serializes runners; a durable common local claim excludes duplicate invocations in one runner.
    requireThat(
      env.RUNNER_TEMP && (await lstat(env.RUNNER_TEMP)).isDirectory(),
      'stage_claim_denied',
    );
    checkpoint(progress, 'intent_claim');
    await durableFile(
      join(env.RUNNER_TEMP, name + '.claim'),
      Buffer.from(canonicalJson(expectedIntent)),
    );
    fresh(until);
    const bytes = Buffer.from(canonicalJson(expectedIntent));
    requireThat(bytes.length <= STAGE_LIMITS.intent, 'stage_intent_limit');
    let uploadedId = null;
    try {
      checkpoint(progress, 'intent_upload');
      uploadedId = await transport.upload(
        name,
        bytes,
        Math.max(1, Math.ceil((until - Date.now()) / 86400000) + 1),
      );
    } catch {
      /* Uncertain persistence never authorizes release mutation. */
    }
    checkpoint(progress, 'intent_reconcile');
    fence = await intentMetadata(http, name, r, expectedIntent, transport, until);
    requireThat(
      uploadedId &&
        fence?.artifactId === uploadedId &&
        same(fence.producer, producer) &&
        fence.sha256 === sha256(bytes),
      'stage_intent_write_unknown',
    );
    fresh(until);
    checkpoint(progress, 'prewrite');
    await tags(http, r);
    requireThat((await releaseState(http, descriptor)) === null, 'stage_release_race');
    fresh(until);
    let response = null;
    try {
      checkpoint(progress, 'release_create');
      response = await http.post(ROOT + '/releases', {
        tag_name: descriptor.tag,
        target_commitish: descriptor.sourceCommit,
        name: descriptor.tag,
        body: canonicalJson(descriptor),
        draft: true,
        prerelease: true,
        generate_release_notes: false,
        make_latest: 'false',
      });
    } catch {
      /* one POST only; exact readback below is the sole reconciliation */
    }
    checkpoint(progress, 'release_readback');
    state = await releaseState(http, descriptor);
    requireThat(state && (!response || decimal(response.id) === state.id), 'stage_create_unknown');
    if (response)
      requireThat(
        response.draft === true &&
          response.prerelease === true &&
          response.published_at === null &&
          response.tag_name === descriptor.tag &&
          response.target_commitish === descriptor.sourceCommit &&
          response.body === canonicalJson(descriptor),
        'stage_create_response_drift',
      );
    await verifyAssets(http, state, [], cache);
    state = await uploadStageAssets(http, r, descriptor, state, assets, cache, until, progress);
  }
  checkpoint(progress, 'prewrite');
  await tags(http, r);
  fresh(until);
  checkpoint(progress, 'final_verify');
  state = await releaseState(http, descriptor, state.id);
  const records = await verifyAssets(http, state, assets, cache);
  const result = {
    formatVersion: 1,
    kind: 'r7-staged-pair',
    requestSha256: sha256(input.stageRequestBytes),
    descriptor,
    intent: fence,
    releaseId: state.id,
    assets: records,
  };
  return writeStagedRecord(result, input, env, progress, output);
}
async function writeStagedRecord(result, input, env, progress, output) {
  if (input.expectedRecordBytes)
    requireThat(
      same(parseStagedRecord(input.expectedRecordBytes, input.expectedRecordSha256), result),
      'stage_record_drift',
    );
  const bytes = Buffer.from(canonicalJson(result)),
    hash = sha256(bytes);
  requireThat(bytes.length <= STAGE_LIMITS.record, 'stage_record_limit');
  parseStagedRecord(bytes, hash);
  checkpoint(progress, 'record_write');
  await durableFile(join(output, 'staged-pair.json'), bytes);
  await durableFile(join(output, 'staged-pair.sha256'), Buffer.from(hash + '\n'));
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `staged_sha256=${hash}\nrelease_id=${result.releaseId}\nstaged_json=${bytes.toString('utf8')}\n`,
    );
  if (env.GITHUB_STEP_SUMMARY)
    await appendFile(
      env.GITHUB_STEP_SUMMARY,
      `Unpublished candidate draft ${result.releaseId}\nStaged record SHA256: ${hash}\nNo publication or consumer tag authority.\n\n\`\`\`json\n${bytes.toString('utf8')}\n\`\`\`\n`,
    );
  return { stagedSha256: hash, releaseId: result.releaseId };
}
// P5 must supply the independently selected original request/record hashes and
// inputs again. This always performs full P3 + remote byte verification, GET only.
export async function verifyStagedCandidate(input, env = process.env, adapters = {}) {
  requireThat(input.expectedRecordBytes && input.expectedRecordSha256, 'stage_record_required');
  const record = parseStagedRecord(input.expectedRecordBytes, input.expectedRecordSha256);
  if (record.formatVersion >= 2) {
    const original =
      record.formatVersion === 3
        ? record.completion.request.previousRequest.stageRequest
        : record.completion.request.stageRequest;
    const token = env.R7_STAGING_TOKEN;
    delete env.R7_STAGING_TOKEN;
    delete process.env.R7_STAGING_TOKEN;
    separateToken(token, env);
    const producer = admitWorkflow(env, input.repo);
    requireThat(
      producer.workflowCommit === record.completion.request.controlCommit &&
        env.R7_STAGING_ENABLED === 'true' &&
        env.R7_STAGING_ENVIRONMENT === STAGE_ENVIRONMENT &&
        input.sourceCommit === original.pair.payload.identity.sourceCommit &&
        input.releaseVersion === original.pair.payload.identity.releaseVersion,
      'stage_authority_denied',
    );
    const scratch = admitOutput(input.repo, input.output + '-verify-' + randomUUID());
    await mkdir(scratch, { mode: 0o700 });
    const evidence = await verifyStagedEvidence(
      { ...input, stageRequestSha256: env.R7_STAGE_REQUEST_SHA256 },
      env,
      adapters.http ?? stageHttp(token),
      adapters.transport ?? artifactTransport(scratch, env),
    );
    return { stagedSha256: input.expectedRecordSha256, releaseId: evidence.state.id };
  }
  return stageCandidate(input, { ...env, RELEASE_OPERATION: 'recover-stage' }, adapters);
}

// Publication owns current authority. This reader authenticates historical R and
// the original P4 request/record/fence without impersonating a staging invocation.
// Only GET/download capabilities are used; existing staging entrypoints stay strict.
export async function verifyStagedEvidence(input, env, http, transport, phase = 'draft') {
  const r = parseStageRequest(input.stageRequestBytes, input.stageRequestSha256);
  const record = parseStagedRecord(input.expectedRecordBytes, input.expectedRecordSha256);
  const supplements = await supplementaryArtifacts(http, r);
  const finishes = await finishArtifacts(http, r);
  requireThat(
    (record.formatVersion >= 2 ? supplements.length === 1 : supplements.length === 0) &&
      (record.formatVersion === 3 ? finishes.length === 1 : finishes.length === 0),
    'completion_record_required',
  );
  const completion = record.formatVersion >= 2 ? record.completion : null;
  const finishing = record.formatVersion === 3;
  const originalCompletion = finishing ? completion.request.previousRequest : completion?.request;
  if (completion) {
    requireThat(
      same(originalCompletion.stageRequest, r) &&
        originalCompletion.stageRequestSha256 === input.stageRequestSha256,
      'completion_request_denied',
    );
    historicalControl(input.repo, completion.request.controlCommit, true);
    if (finishing) await finishHistory(completion.request, input, env, http);
    else await completionParent(completion.request, input, env, http);
  }
  try {
    execFileSync(
      'git',
      [
        '--no-replace-objects',
        '-C',
        input.repo,
        'merge-base',
        '--is-ancestor',
        r.controlCommit,
        'HEAD',
      ],
      {
        env: { PATH: process.env.PATH },
        timeout: 30000,
        stdio: ['ignore', 'pipe', 'pipe'],
      },
    );
  } catch {
    throw controlledError('stage_historical_control_denied');
  }
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  let until = Math.min(
    deadline(r, qualification),
    completion ? timestamp(completion.request.validUntil) : Infinity,
  );
  fresh(until);
  await verifyQualifiedPair(
    {
      ...input,
      requestBytes: Buffer.from(canonicalJson(r.finalization)),
      pairSha256: r.pairSha256,
      qualifierCommit: r.qualifierCommit,
      qualificationId: r.qualificationId,
      qualificationSha256: r.qualificationSha256,
      receiptSha256: r.receiptSha256,
    },
    env,
  );
  const assets = await snapshots(input, r),
    descriptor = descriptorFor(r, assets);
  const fence = await intentMetadata(
    http,
    intentName(r),
    r,
    intentFor(r, input.stageRequestSha256, descriptor, null),
    transport,
    until,
    Boolean(completion),
  );
  requireThat(fence, 'stage_intent_missing');
  let completionFence = null;
  if (completion) {
    const c = originalCompletion;
    requireThat(
      fence.artifactId === c.origin.intentId &&
        fence.sha256 === c.origin.intentSha256 &&
        fence.producer.runId === c.origin.runId &&
        fence.producer.runAttempt === '1',
      'completion_parent_denied',
    );
    until = Math.min(until, timestamp(fence.expiresAt));
    completionFence = await intentMetadata(
      http,
      completionIntentName(r),
      { ...r, controlCommit: c.controlCommit },
      completionIntentFor(
        c,
        finishing ? completion.request.previousRequestSha256 : completion.requestSha256,
        descriptor,
        fence,
        null,
      ),
      transport,
      until,
      true,
    );
    requireThat(completionFence, 'completion_record_required');
    if (finishing) {
      const z = completion.request;
      requireThat(
        completionFence.artifactId === z.origin.intentId &&
          completionFence.sha256 === z.origin.intentSha256 &&
          completionFence.producer.runId === z.origin.runId &&
          completionFence.producer.runAttempt === '1',
        'completion_parent_denied',
      );
      until = Math.min(until, timestamp(completionFence.expiresAt));
      completionFence = await intentMetadata(
        http,
        finishIntentName(r),
        { ...r, controlCommit: z.controlCommit },
        finishIntentFor(z, completion.requestSha256, descriptor, fence, completionFence, null),
        transport,
        until,
        true,
      );
      requireThat(completionFence, 'completion_record_required');
      requireThat(completionFence.producer.runId !== c.origin.runId, 'completion_producer_denied');
      await completionCreator(http, z, completionFence.producer, completion.requestSha256);
    } else await completionCreator(http, c, completionFence.producer, completion.requestSha256);
    until = Math.min(until, timestamp(completionFence.expiresAt));
    fresh(until);
  }
  const state = await releaseState(http, descriptor, record.releaseId, phase);
  const records = await verifyAssets(http, state, assets, new Map());
  if (finishing) {
    finishSnapshot(completion.request, assets);
    requireThat(
      completion.request.snapshot.every((a) => records.some((b) => same(a, b))),
      'finish_snapshot_denied',
    );
  }
  requireThat(
    same(record, {
      formatVersion: record.formatVersion,
      kind: 'r7-staged-pair',
      requestSha256: input.stageRequestSha256,
      descriptor,
      intent: fence,
      releaseId: state.id,
      assets: records,
      ...(completion
        ? {
            completion: {
              request: completion.request,
              requestSha256: completion.requestSha256,
              intent: completionFence,
            },
          }
        : {}),
    }),
    'stage_record_drift',
  );
  return {
    request: r,
    record,
    assets,
    descriptor,
    state,
    fence,
    until: Math.min(until, timestamp(fence.expiresAt)),
  };
}

export async function acquire(r, scratch, env, progress = null) {
  const get = githubReader(env.GITHUB_TOKEN),
    t = artifactTransport(scratch, env),
    pair = r.pair;
  const paths = {};
  for (const [role, target] of [
    ['payload', 'archivePath'],
    ['metadata', 'candidatePath'],
    ['provenance', 'bundlePath'],
  ]) {
    checkpoint(progress, 'acquire_' + role);
    const s = pair.storage[role];
    await verifyStorage(pair.producer, s, get, { role });
    const a = await get(`${ROOT}/actions/artifacts/${s.artifactId}`),
      downloaded = await t.download(a, false);
    paths[target] = join(
      downloaded.directory,
      role === 'payload'
        ? pair.payload.archiveName
        : role === 'metadata'
          ? 'candidate.json'
          : 'bundle.json',
    );
    if (role === 'metadata') paths.mapPath = join(downloaded.directory, 'proposed-action-map.json');
  }
  checkpoint(progress, 'acquire_qualification');
  const a = await get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  requireThat(
    a.digest === 'sha256:' + r.qualificationSha256 &&
      a.expired === false &&
      a.size_in_bytes <= PAIR_LIMITS.qualificationArtifact,
    'stage_qualification_download_denied',
  );
  const downloaded = await t.download(a, true, PAIR_LIMITS.qualificationArtifact);
  paths.qualificationArchivePath = downloaded.path;
  checkpoint(progress, 'acquire_pair');
  paths.pairPath = join(scratch, 'finalized-pair.json');
  await durableFile(paths.pairPath, Buffer.from(canonicalJson(pair)));
  return paths;
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  const progress = { checkpoint: 'arguments' };
  try {
    // Capture into this closure then erase before any SDK/gh subprocess starts.
    const token = env.R7_STAGING_TOKEN;
    delete env.R7_STAGING_TOKEN;
    delete process.env.R7_STAGING_TOKEN;
    requireThat(
      argv.length === 7 &&
        [
          'preflight',
          'workflow-stage',
          'workflow-complete-stage',
          'workflow-finish-stage',
        ].includes(argv[0]) &&
        argv[1] === '--source' &&
        argv[3] === '--version' &&
        argv[5] === '--output',
      'stage_arguments_invalid',
    );
    const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..'),
      rbytes = Buffer.from(env.STAGE_REQUEST ?? '');
    checkpoint(progress, 'request');
    const completing = env.RELEASE_OPERATION === 'complete-stage';
    const finishing = env.RELEASE_OPERATION === 'finish-stage';
    requireThat(
      argv[0] === 'preflight' ||
        argv[0] ===
          (finishing
            ? 'workflow-finish-stage'
            : completing
              ? 'workflow-complete-stage'
              : 'workflow-stage'),
      'stage_arguments_invalid',
    );
    const c = finishing
      ? parseFinishRequest(rbytes, env.R7_STAGE_FINISH_REQUEST_SHA256)
      : completing
        ? parseCompletionRequest(rbytes, env.R7_STAGE_COMPLETION_REQUEST_SHA256)
        : null;
    const r = finishing
        ? c.previousRequest.stageRequest
        : c
          ? c.stageRequest
          : parseStageRequest(rbytes, env.R7_STAGE_REQUEST_SHA256),
      input = {
        repo,
        sourceCommit: argv[2],
        releaseVersion: argv[4],
        output: argv[6],
        stageRequestBytes: c ? Buffer.from(canonicalJson(r)) : rbytes,
        ...(finishing
          ? { finishRequestBytes: rbytes }
          : c
            ? { completionRequestBytes: rbytes }
            : {}),
      };
    const modeEnv = { ...env, R7_STAGING_ENVIRONMENT: STAGE_ENVIRONMENT };
    checkpoint(progress, 'authority');
    if (c) completionAuthority(input, modeEnv, c);
    else authority(input, modeEnv, r);
    checkpoint(progress, 'deadline');
    fresh(Math.min(timestamp(r.validUntil), c ? timestamp(c.validUntil) : Infinity));
    // P3's artifact reader intentionally excludes environments. Use P4's fixed-host,
    // bounded GET transport with ordinary read authority for both CLI commands.
    checkpoint(progress, 'environment');
    await preflightEnvironment(r, stageHttp(env.GITHUB_TOKEN).get);
    if (argv[0] === 'preflight') {
      if (finishing) await finishHistory(c, input, modeEnv, stageHttp(env.GITHUB_TOKEN));
      else if (c) await completionParent(c, input, modeEnv, stageHttp(env.GITHUB_TOKEN));
      return { environmentAdmitted: true };
    }
    checkpoint(progress, 'isolation');
    separateToken(token, env);
    // This authenticates normal review and creates the exclusive local claim
    // before acquire can start any SDK child. Never accept an injected admission.
    const admission = c ? await completionAdmission(input, modeEnv, token, {}, progress) : null;
    checkpoint(progress, 'scratch');
    const scratch = admitOutput(repo, input.output + '-inputs');
    await mkdir(scratch, { mode: 0o700 });
    const paths = await acquire(r, scratch, env, progress);
    if (finishing)
      return await executeFinish({ ...input, ...paths }, modeEnv, {}, progress, admission);
    if (admission)
      return await executeCompletion({ ...input, ...paths }, modeEnv, {}, progress, admission);
    return await stageCandidate(
      { ...input, ...paths },
      { ...modeEnv, R7_STAGING_TOKEN: token },
      {},
      progress,
    );
  } catch (error) {
    const thrown =
      error !== null && (typeof error === 'object' || typeof error === 'function')
        ? error
        : new Error('candidate_staging_denied');
    diagnostics.set(thrown, { ...diagnostics.get(thrown), checkpoint: progress.checkpoint });
    throw thrown;
  }
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((r) => process.stdout.write(canonicalJson(r)))
    .catch((error) => {
      process.stderr.write(stagingDiagnostic(error));
      process.exitCode = 1;
    });
}
