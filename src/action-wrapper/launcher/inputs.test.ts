import { describe, expect, it } from 'vitest';

import { ACTION_INPUT_NAMES, readAndMaskActionInputs, type ActionInputToolkit } from './inputs.js';

function toolkit(values: Record<string, string>) {
  const events: string[] = [];
  const value: ActionInputToolkit = {
    getInput: (name, options) => {
      expect(options).toEqual({ trimWhitespace: false });
      events.push(`get:${name}`);
      return values[name] ?? '';
    },
    setSecret: (secret) => events.push(`mask:${secret}`),
  };
  return { toolkit: value, events };
}

describe('CLI raw Action input capture', () => {
  it('reads seven exact raw values and masks each nonempty secret immediately', () => {
    const values = {
      'github-token': ' github ',
      'provider-api-key': 'provider',
      'state-key': 'state',
      'previous-state-key': 'previous',
      'config-path': ' .github/reviewer.yml ',
      'pr-number': '9223372036854775807',
      'state-mode': 'reset',
    };
    const input = toolkit(values);
    expect(readAndMaskActionInputs(input.toolkit)).toEqual(
      ACTION_INPUT_NAMES.map((name) => ({ name, value: values[name] })),
    );
    expect(input.events).toEqual([
      'get:github-token',
      'mask: github ',
      'get:provider-api-key',
      'mask:provider',
      'get:state-key',
      'mask:state',
      'get:previous-state-key',
      'mask:previous',
      'get:config-path',
      'get:pr-number',
      'get:state-mode',
    ]);
  });

  it('captures absent inputs as empty strings without supplying defaults', () => {
    const input = toolkit({});
    expect(readAndMaskActionInputs(input.toolkit)).toEqual(
      ACTION_INPUT_NAMES.map((name) => ({ name, value: '' })),
    );
    expect(input.events.filter((event) => event.startsWith('mask:'))).toEqual([]);
  });

  it.each(['0', '01', '9223372036854775808', '-1', '1.0', ' 1 '])(
    'preserves malformed product PR %s for the C# parser',
    (value) => {
      expect(readAndMaskActionInputs(toolkit({ 'pr-number': value }).toolkit)).toContainEqual({
        name: 'pr-number',
        value,
      });
    },
  );

  it('captures rejected product evidence and masks all secrets before wire admission', () => {
    const large = 'x'.repeat(4097);
    const values = {
      'github-token': large,
      'provider-api-key': large,
      'state-key': 'a\u0000b',
      'previous-state-key': '\ud800',
      'config-path': 'bad\u0000path',
      'state-mode': 'invalid',
    };
    const input = toolkit(values);
    const captured = readAndMaskActionInputs(input.toolkit);
    for (const [name, value] of Object.entries(values)) {
      expect(captured).toContainEqual({ name, value });
    }
    expect(input.events.filter((event) => event.startsWith('mask:'))).toHaveLength(4);
  });
});
