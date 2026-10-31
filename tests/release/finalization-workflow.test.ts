import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';
import { tmpdir } from 'node:os';
import { parse } from 'yaml';
import { describe, expect, test } from 'vitest';
import {
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  verifyCandidateWorkflow,
} from '../../scripts/release/candidate.mjs';
import { main } from '../../scripts/release/finalize-candidate.mjs';
import { canonicalJson } from '../../scripts/release/build-payload.format.mjs';
const workflow = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
describe('P3 main/manual read-only finalization authority', () => {
  test('isolates finalize from every preparation/signing job and has no remote writes', () => {
    const f = workflow.jobs.finalize;
    expect(workflow.on.workflow_dispatch.inputs.operation).toMatchObject({
      type: 'choice',
      default: 'prepare',
      options: ['prepare', 'finalize'],
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
    const dir = mkdtempSync(join(tmpdir(), 'apr-p3-admit-'));
    const current = execFileSync('git', ['rev-parse', 'HEAD']).toString().trim();
    const request = {
      candidateSha256: 'c'.repeat(64),
      workflowCommit: 'b'.repeat(40),
      actionCommit: 'd'.repeat(40),
      bindingPr: '999',
      payloadId: '201',
      payloadSha256: '1'.repeat(64),
      metadataId: '202',
      metadataSha256: '2'.repeat(64),
      provenanceId: '203',
      provenanceSha256: '3'.repeat(64),
    };
    const env: any = {
      GITHUB_EVENT_NAME: 'workflow_dispatch',
      GITHUB_REPOSITORY: REPOSITORY,
      GITHUB_REPOSITORY_ID: REPOSITORY_ID,
      GITHUB_REF: 'refs/heads/main',
      GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/main`,
      GITHUB_WORKFLOW_SHA: current,
      GITHUB_SHA: current,
      GITHUB_RUN_ID: '501',
      GITHUB_RUN_ATTEMPT: '1',
      RUNNER_ENVIRONMENT: 'github-hosted',
      RUNNER_OS: 'Linux',
      RUNNER_ARCH: 'X64',
      RELEASE_OPERATION: 'finalize',
      FINALIZE_REQUEST: canonicalJson(request),
      GITHUB_OUTPUT: join(dir, 'outputs'),
    };
    const args = [
      'admit-request',
      '--source',
      'a'.repeat(40),
      '--version',
      'v0.0.0-internal.1',
      '--output',
      join(dir, 'request'),
    ];
    try {
      await expect(main(args, { ...env, RELEASE_OPERATION: 'prepare' })).rejects.toThrow(
        'finalization_workflow_denied',
      );
      await expect(main(args, { ...env, GITHUB_EVENT_NAME: 'pull_request' })).rejects.toThrow(
        'prepare_workflow_denied',
      );
      await expect(
        main(args, { ...env, FINALIZE_REQUEST: '{"apiUrl":"https://evil.invalid"}' }),
      ).rejects.toThrow();
      await expect(main(args, env)).resolves.toEqual({ requestAdmitted: true });
      expect(readFileSync(join(dir, 'outputs'), 'utf8')).toContain('provenance_id=203\n');
      expect(readFileSync(join(dir, 'request'), 'utf8')).toBe(canonicalJson(request));
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
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
