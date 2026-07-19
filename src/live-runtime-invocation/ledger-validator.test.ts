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

  it.each([262143, 262144])('accepts a canonical ledger at %i bytes', (target) => {
    const mutated = structuredClone(ledgerFixture);
    const findings = Array.from({ length: 50 }, () => ({
      severity: 'low',
      confidence: 'high',
      category: 'maintainability',
      title: 't',
      body: '',
      evidence: 'e'.repeat(2000),
      suggestedAction: 's'.repeat(1600),
      path: 'src/a.ts',
      startLine: 1,
      endLine: 1,
    }));
    const base = structuredClone(mutated);
    base.records[1].findings = findings;
    const baseLength = canonicalJsonBytes(base).byteLength;
    const delta = target - baseLength;
    expect(delta).toBeGreaterThan(50);
    const perFinding = Math.floor(delta / findings.length);
    const remainder = delta - perFinding * findings.length;
    mutated.records[1].findings = findings.map((finding, index) => ({
      ...finding,
      body: 'b'.repeat(perFinding + (index === 0 ? remainder : 0)),
    }));
    const candidateContext = context();
    candidateContext.outcome!.findings = mutated.records[1].findings;
    expect(canonicalJsonBytes(mutated).byteLength).toBe(target);
    expect(() => validateCandidateLedgerForHost(bytes(mutated), candidateContext)).not.toThrow();
  });

  it('rejects the first byte over the canonical ledger cap', () => {
    const mutated = structuredClone(ledgerFixture);
    const findings = Array.from({ length: 50 }, () => ({
      severity: 'low',
      confidence: 'high',
      category: 'maintainability',
      title: 't',
      body: '',
      evidence: 'e'.repeat(2000),
      suggestedAction: 's'.repeat(1600),
      path: 'src/a.ts',
      startLine: 1,
      endLine: 1,
    }));
    const base = structuredClone(mutated);
    base.records[1].findings = findings;
    const delta = 262145 - canonicalJsonBytes(base).byteLength;
    const perFinding = Math.floor(delta / findings.length);
    const remainder = delta - perFinding * findings.length;
    mutated.records[1].findings = findings.map((finding, index) => ({
      ...finding,
      body: 'b'.repeat(perFinding + (index === 0 ? remainder : 0)),
    }));
    const candidateContext = context();
    candidateContext.outcome!.findings = mutated.records[1].findings;
    expect(canonicalJsonBytes(mutated).byteLength).toBe(262145);
    expect(() => validateCandidateLedgerForHost(bytes(mutated), candidateContext)).toThrowError(
      /canonical byte cap/i,
    );
  });
});
