import { Ajv } from 'ajv';
import { createHash } from 'node:crypto';
import schema from '../../protocol/schemas/provider-session-ledger.v1.json' with { type: 'json' };
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { LEDGER_MAX_BYTES } from '../state-v2/constants.js';
import { LiveRuntimeInvocationError } from './errors.js';
import type { ReviewResultV1 } from '../protocol/review-result.js';

export interface HostLedgerValidationContext {
  readonly stateKey: Record<string, unknown>;
  readonly sessionEpoch: string;
  readonly cacheContractIdentity: Record<string, unknown>;
  readonly generation: Record<string, unknown>;
  readonly transition: Record<string, unknown>;
  readonly currentInteraction: {
    readonly interactionId: string;
    readonly interactionOrdinal: number;
    readonly subjectDigest: string;
    readonly cacheContractDigest: string;
  };
  readonly outcome?: Pick<ReviewResultV1, 'summary' | 'findings' | 'limitations'>;
}

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: false });
const validate = ajv.compile(schema);

export function validateCandidateLedgerForHost(
  bytes: Uint8Array,
  context?: HostLedgerValidationContext,
  predecessorBytes?: Uint8Array,
): Record<string, unknown> {
  const value = validateRaw(bytes);
  if (context) validateHostProjection(value, context, predecessorBytes);
  return value;
}

function validateRaw(bytes: Uint8Array): Record<string, unknown> {
  if (bytes.byteLength > LEDGER_MAX_BYTES)
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger exceeds the raw byte cap.',
    });
  const owned = new Uint8Array(bytes);
  if (owned[0] === 0xef && owned[1] === 0xbb && owned[2] === 0xbf)
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger has a BOM.',
    });
  let value: unknown;
  try {
    value = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(owned));
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is not strict UTF-8 JSON.',
    });
  }
  if (!validate(value))
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger schema validation failed.',
    });
  let canonical: Uint8Array;
  try {
    canonical = canonicalJsonBytes(value);
  } catch {
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is outside the canonical JSON domain.',
    });
  }
  if (!equalBytes(canonical, owned))
    throw new LiveRuntimeInvocationError({
      kind: 'candidate-ledger-invalid',
      message: 'Candidate ledger is not canonical byte-for-byte.',
    });
  return value as Record<string, unknown>;
}

function validateHostProjection(
  value: Record<string, unknown>,
  context: HostLedgerValidationContext,
  predecessorBytes: Uint8Array | undefined,
): void {
  const header = value.header as Record<string, unknown>;
  const state = context.stateKey;
  const identity = context.cacheContractIdentity;
  const generation = context.generation;
  const expected = {
    kind: context.transition.kind,
    sessionEpoch: context.sessionEpoch,
    ledgerEpoch: generation.ledgerEpoch,
    stateGeneration: generation.stateGeneration,
    repository: state.repository,
    headRepository: state.headRepository,
    pullRequest: state.pullRequest,
    workflowIdentity: state.workflowIdentity,
    trustedExecutionDomain: state.trustedExecutionDomain,
    providerId: identity.providerId,
    modelId: identity.modelId,
    adapterId: identity.adapterId,
    templateId: identity.templateId,
    policyId: identity.policyId,
    toolDefinitionId: identity.toolDefinitionId,
    cacheConfigId: identity.cacheConfigId,
  };
  for (const [key, expectedValue] of Object.entries(expected)) {
    if (header[key] !== expectedValue)
      throw invalid('Candidate ledger header does not match the frozen context.');
  }
  const kind = context.transition.kind;
  if (kind === 'bootstrap' || kind === 'recovery_root') {
    if (
      header.predecessorLedgerSha256 !== 'bootstrap' ||
      header.predecessorManifestSha256 !== 'bootstrap' ||
      (value.records as unknown[]).length !== 2
    )
      throw invalid('Root candidate ledger has an invalid predecessor or record shape.');
  } else {
    if (!predecessorBytes)
      throw invalid('A non-root candidate ledger requires a predecessor snapshot.');
    const predecessor = validateRaw(predecessorBytes);
    const predecessorHeader = predecessor.header as Record<string, unknown>;
    const predecessorHash = sha256Hex(predecessorBytes);
    const transition = context.transition;
    if (
      header.predecessorLedgerSha256 !== predecessorHash ||
      header.predecessorLedgerSha256 !== transition.predecessorLedgerSha256 ||
      header.predecessorLedgerEpoch !== transition.predecessorLedgerEpoch ||
      header.predecessorStateGeneration !== transition.predecessorStateGeneration ||
      predecessorHeader.ledgerEpoch !== transition.predecessorLedgerEpoch ||
      predecessorHeader.stateGeneration !== transition.predecessorStateGeneration
    )
      throw invalid('Candidate ledger predecessor binding does not match the context.');
    if (
      kind === 'reset' &&
      header.predecessorManifestSha256 !== context.transition.predecessorManifestSha256
    )
      throw invalid('Reset predecessor manifest binding does not match the context.');
    const predecessorRecords = predecessor.records as unknown[];
    const records = value.records as unknown[];
    if (kind === 'continuation') {
      if (records.length !== predecessorRecords.length + 2)
        throw invalid('Continuation record count is invalid.');
      for (let index = 0; index < predecessorRecords.length; index += 1) {
        if (
          !equalBytes(
            canonicalJsonBytes(records[index]),
            canonicalJsonBytes(predecessorRecords[index]),
          )
        )
          throw invalid('Continuation does not preserve predecessor records.');
      }
    } else if (records.length !== 2) {
      throw invalid('Reset record shape is invalid.');
    }
  }
  const records = value.records as Array<Record<string, unknown>>;
  const contextRecord = records.find(
    (record) =>
      record.role === 'review_context' &&
      record.interactionId === context.currentInteraction.interactionId,
  );
  if (
    !contextRecord ||
    contextRecord.interactionId !== context.currentInteraction.interactionId ||
    contextRecord.interactionOrdinal !== context.currentInteraction.interactionOrdinal ||
    contextRecord.subjectDigest !== context.currentInteraction.subjectDigest ||
    contextRecord.cacheContractDigest !== context.currentInteraction.cacheContractDigest
  )
    throw invalid('Candidate context record does not project the frozen interaction.');
  if (context.outcome) {
    const outcomeRecord = records.find(
      (record) =>
        record.role === 'review_outcome' &&
        record.interactionId === context.currentInteraction.interactionId,
    );
    if (
      !outcomeRecord ||
      outcomeRecord.summary !== context.outcome.summary ||
      !equalJson(outcomeRecord.limitations, context.outcome.limitations) ||
      !equalFindings(outcomeRecord.findings, context.outcome.findings)
    )
      throw invalid('Candidate outcome record does not project the validated result.');
  }
}

function equalJson(left: unknown, right: unknown): boolean {
  return equalBytes(canonicalJsonBytes(left), canonicalJsonBytes(right));
}

function equalFindings(left: unknown, right: ReviewResultV1['findings']): boolean {
  if (!Array.isArray(left) || left.length !== right.length) return false;
  return left.every((candidate, index) => {
    const expected = right[index];
    if (!candidate || typeof candidate !== 'object' || !expected) return false;
    const actual = candidate as Record<string, unknown>;
    return (
      actual.severity === expected.severity &&
      actual.confidence === expected.confidence &&
      actual.category === expected.category &&
      actual.title === expected.title &&
      actual.body === expected.body &&
      (actual.path ?? null) === (expected.path ?? null) &&
      (actual.startLine ?? null) === (expected.startLine ?? null) &&
      (actual.endLine ?? null) === (expected.endLine ?? null)
    );
  });
}

function invalid(message: string): LiveRuntimeInvocationError {
  return new LiveRuntimeInvocationError({ kind: 'binding-mismatch', message });
}

function sha256Hex(bytes: Uint8Array): string {
  const hash = createHash('sha256');
  hash.update(bytes);
  return hash.digest('hex');
}

function equalBytes(a: Uint8Array, b: Uint8Array): boolean {
  return a.byteLength === b.byteLength && a.every((byte, index) => byte === b[index]);
}
