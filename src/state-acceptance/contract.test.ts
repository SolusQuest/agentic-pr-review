import { mkdtemp, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  RecordCodecError,
  ReferenceStateStore,
  candidateLocator,
  computeCandidateId,
  computeSelectionSnapshotId,
  decodeRecord,
  encodeRecord,
  materializeMarker,
  materializeRegistration,
  materializeSelector,
  sha256Hex,
  type AcceptedStateMarkerV1,
  type CandidateBundleBytes,
  type CandidateRegistrationDraft,
  type StateKeyV2,
} from './index.js';

const stateKey: StateKeyV2 = {
  namespace: 'm4-ledger-v2',
  repository: 'SolusQuest/agentic-pr-review',
  headRepository: 'SolusQuest/agentic-pr-review',
  pullRequest: 67,
  workflowIdentity: 'm4-state',
  trustedExecutionDomain: 'trusted-default',
};
const epoch = 'A'.repeat(22) as never;
const sha = 'a'.repeat(64);
const gitSha = 'b'.repeat(40) as never;
const transition = {
  kind: 'bootstrap' as const,
  predecessorManifestSha256: 'bootstrap' as const,
  predecessorLedgerSha256: 'bootstrap' as const,
  reason: 'new_session' as const,
};

function bundle(): CandidateBundleBytes {
  return {
    manifestBytes: new TextEncoder().encode('{"manifest":true}'),
    ledgerBytes: new TextEncoder().encode('{"ledger":true}'),
    providerRunMetadataBytes: new TextEncoder().encode('{"metadata":true}'),
  };
}

function draft(overrides: Partial<CandidateRegistrationDraft> = {}): CandidateRegistrationDraft {
  const candidate = computeCandidateId({
    manifestSha256: sha,
    candidateLedgerSha256: sha,
    providerRunMetadataSha256: sha,
    metadataSemanticSha256: sha,
    consumedInputSha256: sha,
    resultSha256: sha,
    traceSha256: sha,
  });
  return {
    schemaVersion: 1,
    candidateId: candidate,
    observedSelectorRevision: 'bootstrap',
    observedSelectorSnapshotSha256: sha as never,
    predecessorMarkerId: 'bootstrap',
    predecessorManifestSha256: 'bootstrap',
    predecessorLedgerSha256: 'bootstrap',
    stateKey,
    sessionEpoch: epoch,
    stateGeneration: 0,
    ledgerEpoch: epoch,
    transition,
    interactionId: sha as never,
    interactionOrdinal: 0,
    producingRunId: '10',
    producingRunAttempt: 1,
    consumedInputSha256: sha as never,
    manifestSha256: sha as never,
    candidateLedgerSha256: sha as never,
    providerRunMetadataSha256: sha as never,
    metadataSemanticSha256: sha as never,
    resultSha256: sha as never,
    traceSha256: sha as never,
    ...overrides,
  };
}

describe('M4 state acceptance contract', () => {
  it('uses duplicate-aware canonical bytes and rejects non-canonical records', () => {
    const bytes = encodeRecord({ b: 2, a: 1 });
    expect(new TextDecoder().decode(bytes)).toBe('{"a":1,"b":2}');
    expect(decodeRecord(bytes)).toEqual({ a: 1, b: 2 });
    expect(() => decodeRecord(new TextEncoder().encode('{"a":1,"a":2}'))).toThrowError(
      RecordCodecError,
    );
    expect(() => decodeRecord(new TextEncoder().encode('{ "a": 1 }'))).toThrowError(
      expect.objectContaining({ code: 'non_canonical' }),
    );
  });

  it('freezes identity domains and the non-circular marker-to-selector order', () => {
    const candidateDraft = draft();
    const registration = materializeRegistration(candidateDraft, '1', '2026-07-20T00:00:00.000Z');
    expect(registration.candidateArtifactLocator).toEqual(
      candidateLocator(registration.candidateId),
    );
    expect(registration.candidateArtifactLocator.objectId).toHaveLength(74);

    const markerInput: Omit<AcceptedStateMarkerV1, 'markerId' | 'acceptedAt'> = {
      schemaVersion: 1,
      candidateId: registration.candidateId,
      registrationId: registration.registrationId,
      stateKey,
      sessionEpoch: epoch,
      stateGeneration: 0,
      ledgerEpoch: epoch,
      transition,
      predecessorMarkerId: 'bootstrap',
      predecessorManifestSha256: 'bootstrap',
      predecessorLedgerSha256: 'bootstrap',
      observedSelectorRevision: 'bootstrap',
      manifestSha256: sha as never,
      candidateLedgerSha256: sha as never,
      providerRunMetadataSha256: sha as never,
      metadataSemanticSha256: sha as never,
      consumedInputSha256: sha as never,
      resultSha256: sha as never,
      traceSha256: sha as never,
      producingRunId: '10',
      producingRunAttempt: 1,
      acceptingRunId: '11',
      acceptingRunAttempt: 1,
    };
    const marker = materializeMarker(markerInput, '2026-07-20T00:00:01.000Z');
    const selectorInput = {
      schemaVersion: 1,
      stateKey,
      previousSelectorRevision: 'bootstrap',
      acceptedMarkerId: marker.markerId,
      candidateId: marker.candidateId,
      sessionEpoch: marker.sessionEpoch,
      stateGeneration: marker.stateGeneration,
      ledgerEpoch: marker.ledgerEpoch,
      transition: marker.transition,
      manifestSha256: marker.manifestSha256,
      candidateLedgerSha256: marker.candidateLedgerSha256,
      providerRunMetadataSha256: marker.providerRunMetadataSha256,
      metadataSemanticSha256: marker.metadataSemanticSha256,
      consumedInputSha256: marker.consumedInputSha256,
      resultSha256: marker.resultSha256,
      traceSha256: marker.traceSha256,
      currentHeadSha: gitSha,
      currentBaseSha: gitSha,
      workflowIdentity: stateKey.workflowIdentity,
      trustedExecutionDomain: stateKey.trustedExecutionDomain,
    } as const;
    const selector = materializeSelector(selectorInput, '2026-07-20T00:00:02.000Z');
    expect(selector.previousSelectorRevision).toBe('bootstrap');
    expect(selector.selectorRevision).toMatch(/^sha256:[a-f0-9]{64}$/);
    const later = materializeSelector(selectorInput, '2027-01-01T00:00:00.000Z');
    expect(later.selectorRevision).toBe(selector.selectorRevision);
    expect(later.selectorId).toBe(selector.selectorId);
  });

  it('includes exact predecessor byte hashes in selection identity', () => {
    const bytes = bundle();
    const base = {
      schemaVersion: 1 as const,
      kind: 'continuation_selected' as const,
      stateKey,
      observedSelectorBytes: new Uint8Array([1]),
      observedSelectorRevision: 'bootstrap' as const,
      observedSelectorSnapshotSha256: sha as never,
      markerId: sha as never,
      predecessorBytes: bytes,
      transitionPlan: 'continuation' as const,
    };
    const first = computeSelectionSnapshotId({ ...base, selectionSnapshotId: sha as never });
    const second = computeSelectionSnapshotId({
      ...base,
      predecessorBytes: { ...bytes, ledgerBytes: new Uint8Array([9]) },
      selectionSnapshotId: sha as never,
    });
    expect(first).not.toBe(second);
  });
});

describe.skipIf(process.platform !== 'linux')('Linux reference store', () => {
  it('reopens and retains candidate bytes and registration cutoff', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const registrationDraft = draft();
      const registration = materializeRegistration(
        registrationDraft,
        '1',
        '2026-07-20T00:00:00.000Z',
      );
      const candidate = bundle();
      const upload = await store.uploadCandidate(registration.candidateId, candidate);
      expect(['created', 'already_exists_same']).toContain(upload.kind);
      const written = await store.registerCandidate(registrationDraft);
      expect(written.kind).toBe('created');
      const reopened = new ReferenceStateStore(root);
      await reopened.close();
      const read = await reopened.readCandidate(registration.candidateId);
      expect(read.status).toBe('present');
      if (read.status === 'present') expect(read.bundle.ledgerBytes).toEqual(candidate.ledgerBytes);
      const snapshot = await reopened.createAcceptanceSnapshot(
        'bootstrap',
        {
          stateKey,
          sessionEpoch: epoch,
          observedSelectorRevision: 'bootstrap',
          predecessorMarkerId: 'bootstrap',
          predecessorManifestSha256: 'bootstrap',
          predecessorLedgerSha256: 'bootstrap',
          targetStateGeneration: 0,
          interactionId: sha as never,
        },
        sha,
      );
      expect(snapshot.cutoff).toBe('1');
      expect(snapshot.registrations).toHaveLength(1);
      expect(snapshot.registrations[0].registrationRecordSha256).toBe(
        sha256Hex(snapshot.registrations[0].registrationBytes),
      );
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('serializes registration sequences across independent store instances', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-race-'));
    try {
      const left = new ReferenceStateStore(root);
      const right = new ReferenceStateStore(root);
      await Promise.all([left.close(), right.close()]);
      const first = draft({ interactionId: 'c'.repeat(64) as never });
      const second = draft({ interactionId: 'd'.repeat(64) as never });
      const [leftResult, rightResult] = await Promise.all([
        left.registerCandidate(first),
        right.registerCandidate(second),
      ]);
      expect(
        [
          leftResult.registration?.registrationSequence,
          rightResult.registration?.registrationSequence,
        ].sort(),
      ).toEqual(['1', '2']);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });
});
