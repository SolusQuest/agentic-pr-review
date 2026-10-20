import { readFileSync } from 'node:fs';
import { describe, expect, test } from 'vitest';
import {
  cutoverFailureCodes,
  projectCutoverFailureCode,
} from '../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/cutover-failure-code.mjs';

const line = (code: string) => `APR_SOLUS_DIRECT_FAULTS_FAILED ${code}\n`;
const sourcePath = 'runtime/tests/SolusProductionCutoverFixture/ManagedFaultProof.cs';

describe('actual cutover assertion failure projector', () => {
  test('freezes exactly the authored Check codes, excluding fixture names', () => {
    const source = readFileSync(sourcePath, 'utf8');
    const actual = [
      ...new Set(
        [...source.matchAll(/Check\([\s\S]*?\);/gu)].flatMap((call) =>
          [
            ...call[0].matchAll(
              /"((?:body_privacy_|commit_failure_|precancel_)[a-z_]+|(?:secret|capture|incompatible)_current_stage)"/gu,
            ),
          ].map((match) => match[1]),
        ),
      ),
    ].sort();
    expect(cutoverFailureCodes).toHaveLength(48);
    expect(cutoverFailureCodes).toEqual(actual);
    for (const fixture of [
      'precancel_control',
      'precancel_fresh',
      'precancel_restore',
      'precancel_retry',
    ])
      expect(projectCutoverFailureCode(line(fixture))).toBeNull();
  });
  test.each([...cutoverFailureCodes])('projects only the fixed code %s', (code) => {
    for (const suffix of ['', '\n', '\r\n']) {
      expect(projectCutoverFailureCode(line(code).trimEnd() + suffix)).toBe(code);
      expect(projectCutoverFailureCode(Buffer.from(line(code).trimEnd() + suffix))).toBe(code);
    }
  });
  test.each([
    '',
    'PRIVATE_SECRET /private/token',
    line('body_privacy_PRIVATE_SECRET'),
    line('commit_failure_unknown'),
    line('precancel_unknown'),
    line('body_privacy_observed_requests') + line('body_privacy_observed_requests'),
    line('body_privacy_observed_requests') + line('secret_current_stage'),
    line('body_privacy_observed_requests') + line('unknown'),
    'secret-prefix ' + line('body_privacy_observed_requests'),
    line('body_privacy_observed_requests').trimEnd() + ' PRIVATE_SECRET\n',
    'PRIVATE_SECRET\n' + line('body_privacy_observed_requests'),
    line('body_privacy_observed_requests') + 'PRIVATE_SECRET\n',
    '\u001b[31m' + line('body_privacy_observed_requests'),
    line('body_privacy_observed_requests') + '\0',
    line('body_privacy_observed_requests') + '\t',
    line('body_privacy_observed_requests') + '\r',
    line('body_privacy_observed_requests') + '\n',
    'APR_SOLUS_DIRECT_FAULTS_FAILED ' + 'x'.repeat(257),
    'x'.repeat(65537),
    '\ud800' + line('body_privacy_observed_requests'),
    '\ufffd' + line('body_privacy_observed_requests'),
    line('body_privacy_observed_requests').replace('DIRECT_FAULTS', 'CUTOVER'),
  ])('refuses untrusted or ambiguous output %#', (output) => {
    expect(projectCutoverFailureCode(output)).toBeNull();
    expect(projectCutoverFailureCode(Buffer.from(output))).toBeNull();
  });
  test('refuses malformed UTF-8 and byte overflow before decoding', () => {
    expect(projectCutoverFailureCode(Buffer.from([0xc3, 0x28]))).toBeNull();
    expect(projectCutoverFailureCode(Buffer.from([0xc0, 0xaf]))).toBeNull();
    expect(projectCutoverFailureCode(Buffer.from([0xff]))).toBeNull();
    expect(
      projectCutoverFailureCode(
        Buffer.concat([
          Buffer.from([0xef, 0xbb, 0xbf]),
          Buffer.from(line('body_privacy_observed_requests')),
        ]),
      ),
    ).toBeNull();
    expect(projectCutoverFailureCode(Buffer.alloc(65537, 0x61))).toBeNull();
    expect(projectCutoverFailureCode(null)).toBeNull();
  });
  test('keeps original reset branch and failure semantics while using the actual projector', () => {
    const run = readFileSync(
      'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/cutover-proof.mjs',
      'utf8',
    );
    expect(run).toContain("import { projectCutoverFailureCode } from './cutover-failure-code.mjs'");
    expect(run).toMatch(
      /throw new CutoverSupportFailure\(\s*code \?\? projectCutoverFailureCode\(output\) \?\? 'supporting_process_failed'/u,
    );
    expect(run).toContain('reset_(?:oracle_[a-z0-9_]+|retry_inventory)');
    expect(projectCutoverFailureCode('APR_SOLUS_CUTOVER_FAILED reset_oracle_root\n')).toBeNull();
  });
});
