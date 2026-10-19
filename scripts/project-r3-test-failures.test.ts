import { readFileSync } from 'node:fs';
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
const second = 'PostCommitQualityFailureSurvivesTransportProofWrapper';
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
  test('keeps the original test selection and failure code', () => {
    const body = shell.slice(
      shell.indexOf('run_live_dry_run()'),
      shell.indexOf('_audit_aot_warnings()'),
    );
    expect(body).toContain("--filter 'FullyQualifiedName~TrustedLive'");
    expect(body).toContain('_fail APR_R3_TRUSTED_LIVE_TESTS_FAILED');
    expect(body).toContain("printf '%s\\n' APR_R3_TRUSTED_LIVE_DRY_RUN_OK");
  });
});
