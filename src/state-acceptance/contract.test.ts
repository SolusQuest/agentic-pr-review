import { chmod, mkdir, mkdtemp, readFile, readdir, rm, symlink, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Ajv } from 'ajv';
import { build } from 'esbuild';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { describe, expect, it } from 'vitest';
import registrationSchema from '../../protocol/schemas/candidate-registration.v1.json' with { type: 'json' };
import markerSchema from '../../protocol/schemas/accepted-state-marker.v1.json' with { type: 'json' };
import selectorSchema from '../../protocol/schemas/state-selector.v1.json' with { type: 'json' };
import { classifyCandidateRegistrations } from './accept.js';
import {
  acceptLocalCandidate,
  acceptanceSnapshotLimitExceeded,
  RecordCodecError,
  ReferenceStateStore,
  RECORD_CODEC_CODES,
  RECORD_CODEC_DIAGNOSTIC_VECTORS,
  SelectionSnapshotLimitError,
  StoreTransactionError,
  StickyCallbackOutcomeUnknownError,
  candidateLocator,
  computeCandidateId,
  computeCandidateSetDigest,
  computeSelectionSnapshotId,
  ContractValidationError,
  decodeRecord,
  decodeValidatedMarker,
  decodeValidatedRegistration,
  decodeValidatedSelector,
  encodeRecord,
  materializeMarker,
  materializeRegistration,
  materializeSelector,
  observedSelectorSnapshotSha256,
  sha256Hex,
  validateAcceptedStateMarker,
  validateCandidateRegistration,
  validateStateKey,
  validateStateSelector,
  type AcceptedStateMarkerV1,
  type CandidateBundleBytes,
  type CandidateRegistrationDraft,
  type SelectorRevision,
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
const fixtureStateKey: StateKeyV2 = {
  namespace: 'm4-ledger-v2',
  repository: 'SolusQuest/agentic-pr-review',
  headRepository: 'SolusQuest/agentic-pr-review',
  pullRequest: 48,
  workflowIdentity: 'agentic-pr-review',
  trustedExecutionDomain: 'trusted',
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
  registrationId = sha,
  markerStateKey = stateKey,
) {
  return materializeMarker(
    {
      schemaVersion: 1,
      candidateId: candidateId as never,
      registrationId: registrationId as never,
      stateKey: markerStateKey,
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

function selectorFor(
  marker: ReturnType<typeof markerFor>,
  selectorStateKey = stateKey,
  previousSelectorRevision: SelectorRevision = 'bootstrap',
) {
  return materializeSelector(
    {
      schemaVersion: 1,
      stateKey: selectorStateKey,
      previousSelectorRevision,
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
      workflowIdentity: selectorStateKey.workflowIdentity,
      trustedExecutionDomain: selectorStateKey.trustedExecutionDomain,
    },
    '2026-07-20T00:00:02.000Z',
  );
}

async function buildStoreChildBundle(root: string): Promise<string> {
  const outfile = path.join(root, '.m4-state-acceptance-child.mjs');
  await build({
    entryPoints: [fileURLToPath(new URL('./index.ts', import.meta.url))],
    bundle: true,
    format: 'esm',
    outfile,
    platform: 'node',
    logLevel: 'silent',
  });
  return outfile;
}

function runStoreChild(
  bundlePath: string,
  command: string,
  payload: Record<string, unknown>,
): Promise<{
  readonly code: number | null;
  readonly signal: NodeJS.Signals | null;
  readonly result: Record<string, any> | null;
}> {
  const childScript = fileURLToPath(new URL('./store-child.mjs', import.meta.url));
  return new Promise((resolve, reject) => {
    const child = spawn(
      process.execPath,
      [childScript, bundlePath, command, JSON.stringify(payload)],
      { stdio: ['ignore', 'pipe', 'pipe'] },
    );
    let output = '';
    let errors = '';
    child.stdout.on('data', (chunk: Buffer) => {
      output += chunk.toString();
    });
    child.stderr.on('data', (chunk: Buffer) => {
      errors += chunk.toString();
    });
    child.once('error', reject);
    child.once('exit', (code, signal) => {
      if (errors && code === 0) reject(new Error(errors));
      let result: Record<string, any> | null = null;
      if (output.trim()) result = JSON.parse(output.trim()) as Record<string, any>;
      resolve({ code, signal, result });
    });
  });
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

  it('routes schema versions before unicode and canonical stages for every record kind', () => {
    const decoders = [decodeValidatedRegistration, decodeValidatedMarker, decodeValidatedSelector];
    const unknownVersionWithUnsafeString = JSON.stringify({ schemaVersion: 2, bad: '\u0000' });
    const currentVersionWithUnsafeString = JSON.stringify({ schemaVersion: 1, bad: '\u0000' });
    for (const decode of decoders) {
      expect(() => decode(new TextEncoder().encode(unknownVersionWithUnsafeString))).toThrowError(
        expect.objectContaining({ code: 'schema_version_unsupported', path: '/schemaVersion' }),
      );
      expect(() => decode(new TextEncoder().encode(currentVersionWithUnsafeString))).toThrowError(
        expect.objectContaining({ code: 'invalid_unicode' }),
      );
      expect(() => decode(new TextEncoder().encode('{"schemaVersion":1, "bad": 1}'))).toThrowError(
        expect.objectContaining({ code: 'unknown_or_missing_field' }),
      );
    }
    expect(() => decodeValidatedRegistration(new TextEncoder().encode('null'))).toThrowError(
      ContractValidationError,
    );
  });

  it('exposes stable codec diagnostics for every validated record kind', () => {
    expect(RECORD_CODEC_CODES).toEqual([
      'byte_limit_exceeded',
      'bom',
      'invalid_utf8',
      'invalid_json',
      'duplicate_key',
      'invalid_unicode',
      'non_canonical',
    ]);
    expect(RECORD_CODEC_DIAGNOSTIC_VECTORS).toEqual(
      RECORD_CODEC_CODES.map((code) => ({ code, path: '' })),
    );
    const decoders = [decodeValidatedRegistration, decodeValidatedMarker, decodeValidatedSelector];
    const bom = new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]);
    const cases = [
      [new Uint8Array(32 * 1024 + 1), 'byte_limit_exceeded'],
      [bom, 'bom'],
      [new Uint8Array([0xc3, 0x28]), 'invalid_utf8'],
      [new TextEncoder().encode('{'), 'invalid_json'],
      [new TextEncoder().encode('{"schemaVersion":1,"schemaVersion":1}'), 'duplicate_key'],
    ] as const;
    for (const decode of decoders) {
      for (const [bytes, code] of cases) {
        expect(() => decode(bytes)).toThrowError(expect.objectContaining({ code, path: '' }));
      }
      expect(() => decode(new TextEncoder().encode('{}'))).toThrowError(
        expect.objectContaining({ code: 'unknown_or_missing_field', path: '' }),
      );
    }
  });

  it('exercises the published unicode and canonical diagnostics for every decoder', () => {
    const records = [
      [decodeValidatedRegistration, materializeRegistration(draft(), '1')],
      [decodeValidatedMarker, markerFor('e'.repeat(64))],
      [decodeValidatedSelector, selectorFor(markerFor('e'.repeat(64)))],
    ] as const;
    const invalidUnicode = new TextEncoder().encode('{"schemaVersion":1,"bad":"\\u0000"}');
    for (const [decode, value] of records) {
      expect(() => decode(invalidUnicode)).toThrowError(
        expect.objectContaining({ code: 'invalid_unicode', path: '' }),
      );
      const canonical = canonicalJsonBytes(value);
      const nonCanonical = new Uint8Array([0x20, ...canonical, 0x20]);
      expect(() => decode(nonCanonical)).toThrowError(
        expect.objectContaining({ code: 'non_canonical', path: '' }),
      );
    }
  });

  it('keeps schema and runtime state-key domains in parity', () => {
    const ajv = new Ajv({ strict: true, allErrors: true });
    const validateStateKeySchema = ajv.compile(registrationSchema.$defs?.stateKey);
    for (const property of ['workflowIdentity', 'trustedExecutionDomain'] as const) {
      const atLimit = { ...stateKey, [property]: 'x'.repeat(256) };
      expect(validateStateKeySchema(atLimit)).toBe(true);
      expect(() => validateStateKey(atLimit)).not.toThrow();
      const overLimit = { ...stateKey, [property]: `${'x'.repeat(253)}😀` };
      expect(validateStateKeySchema(overLimit)).toBe(true);
      expect(() => validateStateKey(overLimit)).toThrowError(
        expect.objectContaining({ code: 'string_invalid', path: `/stateKey/${property}` }),
      );
      const control = { ...stateKey, [property]: 'safe\u0001identity' };
      expect(validateStateKeySchema(control)).toBe(true);
      expect(() => validateStateKey(control)).toThrowError(
        expect.objectContaining({ code: 'string_invalid', path: `/stateKey/${property}` }),
      );
    }
    const marker = markerFor('e'.repeat(64));
    const records = [
      {
        schema: registrationSchema,
        decode: decodeValidatedRegistration,
        validate: validateCandidateRegistration,
        value: materializeRegistration(draft(), '1'),
      },
      {
        schema: markerSchema,
        decode: decodeValidatedMarker,
        validate: validateAcceptedStateMarker,
        value: marker,
      },
      {
        schema: selectorSchema,
        decode: decodeValidatedSelector,
        validate: validateStateSelector,
        value: selectorFor(marker),
      },
    ] as const;
    for (const { schema, decode, validate: contractValidate, value } of records) {
      const validate = ajv.compile(schema);
      const validBytes = canonicalJsonBytes(value);
      expect(validate(value)).toBe(true);
      expect(() => decode(validBytes)).not.toThrow();

      const invalidRepository = structuredClone(value) as Record<string, any>;
      invalidRepository.stateKey.repository = 'invalid repository';
      const invalidRepositoryBytes = canonicalJsonBytes(invalidRepository);
      expect(validate(invalidRepository)).toBe(false);
      expect(() => decode(invalidRepositoryBytes)).toThrowError(
        expect.objectContaining({ code: 'state_key_invalid', path: '/stateKey/repository' }),
      );

      const overBoundRepository = structuredClone(value) as Record<string, any>;
      overBoundRepository.stateKey.repository = `${'a'.repeat(100)}/${'b'.repeat(100)}`;
      const overBoundRepositoryBytes = canonicalJsonBytes(overBoundRepository);
      expect(validate(overBoundRepository)).toBe(false);
      expect(() => decode(overBoundRepositoryBytes)).toThrowError(
        expect.objectContaining({ code: 'state_key_invalid', path: '/stateKey/repository' }),
      );

      const nestedUnknown = structuredClone(value) as Record<string, any>;
      nestedUnknown.stateKey.extra = true;
      const nestedUnknownBytes = new Uint8Array([0x20, ...canonicalJsonBytes(nestedUnknown), 0x20]);
      expect(() => decode(nestedUnknownBytes)).toThrowError(
        expect.objectContaining({
          code: 'unknown_or_missing_field',
          path: '/stateKey/<untrusted-property>',
        }),
      );
      const controlUnknown = structuredClone(value) as Record<string, any>;
      controlUnknown.stateKey['bad\u0001'] = true;
      expect(() => decode(canonicalJsonBytes(controlUnknown))).toThrowError(
        expect.objectContaining({
          code: 'unknown_or_missing_field',
          path: '/stateKey/<invalid-control>',
        }),
      );

      const wrongNestedTypes = [
        ['stateKey', 'object_required'],
        ['transition', 'object_required'],
        ...('candidateArtifactLocator' in value
          ? [['candidateArtifactLocator', 'object_required'] as const]
          : []),
      ] as const;
      for (const [property, code] of wrongNestedTypes) {
        const wrongNestedType = structuredClone(value) as Record<string, any>;
        wrongNestedType[property] = property === 'stateKey' ? 'not-an-object' : [];
        const wrongNestedTypeBytes = new Uint8Array([
          0x20,
          ...canonicalJsonBytes(wrongNestedType),
          0x20,
        ]);
        expect(() => decode(wrongNestedTypeBytes)).toThrowError(expect.objectContaining({ code }));
      }

      const scalarWrongTypes = [
        ['stateGeneration', '0', 'integer_out_of_range'],
        ['sessionEpoch', 1, 'epoch_invalid'],
        ['currentHeadSha', 1, 'git_sha_invalid'],
        ['predecessorMarkerId', 1, 'predecessor_invalid'],
        ['producingRunId', 1, 'run_id_invalid'],
        ['registeredAt', 1, 'timestamp_invalid'],
        ['candidateId', 1, 'sha256_invalid'],
      ] as const;
      for (const [property, invalidValue, code] of scalarWrongTypes) {
        if (!(property in value)) continue;
        const invalidScalar = structuredClone(value) as Record<string, any>;
        invalidScalar[property] = invalidValue;
        const invalidScalarBytes = canonicalJsonBytes(invalidScalar);
        expect(() => decode(invalidScalarBytes)).toThrowError(
          expect.objectContaining({ code, path: `/${property}` }),
        );
        expect(() => contractValidate(invalidScalar)).toThrowError(
          expect.objectContaining({ code, path: `/${property}` }),
        );
      }

      const overBoundGeneration = structuredClone(value) as Record<string, any>;
      overBoundGeneration.stateGeneration = 1_000_001;
      const overBoundGenerationBytes = canonicalJsonBytes(overBoundGeneration);
      expect(validate(overBoundGeneration)).toBe(false);
      expect(() => decode(overBoundGenerationBytes)).toThrowError(
        expect.objectContaining({ code: 'integer_out_of_range', path: '/stateGeneration' }),
      );
    }
  });

  it('rejects NULs and lone surrogates before materialization or encoding', () => {
    const nulStateKey = { ...stateKey, workflowIdentity: 'm4\u0000state' };
    const surrogateStateKey = { ...stateKey, trustedExecutionDomain: '\ud800' };
    expect(() => materializeRegistration(draft({ stateKey: nulStateKey }), '1')).toThrowError(
      expect.objectContaining({ code: 'invalid_unicode' }),
    );
    expect(() => markerFor('e'.repeat(64), undefined, sha, nulStateKey)).toThrowError(
      expect.objectContaining({ code: 'invalid_unicode' }),
    );
    expect(() => selectorFor(markerFor('e'.repeat(64)), nulStateKey)).toThrowError(
      expect.objectContaining({ code: 'invalid_unicode' }),
    );
    expect(() => materializeRegistration(draft({ stateKey: surrogateStateKey }), '1')).toThrowError(
      expect.objectContaining({ code: 'invalid_unicode' }),
    );
  });

  it('orders duplicate winners and distinguishes semantic conflicts from stale candidates', () => {
    const first = materializeRegistration(draft({ producingRunId: '10' }), '1');
    const laterDuplicate = materializeRegistration(draft({ producingRunId: '11' }), '2');
    expect(classifyCandidateRegistrations(first, [first, laterDuplicate])).toBe(first);
    expect(classifyCandidateRegistrations(laterDuplicate, [first, laterDuplicate])).toBe('stale');

    const conflictResultSha = 'b'.repeat(64) as never;
    const conflictCandidateId = computeCandidateId({
      manifestSha256: sha,
      candidateLedgerSha256: sha,
      providerRunMetadataSha256: sha,
      metadataSemanticSha256: sha,
      consumedInputSha256: sha,
      resultSha256: conflictResultSha,
      traceSha256: sha,
    });
    const conflict = materializeRegistration(
      draft({ candidateId: conflictCandidateId, resultSha256: conflictResultSha }),
      '3',
    );
    expect(classifyCandidateRegistrations(conflict, [first, conflict])).toBe('conflict');

    const differentLedgerEpoch = materializeRegistration(
      draft({ ledgerEpoch: 'B'.repeat(22) as never }),
      '4',
    );
    expect(classifyCandidateRegistrations(first, [first, differentLedgerEpoch])).toBe(first);
    expect(
      classifyCandidateRegistrations(differentLedgerEpoch, [first, differentLedgerEpoch]),
    ).toBe(differentLedgerEpoch);
  });

  it('freezes the exact aggregate acceptance snapshot byte boundary', () => {
    expect(acceptanceSnapshotLimitExceeded(63, 2_097_151, 1)).toBe(false);
    expect(acceptanceSnapshotLimitExceeded(63, 2_097_151, 2)).toBe(true);
    expect(acceptanceSnapshotLimitExceeded(64, 2_097_152, 0)).toBe(true);
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
      currentHeadSha: gitSha,
      currentBaseSha: gitSha,
      currentBaseRef: 'refs/heads/main',
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
    for (const field of ['currentHeadSha', 'currentBaseSha', 'currentBaseRef'] as const) {
      const changed = computeSelectionSnapshotId({
        ...base,
        [field]: field === 'currentBaseRef' ? 'refs/heads/other' : ('e'.repeat(40) as never),
        selectionSnapshotId: sha as never,
      });
      expect(first).not.toBe(changed);
    }
  });

  it('cancels before store mutation and reports lease cleanup failures', async () => {
    const controller = new AbortController();
    controller.abort();
    const result = await acceptLocalCandidate(
      {} as never,
      {
        signal: controller.signal,
        selectionSnapshot: {} as never,
        candidate: {
          release: async () => {
            throw new Error('lease release failed');
          },
        } as never,
      } as never,
    );
    expect(result).toMatchObject({
      acceptance: 'not_accepted',
      reason: 'cancelled_before_acceptance',
      cleanupWarnings: ['lease_release_failed'],
    });
  });

  it('rejects standalone selection identities that disagree with the state key', async () => {
    const parent = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-identity-'));
    const root = path.join(parent, 'store');
    const store = new ReferenceStateStore(root);
    try {
      for (const property of ['workflowIdentity', 'trustedExecutionDomain'] as const) {
        const options = {
          stateKey,
          workflowIdentity: stateKey.workflowIdentity,
          trustedExecutionDomain: stateKey.trustedExecutionDomain,
          [property]: 'mismatched',
        } as never;
        await expect(store.selectAcceptedState(options)).resolves.toEqual({
          selection: 'failed',
          reason: 'state_key_mismatch',
        });
      }
      await expect(readdir(root)).rejects.toThrow();
    } finally {
      await rm(parent, { recursive: true, force: true });
    }
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
        expectedLedgerSchemaVersion: 1,
        expectedPrefixContractVersion: 1,
        cacheContractIdentity: {
          providerId: 'synthetic',
          modelId: 'synthetic-model',
          adapterId: sha as never,
          templateId: sha as never,
          policyId: sha as never,
          toolDefinitionId: sha as never,
          cacheConfigId: sha as never,
        },
        currentHeadSha: gitSha,
        currentBaseSha: gitSha,
        currentBaseRef: 'refs/heads/main',
        provenanceTrusted: true,
        workflowIdentity: stateKey.workflowIdentity,
        trustedExecutionDomain: stateKey.trustedExecutionDomain,
      } as const;
      const bootstrap = await store.selectAcceptedState(options);
      expect(bootstrap).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'bootstrap_selected' },
      });
      const malformedSelectorBytes = new TextEncoder().encode('{"schemaVersion":1,"stateKey":[]}');
      const malformedSelectorPath = path.join(
        root,
        'states',
        sha256Hex(canonicalJsonBytes(stateKey)),
        'selector.json',
      );
      await writeFile(malformedSelectorPath, malformedSelectorBytes, { mode: 0o600 });
      const malformedSelection = await store.selectAcceptedState(options);
      expect(malformedSelection).toMatchObject({
        selection: 'selected',
        snapshot: {
          kind: 'recovery_root_selected',
          observedSelectorRevision: `invalid:${sha256Hex(malformedSelectorBytes)}`,
        },
      });
      if (malformedSelection.selection === 'selected') {
        const recoverySelector = selectorFor(
          markerFor('e'.repeat(64)),
          stateKey,
          malformedSelection.snapshot.observedSelectorRevision,
        );
        expect(
          await store.casSelector(
            malformedSelection.snapshot.observedSelectorRevision,
            recoverySelector,
          ),
        ).toMatchObject({ kind: 'applied' });
      }
      await rm(malformedSelectorPath, { force: true });
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

  it('recognizes a retry of an already committed selector target', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-cas-retry-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const marker = markerFor('e'.repeat(64));
      const selector = selectorFor(marker);
      expect((await store.casSelector('bootstrap', selector)).kind).toBe('applied');
      expect((await store.casSelector('bootstrap', selector)).kind).toBe(
        'already_applied_same_target',
      );
      expect(await store.casSelector(selector.selectorRevision, selector)).toEqual({
        kind: 'rejected_with_current_revision',
        currentRevision: selector.selectorRevision,
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it.each([
    { label: 'legacy v1', manifestBytes: new TextEncoder().encode('{"version":1}') },
    { label: 'unknown version', manifestBytes: new TextEncoder().encode('{"version":3}') },
  ])('preserves explicit contract-version failure for $label', async ({ manifestBytes }) => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-corrupt-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const candidateBytes = { ...bundle(), manifestBytes };
      const candidateHashes = {
        manifestSha256: sha256Hex(candidateBytes.manifestBytes),
        candidateLedgerSha256: sha256Hex(candidateBytes.ledgerBytes),
        providerRunMetadataSha256: sha256Hex(candidateBytes.providerRunMetadataBytes),
      };
      const candidateId = computeCandidateId({
        ...candidateHashes,
        metadataSemanticSha256: sha,
        consumedInputSha256: sha,
        resultSha256: sha,
        traceSha256: sha,
      });
      expect((await store.uploadCandidate(candidateId, candidateBytes)).kind).toBe('created');
      const candidateRegistration = materializeRegistration(
        draft({
          candidateId,
          ...candidateHashes,
        }),
        '1',
      );
      expect((await store.registerCandidate(draft({ candidateId, ...candidateHashes }))).kind).toBe(
        'created',
      );
      const marker = markerFor(candidateId, candidateHashes, candidateRegistration.registrationId);
      const selector = selectorFor(marker);
      expect((await store.writeMarker(marker)).kind).toBe('created');
      await expect(
        store.readMarker(stateKey, `../${marker.markerId}` as never),
      ).rejects.toThrowError(
        expect.objectContaining({ code: 'sha256_invalid', path: '/markerId' }),
      );
      expect((await store.casSelector('bootstrap', selector)).kind).toBe('applied');
      const selectionOptions = {
        stateKey,
        expectedLedgerSchemaVersion: 1,
        expectedPrefixContractVersion: 1,
        cacheContractIdentity: {
          providerId: 'synthetic',
          modelId: 'synthetic-model',
          adapterId: sha as never,
          templateId: sha as never,
          policyId: sha as never,
          toolDefinitionId: sha as never,
          cacheConfigId: sha as never,
        },
        currentHeadSha: gitSha,
        currentBaseSha: gitSha,
        currentBaseRef: 'refs/heads/main',
        provenanceTrusted: true,
        workflowIdentity: stateKey.workflowIdentity,
        trustedExecutionDomain: stateKey.trustedExecutionDomain,
      } as const;
      const result = await store.selectAcceptedState(selectionOptions);
      expect(result).toMatchObject({
        selection: 'selected',
        snapshot: {
          kind: 'recovery_root_selected',
          recoveryReason: 'contract_version_incompatible',
        },
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
          bundleDiagnostic: 'manifest_unknown_version',
        });
      }
      expect(
        await store.selectAcceptedState({ ...selectionOptions, explicitRestore: true }),
      ).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'explicit_restore_invalid', failure: 'contract_version_incompatible' },
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('rejects oversized and unsafe candidate filesystem objects before acceptance', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-files-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const oversizedId = 'f'.repeat(64) as never;
      expect(
        (
          await store.uploadCandidate(oversizedId, {
            manifestBytes: new Uint8Array(65_537),
            ledgerBytes: new Uint8Array(),
            providerRunMetadataBytes: new Uint8Array(),
          })
        ).kind,
      ).toBe('existing_content_conflict');
      await expect(
        readFile(path.join(root, 'candidates', oversizedId, 'manifest.json')),
      ).rejects.toMatchObject({ code: 'ENOENT' });

      const unsafeId = 'e'.repeat(64) as never;
      const unsafeDirectory = path.join(root, 'candidates', unsafeId);
      await mkdir(unsafeDirectory, { mode: 0o700 });
      await chmod(unsafeDirectory, 0o755);
      expect((await store.readCandidate(unsafeId)).status).toBe('unsafe');
      await rm(unsafeDirectory, { recursive: true, force: true });
      await symlink(path.join(root, 'states'), unsafeDirectory, 'dir');
      expect((await store.readCandidate(unsafeId)).status).toBe('unsafe');
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('classifies unsafe expected candidate files instead of treating them as missing', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-file-matrix-'));
    const candidateId = 'd'.repeat(64) as never;
    const directory = path.join(root, 'candidates', candidateId);
    const fileCases = [
      ['manifest.json', 65_537],
      ['ledger.json', 524_289],
      ['provider-run-metadata.json', 32_769],
    ] as const;
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      for (const [fileName, oversizedBytes] of fileCases) {
        await mkdir(directory, { mode: 0o700 });
        for (const sibling of ['manifest.json', 'ledger.json', 'provider-run-metadata.json']) {
          await writeFile(path.join(directory, sibling), new Uint8Array([0x7b, 0x7d]), {
            mode: 0o600,
          });
        }
        await writeFile(path.join(directory, fileName), new Uint8Array(oversizedBytes), {
          mode: 0o600,
        });
        const oversized = await store.readCandidate(candidateId);
        expect(oversized).toMatchObject({
          status: 'unsafe',
          evidence: {
            [fileName === 'manifest.json'
              ? 'manifest'
              : fileName === 'ledger.json'
                ? 'ledger'
                : 'providerRunMetadata']: { status: 'unsafe' },
          },
        });
        await rm(directory, { recursive: true, force: true });

        await mkdir(directory, { mode: 0o700 });
        for (const sibling of ['manifest.json', 'ledger.json', 'provider-run-metadata.json']) {
          await writeFile(path.join(directory, sibling), new Uint8Array([0x7b, 0x7d]), {
            mode: 0o600,
          });
        }
        await chmod(path.join(directory, fileName), 0o644);
        const wrongMode = await store.readCandidate(candidateId);
        expect(wrongMode).toMatchObject({ status: 'unsafe' });
        await rm(directory, { recursive: true, force: true });

        await mkdir(directory, { mode: 0o700 });
        for (const sibling of ['manifest.json', 'ledger.json', 'provider-run-metadata.json']) {
          await writeFile(path.join(directory, sibling), new Uint8Array([0x7b, 0x7d]), {
            mode: 0o600,
          });
        }
        await rm(path.join(directory, fileName), { force: true });
        await symlink(path.join(root, 'states'), path.join(directory, fileName), 'dir');
        const symlinked = await store.readCandidate(candidateId);
        expect(symlinked).toMatchObject({ status: 'unsafe' });
        await rm(directory, { recursive: true, force: true });

        await mkdir(directory, { mode: 0o700 });
        for (const sibling of ['manifest.json', 'ledger.json', 'provider-run-metadata.json']) {
          if (sibling !== fileName) {
            await writeFile(path.join(directory, sibling), new Uint8Array([0x7b, 0x7d]), {
              mode: 0o600,
            });
          }
        }
        const missing = await store.readCandidate(candidateId);
        expect(missing).toMatchObject({ status: 'missing' });
        await rm(directory, { recursive: true, force: true });
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
      expect(await store.uploadCandidate(registration.candidateId, candidate)).toMatchObject({
        kind: 'already_exists_same',
      });
      expect(
        await store.uploadCandidate(registration.candidateId, {
          ...candidate,
          ledgerBytes: new TextEncoder().encode('{"different":true}'),
        }),
      ).toMatchObject({ kind: 'existing_content_conflict' });
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
          ledgerEpoch: epoch,
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

  it('reopens after a registration temp-file crash residue', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-registration-crash-'));
    try {
      const interrupted = new ReferenceStateStore(root, {
        afterRegistrationTempWrite: () => {
          throw new Error('simulated process termination after temp write');
        },
      });
      await interrupted.close();
      expect((await interrupted.registerCandidate(draft())).kind).toBe('registration_write_failed');
      const registrationsDirectory = path.join(
        root,
        'states',
        sha256Hex(canonicalJsonBytes(stateKey)),
        'registrations',
      );
      expect((await readdir(registrationsDirectory)).some((entry) => entry.includes('.tmp-'))).toBe(
        true,
      );

      const reopened = new ReferenceStateStore(root);
      await reopened.close();
      expect((await reopened.registerCandidate(draft())).kind).toBe('created');
      expect((await readdir(registrationsDirectory)).some((entry) => entry.includes('.tmp-'))).toBe(
        false,
      );
      const snapshot = await reopened.createAcceptanceSnapshot(
        'bootstrap',
        {
          stateKey,
          sessionEpoch: epoch,
          observedSelectorRevision: 'bootstrap',
          predecessorMarkerId: 'bootstrap',
          predecessorManifestSha256: 'bootstrap',
          predecessorLedgerSha256: 'bootstrap',
          ledgerEpoch: epoch,
          targetStateGeneration: 0,
          interactionId: sha as never,
        },
        sha,
      );
      expect(snapshot.registrations).toHaveLength(1);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('proves registration, reopen, snapshot, marker, and CAS across child processes', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-child-proof-'));
    const validRoot = await mkdtemp(
      path.join(os.tmpdir(), 'm4-state-acceptance-child-valid-restore-'),
    );
    try {
      const bundlePath = await buildStoreChildBundle(root);
      const registrationDraft = draft();
      const registration = materializeRegistration(registrationDraft, '1');
      const marker = markerFor('e'.repeat(64), undefined, registration.registrationId);
      const selector = selectorFor(marker);
      expect((await runStoreChild(bundlePath, 'init', { root })).result).toEqual({
        kind: 'initialized',
      });

      const killed = await runStoreChild(bundlePath, 'register-kill-after-temp', {
        root,
        draft: registrationDraft,
      });
      expect(killed).toMatchObject({ code: null, signal: 'SIGKILL' });
      expect(
        (await runStoreChild(bundlePath, 'register', { root, draft: registrationDraft })).result,
      ).toMatchObject({ kind: 'created' });
      expect(
        (
          await runStoreChild(bundlePath, 'snapshot', {
            root,
            expectedObservedSelectorRevision: 'bootstrap',
            competingScope: {
              stateKey,
              sessionEpoch: epoch,
              observedSelectorRevision: 'bootstrap',
              predecessorMarkerId: 'bootstrap',
              predecessorManifestSha256: 'bootstrap',
              predecessorLedgerSha256: 'bootstrap',
              ledgerEpoch: epoch,
              targetStateGeneration: 0,
              interactionId: sha,
            },
            selectionSnapshotId: sha,
          })
        ).result,
      ).toEqual({ cutoff: '1', registrations: 1 });
      expect((await runStoreChild(bundlePath, 'marker', { root, marker })).result).toMatchObject({
        kind: 'created',
      });
      expect(
        (
          await runStoreChild(bundlePath, 'cas', {
            root,
            expectedRevision: 'bootstrap',
            selector,
          })
        ).result,
      ).toMatchObject({ kind: 'applied' });
      expect(
        (await runStoreChild(bundlePath, 'read-selector', { root, stateKey })).result,
      ).toMatchObject({ hasBytes: true, selector: { selectorId: selector.selectorId } });

      const validBundle = await buildStoreChildBundle(validRoot);
      expect((await runStoreChild(validBundle, 'init', { root: validRoot })).result).toEqual({
        kind: 'initialized',
      });
      expect(
        (await runStoreChild(validBundle, 'accept-fixture', { root: validRoot })).result,
      ).toEqual(expect.objectContaining({ acceptance: 'accepted' }));
      const restored = (
        await runStoreChild(validBundle, 'restore-and-accept-fixture', {
          root: validRoot,
        })
      ).result;
      expect(restored).toMatchObject({
        selection: 'selected',
        snapshotKind: 'continuation_selected',
        predecessorBytesMatch: true,
        acceptance: { acceptance: 'accepted' },
      });
      const successorRevision = restored?.acceptance?.selectorRevision;
      const leftSelector = selectorFor(
        markerFor('a'.repeat(64), undefined, sha, fixtureStateKey),
        fixtureStateKey,
        successorRevision,
      );
      const rightSelector = selectorFor(
        markerFor('b'.repeat(64), undefined, sha, fixtureStateKey),
        fixtureStateKey,
        successorRevision,
      );
      const [left, right] = await Promise.all([
        runStoreChild(validBundle, 'cas', {
          root: validRoot,
          expectedRevision: successorRevision,
          selector: leftSelector,
        }),
        runStoreChild(validBundle, 'cas', {
          root: validRoot,
          expectedRevision: successorRevision,
          selector: rightSelector,
        }),
      ]);
      expect([left.result?.kind, right.result?.kind].sort()).toEqual([
        'applied',
        'rejected_with_current_revision',
      ]);
    } finally {
      await rm(root, { recursive: true, force: true });
      await rm(validRoot, { recursive: true, force: true });
    }
  }, 30_000);

  it('accepts the real snapshot path at the 64-registration count boundary', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-count-boundary-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      for (let index = 0; index < 64; index += 1) {
        expect(
          (await store.registerCandidate(draft({ producingRunId: String(index + 1) as never })))
            .kind,
        ).toBe('created');
      }
      const snapshot = await store.createAcceptanceSnapshot(
        'bootstrap',
        {
          stateKey,
          sessionEpoch: epoch,
          observedSelectorRevision: 'bootstrap',
          predecessorMarkerId: 'bootstrap',
          predecessorManifestSha256: 'bootstrap',
          predecessorLedgerSha256: 'bootstrap',
          ledgerEpoch: epoch,
          targetStateGeneration: 0,
          interactionId: sha as never,
        },
        sha,
      );
      expect(snapshot.registrations).toHaveLength(64);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('fails closed on valid records stored under a different immutable target id', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-target-id-'));
    try {
      const store = new ReferenceStateStore(root);
      await store.close();
      const firstDraft = draft({ producingRunId: '10' });
      const secondDraft = draft({ producingRunId: '11' });
      const secondRegistration = materializeRegistration(secondDraft, '1');
      expect((await store.registerCandidate(secondDraft)).kind).toBe('created');
      const registrationsDirectory = path.join(
        root,
        'states',
        sha256Hex(canonicalJsonBytes(stateKey)),
        'registrations',
      );
      const firstRegistration = materializeRegistration(firstDraft, '2');
      await writeFile(
        path.join(registrationsDirectory, `${firstRegistration.registrationId}.json`),
        canonicalJsonBytes(secondRegistration),
        { mode: 0o600 },
      );
      expect((await store.registerCandidate(firstDraft)).kind).toBe('registration_write_conflict');

      const marker = markerFor('e'.repeat(64));
      const differentMarker = markerFor('f'.repeat(64), undefined, 'c'.repeat(64));
      expect((await store.writeMarker(marker)).kind).toBe('created');
      const markersDirectory = path.join(
        root,
        'states',
        sha256Hex(canonicalJsonBytes(stateKey)),
        'markers',
      );
      await writeFile(
        path.join(markersDirectory, `${marker.markerId}.json`),
        canonicalJsonBytes(differentMarker),
        { mode: 0o600 },
      );
      expect((await store.writeMarker(marker)).kind).toBe('existing_content_conflict');
      await writeFile(
        path.join(markersDirectory, `${marker.markerId}.json`),
        new TextEncoder().encode('{'),
        { mode: 0o600 },
      );
      expect((await store.writeMarker(marker)).kind).toBe('existing_content_conflict');
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
            ledgerEpoch: epoch,
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
        currentHeadSha: manifest.provenance.currentHeadSha,
        currentBaseSha: manifest.provenance.currentBaseSha,
        currentBaseRef: manifest.provenance.currentBaseRef,
        observedSelectorBytes: null,
        observedSelectorRevision: 'bootstrap' as const,
        observedSelectorSnapshotSha256: observedSelectorSnapshotSha256(null),
        transitionPlan: 'bootstrap' as const,
        selectionSnapshotId: '' as never,
      };
      selection.selectionSnapshotId = computeSelectionSnapshotId(selection) as never;
      const store = new ReferenceStateStore(root, {
        afterCandidateCommit: () => {
          throw new Error('candidate commit outcome unknown');
        },
        afterRegistrationCommit: () => {
          throw new Error('registration commit outcome unknown');
        },
        afterMarkerCommit: () => {
          throw new Error('marker commit outcome unknown');
        },
        afterSelectorCommit: () => {
          throw new Error('selector commit outcome unknown');
        },
      });
      await store.close();
      let publishedMarkerId: string | undefined;
      const candidate = {
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
      };
      const acceptanceOptions = {
        selectionSnapshot: selection,
        candidate,
        interactionId: manifest.transaction.interactionId,
        interactionOrdinal: manifest.transaction.interactionOrdinal,
        producingRunId: manifest.provenance.producingRunId,
        producingRunAttempt: manifest.provenance.producingRunAttempt,
        acceptingRunId: '99',
        acceptingRunAttempt: 1,
        consumedInputSha256: inputSha256,
        transition: manifest.transition,
        now: () => '2026-07-20T00:00:03.000Z',
        publishSticky: async (markerId: string) => {
          publishedMarkerId = markerId;
        },
      };
      const provenanceRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-provenance-'),
      );
      try {
        let uploadAttempts = 0;
        let registrationAttempts = 0;
        let markerAttempts = 0;
        let selectorAttempts = 0;
        const provenanceStore = new ReferenceStateStore(provenanceRoot, {
          beforeCandidateCommit: () => {
            uploadAttempts += 1;
          },
          beforeRegistrationCommit: () => {
            registrationAttempts += 1;
          },
          beforeMarkerCommit: () => {
            markerAttempts += 1;
          },
          beforeSelectorCommit: () => {
            selectorAttempts += 1;
          },
        });
        await provenanceStore.close();
        for (const property of ['currentHeadSha', 'currentBaseSha', 'currentBaseRef'] as const) {
          const mismatchedValue =
            property === 'currentBaseRef'
              ? 'refs/heads/other'
              : manifest.provenance[property] === ('f'.repeat(40) as never)
                ? ('e'.repeat(40) as never)
                : ('f'.repeat(40) as never);
          const mismatchedCandidate = {
            ...candidate,
            manifest: {
              ...manifest,
              provenance: { ...manifest.provenance, [property]: mismatchedValue },
            } as never,
          };
          expect(
            await acceptLocalCandidate(provenanceStore, {
              ...acceptanceOptions,
              candidate: mismatchedCandidate,
            }),
          ).toMatchObject({
            acceptance: 'not_accepted',
            reason: 'candidate_invalid',
          });
        }
        expect(uploadAttempts).toBe(0);
        expect(registrationAttempts).toBe(0);
        expect(markerAttempts).toBe(0);
        expect(selectorAttempts).toBe(0);
      } finally {
        await rm(provenanceRoot, { recursive: true, force: true });
      }
      const snapshotRoot = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-snapshot-'));
      try {
        const snapshotStore = new ReferenceStateStore(snapshotRoot);
        await snapshotStore.close();
        const competingRegistrationDraftBase = draft({
          observedSelectorRevision: selection.observedSelectorRevision,
          observedSelectorSnapshotSha256: selection.observedSelectorSnapshotSha256,
          stateKey: manifest.stateKey,
          sessionEpoch: manifest.sessionEpoch,
          stateGeneration: manifest.generation.stateGeneration,
          ledgerEpoch: manifest.generation.ledgerEpoch,
          transition: manifest.transition,
          interactionId: manifest.transaction.interactionId,
          interactionOrdinal: manifest.transaction.interactionOrdinal,
          producingRunId: '100',
          producingRunAttempt: 1,
          consumedInputSha256: manifest.transaction.consumedInputSha256,
          manifestSha256: sha256Hex(manifestBytes) as never,
          candidateLedgerSha256: sha256Hex(ledgerBytes) as never,
          providerRunMetadataSha256: sha256Hex(providerRunMetadataBytes) as never,
          metadataSemanticSha256: manifest.transaction.metadataSemanticSha256,
          resultSha256: 'e'.repeat(64) as never,
          traceSha256: sha256Hex(traceBytes) as never,
        });
        const competingRegistrationDraft = {
          ...competingRegistrationDraftBase,
          candidateId: computeCandidateId({
            manifestSha256: competingRegistrationDraftBase.manifestSha256,
            candidateLedgerSha256: competingRegistrationDraftBase.candidateLedgerSha256,
            providerRunMetadataSha256: competingRegistrationDraftBase.providerRunMetadataSha256,
            metadataSemanticSha256: competingRegistrationDraftBase.metadataSemanticSha256,
            consumedInputSha256: competingRegistrationDraftBase.consumedInputSha256,
            resultSha256: competingRegistrationDraftBase.resultSha256,
            traceSha256: competingRegistrationDraftBase.traceSha256,
          }) as never,
        };
        expect((await snapshotStore.registerCandidate(competingRegistrationDraft)).kind).toBe(
          'created',
        );
        let corruption: 'registration' | 'count' | 'bytes' | 'digest' | 'incomplete' =
          'registration';
        let registrationCorruption:
          | 'state_key'
          | 'locator_kind'
          | 'locator_namespace'
          | 'locator_shape'
          | null = null;
        let snapshotCalls = 0;
        const corruptingStore = {
          uploadCandidate: snapshotStore.uploadCandidate.bind(snapshotStore),
          readCandidate: snapshotStore.readCandidate.bind(snapshotStore),
          registerCandidate: async (
            ...args: Parameters<ReferenceStateStore['registerCandidate']>
          ) => {
            const result = await snapshotStore.registerCandidate(...args);
            if (registrationCorruption === null || !result.registration) return result;
            const registration = { ...result.registration };
            if (registrationCorruption === 'state_key') {
              registration.stateKey = {
                ...registration.stateKey,
                workflowIdentity: 'other-workflow',
              };
            } else if (registrationCorruption === 'locator_kind') {
              registration.candidateArtifactLocator = {
                ...registration.candidateArtifactLocator,
                kind: 'unexpected-kind',
              } as never;
            } else if (registrationCorruption === 'locator_namespace') {
              registration.candidateArtifactLocator = {
                ...registration.candidateArtifactLocator,
                namespace: 'unexpected-namespace',
              } as never;
            } else {
              registration.candidateArtifactLocator = null as never;
            }
            return {
              ...result,
              registration,
            };
          },
          createAcceptanceSnapshot: async (
            ...args: Parameters<ReferenceStateStore['createAcceptanceSnapshot']>
          ) => {
            snapshotCalls += 1;
            const snapshot = await snapshotStore.createAcceptanceSnapshot(...args);
            if (corruption === 'registration') {
              const first = snapshot.registrations[0]!;
              return {
                ...snapshot,
                registrations: [
                  {
                    ...first,
                    registration: { ...first.registration, registrationSequence: '999' as never },
                  },
                ],
              };
            }
            if (corruption === 'count') {
              return {
                ...snapshot,
                registrations: Array.from({ length: 65 }, () => snapshot.registrations[0]!),
              };
            }
            if (corruption === 'bytes') {
              const first = snapshot.registrations[0]!;
              return {
                ...snapshot,
                registrations: [{ ...first, registrationBytes: new Uint8Array(2_097_153) }],
              };
            }
            if (corruption === 'incomplete') {
              const registrations = snapshot.registrations.slice(1);
              return {
                ...snapshot,
                registrations,
                candidateSetDigest: computeCandidateSetDigest(
                  snapshot.competingScope,
                  snapshot.cutoff,
                  registrations.map(
                    ({ registrationSequence, registrationId, registrationRecordSha256 }) => ({
                      registrationSequence,
                      registrationId,
                      registrationRecordSha256,
                    }),
                  ),
                  snapshot.enumeration,
                ),
              };
            }
            return { ...snapshot, candidateSetDigest: 'f'.repeat(64) as never };
          },
          readSelector: snapshotStore.readSelector.bind(snapshotStore),
          writeMarker: snapshotStore.writeMarker.bind(snapshotStore),
          readMarker: snapshotStore.readMarker.bind(snapshotStore),
          casSelector: snapshotStore.casSelector.bind(snapshotStore),
          selectAcceptedState: snapshotStore.selectAcceptedState.bind(snapshotStore),
        };
        for (const corruptionCase of [
          'state_key',
          'locator_kind',
          'locator_namespace',
          'locator_shape',
        ] as const) {
          registrationCorruption = corruptionCase;
          expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
            acceptance: 'not_accepted',
            reason: 'candidate_invalid',
          });
        }
        expect(snapshotCalls).toBe(0);
        registrationCorruption = null;
        corruption = 'incomplete';
        expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'candidate_snapshot_invalid',
        });
        corruption = 'registration';
        expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'candidate_snapshot_invalid',
        });
        corruption = 'digest';
        expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'candidate_set_digest_mismatch',
        });
        corruption = 'count';
        expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'candidate_snapshot_limit_exceeded',
        });
        corruption = 'bytes';
        expect(await acceptLocalCandidate(corruptingStore, acceptanceOptions)).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'candidate_snapshot_limit_exceeded',
        });
      } finally {
        await rm(snapshotRoot, { recursive: true, force: true });
      }
      const result = await acceptLocalCandidate(store, acceptanceOptions);
      expect(result.acceptance).toBe('accepted');
      expect(result.publication).toEqual({ status: 'succeeded' });
      if (result.acceptance === 'accepted' || result.acceptance === 'already_accepted') {
        const persistedMarker = await store.readMarker(manifest.stateKey, result.markerId);
        expect(persistedMarker.marker?.acceptedAt).toBe('2026-07-20T00:00:03.000Z');
        const persistedSelector = await store.readSelector(manifest.stateKey);
        expect(persistedSelector.selector?.updatedAt).toBe('2026-07-20T00:00:03.000Z');
      }
      const { ledgerSchemaVersion, prefixContractVersion, ...cacheContractIdentity } =
        manifest.cacheContractIdentity;
      const restored = await store.selectAcceptedState({
        stateKey: manifest.stateKey,
        expectedLedgerSchemaVersion: ledgerSchemaVersion,
        expectedPrefixContractVersion: prefixContractVersion,
        cacheContractIdentity,
        currentHeadSha: manifest.provenance.currentHeadSha,
        currentBaseSha: manifest.provenance.currentBaseSha,
        currentBaseRef: manifest.provenance.currentBaseRef,
        provenanceTrusted: true,
        workflowIdentity: manifest.stateKey.workflowIdentity,
        trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
        headRelationship: 'same',
      });
      expect(restored).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'continuation_selected', transitionPlan: 'continuation' },
      });
      const restoredWithoutAncestry = await store.selectAcceptedState({
        stateKey: manifest.stateKey,
        expectedLedgerSchemaVersion: ledgerSchemaVersion,
        expectedPrefixContractVersion: prefixContractVersion,
        cacheContractIdentity,
        currentHeadSha: manifest.provenance.currentHeadSha,
        currentBaseSha: manifest.provenance.currentBaseSha,
        currentBaseRef: manifest.provenance.currentBaseRef,
        provenanceTrusted: true,
        workflowIdentity: manifest.stateKey.workflowIdentity,
        trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
      });
      expect(restoredWithoutAncestry).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'continuation_selected', transitionPlan: 'continuation' },
      });
      const restoredWithUnknownAncestry = await store.selectAcceptedState({
        stateKey: manifest.stateKey,
        expectedLedgerSchemaVersion: ledgerSchemaVersion,
        expectedPrefixContractVersion: prefixContractVersion,
        cacheContractIdentity,
        currentHeadSha: manifest.provenance.currentHeadSha,
        currentBaseSha: manifest.provenance.currentBaseSha,
        currentBaseRef: manifest.provenance.currentBaseRef,
        provenanceTrusted: true,
        workflowIdentity: manifest.stateKey.workflowIdentity,
        trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
        headRelationship: 'unknown',
      });
      expect(restoredWithUnknownAncestry).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'continuation_selected', transitionPlan: 'continuation' },
      });
      if (result.acceptance === 'accepted' || result.acceptance === 'already_accepted') {
        expect(publishedMarkerId).toBe(result.markerId);
      }
      const acceptedCandidateId = computeCandidateId({
        manifestSha256: sha256Hex(manifestBytes),
        candidateLedgerSha256: sha256Hex(ledgerBytes),
        providerRunMetadataSha256: sha256Hex(providerRunMetadataBytes),
        metadataSemanticSha256: manifest.transaction.metadataSemanticSha256,
        consumedInputSha256: inputSha256,
        resultSha256: sha256Hex(resultBytes),
        traceSha256: sha256Hex(traceBytes),
      });
      await writeFile(
        path.join(root, 'candidates', acceptedCandidateId, 'ledger.json'),
        new Uint8Array(524_289),
        { mode: 0o600 },
      );
      expect(
        await store.selectAcceptedState({
          stateKey: manifest.stateKey,
          expectedLedgerSchemaVersion: ledgerSchemaVersion,
          expectedPrefixContractVersion: prefixContractVersion,
          cacheContractIdentity,
          currentHeadSha: manifest.provenance.currentHeadSha,
          currentBaseSha: manifest.provenance.currentBaseSha,
          currentBaseRef: manifest.provenance.currentBaseRef,
          provenanceTrusted: true,
          workflowIdentity: manifest.stateKey.workflowIdentity,
          trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
          headRelationship: 'same',
        }),
      ).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'recovery_root_selected', recoveryReason: 'over_bound_ledger' },
      });
      expect(
        await store.selectAcceptedState({
          stateKey: manifest.stateKey,
          expectedLedgerSchemaVersion: ledgerSchemaVersion,
          expectedPrefixContractVersion: prefixContractVersion,
          cacheContractIdentity,
          currentHeadSha: manifest.provenance.currentHeadSha,
          currentBaseSha: manifest.provenance.currentBaseSha,
          currentBaseRef: manifest.provenance.currentBaseRef,
          provenanceTrusted: true,
          workflowIdentity: manifest.stateKey.workflowIdentity,
          trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
          headRelationship: 'same',
          explicitRestore: true,
        }),
      ).toMatchObject({
        selection: 'selected',
        snapshot: { kind: 'explicit_restore_invalid', failure: 'over_bound_ledger' },
      });
      await writeFile(
        path.join(root, 'candidates', acceptedCandidateId, 'ledger.json'),
        ledgerBytes,
        { mode: 0o600 },
      );
      await writeFile(
        path.join(root, 'candidates', acceptedCandidateId, 'provider-run-metadata.json'),
        new Uint8Array(32_769),
        { mode: 0o600 },
      );
      expect(
        await store.selectAcceptedState({
          stateKey: manifest.stateKey,
          expectedLedgerSchemaVersion: ledgerSchemaVersion,
          expectedPrefixContractVersion: prefixContractVersion,
          cacheContractIdentity,
          currentHeadSha: manifest.provenance.currentHeadSha,
          currentBaseSha: manifest.provenance.currentBaseSha,
          currentBaseRef: manifest.provenance.currentBaseRef,
          provenanceTrusted: true,
          workflowIdentity: manifest.stateKey.workflowIdentity,
          trustedExecutionDomain: manifest.stateKey.trustedExecutionDomain,
          headRelationship: 'same',
        }),
      ).toMatchObject({
        selection: 'selected',
        snapshot: {
          kind: 'recovery_root_selected',
          recoveryReason: 'corrupt_accepted_artifact',
        },
      });

      const staleRoot = await mkdtemp(path.join(os.tmpdir(), 'm4-state-acceptance-stale-'));
      try {
        const staleStore = new ReferenceStateStore(staleRoot);
        await staleStore.close();
        const staleSelector = selectorFor(
          markerFor('e'.repeat(64), undefined, sha, manifest.stateKey),
          manifest.stateKey,
        );
        expect((await staleStore.casSelector('bootstrap', staleSelector)).kind).toBe('applied');
        const staleResult = await acceptLocalCandidate(staleStore, acceptanceOptions);
        expect(staleResult).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'stale_candidate',
        });
      } finally {
        await rm(staleRoot, { recursive: true, force: true });
      }

      const markerCancellationRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-cancel-marker-'),
      );
      try {
        const markerController = new AbortController();
        const markerCancellationStore = new ReferenceStateStore(markerCancellationRoot, {
          beforeMarkerCommit: () => {
            markerController.abort();
          },
        });
        await markerCancellationStore.close();
        const markerCancellation = await acceptLocalCandidate(markerCancellationStore, {
          ...acceptanceOptions,
          signal: markerController.signal,
        });
        expect(markerCancellation).toMatchObject({
          acceptance: 'not_accepted',
          reason: 'cancelled_before_acceptance',
        });
      } finally {
        await rm(markerCancellationRoot, { recursive: true, force: true });
      }

      const casCancellationRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-cancel-cas-'),
      );
      try {
        const casController = new AbortController();
        const casCancellationStore = new ReferenceStateStore(casCancellationRoot, {
          beforeSelectorCommit: () => {
            casController.abort();
          },
        });
        await casCancellationStore.close();
        const casCancellation = await acceptLocalCandidate(casCancellationStore, {
          ...acceptanceOptions,
          signal: casController.signal,
        });
        expect(casCancellation).toMatchObject({
          acceptance: 'accepted',
          publication: { status: 'pending', code: 'cancelled_after_acceptance' },
        });
      } finally {
        await rm(casCancellationRoot, { recursive: true, force: true });
      }

      const unknownRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-sticky-unknown-'),
      );
      try {
        const unknownStore = new ReferenceStateStore(unknownRoot);
        await unknownStore.close();
        const unknownPublication = await acceptLocalCandidate(unknownStore, {
          ...acceptanceOptions,
          publishSticky: async () => {
            throw new StickyCallbackOutcomeUnknownError();
          },
        });
        expect(unknownPublication.publication).toEqual({
          status: 'unknown',
          code: 'sticky_callback_outcome_unknown',
        });
      } finally {
        await rm(unknownRoot, { recursive: true, force: true });
      }

      const registrationReadbackFailureRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-registration-readback-failure-'),
      );
      try {
        const registrationDirectory = path.join(
          registrationReadbackFailureRoot,
          'states',
          sha256Hex(canonicalJsonBytes(acceptanceOptions.selectionSnapshot.stateKey)),
          'registrations',
        );
        const registrationReadbackFailureStore = new ReferenceStateStore(
          registrationReadbackFailureRoot,
          {
            afterRegistrationCommit: async () => {
              const entries = await readdir(registrationDirectory);
              const registrationEntry = entries.find((entry) => entry.endsWith('.json'));
              if (!registrationEntry) throw new Error('registration file missing');
              await chmod(path.join(registrationDirectory, registrationEntry), 0o644);
              throw new Error('registration commit outcome unknown');
            },
          },
        );
        await registrationReadbackFailureStore.close();
        let called = false;
        const registrationReadbackFailure = await acceptLocalCandidate(
          registrationReadbackFailureStore,
          {
            ...acceptanceOptions,
            publishSticky: async () => {
              called = true;
            },
          },
        );
        expect(registrationReadbackFailure).toMatchObject({
          acceptance: 'unknown',
          reason: 'acceptance_outcome_unknown',
          publication: { status: 'not_attempted' },
        });
        expect(called).toBe(false);
      } finally {
        await rm(registrationReadbackFailureRoot, { recursive: true, force: true });
      }

      const markerReadbackFailureRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-marker-readback-failure-'),
      );
      try {
        const markerReadbackFailureStore = new ReferenceStateStore(markerReadbackFailureRoot, {
          afterMarkerCommit: () => {
            throw new Error('marker commit outcome unknown');
          },
        });
        await markerReadbackFailureStore.close();
        markerReadbackFailureStore.readMarker = async () => {
          throw new StoreTransactionError('store_transaction_failed');
        };
        let called = false;
        const markerReadbackFailure = await acceptLocalCandidate(markerReadbackFailureStore, {
          ...acceptanceOptions,
          publishSticky: async () => {
            called = true;
          },
        });
        expect(markerReadbackFailure).toMatchObject({
          acceptance: 'unknown',
          reason: 'acceptance_outcome_unknown',
          publication: { status: 'not_attempted' },
        });
        expect(called).toBe(false);
      } finally {
        await rm(markerReadbackFailureRoot, { recursive: true, force: true });
      }

      const selectorReadbackFailureRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-selector-readback-failure-'),
      );
      try {
        const selectorReadbackFailureStore = new ReferenceStateStore(selectorReadbackFailureRoot, {
          afterSelectorCommit: () => {
            throw new Error('selector commit outcome unknown');
          },
        });
        await selectorReadbackFailureStore.close();
        selectorReadbackFailureStore.readSelector = async () => {
          throw new StoreTransactionError('store_transaction_failed');
        };
        let called = false;
        const selectorReadbackFailure = await acceptLocalCandidate(selectorReadbackFailureStore, {
          ...acceptanceOptions,
          publishSticky: async () => {
            called = true;
          },
        });
        expect(selectorReadbackFailure).toMatchObject({
          acceptance: 'unknown',
          reason: 'acceptance_outcome_unknown',
          publication: { status: 'not_attempted' },
        });
        expect(called).toBe(false);
      } finally {
        await rm(selectorReadbackFailureRoot, { recursive: true, force: true });
      }

      const failedRoot = await mkdtemp(
        path.join(os.tmpdir(), 'm4-state-acceptance-sticky-failed-'),
      );
      try {
        const failedStore = new ReferenceStateStore(failedRoot);
        await failedStore.close();
        const failedPublication = await acceptLocalCandidate(failedStore, {
          ...acceptanceOptions,
          publishSticky: async () => {
            throw new Error('sticky publication failed');
          },
        });
        expect(failedPublication.publication).toEqual({
          status: 'failed',
          code: 'sticky_callback_failed',
        });
      } finally {
        await rm(failedRoot, { recursive: true, force: true });
      }
      expect(releaseCount).toBe(21);
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  }, 15_000);
});
