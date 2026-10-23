// Immutable Action source under test. A proof-only workflow commit may follow it.
export const actionSource = 'e96876af96255e54f1097fe19d4fd51ef23ddb55';
export const repository = 'SolusQuest/agentic-pr-review';
export const assets = [
  'action.yml',
  'payload-map.json',
  'internal/action.yml',
  'internal/prepare/action.yml',
  'internal/dist/index.js',
  'internal/dist/sdk-upload.js',
  'internal/dist/prepare.js',
  'internal/dist/cleanup.js',
];
export const cases = ['valid', 'invalid-input', 'unbound', 'missing-capability'];
