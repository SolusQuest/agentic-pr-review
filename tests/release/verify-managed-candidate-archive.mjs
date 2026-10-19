// Test-only bridge from actual original archive qualification to the CURRENT
// candidate contract. Remote run/storage metadata below is synthetic; this is
// neither provenance verification nor preparation of an operational candidate.
import assert from 'node:assert/strict';
import {
  canonicalJson,
  inspectManagedPackage,
  sha256,
} from '../../scripts/release/build-payload.format.mjs';
import { CAPS, createCandidate, inspectCandidate } from '../../scripts/release/candidate.mjs';
import { producerFixture, storageFixture } from './fixtures.mjs';

export function verifyManagedCandidateArchive({ archive, receipt, sourceCommit, sourceTree }) {
  assert.ok(Buffer.isBuffer(archive));
  const originalDigest = sha256(archive);
  assert.equal(originalDigest, receipt.archiveSha256);
  assert.equal(receipt.identity.sourceCommit, sourceCommit);
  assert.equal(receipt.identity.sourceTree, sourceTree);
  const { manifest } = inspectManagedPackage(archive, receipt);
  const producer = { ...producerFixture(), workflowCommit: sourceCommit };
  const storage = storageFixture(producer);
  const expected = { ...receipt.identity, archiveSha256: originalDigest };
  const record = createCandidate(archive, receipt, producer, storage, expected);
  const accepted = inspectCandidate(archive, record.bytes, record.mapBytes, record.candidateSha256);
  assert.equal(accepted.receipt.archiveSha256, originalDigest);
  assert.equal(accepted.receipt.identity.buildId, manifest.buildId);
  assert.deepEqual(accepted.receipt.members, receipt.members);
  const rejects = [];
  function reject(name, run) {
    assert.throws(run, undefined, name);
    rejects.push(name);
  }
  const alteredBytes = Buffer.from(archive);
  alteredBytes[alteredBytes.length - 1] ^= 1;
  reject('original-byte-substitution', () =>
    createCandidate(alteredBytes, receipt, producer, storage, expected),
  );
  for (const [name, change] of [
    [
      'source',
      (r) => {
        r.identity.sourceCommit = 'f'.repeat(40);
      },
    ],
    [
      'tree',
      (r) => {
        r.identity.sourceTree = 'f'.repeat(40);
      },
    ],
    [
      'build',
      (r) => {
        r.identity.buildId = 'f'.repeat(64);
      },
    ],
    [
      'archive-hash',
      (r) => {
        r.archiveSha256 = 'f'.repeat(64);
      },
    ],
    [
      'member',
      (r) => {
        r.members[0].sha256 = 'f'.repeat(64);
      },
    ],
    [
      'receipt-extra-field',
      (r) => {
        r.untrusted = true;
      },
    ],
  ]) {
    const changed = structuredClone(receipt);
    change(changed);
    reject(name, () => createCandidate(archive, changed, producer, storage, expected));
  }
  const map = JSON.parse(record.mapBytes);
  map.untrusted = true;
  reject('map-extra-field', () =>
    inspectCandidate(
      archive,
      record.bytes,
      Buffer.from(canonicalJson(map)),
      record.candidateSha256,
    ),
  );
  const candidate = JSON.parse(record.bytes);
  candidate.untrusted = true;
  const changedRecord = Buffer.from(canonicalJson(candidate));
  reject('candidate-extra-field', () =>
    inspectCandidate(archive, changedRecord, record.mapBytes, sha256(changedRecord)),
  );
  reject('map-cap', () =>
    inspectCandidate(archive, record.bytes, Buffer.alloc(CAPS.map + 1), record.candidateSha256),
  );
  reject('candidate-cap', () =>
    inspectCandidate(
      archive,
      Buffer.alloc(CAPS.metadata + 1),
      record.mapBytes,
      record.candidateSha256,
    ),
  );
  assert.equal(sha256(archive), originalDigest, 'original archive mutated by validation');
  return {
    originalArchiveSha256: originalDigest,
    buildId: manifest.buildId,
    negativeCases: rejects.length,
    metadata: 'synthetic-not-provenance',
  };
}
