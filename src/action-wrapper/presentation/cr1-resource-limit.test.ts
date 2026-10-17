import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { parseCompletionDocument } from './completion.js';
import { ACTION_OUTPUT_NAMES, projectCompletionOutputs } from './outputs.js';

const cases = JSON.parse(
  readFileSync(
    'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
    'utf8',
  ),
) as { name: string; document: Record<string, unknown> }[];

describe('CR1 generic runtime resource termination', () => {
  it('preserves fourteen outputs and exact incomplete termination', () => {
    const document = {
      ...cases.find((c) => c.name === 'final_failure_unknown_usage')!.document,
      termination_reason: 'resource_limit',
    };
    const completion = parseCompletionDocument(Buffer.from(JSON.stringify(document)), 'r4-h1', 1);
    const outputs = projectCompletionOutputs(completion);
    expect(ACTION_OUTPUT_NAMES).toHaveLength(14);
    expect(Object.keys(outputs).sort()).toEqual([...ACTION_OUTPUT_NAMES].sort());
    expect(outputs['termination-reason']).toBe('resource_limit');
    expect(outputs.status).toBe('provider_failed');
  });
  it('rejects a clean review with resource exhaustion and unknown spellings', () => {
    for (const termination_reason of ['resource_limit', 'resource_limit_extra']) {
      const document = { ...cases[0]!.document, termination_reason };
      expect(() =>
        parseCompletionDocument(Buffer.from(JSON.stringify(document)), 'r4-h1', 0),
      ).toThrow('wrapper_completion_invalid');
    }
  });
});
