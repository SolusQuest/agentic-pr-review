import { readFileSync, rmSync, mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parse } from 'yaml';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { createRawFileUploadStream } from '../../node_modules/@actions/artifact/lib/internal/upload/stream.js';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { verifyCandidateWorkflow } from '../../scripts/release/candidate.mjs';
import { stageHttp } from '../../scripts/release/stage-candidate.http.mjs';
const root = '/repos/SolusQuest/agentic-pr-review';
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});
describe('P4 workflow/transport authority and actual locked SDK codec', () => {
  test('isolates main/manual stage authority, prechecks existing environment and keeps tests credential-free', () => {
    const w = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
    expect(verifyCandidateWorkflow(w)).toBe(true);
    const p = w.jobs['stage-preflight'],
      s = w.jobs.stage;
    expect(p.environment).toBeUndefined();
    expect(s.needs).toBe('stage-preflight');
    expect(s.environment).toBe('r7-staging');
    expect(s.concurrency).toEqual({ group: 'r7-p4-staging', 'cancel-in-progress': false });
    for (const job of [p, s]) {
      expect(job.if).toContain("github.event_name == 'workflow_dispatch'");
      expect(job.if).toContain("github.ref == 'refs/heads/main'");
      expect(job.if).toContain("vars.R7_STAGING_ENABLED == 'true'");
      expect(job.if).toContain("inputs.operation == 'recover-stage'");
      expect(job.permissions).toEqual({ contents: 'read', actions: 'read' });
      expect(job['runs-on']).toBe('ubuntu-24.04');
      expect(job.steps[0].with).toMatchObject({
        ref: '${{ github.workflow_sha }}',
        'persist-credentials': false,
        'fetch-depth': 0,
      });
      for (const step of job.steps.filter((x: any) => x.run))
        expect(step.run).not.toContain('${{ inputs.');
    }
    expect(JSON.stringify(p)).not.toMatch(/secrets\.|id-token|attestations/);
    expect(JSON.stringify(s)).not.toMatch(
      /actions\/attest|id-token|packages:|setup-dotnet|release create|tag push/,
    );
    const secretSteps = s.steps.filter((x: any) => JSON.stringify(x).includes('secrets.'));
    expect(secretSteps).toHaveLength(1);
    expect(secretSteps[0].env.R7_STAGING_TOKEN).toBe('${{ secrets.R7_STAGING_TOKEN }}');
    for (const name of ['candidate-fixtures', 'candidate-smoke']) {
      expect(w.jobs[name].if).toBe("github.event_name != 'workflow_dispatch'");
      expect(JSON.stringify(w.jobs[name])).not.toContain('secrets.');
    }
    for (const mutate of [
      (x: any) => {
        delete x.jobs['stage-preflight'];
      },
      (x: any) => {
        x.jobs.stage.permissions.contents = 'write';
      },
      (x: any) => {
        x.jobs.stage.environment = '${{ inputs.environment }}';
      },
      (x: any) => {
        x.jobs.stage.if = 'true';
      },
      (x: any) => {
        x.jobs.stage.concurrency['cancel-in-progress'] = true;
      },
    ]) {
      const changed = structuredClone(w);
      mutate(changed);
      expect(() => verifyCandidateWorkflow(changed)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('locked SDK raw codec preserves exact canonical intent bytes and digest', async () => {
    const directory = mkdtempSync(join(tmpdir(), 'apr-stage-codec-'));
    try {
      const bytes = Buffer.from(
        canonicalJson({ kind: 'synthetic-intent', operations: [{ id: 'create' }] }),
      );
      const path = join(directory, 'intent.json');
      writeFileSync(path, bytes);
      const stream = await createRawFileUploadStream(path),
        chunks = [];
      for await (const chunk of stream) chunks.push(Buffer.from(chunk));
      const actual = Buffer.concat(chunks);
      expect(actual).toEqual(bytes);
      expect(sha256(actual)).toBe(sha256(bytes));
      const adapter = readFileSync('scripts/release/stage-candidate.sdk.mjs', 'utf8');
      expect(adapter).toContain('skipArchive: true');
      expect(adapter).toContain("expectedHash: 'sha256:' + c.sha256");
      expect(adapter).toContain('r.digestMismatch !== false');
    } finally {
      rmSync(directory, { force: true, recursive: true });
    }
  });
  test('asset redirect uses only fixed GitHub hosts and strips write authority', async () => {
    const calls: any[] = [],
      bytes = Buffer.from('original');
    vi.stubGlobal('fetch', async (url: any, o: any) => {
      calls.push({ url: String(url), options: o });
      return calls.length === 1
        ? new Response(null, {
            status: 302,
            headers: {
              location: 'https://release-assets.githubusercontent.com/file?sig=synthetic',
            },
          })
        : new Response(bytes);
    });
    const http = stageHttp('PRIVATE_CANARY');
    await expect(http.assetHash('123', bytes.length)).resolves.toBe(sha256(bytes));
    expect(calls[0].options.headers.Authorization).toBe('Bearer PRIVATE_CANARY');
    expect(calls[1].options.headers).toBeUndefined();
    expect(calls[1].options.redirect).toBe('error');
    vi.stubGlobal(
      'fetch',
      async () =>
        new Response(null, {
          status: 302,
          headers: { location: 'https://evil.invalid/PRIVATE_CANARY' },
        }),
    );
    await expect(http.assetHash('123', 8)).rejects.toThrow('stage_asset_redirect_denied');
  });
  test('bounded pagination refuses incomplete or excessive responses and never retries a POST', async () => {
    const mock = vi.fn(
      async () => new Response(JSON.stringify({ total_count: 2, artifacts: [{ id: 1 }] })),
    );
    vi.stubGlobal('fetch', mock);
    const h = stageHttp('PRIVATE_CANARY');
    await expect(h.list(root + '/actions/artifacts?name=fixed', 'artifacts')).rejects.toThrow(
      'stage_pagination_incomplete',
    );
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify(Array(100).fill({ id: 1 })))),
    );
    await expect(h.list(root + '/releases')).rejects.toThrow('stage_pagination_limit');
    const post = vi.fn(async () => {
      throw new Error('PRIVATE_CANARY');
    });
    vi.stubGlobal('fetch', post);
    await expect(h.post(root + '/releases', {})).rejects.toThrow('stage_transport_unknown');
    expect(post).toHaveBeenCalledTimes(1);
  });
});
