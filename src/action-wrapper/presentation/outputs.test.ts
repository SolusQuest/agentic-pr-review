import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import {
  parseCompletionDocument,
  renderStepSummary,
  type ActionHostCompletionDocument,
} from './completion.js';
import { ACTION_OUTPUT_NAMES, projectCompletionOutputs } from './outputs.js';

const mapping = {
  status: 'Status',
  'termination-reason': 'Review termination',
  'model-calls': 'Model calls',
  'provider-attempts': 'Provider attempts',
  'provider-retries': 'Provider retries',
  'provider-failed-attempts': 'Failed provider attempts',
  'provider-unknown-usage-attempts': 'Attempts with unknown usage',
  'provider-unknown-cache-partition-attempts': 'Attempts with unknown cache partition',
  'attempt-accounting-completeness': 'Attempt accounting completeness',
  'usage-completeness': 'Usage completeness',
  'input-tokens': 'Known input token sum',
  'input-cache-hit-tokens': 'Known cache hit token sum',
  'input-cache-miss-tokens': 'Known cache miss token sum',
  'output-tokens': 'Known output token sum',
};
const cases = JSON.parse(
  readFileSync(
    'runtime/tests/AgenticPrReview.Runtime.Tests/Host/Action/Contracts/Fixtures/provider-accounting.json',
    'utf8',
  ),
) as { name: string; valid: boolean; document: ActionHostCompletionDocument }[];

describe('H4 validated output projection', () => {
  it('freezes exactly the fourteen master names', () => {
    expect([...ACTION_OUTPUT_NAMES].sort()).toEqual(Object.keys(mapping).sort());
  });
  it.each(cases.filter((c) => c.valid))(
    'matches every known summary fact for $name',
    ({ document }) => {
      const parsed = parseCompletionDocument(
        Buffer.from(JSON.stringify(document)),
        'r4-h1',
        document.process_exit_code,
      );
      const output = projectCompletionOutputs(parsed);
      const summary = renderStepSummary(parsed);
      for (const [name, label] of Object.entries(mapping)) {
        const value =
          name === 'status'
            ? document.status
            : name === 'termination-reason'
              ? document.termination_reason
              : document.accounting[name.replaceAll('-', '_') as keyof typeof document.accounting];
        expect(summary).toContain(`| ${label} | ${value ?? 'Not available'} |`);
        if (value === null) expect(output).not.toHaveProperty(name);
        else expect(output).toHaveProperty(name, value);
      }
      expect(Object.keys(output).every((name) => Object.hasOwn(mapping, name))).toBe(true);
      expect(JSON.stringify(output)).not.toMatch(
        /CANARY|SESSION|github.com|reviewed_sha|publication_url/,
      );
    },
  );
});
