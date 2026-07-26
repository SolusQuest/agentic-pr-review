import { describe, expect, it } from 'vitest';
import { artifactProvenanceVectors } from './artifact-provenance-vectors.js';

describe('retired artifact provenance vectors', () => {
  it('preserves the complete stable provider-independent vector set', () => {
    const expectedIds = Array.from(
      { length: 32 },
      (_, index) => `APV-${String(index + 1).padStart(3, '0')}`,
    );
    const actualIds = artifactProvenanceVectors.map(({ id }) => id);

    expect(actualIds).toEqual(expectedIds);
    expect(new Set(actualIds).size).toBe(actualIds.length);
  });

  it('owns every port and obsolete disposition through an explicit lifecycle gate', () => {
    for (const vector of artifactProvenanceVectors) {
      expect(vector.inputMetadata.length).toBeGreaterThan(0);
      expect(vector.selectionContext).not.toBe('');
      expect(vector.expectedOutcome).not.toBe('');
      expect(vector.securityInvariant).not.toBe('');

      if (vector.disposition === 'port') {
        expect(vector.futureConsumer).toBe('R4 artifact bridge');
        expect(vector.deletionGate).toContain('R4 artifact-bridge conformance');
        expect(vector.deletionRationale).toBeUndefined();
      } else {
        expect(vector.deletionRationale).not.toBe('');
        expect(vector.futureConsumer).toBeUndefined();
        expect(vector.deletionGate).toBeUndefined();
      }
    }
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
