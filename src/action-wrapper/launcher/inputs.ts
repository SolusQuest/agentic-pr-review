import {
  boundedSecret,
  boundedStructure,
  canonicalPositiveDecimal,
  fail,
  utf8Length,
} from './validation.js';

export const ACTION_INPUT_NAMES = Object.freeze([
  'github-token',
  'provider-api-key',
  'state-key',
  'previous-state-key',
  'config-path',
  'pr-number',
  'state-mode',
] as const);

const SECRET_INPUT_NAMES = new Set<string>([
  'github-token',
  'provider-api-key',
  'state-key',
  'previous-state-key',
]);

export interface ActionInputToolkit {
  getInput(name: string, options: { readonly trimWhitespace: false }): string;
  setSecret(secret: string): void;
}

export interface ActionHostInputsDocument {
  readonly github_token: string | null;
  readonly provider_api_key: string | null;
  readonly state_key: string | null;
  readonly previous_state_key: string | null;
  readonly config_path: string | null;
  readonly pr_number: string | null;
  readonly state_mode: 'auto' | 'reset';
}

export function readAndMaskActionInputs(toolkit: ActionInputToolkit): ActionHostInputsDocument {
  const values = new Map<string, string>();
  for (const name of ACTION_INPUT_NAMES) {
    let value: string;
    try {
      value = toolkit.getInput(name, { trimWhitespace: false });
      if (SECRET_INPUT_NAMES.has(name) && value.length > 0) toolkit.setSecret(value);
    } catch {
      fail('wrapper_input_unavailable');
    }
    values.set(name, value);
  }

  const stateModeRaw = values.get('state-mode') || 'auto';
  const present = ACTION_INPUT_NAMES.flatMap((name) => {
    const value = name === 'state-mode' ? stateModeRaw : values.get(name)!;
    return value.length === 0 ? [] : [{ name, value }];
  });
  const aggregateBytes = present.reduce(
    (total, entry) => total + utf8Length(entry.name) + utf8Length(entry.value),
    0,
  );
  if (aggregateBytes > 16 * 1024) fail('wrapper_input_invalid');

  const secret = (name: string): string | null => {
    const value = values.get(name)!;
    if (value.length === 0) return null;
    if (!boundedSecret(value, 4 * 1024)) fail('wrapper_input_invalid');
    return value;
  };
  const configPath = values.get('config-path')!;
  if (configPath.length > 0 && !boundedStructure(configPath, 1024)) {
    fail('wrapper_input_invalid');
  }
  const prNumber = values.get('pr-number')!;
  if (prNumber.length > 0 && !canonicalPositiveDecimal(prNumber, 19, 9_223_372_036_854_775_807n)) {
    fail('wrapper_input_invalid');
  }
  if (stateModeRaw !== 'auto' && stateModeRaw !== 'reset') fail('wrapper_input_invalid');

  return {
    github_token: secret('github-token'),
    provider_api_key: secret('provider-api-key'),
    state_key: secret('state-key'),
    previous_state_key: secret('previous-state-key'),
    config_path: configPath || null,
    pr_number: prNumber || null,
    state_mode: stateModeRaw,
  };
}
