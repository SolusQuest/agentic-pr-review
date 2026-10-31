import { execFileSync } from 'node:child_process';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parse } from 'yaml';
import { describe, expect, test } from 'vitest';
import {
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  verifyCandidateWorkflow,
} from '../../scripts/release/candidate.mjs';
import { admitPreparation, admitWorkflow, main } from '../../scripts/release/prepare-candidate.mjs';
import { GH_VERSION, PREDICATE_TYPE } from '../../scripts/release/verify-provenance.mjs';

const workflow = parse(readFileSync('.github/workflows/release.yml', 'utf8'));
describe('P1 prepare authority and ordinary credential-free CI', () => {
  test('admits only the complete reviewed topology, including commands and credential routes', () => {
    expect(verifyCandidateWorkflow(workflow)).toBe(true);
    const mutations = [
      (w: any) => {
        w.permissions.contents = 'write';
      },
      (w: any) => {
        w.permissions.packages = 'read';
      },
      (w: any) => {
        delete w.jobs['prepare-build'].permissions.packages;
      },
      (w: any) => {
        w.jobs['candidate-smoke'].permissions = { contents: 'read', packages: 'read' };
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'build').env.GITHUB_TOKEN =
          '${{ github.token }}';
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'restore').env.EXPECTED_HEAD =
          '${{ inputs.source-commit }}';
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'restore').run +=
          ' --source https://foreign.invalid';
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'restore').env.GITHUB_TOKEN =
          '${{ secrets.OTHER_TOKEN }}';
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'build').env.NUGET_PACKAGES =
          '/unverified/cache';
      },
      (w: any) => {
        w.jobs['prepare-record'].permissions.actions = 'write';
      },
      (w: any) => {
        delete w.jobs['prepare-build'].if;
      },
      (w: any) => {
        w.on.pull_request_target = null;
      },
      (w: any) => {
        w.jobs['prepare-build'].steps[0].with['persist-credentials'] = true;
      },
      (w: any) => {
        w.jobs['prepare-record'].steps.find(
          (s: any) => s.uses === 'actions/download-artifact@v8',
        ).with['artifact-ids'] = '999';
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'payload').with.overwrite = true;
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.find((s: any) => s.id === 'payload').with.path =
          '/private/source/**';
      },
      (w: any) => {
        w.jobs['prepare-record'].steps.find((s: any) => s.id === 'record').run =
          'curl https://evil.invalid';
      },
      (w: any) => {
        w.jobs['prepare-record'].steps.push({ run: 'gh release create unsafe' });
      },
      (w: any) => {
        w.jobs['candidate-fixtures'].steps.push({
          env: { GITHUB_TOKEN: '${{ github.token }}' },
          run: 'echo unsafe',
        });
      },
      (w: any) => {
        w.jobs['prepare-build'].steps.push({
          uses: 'actions/upload-artifact@v7',
          with: { path: '/private/**' },
        });
      },
      (w: any) => {
        w.jobs.extra = structuredClone(w.jobs['prepare-record']);
      },
    ];
    for (const mutate of mutations) {
      const changed = structuredClone(workflow);
      mutate(changed);
      expect(() => verifyCandidateWorkflow(changed)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('manual main-only build/record have no publication, provider, state or signing authority', () => {
    expect(Object.keys(workflow.on).sort()).toEqual(['pull_request', 'push', 'workflow_dispatch']);
    expect(workflow.permissions).toEqual({ contents: 'read' });
    expect(Object.keys(workflow.on.workflow_dispatch.inputs).sort()).toEqual([
      'finalize-request',
      'operation',
      'release-version',
      'source-commit',
    ]);
    for (const name of ['prepare-build', 'prepare-record']) {
      const job = workflow.jobs[name];
      expect(job.if).toBe(
        "github.event_name == 'workflow_dispatch' && github.repository == 'SolusQuest/agentic-pr-review' && github.ref == 'refs/heads/main' && inputs.operation == 'prepare'",
      );
      expect(job.environment).toBeUndefined();
      expect(job['runs-on']).toBe('ubuntu-24.04');
      for (const value of Object.values(job.permissions)) expect(value).toBe('read');
      expect(JSON.stringify(job)).not.toMatch(
        /secrets\.|id-token|attestations|pull_request_target|PROVIDER|STATE_KEY/,
      );
      for (const step of job.steps.filter((x: any) => x.uses === 'actions/checkout@v6'))
        expect(step.with['persist-credentials']).toBe(false);
    }
    expect(workflow.jobs['prepare-build'].permissions).toEqual({
      contents: 'read',
      packages: 'read',
    });
    expect(workflow.jobs['prepare-record'].permissions).toEqual({
      contents: 'read',
      actions: 'read',
    });
    const build = workflow.jobs['prepare-build'];
    const restore = build.steps.find((step: any) => step.id === 'restore');
    expect(restore).toEqual({
      name: 'Restore and verify exact managed dependencies with bounded package-read authority',
      id: 'restore',
      'working-directory': 'control',
      env: {
        EXPECTED_HEAD: '${{ github.workflow_sha }}',
        GITHUB_TOKEN: '${{ github.token }}',
        GITHUB_ACTOR: '${{ github.actor }}',
      },
      run: 'node scripts/release/solus-package-proof.mjs verify "$RUNNER_TEMP/r7-p1-solus-cache"',
    });
    expect(build.steps.indexOf(restore)).toBeLessThan(
      build.steps.findIndex((step: any) => step.id === 'build'),
    );
    expect(build.steps.find((step: any) => step.id === 'build').env.NUGET_PACKAGES).toBe(
      '${{ runner.temp }}/r7-p1-solus-cache/packages',
    );
    for (const step of build.steps.filter((step: any) => step !== restore))
      expect(step.env?.GITHUB_TOKEN).toBeUndefined();
  });
  test('all shell inputs use environment arguments, never expression interpolation', () => {
    for (const job of Object.values(workflow.jobs) as any[]) {
      for (const step of job.steps) if (step.run) expect(step.run).not.toContain('${{');
    }
    const build = workflow.jobs['prepare-build'].steps;
    expect(build.findIndex((x: any) => x.name?.startsWith('Admit'))).toBeLessThan(
      build.findIndex((x: any) => x.with?.path === 'source'),
    );
    const checkouts = build.filter((x: any) => x.uses === 'actions/checkout@v6');
    expect(checkouts[0].with.ref).toBe('${{ github.workflow_sha }}');
    expect(checkouts[1].with.ref).toBe('${{ inputs.source-commit }}');
  });
  test('isolates signing after build/record, pins tools and retains only verified original provenance', () => {
    const sign = workflow.jobs['prepare-sign'];
    expect(sign.needs).toEqual(['prepare-build', 'prepare-record']);
    expect(sign.if).toBe(workflow.jobs['prepare-build'].if);
    expect(sign['runs-on']).toBe('ubuntu-24.04');
    expect(sign.permissions).toEqual({
      contents: 'read',
      actions: 'read',
      'id-token': 'write',
      attestations: 'write',
    });
    for (const [name, job] of Object.entries(workflow.jobs) as any[]) {
      if (name !== 'prepare-sign') {
        expect(job.permissions?.['id-token']).toBeUndefined();
        expect(job.permissions?.attestations).toBeUndefined();
      }
    }
    const checkouts = sign.steps.filter((s: any) => s.uses === 'actions/checkout@v6');
    expect(checkouts).toHaveLength(1);
    expect(checkouts[0].with).toEqual({
      ref: '${{ github.workflow_sha }}',
      path: 'control',
      'fetch-depth': 0,
      'persist-credentials': false,
    });
    const downloads = sign.steps.filter((s: any) => s.uses === 'actions/download-artifact@v8');
    expect(downloads.map((s: any) => s.with['artifact-ids'])).toEqual([
      '${{ needs.prepare-build.outputs.artifact-id }}',
      '${{ needs.prepare-record.outputs.metadata-id }}',
    ]);
    const attest = sign.steps.find((s: any) => s.id === 'attest');
    expect(attest.uses).toBe('actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6');
    expect(attest.with).toMatchObject({
      'subject-name': '${{ needs.prepare-build.outputs.archive-name }}',
      'subject-digest': 'sha256:${{ needs.prepare-build.outputs.archive-sha256 }}',
      'predicate-type': PREDICATE_TYPE,
      'push-to-registry': false,
      'create-storage-record': false,
    });
    const pre = sign.steps.findIndex((s: any) => s.run?.includes(' workflow-predicate '));
    const verify = sign.steps.findIndex((s: any) => s.run?.includes(' workflow-verify '));
    const upload = sign.steps.findIndex((s: any) => s.id === 'evidence');
    expect(pre).toBeLessThan(sign.steps.indexOf(attest));
    expect(verify).toBeGreaterThan(sign.steps.indexOf(attest));
    expect(upload).toBeGreaterThan(verify);
    expect(sign.steps[upload].with).toMatchObject({
      overwrite: false,
      'retention-days': 7,
      'if-no-files-found': 'error',
    });
    expect(sign.steps[upload].with.path).toBe(
      '${{ runner.temp }}/r7-p2-evidence/predicate.json\n${{ runner.temp }}/r7-p2-evidence/bundle.json\n${{ runner.temp }}/r7-p2-evidence/verification.json\n',
    );
    const installer = sign.steps.find(
      (s: any) => s.name === 'Install exact maintained provenance verifier',
    );
    expect(installer.run).toContain(`v${GH_VERSION}/gh_${GH_VERSION}_linux_amd64.tar.gz`);
    expect(installer.run).toContain(
      '4c806978ffce1cec70853302a56d70ac6da4f19bae7adca4e1a8ab00633dd91d',
    );
    expect(JSON.stringify(sign)).not.toMatch(/packages:|npm ci|dotnet|secrets\.|release create/);
    const mutations = [
      (w: any) => {
        w.jobs['prepare-sign'].if = "github.event_name == 'pull_request'";
      },
      (w: any) => {
        w.jobs['prepare-sign'].needs = ['prepare-build'];
      },
      (w: any) => {
        w.jobs['prepare-sign']['runs-on'] = 'self-hosted';
      },
      (w: any) => {
        w.jobs['prepare-sign'].steps[0].with.ref = '${{ inputs.source-commit }}';
      },
      (w: any) => {
        w.jobs['prepare-record'].permissions.attestations = 'write';
      },
      (w: any) => {
        w.jobs['prepare-sign'].steps[verify].continueOnError = true;
      },
      (w: any) => {
        w.jobs['prepare-sign'].steps[verify].run = 'true';
      },
      (w: any) => {
        w.jobs['prepare-sign'].steps[upload].if = 'always()';
      },
      (w: any) => {
        w.jobs['prepare-sign'].steps.find((s: any) => s.id === 'attest').with['subject-digest'] =
          'sha256:wrong';
      },
    ];
    for (const mutate of mutations) {
      const w = structuredClone(workflow);
      mutate(w);
      expect(() => verifyCandidateWorkflow(w)).toThrow('candidate_workflow_topology_drift');
    }
  });
  test('ordinary PR/push runs fixtures and the actual package supervisor, with no remote writes', () => {
    for (const name of ['candidate-fixtures', 'candidate-smoke']) {
      const job = workflow.jobs[name];
      expect(job.if).toBe("github.event_name != 'workflow_dispatch'");
      expect(job.environment).toBeUndefined();
      expect(
        job.steps.some((x: any) => /upload-artifact|download-artifact/.test(x.uses || '')),
      ).toBe(false);
      const checkout = job.steps.find((x: any) => x.uses === 'actions/checkout@v6');
      expect(checkout.with.ref).toContain('github.event.pull_request.head.sha');
      expect(checkout.with['persist-credentials']).toBe(false);
    }
    expect(
      workflow.jobs['candidate-fixtures'].steps.some(
        (x: any) => x.run === 'npx vitest run tests/release',
      ),
    ).toBe(true);
    expect(
      workflow.jobs['candidate-smoke'].steps.some(
        (x: any) => x.run === 'node tests/release/verify-candidate.mjs',
      ),
    ).toBe(true);
    const sdk = workflow.jobs['candidate-smoke'].steps.find(
      (x: any) => x.uses === 'actions/setup-dotnet@v5',
    );
    expect(sdk.with).toEqual({ 'dotnet-version': '10.0.109' });
    expect(sdk.env.DOTNET_INSTALL_DIR).toBe('${{ runner.temp }}/r7-p1-dotnet');
  });
  test('original payload upload precedes metadata, which checks independent original file hashes', () => {
    const build = workflow.jobs['prepare-build'],
      record = workflow.jobs['prepare-record'];
    expect(record.needs).toBe('prepare-build');
    expect(build.outputs['archive-sha256']).toBe('${{ steps.build.outputs.archive_sha256 }}');
    expect(build.outputs['receipt-sha256']).toBe('${{ steps.build.outputs.receipt_sha256 }}');
    expect(build.outputs['artifact-sha256']).toBe('${{ steps.payload.outputs.artifact-digest }}');
    const download = record.steps.find((x: any) => x.uses === 'actions/download-artifact@v8');
    expect(download.with['artifact-ids']).toBe('${{ needs.prepare-build.outputs.artifact-id }}');
    expect(download.with.name).toBeUndefined();
    const verify = record.steps.find((x: any) => x.id === 'record');
    expect(verify.env.ARCHIVE_SHA256).toBe('${{ needs.prepare-build.outputs.archive-sha256 }}');
    expect(verify.env.RECEIPT_SHA256).toBe('${{ needs.prepare-build.outputs.receipt-sha256 }}');
    for (const job of [build, record]) {
      const upload = job.steps.find((x: any) => x.uses === 'actions/upload-artifact@v7');
      expect(upload.with.overwrite).toBe(false);
      expect(upload.with['include-hidden-files']).toBe(false);
      expect(upload.with['if-no-files-found']).toBe('error');
      expect(upload.with['retention-days']).toBe(7);
      expect(upload.with.name).toContain('${{ github.run_id }}-${{ github.run_attempt }}');
    }
    expect(record.steps.at(-1).run).toContain(' locator ');
    expect(record.steps.at(-1).env.METADATA_ID).toBe('${{ steps.metadata.outputs.artifact-id }}');
  });
  test('admission distinguishes trusted W from explicit ancestor source S and refuses other authority', () => {
    const repo = mkdtempSync(join(tmpdir(), 'apr-p1-source-'));
    const git = (...args: string[]) =>
      execFileSync('git', args, { cwd: repo, stdio: ['ignore', 'pipe', 'pipe'] })
        .toString()
        .trim();
    try {
      git('init', '-q');
      git('config', 'user.name', 'Synthetic fixture');
      git('config', 'user.email', 'fixture@example.invalid');
      writeFileSync(join(repo, 'fixture.txt'), 'source');
      git('add', '.');
      git('commit', '-qm', 'Synthetic source');
      const source = git('rev-parse', 'HEAD');
      writeFileSync(join(repo, 'fixture.txt'), 'workflow');
      git('add', '.');
      git('commit', '-qm', 'Synthetic workflow');
      const builder = git('rev-parse', 'HEAD');
      const env = {
        GITHUB_EVENT_NAME: 'workflow_dispatch',
        GITHUB_REPOSITORY: REPOSITORY,
        GITHUB_REPOSITORY_ID: REPOSITORY_ID,
        GITHUB_REF: 'refs/heads/main',
        GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/main`,
        GITHUB_WORKFLOW_SHA: builder,
        GITHUB_SHA: builder,
        GITHUB_RUN_ID: '9007199254740993',
        GITHUB_RUN_ATTEMPT: '2',
        RUNNER_ENVIRONMENT: 'github-hosted',
        RUNNER_OS: 'Linux',
        RUNNER_ARCH: 'X64',
      };
      expect(source).not.toBe(builder);
      const admitted = admitPreparation(repo, source, 'v0.0.0-internal.1', env);
      expect(admitted.producer.workflowCommit).toBe(builder);
      expect(admitted.sourceTree).toBe(git('rev-parse', source + '^{tree}'));
      for (const [field, value] of Object.entries({
        GITHUB_EVENT_NAME: 'pull_request',
        GITHUB_REPOSITORY: 'foreign/repo',
        GITHUB_REPOSITORY_ID: '9',
        GITHUB_REF: 'refs/heads/topic',
        GITHUB_WORKFLOW_REF: `${REPOSITORY}/${WORKFLOW}@refs/heads/topic`,
        GITHUB_SHA: source,
        RUNNER_ENVIRONMENT: 'self-hosted',
        RUNNER_OS: 'Windows',
        RUNNER_ARCH: 'ARM64',
      }))
        expect(() => admitWorkflow({ ...env, [field]: value }, repo)).toThrow(
          'prepare_workflow_denied',
        );
      expect(() =>
        admitWorkflow({ ...env, GITHUB_WORKFLOW_SHA: source, GITHUB_SHA: source }, repo),
      ).toThrow('workflow_checkout_drift');
      expect(() => admitPreparation(repo, 'f'.repeat(40), 'v0.0.0-internal.1', env)).toThrow(
        'source_admission_failed',
      );
      expect(() => admitPreparation(repo, 'main', 'v0.0.0-internal.1', env)).toThrow(
        'invalid_source_commit',
      );
      expect(() => admitPreparation(repo, source, 'v1.0.0', env)).toThrow(
        'invalid_release_version',
      );
    } finally {
      rmSync(repo, { recursive: true, force: true });
    }
  });
  test('production CLI has no arbitrary endpoint or in-progress handoff flag', async () => {
    await expect(main(['storage', '--api-url', 'https://evil.invalid'])).rejects.toThrow(
      'invalid_arguments',
    );
    await expect(main(['storage', '--allow-in-progress', 'true'])).rejects.toThrow(
      'invalid_arguments',
    );
    await expect(main(['publish'])).rejects.toThrow('invalid_arguments');
  });
});
