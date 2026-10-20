import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, test } from 'vitest';

const shell = readFileSync('runtime/scripts/verify-live-agent.sh', 'utf8');
const start = '// BEGIN APR_R3_TRUSTED_LIVE_TEST_PROJECTION';
const end = '// END APR_R3_TRUSTED_LIVE_TEST_PROJECTION';
const region = shell.slice(shell.indexOf(start), shell.indexOf(end));
// Execute the actual inline projector; no mirrored implementation.
const project = new Function(
  'bytes',
  region + '\nreturn projectTrustedLiveTestFailures(bytes);',
) as (bytes: Buffer) => string[];
const unknown = ['APR_R3_TRUSTED_LIVE_TEST_FAILURES_UNKNOWN'];
const first = 'SupervisorSigtermTerminatesChildAndCleansSensitiveRoot';
const second = 'QualityFailureProjectionSurvivesTransportProofWrapper';
const line = (method: string, parameters = '') =>
  `[xUnit.net 00:00:10.00]     AgenticPrReview.Runtime.Tests.Agent.Core.TrustedLiveAgentTests.${method}${parameters} [FAIL]\n`;

describe('actual inline trusted-live failure projection', () => {
  test('pins exactly the independently declared current test methods', () => {
    expect(shell.split(start)).toHaveLength(2);
    expect(shell.split(end)).toHaveLength(2);
    const fixed = JSON.parse(region.match(/new Set\((\[[\s\S]*?\])\)/u)![1]);
    const source = readFileSync(
      'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Core/TrustedLiveAgentTests.cs',
      'utf8',
    );
    let previous = 0;
    const actual: string[] = [];
    for (const match of source.matchAll(/public\s+(?:async\s+)?(?:Task|void)\s+(\w+)\(/gu)) {
      const prefix = source.slice(previous, match.index);
      previous = match.index! + match[0].length;
      if (/^\s*\[(?:Fact|Theory)(?:\(|\])/mu.test(prefix)) actual.push(match[1]);
    }
    expect(fixed).toEqual(actual.sort());
    expect(fixed).toHaveLength(33);
  });
  test('reports a real failed identifier without private theory values or paths', () => {
    const result = project(
      Buffer.from(line(first, '(value: "PRIVATE_SECRET /private/fake/token")')),
    );
    expect(result).toEqual(['APR_R3_TRUSTED_LIVE_CASE_FAILED ' + first]);
    expect(result.join()).not.toMatch(/PRIVATE_SECRET|private\/fake/u);
  });
  test('deduplicates and sorts actual declared identifiers', () => {
    expect(project(Buffer.from(line(first) + line(second) + line(first)))).toEqual(
      [first, second].sort().map((method) => 'APR_R3_TRUSTED_LIVE_CASE_FAILED ' + method),
    );
  });
  test.each([
    '',
    'arbitrary SECRET /private/token\n',
    line('UndeclaredSecretIdentifier'),
    line(first).replace('TrustedLiveAgentTests', 'OtherClass'),
    'secret-prefix ' + line(first),
    line(first) + line('Unowned'),
    '\u001b[31m' + line(first),
    line(first, '(value: "\u0000SECRET")'),
    line(first, '(value: "' + 'x'.repeat(8193) + '")'),
    'x'.repeat(1048577),
  ])('suppresses unknown, sensitive or unbounded input %#', (text) => {
    expect(project(Buffer.from(text))).toEqual(unknown);
  });
  test('rejects invalid UTF8 without replacement decoding', () => {
    expect(project(Buffer.from([0xff, 0xfe]))).toEqual(unknown);
  });
  test('keeps bounded failure projection on the real current component route', () => {
    const body = shell.slice(
      shell.indexOf('run_current_components()'),
      shell.lastIndexOf('\ncase '),
    );
    expect(body).toContain('FullyQualifiedName~TrustedLiveAgentTests');
    expect(body).toContain('FullyQualifiedName~LiveAgentFreshProcessTests');
    expect(body).toContain('scripts/run-native-tests.mjs runtime');
    expect(body).toContain('_project_component_failures');
    expect(body).toContain('_fail APR_R3_CURRENT_COMPONENTS_FAILED');
    expect(body).toContain("printf '%s\\n' APR_R3_CURRENT_COMPONENTS_OK");
  });
});

describe('actual component suite failure categories', () => {
  test.each([
    [
      '[xUnit.net 00:00:01.00]     AgenticPrReview.Runtime.Tests.Application.LiveAgentFreshProcessTests.Example(value: "PRIVATE_SECRET") [FAIL]\n',
      'fresh_process',
    ],
    [
      '[xUnit.net 00:00:01.00]     AgenticPrReview.Runtime.Tests.Agent.Core.LiveAgentVerifierContractTests.Example(value: "/private/path") [FAIL]\n',
      'historical_contract',
    ],
    [
      'untrusted AgenticPrReview.Runtime.Tests.Application.LiveAgentFreshProcessTests.Example [FAIL]\n',
      '',
    ],
  ])('emits only one fixed nonreflective category', (log, category) => {
    const start = shell.indexOf('_project_component_suites() {');
    const stop = shell.indexOf('\nrun_current_components() {', start);
    expect(start).toBeGreaterThan(0);
    expect(stop).toBeGreaterThan(start);
    const directory = mkdtempSync(path.join(os.tmpdir(), 'apr-r3-categories-'));
    try {
      const input = path.join(directory, 'input.log');
      writeFileSync(input, log);
      const result = spawnSync(
        '/bin/bash',
        [
          '-c',
          shell.slice(start, stop) + '\n_project_component_suites "$1"',
          'category-test',
          input,
        ],
        { encoding: 'utf8' },
      );
      expect(result.status).toBe(0);
      expect(result.stderr).toBe('');
      expect(result.stdout).toBe(
        category ? 'APR_R3_COMPONENT_SUITE_FAILED ' + category + '\n' : '',
      );
      expect(result.stdout).not.toMatch(/PRIVATE_SECRET|private\/path/u);
    } finally {
      rmSync(directory, { recursive: true, force: true });
    }
  });
});
