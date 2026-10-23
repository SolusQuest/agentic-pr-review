import { expect, test } from 'vitest';
import {
  assertTransactionOrder,
  createTransactionObservation,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/transaction-observation.mjs';

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
