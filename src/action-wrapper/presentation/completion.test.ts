import { Buffer } from 'node:buffer';
import { describe, expect, it } from 'vitest';
import { parseCompletionDocument, renderStepSummary } from './completion.js';
import { projectionFixture } from './projection-fixtures.js';

const bytes = () => Buffer.from(JSON.stringify(projectionFixture('status-reviewed')));
describe('P1 strict final projection wire boundary', () => {
  it.each([
    Buffer.alloc(0),
    Buffer.of(0xff),
    Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), bytes()]),
    Buffer.from('null'),
    Buffer.from('[]'),
    Buffer.concat([bytes(), Buffer.from('{}')]),
    Buffer.from(
      bytes().toString().replace('"protocol":', '"protocol":"apr.cli.result.cr2","protocol":'),
    ),
    Buffer.from(bytes().toString().replace('"status":', '"\\u0073tatus":"reviewed","status":')),
  ])('rejects malformed or duplicate JSON', (document) => {
    expect(() => parseCompletionDocument(document, 'r4-h1', 0)).toThrow();
  });
  it.each([
    'extra-root',
    'missing-root',
    'extra-annotation',
    'missing-annotation',
    'annotation-count',
    'exit-type',
    'summary-type',
    'output-type',
    'output-control',
    'annotation-control',
    'summary-command',
  ])('rejects unsafe shape or sinks: %s', (kind) => {
    const document = JSON.parse(bytes().toString());
    if (kind === 'extra-root') document.extra = null;
    if (kind === 'missing-root') delete document.process_exit_code;
    if (kind === 'extra-annotation')
      document.annotations = [{ code: 'safe', severity: 'error', message: 'safe', extra: null }];
    if (kind === 'missing-annotation') document.annotations = [{ code: 'safe', severity: 'error' }];
    if (kind === 'annotation-count')
      document.annotations = Array.from({ length: 2 }, () => ({
        code: 'safe',
        severity: 'error',
        message: 'safe',
      }));
    if (kind === 'exit-type') document.process_exit_code = '0';
    if (kind === 'summary-type') document.summary = {};
    if (kind === 'output-type') document.outputs.status = [];
    if (kind === 'output-control') document.outputs.status = 'bad\nvalue';
    if (kind === 'annotation-control')
      document.annotations = [{ code: 'safe', severity: 'warning', message: 'bad\u0000value' }];
    if (kind === 'summary-command') document.summary = '::error::bad';
    expect(() =>
      parseCompletionDocument(Buffer.from(JSON.stringify(document)), 'r4-h1', 0),
    ).toThrow();
  });
  it('accepts the exact frame cap and rejects cap plus one', () => {
    const atCap = Buffer.from(
      bytes()
        .toString()
        .padEnd(16 * 1024, ' '),
    );
    expect(parseCompletionDocument(atCap, 'r4-h1', 0).process_exit_code).toBe(0);
    expect(() =>
      parseCompletionDocument(Buffer.concat([atCap, Buffer.of(32)]), 'r4-h1', 0),
    ).toThrow();
  });
  it('does not construct summary facts from outputs', () => {
    const value = projectionFixture('status-reviewed');
    const supplied = { ...value, summary: 'The producer supplied this text.' };
    expect(
      renderStepSummary(parseCompletionDocument(Buffer.from(JSON.stringify(supplied)), 'r4-h1', 0)),
    ).toBe('<pre>The producer supplied this text.</pre>\n');
  });
});
