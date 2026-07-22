import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  checkStateManifestV2Compatibility,
  classifyStateBundleV2,
  LEDGER_MAX_BYTES,
  MANIFEST_MAX_BYTES,
  METADATA_MAX_BYTES,
} from '../state-v2/index.js';
import { bytesEqual, decodeRecord, encodeRecord, recordSha256 } from './codec.js';
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
import {
  competingScopeDigest,
  gitStatePaths,
  isAllowedGitStatePath,
  stateKeyDigest,
} from './github-state-paths.js';
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
const refRetryLimit = 4;
const REGISTRATION_COUNTER_KIND = 'm4-registration-counter';

interface RegistrationCounterV1 {
  readonly schemaVersion: 1;
  readonly kind: typeof REGISTRATION_COUNTER_KIND;
  readonly stateKeyDigest: string;
  readonly lastAllocatedSequence: string;
  readonly lastRegistrationId?: string;
  readonly lastCompetingScopeDigest?: string;
}

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
    if (
      !/^[1-9][0-9]{0,18}$/.test(input.runId) ||
      !Number.isInteger(input.runAttempt) ||
      input.runAttempt < 1 ||
      input.runAttempt > 2_147_483_647
    ) {
      throw new StoreTransactionError('store_capability_unsupported');
    }
    const created = await this.transport.initialize(input.defaultBranchCommitSha);
    if (created === 'unknown' && (await this.transport.read()) === null) {
      throw new StoreTransactionError('store_transaction_failed');
    }
    const sentinel = canonicalJsonBytes({
      schemaVersion: 1,
      kind: 'agentic-pr-review-m4-state-store',
      namespace: 'm4-state-v1',
    });
    const probe = canonicalJsonBytes({
      schemaVersion: 1,
      kind: 'm4-store-capability-probe',
      runId: input.runId,
      runAttempt: input.runAttempt,
      stateKeyDigest: stateKeyDigest(input.stateKey),
    });
    const probePath = gitStatePaths.probe(input.runId, input.runAttempt);
    for (let attempt = 0; attempt < refRetryLimit; attempt += 1) {
      const state = await this.requireState();
      const existing = await this.readPath(state, gitStatePaths.sentinel);
      if (existing === null && hasM4NamespaceEntries(state)) {
        throw new StoreTransactionError('store_transaction_failed');
      }
      if (existing !== null && !bytesEqual(existing, sentinel))
        throw new StoreTransactionError('store_transaction_failed');
      const existingProbe = await this.readPath(state, probePath);
      if (existingProbe !== null && !bytesEqual(existingProbe, probe))
        throw new StoreTransactionError('store_transaction_failed');
      if (existing !== null && existingProbe !== null) return;
      const result = await this.transport.commit(
        state,
        new Map([
          ...(existing === null ? [[gitStatePaths.sentinel, sentinel] as const] : []),
          ...(existingProbe === null ? [[probePath, probe] as const] : []),
        ]),
        'm4 state capability probe',
      );
      const reread = await this.transport.read();
      if (
        reread &&
        bytesEqual(
          (await this.readPath(reread, gitStatePaths.sentinel)) ?? new Uint8Array(),
          sentinel,
        ) &&
        bytesEqual((await this.readPath(reread, probePath)) ?? new Uint8Array(), probe)
      ) {
        return;
      }
      if (result === 'unknown') throw new StoreTransactionError('store_transaction_failed');
    }
    throw new StoreTransactionError('store_capability_unsupported');
  }

  async uploadCandidate(
    candidateId: CandidateId,
    bundle: CandidateBundleBytes,
  ): Promise<CandidateUploadOutcome> {
    if (!SHA256_HEX.test(candidateId)) return { kind: 'existing_content_conflict' };
    if (
      bundle.manifestBytes.byteLength > MANIFEST_MAX_BYTES ||
      bundle.ledgerBytes.byteLength > LEDGER_MAX_BYTES ||
      bundle.providerRunMetadataBytes.byteLength > METADATA_MAX_BYTES
    )
      return { kind: 'existing_content_conflict' };
    const locator = {
      kind: 'store-object' as const,
      namespace: 'm4-state-v1' as const,
      objectId: `candidate-${candidateId}` as const,
    };
    for (let attempt = 0; attempt < refRetryLimit; attempt += 1) {
      const state = await this.requireState();
      const existing = await this.readCandidateFrom(state, candidateId);
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
          [
            gitStatePaths.candidateFile(candidateId, 'provider-run-metadata.json'),
            bundle.providerRunMetadataBytes,
          ],
        ]),
        `m4 state candidate ${candidateId}`,
      );
      const reread = await this.transport.read();
      if (!reread) return { kind: 'outcome_unknown', locator };
      const reconciled = await this.readCandidateFrom(reread, candidateId);
      if (reconciled.status === 'present' && bundlesEqual(reconciled.bundle, bundle)) {
        return { kind: result === 'applied' ? 'created' : 'already_exists_same', locator };
      }
      if (result === 'unknown') return { kind: 'outcome_unknown', locator };
    }
    return { kind: 'outcome_unknown', locator };
  }

  async readCandidate(candidateId: CandidateId): Promise<CandidateReadResult> {
    if (!SHA256_HEX.test(candidateId))
      return {
        status: 'unsafe',
        diagnostic: 'bundle_listing_mismatch',
        evidence: unsafeEvidence(),
      };
    const state = await this.requireState();
    return this.readCandidateFrom(state, candidateId);
  }

  async registerCandidate(draft: CandidateRegistrationDraft): Promise<RegistrationWriteResult> {
    const registrationId = computeRegistrationId(draft);
    for (let attempt = 0; attempt < 4; attempt += 1) {
      const state = await this.requireState();
      const { registrations: all, counter } = await this.registrationState(state, draft.stateKey);
      const same = all.find((entry) => entry.registration.registrationId === registrationId);
      if (same) return { kind: 'already_exists_same', registration: same.registration };

      const counterPath = gitStatePaths.counter(draft.stateKey);
      const last = Number(counter.lastAllocatedSequence);
      if (last >= counterMax) return { kind: 'registration_sequence_overflow' };

      const registration = materializeRegistration(draft, String(last + 1));
      const scope = scopeOf(registration);
      const path = gitStatePaths.registration(
        scope,
        registration.registrationSequence,
        registration.registrationId,
      );
      const bytes = encodeValidatedRecord(registration);
      const nextCounter = encodeRecord({
        ...counter,
        lastAllocatedSequence: registration.registrationSequence,
        lastRegistrationId: registration.registrationId,
        lastCompetingScopeDigest: competingScopeDigest(scope),
      } satisfies RegistrationCounterV1);
      const result = await this.transport.commit(
        state,
        new Map([
          [counterPath, nextCounter],
          [path, bytes],
        ]),
        `m4 state registration ${registration.registrationId}`,
      );
      if (result === 'applied') return { kind: 'created', registration };

      const reread = await this.transport.read();
      if (!reread) return { kind: 'outcome_unknown', registration };
      const reconciled = await this.registrations(reread, draft.stateKey);
      const existing = reconciled.find(
        (entry) => entry.registration.registrationId === registrationId,
      );
      if (existing) return { kind: 'already_exists_same', registration: existing.registration };
      if (result === 'unknown') return { kind: 'outcome_unknown', registration };
    }
    return { kind: 'registration_write_conflict' };
  }

  async readSelector(
    stateKey: StateKeyV2,
  ): Promise<{ readonly bytes: Uint8Array | null; readonly selector: StateSelectorV1 | null }> {
    const bytes = await this.readPath(await this.requireState(), gitStatePaths.selector(stateKey));
    return { bytes, selector: bytes === null ? null : decodeValidatedSelector(bytes) };
  }

  /**
   * Reads only enough selector state to choose a conservative ancestry input.
   * Selection remains the sole authority that classifies malformed bytes into
   * automatic recovery versus explicit-restore failure.
   */
  async peekSelectorForComparison(
    stateKey: StateKeyV2,
  ): Promise<{ readonly selector: StateSelectorV1 | null; readonly revision: SelectorRevision }> {
    const bytes = await this.readPath(await this.requireState(), gitStatePaths.selector(stateKey));
    if (bytes === null) return { selector: null, revision: 'bootstrap' };
    try {
      const selector = decodeValidatedSelector(bytes);
      return { selector, revision: selector.selectorRevision };
    } catch {
      return { selector: null, revision: selectorRevisionFromBytes(bytes) };
    }
  }

  async writeMarker(marker: AcceptedStateMarkerV1): Promise<WriteOutcome<AcceptedStateMarkerV1>> {
    validateAcceptedStateMarker(marker);
    const path = gitStatePaths.marker(marker.markerId);
    const bytes = encodeValidatedRecord(marker);
    for (let attempt = 0; attempt < refRetryLimit; attempt += 1) {
      const state = await this.requireState();
      const existing = await this.readPath(state, path);
      if (existing) {
        try {
          const value = decodeValidatedMarker(existing);
          return bytesEqual(existing, bytes)
            ? { kind: 'already_exists_same', value }
            : { kind: 'existing_content_conflict' };
        } catch {
          return { kind: 'existing_content_conflict' };
        }
      }
      const outcome = await this.transport.commit(
        state,
        new Map([[path, bytes]]),
        `m4 state marker ${marker.markerId}`,
      );
      if (outcome === 'applied') return { kind: 'created', value: marker };
      const reread = await this.transport.read();
      if (!reread) return { kind: 'outcome_unknown' };
      if (bytesEqual((await this.readPath(reread, path)) ?? new Uint8Array(), bytes)) {
        return { kind: 'already_exists_same', value: marker };
      }
      if (outcome === 'unknown') return { kind: 'outcome_unknown' };
    }
    return { kind: 'outcome_unknown' };
  }

  /** Persist an idempotent public receipt after a candidate acceptance attempt. */
  async writePublicationReceipt(input: {
    readonly markerId: MarkerId;
    readonly stateKey: StateKeyV2;
    readonly selectorRevision: SelectorRevision;
    readonly acceptingRunId: string;
    readonly acceptingRunAttempt: number;
    readonly publicationStatus: 'not_attempted' | 'succeeded' | 'failed' | 'unknown';
    readonly commentId?: string;
    readonly bodySha256?: string;
    readonly failureCode?:
      | 'comment_create_failed'
      | 'comment_update_failed'
      | 'comment_readback_failed'
      | 'comment_outcome_unknown';
    readonly recordedAt: string;
  }): Promise<'created' | 'already_exists_same' | 'failed' | 'unknown'> {
    if (
      !SHA256_HEX.test(input.markerId) ||
      !/^sha256:[a-f0-9]{64}$/.test(input.selectorRevision) ||
      !/^[1-9][0-9]{0,18}$/.test(input.acceptingRunId) ||
      !Number.isInteger(input.acceptingRunAttempt) ||
      input.acceptingRunAttempt < 1 ||
      input.acceptingRunAttempt > 2_147_483_647 ||
      !isCanonicalTimestamp(input.recordedAt)
    ) {
      return 'failed';
    }
    const common = {
      schemaVersion: 1,
      markerId: input.markerId,
      stateKeyDigest: stateKeyDigest(input.stateKey),
      selectorRevision: input.selectorRevision,
      acceptingRunId: input.acceptingRunId,
      acceptingRunAttempt: input.acceptingRunAttempt,
      publicationStatus: input.publicationStatus,
      recordedAt: input.recordedAt,
    };
    const receipt =
      input.publicationStatus === 'succeeded'
        ? input.commentId &&
          /^[1-9][0-9]{0,18}$/.test(input.commentId) &&
          input.bodySha256 &&
          SHA256_HEX.test(input.bodySha256)
          ? { ...common, commentId: input.commentId, bodySha256: input.bodySha256 }
          : null
        : input.publicationStatus === 'failed'
          ? input.failureCode &&
            ['comment_create_failed', 'comment_update_failed', 'comment_readback_failed'].includes(
              input.failureCode,
            )
            ? { ...common, failureCode: input.failureCode }
            : null
          : input.publicationStatus === 'unknown'
            ? input.failureCode === 'comment_outcome_unknown'
              ? { ...common, failureCode: input.failureCode }
              : null
            : { ...common };
    if (!receipt) return 'failed';
    const bytes = encodeRecord(receipt, 4096);
    const path = gitStatePaths.receipt(
      input.markerId,
      input.acceptingRunId,
      input.acceptingRunAttempt,
    );
    for (let attempt = 0; attempt < refRetryLimit; attempt += 1) {
      const state = await this.requireState();
      const existing = await this.readPath(state, path);
      if (existing !== null) {
        decodePublicationReceipt(existing);
        return bytesEqual(existing, bytes) ? 'already_exists_same' : 'failed';
      }
      const outcome = await this.transport.commit(
        state,
        new Map([[path, bytes]]),
        'm4 state receipt',
      );
      if (outcome === 'applied') return 'created';
      const reread = await this.transport.read();
      if (!reread) return outcome === 'unknown' ? 'unknown' : 'failed';
      const observed = await this.readPath(reread, path);
      if (observed !== null) {
        decodePublicationReceipt(observed);
        return bytesEqual(observed, bytes) ? 'already_exists_same' : 'failed';
      }
      if (outcome === 'unknown') return 'unknown';
    }
    return 'unknown';
  }

  async readMarker(
    stateKey: StateKeyV2,
    markerId: MarkerId,
  ): Promise<{ readonly bytes: Uint8Array | null; readonly marker: AcceptedStateMarkerV1 | null }> {
    if (!SHA256_HEX.test(markerId)) throw new Error('invalid marker id');
    const bytes = await this.readPath(await this.requireState(), gitStatePaths.marker(markerId));
    const marker = bytes === null ? null : decodeValidatedMarker(bytes);
    if (marker && !bytesEqual(canonicalJsonBytes(marker.stateKey), canonicalJsonBytes(stateKey)))
      return { bytes, marker: null };
    return { bytes, marker };
  }

  async casSelector(
    expectedRevision: SelectorRevision,
    selector: StateSelectorV1,
  ): Promise<SelectorCasOutcome> {
    validateStateSelector(selector);
    const path = gitStatePaths.selector(selector.stateKey);
    const bytes = encodeValidatedRecord(selector);
    for (let attempt = 0; attempt < refRetryLimit; attempt += 1) {
      const state = await this.requireState();
      const current = await this.readPath(state, path);
      const currentRevision = revision(current);
      if (
        selector.previousSelectorRevision !== expectedRevision ||
        currentRevision !== expectedRevision
      ) {
        return { kind: 'rejected_with_current_revision', currentRevision };
      }
      if (current) {
        try {
          const decoded = decodeValidatedSelector(current);
          if (decoded.selectorId === selector.selectorId) {
            return { kind: 'already_applied_same_target', selector: decoded };
          }
        } catch {
          /* revision carries invalid state and is rejected above on a non-bootstrap CAS. */
        }
      }
      const outcome = await this.transport.commit(
        state,
        new Map([[path, bytes]]),
        `m4 state selector ${selector.selectorId}`,
      );
      if (outcome === 'applied') return { kind: 'applied', selector };
      const reread = await this.transport.read();
      if (!reread) return { kind: 'outcome_unknown' };
      const observed = await this.readPath(reread, path);
      if (observed && bytesEqual(observed, bytes)) {
        return { kind: 'already_applied_same_target', selector };
      }
      if (outcome === 'unknown') return { kind: 'outcome_unknown' };
      if (revision(observed) !== expectedRevision) {
        return { kind: 'rejected_with_current_revision', currentRevision: revision(observed) };
      }
    }
    return { kind: 'outcome_unknown' };
  }

  async createAcceptanceSnapshot(
    expectedObservedSelectorRevision: SelectorRevision,
    competingScope: CompetingScope,
    selectionSnapshotId: string,
  ): Promise<AcceptanceSnapshot> {
    const state = await this.requireState();
    const currentRevision = revision(
      await this.readPath(state, gitStatePaths.selector(competingScope.stateKey)),
    );
    if (currentRevision !== expectedObservedSelectorRevision)
      throw new SelectorRevisionMismatchError(currentRevision);
    const { registrations: allRegistrations, counter } = await this.registrationState(
      state,
      competingScope.stateKey,
    );
    const registrations = allRegistrations.filter((entry) =>
      matchesScope(entry.registration, competingScope),
    );
    const cutoff = counter.lastAllocatedSequence;
    const frozen: FrozenRegistration[] = [];
    let total = 0;
    for (const entry of registrations.sort((a, b) => {
      const bySequence = compareDecimalIds(
        a.registration.registrationSequence,
        b.registration.registrationSequence,
      );
      return (
        bySequence || a.registration.registrationId.localeCompare(b.registration.registrationId)
      );
    })) {
      if (acceptanceSnapshotLimitExceeded(frozen.length, total, entry.bytes.byteLength))
        throw new SelectionSnapshotLimitError();
      total += entry.bytes.byteLength;
      frozen.push({
        registrationSequence: entry.registration.registrationSequence,
        registrationId: entry.registration.registrationId,
        registrationRecordSha256: recordSha256(
          entry.bytes,
        ) as FrozenRegistration['registrationRecordSha256'],
        registrationBytes: new Uint8Array(entry.bytes),
        registration: structuredClone(entry.registration),
      });
    }
    const enumeration = {
      kind: 'complete' as const,
      matchingRegistrationCount: frozen.length,
      matchingRegistrationBytes: total,
    };
    return {
      schemaVersion: 1,
      selectionSnapshotId: selectionSnapshotId as AcceptanceSnapshot['selectionSnapshotId'],
      expectedObservedSelectorRevision,
      currentSelectorRevision: currentRevision,
      competingScope,
      cutoff: cutoff as AcceptanceSnapshot['cutoff'],
      registrations: frozen,
      enumeration,
      candidateSetDigest: computeCandidateSetDigest(
        competingScope,
        cutoff,
        frozen.map((entry) => ({
          registrationSequence: entry.registrationSequence,
          registrationId: entry.registrationId,
          registrationRecordSha256: entry.registrationRecordSha256,
        })),
        enumeration,
      ),
    };
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
      if (
        options.expectedObservedSelectorRevision !== undefined &&
        selectorRevisionFromBytes(selectorBytes) !== options.expectedObservedSelectorRevision
      ) {
        return { selection: 'failed', reason: 'selector_read_failed' };
      }
      if (selectorBytes === null) return this.emptySelection(options);
      let selector: StateSelectorV1;
      try {
        selector = decodeValidatedSelector(selectorBytes);
      } catch {
        return this.recovery(
          options,
          selectorBytes,
          'corrupt_accepted_artifact',
          'selector_invalid',
          [{ kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) }],
        );
      }
      const revision = selector.selectorRevision;
      if (
        !bytesEqual(canonicalJsonBytes(selector.stateKey), canonicalJsonBytes(options.stateKey))
      ) {
        return this.recovery(
          options,
          selectorBytes,
          'state_key_mismatch',
          'state_key_mismatch',
          [{ kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) }],
          revision,
        );
      }
      const markerBytes = await this.readPath(
        state,
        gitStatePaths.marker(selector.acceptedMarkerId),
      );
      if (!markerBytes)
        return this.recovery(
          options,
          selectorBytes,
          'corrupt_accepted_artifact',
          'marker_invalid',
          [
            { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
            { kind: 'marker_reference', markerId: selector.acceptedMarkerId },
          ],
          revision,
        );
      let marker: AcceptedStateMarkerV1;
      try {
        marker = decodeValidatedMarker(markerBytes);
      } catch {
        return this.recovery(
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
          revision,
        );
      }
      if (!selectorMarkerMatches(selector, marker))
        return this.recovery(
          options,
          selectorBytes,
          'integrity_mismatch',
          'explicit_state_invalid',
          [
            { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
            { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
          ],
          revision,
        );
      // Orphaned malformed registrations cannot be allowed to block recovery
      // of the independently bound accepted chain.  Mutation paths retain a
      // strict scan before issuing any new registration.
      const registrations = await this.registrations(state, options.stateKey, true);
      await this.validateSelectionControlPlane(state, options.stateKey, registrations);
      const entry = registrations.find(
        (item) => item.registration.registrationId === marker.registrationId,
      );
      if (!entry || !registrationMarkerMatches(entry.registration, marker))
        return this.recovery(
          options,
          selectorBytes,
          'integrity_mismatch',
          'candidate_invalid',
          [
            { kind: 'selector_bytes', sha256: sha256Hex(selectorBytes) },
            { kind: 'marker_bytes', markerId: marker.markerId, sha256: sha256Hex(markerBytes) },
            { kind: 'candidate_reference', candidateId: marker.candidateId },
          ],
          revision,
        );
      const candidate = await this.readCandidateFrom(state, marker.candidateId);
      if (candidate.status === 'failed')
        return { selection: 'failed', reason: 'candidate_read_failed' };
      if (candidate.status !== 'present')
        return this.recovery(
          options,
          selectorBytes,
          candidate.status === 'missing'
            ? 'unavailable_accepted_artifact'
            : candidate.diagnostic === 'ledger_byte_limit_exceeded'
              ? 'over_bound_ledger'
              : 'corrupt_accepted_artifact',
          'candidate_invalid',
          [{ kind: 'candidate_reference', candidateId: marker.candidateId }],
          revision,
        );
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
      if (classified.kind !== 'valid')
        return this.recovery(
          options,
          selectorBytes,
          classified.kind === 'unsupported_legacy_v1' ||
            (classified.kind === 'invalid' && classified.diagnostic === 'manifest_unknown_version')
            ? 'contract_version_incompatible'
            : 'corrupt_accepted_artifact',
          'candidate_invalid',
          [{ kind: 'candidate_reference', candidateId: marker.candidateId }],
          revision,
        );
      if (!manifestProvenanceMatches(classified.manifest, options))
        return this.recovery(
          options,
          selectorBytes,
          'unsafe_provenance',
          'provenance_invalid',
          [{ kind: 'candidate_reference', candidateId: marker.candidateId }],
          revision,
        );
      const compatibility = checkStateManifestV2Compatibility(classified.manifest, {
        stateKey: options.stateKey,
        expectedLedgerSchemaVersion: options.expectedLedgerSchemaVersion,
        expectedPrefixContractVersion: options.expectedPrefixContractVersion,
        cacheContractIdentity: options.cacheContractIdentity,
        currentBaseSha: options.currentBaseSha,
        currentBaseRef: options.currentBaseRef,
        headRelationship:
          selector.currentHeadSha === options.currentHeadSha
            ? 'same'
            : (options.headRelationship ?? 'unknown'),
        provenanceTrusted: options.provenanceTrusted,
      });
      if (compatibility.kind === 'incompatible')
        return this.recovery(
          options,
          selectorBytes,
          compatibility.code,
          compatibility.code === 'unsafe_provenance' ? 'provenance_invalid' : compatibility.code,
          [{ kind: 'candidate_reference', candidateId: marker.candidateId }],
          revision,
        );
      const common = {
        schemaVersion: 1 as const,
        stateKey: options.stateKey,
        currentHeadSha: options.currentHeadSha,
        currentBaseSha: options.currentBaseSha,
        currentBaseRef: options.currentBaseRef,
        observedSelectorBytes: new Uint8Array(selectorBytes),
        observedSelectorRevision: revision,
        observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes),
      };
      const predecessorBytes = candidate.bundle;
      if (
        compatibility.kind === 'compatible_continuation' &&
        selector.currentBaseSha === options.currentBaseSha &&
        (selector.currentHeadSha === options.currentHeadSha ||
          options.headRelationship === 'descendant')
      ) {
        return {
          selection: 'selected',
          snapshot: finalize({
            ...common,
            kind: 'continuation_selected',
            transitionPlan: 'continuation',
            markerId: marker.markerId,
            predecessorBytes,
          }),
        };
      }
      const resetReason =
        compatibility.kind === 'expected_invalidation'
          ? compatibility.code
          : selector.currentBaseSha !== options.currentBaseSha
            ? 'base_change'
            : selector.currentHeadSha !== options.currentHeadSha
              ? 'head_history_discontinuity'
              : 'cache_contract_change';
      return {
        selection: 'selected',
        snapshot: finalize({
          ...common,
          kind: 'reset_selected',
          transitionPlan: 'reset',
          markerId: marker.markerId,
          predecessorBytes,
          resetReason,
        }),
      };
    } catch (error) {
      if (error instanceof SelectionSnapshotLimitError)
        return { selection: 'failed', reason: 'selection_snapshot_limit_exceeded' };
      if (error instanceof StoreTransactionError)
        return { selection: 'failed', reason: error.reason };
      return { selection: 'unknown', reason: 'selection_outcome_unknown' };
    }
  }

  private emptySelection(options: SelectionOptions): SelectionOutcome {
    const common = {
      schemaVersion: 1 as const,
      stateKey: options.stateKey,
      currentHeadSha: options.currentHeadSha,
      currentBaseSha: options.currentBaseSha,
      currentBaseRef: options.currentBaseRef,
      observedSelectorBytes: null,
      observedSelectorRevision: 'bootstrap' as const,
      observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
    };
    return options.explicitRestore
      ? {
          selection: 'selected',
          snapshot: finalize({
            ...common,
            kind: 'explicit_restore_invalid',
            failure: 'explicit_state_invalid',
          }),
        }
      : {
          selection: 'selected',
          snapshot: finalize({
            ...common,
            kind: 'bootstrap_selected',
            transitionPlan: 'bootstrap',
          }),
        };
  }

  private recovery(
    options: SelectionOptions,
    selectorBytes: Uint8Array,
    reason: RecoveryReason,
    failure:
      | 'explicit_state_invalid'
      | 'selector_invalid'
      | 'marker_invalid'
      | 'candidate_invalid'
      | 'provenance_invalid'
      | 'state_key_mismatch'
      | 'contract_version_incompatible'
      | 'over_bound_ledger',
    evidence: readonly RecoveryEvidence[],
    observedRevision?: SelectorRevision,
  ): SelectionOutcome {
    const common = {
      schemaVersion: 1 as const,
      stateKey: options.stateKey,
      currentHeadSha: options.currentHeadSha,
      currentBaseSha: options.currentBaseSha,
      currentBaseRef: options.currentBaseRef,
      observedSelectorBytes: new Uint8Array(selectorBytes),
      observedSelectorRevision:
        observedRevision ?? (`invalid:${sha256Hex(selectorBytes)}` as SelectorRevision),
      observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(selectorBytes),
    };
    return options.explicitRestore
      ? {
          selection: 'selected',
          snapshot: finalize({ ...common, kind: 'explicit_restore_invalid', failure }),
        }
      : {
          selection: 'selected',
          snapshot: finalize({
            ...common,
            kind: 'recovery_root_selected',
            transitionPlan: 'recovery_root',
            recoveryReason: reason,
            recoveryEvidence: evidence,
          }),
        };
  }

  private async requireState(): Promise<GitStateRef> {
    const state = await this.transport.read();
    if (state === null) throw new StoreTransactionError('store_capability_unsupported');
    this.validateStateTree(state);
    return state;
  }

  private validateStateTree(state: GitStateRef): void {
    for (const [path, entry] of state.entries) {
      if (path === 'm4-state') {
        if (entry.type === 'tree' && entry.mode === '040000') continue;
        throw new StoreTransactionError('store_transaction_failed');
      }
      if (!path.startsWith('m4-state/')) continue;
      if (isM4TreePath(path)) {
        if (entry.type === 'tree' && entry.mode === '040000') continue;
        throw new StoreTransactionError('store_transaction_failed');
      }
      if (!isAllowedGitStatePath(path) || entry.type !== 'blob' || entry.mode !== '100644') {
        throw new StoreTransactionError('store_transaction_failed');
      }
    }
  }

  private async readPath(state: GitStateRef, path: string): Promise<Uint8Array | null> {
    return this.transport.readBlob(state, path);
  }

  private async readCandidateFrom(
    state: GitStateRef,
    candidateId: CandidateId,
  ): Promise<CandidateReadResult> {
    const manifest = await this.readPath(
      state,
      gitStatePaths.candidateFile(candidateId, 'manifest.json'),
    );
    const ledger = await this.readPath(
      state,
      gitStatePaths.candidateFile(candidateId, 'ledger.json'),
    );
    const metadata = await this.readPath(
      state,
      gitStatePaths.candidateFile(candidateId, 'provider-run-metadata.json'),
    );
    const evidence = {
      manifest: manifest
        ? { status: 'present' as const, sha256: sha256Hex(manifest) }
        : { status: 'missing' as const },
      ledger: ledger
        ? { status: 'present' as const, sha256: sha256Hex(ledger) }
        : { status: 'missing' as const },
      providerRunMetadata: metadata
        ? { status: 'present' as const, sha256: sha256Hex(metadata) }
        : { status: 'missing' as const },
    };
    if (!manifest && !ledger && !metadata) return { status: 'missing', evidence };
    if (!manifest || !ledger || !metadata)
      return { status: 'unsafe', diagnostic: 'bundle_listing_mismatch', evidence };
    if (
      manifest.byteLength > MANIFEST_MAX_BYTES ||
      ledger.byteLength > LEDGER_MAX_BYTES ||
      metadata.byteLength > METADATA_MAX_BYTES
    )
      return {
        status: 'unsafe',
        diagnostic:
          ledger.byteLength > LEDGER_MAX_BYTES
            ? 'ledger_byte_limit_exceeded'
            : 'provider_run_metadata_byte_limit_exceeded',
        evidence,
      };
    return {
      status: 'present',
      bundle: { manifestBytes: manifest, ledgerBytes: ledger, providerRunMetadataBytes: metadata },
    };
  }

  private async registrations(
    state: GitStateRef,
    stateKey: StateKeyV2,
    ignoreInvalid = false,
  ): Promise<{ readonly registration: CandidateRegistrationV1; readonly bytes: Uint8Array }[]> {
    const prefix = `m4-state/v1/states/${stateKeyDigest(stateKey)}/registrations/`;
    const results: { registration: CandidateRegistrationV1; bytes: Uint8Array }[] = [];
    for (const [path, entry] of state.entries) {
      if (!path.startsWith(prefix)) continue;
      if (entry.type === 'tree' && entry.mode === '040000') continue;
      if (entry.type !== 'blob' || entry.mode !== '100644' || !isAllowedGitStatePath(path)) {
        if (ignoreInvalid) continue;
        throw new StoreTransactionError('store_transaction_failed');
      }
      try {
        const bytes = await this.readPath(state, path);
        if (!bytes) throw new StoreTransactionError('store_transaction_failed');
        const registration = decodeValidatedRegistration(bytes);
        const expected = gitStatePaths.registration(
          scopeOf(registration),
          registration.registrationSequence,
          registration.registrationId,
        );
        if (path !== expected) throw new StoreTransactionError('store_transaction_failed');
        results.push({ registration, bytes });
      } catch (error) {
        if (!ignoreInvalid || !(error instanceof ContractValidationError)) throw error;
      }
    }
    return results;
  }

  private async registrationState(
    state: GitStateRef,
    stateKey: StateKeyV2,
  ): Promise<{
    readonly registrations: readonly {
      readonly registration: CandidateRegistrationV1;
      readonly bytes: Uint8Array;
    }[];
    readonly counter: RegistrationCounterV1;
  }> {
    const registrations = await this.registrations(state, stateKey);
    const counterBytes = await this.readPath(state, gitStatePaths.counter(stateKey));
    if (counterBytes === null) {
      if (registrations.length > 0) throw new StoreTransactionError('store_transaction_failed');
      return { registrations, counter: initialCounter(stateKey) };
    }
    const counter = decodeRegistrationCounter(counterBytes, stateKey);
    await this.assertCounterIndex(state, stateKey, counter);
    const last = Number(counter.lastAllocatedSequence);
    if (registrations.length !== last) throw new StoreTransactionError('store_transaction_failed');
    const sequences = new Set<string>();
    const identifiers = new Set<string>();
    for (const entry of registrations) {
      const sequence = entry.registration.registrationSequence;
      if (
        Number(sequence) > last ||
        sequences.has(sequence) ||
        identifiers.has(entry.registration.registrationId)
      ) {
        throw new StoreTransactionError('store_transaction_failed');
      }
      sequences.add(sequence);
      identifiers.add(entry.registration.registrationId);
    }
    for (let sequence = 1; sequence <= last; sequence += 1) {
      if (!sequences.has(String(sequence)))
        throw new StoreTransactionError('store_transaction_failed');
    }
    return { registrations, counter };
  }

  private async validateSelectionControlPlane(
    state: GitStateRef,
    stateKey: StateKeyV2,
    registrations: readonly {
      readonly registration: CandidateRegistrationV1;
      readonly bytes: Uint8Array;
    }[],
  ): Promise<void> {
    const counterBytes = await this.readPath(state, gitStatePaths.counter(stateKey));
    if (counterBytes === null) {
      if (registrations.length > 0) throw new StoreTransactionError('store_transaction_failed');
      return;
    }
    await this.assertCounterIndex(
      state,
      stateKey,
      decodeRegistrationCounter(counterBytes, stateKey),
    );
  }

  private async assertCounterIndex(
    state: GitStateRef,
    stateKey: StateKeyV2,
    counter: RegistrationCounterV1,
  ): Promise<void> {
    if (counter.lastAllocatedSequence === '0') return;
    if (!counter.lastRegistrationId || !counter.lastCompetingScopeDigest) {
      throw new StoreTransactionError('store_transaction_failed');
    }
    const path = `m4-state/v1/states/${stateKeyDigest(stateKey)}/registrations/${counter.lastCompetingScopeDigest}/${counter.lastAllocatedSequence}-${counter.lastRegistrationId}.json`;
    const bytes = await this.readPath(state, path);
    if (!bytes) throw new StoreTransactionError('store_transaction_failed');
    const registration = decodeValidatedRegistration(bytes);
    if (
      registration.registrationSequence !== counter.lastAllocatedSequence ||
      registration.registrationId !== counter.lastRegistrationId ||
      !bytesEqual(canonicalJsonBytes(registration.stateKey), canonicalJsonBytes(stateKey)) ||
      competingScopeDigest(scopeOf(registration)) !== counter.lastCompetingScopeDigest
    ) {
      throw new StoreTransactionError('store_transaction_failed');
    }
  }
}

function initialCounter(stateKey: StateKeyV2): RegistrationCounterV1 {
  return {
    schemaVersion: 1,
    kind: REGISTRATION_COUNTER_KIND,
    stateKeyDigest: stateKeyDigest(stateKey),
    lastAllocatedSequence: '0',
  };
}

function decodeRegistrationCounter(bytes: Uint8Array, stateKey: StateKeyV2): RegistrationCounterV1 {
  const value = decodeRecord<unknown>(bytes, 1024);
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new StoreTransactionError('store_transaction_failed');
  }
  const record = value as Record<string, unknown>;
  const keys = Object.keys(record).sort();
  const zeroCounter = record.lastAllocatedSequence === '0';
  const expectedKeys = zeroCounter
    ? 'kind,lastAllocatedSequence,schemaVersion,stateKeyDigest'
    : 'kind,lastAllocatedSequence,lastCompetingScopeDigest,lastRegistrationId,schemaVersion,stateKeyDigest';
  if (
    keys.join(',') !== expectedKeys ||
    record.schemaVersion !== 1 ||
    record.kind !== REGISTRATION_COUNTER_KIND ||
    record.stateKeyDigest !== stateKeyDigest(stateKey) ||
    typeof record.lastAllocatedSequence !== 'string' ||
    !/^(?:0|[1-9][0-9]{0,6})$/.test(record.lastAllocatedSequence) ||
    Number(record.lastAllocatedSequence) > counterMax ||
    (!zeroCounter &&
      (typeof record.lastRegistrationId !== 'string' ||
        typeof record.lastCompetingScopeDigest !== 'string' ||
        !SHA256_HEX.test(record.lastRegistrationId) ||
        !SHA256_HEX.test(record.lastCompetingScopeDigest)))
  ) {
    throw new StoreTransactionError('store_transaction_failed');
  }
  return record as unknown as RegistrationCounterV1;
}

function isM4TreePath(path: string): boolean {
  return /^(?:m4-state|m4-state\/v1|m4-state\/v1\/(?:candidates|states|markers|receipts|probes)|m4-state\/v1\/candidates\/[a-f0-9]{64}|m4-state\/v1\/states\/[a-f0-9]{64}(?:\/(?:selectors|registrations)(?:\/[a-f0-9]{64})?)?|m4-state\/v1\/receipts\/[a-f0-9]{64})$/u.test(
    path,
  );
}

function hasM4NamespaceEntries(state: GitStateRef): boolean {
  return [...state.entries.keys()].some(
    (path) => path === 'm4-state' || path.startsWith('m4-state/'),
  );
}

function isCanonicalTimestamp(value: string): boolean {
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$/.test(value)) return false;
  const parsed = new Date(value);
  return Number.isFinite(parsed.valueOf()) && parsed.toISOString() === value;
}

export function manifestProvenanceMatches(
  manifest: import('../state-v2/index.js').StateManifestV2,
  options: SelectionOptions,
): boolean {
  const expected = [
    [manifest.provenance.workflowEvent, options.expectedWorkflowEvent],
    [manifest.provenance.producingWorkflowRef, options.expectedProducingWorkflowRef],
    [manifest.provenance.producingGitRef, options.expectedProducingGitRef],
    [manifest.provenance.producingActionSourceSha, options.expectedProducingActionSourceSha],
  ] as const;
  return expected.every(([actual, required]) => required === undefined || actual === required);
}

function decodePublicationReceipt(bytes: Uint8Array): void {
  const value = decodeRecord<unknown>(bytes, 4096);
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new StoreTransactionError('store_transaction_failed');
  }
  const receipt = value as Record<string, unknown>;
  const common = [
    'acceptingRunAttempt',
    'acceptingRunId',
    'markerId',
    'publicationStatus',
    'recordedAt',
    'schemaVersion',
    'selectorRevision',
    'stateKeyDigest',
  ];
  const expected =
    receipt.publicationStatus === 'succeeded'
      ? [...common, 'bodySha256', 'commentId']
      : receipt.publicationStatus === 'failed' || receipt.publicationStatus === 'unknown'
        ? [...common, 'failureCode']
        : receipt.publicationStatus === 'not_attempted'
          ? common
          : [];
  if (
    expected.length === 0 ||
    Object.keys(receipt).sort().join(',') !== expected.sort().join(',') ||
    receipt.schemaVersion !== 1 ||
    typeof receipt.markerId !== 'string' ||
    !SHA256_HEX.test(receipt.markerId) ||
    typeof receipt.stateKeyDigest !== 'string' ||
    !SHA256_HEX.test(receipt.stateKeyDigest) ||
    typeof receipt.selectorRevision !== 'string' ||
    !/^sha256:[a-f0-9]{64}$/.test(receipt.selectorRevision) ||
    typeof receipt.acceptingRunId !== 'string' ||
    !/^[1-9][0-9]{0,18}$/.test(receipt.acceptingRunId) ||
    !Number.isInteger(receipt.acceptingRunAttempt) ||
    (receipt.acceptingRunAttempt as number) < 1 ||
    (receipt.acceptingRunAttempt as number) > 2_147_483_647 ||
    typeof receipt.recordedAt !== 'string' ||
    !isCanonicalTimestamp(receipt.recordedAt) ||
    (receipt.publicationStatus === 'succeeded' &&
      (typeof receipt.commentId !== 'string' ||
        !/^[1-9][0-9]{0,18}$/.test(receipt.commentId) ||
        typeof receipt.bodySha256 !== 'string' ||
        !SHA256_HEX.test(receipt.bodySha256))) ||
    (receipt.publicationStatus === 'failed' &&
      !['comment_create_failed', 'comment_update_failed', 'comment_readback_failed'].includes(
        receipt.failureCode as string,
      )) ||
    (receipt.publicationStatus === 'unknown' && receipt.failureCode !== 'comment_outcome_unknown')
  ) {
    throw new StoreTransactionError('store_transaction_failed');
  }
}

function revision(bytes: Uint8Array | null): SelectorRevision {
  if (bytes === null) return 'bootstrap';
  try {
    return decodeValidatedSelector(bytes).selectorRevision;
  } catch {
    return `invalid:${sha256Hex(bytes)}` as SelectorRevision;
  }
}

function selectorRevisionFromBytes(bytes: Uint8Array | null): SelectorRevision {
  return revision(bytes);
}
function scopeOf(registration: CandidateRegistrationV1): CompetingScope {
  return {
    stateKey: registration.stateKey,
    sessionEpoch: registration.sessionEpoch,
    observedSelectorRevision: registration.observedSelectorRevision,
    predecessorMarkerId: registration.predecessorMarkerId,
    predecessorManifestSha256: registration.predecessorManifestSha256,
    predecessorLedgerSha256: registration.predecessorLedgerSha256,
    ledgerEpoch: registration.ledgerEpoch,
    targetStateGeneration: registration.stateGeneration,
    interactionId: registration.interactionId,
  };
}
function matchesScope(registration: CandidateRegistrationV1, scope: CompetingScope): boolean {
  return bytesEqual(canonicalJsonBytes(scopeOf(registration)), canonicalJsonBytes(scope));
}
function bundlesEqual(left: CandidateBundleBytes, right: CandidateBundleBytes): boolean {
  return (
    bytesEqual(left.manifestBytes, right.manifestBytes) &&
    bytesEqual(left.ledgerBytes, right.ledgerBytes) &&
    bytesEqual(left.providerRunMetadataBytes, right.providerRunMetadataBytes)
  );
}
function unsafeEvidence() {
  return {
    manifest: { status: 'unsafe' as const },
    ledger: { status: 'unsafe' as const },
    providerRunMetadata: { status: 'unsafe' as const },
  };
}

function finalize(snapshot: unknown): StateSelectionSnapshot {
  const provisional = {
    ...(snapshot as Record<string, unknown>),
    selectionSnapshotId: '',
  } as StateSelectionSnapshot;
  return {
    ...provisional,
    selectionSnapshotId: computeSelectionSnapshotId(provisional),
  } as StateSelectionSnapshot;
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

function registrationMarkerMatches(
  registration: CandidateRegistrationV1,
  marker: AcceptedStateMarkerV1,
): boolean {
  return (
    registration.registrationId === marker.registrationId &&
    registration.candidateId === marker.candidateId &&
    bytesEqual(canonicalJsonBytes(registration.stateKey), canonicalJsonBytes(marker.stateKey)) &&
    registration.sessionEpoch === marker.sessionEpoch &&
    registration.stateGeneration === marker.stateGeneration &&
    registration.ledgerEpoch === marker.ledgerEpoch &&
    bytesEqual(
      canonicalJsonBytes(registration.transition),
      canonicalJsonBytes(marker.transition),
    ) &&
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
