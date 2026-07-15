import { describe, expect, it } from 'vitest';
import { strictParseJson, StrictJsonParseError } from './strict-json.js';

describe('strictParseJson', () => {
  it('parses valid JSON objects', () => {
    expect(strictParseJson('{"a":1,"b":true}')).toEqual({ a: 1, b: true });
  });

  it('parses nested arrays/objects', () => {
    expect(strictParseJson('[{"a":[1,{"b":2}]}]')).toEqual([{ a: [1, { b: 2 }] }]);
  });

  it('tolerates trailing whitespace', () => {
    expect(strictParseJson('{"a":1}\n\t ')).toEqual({ a: 1 });
    expect(strictParseJson('{"a":1}\r\n')).toEqual({ a: 1 });
  });

  it('rejects trailing garbage after top-level value', () => {
    expect(() => strictParseJson('{"a":1} garbage')).toThrow();
  });

  it('rejects duplicate keys at top level', () => {
    expect(() => strictParseJson('{"a":1,"a":2}')).toThrow(StrictJsonParseError);
  });

  it('rejects escape-equivalent duplicate keys ("a" vs "\\u0061")', () => {
    expect(() => strictParseJson('{"a":1,"\\u0061":2}')).toThrow(StrictJsonParseError);
  });

  it('rejects duplicate keys inside nested objects', () => {
    expect(() => strictParseJson('{"outer":{"k":1,"k":2}}')).toThrow(StrictJsonParseError);
  });

  it('rejects duplicate keys inside array-of-object elements', () => {
    expect(() => strictParseJson('[{"k":1,"k":2}]')).toThrow(StrictJsonParseError);
  });

  it('allows the same key in DIFFERENT sibling objects', () => {
    const value = strictParseJson('{"a":{"k":1},"b":{"k":2}}');
    expect(value).toEqual({ a: { k: 1 }, b: { k: 2 } });
  });

  it('allows the same key in DIFFERENT array elements', () => {
    const value = strictParseJson('[{"k":1},{"k":2}]');
    expect(value).toEqual([{ k: 1 }, { k: 2 }]);
  });

  it('rejects unterminated string', () => {
    expect(() => strictParseJson('{"a":"unfinished}')).toThrow();
  });

  it('parses escapes in string values', () => {
    expect(strictParseJson('"a\\nb\\tc\\u00e9"')).toBe('a\nb\tc\u00e9');
  });
});
