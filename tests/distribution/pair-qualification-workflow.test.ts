import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import { parse } from 'yaml';
import { verifyWorkflow } from '../../scripts/verify-runtime-ci-workflow.mjs';
import { verifyQualificationWorkflow } from '../../scripts/release/qualification-workflow.mjs';
const workflow = parse(readFileSync('.github/workflows/runtime-ci.yml', 'utf8'));

describe('V2 closed Runtime CI qualification route', () => {
  test('preserves every former lane and gates original execution on main/manual explicit inputs', () => {
    expect(verifyQualificationWorkflow(workflow)).toBe(true);
    expect(verifyWorkflow(workflow)).toHaveProperty('v2');
    const prerequisite = workflow.jobs['runtime-core'].steps.find(
      (s: any) =>
        s.run ===
        'node runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs',
    );
    expect(prerequisite).toBeDefined();
    expect(prerequisite.if).toBeUndefined();
    const job = workflow.jobs['qualify-original-pair'];
    expect(job.if).toContain("github.event_name == 'workflow_dispatch'");
    expect(job.if).toContain("github.ref == 'refs/heads/main'");
    expect(job.if).toContain('inputs.qualify-pair');
    expect(workflow.on.workflow_dispatch.inputs['qualify-pair'].default).toBe(false);
    expect(job.permissions).toEqual({ contents: 'read', actions: 'read' });
    const text = JSON.stringify(job);
    expect(text).not.toMatch(
      /secrets\.|packages:|id-token|attestations|release create|build-managed|solus-package-proof/,
    );
    const admission = job.steps.findIndex((s: any) => s.id === 'request');
    expect(
      job.steps
        .filter((s: any) => s.uses === 'actions/download-artifact@v8')
        .every(
          (s: any) => job.steps.indexOf(s) > admission && s.with['digest-mismatch'] === 'error',
        ),
    ).toBe(true);
    for (const step of job.steps) if (step.run) expect(step.run).not.toContain('${{');
    const upload = job.steps.at(-1);
    expect(upload.if).toBe("${{ always() && steps.qualify.outputs.evidence_ready == 'true' }}");
    expect(upload.with.name).toBe(
      'r7-v2-qualification-${{ github.run_id }}-${{ github.run_attempt }}',
    );
  });
  test.each([
    'missing-job',
    'unconditional',
    'wrong-runner',
    'write-token',
    'provider-secret',
    'unbounded-time',
    'wrong-Q-checkout',
    'unreviewed-action',
    'skip-admission',
    'download-latest',
    'wrong-run',
    'ignored-digest',
    'skip-execution',
    'forged-evidence',
    'upload-anyway',
    'upload-tree',
    'wrong-name',
    'overwrite',
    'skipArchive',
    'disabled-default',
    'extra-input',
    'other-lane-write',
    'other-lane-conditional',
    'skip-network-prerequisite',
  ])('rejects topology or evidence bypass: %s', (kind) => {
    const w = structuredClone(workflow),
      j = w.jobs['qualify-original-pair'];
    const download = j.steps.find((s: any) => s.uses === 'actions/download-artifact@v8');
    if (kind === 'missing-job') delete w.jobs['qualify-original-pair'];
    if (kind === 'unconditional') delete j.if;
    if (kind === 'wrong-runner') j['runs-on'] = 'self-hosted';
    if (kind === 'write-token') j.permissions.contents = 'write';
    if (kind === 'provider-secret') j.env = { KEY: '${{ secrets.PROVIDER }}' };
    if (kind === 'unbounded-time') delete j['timeout-minutes'];
    if (kind === 'wrong-Q-checkout') j.steps[0].with.ref = '${{ inputs.source }}';
    if (kind === 'unreviewed-action') j.steps.push({ uses: 'actions/setup-go@v6' });
    if (kind === 'skip-admission') j.steps.find((s: any) => s.id === 'request').run = 'true';
    if (kind === 'download-latest') download.with['artifact-ids'] = 'latest';
    if (kind === 'wrong-run') download.with['run-id'] = '${{ github.run_id }}';
    if (kind === 'ignored-digest') download.with['digest-mismatch'] = 'warn';
    if (kind === 'skip-execution') j.steps.find((s: any) => s.id === 'qualify').run = 'true';
    if (kind === 'forged-evidence')
      j.steps.find((s: any) => s.id === 'qualify').run += ' || echo evidence_ready=true';
    if (kind === 'upload-anyway') j.steps.at(-1).if = 'always()';
    if (kind === 'upload-tree') j.steps.at(-1).with.path = '${{ github.workspace }}';
    if (kind === 'wrong-name') j.steps.at(-1).with.name = 'qualification-latest';
    if (kind === 'overwrite') j.steps.at(-1).with.overwrite = true;
    if (kind === 'skipArchive') j.steps.at(-1).with.archive = false;
    if (kind === 'disabled-default') w.on.workflow_dispatch.inputs['qualify-pair'].default = true;
    if (kind === 'extra-input')
      w.on.workflow_dispatch.inputs.transport = {
        description: 'override',
        type: 'string',
        required: false,
      };
    if (kind === 'other-lane-write') w.jobs['r2-agent-loop'].permissions = { contents: 'write' };
    if (kind === 'other-lane-conditional') w.jobs['r4-action-host'].if = 'false';
    if (kind === 'skip-network-prerequisite')
      w.jobs['runtime-core'].steps.find(
        (s: any) =>
          s.run ===
          'node runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs',
      ).if = 'false';
    expect(() => verifyWorkflow(w)).toThrow();
  });
  test('original adapter cannot take a saved observation or alternate build path from CLI', () => {
    const code = readFileSync('scripts/release/qualify-pair.mjs', 'utf8');
    expect(code).toContain('await verifyFinalizedPair(input, env)');
    expect(code).toContain('await executeOriginalPair(input, verified)');
    expect(code).not.toContain('--observation');
    expect(code).not.toContain('all-not-run fallback');
    const owner = readFileSync(
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/verify-network.mjs',
      'utf8',
    );
    expect(owner.indexOf('await teardownFixture(root)')).toBeLessThan(
      owner.indexOf('if (returnObservation) return JSON.parse'),
    );
  });
});
