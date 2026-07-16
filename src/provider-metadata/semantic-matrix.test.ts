import { describe, expect, it } from 'vitest';
import { runSemanticFixture, SEMANTIC_FIXTURE_CASES } from './semantic-fixtures.js';

describe('ProviderRunMetadataV1 semantic fixture matrix', () => {
  for (const fixture of SEMANTIC_FIXTURE_CASES) {
    it(`covers ${fixture.name}`, () => {
      expect(runSemanticFixture(fixture.name).map((error) => error.code)).toContain(
        fixture.expected,
      );
    });
  }
});
