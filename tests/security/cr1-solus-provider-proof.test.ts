import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { parse, stringify } from 'yaml';
import { checkR4TrustedProof } from '../../scripts/check-r4-trusted-proof.mjs';

const root = process.cwd();
const stepName = 'CR1-R4 controlled Solus provider and accounting proof';

describe('R4 controlled provider trusted proof route', () => {
  test('admits the exact credential-free R4 step alongside S1 with all existing routes', () => {
    expect(checkR4TrustedProof()).toBe(true);
  });

  test.each(['credential', 'command', 'gate', 'extra-step'])('rejects %s drift', (mutation) => {
    const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-provider-policy-'));
    try {
      fs.cpSync(path.join(root, '.github/workflows'), directory, { recursive: true });
      const file = path.join(directory, 'solus-agent-package-proof.yml');
      const workflow = parse(fs.readFileSync(file, 'utf8'));
      const steps = workflow.jobs['install-proof'].steps;
      const step = steps.find((value: { name?: string }) => value.name === stepName);
      expect(step).toBeDefined();
      if (mutation === 'credential') step.env.GITHUB_TOKEN = '${{ secrets.GITHUB_TOKEN }}';
      if (mutation === 'command') step.run = 'node unreviewed.mjs';
      if (mutation === 'gate') delete step.if;
      if (mutation === 'extra-step') steps.push({ name: 'unreviewed', run: 'echo unreviewed' });
      fs.writeFileSync(file, stringify(workflow));
      expect(() => checkR4TrustedProof({ workflowsRoot: directory })).toThrow(
        /solus-agent-install-route/u,
      );
    } finally {
      fs.rmSync(directory, { recursive: true, force: true });
    }
  });
});
