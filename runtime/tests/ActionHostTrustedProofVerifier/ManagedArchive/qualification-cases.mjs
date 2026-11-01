// Exact-pair additions use the existing archive consumer, not a second runner.
import { QUALIFICATION_SCOPE } from '../../../../scripts/release/finalized-pair.format.mjs';

const groups = {
  review: 'fresh-restored-review',
  failure: 'incomplete-capacity-cancel-failure',
  reset: 'reset-stale-denied',
  state: 'publication-state-ordering',
  loading: 'managed-loading',
};
const cases = [
  ['fresh', 'review'],
  ['same-head-recovery', 'state'],
  ['restored-second-head', 'review'],
  ['restored-third-head', 'review'],
  ...['provider-failure', 'missing-usage', 'malformed-final', 'incomplete-tools', 'cancel'].map(
    (name) => [name, 'failure'],
  ),
  ['stale', 'reset'],
  ...[
    'known-commit-crash',
    'known-commit-recovery',
    'publication-refused',
    'publication-readback-mismatch',
    'acceptance-refused',
    'acceptance-crash',
    'acceptance-crash-recovery',
    'acceptance-cancel',
    'acceptance-cancel-recovery',
  ].map((name) => [name, 'state']),
  ...['tree', 'runtime', 'record', 'map', 'asset'].map((name) => ['whole-tree-' + name, 'loading']),
  ['corrupt-context-artifact', 'reset'],
  ['bad-redirect', 'failure'],
  ['wrong-archive', 'failure'],
  ['wrongKey', 'reset', 'wrong-key'],
  ['noProvider', 'failure', 'no-provider'],
  ['substitution', 'loading'],
  ['unsupportedRuntime', 'loading', 'unsupported-runtime'],
  ['genuine-old-session-auto-refusal', 'reset'],
  ['unauthorized-reset-refusal', 'reset'],
  ['authorized-reset-fresh-solus', 'reset'],
  ['same-reset-invocation-recovery', 'reset'],
  ...['automatic', 'manual'].flatMap((variant) => [
    ['h1-' + variant + '-bootstrap', 'review'],
    ['h1-' + variant + '-manual-continuation', 'review'],
    ['h1-' + variant + '-explicit-reset', 'reset'],
  ]),
  ['useful-six-call-tools', 'read-only-tools'],
  ['useful-six-restored', 'review'],
  ...[
    'managed-tool-capacity',
    'managed-call-capacity',
    'managed-batch-capacity',
    'smaller-call-limit',
    'oversized-call-limit',
    'small-output-budget',
    'missing-cache-partition',
  ].map((name) => [name, 'failure']),
];
export const originalCases = Object.freeze(
  cases.map(([name, group, id = name]) =>
    Object.freeze({ name, id: 'case-' + id, group: groups[group] ?? group }),
  ),
);
export const ORIGINAL_PAIR_CASE_COUNT = 50;
if (originalCases.length !== ORIGINAL_PAIR_CASE_COUNT) throw new Error('qualification_case_drift');
export const qualificationChecks = Object.freeze(
  [
    ...originalCases.map(({ id, group }) => ({ id, group })),
    { id: 'pair-authentication', group: 'original-archive' },
    { id: 'original-cli', group: 'original-archive' },
    { id: 'composite-runner-reuse', group: 'composite-assets' },
    { id: 'composite-eight-assets', group: 'composite-assets' },
    { id: 'runtime-only', group: 'trusted-runtime-no-sdk' },
    { id: 'full-managed-loading', group: 'managed-loading' },
    { id: 'host-state-ordering', group: 'publication-state-ordering' },
    { id: 'output-summary-parity', group: 'outputs-accounting-parity' },
    { id: 'denied-external-egress', group: 'external-confinement' },
    { id: 'service-sdk-settlement', group: 'external-confinement' },
  ]
    .sort((a, b) => (a.id < b.id ? -1 : 1))
    .map(Object.freeze),
);
export const qualificationManifest = Object.freeze({
  formatVersion: 1,
  kind: 'r7-qualification-checks',
  scope: QUALIFICATION_SCOPE,
  checks: qualificationChecks,
});
