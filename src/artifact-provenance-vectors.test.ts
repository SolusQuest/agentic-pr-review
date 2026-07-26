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
});
