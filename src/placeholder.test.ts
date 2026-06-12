import { describe, it, expect } from 'vitest';
import { placeholder } from './placeholder.js';

describe('placeholder', () => {
  it('returns skeleton string', () => {
    expect(placeholder()).toBe('agentic-pr-review skeleton');
  });
});
