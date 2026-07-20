import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { describe, expect, it } from 'vitest';
import {
  acceptLocalCandidate,
  RecordCodecError,
  ReferenceStateStore,
  SelectionSnapshotLimitError,
  candidateLocator,
  computeCandidateId,
  computeSelectionSnapshotId,
  decodeRecord,
  encodeRecord,
  materializeMarker,
  materializeRegistration,
  materializeSelector,
  observedSelectorSnapshotSha256,
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

function markerFor(
  candidateId: string,
  hashes: {
    manifestSha256: string;
    candidateLedgerSha256: string;
    providerRunMetadataSha256: string;
  } = {
    manifestSha256: sha,
    candidateLedgerSha256: sha,
    providerRunMetadataSha256: sha,
  },
) {
  return materializeMarker(
    {
      schemaVersion: 1,
      candidateId: candidateId as never,
      registrationId: sha as never,
      stateKey,
      sessionEpoch: epoch,
      stateGeneration: 0,
      ledgerEpoch: epoch,
      transition,
      predecessorMarkerId: 'bootstrap',
      predecessorManifestSha256: 'bootstrap',
      predecessorLedgerSha256: 'bootstrap',
      observedSelectorRevision: 'bootstrap',
      manifestSha256: hashes.manifestSha256 as never,
      candidateLedgerSha256: hashes.candidateLedgerSha256 as never,
      providerRunMetadataSha256: hashes.providerRunMetadataSha256 as never,
      metadataSemanticSha256: sha as never,
      consumedInputSha256: sha as never,
      resultSha256: sha as never,
      traceSha256: sha as never,
      producingRunId: '10',
      producingRunAttempt: 1,
      acceptingRunId: '11',
      acceptingRunAttempt: 1,
    },
    '2026-07-20T00:00:01.000Z',
  );
}

function selectorFor(marker: ReturnType<typeof markerFor>) {
  return materializeSelector(
    {
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
    },
    '2026-07-20T00:00:02.000Z',
  );
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
  it('selects bootstrap without state and recovers selector-referenced corruption', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-select-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const options = {
        stateKey,
        sessionEpoch: epoch,
        ledgerEpoch: epoch,
        currentHeadSha: gitSha,
        currentBaseSha: gitSha,
        workflowIdentity: stateKey.workflowIdentity,
        trustedExecutionDomain: stateKey.trustedExecutionDomain,
      } as const;
      const bootstrap = await store.selectAcceptedState(options);
      expect(bootstrap).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'bootstrap_selected' },
      });
      const marker = markerFor('e'.repeat(64));
      const selector = selectorFor(marker);
      expect((await store.casSelector('bootstrap', selector)).kind).toBe('applied');
      const missingMarker = await store.selectAcceptedState(options);
      expect(missingMarker).toMatchObject({
        selection: 'selected',
        snapshot: {
          kind: 'recovery_root_selected',
          recoveryReason: 'corrupt_accepted_artifact',
          observedSelectorRevision: selector.selectorRevision,
        },
      });
      const explicit = await store.selectAcceptedState({ ...options, explicitRestore: true });
      expect(explicit).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'explicit_restore_invalid', failure: 'marker_invalid' },
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('classifies a present but invalid candidate bundle as corruption with per-file hashes', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-corrupt-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const candidateId = 'e'.repeat(64) as never;
      const candidateBytes = bundle();
      expect((await store.uploadCandidate(candidateId, candidateBytes)).kind).toBe('created');
      const marker = markerFor(candidateId, {
        manifestSha256: sha256Hex(candidateBytes.manifestBytes),
        candidateLedgerSha256: sha256Hex(candidateBytes.ledgerBytes),
        providerRunMetadataSha256: sha256Hex(candidateBytes.providerRunMetadataBytes),
      });
      const selector = selectorFor(marker);
      expect((await store.writeMarker(marker)).kind).toBe('created');
      expect((await store.casSelector('bootstrap', selector)).kind).toBe('applied');
      const result = await store.selectAcceptedState({
        stateKey,
        sessionEpoch: epoch,
        ledgerEpoch: epoch,
        currentHeadSha: gitSha,
        currentBaseSha: gitSha,
        workflowIdentity: stateKey.workflowIdentity,
        trustedExecutionDomain: stateKey.trustedExecutionDomain,
      });
      expect(result).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'recovery_root_selected', recoveryReason: 'corrupt_accepted_artifact' },
      });
      if (result.selection === 'selected' && result.snapshot.kind === 'recovery_root_selected') {
        expect(result.snapshot.recoveryEvidence).toContainEqual({
          kind: 'candidate_bundle',
          candidateId,
          manifest: { status: 'present', sha256: sha256Hex(candidateBytes.manifestBytes) },
          ledger: { status: 'present', sha256: sha256Hex(candidateBytes.ledgerBytes) },
          providerRunMetadata: {
            status: 'present',
            sha256: sha256Hex(candidateBytes.providerRunMetadataBytes),
          },
          bundleDiagnostic: 'manifest_unknown_field',
        });
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

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

  it('rejects a competing registration set at the exact 64-entry cap', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-cap-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      for (let index = 0; index < 65; index += 1) {
        const result = await store.registerCandidate(
          draft({ producingRunId: String(index + 1) as never }),
        );
        expect(result.kind).toBe('created');
      }
      await expect(
        store.createAcceptanceSnapshot(
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
        ),
      ).rejects.toBeInstanceOf(SelectionSnapshotLimitError);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 20_000);

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

  it('uses the kernel lock across a child process and releases it after kill', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-lock-'));
    const childScript = fileURLToPath(new URL('./lock-child.mjs', import.meta.url));
    const startChild = () => {
      const child = spawn(process.execPath, [childScript, 'hold', JSON.stringify(stateKey)], {
        stdio: ['ignore', 'pipe', 'pipe'],
      });
      const ready = new Promise<void>((resolve, reject) => {
        let output = '';
        child.stdout.on('data', (chunk: Buffer) => {
          output += chunk.toString();
          if (output.includes('READY\n')) resolve();
        });
        child.once('error', reject);
        child.once('exit', (code) => {
          if (code !== null && code !== 0) reject(new Error(`lock child exited ${code}`));
        });
      });
      return { child, ready };
    };
    try {
      const holder = startChild();
      await holder.ready;
      const store = new ReferenceStateStore(root);
      await store.close();
      await expect(store.registerCandidate(draft())).rejects.toMatchObject({
        reason: 'store_transaction_failed',
      });
      holder.child.kill('SIGKILL');
      await new Promise((resolve) => holder.child.once('exit', resolve));
      const successor = startChild();
      await successor.ready;
      successor.child.kill('SIGKILL');
      await new Promise((resolve) => successor.child.once('exit', resolve));
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 15_000);

  it('accepts an exact lease-shaped candidate and publishes only after selector CAS', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-accept-'));
    let releaseCount = 0;
    try {
      const manifest = JSON.parse(
        await readFile(
          'protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/manifest.json',
          'utf8',
        ),
      ) as Record<string, any>;
      const ledgerBytes = new Uint8Array(
        await readFile('protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/ledger.json'),
      );
      const providerRunMetadataBytes = new Uint8Array(
        await readFile(
          'protocol/fixtures/state-manifest-v2/positive-bootstrap/bundle/provider-run-metadata.json',
        ),
      );
      manifest.ledger.sha256 = sha256Hex(ledgerBytes);
      manifest.ledger.bytes = ledgerBytes.byteLength;
      manifest.transaction.candidateLedgerSha256 = sha256Hex(ledgerBytes);
      manifest.providerRunMetadata.sha256 = sha256Hex(providerRunMetadataBytes);
      manifest.providerRunMetadata.bytes = providerRunMetadataBytes.byteLength;
      const resultBytes = new TextEncoder().encode('{}');
      const traceBytes = new TextEncoder().encode('{}');
      const inputSha256 = sha256Hex(new Uint8Array([7]));
      manifest.transaction.consumedInputSha256 = inputSha256;
      manifest.transaction.resultSha256 = sha256Hex(resultBytes);
      manifest.transaction.traceSha256 = sha256Hex(traceBytes);
      const manifestBytes = canonicalJsonBytes(manifest);
      const selection = {
        schemaVersion: 1 as const,
        kind: 'bootstrap_selected' as const,
        stateKey: manifest.stateKey,
        observedSelectorBytes: null,
        observedSelectorRevision: 'bootstrap' as const,
        observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
        transitionPlan: 'bootstrap' as const,
        selectionSnapshotId: '' as never,
      };
      selection.selectionSnapshotId = computeSelectionSnapshotId(selection) as never;
      const store = new ReferenceStateStore(root);
      await store.close();
      let publishedMarkerId: string | undefined;
      const result = await acceptLocalCandidate(store, {
        selectionSnapshot: selection,
        candidate: {
          manifest: manifest as never,
          manifestBytes,
          ledgerBytes,
          providerRunMetadataBytes,
          resultBytes,
          traceBytes,
          inputSha256,
          resultSha256: sha256Hex(resultBytes),
          traceSha256: sha256Hex(traceBytes),
          candidateLedgerSha256: sha256Hex(ledgerBytes),
          metadataSemanticSha256: manifest.transaction.metadataSemanticSha256,
          release: async () => {
            releaseCount += 1;
          },
        },
        interactionId: manifest.transaction.interactionId,
        interactionOrdinal: manifest.transaction.interactionOrdinal,
        producingRunId: manifest.provenance.producingRunId,
        producingRunAttempt: manifest.provenance.producingRunAttempt,
        acceptingRunId: '99',
        acceptingRunAttempt: 1,
        consumedInputSha256: inputSha256,
        transition: manifest.transition,
        publishSticky: async (markerId) => {
          publishedMarkerId = markerId;
        },
      });
      expect(result.acceptance).toBe('accepted');
      expect(result.publication).toEqual({ status: 'succeeded' });
      if (result.acceptance === 'accepted' || result.acceptance === 'already_accepted') {
        expect(publishedMarkerId).toBe(result.markerId);
      }
      expect(releaseCount).toBe(1);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 15_000);
});
