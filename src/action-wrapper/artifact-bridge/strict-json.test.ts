import { describe, expect, it, vi } from 'vitest';
import {
  ArtifactBridgeStrictJsonError,
  strictParseFlatStringObject,
  strictParseArtifactBridgeJson,
} from './strict-json.js';

describe('transport flat JSON admission', () => {
  it.each([
    '{"a":[]}',
    '{"a":{"nested":"x"}}',
    '{"a":1}',
    '{"a":null}',
    '{"a":"x","\\u0061":"y"}',
    '{"a":"x","unknown":"y"}',
  ])('rejects invalid shape before parsing the whole document: %s', (document) => {
    const parse = vi.spyOn(JSON, 'parse');
    expect(() => strictParseFlatStringObject(document, ['a'], () => {})).toThrow(
      ArtifactBridgeStrictJsonError,
    );
    expect(parse.mock.calls.some(([text]) => text === document)).toBe(false);
  });

  it('handles escaped keys and quotes while retaining nested command metadata support', () => {
    const value = { a: 'backslash \\ and quote "' };
    expect(
      strictParseFlatStringObject(
        JSON.stringify(value).replace('"a"', '"\\u0061"'),
        ['a'],
        () => {},
      ),
    ).toEqual(value);
    expect(strictParseArtifactBridgeJson('{"expected":{"size":"1"}}')).toEqual({
      expected: { size: '1' },
    });
  });

  it('checks the deadline during a long scalar scan before whole-document parsing', () => {
    const document = JSON.stringify({ a: 'x'.repeat(200_000) });
    const parse = vi.spyOn(JSON, 'parse');
    let checks = 0;
    expect(() =>
      strictParseFlatStringObject(document, ['a'], () => {
        if (++checks === 2) throw new Error('expired');
      }),
    ).toThrow('expired');
    expect(parse.mock.calls.some(([text]) => text === document)).toBe(false);
  });
});
