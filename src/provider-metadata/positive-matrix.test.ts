import { describe, expect, it } from 'vitest';
import { POSITIVE_FIXTURE_CASES, runPositiveFixture } from './positive-fixtures.js';

describe('ProviderRunMetadataV1 positive parser fixture matrix', () => {
  for (const name of POSITIVE_FIXTURE_CASES) {
    it(`accepts ${name}`, () => expect(runPositiveFixture(name)).toBe(true));
  }
});
