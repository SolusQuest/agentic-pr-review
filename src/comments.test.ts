import { describe, expect, it } from 'vitest';
import { buildStickyComment, STICKY_COMMENT_MARKER } from './comments.js';

describe('sticky comment', () => {
  it('uses generic markers', () => {
    const comment = buildStickyComment(
      {
        mode: 'synthetic-fixture',
        title: 'Synthetic',
        baseSha: 'base',
        headSha: 'head',
        changedFiles: [],
      },
      'No findings.',
      'state-key',
    );
    expect(comment).toContain(STICKY_COMMENT_MARKER);
    expect(comment).toContain('agentic-pr-review:meta');
  });
});
