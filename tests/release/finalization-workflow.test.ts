import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { parse } from 'yaml';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { DefaultArtifactClient } from '@actions/artifact';
import { finalizationFixture } from './finalization.fixtures.mjs';
import {
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  verifyCandidateWorkflow,
} from '../../scripts/release/candidate.mjs';
import { main } from '../../scripts/release/finalize-candidate.mjs';
import { canonicalJson } from '../../scripts/release/build-payload.format.mjs';
const workflow = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});
function admissionFixture() {
  const f = finalizationFixture();
  roots.push(f.root);
  vi.stubGlobal('fetch', f.fetch);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
  const env: any = {
    GITHUB_EVENT_NAME: 'workflow_dispatch',
    GITHUB_REPOSITORY: REPOSITORY,
    GITHUB_REPOSITORY_ID: REPOSITORY_ID,
    GITHUB_REF: 'refs/heads/main',
    GITHUB_WORKFLOW_REF: REPOSITORY + '/' + WORKFLOW + '@refs/heads/main',
    GITHUB_WORKFLOW_SHA: current,
    GITHUB_SHA: current,
    GITHUB_RUN_ID: '501',
    GITHUB_RUN_ATTEMPT: '1',
    RUNNER_ENVIRONMENT: 'github-hosted',
    RUNNER_OS: 'Linux',
    RUNNER_ARCH: 'X64',
    RELEASE_OPERATION: 'finalize',
    FINALIZE_REQUEST: canonicalJson(f.request),
    GITHUB_OUTPUT: join(f.root, 'admitted-outputs'),
    GITHUB_TOKEN: 'synthetic-read-only',
  };
  const output = join(f.root, 'admitted-request');
  const args = [
    'admit-request',
    '--source',
    f.source,
    '--version',
    f.input.releaseVersion,
    '--output',
    output,
  ];
  return { f, env, args, output };
}
describe('P3 main/manual read-only finalization authority', () => {
  test('isolates finalize from every preparation/signing job and has no remote writes', () => {
    const f = workflow.jobs.finalize;
    expect(workflow.on.workflow_dispatch.inputs.operation).toMatchObject({
      type: 'choice',
      default: 'prepare',
      options: ['prepare', 'finalize', 'stage', 'recover-stage'],
    });
    for (const name of ['prepare-build', 'prepare-record', 'prepare-sign'])
      expect(workflow.jobs[name].if).toContain("inputs.operation == 'prepare'");
    expect(f.if).toBe(
      "github.event_name == 'workflow_dispatch' && github.repository == 'SolusQuest/agentic-pr-review' && github.ref == 'refs/heads/main' && inputs.operation == 'finalize'",
    );
    expect(f.permissions).toEqual({ contents: 'read', actions: 'read' });
    expect(f.environment).toBeUndefined();
    expect(f['runs-on']).toBe('ubuntu-24.04');
    const checkout = f.steps.find((s: any) => s.uses === 'actions/checkout@v6');
    expect(checkout.with).toEqual({
      ref: '${{ github.workflow_sha }}',
      path: 'control',
      'fetch-depth': 0,
      'persist-credentials': false,
    });
    const request = f.steps.findIndex((s: any) => s.id === 'request');
    const downloads = f.steps.filter((s: any) => s.uses === 'actions/download-artifact@v8');
    expect(downloads).toHaveLength(3);
    expect(downloads.map((s: any) => s.with['artifact-ids'])).toEqual([
      '${{ steps.request.outputs.payload_id }}',
      '${{ steps.request.outputs.metadata_id }}',
      '${{ steps.request.outputs.provenance_id }}',
    ]);
    expect(
      downloads.every(
        (s: any) => s.with['run-id'] === '${{ steps.request.outputs.producer_run_id }}',
      ),
    ).toBe(true);
    expect(f.steps[request].env.GITHUB_TOKEN).toBe('${{ github.token }}');
    expect(f.steps.indexOf(downloads[0])).toBeGreaterThan(request);
    expect(JSON.stringify(f)).not.toMatch(
      /upload-artifact|actions\/attest|id-token|attestations|packages|dotnet|secrets\.|release create/,
    );
    expect(f.outputs).toEqual({
      'pair-sha256': '${{ steps.finalize.outputs.pair_sha256 }}',
      'pair-json': '${{ steps.finalize.outputs.pair_json }}',
    });
    for (const step of f.steps) if (step.run) expect(step.run).not.toContain('${{');
    expect(f.steps.find((s: any) => s.id === 'finalize').run).toContain(' workflow-finalize ');
    expect(verifyCandidateWorkflow(workflow)).toBe(true);
  });
  test('whole topology rejects mode collision, write authority, substituted original evidence and missing admission', () => {
    const mutations = [
      (w: any) => {
        w.jobs['prepare-sign'].if = w.jobs.finalize.if;
      },
      (w: any) => {
        delete w.jobs.finalize.if;
      },
      (w: any) => {
        w.jobs.finalize.permissions['id-token'] = 'write';
      },
      (w: any) => {
        w.jobs.finalize.permissions.contents = 'write';
      },
      (w: any) => {
        w.jobs.finalize.steps[0].with.ref = '${{ inputs.source-commit }}';
      },
      (w: any) => {
        w.jobs.finalize.steps.find((s: any) => s.id === 'request').run = 'true';
      },
      (w: any) => {
        w.jobs.finalize.steps.find((s: any) => s.uses === 'actions/download-artifact@v8').with[
          'artifact-ids'
        ] = 'latest';
      },
      (w: any) => {
        delete w.jobs.finalize.steps.find((s: any) => s.uses === 'actions/download-artifact@v8')
          .with['run-id'];
      },
      (w: any) => {
        w.jobs.finalize.steps.filter((s: any) => s.uses === 'actions/download-artifact@v8')[1].with[
          'run-id'
        ] = '${{ github.run_id }}';
      },
      (w: any) => {
        w.jobs.finalize.steps.filter((s: any) => s.uses === 'actions/download-artifact@v8')[2].with[
          'run-id'
        ] = '${{ inputs.finalize-request }}';
      },
      (w: any) => {
        w.jobs.finalize.steps.push({ uses: 'actions/upload-artifact@v7', with: { path: '**' } });
      },
      (w: any) => {
        w.jobs.finalize.steps.find((s: any) => s.id === 'finalize').run = 'echo qualified';
      },
    ];
    for (const mutate of mutations) {
      const w = structuredClone(workflow);
      mutate(w);
      expect(() => verifyCandidateWorkflow(w)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('admits only bounded canonical requests under the actual current workflow context', async () => {
    const x = admissionFixture();
    await expect(main(x.args, { ...x.env, RELEASE_OPERATION: 'prepare' })).rejects.toThrow(
      'finalization_workflow_denied',
    );
    await expect(main(x.args, { ...x.env, GITHUB_EVENT_NAME: 'pull_request' })).rejects.toThrow(
      'prepare_workflow_denied',
    );
    await expect(
      main(x.args, { ...x.env, FINALIZE_REQUEST: '{"apiUrl":"https://evil.invalid"}' }),
    ).rejects.toThrow();
    await expect(main(x.args, x.env)).resolves.toEqual({ requestAdmitted: true });
    expect(readFileSync(x.env.GITHUB_OUTPUT, 'utf8')).toContain(
      'provenance_id=' + x.f.provenance.artifactId + '\n',
    );
    expect(readFileSync(x.output, 'utf8')).toBe(canonicalJson(x.f.request));
  });
  test('downloads original preparation artifacts in a separate finalization run', async () => {
    const x = admissionFixture();
    await main(x.args, x.env);
    const outputs = Object.fromEntries(
      readFileSync(x.env.GITHUB_OUTPUT, 'utf8')
        .trim()
        .split('\n')
        .map((line) => line.split('=')),
    );
    const github = await import(
      /* @vite-ignore */ new URL('../../node_modules/@actions/github/lib/utils.js', import.meta.url)
        .href
    );
    const previousFetch = github.defaults.request.fetch;
    const selectedRuns: string[] = [];
    github.defaults.request.fetch = async (url: string) => {
      const run = /\/actions\/runs\/([0-9]+)\/artifacts/.exec(String(url))?.[1];
      if (!run) throw new Error('unexpected SDK route');
      selectedRuns.push(run);
      const artifacts =
        run === x.f.producer.runId
          ? x.f.artifacts.map((a: any) => ({ ...a, id: Number(a.id) }))
          : [];
      return new Response(JSON.stringify({ total_count: artifacts.length, artifacts }), {
        headers: { 'content-type': 'application/json' },
      });
    };
    try {
      const sdk = new DefaultArtifactClient();
      for (const download of workflow.jobs.finalize.steps.filter(
        (s: any) => s.uses === 'actions/download-artifact@v8',
      )) {
        const expression = download.with['run-id'];
        const key = /steps.request.outputs.([a-z_]+)/.exec(expression ?? '')?.[1];
        // v8 action.yml's documented default; real SDK public listing supplies
        // the same run-scoped candidates v8 filters by artifact-ids.
        const runId = key ? outputs[key] : x.env.GITHUB_RUN_ID;
        const { artifacts } = await sdk.listArtifacts({
          latest: true,
          findBy: {
            token: 'synthetic',
            repositoryOwner: 'SolusQuest',
            repositoryName: 'agentic-pr-review',
            workflowRunId: Number(runId),
          },
        });
        const idKey = /steps.request.outputs.([a-z_]+)/.exec(download.with['artifact-ids'])![1]!;
        expect(artifacts.some((a) => a.id === Number(outputs[idKey]))).toBe(true);
      }
      expect(selectedRuns).toEqual(Array(3).fill(x.f.producer.runId));
      expect(selectedRuns).not.toContain(x.env.GITHUB_RUN_ID);
    } finally {
      github.defaults.request.fetch = previousFetch;
    }
  });
  test.each([
    'payload-run',
    'payload-attempt',
    'metadata-run',
    'provenance-attempt',
    'foreign-repo',
    'foreign-head-repo',
    'wrong-W',
    'wrong-ref',
    'wrong-workflow',
    'pr-run',
    'failed-run',
    'in-progress',
    'expired',
    'digest',
    'duplicate',
    'missing',
    'unsafe-ID',
    'unsafe-run',
    'role-alias',
  ])('does not emit admitted downloads for untrusted original producer: %s', async (kind) => {
    const x = admissionFixture(),
      a = x.f.artifacts;
    if (kind === 'payload-run') a[0].workflow_run.id = '102';
    if (kind === 'payload-attempt') a[0].name = a[0].name.replace(/-[0-9]+$/, '-9');
    if (kind === 'metadata-run') a[1].workflow_run.id = '102';
    if (kind === 'provenance-attempt') a[2].name = a[2].name.replace(/-[0-9]+$/, '-9');
    if (kind === 'foreign-repo') a[0].workflow_run.repository_id = '999';
    if (kind === 'foreign-head-repo') a[1].workflow_run.head_repository_id = '999';
    if (kind === 'wrong-W') a[0].workflow_run.head_sha = 'e'.repeat(40);
    if (kind === 'wrong-ref') x.f.releaseRun.head_branch = 'feature';
    if (kind === 'wrong-workflow') x.f.releaseRun.path = '.github/workflows/other.yml';
    if (kind === 'pr-run') x.f.releaseRun.event = 'pull_request';
    if (kind === 'failed-run') x.f.releaseRun.conclusion = 'failure';
    if (kind === 'in-progress') {
      x.f.releaseRun.status = 'in_progress';
      x.f.releaseRun.conclusion = null;
    }
    if (kind === 'expired') a[1].expired = true;
    if (kind === 'digest') a[2].digest = 'sha256:' + 'f'.repeat(64);
    if (kind === 'duplicate') a.push({ ...a[2], id: '204' });
    if (kind === 'missing') x.f.missing.add('/actions/artifacts/' + x.f.metadata.artifactId);
    if (kind === 'unsafe-ID') x.f.request.payloadId = '9007199254740993';
    if (kind === 'unsafe-run') {
      a[0].workflow_run.id = '9007199254740993';
      a[0].name = 'r7-p1-payload-9007199254740993-2';
    }
    if (kind === 'role-alias') x.f.request.provenanceId = x.f.request.metadataId;
    x.env.FINALIZE_REQUEST = canonicalJson(x.f.request);
    await expect(main(x.args, x.env)).rejects.toThrow();
    expect(existsSync(x.output)).toBe(false);
    expect(existsSync(x.env.GITHUB_OUTPUT)).toBe(false);
    expect(x.f.calls.every((c: any) => c.method === 'GET')).toBe(true);
  });
  test('keeps only exact byte-stable contract files outside formatter rewriting', () => {
    const ignored = readFileSync('.prettierignore', 'utf8').split('\n');
    expect(ignored).toContain('.github/actions/agentic-pr-review/payload-map.json');
    expect(ignored).toContain('scripts/release/qualification-checks.json');
    expect(ignored).not.toContain('scripts/release/');
    expect(readFileSync('.github/actions/agentic-pr-review/payload-map.json', 'utf8')).toBe(
      'null\n',
    );
  });
});
