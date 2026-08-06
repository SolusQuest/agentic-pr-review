import { execFileSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';

const root = path.resolve(import.meta.dirname, '..');
const script = path.join(root, 'scripts', 'check-r3-live-proof.mjs');
const workflow = path.join(root, '.github', 'workflows', 'r3-live-proof.yml');
const verifierScript = path.join(root, 'runtime', 'scripts', 'verify-live-agent.sh');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const directory of temporaryRoots.splice(0)) {
    fs.rmSync(directory, { force: true, recursive: true });
  }
});

function run(candidate = workflow, verifierCandidate = verifierScript) {
  return execFileSync(process.execPath, [script], {
    cwd: root,
    encoding: 'utf8',
    env: {
      ...process.env,
      APR_R3_LIVE_POLICY_WORKFLOW: candidate,
      APR_R3_LIVE_POLICY_SCRIPT: verifierCandidate,
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
}

function mutatedVerifier(search: string, replacement: string) {
  const source = fs.readFileSync(verifierScript, 'utf8');
  expect(source).toContain(search);
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r3-live-script-'));
  temporaryRoots.push(directory);
  const candidate = path.join(directory, 'verify-live-agent.sh');
  fs.writeFileSync(candidate, source.replace(search, replacement));
  return candidate;
}

function withAdditionalWorkflow(source: string) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r3-live-workflows-'));
  temporaryRoots.push(directory);
  const candidate = path.join(directory, 'r3-live-proof.yml');
  fs.copyFileSync(workflow, candidate);
  fs.writeFileSync(path.join(directory, 'additional.yml'), source);
  return candidate;
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
    ['sudo apt-get install -y clang zlib1g-dev', 'sudo apt-get install -y clang'],
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
      '[[ "${WORKFLOW_SHA}" == "${ACTUAL_SHA}" ]]',
      '[[ "${WORKFLOW_SHA}" == "${ACTUAL_SHA}" ]] || true',
    ],
    [
      'git merge-base --is-ancestor "${ACTUAL_SHA}" refs/remotes/origin/main',
      'git merge-base --is-ancestor "${ACTUAL_SHA}" refs/remotes/origin/main || true',
    ],
    [
      '[[ -z "$(git status --porcelain=v1 --untracked-files=all)" ]]',
      '[[ -z "$(git status --porcelain=v1 --untracked-files=all)" ]] || true',
    ],
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

  test('rejects bracket notation for a second provider secret route', () => {
    const candidate = withAdditionalWorkflow(
      `name: additional\non:\n  workflow_dispatch:\njobs:\n  extra:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          KEY: \${{ secrets['R3_LIVE_PROOF_DEEPSEEK_API_KEY'] }}\n        run: echo blocked\n`,
    );
    expect(() => run(candidate)).toThrow();
  });

  test('rejects inherited secrets in another reusable-workflow route', () => {
    const candidate = withAdditionalWorkflow(
      `name: additional\non:\n  workflow_dispatch:\njobs:\n  extra:\n    uses: owner/repository/.github/workflows/reusable.yml@0123456789012345678901234567890123456789\n    secrets: inherit\n`,
    );
    expect(() => run(candidate)).toThrow();
  });

  test.each([
    [
      'exec "${supervisor}" "${live_arguments[@]}"',
      'curl https://api.deepseek.com && exec "${supervisor}" "${live_arguments[@]}"',
    ],
    ['--execution-kind native-aot', '--execution-kind framework'],
    [
      'exec "${supervisor}" launcher-dispatch-probe "${live_arguments[@]}"',
      'printf "%s\\n" APR_R3_TRUSTED_LIVE_DISPATCH_OK',
    ],
  ])('rejects a trusted-live script dispatch mutation', (search, replacement) => {
    expect(() => run(workflow, mutatedVerifier(search, replacement))).toThrow();
  });
});
