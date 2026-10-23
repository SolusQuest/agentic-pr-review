// Immutable Action source under test. A proof-only workflow commit may follow it.
export const actionSource = '23d5ce234b90126097dd99fda0e02b39b6c2c9c4';
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
