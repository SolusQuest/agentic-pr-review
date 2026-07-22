import { describe, expect, it } from 'vitest';
import { isAllowedGitStatePath } from './github-state-paths.js';

describe('Git state path grammar', () => {
  it('accepts only the frozen M4 state paths', () => {
    const digest = 'a'.repeat(64);
    expect(isAllowedGitStatePath('m4-state/v1/store.json')).toBe(true);
    expect(isAllowedGitStatePath(`m4-state/v1/candidates/${digest}/manifest.json`)).toBe(true);
    expect(isAllowedGitStatePath(`m4-state/v1/states/${digest}/selectors/current.json`)).toBe(true);
    expect(isAllowedGitStatePath(`m4-state/v1/markers/${digest}.json`)).toBe(true);
    expect(isAllowedGitStatePath(`m4-state/v1/receipts/${digest}/12-1.json`)).toBe(true);
    expect(isAllowedGitStatePath('m4-state/v1/unbounded/user-input.json')).toBe(false);
    expect(isAllowedGitStatePath(`m4-state/v1/candidates/${digest}/extra.json`)).toBe(false);
  });
});
