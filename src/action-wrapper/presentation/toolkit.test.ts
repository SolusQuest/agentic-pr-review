import { projectionFixture } from './projection-fixtures.js';
import { readFile } from 'node:fs/promises';
import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  getInput: vi.fn(() => ' value '),
  setSecret: vi.fn(),
  setOutput: vi.fn(),
  addRaw: vi.fn(),
  write: vi.fn(async () => undefined),
  warning: vi.fn(),
  error: vi.fn(),
}));

vi.mock('@actions/core', () => ({
  getInput: mocks.getInput,
  setSecret: mocks.setSecret,
  setOutput: mocks.setOutput,
  summary: { addRaw: mocks.addRaw, write: mocks.write },
  warning: mocks.warning,
  error: mocks.error,
}));

import { createActionsToolkit, presentCompletion, presentFixedWrapperFailure } from './toolkit.js';

beforeEach(() => vi.clearAllMocks());

describe('W1 concrete Actions presentation binding', () => {
  it('binds exact input, masking, one summary, and canonical annotation methods', async () => {
    const toolkit = createActionsToolkit();
    expect(toolkit.getInput('github-token', { trimWhitespace: false })).toBe(' value ');
    toolkit.setSecret('secret');
    await presentCompletion(toolkit, projectionFixture('status-reviewed_with_inline_warnings'));
    expect(mocks.getInput).toHaveBeenCalledWith('github-token', { trimWhitespace: false });
    expect(mocks.setSecret).toHaveBeenCalledWith('secret');
    expect(mocks.addRaw).toHaveBeenCalledTimes(1);
    expect(mocks.write).toHaveBeenCalledWith({ overwrite: true });
    expect(mocks.warning).toHaveBeenCalledWith('Some inline annotations could not be published.');
    expect(mocks.error).not.toHaveBeenCalled();
    expect(mocks.setOutput.mock.calls).toEqual([
      ['status', 'reviewed_with_inline_warnings'],
      ['termination-reason', 'review_completed'],
      ['attempt-accounting-completeness', 'unavailable'],
      ['usage-completeness', 'unavailable'],
    ]);
    expect(mocks.setOutput.mock.invocationCallOrder.at(-1)).toBeLessThan(
      mocks.addRaw.mock.invocationCallOrder[0]!,
    );
  });

  it.each(['output', 'summary', 'error'])(
    'attempts fixed sinks independently after %s failure',
    async (sink) => {
      const toolkit = createActionsToolkit();
      if (sink === 'output')
        mocks.setOutput.mockImplementationOnce(() => {
          throw new Error('PRIVATE_CANARY');
        });
      if (sink === 'summary') mocks.write.mockRejectedValueOnce(new Error('PRIVATE_CANARY'));
      if (sink === 'error')
        mocks.error.mockImplementationOnce(() => {
          throw new Error('PRIVATE_CANARY');
        });
      await expect(presentFixedWrapperFailure(toolkit)).resolves.toBeUndefined();
      expect(mocks.setOutput.mock.calls).toEqual([
        ['status', 'failed'],
        ['termination-reason', 'host_failure'],
        ['attempt-accounting-completeness', 'unavailable'],
        ['usage-completeness', 'unavailable'],
      ]);
      expect(mocks.write).toHaveBeenCalledTimes(1);
      expect(mocks.error).toHaveBeenCalledWith('The private review wrapper failed.');
    },
  );

  it('contains no annotation-producing failure API', async () => {
    const source = await readFile('src/action-wrapper/presentation/toolkit.ts', 'utf8');
    expect(source).not.toContain('set' + 'Failed');
  });
});
