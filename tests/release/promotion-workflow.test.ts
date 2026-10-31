import { execFileSync } from 'node:child_process';
import { readFileSync, rmSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parse } from 'yaml';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { REPOSITORY, verifyCandidateWorkflow } from '../../scripts/release/candidate.mjs';
import { main, promoteCandidate } from '../../scripts/release/promote-candidate.mjs';
import { parsePromotionRequest } from '../../scripts/release/promote-candidate.format.mjs';
import {
  promotionHttp,
  verifyPublicRuntime,
} from '../../scripts/release/promote-candidate.http.mjs';
import { artifactTransport } from '../../scripts/release/stage-candidate.mjs';
import { promotionFixture } from './promotion.fixtures.mjs';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const p of roots.splice(0)) rmSync(p, { recursive: true, force: true });
});
async function fixture() {
  const x = await promotionFixture(vi);
  roots.push(x.f.root);
  return x;
}
const root = `/repos/${REPOSITORY}`;
async function cliFixture() {
  const x = await fixture();
  const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
  x.request.controlCommit = current;
  x.approved();
  const env = {
    ...x.env,
    GITHUB_WORKFLOW_SHA: current,
    GITHUB_SHA: current,
    PROMOTION_REQUEST: canonicalJson(x.request),
    R7_PUBLISH_TOKEN: undefined,
    R7_PROTECTION_TOKEN: undefined,
  };
  const args = [
    '--source',
    x.f.source,
    '--version',
    x.input.releaseVersion,
    '--output',
    join(x.f.root, 'preflight'),
  ];
  return { x, env, args };
}
describe('P5 actual CLI, fixed transport and workflow authority', () => {
  test('actual preflight reads existing publication environment with ordinary read authority', async () => {
    const { x, env, args } = await cliFixture();
    await expect(main(['preflight', ...args], env)).resolves.toEqual({ environmentAdmitted: true });
    expect(x.state.allCalls).toEqual([
      {
        url: 'https://api.github.com' + root + '/environments/r7-release',
        method: 'GET',
        auth: 'Bearer READ_ONLY_CANARY',
      },
    ]);
    expect(x.state.writes).toHaveLength(0);
  });
  test('actual protected CLI shares preflight then requires separate tokens before any SDK/download/write', async () => {
    const { x, env, args } = await cliFixture();
    await expect(main(['workflow-promote', ...args], env)).rejects.toThrow(
      'promotion_token_isolation_denied',
    );
    expect(x.state.allCalls).toHaveLength(1);
    expect(x.state.writes).toHaveLength(0);
  });
  test.each(['preflight', 'workflow-promote'])(
    'actual %s denies missing or replaced environment without writes',
    async (command) => {
      const { x, env, args } = await cliFixture();
      x.state.publicationEnvironment = null;
      await expect(main([command, ...args], { ...env })).rejects.toThrow('promotion_read_denied');
      x.state.publicationEnvironment = { id: '799', name: 'r7-release' };
      await expect(main([command, ...args], { ...env })).rejects.toThrow(
        'promotion_environment_denied',
      );
      expect(x.state.writes).toHaveLength(0);
    },
  );
  test('actual CLI rejects push/PR and unapproved request before transport', async () => {
    const { x, env, args } = await cliFixture();
    for (const event of ['push', 'pull_request'])
      await expect(
        main(['preflight', ...args], { ...env, GITHUB_EVENT_NAME: event }),
      ).rejects.toThrow('prepare_workflow_denied');
    await expect(
      main(['preflight', ...args], { ...env, R7_PUBLISH_REQUEST_SHA256: 'f'.repeat(64) }),
    ).rejects.toThrow('promotion_approval_denied');
    expect(x.state.allCalls).toHaveLength(0);
  });
  test('real artifact SDK child launch cannot inherit either captured privileged token', async () => {
    const x = await fixture(),
      log = join(x.f.root, 'sdk-env.json'),
      probe = join(x.f.root, 'sdk-probe.mjs');
    writeFileSync(
      probe,
      `import fs from 'node:fs';fs.writeFileSync(${JSON.stringify(log)},JSON.stringify({publisher:process.env.R7_PUBLISH_TOKEN,protection:process.env.R7_PROTECTION_TOKEN,read:process.env.GITHUB_TOKEN}));throw new Error('PRIVATE-CANARY');`,
    );
    const env = { ...x.env, NODE_OPTIONS: '--import=' + probe };
    // Probe exits before importing the SDK or attempting any network operation.
    const actual = artifactTransport(join(x.f.root, 'sdk-scratch'), env);
    const { mkdirSync } = await import('node:fs');
    mkdirSync(join(x.f.root, 'sdk-scratch'));
    vi.stubEnv('R7_PUBLISH_TOKEN', env.R7_PUBLISH_TOKEN);
    vi.stubEnv('R7_PROTECTION_TOKEN', env.R7_PROTECTION_TOKEN);
    await expect(
      promoteCandidate(x.input, env, {
        transport: { download: x.transport.download, upload: actual.upload },
      }),
    ).rejects.toThrow('promotion_intent_write_unknown');
    expect(JSON.parse(readFileSync(log, 'utf8'))).toEqual({ read: x.env.GITHUB_TOKEN });
    expect(x.state.writes).toHaveLength(0);
  });
  test('fixed protected jobs preserve ordinary read permissions, no auto-environment creation or CI secrets', () => {
    const w = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
    expect(verifyCandidateWorkflow(w)).toBe(true);
    const preflight = w.jobs['promote-preflight'],
      promote = w.jobs.promote;
    expect(preflight.environment).toBeUndefined();
    expect(promote.environment).toBe('r7-release');
    expect(promote.needs).toBe('promote-preflight');
    expect(promote.concurrency).toEqual(w.jobs.stage.concurrency);
    expect(promote.concurrency).toEqual({ group: 'r7-p4-staging', 'cancel-in-progress': false });
    for (const j of [preflight, promote]) {
      expect(j.permissions).toEqual({ contents: 'read', actions: 'read' });
      expect(j.if).toContain("github.event_name == 'workflow_dispatch'");
      expect(j.if).toContain("github.ref == 'refs/heads/main'");
      expect(j.if).toContain("inputs.operation == 'recover-promote'");
      expect(j.if).toContain("vars.R7_PUBLICATION_ENABLED == 'true'");
      expect(j.steps[0].with).toMatchObject({
        ref: '${{ github.workflow_sha }}',
        'fetch-depth': 0,
        'persist-credentials': false,
      });
      for (const s of j.steps.filter((s: any) => s.run)) expect(s.run).not.toContain('${{ inputs.');
    }
    expect(JSON.stringify(preflight)).not.toContain('secrets.');
    const secretSteps = promote.steps.filter((s: any) => JSON.stringify(s).includes('secrets.'));
    expect(secretSteps).toHaveLength(1);
    expect(secretSteps[0].env).toMatchObject({
      R7_PUBLISH_TOKEN: '${{ secrets.R7_PUBLISH_TOKEN }}',
      R7_PROTECTION_TOKEN: '${{ secrets.R7_PROTECTION_TOKEN }}',
    });
    for (const name of ['candidate-fixtures', 'candidate-smoke'])
      expect(JSON.stringify(w.jobs[name])).not.toContain('secrets.');
    for (const mutate of [
      (x: any) => delete x.jobs['promote-preflight'],
      (x: any) => (x.jobs.promote.permissions.contents = 'write'),
      (x: any) => (x.jobs.promote.environment = '${{ inputs.environment }}'),
      (x: any) => (x.jobs.promote.if = 'true'),
      (x: any) => (x.jobs.promote.concurrency['cancel-in-progress'] = true),
    ]) {
      const changed = structuredClone(w);
      mutate(changed);
      expect(() => verifyCandidateWorkflow(changed)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('observer and public transports cannot mutate settings, refs, releases or assets', async () => {
    const fetch = vi.fn();
    vi.stubGlobal('fetch', fetch);
    for (const http of [promotionHttp('OBSERVER', 'protection'), promotionHttp(null, 'public')]) {
      await expect(http.patch(root + '/releases/801', { draft: false })).rejects.toThrow(
        'promotion_route_denied',
      );
      await expect(http.post(root + '/git/refs', {})).rejects.toThrow('promotion_route_denied');
    }
    const pub = promotionHttp('PUBLISHER', 'publisher');
    await expect(pub.post(root + '/releases', {})).rejects.toThrow('promotion_route_denied');
    await expect(pub.patch(root + '/git/refs/tags/v1', {})).rejects.toThrow(
      'promotion_route_denied',
    );
    expect(fetch).not.toHaveBeenCalled();
  });
  test('public download and permitted storage redirect carry no Authorization', async () => {
    const calls: any[] = [],
      bytes = Buffer.from('original');
    vi.stubGlobal('fetch', async (url: any, o: any) => {
      calls.push({ url: String(url), o });
      return calls.length === 1
        ? new Response(null, {
            status: 302,
            headers: {
              location: 'https://release-assets.githubusercontent.com/exact?sig=synthetic',
            },
          })
        : new Response(bytes);
    });
    await expect(promotionHttp(null, 'public').assetBytes('123', bytes.length)).resolves.toEqual(
      bytes,
    );
    expect(calls.every((c) => !c.o.headers?.Authorization)).toBe(true);
    expect(calls[1].o.redirect).toBe('error');
    vi.stubGlobal(
      'fetch',
      async () =>
        new Response(null, { status: 302, headers: { location: 'https://evil.invalid/x' } }),
    );
    await expect(promotionHttp(null, 'public').assetBytes('123', 8)).rejects.toThrow(
      'promotion_redirect_denied',
    );
  });
  test('failed PATCH/POST are never replayed by the actual HTTP client', async () => {
    const fetch = vi.fn(async () => {
      throw new Error('PRIVATE-CANARY');
    });
    vi.stubGlobal('fetch', fetch);
    const h = promotionHttp('PUBLISHER', 'publisher');
    await expect(h.patch(root + '/releases/801', { draft: false })).rejects.toThrow(
      'promotion_transport_unknown',
    );
    await expect(h.post(root + '/git/refs', {})).rejects.toThrow('promotion_transport_unknown');
    expect(fetch).toHaveBeenCalledTimes(2);
  });
  test('pagination, oversized bytes and runtime URL/redirect boundaries fail closed', async () => {
    vi.stubGlobal(
      'fetch',
      async () => new Response(JSON.stringify({ artifacts: [{ id: 1 }], total_count: 2 })),
    );
    await expect(
      promotionHttp('PUBLISHER', 'publisher').list(
        root + '/actions/artifacts?name=fixed',
        'artifacts',
      ),
    ).rejects.toThrow('promotion_pagination_incomplete');
    vi.stubGlobal('fetch', async () => new Response(Buffer.from('oversized')));
    await expect(promotionHttp(null, 'public').assetBytes('123', 1)).rejects.toThrow(
      'promotion_response_invalid',
    );
    const x = await fixture(),
      policy = x.request.stageRequest.pair.action.runtime.policy;
    await expect(
      verifyPublicRuntime({ ...policy, url: 'https://evil.invalid/runtime' }),
    ).rejects.toThrow('promotion_runtime_policy_denied');
  });
  test('complete nested request with maximum approval/ID fields remains below dispatch bound', async () => {
    const x = await fixture();
    x.request.approvalId = 'A'.repeat(64);
    x.request.environmentId = '18446744073709551615';
    x.request.actionRulesetId = '18446744073709551615';
    x.approved();
    expect(x.input.promotionRequestBytes.length).toBeLessThan(49152);
    expect(
      parsePromotionRequest(x.input.promotionRequestBytes, x.env.R7_PUBLISH_REQUEST_SHA256),
    ).toEqual(x.request);
    expect(sha256(x.input.promotionRequestBytes)).toBe(x.env.R7_PUBLISH_REQUEST_SHA256);
  });
});
