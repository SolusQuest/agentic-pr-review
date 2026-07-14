import type {
  LEDGER_FILENAME,
  LEDGER_SCHEMA_VERSION,
  PROVIDER_RUN_METADATA_FILENAME,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  STATE_NAMESPACE,
} from './constants.js';

export type EpochId = string;
export type Sha256Hex = string;
export type GitSha = string;

export interface StateKeyV2 {
  namespace: typeof STATE_NAMESPACE;
  repository: string;
  headRepository: string;
  pullRequest: number;
  workflowIdentity: string;
  trustedExecutionDomain: string;
}

export interface CacheContractIdentityV2 {
  ledgerSchemaVersion: typeof LEDGER_SCHEMA_VERSION;
  prefixContractVersion: 1;
  providerId: string;
  modelId: string;
  adapterId: Sha256Hex;
  templateId: Sha256Hex;
  policyId: Sha256Hex;
  toolDefinitionId: Sha256Hex;
  cacheConfigId: Sha256Hex;
}

export interface GenerationV2 {
  stateGeneration: number;
  ledgerEpoch: EpochId;
}

export interface ProducingGenerationV2 {
  sessionEpoch: EpochId;
  stateGeneration: number;
  ledgerEpoch: EpochId;
}

export type StateManifestV2Transition =
  | {
      kind: 'bootstrap';
      predecessorManifestSha256: 'bootstrap';
      predecessorLedgerSha256: 'bootstrap';
      reason: 'new_session';
    }
  | {
      kind: 'continuation';
      predecessorManifestSha256: Sha256Hex;
      predecessorLedgerSha256: Sha256Hex;
      predecessorStateGeneration: number;
      predecessorLedgerEpoch: EpochId;
    }
  | {
      kind: 'reset';
      predecessorManifestSha256: Sha256Hex;
      predecessorLedgerSha256: Sha256Hex;
      predecessorStateGeneration: number;
      predecessorLedgerEpoch: EpochId;
      reason: 'base_change' | 'head_history_discontinuity' | 'cache_contract_change';
    }
  | {
      kind: 'recovery_root';
      predecessorManifestSha256: 'bootstrap';
      predecessorLedgerSha256: 'bootstrap';
      reason:
        | 'corrupt_accepted_artifact'
        | 'integrity_mismatch'
        | 'unsafe_provenance'
        | 'state_key_mismatch'
        | 'contract_version_incompatible'
        | 'over_bound_ledger'
        | 'unavailable_accepted_artifact';
    };

export interface ProvenanceV2 {
  reviewedHeadSha: GitSha;
  reviewedBaseSha: GitSha;
  reviewedBaseRef: string;
  currentHeadSha: GitSha;
  currentBaseSha: GitSha;
  currentBaseRef: string;
  workflowEvent: string;
  producingRunId: string;
  producingRunAttempt: number;
  producingWorkflowRef: string;
  producingGitRef: string;
  producingActionSourceSha: GitSha;
  producedAt: string;
}

export interface TransactionV2 {
  interactionId: Sha256Hex;
  interactionOrdinal: number;
  consumedInputSha256: Sha256Hex;
  resultSha256: Sha256Hex;
  traceSha256: Sha256Hex;
  candidateLedgerSha256: Sha256Hex;
  metadataSemanticSha256: Sha256Hex;
}

export interface LedgerDescriptorV2 {
  path: typeof LEDGER_FILENAME;
  sha256: Sha256Hex;
  bytes: number;
  schemaVersion: typeof LEDGER_SCHEMA_VERSION;
}

export interface ProviderRunMetadataDescriptorV2 {
  path: typeof PROVIDER_RUN_METADATA_FILENAME;
  sha256: Sha256Hex;
  bytes: number;
  schemaVersion: typeof PROVIDER_RUN_METADATA_SCHEMA_VERSION;
  producingGeneration: ProducingGenerationV2;
}

export interface StateManifestV2 {
  version: 2;
  stateNamespace: typeof STATE_NAMESPACE;
  stateKey: StateKeyV2;
  sessionEpoch: EpochId;
  cacheContractIdentity: CacheContractIdentityV2;
  generation: GenerationV2;
  transition: StateManifestV2Transition;
  provenance: ProvenanceV2;
  transaction: TransactionV2;
  ledger: LedgerDescriptorV2;
  providerRunMetadata: ProviderRunMetadataDescriptorV2;
}

/**
 * Builder input. The caller supplies everything except the descriptor
 * hash/length fields and `transaction.candidateLedgerSha256`; the builder
 * derives those from the supplied bytes.
 */
export interface StateManifestV2Input {
  version: 2;
  stateNamespace: typeof STATE_NAMESPACE;
  stateKey: StateKeyV2;
  sessionEpoch: EpochId;
  cacheContractIdentity: CacheContractIdentityV2;
  generation: GenerationV2;
  transition: StateManifestV2Transition;
  provenance: ProvenanceV2;
  transaction: Omit<TransactionV2, 'candidateLedgerSha256'>;
  ledger: {
    path: typeof LEDGER_FILENAME;
    schemaVersion: typeof LEDGER_SCHEMA_VERSION;
  };
  providerRunMetadata: {
    path: typeof PROVIDER_RUN_METADATA_FILENAME;
    schemaVersion: typeof PROVIDER_RUN_METADATA_SCHEMA_VERSION;
    producingGeneration: ProducingGenerationV2;
  };
}
