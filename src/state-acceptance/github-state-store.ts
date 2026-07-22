import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  checkStateManifestV2Compatibility,
  classifyStateBundleV2,
  LEDGER_MAX_BYTES,
  MANIFEST_MAX_BYTES,
  METADATA_MAX_BYTES,
} from '../state-v2/index.js';
import { bytesEqual, recordSha256 } from './codec.js';
import {
  compareDecimalIds,
  computeCandidateSetDigest,
  computeRegistrationId,
  computeSelectionSnapshotId,
  observedSelectorSnapshotSha256,
  SHA256_HEX,
  sha256Hex,
} from './hash.js';
import { GitDataStateTransport, type GitDataClient, type GitStateRef } from './github-git-data.js';
import { gitStatePaths, stateKeyDigest } from './github-state-paths.js';
import {
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
  AcceptanceSnapshot,
  CandidateBundleBytes,
  CandidateId,
  CandidateRegistrationDraft,
  CandidateRegistrationV1,
  CompetingScope,
  FrozenRegistration,
  MarkerId,
  RecoveryEvidence,
  RecoveryReason,
  SelectorRevision,
  StateKeyV2,
  StateSelectionSnapshot,
  StateSelectorV1,
} from './types.js';
import type {
  CandidateReadResult,
  RegistrationWriteResult,
  StateAcceptanceStore,
} from './store.js';
import type {
  CandidateUploadOutcome,
  SelectionOptions,
  SelectionOutcome,
  SelectorCasOutcome,
  WriteOutcome,
} from './types.js';
import {
  acceptanceSnapshotLimitExceeded,
  SelectionSnapshotLimitError,
  SelectorRevisionMismatchError,
  StoreTransactionError,
} from './store.js';

const counterMax = 1_000_000;

export class GitHubGitStateAcceptanceStore implements StateAcceptanceStore {
  private readonly transport: GitDataStateTransport;

  constructor(client: GitDataClient, owner: string, repo: string) {
    this.transport = new GitDataStateTransport(client, owner, repo);
  }

  async ensureInitialized(input: {
    readonly defaultBranchCommitSha: string;
    readonly stateKey: StateKeyV2;
    readonly runId: string;
    readonly runAttempt: number;
  }): Promise<void> {
    if (!/^[1-9][0-9]{0,18}$/.test(input.runId) || !Number.isInteger(input.runAttempt) || input.runAttempt < 1 || input.runAttempt > 2_147_483_647) {
      throw new StoreTransactionError('store_capability_unsupported');
    }
    const created = await this.transport.initialize(input.defaultBranchCommitSha);
    if (created === 'unknown') throw new StoreTransactionError('store_transaction_failed');
    const state = await this.requireState();
    const sentinel = canonicalJsonBytes({ schemaVersion: 1, kind: 'agentic-pr-review-m4-state-store', namespace: 'm4-state-v1' });
    const existing = await this.readPath(state, gitStatePaths.sentinel);
    if (existing !== null && !bytesEqual(existing, sentinel)) throw new StoreTransactionError('store_transaction_failed');
    const probe = canonicalJsonBytes({ schemaVersion: 1, kind: 'm4-store-capability-probe', runId: input.runId, runAttempt: input.runAttempt, stateKeyDigest: stateKeyDigest(input.stateKey) });
    const probePath = gitStatePaths.probe(input.runId, input.runAttempt);
    const existingProbe = await this.readPath(state, probePath);
    if (existingProbe !== null && !bytesEqual(existingProbe, probe)) throw new StoreTransactionError('store_transaction_failed');
    if (existing !== null && existingProbe !== null) return;
    const result = await this.transport.commit(state, new Map([
      ...(existing === null ? [[gitStatePaths.sentinel, sentinel] as const] : []),
      ...(existingProbe === null ? [[probePath, probe] as const] : []),
    ]), 'm4 state capability probe');
    if (result !== 'applied') {
      const reread = await this.transport.read();
      if (!reread || !bytesEqual((await this.readPath(reread, gitStatePaths.sentinel)) ?? new Uint8Array(), sentinel) || !bytesEqual((await this.readPath(reread, probePath)) ?? new Uint8Array(), probe)) {
        throw new StoreTransactionError(result === 'unknown' ? 'store_transaction_failed' : 'store_capability_unsupported');
      }
    }
  }

  async uploadCandidate(candidateId: CandidateId, bundle: CandidateBundleBytes): Promise<CandidateUploadOutcome> {
    if (!SHA256_HEX.test(candidateId)) return { kind: 'existing_content_conflict' };
    if (
      bundle.manifestBytes.byteLength > MANIFEST_MAX_BYTES ||
      bundle.ledgerBytes.byteLength > LEDGER_MAX_BYTES ||
      bundle.providerRunMetadataBytes.byteLength > METADATA_MAX_BYTES
    )
      return { kind: 'existing_content_conflict' };
    const state = await this.requireState();
    const existing = await this.readCandidateFrom(state, candidateId);
    const locator = { kind: 'store-object' as const, namespace: 'm4-state-v1' as const, objectId: `candidate-${candidateId}` as const };
    if (existing.status === 'present')
      return bundlesEqual(existing.bundle, bundle)
        ? { kind: 'already_exists_same', locator }
        : { kind: 'existing_content_conflict' };
    if (existing.status !== 'missing') return { kind: 'outcome_unknown', locator };
    const result = await this.transport.commit(
      state,
      new Map([
        [gitStatePaths.candidateFile(candidateId, 'manifest.json'), bundle.manifestBytes],
        [gitStatePaths.candidateFile(candidateId, 'ledger.json'), bundle.ledgerBytes],
        [gitStatePaths.candidateFile(candidateId, 'provider-run-metadata.json'), bundle.providerRunMetadataBytes],
      ]),
      `m4 state candidate ${candidateId}`,
    );
    if (result === 'applied') return { kind: 'created', locator };
    const reread = await this.transport.read();
    if (reread) {
      const reconciled = await this.readCandidateFrom(reread, candidateId);
      if (reconciled.status === 'present' && bundlesEqual(reconciled.bundle, bundle))
        return { kind: 'already_exists_same', locator };
    }
    return result === 'rejected' ? { kind: 'outcome_unknown', locator } : { kind: 'outcome_unknown', locator };
  }

  async readCandidate(candidateId: CandidateId): Promise<CandidateReadResult> {
    if (!SHA256_HEX.test(candidateId)) return { status: 'unsafe', diagnostic: 'bundle_listing_mismatch', evidence: unsafeEvidence() };
    const state = await this.requireState();
    return this.readCandidateFrom(state, candidateId);
  }

  async registerCandidate(draft: CandidateRegistrationDraft): Promise<RegistrationWriteResult> {
    const state = await this.requireState();
    const registrationId = computeRegistrationId(draft);
    const all = await this.registrations(state, draft.stateKey);
    const same = all.find((entry) => entry.registration.registrationId === registrationId);
    if (same) return { kind: 'already_exists_same', registration: same.registration };
    const maximum = all.reduce((value, entry) => Math.max(value, Number(entry.registration.registrationSequence)), 0);
    if (maximum >= counterMax) return { kind: 'registration_sequence_overflow' };
    const registration = materializeRegistration(draft, String(maximum + 1));
    const scope = scopeOf(registration);
    const path = gitStatePaths.registration(scope, registration.registrationSequence, registration.registrationId);
    const bytes = encodeValidatedRecord(registration);
    const result = await this.transport.commit(state, new Map([[path, bytes]]), `m4 state registration ${registration.registrationId}`);
    if (result === 'applied') return { kind: 'created', registration };
    const reread = await this.transport.read();
    if (reread) {
      const candidate = await this.readPath(reread, path);
      if (candidate && bytesEqual(candidate, bytes)) return { kind: 'already_exists_same', registration };
    }
    return result === 'rejected' ? { kind: 'registration_write_failed' } : { kind: 'outcome_unknown', registration };
  }

  async readSelector(stateKey: StateKeyV2): Promise<{ readonly bytes: Uint8Array | null; readonly selector: StateSelectorV1 | null }> {
    const bytes = await this.readPath(await this.requireState(), gitStatePaths.selector(stateKey));
    return { bytes, selector: bytes === null ? null : decodeValidatedSelector(bytes) };
  }

  async writeMarker(marker: AcceptedStateMarkerV1): Promise<WriteOutcome<AcceptedStateMarkerV1>> {
    validateAcceptedStateMarker(marker);
    const state = await this.requireState();
    const path = gitStatePaths.marker(marker.markerId);
    const existing = await this.readPath(state, path);
    if (existing) {
      try {
        const value = decodeValidatedMarker(existing);
        return value.markerId === marker.markerId ? { kind: 'already_exists_same', value } : { kind: 'existing_content_conflict' };
      } catch {
        return { kind: 'existing_content_conflict' };
      }
    }
    const bytes = encodeValidatedRecord(marker);
    const outcome = await this.transport.commit(state, new Map([[path, bytes]]), `m4 state marker ${marker.markerId}`);
    if (outcome === 'applied') return { kind: 'created', value: marker };
    const reread = await this.transport.read();
    if (reread && bytesEqual((await this.readPath(reread, path)) ?? new Uint8Array(), bytes)) return { kind: 'already_exists_same', value: marker };
    return outcome === 'rejected' ? { kind: 'existing_content_conflict' } : { kind: 'outcome_unknown' };
  }

  async readMarker(stateKey: StateKeyV2, markerId: MarkerId): Promise<{ readonly bytes: Uint8Array | null; readonly marker: AcceptedStateMarkerV1 | null }> {
    if (!SHA256_HEX.test(markerId)) throw new Error('invalid marker id');
    const bytes = await this.readPath(await this.requireState(), gitStatePaths.marker(markerId));
    const marker = bytes === null ? null : decodeValidatedMarker(bytes);
    if (marker && !bytesEqual(canonicalJsonBytes(marker.stateKey), canonicalJsonBytes(stateKey))) return { bytes, marker: null };
    return { bytes, marker };
  }

  async casSelector(expectedRevision: SelectorRevision, selector: StateSelectorV1): Promise<SelectorCasOutcome> {
    validateStateSelector(selector);
    const state = await this.requireState();
    const path = gitStatePaths.selector(selector.stateKey);
    const current = await this.readPath(state, path);
    const currentRevision = revision(current);
    if (selector.previousSelectorRevision !== expectedRevision || currentRevision !== expectedRevision)
      return { kind: 'rejected_with_current_revision', currentRevision };
    if (current) {
      try {
        const decoded = decodeValidatedSelector(current);
        if (decoded.selectorId === selector.selectorId) return { kind: 'already_applied_same_target', selector: decoded };
      } catch { /* revision already carries invalid state */ }
    }
    const bytes = encodeValidatedRecord(selector);
    const outcome = await this.transport.commit(state, new Map([[path, bytes]]), `m4 state selector ${selector.selectorId}`);
    if (outcome === 'applied') return { kind: 'applied', selector };
    const reread = await this.transport.read();
    if (reread) {
      const observed = await this.readPath(reread, path);
      if (observed && bytesEqual(observed, bytes)) return { kind: 'already_applied_same_target', selector };
      return { kind: 'rejected_with_current_revision', currentRevision: revision(observed) };
    }
    return { kind: 'outcome_unknown' };
  }

  async createAcceptanceSnapshot(expectedObservedSelectorRevision: SelectorRevision, competingScope: CompetingScope, selectionSnapshotId: string): Promise<AcceptanceSnapshot> {
    const state = await this.requireState();
    const currentRevision = revision(await this.readPath(state, gitStatePaths.selector(competingScope.stateKey)));
    if (currentRevision !== expectedObservedSelectorRevision) throw new SelectorRevisionMismatchError(currentRevision);
    const registrations = (await this.registrations(state, competingScope.stateKey)).filter((entry) => matchesScope(entry.registration, competingScope));
    const cutoff = registrations.reduce((value, entry) => Math.max(value, Number(entry.registration.registrationSequence)), 0).toString();
    const frozen: FrozenRegistration[] = [];
    let total = 0;
    for (const entry of registrations.sort((a, b) => compareDecimalIds(a.registration.registrationSequence, b.registration.registrationSequence))) {
      if (acceptanceSnapshotLimitExceeded(frozen.length, total, entry.bytes.byteLength)) throw new SelectionSnapshotLimitError();
      total += entry.bytes.byteLength;
      frozen.push({ registrationSequence: entry.registration.registrationSequence, registrationId: entry.registration.registrationId, registrationRecordSha256: recordSha256(entry.bytes) as FrozenRegistration['registrationRecordSha256'], registrationBytes: new Uint8Array(entry.bytes), registration: structuredClone(entry.registration) });
    }
    const enumeration = { kind: 'complete' as const, matchingRegistrationCount: frozen.length, matchingRegistrationBytes: total };
    return { schemaVersion: 1, selectionSnapshotId: selectionSnapshotId as AcceptanceSnapshot['selectionSnapshotId'], expectedObservedSelectorRevision, currentSelectorRevision: currentRevision, competingScope, cutoff: cutoff as AcceptanceSnapshot['cutoff'], registrations: frozen, enumeration, candidateSetDigest: computeCandidateSetDigest(competingScope, cutoff, frozen, enumeration) };
  }

  async selectAcceptedState(options: SelectionOptions): Promise<SelectionOutcome> {
    if (
      options.workflowIdentity !== options.stateKey.workflowIdentity ||
      options.trustedExecutionDomain !== options.stateKey.trustedExecutionDomain
    )
      return { selection: 'failed', reason: 'state_key_mismatch' };
    try {
      const state = await this.requireState();
      const selectorBytes = await this.readPath(state, gitStatePaths.selector(options.stateKey));
      if (selectorBytes === null) return this.emptySelection(options);
      let selector: StateSelectorV1;
      try {
        selector = decodeValidatedSelector(selectorBytes);
      } catch {
        return this.recovery(options, selectorBytes, 'corrupt_accepted_artifact', 'selector_invalid', [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        ]);
      }
      const revision = selector.selectorRevision;
      if (!bytesEqual(canonicalJsonBytes(selector.stateKey), canonicalJsonBytes(options.stateKey))) {
        return this.recovery(options, selectorBytes, 'state_key_mismatch', 'state_key_mismatch', [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        ], revision);
      }
      const markerBytes = await this.readPath(state, gitStatePaths.marker(selector.acceptedMarkerId));
      if (!markerBytes) return this.recovery(options, selectorBytes, 'corrupt_accepted_artifact', 'marker_invalid', [
        { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        { kind: 'marker_reference', markerId: selector.acceptedMarkerId },
      ], revision);
      let marker: AcceptedStateMarkerV1;
      try {
        marker = decodeValidatedMarker(markerBytes);
      } catch {
        return this.recovery(options, selectorBytes, 'corrupt_accepted_artifact', 'marker_invalid', [
          { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
          { kind: 'marker_bytes', markerId: selector.acceptedMarkerId, sha256: sha256Hex(markerBytes) },
        ], revision);
      }
      if (!selectorMarkerMatches(selector, marker)) return this.recovery(options, selectorBytes, 'integrity_mismatch', 'explicit_state_invalid', [
        { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
      ], revision);
      const registrations = await this.registrations(state, options.stateKey);
      const entry = registrations.find((item) => item.registration.registrationId === marker.registrationId);
      if (!entry || !registrationMarkerMatches(entry.registration, marker)) return this.recovery(options, selectorBytes, 'integrity_mismatch', 'candidate_invalid', [
        { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
        { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
        { kind: 'candidate_reference', candidateId: marker.candidateId },
      ], revision);
      const candidate = await this.readCandidateFrom(state, marker.candidateId);
      if (candidate.status === 'failed') return { selection: 'failed', reason: 'candidate_read_failed' };
      if (candidate.status !== 'present') return this.recovery(options, selectorBytes,
        candidate.status === 'missing' ? 'unavailable_accepted_artifact' : candidate.diagnostic === 'ledger_byte_limit_exceeded' ? 'over_bound_ledger' : 'corrupt_accepted_artifact',
        'candidate_invalid', [{ kind: 'candidate_reference', candidateId: marker.candidateId }], revision);
      const classified = classifyStateBundleV2({
        entryListing: [
          { name: 'manifest.json', isRegularFile: true },
          { name: 'ledger.json', isRegularFile: true },
          { name: 'provider-run-metadata.json', isRegularFile: true },
        ],
        manifestBytes: candidate.bundle.manifestBytes,
        ledgerBytes: candidate.bundle.ledgerBytes,
        providerRunMetadataBytes: candidate.bundle.providerRunMetadataBytes,
      });
      if (classified.kind !== 'valid') return this.recovery(options, selectorBytes,
        classified.kind === 'unsupported_legacy_v1' || (classified.kind === 'invalid' && classified.diagnostic === 'manifest_unknown_version') ? 'contract_version_incompatible' : 'corrupt_accepted_artifact',
        'candidate_invalid', [{ kind: 'candidate_reference', candidateId: marker.candidateId }], revision);
      const compatibility = checkStateManifestV2Compatibility(classified.manifest, {
        stateKey: options.stateKey,
        expectedLedgerSchemaVersion: options.expectedLedgerSchemaVersion,
        expectedPrefixContractVersion: options.expectedPrefixContractVersion,
        cacheContractIdentity: options.cacheContractIdentity,
        currentBaseSha: options.currentBaseSha,
        currentBaseRef: options.currentBaseRef,
        headRelationship: selector.currentHeadSha === options.currentHeadSha ? 'same' : (options.headRelationship ?? 'unknown'),
        provenanceTrusted: options.provenanceTrusted,
      });
      if (compatibility.kind === 'incompatible') return this.recovery(options, selectorBytes, compatibility.code, compatibility.code === 'unsafe_provenance' ? 'provenance_invalid' : compatibility.code, [{ kind: 'candidate_reference', candidateId: marker.candidateId }], revision);
      const common = {
        schemaVersion: 1 as const, stateKey: options.stateKey, currentHeadSha: options.currentHeadSha, currentBaseSha: options.currentBaseSha, currentBaseRef: options.currentBaseRef,
        observedSelectorBytes: new Uint8Array(selectorBytes), observedSelectorRevision: revision, observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes),
      };
      const predecessorBytes = candidate.bundle;
      if (compatibility.kind === 'compatible_continuation' && selector.currentBaseSha === options.currentBaseSha && (selector.currentHeadSha === options.currentHeadSha || options.headRelationship === 'descendant')) {
        return { selection: 'selected', snapshot: finalize({ ...common, kind: 'continuation_selected', transitionPlan: 'continuation', markerId: marker.markerId, predecessorBytes }) };
      }
      const resetReason = compatibility.kind === 'expected_invalidation' ? compatibility.code : selector.currentBaseSha !== options.currentBaseSha ? 'base_change' : selector.currentHeadSha !== options.currentHeadSha ? 'head_history_discontinuity' : 'cache_contract_change';
      return { selection: 'selected', snapshot: finalize({ ...common, kind: 'reset_selected', transitionPlan: 'reset', markerId: marker.markerId, predecessorBytes, resetReason }) };
    } catch (error) {
      if (error instanceof SelectionSnapshotLimitError) return { selection: 'failed', reason: 'selection_snapshot_limit_exceeded' };
      if (error instanceof StoreTransactionError) return { selection: 'failed', reason: error.reason };
      return { selection: 'unknown', reason: 'selection_outcome_unknown' };
    }
  }

  private emptySelection(options: SelectionOptions): SelectionOutcome {
    const common = { schemaVersion: 1 as const, stateKey: options.stateKey, currentHeadSha: options.currentHeadSha, currentBaseSha: options.currentBaseSha, currentBaseRef: options.currentBaseRef, observedSelectorBytes: null, observedSelectorRevision: 'bootstrap' as const, observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null) };
    return options.explicitRestore
      ? { selection: 'selected', snapshot: finalize({ ...common, kind: 'explicit_restore_invalid', failure: 'explicit_state_invalid' }) }
      : { selection: 'selected', snapshot: finalize({ ...common, kind: 'bootstrap_selected', transitionPlan: 'bootstrap' }) };
  }

  private recovery(
    options: SelectionOptions,
    selectorBytes: Uint8Array,
    reason: RecoveryReason,
    failure: 'explicit_state_invalid' | 'selector_invalid' | 'marker_invalid' | 'candidate_invalid' | 'provenance_invalid' | 'state_key_mismatch' | 'contract_version_incompatible' | 'over_bound_ledger',
    evidence: readonly RecoveryEvidence[],
    observedRevision?: SelectorRevision,
  ): SelectionOutcome {
    const common = { schemaVersion: 1 as const, stateKey: options.stateKey, currentHeadSha: options.currentHeadSha, currentBaseSha: options.currentBaseSha, currentBaseRef: options.currentBaseRef, observedSelectorBytes: new Uint8Array(selectorBytes), observedSelectorRevision: observedRevision ?? (`invalid:${sha256Hex(selectorBytes)}` as SelectorRevision), observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes) };
    return options.explicitRestore
      ? { selection: 'selected', snapshot: finalize({ ...common, kind: 'explicit_restore_invalid', failure }) }
      : { selection: 'selected', snapshot: finalize({ ...common, kind: 'recovery_root_selected', transitionPlan: 'recovery_root', recoveryReason: reason, recoveryEvidence: evidence }) };
  }

  private async requireState(): Promise<GitStateRef> {
    const state = await this.transport.read();
    if (state === null) throw new StoreTransactionError('store_capability_unsupported');
    return state;
  }

  private async readPath(state: GitStateRef, path: string): Promise<Uint8Array | null> {
    return this.transport.readBlob(state, path);
  }

  private async readCandidateFrom(state: GitStateRef, candidateId: CandidateId): Promise<CandidateReadResult> {
    const manifest = await this.readPath(state, gitStatePaths.candidateFile(candidateId, 'manifest.json'));
    const ledger = await this.readPath(state, gitStatePaths.candidateFile(candidateId, 'ledger.json'));
    const metadata = await this.readPath(state, gitStatePaths.candidateFile(candidateId, 'provider-run-metadata.json'));
    const evidence = { manifest: manifest ? { status: 'present' as const, sha256: sha256Hex(manifest) } : { status: 'missing' as const }, ledger: ledger ? { status: 'present' as const, sha256: sha256Hex(ledger) } : { status: 'missing' as const }, providerRunMetadata: metadata ? { status: 'present' as const, sha256: sha256Hex(metadata) } : { status: 'missing' as const } };
    if (!manifest && !ledger && !metadata) return { status: 'missing', evidence };
    if (!manifest || !ledger || !metadata) return { status: 'unsafe', diagnostic: 'bundle_listing_mismatch', evidence };
    if (manifest.byteLength > MANIFEST_MAX_BYTES || ledger.byteLength > LEDGER_MAX_BYTES || metadata.byteLength > METADATA_MAX_BYTES)
      return { status: 'unsafe', diagnostic: ledger.byteLength > LEDGER_MAX_BYTES ? 'ledger_byte_limit_exceeded' : 'provider_run_metadata_byte_limit_exceeded', evidence };
    return { status: 'present', bundle: { manifestBytes: manifest, ledgerBytes: ledger, providerRunMetadataBytes: metadata } };
  }

  private async registrations(state: GitStateRef, stateKey: StateKeyV2): Promise<{ readonly registration: CandidateRegistrationV1; readonly bytes: Uint8Array }[]> {
    const prefix = `m4-state/v1/states/${stateKeyDigest(stateKey)}/registrations/`;
    const results: { registration: CandidateRegistrationV1; bytes: Uint8Array }[] = [];
    for (const path of state.entries.keys()) {
      if (!path.startsWith(prefix)) continue;
      const bytes = await this.readPath(state, path);
      if (!bytes) throw new StoreTransactionError('store_transaction_failed');
      results.push({ registration: decodeValidatedRegistration(bytes), bytes });
    }
    return results;
  }
}

function revision(bytes: Uint8Array | null): SelectorRevision {
  if (bytes === null) return 'bootstrap';
  try { return decodeValidatedSelector(bytes).selectorRevision; } catch { return `invalid:${sha256Hex(bytes)}` as SelectorRevision; }
}
function scopeOf(registration: CandidateRegistrationV1): CompetingScope {
  return { stateKey: registration.stateKey, sessionEpoch: registration.sessionEpoch, observedSelectorRevision: registration.observedSelectorRevision, predecessorMarkerId: registration.predecessorMarkerId, predecessorManifestSha256: registration.predecessorManifestSha256, predecessorLedgerSha256: registration.predecessorLedgerSha256, ledgerEpoch: registration.ledgerEpoch, targetStateGeneration: registration.stateGeneration, interactionId: registration.interactionId };
}
function matchesScope(registration: CandidateRegistrationV1, scope: CompetingScope): boolean { return bytesEqual(canonicalJsonBytes(scopeOf(registration)), canonicalJsonBytes(scope)); }
function bundlesEqual(left: CandidateBundleBytes, right: CandidateBundleBytes): boolean { return bytesEqual(left.manifestBytes, right.manifestBytes) && bytesEqual(left.ledgerBytes, right.ledgerBytes) && bytesEqual(left.providerRunMetadataBytes, right.providerRunMetadataBytes); }
function unsafeEvidence() { return { manifest: { status: 'unsafe' as const }, ledger: { status: 'unsafe' as const }, providerRunMetadata: { status: 'unsafe' as const } }; }

function finalize(snapshot: unknown): StateSelectionSnapshot {
  const provisional = { ...(snapshot as Record<string, unknown>), selectionSnapshotId: '' } as StateSelectionSnapshot;
  return { ...provisional, selectionSnapshotId: computeSelectionSnapshotId(provisional) } as StateSelectionSnapshot;
}

function selectorMarkerMatches(selector: StateSelectorV1, marker: AcceptedStateMarkerV1): boolean {
  return (
    marker.markerId === selector.acceptedMarkerId &&
    marker.candidateId === selector.candidateId &&
    bytesEqual(canonicalJsonBytes(marker.stateKey), canonicalJsonBytes(selector.stateKey)) &&
    marker.sessionEpoch === selector.sessionEpoch &&
    marker.stateGeneration === selector.stateGeneration &&
    marker.ledgerEpoch === selector.ledgerEpoch &&
    bytesEqual(canonicalJsonBytes(marker.transition), canonicalJsonBytes(selector.transition)) &&
    marker.observedSelectorRevision === selector.previousSelectorRevision &&
    marker.manifestSha256 === selector.manifestSha256 &&
    marker.candidateLedgerSha256 === selector.candidateLedgerSha256 &&
    marker.providerRunMetadataSha256 === selector.providerRunMetadataSha256 &&
    marker.metadataSemanticSha256 === selector.metadataSemanticSha256 &&
    marker.consumedInputSha256 === selector.consumedInputSha256 &&
    marker.resultSha256 === selector.resultSha256 &&
    marker.traceSha256 === selector.traceSha256
  );
}

function registrationMarkerMatches(registration: CandidateRegistrationV1, marker: AcceptedStateMarkerV1): boolean {
  return (
    registration.registrationId === marker.registrationId &&
    registration.candidateId === marker.candidateId &&
    bytesEqual(canonicalJsonBytes(registration.stateKey), canonicalJsonBytes(marker.stateKey)) &&
    registration.sessionEpoch === marker.sessionEpoch &&
    registration.stateGeneration === marker.stateGeneration &&
    registration.ledgerEpoch === marker.ledgerEpoch &&
    bytesEqual(canonicalJsonBytes(registration.transition), canonicalJsonBytes(marker.transition)) &&
    registration.predecessorMarkerId === marker.predecessorMarkerId &&
    registration.predecessorManifestSha256 === marker.predecessorManifestSha256 &&
    registration.predecessorLedgerSha256 === marker.predecessorLedgerSha256 &&
    registration.observedSelectorRevision === marker.observedSelectorRevision &&
    registration.manifestSha256 === marker.manifestSha256 &&
    registration.candidateLedgerSha256 === marker.candidateLedgerSha256 &&
    registration.providerRunMetadataSha256 === marker.providerRunMetadataSha256 &&
    registration.metadataSemanticSha256 === marker.metadataSemanticSha256 &&
    registration.consumedInputSha256 === marker.consumedInputSha256 &&
    registration.resultSha256 === marker.resultSha256 &&
    registration.traceSha256 === marker.traceSha256 &&
    registration.producingRunId === marker.producingRunId &&
    registration.producingRunAttempt === marker.producingRunAttempt
  );
}
