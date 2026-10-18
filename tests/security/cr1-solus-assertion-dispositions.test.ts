import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, test } from 'vitest';
import { verifyAssertionDispositions } from '../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs';

const load = () =>
  JSON.parse(
    fs.readFileSync(
      path.join(process.cwd(), 'runtime/tests/SolusEvaluationFixture/native-assertions.json'),
      'utf8',
    ),
  );
describe('V1 native assertion dispositions', () => {
  test('maps every attributed method in the seven affected source families', () => {
    expect(verifyAssertionDispositions(process.cwd())).toBe(152);
  });
  test.each(['missing', 'duplicate', 'unknown-source', 'retirement', 'replacement', 'authority'])(
    'rejects %s drift',
    (mutation) => {
      const mapping = load();
      if (mutation === 'missing') mapping.assertions.pop();
      if (mutation === 'duplicate') mapping.assertions.push(mapping.assertions[0]);
      if (mutation === 'unknown-source')
        mapping.assertions[0].source = 'runtime/tests/unclassified.cs';
      if (mutation === 'retirement') mapping.assertions[0].disposition = 'justified-retirement';
      if (mutation === 'replacement') mapping.assertions[0].replacement = 'unverified';
      if (mutation === 'authority') mapping.deletion_authorized = true;
      expect(() => verifyAssertionDispositions(process.cwd(), mapping)).toThrow();
    },
  );
});
