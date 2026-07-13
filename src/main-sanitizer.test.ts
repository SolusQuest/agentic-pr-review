import { describe, expect, it } from 'vitest';
import { sanitizeRuntimeDiagnostic } from './main.js';

describe('sanitizeRuntimeDiagnostic', () => {
  it('redacts paths, auth headers, API headers, and credential-shaped tokens', () => {
    const value = [
      '/tmp/secret.txt',
      'single-segment',
      'C:\\Users\\runner\\secret.txt',
      '\\\\server\\share\\secret.txt',
      '\\\\?\\C:\\extended\\secret.txt',
      'Authorization: secret',
      'Authorization: Bearer secret',
      'x-api-token: token-value',
      'ghp_one github_pat_two gho_three ghu_four ghs_five ghr_six sk-secret',
    ].join(' | ');
    const sanitized = sanitizeRuntimeDiagnostic(value, ['single-segment']);

    expect(sanitized).not.toContain('/tmp/secret.txt');
    expect(sanitized).not.toContain('C:\\Users\\runner\\secret.txt');
    expect(sanitized).not.toContain('server\\share\\secret.txt');
    expect(sanitized).not.toContain('Authorization: secret');
    expect(sanitized).not.toContain('Bearer secret');
    expect(sanitized).not.toContain('token-value');
    expect(sanitized).not.toMatch(
      /ghp_one|github_pat_two|gho_three|ghu_four|ghs_five|ghr_six|sk-secret/,
    );
    expect(sanitized).toContain('<path>');
  });

  it.each([
    ['bracketed path', 'path=[/home/runner/work/private/file]', 'private/file'],
    ['braced token', 'token={ghp_example_token}', 'ghp_example_token'],
    ['equals authorization', 'Authorization=Bearer secret-value', 'Bearer secret-value'],
    ['equals API token', 'x-api-token=secret-token', 'secret-token'],
  ])('redacts %s', (_label, value, leakedValue) => {
    const sanitized = sanitizeRuntimeDiagnostic(value);

    expect(sanitized).not.toContain(leakedValue);
  });
});
