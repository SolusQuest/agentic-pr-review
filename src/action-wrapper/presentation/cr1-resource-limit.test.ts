import { describe, expect, it } from 'vitest';
import { parseCompletionDocument } from './completion.js';
import { ACTION_OUTPUT_NAMES, projectCompletionOutputs } from './outputs.js';
import { projectionFixture } from './projection-fixtures.js';

describe('P1 transport preserves C# termination facts', () => {
  it('forwards resource-limit data without reclassifying accounting', () => {
    const document = projectionFixture('final_failure_unknown_usage');
    const supplied = {
      ...document,
      outputs: { ...document.outputs, 'termination-reason': 'resource_limit' },
    };
    const outputs = projectCompletionOutputs(
      parseCompletionDocument(Buffer.from(JSON.stringify(supplied)), 'r4-h1', 1),
    );
    expect(ACTION_OUTPUT_NAMES).toHaveLength(14);
    expect(Object.keys(outputs).sort()).toEqual([...ACTION_OUTPUT_NAMES].sort());
    expect(outputs['termination-reason']).toBe('resource_limit');
    expect(outputs.status).toBe('provider_failed');
  });
  it('compares the actual process exit even for a valid-looking supplied result', () => {
    const document = projectionFixture('status-reviewed');
    expect(() =>
      parseCompletionDocument(Buffer.from(JSON.stringify(document)), 'r4-h1', 1),
    ).toThrow('wrapper_completion_invalid');
  });
});
