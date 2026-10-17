import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { parse, stringify } from 'yaml';
import { checkR4TrustedProof } from '../../scripts/check-r4-trusted-proof.mjs';

const root = process.cwd();
const stepName = 'CR1-R5 actual managed fresh Host review and capture proof';

describe('R5 actual managed Host trusted proof route', () => {
  test('admits only the exact credential-free R5 append while retaining all earlier routes', () => {
    expect(checkR4TrustedProof()).toBe(true);
  });

  test.each(['credential', 'extra-env', 'command', 'gate', 'drop-earlier', 'extra-step', 'runner'])(
    'rejects %s drift',
    (mutation) => {
      const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r5-proof-policy-'));
      try {
        fs.cpSync(path.join(root, '.github/workflows'), directory, { recursive: true });
        const file = path.join(directory, 'solus-agent-package-proof.yml');
        const workflow = parse(fs.readFileSync(file, 'utf8'));
        const steps = workflow.jobs['install-proof'].steps;
        const step = steps.find((value: { name?: string }) => value.name === stepName);
        expect(step).toBeDefined();
        if (mutation === 'credential') step.env.GITHUB_TOKEN = '${{ secrets.GITHUB_TOKEN }}';
        if (mutation === 'extra-env') step.env.APR_UNREVIEWED = '1';
        if (mutation === 'command') step.run += ' --skip-actual-package';
        if (mutation === 'gate') delete step.if;
        if (mutation === 'drop-earlier')
          steps.splice(
            steps.findIndex(
              (value: { name?: string }) =>
                value.name === 'CR1-S1 authenticated Solus state and capture proof',
            ),
            1,
          );
        if (mutation === 'extra-step') steps.push({ name: 'unreviewed', run: 'echo unreviewed' });
        if (mutation === 'runner') workflow.jobs['install-proof']['runs-on'] = 'ubuntu-latest';
        fs.writeFileSync(file, stringify(workflow));
        expect(() => checkR4TrustedProof({ workflowsRoot: directory })).toThrow(
          /solus-agent-install-route/u,
        );
      } finally {
        fs.rmSync(directory, { recursive: true, force: true });
      }
    },
  );
});
