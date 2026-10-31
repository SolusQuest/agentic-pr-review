import { mkdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { REPOSITORY, REPOSITORY_ID, WORKFLOW } from '../../scripts/release/candidate.mjs';
import { finalizeCandidate } from '../../scripts/release/finalize-candidate.mjs';
import { finalizationFixture } from './finalization.fixtures.mjs';

// Real P3 composed verifier, synthetic Git history/gh results/API only.
export async function stagingFixture(vi) {
  const f = finalizationFixture();
  vi.stubGlobal('fetch', f.fetch);
  vi.stubEnv('PATH', f.bin + ':' + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  const finalized = await finalizeCandidate(f.input, {});
  const pairPath = join(f.input.output, 'finalized-pair.json'),
    pair = JSON.parse(readFileSync(pairPath));
  const q = f.qualificationFiles(readFileSync(pairPath), { executionKind: 'original-candidate' });
  const request = {
    formatVersion: 1,
    kind: 'r7-stage-request',
    pair,
    pairSha256: finalized.pairSha256,
    finalization: f.request,
    qualifierCommit: f.qualifier,
    qualificationId: f.qualification.artifactId,
    qualificationSha256: q.archiveSha256,
    receiptSha256: q.receiptSha256,
    approvalId: 'synthetic-approval',
    validUntil: new Date(f.now + 3600000).toISOString(),
    controlCommit: f.qualifier,
    environmentId: '701',
    protectionEvidenceSha256: sha256('synthetic maintainer preflight'),
  };
  const temp = join(f.root, 'runner');
  mkdirSync(temp);
  const env = {
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: REPOSITORY,
    GITHUB_REPOSITORY_ID: REPOSITORY_ID,
    GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: REPOSITORY + '/' + WORKFLOW + '@refs/heads/main',
    GITHUB_WORKFLOW_SHA: f.qualifier,
    GITHUB_SHA: f.qualifier,
    GITHUB_RUN_ID: '501',
    GITHUB_RUN_ATTEMPT: '1',
    RUNNER_ENVIRONMENT: 'github-hosted',
    RUNNER_OS: 'Linux',
    RUNNER_ARCH: 'X64',
    RUNNER_TEMP: temp,
    RELEASE_OPERATION: 'stage',
    R7_STAGING_ENABLED: 'true',
    R7_STAGING_ENVIRONMENT: 'r7-staging',
    R7_STAGING_TOKEN: 'WRITE_SECRET_CANARY',
    GITHUB_TOKEN: 'READ_ONLY_CANARY',
    R7_STAGE_REQUEST_SHA256: sha256(canonicalJson(request)),
  };
  const input = {
    ...f.input,
    pairPath,
    qualificationArchivePath: q.archivePath,
    stageRequestBytes: Buffer.from(canonicalJson(request)),
    output: join(f.root, 'staged'),
  };
  const repository = { id: REPOSITORY_ID, full_name: REPOSITORY, permissions: { push: true } };
  const stageRun = {
    ...f.qRun,
    id: '501',
    path: WORKFLOW,
    head_sha: f.qualifier,
    status: 'in_progress',
    conclusion: null,
  };
  const state = {
    releases: [],
    assets: new Map(),
    intents: [],
    intentBytes: new Map(),
    writes: [],
    reads: [],
    faultAt: -1,
    fault: 'lost',
    visibility: true,
    postReadHook: null,
    fenceUploadFail: false,
    consumer: null,
    payloadTag: null,
    environment: { id: '701', name: 'r7-staging' },
    stageRun,
    repository,
  };
  const base = `https://api.github.com/repos/${REPOSITORY}`;
  const json = (body, status = 200) => new Response(JSON.stringify(body), { status });
  async function fetch(url, options) {
    const u = new URL(url),
      path = u.pathname.slice(`/repos/${REPOSITORY}`.length),
      auth = options.headers.Authorization;
    const method = options.method ?? 'GET';
    if (method === 'POST') {
      if (auth !== 'Bearer WRITE_SECRET_CANARY') throw new Error('wrong_write_credential');
      const index = state.writes.length;
      state.writes.push({ path, method });
      if (state.faultAt === index && state.fault === 'absent')
        throw new Error('lost_before_commit');
      let result;
      if (path === '/releases') {
        const r = JSON.parse(options.body);
        result = { ...r, id: '801', published_at: null };
        state.releases.push(result);
      } else {
        if (u.hostname !== 'uploads.github.com' || !/^\/releases\/801\/assets$/.test(path))
          throw new Error('wrong_upload_endpoint');
        const bytes = Buffer.from(options.body),
          name = u.searchParams.get('name'),
          id = String(900 + state.assets.size);
        result = {
          id,
          name,
          state: 'uploaded',
          content_type: 'application/octet-stream',
          size: bytes.length,
          digest: 'sha256:' + sha256(bytes),
          url: base + '/releases/assets/' + id,
        };
        state.assets.set(id, { ...result, bytes });
      }
      if (state.faultAt === index) {
        if (state.fault === 'starter' && path !== '/releases') {
          const a = state.assets.get(result.id);
          a.state = 'starter';
          a.size = 0;
        }
        if (state.fault === 'wrong-response-id') return json({ ...result, id: '1999' }, 201);
        if (state.fault === 'malformed') return new Response('not-json', { status: 201 });
        throw new Error('lost_after_commit');
      }
      return json(result, 201);
    }
    state.reads.push({ path, auth });
    if (path === '/' || path === '') return json(repository);
    if (path === '/environments/r7-staging')
      return json(state.environment, state.environment ? 200 : 404);
    if (path === '/git/ref/tags/' + input.releaseVersion)
      return state.consumer ? json(state.consumer) : json({}, 404);
    if (path === '/git/ref/tags/payload-' + input.releaseVersion)
      return state.payloadTag ? json(state.payloadTag) : json({}, 404);
    if (path === '/actions/runs/501/attempts/1') return json(state.stageRun);
    if (path === '/actions/artifacts' && u.searchParams.has('name'))
      return json({ total_count: state.intents.length, artifacts: state.intents });
    if (path.startsWith('/releases')) {
      if (auth !== 'Bearer WRITE_SECRET_CANARY') throw new Error('draft_read_wrong_credential');
      if (state.postReadHook) state.postReadHook(state);
      if (path === '/releases') return json(state.visibility ? state.releases : []);
      if (path === '/releases/801')
        return state.releases.length ? json(state.releases[0]) : json({}, 404);
      if (path === '/releases/801/assets')
        return json([...state.assets.values()].map(({ bytes, ...a }) => a));
      if (path.startsWith('/releases/assets/')) {
        const a = state.assets.get(path.split('/').at(-1));
        return a ? new Response(a.bytes) : json({}, 404);
      }
      throw new Error('unknown_release_endpoint');
    }
    // P3's fixture requires redirect:error; stage's fixed REST adapter uses
    // manual redirects so it can separately admit binary 302 responses.
    return f.fetch(url, { ...options, redirect: 'error' });
  }
  vi.stubGlobal('fetch', fetch);
  const transport = {
    async upload(name, bytes) {
      state.writes.push({ method: 'INTENT', path: name });
      const a = {
        id: '601',
        name,
        expired: false,
        digest: 'sha256:' + sha256(bytes),
        size_in_bytes: bytes.length,
        created_at: new Date(f.now - 1000).toISOString(),
        expires_at: new Date(f.now + 86400000).toISOString(),
        workflow_run: {
          id: '501',
          repository_id: REPOSITORY_ID,
          head_repository_id: REPOSITORY_ID,
          head_branch: 'main',
          head_sha: f.qualifier,
        },
      };
      state.intents.push(a);
      state.intentBytes.set(a.id, Buffer.from(bytes));
      if (state.fenceUploadFail) throw new Error('lost_intent_response');
      return a.id;
    },
    async download(a) {
      return { bytes: state.intentBytes.get(a.id) };
    },
  };
  function again() {
    return { ...input, output: join(f.root, 'staged-' + crypto.randomUUID()) };
  }
  function approved(r = request) {
    input.stageRequestBytes = Buffer.from(canonicalJson(r));
    env.R7_STAGE_REQUEST_SHA256 = sha256(input.stageRequestBytes);
  }
  return { f, env, input, request, q, state, transport, again, approved };
}
