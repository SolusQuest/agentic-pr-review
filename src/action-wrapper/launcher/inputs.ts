import { fail } from './validation.js';

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

export interface ActionHostRawInputDocument {
  readonly name: string;
  readonly value: string;
}

export type ActionHostInputsDocument = readonly ActionHostRawInputDocument[];

export function readAndMaskActionInputs(toolkit: ActionInputToolkit): ActionHostInputsDocument {
  const entries: ActionHostRawInputDocument[] = [];
  for (const name of ACTION_INPUT_NAMES) {
    let value: string;
    try {
      value = toolkit.getInput(name, { trimWhitespace: false });
      if (SECRET_INPUT_NAMES.has(name) && value.length > 0) toolkit.setSecret(value);
    } catch {
      fail('wrapper_input_unavailable');
    }
    entries.push({ name, value });
  }
  return entries;
}
