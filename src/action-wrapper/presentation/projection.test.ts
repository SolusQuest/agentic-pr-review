import { describe, expect, it } from 'vitest';
import { Buffer } from 'node:buffer';
import {
  parseCompletionDocument,
  renderStepSummary,
  MAXIMUM_RENDERED_SUMMARY_BYTES,
  RESULT_PROTOCOL,
} from './completion.js';
import { ACTION_OUTPUT_NAMES, projectCompletionOutputs } from './outputs.js';

function document() {
  return {
    protocol: RESULT_PROTOCOL,
    build_discriminator: 'r7-d0',
    process_exit_code: 0,
    outputs: Object.fromEntries(
      ACTION_OUTPUT_NAMES.map((name) => [
        name,
        name === 'status'
          ? 'supplied_status'
          : name === 'termination-reason'
            ? 'supplied_reason'
            : name.endsWith('completeness')
              ? 'supplied_completeness'
              : null,
      ]),
    ) as Record<string, string | null>,
    summary: 'Supplied summary\nNo inferred fields',
    annotations: [] as { code: string; severity: string; message: string }[],
  };
}
function parse(value: unknown, exit = 0) {
  return parseCompletionDocument(Buffer.from(JSON.stringify(value)), 'r7-d0', exit);
}
describe('P1 final projection transport', () => {
  it('forwards supplied scalar product values without status tables or numeric conversion', () => {
    const value = document();
    value.outputs['input-tokens'] = '9223372036854775807';
    value.outputs['provider-retries'] = 'a safe supplied string';
    const parsed = parse(value);
    expect(projectCompletionOutputs(parsed)).toEqual({
      status: 'supplied_status',
      'termination-reason': 'supplied_reason',
      'attempt-accounting-completeness': 'supplied_completeness',
      'usage-completeness': 'supplied_completeness',
      'input-tokens': '9223372036854775807',
      'provider-retries': 'a safe supplied string',
    });
    expect(renderStepSummary(parsed)).not.toContain('9223372036854775807');
  });
  it.each([
    'status',
    'termination-reason',
    'attempt-accounting-completeness',
    'usage-completeness',
  ])('rejects a null required string: %s', (name) => {
    const value = document();
    value.outputs[name] = null;
    expect(() => parse(value)).toThrow('wrapper_completion_invalid');
  });
  it.each(['protocol', 'build', 'exit', 'extra', 'missing', 'scalar', 'severity', 'control'])(
    'rejects malformed transport %s',
    (kind) => {
      const value = document();
      if (kind === 'protocol') value.protocol = 'wrong' as typeof RESULT_PROTOCOL;
      if (kind === 'build') value.build_discriminator = 'other';
      if (kind === 'exit') value.process_exit_code = 1;
      if (kind === 'extra') value.outputs.extra = 'x';
      if (kind === 'missing') delete value.outputs['input-tokens'];
      if (kind === 'scalar') (value.outputs as Record<string, unknown>)['input-tokens'] = 1;
      if (kind === 'severity')
        value.annotations = [{ code: 'supplied', severity: 'info', message: 'message' }];
      if (kind === 'control') value.summary = 'bad\u0000text';
      expect(() => parse(value)).toThrow('wrapper_completion_invalid');
    },
  );
  it('renders supplied markup as literal text with bounded escaping amplification', () => {
    const value = document();
    value.summary = '<script>bad</script>\n[link](javascript:bad) & "quoted"';
    const rendered = renderStepSummary(parse(value));
    expect(rendered).toBe(
      '<pre>&lt;script&gt;bad&lt;/script&gt;\n[link](javascript:bad) &amp; &quot;quoted&quot;</pre>\n',
    );
    expect(rendered).not.toContain('<script>');
    value.summary = '&'.repeat(10_000);
    const large = renderStepSummary(parse(value));
    expect(Buffer.byteLength(large)).toBe(50_012);
    expect(Buffer.byteLength(large)).toBeLessThanOrEqual(MAXIMUM_RENDERED_SUMMARY_BYTES);
  });
});
