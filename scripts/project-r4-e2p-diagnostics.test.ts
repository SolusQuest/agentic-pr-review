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
  it('observes historical child EXIT before parent cleanup without changing status or stdout', () => {
    const base = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'apr-observer-')));
    roots.push(base);
    const helper = path.resolve('scripts/project-r4-e2p-diagnostics.mjs');
    const hook = path.join(base, 'bash-env');
    fs.writeFileSync(hook, spawnSync(process.execPath, [helper, '--historical-hook']).stdout);
    for (const [status, available, target] of [
      [0, true, true],
      [7, true, true],
      [0, false, true],
      [7, false, true],
      [7, true, false],
    ] as const) {
      const temporary = fs.mkdtempSync(path.join(base, 'apr-r4-e2p.ab'));
      fs.writeFileSync(path.join(temporary, 'clean-source.log'), 'private-canary\n');
      const output = path.join(base, `snapshot-${status}-${available}-${target}.json`);
      const result = spawnSync(
        'bash',
        [
          '-c',
          `trap 'saved=$?; trap - EXIT; rm -rf -- "$temporary_root"; exit "$saved"' EXIT
execute_proof() { printf 'original-stdout\n'; return "$PROOF_EXIT"; }
export -f execute_proof
${target ? 'bash -euo pipefail -c execute_proof' : `bash -c 'printf "original-stdout\\n"; exit "$PROOF_EXIT"'`}
exit "$?"`,
        ],
        {
          encoding: 'utf8',
          env: {
            ...process.env,
            BASH_ENV: hook,
            temporary_root: temporary,
            PROOF_EXIT: String(status),
            APR_R4_OBSERVER_HELPER: available ? helper : path.join(base, 'absent.mjs'),
            APR_R4_OBSERVER_OUTPUT: output,
          },
        },
      );
      expect(result.status).toBe(status);
      expect(result.stdout).toBe('original-stdout\n');
      expect(result.stderr).toBe('');
      expect(fs.existsSync(temporary)).toBe(false);
      expect(fs.existsSync(output)).toBe(available && target);
      if (available && target) {
        expect(JSON.parse(fs.readFileSync(output, 'utf8'))).toMatchObject({
          child_exit: status,
          clean_source: 'readable',
          receipt: 'missing',
        });
      }
    }
  });

  it('bounds historical snapshots and rejects unsafe files and nonclosed reports without private output', () => {
    const base = fs.realpathSync(fs.mkdtempSync(path.join(os.tmpdir(), 'apr-observer-')));
    roots.push(base);
    const temporary = fs.mkdtempSync(path.join(base, 'apr-r4-e2p.ab'));
    const helper = path.resolve('scripts/project-r4-e2p-diagnostics.mjs');
    const call = (...args: string[]) =>
      spawnSync(process.execPath, [helper, ...args], { encoding: 'utf8' });
    const output = path.join(base, 'snapshot.json');
    fs.writeFileSync(
      path.join(temporary, 'clean-source.log'),
      'private-canary /private/path\nerror CS1002: private-body\nAPR_R4_E2P_AOT_WARNING_ALLOWLIST_INVALID\n',
    );
    const publish = path.join(temporary, 'publish.log');
    fs.writeFileSync(publish, '');
    fs.truncateSync(publish, 8 * 1024 * 1024 + 1);
    fs.symlinkSync(publish, path.join(temporary, 'verifier-publish.log'));
    fs.writeFileSync(path.join(temporary, 'receipt.line'), 'APR_R4_E2P_RECEIPT {}\n');
    fs.writeFileSync(
      publish,
      'error NU1301: private-feed-canary\nerror NU1903: private-advisory\n',
    );
    const codeOutput = path.join(base, 'code-snapshot.json');
    call('--historical-capture', temporary, codeOutput, '7');
    expect(JSON.parse(fs.readFileSync(codeOutput, 'utf8')).publish_nuget_code).toBe('NU1301');
    expect(call('--historical-report', codeOutput, 'first').stdout).toContain(
      'publish_nuget_code=NU1301',
    );
    fs.truncateSync(publish, 8 * 1024 * 1024 + 1);
    const captured = call('--historical-capture', temporary, output, '7');
    expect(captured.stdout).toBe('');
    expect(captured.stderr).toBe('');
    const value = JSON.parse(fs.readFileSync(output, 'utf8'));
    expect(value).toMatchObject({
      child_exit: 7,
      clean_source_error: 'compiler',
      publish: 'oversized',
      verifier: 'unavailable',
      warning_policy: true,
      receipt_single_line: true,
      receipt_prefix: true,
      publish_nuget_code: 'unknown',
    });
    expect(fs.statSync(output).size).toBeLessThanOrEqual(1024);
    const report = call('--historical-report', output, 'first');
    expect(report.stdout).toContain('role=first observer=available child_exit=7');
    expect(report.stdout).not.toMatch(/private|CS1002|APR_R4_E2P_RECEIPT/u);
    expect(report.stderr).toBe('');
    for (const invalid of [
      { ...value, secret: 'private-canary' },
      { ...value, child_exit: 256 },
      { ...value, publish_error: 'private-canary' },
      { ...value, publish_nuget_code: 'NU1301 private-canary' },
    ]) {
      fs.writeFileSync(output, JSON.stringify(invalid));
      expect(call('--historical-report', output, 'first').stdout).toBe(
        'APR_R4_E2P_OBSERVER role=first observer=unavailable\n',
      );
    }
    fs.writeFileSync(output, 'x'.repeat(1025));
    expect(call('--historical-report', output, 'first').stdout).toContain('observer=unavailable');
    fs.unlinkSync(output);
    fs.symlinkSync(publish, output);
    expect(call('--historical-report', output, 'first').stdout).toContain('observer=unavailable');
  });

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
