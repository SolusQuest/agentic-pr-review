import { describe, expect, it } from 'vitest';
import { artifactProvenanceVectors } from './artifact-provenance-vectors.js';

describe('R4 S2 artifact provenance vectors', () => {
  it('preserves the complete stable provider-independent vector set', () => {
    const expectedIds = Array.from(
      { length: 32 },
      (_, index) => `APV-${String(index + 1).padStart(3, '0')}`,
    );
    const actualIds = artifactProvenanceVectors.map(({ id }) => id);

    expect(actualIds).toEqual(expectedIds);
    expect(new Set(actualIds).size).toBe(actualIds.length);
  });

  it('owns every vector as permanent S2 conformance evidence', () => {
    for (const vector of artifactProvenanceVectors) {
      expect(vector.inputMetadata.length).toBeGreaterThan(0);
      expect(vector.selectionContext).not.toBe('');
      expect(vector.expectedOutcome).not.toBe('');
      expect(vector.securityInvariant).not.toBe('');

      expect(vector.owner).toBe('R4 S2 private artifact bridge');
      expect(vector.evidenceClass).toBe('permanent-negative-conformance');
      if (vector.disposition === 'obsolete-absent') {
        expect(vector.deletionRationale).not.toBe('');
      } else {
        expect(vector.deletionRationale).toBeUndefined();
      }
    }
  });

  it('records the vector-by-vector S2 ownership boundary', () => {
    const group = (disposition: string) =>
      artifactProvenanceVectors
        .filter((vector) => vector.disposition === disposition)
        .map((vector) => vector.id);

    expect(group('direct-transport')).toEqual([
      'APV-001',
      'APV-002',
      'APV-003',
      'APV-004',
      'APV-005',
      'APV-006',
      'APV-007',
      'APV-008',
      'APV-026',
      'APV-027',
      'APV-028',
      'APV-030',
    ]);
    expect(group('later-policy')).toEqual(['APV-023', 'APV-024', 'APV-025']);
    expect(group('obsolete-absent')).toEqual(['APV-031', 'APV-032']);
    expect(group('negative-ownership')).toEqual([
      'APV-009',
      'APV-010',
      'APV-011',
      'APV-012',
      'APV-013',
      'APV-014',
      'APV-015',
      'APV-016',
      'APV-017',
      'APV-018',
      'APV-019',
      'APV-020',
      'APV-021',
      'APV-022',
      'APV-029',
    ]);
  });

  it('preserves the retired producer-id precedence cases in APV-004', () => {
    const vector = artifactProvenanceVectors.find(({ id }) => id === 'APV-004');

    expect(vector?.cases).toEqual([
      {
        id: 'repository-missing',
        lookup: 'repository',
        embeddedWorkflowRunId: 'missing',
        producerIdentitySource: 'none',
        expectedOutcome: 'ineligible',
      },
      {
        id: 'repository-null',
        lookup: 'repository',
        embeddedWorkflowRunId: 'null',
        producerIdentitySource: 'none',
        expectedOutcome: 'ineligible',
      },
      {
        id: 'repository-zero',
        lookup: 'repository',
        embeddedWorkflowRunId: 'zero',
        producerIdentitySource: 'none',
        expectedOutcome: 'ineligible',
      },
      {
        id: 'explicit-missing',
        lookup: 'explicit-run',
        embeddedWorkflowRunId: 'missing',
        explicitRunId: 'positive',
        producerIdentitySource: 'explicitRunId',
        expectedOutcome: 'evaluate-provenance',
      },
      {
        id: 'explicit-null',
        lookup: 'explicit-run',
        embeddedWorkflowRunId: 'null',
        explicitRunId: 'positive',
        producerIdentitySource: 'explicitRunId',
        expectedOutcome: 'evaluate-provenance',
      },
      {
        id: 'explicit-zero',
        lookup: 'explicit-run',
        embeddedWorkflowRunId: 'zero',
        explicitRunId: 'positive',
        producerIdentitySource: 'none',
        expectedOutcome: 'ineligible',
      },
      {
        id: 'explicit-invalid',
        lookup: 'explicit-run',
        embeddedWorkflowRunId: 'invalid',
        explicitRunId: 'positive',
        producerIdentitySource: 'none',
        expectedOutcome: 'ineligible',
      },
      {
        id: 'explicit-positive',
        lookup: 'explicit-run',
        embeddedWorkflowRunId: 'positive',
        explicitRunId: 'positive',
        producerIdentitySource: 'artifact.workflow_run.id',
        expectedOutcome: 'evaluate-provenance',
      },
    ]);
  });
});
