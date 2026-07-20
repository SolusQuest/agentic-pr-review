import net from 'node:net';
import path from 'node:path';
import { mkdir, readdir, rename, rm, writeFile, open } from 'node:fs/promises';
import { constants as fsConstants } from 'node:fs';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { bytesEqual, recordSha256 } from './codec.js';
import {
  candidateBundleSha256,
  candidateLocator,
  compareDecimalIds,
  computeCandidateSetDigest,
  computeSelectionSnapshotId,
  observedSelectorSnapshotSha256,
  sha256Hex,
} from './hash.js';
import {
  ContractValidationError,
  decodeValidatedMarker,
  decodeValidatedRegistration,
  decodeValidatedSelector,
  encodeValidatedRecord,
  materializeRegistration,
  validateAcceptedStateMarker,
  validateStateSelector,
} from './validation.js';
import type {
  AcceptedStateMarkerV1,
  CandidateBundleBytes,
  CandidateId,
  CandidateRegistrationDraft,
  CandidateRegistrationV1,
  MarkerId,
  CompetingScope,
  DecimalSequence,
  RecoveryEvidence,
  SelectionOptions,
  SelectionOutcome,
  StateKeyV2,
  StateSelectorV1,
  StateSelectionSnapshot,
  SelectorRevision,
  CandidateUploadOutcome,
  FrozenRegistration,
  ObservedCandidateEntry,
  SelectorCasOutcome,
  WriteOutcome,
} from './types.js';

export const MAX_ACCEPTANCE_SNAPSHOT_REGISTRATIONS = 64 as const;
export const MAX_ACCEPTANCE_SNAPSHOT_REGISTRATION_BYTES = 2_097_152 as const;

type SnapshotDraft = {
  [K in StateSelectionSnapshot['kind']]: Omit<
    Extract<StateSelectionSnapshot, { readonly kind: K }>,
    'selectionSnapshotId'
  >;
}[StateSelectionSnapshot['kind']];

export class StoreTransactionError extends Error {
  readonly reason: 'store_transaction_failed' | 'store_capability_unsupported';

  constructor(reason: StoreTransactionError['reason']) {
    super(reason);
    this.name = 'StoreTransactionError';
    this.reason = reason;
  }
}

export class SelectionSnapshotLimitError extends Error {
  constructor() {
    super('candidate_snapshot_limit_exceeded');
    this.name = 'SelectionSnapshotLimitError';
  }
}

export class SelectorRevisionMismatchError extends Error {
  readonly currentRevision: SelectorRevision;

  constructor(currentRevision: SelectorRevision) {
    super('selector revision mismatch');
    this.name = 'SelectorRevisionMismatchError';
    this.currentRevision = currentRevision;
  }
}

export interface ReferenceStoreHooks {
  readonly afterCandidateCommit?: () => void | Promise<void>;
  readonly afterRegistrationCommit?: () => void | Promise<void>;
  readonly afterMarkerCommit?: () => void | Promise<void>;
  readonly afterSelectorCommit?: () => void | Promise<void>;
}

export type CandidateReadResult =
  | { readonly status: 'present'; readonly bundle: CandidateBundleBytes }
  | { readonly status: 'missing'; readonly evidence: CandidateEvidence }
  | {
      readonly status: 'unsafe';
      readonly diagnostic: 'bundle_extra_entry' | 'bundle_listing_mismatch';
      readonly evidence: CandidateEvidence;
    }
  | { readonly status: 'failed' };

export interface CandidateEvidence {
  readonly manifest: ObservedCandidateEntry;
  readonly ledger: ObservedCandidateEntry;
  readonly providerRunMetadata: ObservedCandidateEntry;
}

export interface RegistrationWriteResult {
  readonly kind:
    | 'created'
    | 'already_exists_same'
    | 'registration_write_conflict'
    | 'outcome_unknown'
    | 'registration_sequence_overflow';
  readonly registration?: CandidateRegistrationV1;
}

interface LockHandle {
  readonly server: net.Server;
  readonly release: () => Promise<void>;
}

export class ReferenceStateStore {
  readonly root: string;
  private readonly hooks: ReferenceStoreHooks;
  private readonly initialized: Promise<void>;

  constructor(root: string, hooks: ReferenceStoreHooks = {}) {
    this.root = path.resolve(root);
    this.hooks = hooks;
    this.initialized = this.initialize();
  }

  async close(): Promise<void> {
    await this.initialized;
  }

  async uploadCandidate(
    candidateId: CandidateId,
    bundle: CandidateBundleBytes,
  ): Promise<CandidateUploadOutcome> {
    await this.initialized;
    const locator = candidateLocator(candidateId);
    if (!/^[a-f0-9]{64}$/.test(candidateId)) return { kind: 'existing_content_conflict' };
    const candidatesRoot = path.join(this.root, 'candidates');
    const target = path.join(candidatesRoot, candidateId);
    try {
      const temporary = path.join(
        candidatesRoot,
        `.candidate-${candidateId}-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}`,
      );
      await mkdir(temporary, { mode: 0o700 });
      try {
        await writePrivate(path.join(temporary, 'manifest.json'), bundle.manifestBytes);
        await writePrivate(path.join(temporary, 'ledger.json'), bundle.ledgerBytes);
        await writePrivate(
          path.join(temporary, 'provider-run-metadata.json'),
          bundle.providerRunMetadataBytes,
        );
        await rename(temporary, target);
      } catch (error) {
        await rm(temporary, { recursive: true, force: true }).catch(() => undefined);
        throw error;
      }
      try {
        await this.hooks.afterCandidateCommit?.();
      } catch {
        return { kind: 'outcome_unknown', locator };
      }
      return { kind: 'created', locator };
    } catch (error) {
      if (!isAlreadyExists(error)) return { kind: 'outcome_unknown', locator };
      const existing = await this.readCandidate(candidateId);
      if (existing.status === 'present' && bundlesEqual(existing.bundle, bundle)) {
        return { kind: 'already_exists_same', locator };
      }
      if (existing.status === 'missing') return { kind: 'outcome_unknown', locator };
      return { kind: 'existing_content_conflict' };
    }
  }

  async readCandidate(candidateId: CandidateId): Promise<CandidateReadResult> {
    await this.initialized;
    if (!/^[a-f0-9]{64}$/.test(candidateId))
      return {
        status: 'unsafe',
        diagnostic: 'bundle_listing_mismatch',
        evidence: unsafeCandidateEvidence(),
      };
    const directory = path.join(this.root, 'candidates', candidateId);
    try {
      const entries = await readdir(directory);
      const expected = new Set(['manifest.json', 'ledger.json', 'provider-run-metadata.json']);
      const evidence = {
        manifest: await readObservedEntry(path.join(directory, 'manifest.json')),
        ledger: await readObservedEntry(path.join(directory, 'ledger.json')),
        providerRunMetadata: await readObservedEntry(
          path.join(directory, 'provider-run-metadata.json'),
        ),
      } satisfies CandidateEvidence;
      if (entries.some((entry) => !expected.has(entry)))
        return { status: 'unsafe', diagnostic: 'bundle_extra_entry', evidence };
      if (entries.length !== expected.size)
        return { status: 'unsafe', diagnostic: 'bundle_listing_mismatch', evidence };
      if (
        evidence.manifest.status !== 'present' ||
        evidence.ledger.status !== 'present' ||
        evidence.providerRunMetadata.status !== 'present'
      ) {
        return { status: 'missing', evidence };
      }
      const manifestBytes = await readPrivate(path.join(directory, 'manifest.json'));
      const ledgerBytes = await readPrivate(path.join(directory, 'ledger.json'));
      const providerRunMetadataBytes = await readPrivate(
        path.join(directory, 'provider-run-metadata.json'),
      );
      return {
        status: 'present',
        bundle: {
          manifestBytes,
          ledgerBytes,
          providerRunMetadataBytes,
        },
      };
    } catch (error) {
      if (isMissing(error)) return { status: 'missing', evidence: missingCandidateEvidence() };
      return { status: 'failed' };
    }
  }

  async registerCandidate(draft: CandidateRegistrationDraft): Promise<RegistrationWriteResult> {
    await this.initialized;
    try {
      return await this.withStateKeyLock(draft.stateKey, async () => {
        const registrationsDirectory = await this.registrationsDirectory(draft.stateKey);
        const registrationId = (await import('./hash.js')).computeRegistrationId(draft);
        const registrationPath = path.join(registrationsDirectory, `${registrationId}.json`);
        const existingBytes = await readOptionalPrivate(registrationPath);
        if (existingBytes !== null) {
          try {
            const existing = decodeValidatedRegistration(existingBytes);
            return { kind: 'already_exists_same', registration: existing };
          } catch {
            return { kind: 'registration_write_conflict' };
          }
        }
        const current = await this.scanRegistrations(registrationsDirectory);
        const maximum = current.reduce(
          (max, item) => Math.max(max, Number(item.registration.registrationSequence)),
          0,
        );
        if (maximum >= 1_000_000) return { kind: 'registration_sequence_overflow' };
        const registration = materializeRegistration(draft, String(maximum + 1));
        const bytes = encodeValidatedRecord(registration);
        await writePrivateAtomic(registrationPath, bytes);
        await writePrivateAtomic(
          path.join(registrationsDirectory, 'sequence.txt'),
          new TextEncoder().encode(registration.registrationSequence),
        );
        try {
          await this.hooks.afterRegistrationCommit?.();
        } catch {
          return { kind: 'outcome_unknown', registration };
        }
        return { kind: 'created', registration };
      });
    } catch (error) {
      if (error instanceof StoreTransactionError) throw error;
      return { kind: 'outcome_unknown' };
    }
  }

  async readSelector(
    stateKey: StateKeyV2,
  ): Promise<{ readonly bytes: Uint8Array | null; readonly selector: StateSelectorV1 | null }> {
    await this.initialized;
    return this.withStateKeyLock(stateKey, async () => {
      const bytes = await readOptionalPrivate(
        path.join(await this.stateDirectory(stateKey), 'selector.json'),
      );
      return { bytes, selector: bytes === null ? null : decodeValidatedSelector(bytes) };
    });
  }

  async writeMarker(marker: AcceptedStateMarkerV1): Promise<WriteOutcome<AcceptedStateMarkerV1>> {
    await this.initialized;
    validateAcceptedStateMarker(marker);
    return this.withStateKeyLock(marker.stateKey, async () => {
      const directory = await this.markersDirectory(marker.stateKey);
      const target = path.join(directory, `${marker.markerId}.json`);
      const existing = await readOptionalPrivate(target);
      if (existing !== null) {
        const current = decodeValidatedMarker(existing);
        return { kind: 'already_exists_same', value: current };
      }
      const bytes = encodeValidatedRecord(marker);
      await writePrivateAtomic(target, bytes);
      try {
        await this.hooks.afterMarkerCommit?.();
      } catch {
        return { kind: 'outcome_unknown' };
      }
      return { kind: 'created', value: marker };
    });
  }

  async readMarker(
    stateKey: StateKeyV2,
    markerId: MarkerId,
  ): Promise<{ readonly bytes: Uint8Array | null; readonly marker: AcceptedStateMarkerV1 | null }> {
    await this.initialized;
    return this.withStateKeyLock(stateKey, async () => {
      const bytes = await readOptionalPrivate(
        path.join(await this.markersDirectory(stateKey), `${markerId}.json`),
      );
      return { bytes, marker: bytes === null ? null : decodeValidatedMarker(bytes) };
    });
  }

  async casSelector(
    expectedRevision: SelectorRevision,
    selector: StateSelectorV1,
  ): Promise<SelectorCasOutcome> {
    await this.initialized;
    validateStateSelector(selector);
    return this.withStateKeyLock(selector.stateKey, async () => {
      const target = path.join(await this.stateDirectory(selector.stateKey), 'selector.json');
      const currentBytes = await readOptionalPrivate(target);
      const currentRevision =
        currentBytes === null ? 'bootstrap' : selectorRevisionFromBytes(currentBytes);
      if (currentRevision !== expectedRevision) {
        return { kind: 'rejected_with_current_revision', currentRevision };
      }
      if (currentBytes !== null) {
        const current = decodeValidatedSelector(currentBytes);
        if (current.selectorId === selector.selectorId)
          return { kind: 'already_applied_same_target', selector: current };
      }
      await writePrivateAtomic(target, encodeValidatedRecord(selector));
      try {
        await this.hooks.afterSelectorCommit?.();
      } catch {
        return { kind: 'outcome_unknown' };
      }
      return { kind: 'applied', selector };
    });
  }

  async createAcceptanceSnapshot(
    expectedObservedSelectorRevision: SelectorRevision,
    competingScope: CompetingScope,
    selectionSnapshotId: string,
  ) {
    await this.initialized;
    return this.withStateKeyLock(competingScope.stateKey, async () => {
      const stateDirectory = await this.stateDirectory(competingScope.stateKey);
      const selectorBytes = await readOptionalPrivate(path.join(stateDirectory, 'selector.json'));
      const currentRevision =
        selectorBytes === null ? 'bootstrap' : selectorRevisionFromBytes(selectorBytes);
      if (currentRevision !== expectedObservedSelectorRevision)
        throw new SelectorRevisionMismatchError(currentRevision);
      const registrations = await this.scanRegistrations(
        await this.registrationsDirectory(competingScope.stateKey),
      );
      const cutoff = String(
        registrations.reduce(
          (max, item) => Math.max(max, Number(item.registration.registrationSequence)),
          0,
        ),
      );
      const matching = registrations.filter(
        (item) =>
          matchesScope(item.registration, competingScope) &&
          compareDecimalIds(item.registration.registrationSequence, cutoff) <= 0,
      );
      const frozen: FrozenRegistration[] = [];
      let totalBytes = 0;
      for (const item of matching) {
        totalBytes += item.bytes.byteLength;
        if (
          frozen.length + 1 > MAX_ACCEPTANCE_SNAPSHOT_REGISTRATIONS ||
          totalBytes > MAX_ACCEPTANCE_SNAPSHOT_REGISTRATION_BYTES
        ) {
          throw new SelectionSnapshotLimitError();
        }
        frozen.push({
          registrationSequence: item.registration.registrationSequence,
          registrationId: item.registration.registrationId,
          registrationRecordSha256: recordSha256(
            item.bytes,
          ) as FrozenRegistration['registrationRecordSha256'],
          registrationBytes: new Uint8Array(item.bytes),
          registration: structuredClone(item.registration),
        });
      }
      frozen.sort((left, right) => {
        const sequenceOrder = compareDecimalIds(
          left.registrationSequence,
          right.registrationSequence,
        );
        return sequenceOrder === 0
          ? left.registrationId.localeCompare(right.registrationId)
          : sequenceOrder;
      });
      return {
        schemaVersion: 1 as const,
        selectionSnapshotId: selectionSnapshotId as FrozenRegistration['registrationRecordSha256'],
        expectedObservedSelectorRevision,
        currentSelectorRevision: currentRevision,
        competingScope,
        cutoff: cutoff as DecimalSequence,
        registrations: frozen,
        candidateSetDigest: computeCandidateSetDigest(
          competingScope,
          cutoff,
          frozen.map(({ registrationSequence, registrationId, registrationRecordSha256 }) => ({
            registrationSequence,
            registrationId,
            registrationRecordSha256,
          })),
        ),
      };
    });
  }

  async selectAcceptedState(options: SelectionOptions): Promise<SelectionOutcome> {
    await this.initialized;
    if (process.platform !== 'linux')
      return { selection: 'failed', reason: 'store_capability_unsupported' };
    try {
      return await this.withStateKeyLock(options.stateKey, async () =>
        this.selectAcceptedStateLocked(options),
      );
    } catch (error) {
      if (error instanceof SelectionSnapshotLimitError)
        return { selection: 'failed', reason: 'selection_snapshot_limit_exceeded' };
      if (error instanceof StoreTransactionError)
        return { selection: 'failed', reason: error.reason };
      if (error instanceof ContractValidationError)
        return { selection: 'failed', reason: 'selector_read_failed' };
      return { selection: 'unknown', reason: 'selection_outcome_unknown' };
    }
  }

  private async selectAcceptedStateLocked(options: SelectionOptions): Promise<SelectionOutcome> {
    const stateDirectory = await this.stateDirectory(options.stateKey);
    const selectorBytes = await readOptionalPrivate(path.join(stateDirectory, 'selector.json'));
    if (selectorBytes === null) {
      if (options.explicitRestore)
        return {
          selection: 'selected',
          snapshot: this.finalizeSnapshot({
            schemaVersion: 1,
            kind: 'explicit_restore_invalid',
            stateKey: options.stateKey,
            observedSelectorBytes: null,
            observedSelectorRevision: 'bootstrap',
            observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
            failure: 'explicit_state_invalid',
          }),
        };
      return {
        selection: 'selected',
        snapshot: this.finalizeSnapshot({
          schemaVersion: 1,
          kind: 'bootstrap_selected',
          stateKey: options.stateKey,
          observedSelectorBytes: null,
          observedSelectorRevision: 'bootstrap',
          observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
          transitionPlan: 'bootstrap',
        }),
      };
    }

    let selector: StateSelectorV1;
    try {
      selector = decodeValidatedSelector(selectorBytes);
    } catch {
      return this.recoveryOrExplicit(
        options,
        new Uint8Array(selectorBytes),
        'corrupt_accepted_artifact',
        'selector_invalid',
        [{ kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) }],
      );
    }
    const observedRevision = selector.selectorRevision;
    const observedHash = observedSelectorSnapshotSha256(selectorBytes);
    if (!bytesEqual(canonicalJsonBytes(selector.stateKey), canonicalJsonBytes(options.stateKey))) {
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        'state_key_mismatch',
        'state_key_mismatch',
        [{ kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) }],
        observedRevision,
      );
    }
    let markerBytes: Uint8Array | null;
    try {
      markerBytes = await readOptionalPrivate(
        path.join(
          await this.markersDirectory(options.stateKey),
          `${selector.acceptedMarkerId}.json`,
        ),
      );
    } catch {
      return { selection: 'failed', reason: 'marker_read_failed' };
    }
    if (markerBytes === null) {
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        'corrupt_accepted_artifact',
        'marker_invalid',
        [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
          { kind: 'marker_reference', markerId: selector.acceptedMarkerId },
        ],
        observedRevision,
      );
    }
    let marker: AcceptedStateMarkerV1;
    try {
      marker = decodeValidatedMarker(markerBytes);
    } catch {
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        'corrupt_accepted_artifact',
        'marker_invalid',
        [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
          {
            kind: 'marker_bytes',
            markerId: selector.acceptedMarkerId,
            sha256: sha256Hex(markerBytes),
          },
        ],
        observedRevision,
      );
    }
    if (
      marker.markerId !== selector.acceptedMarkerId ||
      marker.candidateId !== selector.candidateId ||
      marker.stateKey.workflowIdentity !== selector.stateKey.workflowIdentity
    ) {
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        'integrity_mismatch',
        'explicit_state_invalid',
        [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
          { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
        ],
        observedRevision,
      );
    }
    const candidate = await this.readCandidate(marker.candidateId);
    if (candidate.status === 'failed') {
      return { selection: 'failed', reason: 'candidate_read_failed' };
    }
    if (candidate.status !== 'present') {
      const evidence: RecoveryEvidence[] = [
        { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
        {
          kind: 'candidate_bundle',
          candidateId: marker.candidateId,
          manifest: candidate.evidence.manifest,
          ledger: candidate.evidence.ledger,
          providerRunMetadata: candidate.evidence.providerRunMetadata,
          bundleDiagnostic:
            candidate.status === 'unsafe' ? candidate.diagnostic : 'manifest_missing',
        },
      ];
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        candidate.status === 'missing'
          ? 'unavailable_accepted_artifact'
          : 'corrupt_accepted_artifact',
        'candidate_invalid',
        evidence,
        observedRevision,
      );
    }
    const hashes = candidateBundleSha256(candidate.bundle);
    if (
      hashes.manifestSha256 !== marker.manifestSha256 ||
      hashes.candidateLedgerSha256 !== marker.candidateLedgerSha256 ||
      hashes.providerRunMetadataSha256 !== marker.providerRunMetadataSha256
    ) {
      return this.recoveryOrExplicit(
        options,
        selectorBytes,
        'integrity_mismatch',
        'candidate_invalid',
        [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
          { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
          { kind: 'candidate_reference', candidateId: marker.candidateId },
        ],
        observedRevision,
      );
    }

    const predecessorBytes = {
      manifestBytes: new Uint8Array(candidate.bundle.manifestBytes),
      ledgerBytes: new Uint8Array(candidate.bundle.ledgerBytes),
      providerRunMetadataBytes: new Uint8Array(candidate.bundle.providerRunMetadataBytes),
    };
    const common = {
      schemaVersion: 1 as const,
      stateKey: options.stateKey,
      observedSelectorBytes: new Uint8Array(selectorBytes),
      observedSelectorRevision: observedRevision,
      observedSelectorSnapshotSha256: observedHash,
    };
    if (
      selector.sessionEpoch === options.sessionEpoch &&
      selector.ledgerEpoch === options.ledgerEpoch &&
      selector.currentBaseSha === options.currentBaseSha &&
      selector.currentHeadSha === options.currentHeadSha
    ) {
      return {
        selection: 'selected',
        snapshot: this.finalizeSnapshot({
          ...common,
          kind: 'continuation_selected',
          transitionPlan: 'continuation',
          markerId: marker.markerId,
          predecessorBytes,
        }),
      };
    }
    const resetReason =
      selector.currentBaseSha !== options.currentBaseSha
        ? 'base_change'
        : selector.currentHeadSha !== options.currentHeadSha
          ? 'head_history_discontinuity'
          : 'cache_contract_change';
    return {
      selection: 'selected',
      snapshot: this.finalizeSnapshot({
        ...common,
        kind: 'reset_selected',
        transitionPlan: 'reset',
        markerId: marker.markerId,
        predecessorBytes,
        resetReason,
      }),
    };
  }

  private recoveryOrExplicit(
    options: SelectionOptions,
    selectorBytes: Uint8Array,
    reason:
      | 'unavailable_accepted_artifact'
      | 'contract_version_incompatible'
      | 'corrupt_accepted_artifact'
      | 'integrity_mismatch'
      | 'unsafe_provenance'
      | 'state_key_mismatch'
      | 'over_bound_ledger',
    failure:
      | 'explicit_state_invalid'
      | 'selector_invalid'
      | 'marker_invalid'
      | 'candidate_invalid'
      | 'state_key_mismatch'
      | 'contract_version_incompatible'
      | 'over_bound_ledger',
    evidence: readonly RecoveryEvidence[],
    observedRevision?: SelectorRevision,
  ): SelectionOutcome {
    const revision =
      observedRevision ?? (`invalid:${sha256Hex(selectorBytes)}` as SelectorRevision);
    if (options.explicitRestore)
      return {
        selection: 'selected',
        snapshot: this.finalizeSnapshot({
          schemaVersion: 1,
          kind: 'explicit_restore_invalid',
          stateKey: options.stateKey,
          observedSelectorBytes: new Uint8Array(selectorBytes),
          observedSelectorRevision: revision,
          observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes),
          failure,
        }),
      };
    return {
      selection: 'selected',
      snapshot: this.finalizeSnapshot({
        schemaVersion: 1,
        kind: 'recovery_root_selected',
        stateKey: options.stateKey,
        observedSelectorBytes: new Uint8Array(selectorBytes),
        observedSelectorRevision: revision,
        observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes),
        transitionPlan: 'recovery_root',
        recoveryReason: reason,
        recoveryEvidence: evidence,
      }),
    };
  }

  private finalizeSnapshot(snapshot: SnapshotDraft): StateSelectionSnapshot {
    const value = { ...snapshot, selectionSnapshotId: '' } as StateSelectionSnapshot;
    return {
      ...value,
      selectionSnapshotId: computeSelectionSnapshotId(value),
    } as StateSelectionSnapshot;
  }

  private async initialize(): Promise<void> {
    await mkdir(this.root, { recursive: true, mode: 0o700 });
    await mkdir(path.join(this.root, 'candidates'), { recursive: true, mode: 0o700 });
  }

  private async stateDirectory(stateKey: StateKeyV2): Promise<string> {
    const directory = path.join(this.root, 'states', sha256Hex(canonicalJsonBytes(stateKey)));
    await mkdir(directory, { recursive: true, mode: 0o700 });
    return directory;
  }

  private async registrationsDirectory(stateKey: StateKeyV2): Promise<string> {
    const directory = path.join(await this.stateDirectory(stateKey), 'registrations');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    return directory;
  }

  private async markersDirectory(stateKey: StateKeyV2): Promise<string> {
    const directory = path.join(await this.stateDirectory(stateKey), 'markers');
    await mkdir(directory, { recursive: true, mode: 0o700 });
    return directory;
  }

  private async scanRegistrations(
    directory: string,
  ): Promise<readonly { registration: CandidateRegistrationV1; bytes: Uint8Array }[]> {
    const entries = await readdir(directory).catch((error) =>
      isMissing(error) ? [] : Promise.reject(error),
    );
    const result: Array<{ registration: CandidateRegistrationV1; bytes: Uint8Array }> = [];
    const ids = new Set<string>();
    const sequences = new Set<string>();
    if (entries.some((entry) => entry !== 'sequence.txt' && !entry.endsWith('.json'))) {
      throw new StoreTransactionError('store_transaction_failed');
    }
    for (const entry of entries.filter((name) => name.endsWith('.json'))) {
      const bytes = await readPrivate(path.join(directory, entry));
      const registration = decodeValidatedRegistration(bytes);
      if (
        ids.has(registration.registrationId) ||
        sequences.has(registration.registrationSequence)
      ) {
        throw new StoreTransactionError('store_transaction_failed');
      }
      ids.add(registration.registrationId);
      sequences.add(registration.registrationSequence);
      result.push({ registration, bytes });
    }
    return result;
  }

  private async withStateKeyLock<T>(stateKey: StateKeyV2, callback: () => Promise<T>): Promise<T> {
    if (process.platform !== 'linux')
      throw new StoreTransactionError('store_capability_unsupported');
    const handle = await acquireAbstractLock(stateKey);
    try {
      return await callback();
    } finally {
      await handle.release();
    }
  }
}

function matchesScope(registration: CandidateRegistrationV1, scope: CompetingScope): boolean {
  return bytesEqual(
    canonicalJsonBytes({
      stateKey: registration.stateKey,
      sessionEpoch: registration.sessionEpoch,
      observedSelectorRevision: registration.observedSelectorRevision,
      predecessorMarkerId: registration.predecessorMarkerId,
      predecessorManifestSha256: registration.predecessorManifestSha256,
      predecessorLedgerSha256: registration.predecessorLedgerSha256,
      targetStateGeneration: registration.stateGeneration,
      interactionId: registration.interactionId,
    }),
    canonicalJsonBytes(scope),
  );
}

function selectorRevisionFromBytes(bytes: Uint8Array): SelectorRevision {
  try {
    return decodeValidatedSelector(bytes).selectorRevision;
  } catch {
    return `invalid:${sha256Hex(bytes)}`;
  }
}

async function acquireAbstractLock(stateKey: StateKeyV2): Promise<LockHandle> {
  const name = `\0agentic-pr-review-m4-${sha256Hex(canonicalJsonBytes(stateKey))}`;
  const deadline = Date.now() + 5_000;
  while (true) {
    const server = net.createServer((socket) => socket.destroy());
    try {
      await listen(server, name);
      return {
        server,
        release: () => closeServer(server),
      };
    } catch (error) {
      if (server.listening) await closeServer(server).catch(() => undefined);
      else server.removeAllListeners();
      if ((error as NodeJS.ErrnoException).code !== 'EADDRINUSE') {
        throw new StoreTransactionError('store_transaction_failed');
      }
      if (Date.now() >= deadline) throw new StoreTransactionError('store_transaction_failed');
      await new Promise((resolve) => setTimeout(resolve, 25));
    }
  }
}

function listen(server: net.Server, pathName: string): Promise<void> {
  return new Promise((resolve, reject) => {
    const onError = (error: Error) => {
      server.removeListener('listening', onListening);
      reject(error);
    };
    const onListening = () => {
      server.removeListener('error', onError);
      resolve();
    };
    server.once('error', onError);
    server.once('listening', onListening);
    server.listen({ path: pathName });
  });
}

function closeServer(server: net.Server): Promise<void> {
  if (!server.listening) return Promise.resolve();
  return new Promise((resolve, reject) =>
    server.close((error) => (error ? reject(error) : resolve())),
  );
}

async function writePrivate(filePath: string, bytes: Uint8Array): Promise<void> {
  await writeFile(filePath, bytes, { mode: 0o600, flag: 'wx' });
}

async function writePrivateAtomic(filePath: string, bytes: Uint8Array): Promise<void> {
  const temporary = `${filePath}.tmp-${process.pid}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  await writePrivate(temporary, bytes);
  try {
    await rename(temporary, filePath);
  } catch (error) {
    await rm(temporary, { force: true }).catch(() => undefined);
    throw error;
  }
}

async function readPrivate(filePath: string): Promise<Uint8Array> {
  const handle = await open(filePath, fsConstants.O_RDONLY | (fsConstants.O_NOFOLLOW ?? 0));
  try {
    const stat = await handle.stat();
    if (!stat.isFile()) throw new Error('unsafe file');
    return new Uint8Array(await handle.readFile());
  } finally {
    await handle.close();
  }
}

async function readOptionalPrivate(filePath: string): Promise<Uint8Array | null> {
  try {
    return await readPrivate(filePath);
  } catch (error) {
    if (isMissing(error)) return null;
    throw error;
  }
}

async function readObservedEntry(filePath: string): Promise<ObservedCandidateEntry> {
  try {
    return { status: 'present', sha256: sha256Hex(await readPrivate(filePath)) };
  } catch (error) {
    return isMissing(error) ? { status: 'missing' } : { status: 'unsafe' };
  }
}

function missingCandidateEvidence(): CandidateEvidence {
  return {
    manifest: { status: 'missing' },
    ledger: { status: 'missing' },
    providerRunMetadata: { status: 'missing' },
  };
}

function unsafeCandidateEvidence(): CandidateEvidence {
  return {
    manifest: { status: 'unsafe' },
    ledger: { status: 'unsafe' },
    providerRunMetadata: { status: 'unsafe' },
  };
}

function bundlesEqual(left: CandidateBundleBytes, right: CandidateBundleBytes): boolean {
  return (
    bytesEqual(left.manifestBytes, right.manifestBytes) &&
    bytesEqual(left.ledgerBytes, right.ledgerBytes) &&
    bytesEqual(left.providerRunMetadataBytes, right.providerRunMetadataBytes)
  );
}

function isMissing(error: unknown): boolean {
  return (error as NodeJS.ErrnoException | undefined)?.code === 'ENOENT';
}

function isAlreadyExists(error: unknown): boolean {
  return (error as NodeJS.ErrnoException | undefined)?.code === 'EEXIST';
}
