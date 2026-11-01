import { createHash, randomUUID } from 'node:crypto';
import { chmodSync, readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { REPOSITORY, REPOSITORY_ID, WORKFLOW } from '../../scripts/release/candidate.mjs';
import { stageCandidate } from '../../scripts/release/stage-candidate.mjs';
import { GH_VERSION } from '../../scripts/release/verify-provenance.mjs';
import { stagingFixture } from './staging.fixtures.mjs';

// Entirely synthetic Git history, API, runtime bytes, attestation tool output.
// The production P3 gate and P4 staging are actually composed, never stubbed.
export async function promotionFixture(vi) {
  const runtime = Buffer.from('SYNTHETIC .NET runtime archive bytes; never executed');
  const policy = {
    version: '10.0.13',
    url: 'https://builds.dotnet.microsoft.com/dotnet/Runtime/10.0.13/dotnet-runtime-10.0.13-linux-x64.tar.gz',
    sha512: createHash('sha512').update(runtime).digest('hex'),
    archiveBytes: runtime.length,
  };
  const x = await stagingFixture(vi, { runtimePolicy: policy });
  await stageCandidate(x.input, { ...x.env }, { transport: x.transport });
  const stageFetch = globalThis.fetch;
  const staged = JSON.parse(readFileSync(join(x.input.output, 'staged-pair.json')));
  x.f.write('publication-control.txt', 'Synthetic P distinct from historical stage R\n');
  const control = x.f.commit('Synthetic publication control');
  x.f.main.object.sha = control;
  const request = {
    formatVersion: 1,
    kind: 'r7-promotion-request',
    channel: 'PUBLIC',
    stageRequest: x.request,
    stageRequestSha256: sha256(canonicalJson(x.request)),
    stagedRecord: staged,
    stagedRecordSha256: sha256(canonicalJson(staged)),
    approvalId: 'synthetic-publication-approval',
    validUntil: x.request.validUntil,
    controlCommit: control,
    environmentId: '702',
    actionRulesetId: '703',
    redistributionApprovalSha256: sha256(
      'SYNTHETIC human approval, no actual redistribution claim',
    ),
  };
  const env = {
    ...x.env,
    GITHUB_WORKFLOW_SHA: control,
    GITHUB_SHA: control,
    GITHUB_RUN_ID: '701',
    R7_STAGING_ENABLED: undefined,
    R7_STAGING_TOKEN: undefined,
    RELEASE_OPERATION: 'promote',
    R7_PUBLICATION_ENABLED: 'true',
    R7_PUBLICATION_ENVIRONMENT: 'r7-release',
    R7_PUBLISH_TOKEN: 'PUBLISH_SECRET_CANARY',
    R7_PROTECTION_TOKEN: 'PROTECTION_SECRET_CANARY',
    R7_PUBLISH_REQUEST_SHA256: sha256(canonicalJson(request)),
  };
  const input = {
    ...x.input,
    output: join(x.f.root, 'published'),
    promotionRequestBytes: Buffer.from(canonicalJson(request)),
  };
  const state = x.state;
  state.writes = [];
  state.reads = [];
  Object.assign(state.repository, { private: false, visibility: 'public' });
  Object.assign(state, {
    immutable: { enabled: true, enforced_by_owner: false },
    publicationEnvironment: { id: '702', name: 'r7-release' },
    ruleset: {
      id: '703',
      source_type: 'Repository',
      source: REPOSITORY,
      target: 'tag',
      enforcement: 'active',
      bypass_actors: [],
      conditions: { ref_name: { include: ['refs/tags/' + input.releaseVersion], exclude: [] } },
      rules: [{ type: 'update' }, { type: 'deletion' }],
    },
    promotionRun: { ...state.stageRun, id: '701', head_sha: control, path: WORKFLOW },
    publicReads: [],
    allCalls: [],
    failOperation: null,
    failKind: 'lost',
    publicFault: null,
    runtimeFault: null,
    hook: null,
    intentUploadFail: false,
  });
  const root = `/repos/${REPOSITORY}`,
    json = (b, status = 200) => new Response(JSON.stringify(b), { status });
  const ghLog = join(x.f.root, 'gh-log.jsonl'),
    ghFail = join(x.f.root, 'gh-fail');
  writeFileSync(
    join(x.f.bin, 'gh'),
    `#!${process.execPath}
import fs from 'node:fs';
const args=process.argv.slice(2);
fs.appendFileSync(${JSON.stringify(ghLog)},JSON.stringify({args,publisher:process.env.R7_PUBLISH_TOKEN,protection:process.env.R7_PROTECTION_TOKEN})+'\\n');
if(args[0]==='--version')console.log('gh version ${GH_VERSION} (synthetic)');
else if(args[0]==='release' && fs.existsSync(${JSON.stringify(ghFail)})){console.error('PRIVATE-CANARY');process.exit(1);}
else process.stdout.write(fs.readFileSync(${JSON.stringify(join(x.f.root, 'gh-result.json'))}));
`,
  );
  chmodSync(join(x.f.bin, 'gh'), 0o700);
  async function fetch(url, options = {}) {
    const u = new URL(url),
      path = u.pathname.slice(root.length),
      auth = options.headers?.Authorization,
      method = options.method ?? 'GET';
    state.allCalls.push({ url: String(url), method, auth });
    if (state.hook) state.hook({ path, auth, method, state });
    if (String(url) === policy.url) {
      if (auth) throw new Error('runtime_authority_leak');
      state.runtimeRead = true;
      return state.runtimeFault === 'unavailable'
        ? json({}, 503)
        : new Response(state.runtimeFault === 'bytes' ? Buffer.from('wrong runtime') : runtime);
    }
    if (path === '/immutable-releases' || path.startsWith('/rulesets/')) {
      if (auth !== 'Bearer PROTECTION_SECRET_CANARY') throw new Error('wrong_observer');
      return json(path === '/immutable-releases' ? state.immutable : state.ruleset);
    }
    if (path === '/environments/r7-release')
      return json(state.publicationEnvironment, state.publicationEnvironment ? 200 : 404);
    if (path === '/actions/runs/701/attempts/1') return json(state.promotionRun);
    if (path === '/actions/artifacts' && u.searchParams.has('name')) {
      const artifacts = state.intents.filter((a) => a.name === u.searchParams.get('name'));
      return json({ total_count: artifacts.length, artifacts });
    }
    if (method === 'PATCH' || (method === 'POST' && path === '/git/refs')) {
      if (auth !== 'Bearer PUBLISH_SECRET_CANARY') throw new Error('wrong_publisher');
      const operation = method === 'PATCH' ? 'publish' : 'tag';
      state.writes.push({ method, path, body: JSON.parse(options.body) });
      if (state.failOperation === operation && state.failKind === 'absent')
        throw new Error('lost_before_commit');
      let result;
      if (operation === 'publish') {
        if (path !== '/releases/801') throw new Error('wrong_release');
        result = Object.assign(state.releases[0], JSON.parse(options.body), {
          published_at: new Date(x.f.now).toISOString(),
          immutable: true,
        });
        state.payloadTag = {
          ref: 'refs/tags/payload-' + input.releaseVersion,
          object: { type: 'commit', sha: x.f.source },
        };
      } else {
        if (
          state.publicReads.length < 14 ||
          !state.runtimeRead ||
          !readFileSync(ghLog, 'utf8').includes('verify-asset')
        )
          throw new Error('tag_before_public_proof');
        const body = JSON.parse(options.body);
        result = state.consumer = { ref: body.ref, object: { type: 'commit', sha: body.sha } };
      }
      if (state.failOperation === operation) {
        if (state.failKind === 'wrong-response')
          return json(
            { ...result, id: '9999', object: { type: 'commit', sha: 'f'.repeat(40) } },
            method === 'PATCH' ? 200 : 201,
          );
        if (state.failKind === 'malformed')
          return new Response('invalid-json', { status: method === 'PATCH' ? 200 : 201 });
        throw new Error('lost_after_commit');
      }
      return json(result, method === 'PATCH' ? 200 : 201);
    }
    if (path.startsWith('/releases')) {
      const publicRead = auth === undefined;
      if (!publicRead && auth !== 'Bearer PUBLISH_SECRET_CANARY')
        throw new Error('wrong_draft_visibility');
      if (publicRead && (state.releases[0].draft || state.publicFault === 'unavailable'))
        return json({}, 404);
      const release = { ...state.releases[0] };
      if (publicRead && state.publicFault === 'release') release.immutable = false;
      if (path === '/releases') return json([release]);
      if (path === '/releases/801') return json(release);
      if (path === '/releases/801/assets') {
        const assets = [...state.assets.values()].map(({ bytes, ...a }) => a);
        if (publicRead && state.publicFault === 'asset-id') assets[0].id = '9999';
        if (publicRead && state.publicFault === 'extra') assets.push({ ...assets[0], id: '9999' });
        return json(assets);
      }
      if (path.startsWith('/releases/assets/')) {
        const a = state.assets.get(path.split('/').at(-1));
        if (publicRead) state.publicReads.push(a.name);
        return a
          ? new Response(
              publicRead && state.publicFault === a.name ? Buffer.alloc(a.size, 88) : a.bytes,
            )
          : json({}, 404);
      }
      throw new Error('unknown_release_route');
    }
    if (path === '' || path === '/') return json(state.repository);
    // Delegate original qualification/run/ref reads to the composed P4/P3 fixture.
    return stageFetch(url, options);
  }
  vi.stubGlobal('fetch', fetch);
  const transport = {
    async upload(name, bytes) {
      state.writes.push({ method: 'INTENT', path: name });
      const a = {
        id: '602',
        name,
        expired: false,
        digest: 'sha256:' + sha256(bytes),
        size_in_bytes: bytes.length,
        created_at: new Date(x.f.now - 1000).toISOString(),
        expires_at: new Date(x.f.now + 86400000).toISOString(),
        workflow_run: {
          id: '701',
          repository_id: REPOSITORY_ID,
          head_repository_id: REPOSITORY_ID,
          head_branch: 'main',
          head_sha: control,
        },
      };
      state.intents.push(a);
      state.intentBytes.set(a.id, Buffer.from(bytes));
      if (state.intentUploadFail) throw new Error('lost_intent_response');
      return a.id;
    },
    download: x.transport.download,
  };
  function approved() {
    input.promotionRequestBytes = Buffer.from(canonicalJson(request));
    env.R7_PUBLISH_REQUEST_SHA256 = sha256(input.promotionRequestBytes);
  }
  return {
    ...x,
    request,
    staged,
    control,
    env,
    input,
    state,
    transport,
    approved,
    runtime,
    ghLog,
    ghFail,
    again: () => ({ ...input, output: join(x.f.root, 'published-' + randomUUID()) }),
  };
}
