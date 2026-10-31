import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { parse, stringify } from 'yaml';
import { checkR4TrustedProof } from '../../scripts/check-r4-trusted-proof.mjs';

const stepName = 'CR1-V1 actual managed Solus evaluation and replay proof';

describe('V1 actual managed evaluation trusted qualification', () => {
  test('requires the exact credential-free managed gate and preserves accepted proof steps', () => {
    expect(checkR4TrustedProof()).toBe(true);
  });

  test.each([
    'missing-step',
    'condition',
    'credential',
    'extra-env',
    'command',
    'runner',
    'clean-head',
    'pin-proof',
    'cache',
    'drop-earlier',
    'archive-timeout-short',
    'archive-timeout-long',
  ])('rejects %s drift', (mutation) => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-v1-proof-policy-'));
    try {
      fs.cpSync(path.join(process.cwd(), '.github/workflows'), directory, { recursive: true });
      const file = path.join(directory, 'solus-agent-package-proof.yml');
      const workflow = parse(fs.readFileSync(file, 'utf8'));
      const steps = workflow.jobs['install-proof'].steps;
      const step = steps.find((value: { name?: string }) => value.name === stepName);
      expect(step).toBeDefined();
      if (mutation === 'missing-step') steps.splice(steps.indexOf(step), 1);
      if (mutation === 'condition') delete step.if;
      if (mutation === 'credential') step.env.GITHUB_TOKEN = '${{ secrets.GITHUB_TOKEN }}';
      if (mutation === 'extra-env') step.env.APR_SKIP_ACTUAL_EXECUTION = '1';
      if (mutation === 'command')
        step.run = 'bash runtime/scripts/verify-r5-evaluation.sh framework';
      if (mutation === 'runner') workflow.jobs['install-proof']['runs-on'] = 'ubuntu-latest';
      if (mutation === 'clean-head')
        steps[0].run = steps[0].run.replace('test "$WORKFLOW_HEAD" = "$GITHUB_SHA"', 'true');
      if (mutation === 'pin-proof')
        steps.find(
          (value: { name?: string }) =>
            value.name === 'Cold-cache actual APR managed construction and pin proof',
        ).run = 'true';
      if (mutation === 'cache') step.env.NUGET_PACKAGES = '${{ runner.temp }}/unverified-cache';
      if (mutation === 'archive-timeout-short' || mutation === 'archive-timeout-long')
        steps.find(
          (value: { name?: string }) =>
            value.name === 'CR2-V1 complete runtime and original archive qualification',
        )['timeout-minutes'] = mutation === 'archive-timeout-short' ? 180 : 211;
      if (mutation === 'drop-earlier')
        steps.splice(
          steps.findIndex(
            (value: { name?: string }) =>
              value.name === 'CR1-S4 actual restored Solus Host review and successor proof',
          ),
          1,
        );
      fs.writeFileSync(file, stringify(workflow));
      expect(() => checkR4TrustedProof({ workflowsRoot: directory })).toThrow(
        /solus-agent-install-route/u,
      );
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });
});
