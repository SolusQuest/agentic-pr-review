import { describe, expect, it } from 'vitest';

import { readAndMaskActionInputs, type ActionInputToolkit } from './inputs.js';

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

describe('W1 Action input acquisition', () => {
  it('reads only the seven names, preserves whitespace, and masks each secret immediately', () => {
    const input = toolkit({
      'github-token': ' github ',
      'provider-api-key': 'provider',
      'state-key': 'state',
      'previous-state-key': 'previous',
      'config-path': ' .github/reviewer.yml ',
      'pr-number': '9223372036854775807',
      'state-mode': 'reset',
    });
    expect(readAndMaskActionInputs(input.toolkit)).toEqual({
      github_token: ' github ',
      provider_api_key: 'provider',
      state_key: 'state',
      previous_state_key: 'previous',
      config_path: ' .github/reviewer.yml ',
      pr_number: '9223372036854775807',
      state_mode: 'reset',
    });
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

  it('emits explicit nulls and defaults an empty state mode to auto', () => {
    const input = toolkit({});
    expect(readAndMaskActionInputs(input.toolkit)).toEqual({
      github_token: null,
      provider_api_key: null,
      state_key: null,
      previous_state_key: null,
      config_path: null,
      pr_number: null,
      state_mode: 'auto',
    });
    expect(input.events.filter((event) => event.startsWith('mask:'))).toEqual([]);
  });

  it('masks all returned secrets before rejecting a later invalid field', () => {
    const input = toolkit({
      'github-token': 'github-canary',
      'provider-api-key': 'provider-canary',
      'state-key': 'state-canary',
      'previous-state-key': 'previous-canary',
      'config-path': 'bad\u0000path',
    });
    expect(() => readAndMaskActionInputs(input.toolkit)).toThrow('wrapper_input_invalid');
    expect(input.events.filter((event) => event.startsWith('mask:'))).toEqual([
      'mask:github-canary',
      'mask:provider-canary',
      'mask:state-canary',
      'mask:previous-canary',
    ]);
  });

  it.each(['0', '01', '9223372036854775808', '-1', '1.0'])(
    'rejects noncanonical pull request number %s',
    (prNumber) => {
      expect(() => readAndMaskActionInputs(toolkit({ 'pr-number': prNumber }).toolkit)).toThrow(
        'wrapper_input_invalid',
      );
    },
  );

  it('accepts opaque controls in secrets but rejects lone UTF-16 surrogates', () => {
    expect(readAndMaskActionInputs(toolkit({ 'state-key': 'a\u0000b' }).toolkit).state_key).toBe(
      'a\u0000b',
    );
    expect(() => readAndMaskActionInputs(toolkit({ 'state-key': '\ud800' }).toolkit)).toThrow(
      'wrapper_string_invalid',
    );
  });

  it('applies the aggregate bound after every nonempty secret has been masked', () => {
    const large = 'x'.repeat(4096);
    const input = toolkit({
      'github-token': large,
      'provider-api-key': large,
      'state-key': large,
      'previous-state-key': large,
    });
    expect(() => readAndMaskActionInputs(input.toolkit)).toThrow('wrapper_input_invalid');
    expect(input.events.filter((event) => event.startsWith('mask:'))).toHaveLength(4);
  });
});
