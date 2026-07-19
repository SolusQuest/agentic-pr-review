import { readFileSync } from 'node:fs';
import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { validateCandidateLedgerForHost } from './ledger-validator.js';

const ledgerFixture = JSON.parse(
  readFileSync('protocol/fixtures/v1/provider-session-ledger/valid-bootstrap.json', 'utf8'),
) as Record<string, any>;

function context() {
  const header = ledgerFixture.header;
  const contextRecord = ledgerFixture.records[0];
  return {
    stateKey: {
      repository: header.repository,
      headRepository: header.headRepository,
      pullRequest: header.pullRequest,
      workflowIdentity: header.workflowIdentity,
      trustedExecutionDomain: header.trustedExecutionDomain,
    },
    sessionEpoch: header.sessionEpoch,
    cacheContractIdentity: {
      providerId: header.providerId,
      modelId: header.modelId,
      adapterId: header.adapterId,
      templateId: header.templateId,
      policyId: header.policyId,
      toolDefinitionId: header.toolDefinitionId,
      cacheConfigId: header.cacheConfigId,
    },
    generation: { ledgerEpoch: header.ledgerEpoch, stateGeneration: header.stateGeneration },
    transition: { kind: 'bootstrap', reason: 'new_session' },
    currentInteraction: {
      interactionId: contextRecord.interactionId,
      interactionOrdinal: contextRecord.interactionOrdinal,
      subjectDigest: contextRecord.subjectDigest,
      cacheContractDigest: contextRecord.cacheContractDigest,
      reviewedHeadSha: contextRecord.reviewedHeadSha,
      reviewedBaseSha: contextRecord.reviewedBaseSha,
      changedFiles: contextRecord.changedFiles,
    },
    outcome: {
      summary: ledgerFixture.records[1].summary,
      findings: [],
      limitations: [],
    },
  };
}

function bytes(value: unknown): Uint8Array {
  return canonicalJsonBytes(value);
}

describe('host ledger acceptance', () => {
  it('accepts the contract bootstrap root without a predecessor manifest sentinel', () => {
    expect(() => validateCandidateLedgerForHost(bytes(ledgerFixture), context())).not.toThrow();
  });

  it('rejects a current context projection mutation', () => {
    const mutated = structuredClone(ledgerFixture);
    mutated.records[0].reviewedHeadSha = '2'.repeat(40);
    expect(() => validateCandidateLedgerForHost(bytes(mutated), context())).toThrowError(
      /context record/i,
    );
  });

  it('rejects an outcome optional-field mutation', () => {
    const mutated = structuredClone(ledgerFixture);
    mutated.records[1].summary = 'different';
    expect(() => validateCandidateLedgerForHost(bytes(mutated), context())).toThrowError(
      /outcome record/i,
    );
  });
});
