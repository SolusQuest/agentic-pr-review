import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';

const root = path.resolve(import.meta.dirname, '..');
const script = path.join(root, 'scripts', 'check-r3-live-proof.mjs');
const workflow = path.join(root, '.github', 'workflows', 'r3-live-proof.yml');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const directory of temporaryRoots.splice(0)) {
    fs.rmSync(directory, { force: true, recursive: true });
  }
});

function run(candidate = workflow) {
  return execFileSync(process.execPath, [script], {
    cwd: root,
    encoding: 'utf8',
    env: {
      ...process.env,
      APR_R3_LIVE_POLICY_WORKFLOW: candidate,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function mutated(search: string, replacement: string) {
  const source = fs.readFileSync(workflow, 'utf8');
  expect(source).toContain(search);
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r3-live-policy-'));
  temporaryRoots.push(directory);
  const candidate = path.join(directory, 'r3-live-proof.yml');
  fs.writeFileSync(candidate, source.replace(search, replacement));
  return candidate;
}

describe('trusted live workflow policy', () => {
  test('admits the exact checked-in workflow', () => {
    expect(run()).toBe('APR_R3_LIVE_POLICY_OK\n');
  });

  test.each([
    [
      '  workflow_dispatch:\n',
      '  workflow_dispatch:\n    inputs:\n      ref:\n        required: true\n',
    ],
    ['      deployment: false', '      deployment: true'],
    ['      name: r3-live-proof', '      name: other-environment'],
    ['actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803', 'actions/checkout@v6'],
    ['          ref: ${{ github.sha }}', '          ref: refs/heads/main'],
    ['          persist-credentials: false', '          persist-credentials: true'],
    ['  contents: read', '  contents: write'],
    ['  workflow_dispatch:', '  pull_request_target:'],
    [
      'exec runtime/scripts/verify-live-agent.sh live',
      'bash runtime/scripts/probe-deepseek-request.sh run',
    ],
    [
      '${{ secrets.R3_LIVE_PROOF_DEEPSEEK_API_KEY }}',
      '${{ secrets.AGENTIC_REVIEW_DEEPSEEK_API_KEY }}',
    ],
    [
      '          ACTUAL_SHA: ${{ github.sha }}',
      '          ACTUAL_SHA: ${{ github.sha }}\n          EARLY_SECRET: ${{ secrets.R3_LIVE_PROOF_DEEPSEEK_API_KEY }}',
    ],
    ["      github.ref == 'refs/heads/main'", "      github.ref != 'refs/heads/main'"],
    ['r3-live-proof.yml@refs/heads/main', 'r3-live-proof.yml@refs/heads/other'],
    [
      'actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1',
      'attacker/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1',
    ],
    [
      '      - name: Run the bounded trusted live proof',
      '      - name: Upload evidence\n        uses: actions/upload-artifact@0000000000000000000000000000000000000000\n\n      - name: Run the bounded trusted live proof',
    ],
  ])('rejects a counterfactual workflow mutation', (search, replacement) => {
    expect(() => run(mutated(search, replacement))).toThrow();
  });
});
