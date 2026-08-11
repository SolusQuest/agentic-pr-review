import { readFile } from 'node:fs/promises';
import { describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  getInput: vi.fn(() => ' value '),
  setSecret: vi.fn(),
  addRaw: vi.fn(),
  write: vi.fn(async () => undefined),
  warning: vi.fn(),
  error: vi.fn(),
}));

vi.mock('@actions/core', () => ({
  getInput: mocks.getInput,
  setSecret: mocks.setSecret,
  summary: { addRaw: mocks.addRaw, write: mocks.write },
  warning: mocks.warning,
  error: mocks.error,
}));

import { createActionsToolkit, presentCompletion } from './toolkit.js';

describe('W1 concrete Actions presentation binding', () => {
  it('binds exact input, masking, one summary, and canonical annotation methods', async () => {
    const toolkit = createActionsToolkit();
    expect(toolkit.getInput('github-token', { trimWhitespace: false })).toBe(' value ');
    toolkit.setSecret('secret');
    await presentCompletion(toolkit, {
      build_discriminator: 'r4-h1',
      status: 'reviewed_with_inline_warnings',
      exit_class: 'success',
      process_exit_code: 0,
      summary: {
        reviewed_sha: 'a'.repeat(40),
        publication_url: 'https://github.com/o/r/pull/1',
        finding_count: 1,
        state_disposition: 'accepted',
      },
      annotations: [
        {
          code: 'inline_publication_incomplete',
          severity: 'warning',
          message: 'Some inline annotations could not be published.',
        },
      ],
    });
    expect(mocks.getInput).toHaveBeenCalledWith('github-token', { trimWhitespace: false });
    expect(mocks.setSecret).toHaveBeenCalledWith('secret');
    expect(mocks.addRaw).toHaveBeenCalledTimes(1);
    expect(mocks.write).toHaveBeenCalledWith({ overwrite: true });
    expect(mocks.warning).toHaveBeenCalledWith('Some inline annotations could not be published.');
    expect(mocks.error).not.toHaveBeenCalled();
  });

  it('contains no stable output or annotation-producing failure API', async () => {
    const source = await readFile('src/action-wrapper/presentation/toolkit.ts', 'utf8');
    expect(source).not.toContain('set' + 'Output');
    expect(source).not.toContain('set' + 'Failed');
  });
});
