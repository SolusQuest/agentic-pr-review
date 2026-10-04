import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { parseCompletionDocument, renderStepSummary } from './completion.js';

const cases = JSON.parse(
  readFileSync(
    'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
    'utf8',
  ),
) as { name: string; valid: boolean; document: Record<string, unknown> }[];

describe('H2 shared accounting contract', () => {
  it.each(cases)('$name', ({ valid, document }) => {
    const parse = () =>
      parseCompletionDocument(
        Buffer.from(JSON.stringify(document)),
        'r4-h1',
        document.process_exit_code as number,
      );
    if (!valid) {
      expect(parse).toThrow('wrapper_completion_invalid');
      return;
    }
    const completion = parse();
    expect(completion.accounting).toEqual(document.accounting);
    expect(completion.termination_reason).toBe(document.termination_reason);
    const summary = renderStepSummary(completion);
    expect(summary).toContain(`| Review termination | ${completion.termination_reason} |`);
    expect(summary).toContain(
      `| Provider attempts | ${completion.accounting.provider_attempts ?? 'Not available'} |`,
    );
    expect(summary).toContain(
      `| Failed provider attempts | ${completion.accounting.provider_failed_attempts ?? 'Not available'} |`,
    );
    expect(summary).toContain(
      `| Known output token sum | ${completion.accounting.output_tokens ?? 'Not available'} |`,
    );
    expect(summary).not.toMatch(/CANARY|::/);
  });

  it('requires root members and rejects escaped duplicate facts', () => {
    const document = cases[0]!.document;
    for (const key of ['accounting', 'termination_reason']) {
      const copy = { ...document };
      delete copy[key];
      expect(() =>
        parseCompletionDocument(Buffer.from(JSON.stringify(copy)), 'r4-h1', 0),
      ).toThrow();
    }
    const duplicate = JSON.stringify(document).replace(
      '"model_calls":"1"',
      '"model_calls":"1","\\u006dodel_calls":"1"',
    );
    expect(() => parseCompletionDocument(Buffer.from(duplicate), 'r4-h1', 0)).toThrow();
  });
});
