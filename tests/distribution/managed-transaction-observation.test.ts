import { expect, test } from 'vitest';
import {
  assertTransactionOrder,
  createTransactionObservation,
  projectTransactionInventoryFailure,
  transactionInventoryStages,
  transactionInventoryErrors,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/transaction-observation.mjs';

const frame = (stage = 'control_envelope', error = 'invalid_operation') =>
  Buffer.from(`APR_TRANSACTION_INVENTORY_FAILURE stage=${stage} error=${error}\n`);
const processFailure = (changes: object = {}) => ({
  code: 1,
  signal: null,
  watchdog: false,
  overflow: false,
  stdout: Buffer.alloc(0),
  stderr: frame(),
  ...changes,
});

test('admits only authored inventory diagnostics from a normal failed process', () => {
  for (const stage of transactionInventoryStages)
    for (const error of transactionInventoryErrors)
      for (const stderr of [
        frame(stage, error),
        Buffer.from(frame(stage, error).toString().replace(/\n$/u, '\r\n')),
      ])
        expect(projectTransactionInventoryFailure(processFailure({ stderr }))).toEqual({
          stage,
          error,
          exit: 1,
          signal: null,
        });
});

test('never exposes ambiguous, private, oversized or malformed inventory frames', () => {
  const good = frame().toString();
  const hostile = [
    '',
    good + '\n',
    good + good,
    '\n' + good,
    good + 'PRIVATE_KEY /private/source',
    'PRIVATE_CONTEXT\n' + good,
    good.replace('control_envelope', 'PRIVATE_STAGE'),
    good.replace('invalid_operation', 'unexpected_private'),
    good.replace(' error=', ' extra=PRIVATE_REASON error='),
    good.trimEnd(),
    good + '\0',
    '\ufeff' + good,
    '\u001b[31m' + good,
    '\ud800' + good,
    'x'.repeat(257),
  ].map((text) => Buffer.from(text));
  hostile.push(Buffer.from([0xff]), Buffer.concat([Buffer.from([0xc0, 0xaf]), frame()]));
  for (const stderr of hostile) {
    const result = projectTransactionInventoryFailure(processFailure({ stderr }));
    expect(result).toEqual({ stage: 'unknown', error: 'unavailable', exit: 1, signal: null });
    expect(JSON.stringify(result)).not.toMatch(/PRIVATE_|private\/source|\u001b/u);
  }
});

test('a valid-looking frame cannot authenticate abnormal process or output state', () => {
  for (const changes of [
    { code: 0 },
    { code: null },
    { code: 256 },
    { code: -1 },
    { code: 'PRIVATE_EXIT' },
    { code: 1.5 },
    { signal: 'SIGTERM' },
    { signal: 'PRIVATE_SIGNAL\n' },
    { signal: undefined },
    { watchdog: true },
    { watchdog: undefined },
    { overflow: true },
    { stdout: Buffer.from('PRIVATE_BODY') },
    { stdout: '' },
    { stderr: frame().toString() },
  ]) {
    const result = projectTransactionInventoryFailure(processFailure(changes));
    expect(result.stage).toBe('unknown');
    expect(result.error).toBe('unavailable');
    expect(JSON.stringify(result)).not.toContain('PRIVATE');
  }
  expect(projectTransactionInventoryFailure(processFailure({ code: 255 })).exit).toBe(255);
  expect(projectTransactionInventoryFailure(processFailure({ signal: 'SIGTERM' })).signal).toBe(
    'SIGTERM',
  );
});

test('the bounded inventory projection fits the unchanged complete diagnostic forwarding cap', () => {
  const diagnostic = {
    external_errors: 0,
    sends: 3,
    github_requests: 999999,
    official_requests: 999999,
    official_operations: Object.fromEntries(
      ['create', 'finalize', 'list', 'metadata', 'download', 'delete', 'run-attempt'].map((key) => [
        key,
        999999,
      ]),
    ),
    artifact_objects: 2,
    sticky_writes: 18,
    node_exit: 1,
    node_signal: null,
    watchdog_fired: false,
    group_close_confirmed: true,
    host_started: true,
    host_exit: 1,
    status: 'agent_result_invalid',
    cleanup: {
      kind: 'presentation',
      payloadGone: true,
      ownedParentsGone: true,
      bridgeGone: true,
      admittedHandleClosed: true,
      runtimeGone: true,
    },
    host_signal: 'none',
    assertion_site: 'archive-child.mjs:9999',
    transaction_inventory: projectTransactionInventoryFailure(processFailure()),
  };
  expect(('APR_D5_ARCHIVE_DIAGNOSTIC ' + JSON.stringify(diagnostic)).length).toBeLessThanOrEqual(
    1024,
  );
});

function scenario(restored = false) {
  const events = [
    ['provider', null],
    ['finalized', 1],
    ['downloaded', 1],
    ['finalized', 2],
    ['downloaded', 2],
    ['sticky_write', null],
    ['sticky_read', null],
    ['finalized', 3],
    ['downloaded', 3],
  ].map(([kind, id], index) => ({ ordinal: index + 1, kind, id }));
  const common = { run: '42', head: 'head', body: 'body', publicationScope: 'scope' };
  const records = [
    {
      ...common,
      id: '1',
      kind: 'candidate',
      objectIdentity: 'candidate',
      candidate: 'candidate',
      stateEnvelope: 'next',
      previous: restored ? 'prior-state' : null,
    },
    { ...common, id: '2', kind: 'publication_intent', objectIdentity: 'intent' },
    {
      ...common,
      id: '3',
      kind: 'acceptance',
      objectIdentity: 'acceptance',
      candidate: 'candidate',
      logical: 'next-logical',
    },
  ];
  if (restored) {
    events.unshift(
      { ordinal: -1, kind: 'finalized', id: 4 },
      { ordinal: 0, kind: 'downloaded', id: 4 },
    );
    events.forEach((event, index) => {
      event.ordinal = index + 1;
    });
    records.push(
      {
        ...common,
        run: '41',
        head: 'prior',
        id: '10',
        kind: 'candidate_copy',
        objectIdentity: 'copy',
        candidate: 'prior-candidate',
        stateEnvelope: 'prior-state',
      } as any,
      {
        ...common,
        run: '41',
        head: 'prior',
        id: '11',
        kind: 'acceptance',
        objectIdentity: 'prior-acceptance',
        candidate: 'prior-candidate',
        logical: 'prior-logical',
      },
      {
        ...common,
        id: '4',
        kind: 'context_claim',
        objectIdentity: 'claim',
        acceptance: 'prior-acceptance',
        logical: 'prior-logical',
        stateEnvelope: 'prior-state',
      } as any,
    );
  }
  return { events, records, options: { head: 'head', run: 42, restored } };
}

test('authenticates the distinct fresh and restored ordering obligations', () => {
  for (const restored of [false, true]) {
    const s = scenario(restored);
    const receipt = assertTransactionOrder(s.events, s.records, s.options);
    expect(receipt.authenticated_transaction_order).toBe(true);
    expect(receipt.restored_source_claim).toBe(restored);
    expect(receipt.observation_sha256).toMatch(/^[a-f0-9]{64}$/);
  }
});

test.each(['candidate', 'publication_intent', 'acceptance'])(
  'rejects missing or wrongly classified %s',
  (kind) => {
    const s = scenario();
    s.records.find((record) => record.kind === kind)!.kind = 'unclassified';
    expect(() => assertTransactionOrder(s.events, s.records, s.options)).toThrow();
  },
);

test.each([1, 2, 3])(
  'rejects a provisional SDK result without original REST body delivery for object%s',
  (id) => {
    const s = scenario();
    s.events = s.events.filter((event) => !(event.kind === 'downloaded' && event.id === id));
    expect(() => assertTransactionOrder(s.events, s.records, s.options)).toThrow();
  },
);

test('rejects acceptance before publication readback and a read from before the write', () => {
  for (const ordinal of [1, 10]) {
    const s = scenario();
    s.events.find((event) => event.kind === 'sticky_read')!.ordinal = ordinal;
    expect(() => assertTransactionOrder(s.events, s.records, s.options)).toThrow();
  }
});

test('rejects claim after dispatch, foreign acceptance binding and changed predecessor state', () => {
  for (const mutation of ['late-claim', 'foreign-acceptance', 'changed-state']) {
    const s = scenario(true);
    if (mutation === 'late-claim')
      s.events.find((event) => event.kind === 'downloaded' && event.id === 4)!.ordinal = 99;
    if (mutation === 'foreign-acceptance')
      (s.records.find((record) => record.kind === 'context_claim') as any).acceptance = 'foreign';
    if (mutation === 'changed-state')
      s.records.find((record) => record.kind === 'candidate')!.previous = 'changed';
    expect(() => assertTransactionOrder(s.events, s.records, s.options)).toThrow();
  }
});

test('retains original bytes after deletion from the service and separates case observations', () => {
  const bytes = Buffer.from('original SDK ZIP'),
    metadata = { name: 'PRIVATE_NAME', workflow_run: { id: 42 } };
  const observer = createTransactionObservation();
  observer.begin([]);
  observer.finalized(1, { bytes, metadata });
  bytes[0] ^= 1;
  expect(Buffer.from(observer.entries()[0].archive, 'base64').toString()).toBe('original SDK ZIP');
  expect(observer.events()).toEqual([{ ordinal: 1, kind: 'finalized', id: 1 }]);
  observer.begin([]);
  expect(observer.entries()).toEqual([]);
  expect(observer.events()).toEqual([]);
});
