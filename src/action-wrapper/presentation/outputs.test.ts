import { describe, expect, it } from 'vitest';
import { parseCompletionDocument, renderStepSummary } from './completion.js';
import { ACTION_OUTPUT_NAMES, projectCompletionOutputs } from './outputs.js';
import { projectionCases } from './projection-fixtures.js';

describe('P1 C#-produced fourteen-output projection', () => {
  it.each(projectionCases)('forwards the producer bytes for $name', ({ document }) => {
    const parsed = parseCompletionDocument(
      Buffer.from(JSON.stringify(document)),
      'r4-h1',
      document.process_exit_code,
    );
    expect(Object.keys(parsed.outputs).sort()).toEqual([...ACTION_OUTPUT_NAMES].sort());
    const expected = Object.fromEntries(
      Object.entries(document.outputs).filter(([, value]) => value !== null),
    );
    expect(projectCompletionOutputs(parsed)).toEqual(expected);
    const summary = renderStepSummary(parsed);
    expect(summary.startsWith('<pre>')).toBe(true);
    expect(JSON.stringify(expected)).not.toMatch(
      /CANARY|SESSION|github.com|reviewed_sha|publication_url/,
    );
  });
});
