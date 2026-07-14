import { createHash } from 'node:crypto';
import type {
  CacheContractIdentityV2,
  EpochId,
  GenerationV2,
  GitSha,
  ProducingGenerationV2,
  ProvenanceV2,
  Sha256Hex,
  StateKeyV2,
  StateManifestV2,
  StateManifestV2Input,
  StateManifestV2Transition,
} from './manifest.js';
import {
  LEDGER_FILENAME,
  LEDGER_SCHEMA_VERSION,
  PROVIDER_RUN_METADATA_FILENAME,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  STATE_NAMESPACE,
} from './constants.js';

export function sha256Hex(bytes: Uint8Array | string): Sha256Hex {
  const input = typeof bytes === 'string' ? new TextEncoder().encode(bytes) : bytes;
  return createHash('sha256').update(input).digest('hex');
}

export function makeStateKey(overrides: Partial<StateKeyV2> = {}): StateKeyV2 {
  return {
    namespace: STATE_NAMESPACE,
    repository: 'SolusQuest/agentic-pr-review',
    headRepository: 'SolusQuest/agentic-pr-review',
    pullRequest: 48,
    workflowIdentity: 'agentic-pr-review',
    trustedExecutionDomain: 'trusted',
    ...overrides,
  };
}

export function makeCacheContract(
  overrides: Partial<CacheContractIdentityV2> = {},
): CacheContractIdentityV2 {
  return {
    ledgerSchemaVersion: 1,
    prefixContractVersion: 1,
    providerId: 'anthropic',
    modelId: 'claude-sonnet-4-20250514',
    adapterId: sha256Hex('adapter'),
    templateId: sha256Hex('template'),
    policyId: sha256Hex('policy'),
    toolDefinitionId: sha256Hex('tools'),
    cacheConfigId: sha256Hex('cache-config'),
    ...overrides,
  };
}

export function makeGeneration(overrides: Partial<GenerationV2> = {}): GenerationV2 {
  return {
    stateGeneration: 0,
    ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA',
    ...overrides,
  };
}

export function makeProducingGeneration(
  sessionEpoch: EpochId,
  gen: GenerationV2,
  overrides: Partial<ProducingGenerationV2> = {},
): ProducingGenerationV2 {
  return {
    sessionEpoch,
    stateGeneration: gen.stateGeneration,
    ledgerEpoch: gen.ledgerEpoch,
    ...overrides,
  };
}

const FAKE_HEAD_SHA: GitSha = 'a'.repeat(40);
const FAKE_BASE_SHA: GitSha = 'b'.repeat(40);
const FAKE_ACTION_SHA: GitSha = 'c'.repeat(40);

export function makeProvenance(overrides: Partial<ProvenanceV2> = {}): ProvenanceV2 {
  return {
    reviewedHeadSha: FAKE_HEAD_SHA,
    reviewedBaseSha: FAKE_BASE_SHA,
    reviewedBaseRef: 'refs/heads/main',
    currentHeadSha: FAKE_HEAD_SHA,
    currentBaseSha: FAKE_BASE_SHA,
    currentBaseRef: 'refs/heads/main',
    workflowEvent: 'pull_request',
    producingRunId: '123456789',
    producingRunAttempt: 1,
    producingWorkflowRef:
      'SolusQuest/agentic-pr-review/.github/workflows/review.yml@refs/heads/main',
    producingGitRef: 'refs/pull/48/merge',
    producingActionSourceSha: FAKE_ACTION_SHA,
    producedAt: '2026-07-14T00:00:00Z',
    ...overrides,
  };
}

export interface MakeInputOptions {
  sessionEpoch?: EpochId;
  stateKey?: Partial<StateKeyV2>;
  cacheContractIdentity?: Partial<CacheContractIdentityV2>;
  generation?: Partial<GenerationV2>;
  transition?: StateManifestV2Transition;
  provenance?: Partial<ProvenanceV2>;
  transaction?: Partial<Omit<StateManifestV2['transaction'], 'candidateLedgerSha256'>>;
  producingGeneration?: Partial<ProducingGenerationV2>;
}

export function makeStateManifestV2Input(opts: MakeInputOptions = {}): StateManifestV2Input {
  const sessionEpoch = opts.sessionEpoch ?? 'S00000000000000000000A';
  const generation = makeGeneration(opts.generation);
  const stateKey = makeStateKey(opts.stateKey);
  const transition: StateManifestV2Transition = opts.transition ?? {
    kind: 'bootstrap',
    predecessorManifestSha256: 'bootstrap',
    predecessorLedgerSha256: 'bootstrap',
    reason: 'new_session',
  };
  const transactionBase = {
    interactionId: sha256Hex('interaction'),
    interactionOrdinal: transition.kind === 'continuation' ? 1 : 0,
    consumedInputSha256: sha256Hex('input'),
    resultSha256: sha256Hex('result'),
    traceSha256: sha256Hex('trace'),
    metadataSemanticSha256: sha256Hex('metadata-semantic'),
    ...opts.transaction,
  };
  const producingGeneration = makeProducingGeneration(
    sessionEpoch,
    generation,
    opts.producingGeneration,
  );
  return {
    version: 2,
    stateNamespace: STATE_NAMESPACE,
    stateKey,
    sessionEpoch,
    cacheContractIdentity: makeCacheContract(opts.cacheContractIdentity),
    generation,
    transition,
    provenance: makeProvenance(opts.provenance),
    transaction: transactionBase,
    ledger: {
      path: LEDGER_FILENAME,
      schemaVersion: LEDGER_SCHEMA_VERSION,
    },
    providerRunMetadata: {
      path: PROVIDER_RUN_METADATA_FILENAME,
      schemaVersion: PROVIDER_RUN_METADATA_SCHEMA_VERSION,
      producingGeneration,
    },
  };
}
