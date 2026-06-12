import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';

describe('action contract', () => {
  const action = readFileSync('.github/actions/agentic-pr-review/action.yml', 'utf8');

  it('uses nested node24 JavaScript action', () => {
    expect(action).toContain('using: node24');
    expect(action).toContain('main: dist/index.js');
  });

  it('declares core public inputs and outputs', () => {
    for (const name of [
      'runtime_provider',
      'target_mode',
      'review_mode',
      'api_key_mode',
      'debug_acknowledgement',
      'artifact_name',
      'review_markdown_path',
    ]) {
      expect(action).toContain(name);
    }
  });
});
