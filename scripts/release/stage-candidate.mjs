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
  requireThat,
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
} from './stage-candidate.format.mjs';
import { stageHttp } from './stage-candidate.http.mjs';

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
    return JSON.parse(await readBoundedFile(resultPath, 1024));
  } catch {
    throw new Error('stage_artifact_transport_unknown');
  }
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
      throw new Error('stage_action_read_denied');
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
export async function intentMetadata(http, name, r, expected, transport, until) {
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
      timestamp(a.expires_at) >= until &&
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
    throw new Error('stage_intent_invalid');
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
export async function stageCandidate(input, env = process.env, adapters = {}) {
  // Remove write authority BEFORE P3's gh subprocess or the artifact SDK can inherit it.
  const token = env.R7_STAGING_TOKEN;
  delete env.R7_STAGING_TOKEN;
  delete process.env.R7_STAGING_TOKEN;
  const r = parseStageRequest(input.stageRequestBytes, env.R7_STAGE_REQUEST_SHA256);
  const producer = authority(input, env, r);
  separateToken(token, env);
  const http = adapters.http ?? stageHttp(token);
  const repo = await http.get(ROOT);
  requireThat(
    repo?.full_name === REPOSITORY &&
      decimal(repo.id) === REPOSITORY_ID &&
      repo.permissions?.push === true,
    'stage_draft_visibility_denied',
  );
  await preflightEnvironment(r, http.get);
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`),
    until = deadline(r, qualification);
  fresh(until);
  const output = admitOutput(input.repo, input.output);
  // Always execute the complete existing P3 authentication; no dependency-injected gate.
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
  await mkdir(output, { mode: 0o700 });
  const transport = adapters.transport ?? artifactTransport(output, env);
  const expectedIntent = intentFor(r, sha256(input.stageRequestBytes), descriptor, producer),
    name = intentName(r);
  await tags(http, r);
  let state = await releaseState(http, descriptor),
    fence = await intentMetadata(http, name, r, expectedIntent, transport, until);
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
    await durableFile(
      join(env.RUNNER_TEMP, name + '.claim'),
      Buffer.from(canonicalJson(expectedIntent)),
    );
    fresh(until);
    const bytes = Buffer.from(canonicalJson(expectedIntent));
    requireThat(bytes.length <= STAGE_LIMITS.intent, 'stage_intent_limit');
    let uploadedId = null;
    try {
      uploadedId = await transport.upload(
        name,
        bytes,
        Math.max(1, Math.ceil((until - Date.now()) / 86400000) + 1),
      );
    } catch {
      /* Uncertain persistence never authorizes release mutation. */
    }
    fence = await intentMetadata(http, name, r, expectedIntent, transport, until);
    requireThat(
      uploadedId &&
        fence?.artifactId === uploadedId &&
        same(fence.producer, producer) &&
        fence.sha256 === sha256(bytes),
      'stage_intent_write_unknown',
    );
    fresh(until);
    await tags(http, r);
    requireThat((await releaseState(http, descriptor)) === null, 'stage_release_race');
    fresh(until);
    let response = null;
    try {
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
    for (let n = 0; n < assets.length; n++) {
      fresh(until);
      await tags(http, r);
      state = await releaseState(http, descriptor, state.id);
      await verifyAssets(http, state, assets.slice(0, n), cache);
      fresh(until);
      const a = assets[n];
      response = null;
      try {
        response = await http.post(
          `${ROOT}/releases/${state.id}/assets?name=${encodeURIComponent(a.name)}`,
          a.bytes,
          true,
        );
      } catch {
        /* No replay on 422, starter, lost response, malformed JSON or 5xx. */
      }
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
  }
  await tags(http, r);
  fresh(until);
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
  if (input.expectedRecordBytes)
    requireThat(
      same(parseStagedRecord(input.expectedRecordBytes, input.expectedRecordSha256), result),
      'stage_record_drift',
    );
  const bytes = Buffer.from(canonicalJson(result)),
    hash = sha256(bytes);
  requireThat(bytes.length <= STAGE_LIMITS.record, 'stage_record_limit');
  await durableFile(join(output, 'staged-pair.json'), bytes);
  await durableFile(join(output, 'staged-pair.sha256'), Buffer.from(hash + '\n'));
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `staged_sha256=${hash}\nrelease_id=${state.id}\nstaged_json=${bytes.toString('utf8')}\n`,
    );
  if (env.GITHUB_STEP_SUMMARY)
    await appendFile(
      env.GITHUB_STEP_SUMMARY,
      `Unpublished candidate draft ${state.id}\nStaged record SHA256: ${hash}\nNo publication or consumer tag authority.\n\n\`\`\`json\n${bytes.toString('utf8')}\n\`\`\`\n`,
    );
  return { stagedSha256: hash, releaseId: state.id };
}
// P5 must supply the independently selected original request/record hashes and
// inputs again. This always performs full P3 + remote byte verification, GET only.
export async function verifyStagedCandidate(input, env = process.env, adapters = {}) {
  requireThat(input.expectedRecordBytes && input.expectedRecordSha256, 'stage_record_required');
  return stageCandidate(input, { ...env, RELEASE_OPERATION: 'recover-stage' }, adapters);
}

// Publication owns current authority. This reader authenticates historical R and
// the original P4 request/record/fence without impersonating a staging invocation.
// Only GET/download capabilities are used; existing staging entrypoints stay strict.
export async function verifyStagedEvidence(input, env, http, transport, phase = 'draft') {
  const r = parseStageRequest(input.stageRequestBytes, input.stageRequestSha256);
  const record = parseStagedRecord(input.expectedRecordBytes, input.expectedRecordSha256);
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
    throw new Error('stage_historical_control_denied');
  }
  const qualification = await http.get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  const until = deadline(r, qualification);
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
  );
  requireThat(fence, 'stage_intent_missing');
  const state = await releaseState(http, descriptor, record.releaseId, phase);
  const records = await verifyAssets(http, state, assets, new Map());
  requireThat(
    same(record, {
      formatVersion: 1,
      kind: 'r7-staged-pair',
      requestSha256: input.stageRequestSha256,
      descriptor,
      intent: fence,
      releaseId: state.id,
      assets: records,
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

export async function acquire(r, scratch, env) {
  const get = githubReader(env.GITHUB_TOKEN),
    t = artifactTransport(scratch, env),
    pair = r.pair;
  const paths = {};
  for (const [role, target] of [
    ['payload', 'archivePath'],
    ['metadata', 'candidatePath'],
    ['provenance', 'bundlePath'],
  ]) {
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
  const a = await get(`${ROOT}/actions/artifacts/${r.qualificationId}`);
  requireThat(
    a.digest === 'sha256:' + r.qualificationSha256 &&
      a.expired === false &&
      a.size_in_bytes <= PAIR_LIMITS.qualificationArtifact,
    'stage_qualification_download_denied',
  );
  const downloaded = await t.download(a, true, PAIR_LIMITS.qualificationArtifact);
  paths.qualificationArchivePath = downloaded.path;
  paths.pairPath = join(scratch, 'finalized-pair.json');
  await durableFile(paths.pairPath, Buffer.from(canonicalJson(pair)));
  return paths;
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  // Capture into this closure then erase before any SDK/gh subprocess starts.
  const token = env.R7_STAGING_TOKEN;
  delete env.R7_STAGING_TOKEN;
  delete process.env.R7_STAGING_TOKEN;
  requireThat(
    argv.length === 7 &&
      ['preflight', 'workflow-stage'].includes(argv[0]) &&
      argv[1] === '--source' &&
      argv[3] === '--version' &&
      argv[5] === '--output',
    'stage_arguments_invalid',
  );
  const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..'),
    rbytes = Buffer.from(env.STAGE_REQUEST ?? '');
  const r = parseStageRequest(rbytes, env.R7_STAGE_REQUEST_SHA256),
    input = {
      repo,
      sourceCommit: argv[2],
      releaseVersion: argv[4],
      output: argv[6],
      stageRequestBytes: rbytes,
    };
  const modeEnv = { ...env, R7_STAGING_ENVIRONMENT: STAGE_ENVIRONMENT };
  authority(input, modeEnv, r);
  fresh(timestamp(r.validUntil));
  // P3's artifact reader intentionally excludes environments. Use P4's fixed-host,
  // bounded GET transport with ordinary read authority for both CLI commands.
  await preflightEnvironment(r, stageHttp(env.GITHUB_TOKEN).get);
  if (argv[0] === 'preflight') return { environmentAdmitted: true };
  separateToken(token, env);
  const scratch = admitOutput(repo, input.output + '-inputs');
  await mkdir(scratch, { mode: 0o700 });
  const paths = await acquire(r, scratch, env);
  return stageCandidate({ ...input, ...paths }, { ...modeEnv, R7_STAGING_TOKEN: token });
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((r) => process.stdout.write(canonicalJson(r)))
    .catch(() => {
      process.stderr.write('candidate_staging_denied\n');
      process.exitCode = 1;
    });
}
