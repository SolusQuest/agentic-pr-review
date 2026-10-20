import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';

const roots: string[] = [];
afterEach(() => {
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

function project(contents: string) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-e2p-diagnostics-'));
  roots.push(root);
  const log = path.join(root, 'private-child.log');
  fs.writeFileSync(log, contents);
  return spawnSync(process.execPath, ['scripts/project-r4-e2p-diagnostics.mjs', log], {
    cwd: process.cwd(),
    encoding: 'utf8',
  });
}

describe('R4 E2P public diagnostic projection', () => {
  it('retains bounded visibility failures and drops unapproved predicate fields', () => {
    const diagnostic =
      'APR_R4_E2P_V2_DIAGNOSTIC case=state-visibility-diagnostic check=state_read_clock_advanced result=failed';
    const result = project(
      `${diagnostic}\nAPR_R4_E2P_V2_DIAGNOSTIC case=private-canary check=secret result=failed\n`,
    );
    expect(result.stdout).toBe(`${diagnostic}\n`);
  });

  it('projects actual supervisor predicates and fixed bounded counts without private values', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-r4-supervisor-diagnostics-'));
    roots.push(root);
    const evidence = path.join(root, 'trusted-proof-payload-evidence.json');
    fs.writeFileSync(
      evidence,
      JSON.stringify({
        cases: [
          { Name: 'state-visibility-diagnostic', Passed: false, ActualStatus: 'outcome_ambiguous' },
        ],
      }),
    );
    fs.mkdirSync(path.join(root, 'state-visibility-diagnostic'));
    fs.writeFileSync(
      path.join(root, 'state-visibility-diagnostic', 'case-diagnostic.json'),
      JSON.stringify({
        state_read_clock_advanced: false,
        expected_outcome: true,
        synthetic_primary_remaining: 42,
        secret: 'private-canary',
      }),
    );
    const result = spawnSync(
      process.execPath,
      ['scripts/project-r4-e2p-diagnostics.mjs', '--supervisor', evidence],
      { cwd: process.cwd(), encoding: 'utf8' },
    );
    expect(result.status).toBe(0);
    expect(result.stdout).toBe(
      'APR_R4_E2P_V2_FAILURE stage=synthetic-route case=state-visibility-diagnostic code=case-failed status=outcome_ambiguous\nAPR_R4_E2P_V2_DIAGNOSTIC case=state-visibility-diagnostic check=state_read_clock_advanced result=failed\nAPR_R4_E2P_V2_DIAGNOSTIC case=state-visibility-diagnostic metric=synthetic_primary_remaining value=42\n',
    );
    expect(result.stdout).not.toContain('private-canary');
    expect(project(result.stdout).stdout).toBe(result.stdout);
  });

  it('rejects unknown, nonnumeric and out-of-bound request counts', () => {
    const allowed =
      'APR_R4_E2P_V2_DIAGNOSTIC case=stale-head metric=synthetic_primary_remaining value=80';
    const result = project(
      `${allowed}\n` +
        'APR_R4_E2P_V2_DIAGNOSTIC case=stale-head metric=secret value=42\n' +
        'APR_R4_E2P_V2_DIAGNOSTIC case=stale-head metric=host_rest value=private-canary\n' +
        'APR_R4_E2P_V2_DIAGNOSTIC case=stale-head metric=host_rest value=-1\n' +
        'APR_R4_E2P_V2_DIAGNOSTIC case=stale-head metric=host_rest value=1000001\n',
    );
    expect(result.stdout).toBe(`${allowed}\n`);
  });

  it('reports only the last fixed execution stage alongside the original failure', () => {
    const failure =
      'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed';
    const result = project(
      'APR_R4_E2P_V2_DIAGNOSTIC stage=bundle\n' +
        'private output\n' +
        'APR_R4_E2P_V2_DIAGNOSTIC stage=architecture\n',
    );
    expect(result.stdout).toBe(`${failure}\nAPR_R4_E2P_V2_DIAGNOSTIC stage=architecture\n`);
    expect(result.status).toBe(0);
    expect(result.stderr).toBe('');
  });

  it.each([
    'stage=unknown',
    'stage=architecture secret=private-canary',
    'stage=private-canary',
    'stage=architecture/private-canary',
    'stage=architecture\u0000private-canary',
    `stage=${'a'.repeat(16_384)}`,
    'stage=architecture status=failed',
  ])('rejects forged stage fields without reflecting their contents: %s', (fields) => {
    const result = project(`APR_R4_E2P_V2_DIAGNOSTIC ${fields}\n`);
    expect(result.stdout).toBe(
      'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed\n',
    );
    expect(result.stderr).toBe('');
  });

  it('does not admit even fixed stages from an oversized log', () => {
    const result = project(
      `${'x'.repeat(8 * 1024 * 1024)}\nAPR_R4_E2P_V2_DIAGNOSTIC stage=architecture\n`,
    );
    expect(result.stdout).toBe(
      'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed\n',
    );
  });

  it('drops paths, bodies, canaries, multiline stderr, and unapproved tokens', () => {
    const child = spawnSync(
      process.execPath,
      [
        '-e',
        "console.log('/private/worktree/source.cs: error');" +
          'console.error(\'{"body":"private-body-canary"}\');' +
          "console.error('provider-canary-value\\nsecond stderr line');" +
          "console.error('APR_R4_E2P_FAILURE stage=attacker case=raw code=leak status=failed');" +
          'process.exit(1);',
      ],
      { encoding: 'utf8' },
    );
    expect(child.status).toBe(1);

    const result = project(`${child.stdout}${child.stderr}`);

    expect(result.status).toBe(0);
    expect(result.stderr).toBe('');
    expect(result.stdout).toBe(
      'APR_R4_E2P_FAILURE stage=proof case=clean-source code=child-failed status=failed\n',
    );
  });

  it('projects at most three distinct allowlisted diagnostics', () => {
    const allowed =
      'APR_R4_E2P_FAILURE stage=synthetic-route case=dispatch-continuation ' +
      'code=case-failed status=agent_result_invalid';
    const result = project(`${allowed}\n${allowed}\nprivate tail\n`);

    expect(result.status).toBe(0);
    expect(result.stdout).toBe(`${allowed}\n`);
    expect(result.stdout).not.toContain('private tail');
  });

  it('projects an allowlisted v2 warning-policy failure without exposing private logs', () => {
    const allowed =
      'APR_R4_E2P_V2_FAILURE stage=proof case=clean-source ' + 'code=warning-policy status=failed';
    const result = project(`${allowed}\n/private/verifier-publish.log\n`);

    expect(result.status).toBe(0);
    expect(result.stdout).toBe(`${allowed}\n`);
    expect(result.stdout).not.toContain('/private/');
  });
});
