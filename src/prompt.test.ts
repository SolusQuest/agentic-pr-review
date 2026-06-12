import { describe, expect, it } from 'vitest';
import { buildReviewPrompt } from './prompt.js';

describe('buildReviewPrompt', () => {
  it('builds deterministic public-safe prompt text', () => {
    const prompt = buildReviewPrompt(
      {
        mode: 'synthetic-fixture',
        title: 'Synthetic',
        baseSha: 'base',
        headSha: 'head',
        changedFiles: [{ filename: 'file.md', status: 'modified', patch: '+hello' }],
      },
      'bootstrap',
      [
        {
          name: 'instructions',
          source: 'input',
          text: 'review carefully',
          bytes: 16,
          sha256: 'hash',
        },
      ],
      1000,
    );
    expect(prompt.text).toContain('review carefully');
    expect(prompt.text).not.toMatch(/SECRET_TOKEN|non-public fixture marker/i);
  });
});
