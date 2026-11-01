import { execFile } from 'node:child_process';
import { appendFile, lstat, mkdir } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { canonicalJson, sha256, validateReleaseVersion } from './build-payload.format.mjs';
import { admitOutput } from './build-payload.mjs';
import { decimal, REPOSITORY, REPOSITORY_ID, requireThat, timestamp } from './candidate.mjs';
import { verifyQualifiedPair } from './finalize-candidate.mjs';
import { admitWorkflow } from './prepare-candidate.mjs';
import {
  acquire,
  artifactTransport,
  durableFile,
  intentMetadata,
  releaseState,
  verifyAssets,
  verifyStagedEvidence,
} from './stage-candidate.mjs';
import { same } from './stage-candidate.format.mjs';
import {
  parsePromotionRequest,
  promotionIntent,
  promotionIntentName,
  publicIdentity,
  PROMOTION_LIMITS,
  PUBLICATION_ENVIRONMENT,
} from './promote-candidate.format.mjs';
import { promotionHttp, verifyPublicRuntime } from './promote-candidate.http.mjs';
import { GH_VERSION } from './verify-provenance.mjs';

const ROOT = `/repos/${REPOSITORY}`;
const runFile = promisify(execFile);
function capture(env) {
  const tokens = { publisher: env.R7_PUBLISH_TOKEN, protection: env.R7_PROTECTION_TOKEN };
  for (const name of ['R7_PUBLISH_TOKEN', 'R7_PROTECTION_TOKEN']) {
    delete env[name];
    delete process.env[name];
  }
  return tokens;
}
function isolate(tokens, env) {
  const values = Object.values(tokens);
  requireThat(
    typeof env.GITHUB_TOKEN === 'string' &&
      env.GITHUB_TOKEN.length > 0 &&
      values.every(
        (t) =>
          typeof t === 'string' &&
          t.length > 0 &&
          ![
            env.GITHUB_TOKEN,
            env.GH_TOKEN,
            process.env.GH_TOKEN,
            process.env.GITHUB_TOKEN,
          ].includes(t),
      ) &&
      values[0] !== values[1],
    'promotion_token_isolation_denied',
  );
}
function authority(input, env, r) {
  const producer = admitWorkflow(env, input.repo);
  requireThat(
    ['promote', 'recover-promote'].includes(env.RELEASE_OPERATION) &&
      env.R7_PUBLICATION_ENABLED === 'true' &&
      env.R7_PUBLICATION_ENVIRONMENT === PUBLICATION_ENVIRONMENT &&
      producer.workflowCommit === r.controlCommit &&
      r.stageRequest.pair.payload.identity.sourceCommit === input.sourceCommit &&
      r.stageRequest.pair.payload.identity.releaseVersion === input.releaseVersion,
    'promotion_authority_denied',
  );
  validateReleaseVersion(input.releaseVersion);
  return producer;
}
function fresh(until) {
  requireThat(Date.now() < until, 'promotion_authority_expired');
}
export async function preflightPublication(r, get) {
  const e = await get(`${ROOT}/environments/${PUBLICATION_ENVIRONMENT}`);
  requireThat(
    e?.name === PUBLICATION_ENVIRONMENT && decimal(e.id) === r.environmentId,
    'promotion_environment_denied',
  );
}
export async function readProtection(r, http) {
  const immutable = await http.get(ROOT + '/immutable-releases');
  requireThat(immutable?.enabled === true, 'promotion_immutable_protection_denied');
  const s = await http.get(`${ROOT}/rulesets/${r.actionRulesetId}`),
    ref = 'refs/tags/' + r.stageRequest.pair.payload.identity.releaseVersion;
  const names = s?.conditions?.ref_name;
  requireThat(
    s &&
      decimal(s.id) === r.actionRulesetId &&
      s.source_type === 'Repository' &&
      s.source === REPOSITORY &&
      s.target === 'tag' &&
      s.enforcement === 'active' &&
      Array.isArray(s.bypass_actors) &&
      s.bypass_actors.length === 0 &&
      s.conditions &&
      Object.keys(s.conditions).length === 1 &&
      names &&
      Object.keys(names).sort().join(',') === 'exclude,include' &&
      Array.isArray(names.exclude) &&
      names.exclude.length === 0 &&
      Array.isArray(names.include) &&
      names.include.length === 1 &&
      [ref, 'refs/tags/v*-internal.*'].includes(names.include[0]) &&
      Array.isArray(s.rules) &&
      s.rules.length === 2,
    'promotion_action_protection_denied',
  );
  const update = s.rules.find((x) => x.type === 'update'),
    deletion = s.rules.find((x) => x.type === 'deletion');
  requireThat(
    update &&
      deletion &&
      same(deletion, { type: 'deletion' }) &&
      (same(update, { type: 'update' }) ||
        same(update, { type: 'update', parameters: { update_allows_fetch_and_merge: false } })),
    'promotion_action_protection_denied',
  );
  return {
    immutableEnabled: true,
    enforcedByOwner: immutable.enforced_by_owner === true,
    actionRuleset: {
      id: r.actionRulesetId,
      source: s.source,
      target: s.target,
      enforcement: s.enforcement,
      conditions: s.conditions,
      rules: s.rules,
      bypass_actors: [],
    },
  };
}
async function refs(http, r, publicPayload, complete) {
  const p = r.stageRequest.pair,
    version = p.payload.identity.releaseVersion;
  const c = await http.get(`${ROOT}/git/ref/tags/${version}`, true);
  requireThat(
    complete
      ? c?.ref === 'refs/tags/' + version &&
          c.object?.type === 'commit' &&
          c.object.sha === p.action.commit
      : c === null,
    'promotion_action_ref_drift',
  );
  const payload = await http.get(`${ROOT}/git/ref/tags/payload-${version}`, true);
  requireThat(payload || !publicPayload, 'promotion_payload_ref_missing');
  if (payload) {
    requireThat(payload.ref === `refs/tags/payload-${version}`, 'promotion_payload_ref_drift');
    let object = payload.object;
    const visited = new Set();
    for (let n = 0; object?.type === 'tag' && n < 5; n++) {
      requireThat(
        /^[a-f0-9]{40}$/.test(object.sha) && !visited.has(object.sha),
        'promotion_payload_ref_drift',
      );
      visited.add(object.sha);
      object = (await http.get(`${ROOT}/git/tags/${object.sha}`))?.object;
    }
    requireThat(
      object?.type === 'commit' && object.sha === p.payload.identity.sourceCommit,
      'promotion_payload_ref_drift',
    );
  }
}
async function nativeReleaseProof(tag, archivePath, scratch, env) {
  const config = join(scratch, 'gh-config');
  await mkdir(config, { mode: 0o700 });
  const childEnv = {
    PATH: process.env.PATH,
    HOME: config,
    GH_CONFIG_DIR: config,
    XDG_CACHE_HOME: config,
    GH_HOST: 'github.com',
    GH_TOKEN: env.GITHUB_TOKEN,
    GH_PROMPT_DISABLED: '1',
    GH_NO_UPDATE_NOTIFIER: '1',
  };
  async function gh(args) {
    try {
      return await runFile('gh', args, {
        env: childEnv,
        timeout: 120000,
        killSignal: 'SIGKILL',
        maxBuffer: 1048576,
      });
    } catch {
      throw new Error('promotion_release_verification_denied');
    }
  }
  requireThat(
    (await gh(['--version'])).stdout.split('\n')[0].startsWith(`gh version ${GH_VERSION} (`),
    'promotion_release_verifier_version_denied',
  );
  await gh(['release', 'verify', tag, '--repo', REPOSITORY]);
  await gh(['release', 'verify-asset', tag, archivePath, '--repo', REPOSITORY]);
}
async function publicProof(input, r, evidence, publicHttp, output, env) {
  const repository = await publicHttp.get(ROOT);
  requireThat(
    repository?.full_name === REPOSITORY &&
      decimal(repository.id) === REPOSITORY_ID &&
      repository.private === false &&
      repository.visibility === 'public',
    'promotion_public_repository_denied',
  );
  const state = await releaseState(publicHttp, evidence.descriptor, evidence.state.id, 'public');
  const directory = join(output, 'public');
  await mkdir(directory, { mode: 0o700 });
  // This cache is populated ONLY by independently downloaded unauthenticated bytes.
  const cache = new Map(),
    paths = {};
  for (const a of r.stagedRecord.assets) {
    const bytes = await publicHttp.assetBytes(a.id, a.size);
    requireThat(sha256(bytes) === a.sha256, 'promotion_public_bytes_drift');
    const path = join(directory, a.name);
    await durableFile(path, bytes);
    paths[a.name] = path;
    cache.set(a.name, a);
  }
  requireThat(
    same(await verifyAssets(publicHttp, state, evidence.assets, cache), r.stagedRecord.assets),
    'promotion_public_asset_drift',
  );
  const original = r.stageRequest,
    pair = original.pair;
  await verifyQualifiedPair(
    {
      ...input,
      requestBytes: Buffer.from(canonicalJson(original.finalization)),
      archivePath: paths[pair.payload.archiveName],
      candidatePath: paths['candidate.json'],
      mapPath: paths['action-payload-map.json'],
      bundlePath: paths['provenance-bundle.json'],
      pairPath: paths['finalized-pair.json'],
      pairSha256: original.pairSha256,
      qualifierCommit: original.qualifierCommit,
      qualificationId: original.qualificationId,
      qualificationSha256: original.qualificationSha256,
      receiptSha256: original.receiptSha256,
      qualificationArchivePath: paths['qualification.zip'],
    },
    env,
  );
  await verifyPublicRuntime(pair.action.runtime.policy);
  await nativeReleaseProof(evidence.descriptor.tag, paths[pair.payload.archiveName], output, env);
  // Re-enumerate identities after the independent gate; replacement IDs cannot be hidden by a cache.
  const after = await releaseState(publicHttp, evidence.descriptor, state.id, 'public');
  requireThat(
    same(await verifyAssets(publicHttp, after, evidence.assets, cache), r.stagedRecord.assets),
    'promotion_public_asset_drift',
  );
  return {
    ...publicIdentity(r),
    bytesVerified: true,
    qualificationVerified: true,
    releaseIntegrityVerified: true,
    runtimePolicy: pair.action.runtime.policy,
  };
}
export async function promoteCandidate(input, env = process.env, adapters = {}) {
  const tokens = capture(env);
  const r = parsePromotionRequest(input.promotionRequestBytes, env.R7_PUBLISH_REQUEST_SHA256),
    producer = authority(input, env, r);
  isolate(tokens, env);
  fresh(timestamp(r.validUntil));
  const http = adapters.http ?? promotionHttp(tokens.publisher, 'publisher'),
    protectionHttp = adapters.protectionHttp ?? promotionHttp(tokens.protection, 'protection'),
    publicHttp = adapters.publicHttp ?? promotionHttp(null, 'public');
  const repository = await http.get(ROOT);
  requireThat(
    repository?.full_name === REPOSITORY &&
      decimal(repository.id) === REPOSITORY_ID &&
      repository.permissions?.push === true &&
      repository.private === false &&
      repository.visibility === 'public',
    'promotion_draft_visibility_denied',
  );
  await preflightPublication(r, http.get);
  const protection = await readProtection(r, protectionHttp);
  const output = admitOutput(input.repo, input.output);
  await mkdir(output, { mode: 0o700 });
  const transport = adapters.transport ?? artifactTransport(output, env);
  const original = r.stageRequest,
    pair = original.pair,
    initial = await http.get(`${ROOT}/releases/${r.stagedRecord.releaseId}`),
    phase = initial.draft === true ? 'draft' : 'public';
  const evidence = await verifyStagedEvidence(
    {
      ...input,
      stageRequestBytes: Buffer.from(canonicalJson(original)),
      stageRequestSha256: r.stageRequestSha256,
      expectedRecordBytes: Buffer.from(canonicalJson(r.stagedRecord)),
      expectedRecordSha256: r.stagedRecordSha256,
    },
    env,
    http,
    transport,
    phase,
  );
  let until = Math.min(timestamp(r.validUntil), evidence.until);
  fresh(until);
  const name = promotionIntentName(r),
    expected = promotionIntent(r, sha256(input.promotionRequestBytes), producer);
  let fence = await intentMetadata(http, name, r, expected, transport, until);
  async function check(publicPayload, complete) {
    fresh(until);
    requireThat(
      same(await readProtection(r, protectionHttp), protection),
      'promotion_protection_drift',
    );
    await refs(http, r, publicPayload, complete);
    const state = await releaseState(
      http,
      evidence.descriptor,
      evidence.state.id,
      publicPayload ? 'public' : 'draft',
    );
    requireThat(
      same(await verifyAssets(http, state, evidence.assets, new Map()), r.stagedRecord.assets),
      'promotion_asset_record_drift',
    );
    fresh(until);
    return state;
  }
  if (fence) {
    // Every subsequent process is READ ONLY, including same run/attempt and new nonces.
    until = Math.min(until, timestamp(fence.expiresAt));
    requireThat(phase === 'public', 'promotion_recovery_incomplete');
    await check(true, true);
  } else {
    requireThat(
      env.RELEASE_OPERATION === 'promote' && phase === 'draft',
      'promotion_intent_missing',
    );
    await check(false, false);
    requireThat(
      env.RUNNER_TEMP && (await lstat(env.RUNNER_TEMP)).isDirectory(),
      'promotion_claim_denied',
    );
    const bytes = Buffer.from(canonicalJson(expected));
    await durableFile(join(env.RUNNER_TEMP, name + '.claim'), bytes);
    let uploadedId = null;
    try {
      uploadedId = await transport.upload(
        name,
        bytes,
        Math.max(1, Math.ceil((until - Date.now()) / 86400000) + 1),
      );
    } catch {
      /* Unknown persistence never authorizes a write. */
    }
    fence = await intentMetadata(http, name, r, expected, transport, until);
    requireThat(
      uploadedId &&
        fence?.artifactId === uploadedId &&
        same(fence.producer, producer) &&
        fence.sha256 === sha256(bytes),
      'promotion_intent_write_unknown',
    );
    until = Math.min(until, timestamp(fence.expiresAt));
    await check(false, false);
    let response = null;
    try {
      response = await http.patch(`${ROOT}/releases/${evidence.state.id}`, {
        draft: false,
        prerelease: true,
        make_latest: 'false',
      });
    } catch {
      /* One PATCH. Exact readback is the only permitted reconciliation. */
    }
    const state = await check(true, false);
    requireThat(
      !response ||
        (decimal(response.id) === state.id &&
          response.draft === false &&
          response.prerelease === true &&
          response.immutable === true &&
          response.tag_name === evidence.descriptor.tag &&
          response.target_commitish === pair.payload.identity.sourceCommit &&
          response.body === canonicalJson(evidence.descriptor)),
      'promotion_publish_response_drift',
    );
  }
  const proof = await publicProof(input, r, evidence, publicHttp, output, env);
  if (phase === 'draft') {
    await check(true, false);
    let response = null;
    try {
      response = await http.post(ROOT + '/git/refs', {
        ref: 'refs/tags/' + input.releaseVersion,
        sha: pair.action.commit,
      });
    } catch {
      /* One POST. An absent/unknown ref is not authority to replay. */
    }
    await check(true, true);
    requireThat(
      !response ||
        (response.ref === 'refs/tags/' + input.releaseVersion &&
          response.object?.type === 'commit' &&
          response.object.sha === pair.action.commit),
      'promotion_tag_response_drift',
    );
  }
  await check(true, true);
  const result = {
    formatVersion: 1,
    kind: 'r7-published-pair',
    requestSha256: sha256(input.promotionRequestBytes),
    stagedRecordSha256: r.stagedRecordSha256,
    pairSha256: original.pairSha256,
    qualificationSha256: original.qualificationSha256,
    receiptSha256: original.receiptSha256,
    provenanceSha256: pair.provenance.bundleSha256,
    sourceCommit: pair.payload.identity.sourceCommit,
    workflowCommit: pair.producer.workflowCommit,
    actionCommit: pair.action.commit,
    qualifierCommit: original.qualifierCommit,
    releaseVersion: input.releaseVersion,
    releaseId: evidence.state.id,
    assets: r.stagedRecord.assets,
    protection,
    intent: fence,
    publicProof: proof,
  };
  const bytes = Buffer.from(canonicalJson(result)),
    hash = sha256(bytes);
  requireThat(bytes.length <= PROMOTION_LIMITS.record, 'promotion_record_limit');
  await durableFile(join(output, 'published-pair.json'), bytes);
  await durableFile(join(output, 'published-pair.sha256'), Buffer.from(hash + '\n'));
  if (env.GITHUB_OUTPUT)
    await appendFile(
      env.GITHUB_OUTPUT,
      `published_sha256=${hash}\nrelease_id=${evidence.state.id}\npublished_json=${bytes.toString('utf8')}\n`,
    );
  if (env.GITHUB_STEP_SUMMARY)
    await appendFile(
      env.GITHUB_STEP_SUMMARY,
      `Published pair ${input.releaseVersion}\nRecord SHA256: ${hash}\nAction: ${pair.action.commit}\nPayload source: ${pair.payload.identity.sourceCommit}\n`,
    );
  return { publishedSha256: hash, releaseId: evidence.state.id };
}
export async function main(argv = process.argv.slice(2), env = process.env) {
  const tokens = capture(env);
  requireThat(
    argv.length === 7 &&
      ['preflight', 'workflow-promote'].includes(argv[0]) &&
      argv[1] === '--source' &&
      argv[3] === '--version' &&
      argv[5] === '--output',
    'promotion_arguments_invalid',
  );
  const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../..'),
    requestBytes = Buffer.from(env.PROMOTION_REQUEST ?? ''),
    r = parsePromotionRequest(requestBytes, env.R7_PUBLISH_REQUEST_SHA256),
    modeEnv = { ...env, R7_PUBLICATION_ENVIRONMENT: PUBLICATION_ENVIRONMENT },
    input = {
      repo,
      sourceCommit: argv[2],
      releaseVersion: argv[4],
      output: argv[6],
      promotionRequestBytes: requestBytes,
    };
  authority(input, modeEnv, r);
  fresh(timestamp(r.validUntil));
  await preflightPublication(r, promotionHttp(env.GITHUB_TOKEN, 'preflight').get);
  if (argv[0] === 'preflight') return { environmentAdmitted: true };
  isolate(tokens, env);
  const scratch = admitOutput(repo, input.output + '-inputs');
  await mkdir(scratch, { mode: 0o700 });
  const paths = await acquire(r.stageRequest, scratch, env);
  return promoteCandidate(
    { ...input, ...paths },
    { ...modeEnv, R7_PUBLISH_TOKEN: tokens.publisher, R7_PROTECTION_TOKEN: tokens.protection },
  );
}
if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main()
    .then((r) => process.stdout.write(canonicalJson(r)))
    .catch(() => {
      process.stderr.write('candidate_promotion_denied\n');
      process.exitCode = 1;
    });
}
