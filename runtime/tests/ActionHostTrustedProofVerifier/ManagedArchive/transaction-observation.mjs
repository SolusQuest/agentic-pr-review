// Private original-byte observations. This journal is never a public receipt.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
const digest = (bytes) => createHash('sha256').update(bytes).digest('hex');
export function createTransactionObservation() {
  let events = [],
    originals = new Map();
  return {
    begin(snapshot) {
      events = [];
      originals = new Map(
        snapshot.map(([id, item]) => [
          id,
          {
            metadata: structuredClone(item.metadata),
            bytes: Buffer.from(item.bytes),
          },
        ]),
      );
    },
    event(kind, id = null) {
      assert.ok(events.length < 20000, 'v1_transaction_observation_bound');
      events.push({ ordinal: events.length + 1, kind, id });
    },
    finalized(id, item) {
      assert.ok(originals.size < 256);
      originals.set(id, {
        metadata: structuredClone(item.metadata),
        bytes: Buffer.from(item.bytes),
      });
      assert.ok(
        [...originals.values()].reduce((n, item) => n + item.bytes.length, 0) <= 64 * 1024 * 1024,
      );
      this.event('finalized', id);
    },
    delivered(response, kind, id = null) {
      response.once('finish', () => {
        if (response.statusCode >= 200 && response.statusCode < 300) this.event(kind, id);
      });
    },
    entries() {
      return [...originals].map(([id, item]) => ({
        id: String(id),
        name: item.metadata.name,
        archive: item.bytes.toString('base64'),
        run: String(item.metadata.workflow_run.id),
        attempt: 1,
      }));
    },
    events() {
      return structuredClone(events);
    },
    snapshot() {
      return [...originals];
    },
  };
}

export function assertTransactionOrder(events, records, { head, run, restored = false }) {
  const own = records.filter((record) => record.run === String(run));
  const one = (kind) => {
    const found = own.filter((record) => record.kind === kind && record.head === head);
    assert.equal(found.length, 1, 'v1_exact_transaction_' + kind);
    return found[0];
  };
  const candidate = one('candidate'),
    intent = one('publication_intent'),
    acceptance = one('acceptance');
  const at = (kind, id) => {
    const found = events.filter(
      (event) => event.kind === kind && (id === undefined || String(event.id) === id),
    );
    assert.ok(found.length > 0, 'v1_reached_' + kind);
    return found[0].ordinal;
  };
  const read = (record) => {
    assert.ok(
      at('finalized', record.id) < at('downloaded', record.id),
      'v1_upload_before_readback',
    );
    return at('downloaded', record.id);
  };
  const provider = at('provider');
  if (restored) {
    const claims = own.filter((record) => record.kind === 'context_claim');
    assert.equal(claims.length, 1, 'v1_exact_source_claim');
    const source = records.find(
      (record) => record.kind === 'acceptance' && record.objectIdentity === claims[0].acceptance,
    );
    assert.ok(source && source.logical === claims[0].logical, 'v1_claim_exact_accepted_source');
    assert.ok(read(claims[0]) < provider, 'v1_claim_before_provider');
    const prior = records.find(
      (record) =>
        ['candidate', 'candidate_copy'].includes(record.kind) &&
        record.candidate === source.candidate,
    );
    assert.ok(prior && prior.stateEnvelope === claims[0].stateEnvelope, 'v1_original_source_state');
    assert.equal(
      candidate.previous,
      prior.stateEnvelope,
      'v1_successor_uses_unchanged_encrypted_state',
    );
  }
  assert.ok(provider < at('finalized', candidate.id), 'v1_work_before_candidate');
  assert.ok(read(candidate) < at('finalized', intent.id), 'v1_candidate_before_intent');
  assert.ok(read(intent) < at('sticky_write'), 'v1_intent_before_publication');
  const stickyRead = events.find(
    (event) => event.kind === 'sticky_read' && event.ordinal > at('sticky_write'),
  );
  assert.ok(stickyRead, 'v1_publication_readback_reached');
  assert.ok(
    stickyRead.ordinal < at('finalized', acceptance.id),
    'v1_publication_readback_before_acceptance',
  );
  read(acceptance);
  assert.equal(acceptance.candidate, candidate.objectIdentity);
  assert.equal(intent.body, candidate.body);
  assert.equal(acceptance.body, candidate.body);
  assert.equal(intent.publicationScope, candidate.publicationScope);
  assert.equal(acceptance.publicationScope, candidate.publicationScope);
  return {
    authenticated_transaction_order: true,
    restored_source_claim: restored,
    source_state_unchanged: restored,
    observed_events: events.length,
    observation_sha256: digest(Buffer.from(JSON.stringify(events))),
  };
}
