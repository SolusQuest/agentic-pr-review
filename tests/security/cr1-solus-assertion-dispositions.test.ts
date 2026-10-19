import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { createHash } from 'node:crypto';
import { describe, expect, test } from 'vitest';
import {
  verifyAssertionDispositions,
  verifyS5AssertionSuccessor,
} from '../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs';

const load = () =>
  JSON.parse(
    fs.readFileSync(
      path.join(process.cwd(), 'runtime/tests/SolusEvaluationFixture/native-assertions.json'),
      'utf8',
    ),
  );
describe('V1 native assertion dispositions', () => {
  test('preserves the original 152-row mapping while reporting the exact current census', () => {
    expect(verifyAssertionDispositions(process.cwd())).toBe(147);
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

const fixtureDirectory = 'runtime/tests/SolusEvaluationFixture';
const loadSuccessor = () =>
  JSON.parse(
    fs.readFileSync(
      path.join(process.cwd(), fixtureDirectory, 's5-assertion-successor.json'),
      'utf8',
    ),
  );

describe('S5 source-bound native assertion census', () => {
  test('binds all 152 originals, 147 current bodies and exactly five individual dispositions', () => {
    expect(verifyS5AssertionSuccessor(process.cwd())).toEqual({
      originalMethods: 152,
      currentNativeMethods: 147,
      authorizedDeletions: 5,
    });
  });
  test.each([
    'owner',
    'kind',
    'source-commit',
    'source-tree',
    'original-commit',
    'mapping-digest',
    'baseline-digest',
    'deletion',
    'sdk-overclaim',
    'missing-row',
    'duplicate-row',
    'unknown-original',
    'old-disposition',
    'old-replacement',
    'blanket-retirement',
    'unverified-migration',
    'reason-only-retirement',
    'redirected-oracle',
    'body-digest',
    'body-span',
    'negative-span',
    'fractional-span',
    'oversized-span',
    'duplicate-source',
    'missing-source',
    'unknown-source',
    'source-digest',
    'extra-row-field',
    'extra-current-field',
    'extra-authority',
    'swapped-bodies',
  ])('rejects %s drift independently of counts', (mutation) => {
    const successor = loadSuccessor();
    const row = successor.assertions[0];
    if (mutation === 'owner') successor.owner_issue = 403;
    if (mutation === 'kind') successor.kind = 'old';
    if (mutation === 'source-commit') successor.retirement_source_commit = '0'.repeat(40);
    if (mutation === 'source-tree') successor.retirement_source_tree = '0'.repeat(40);
    if (mutation === 'original-commit') successor.original_source_commit = '0'.repeat(40);
    if (mutation === 'mapping-digest') successor.original_mapping_sha256 = '0'.repeat(64);
    if (mutation === 'baseline-digest')
      successor.baseline_method_identities_sha256 = '0'.repeat(64);
    if (mutation === 'deletion') successor.original_deletion_authorized = true;
    if (mutation === 'sdk-overclaim') successor.execution_qualification = 'SDK qualified';
    if (mutation === 'missing-row') successor.assertions.pop();
    if (mutation === 'duplicate-row') successor.assertions[1] = structuredClone(row);
    if (mutation === 'unknown-original') row.method = 'Unclassified';
    if (mutation === 'old-disposition') row.original_disposition = 'justified-retirement';
    if (mutation === 'old-replacement') row.original_replacement = 'unverified';
    if (mutation === 'blanket-retirement') row.disposition = 'obsolete-format';
    if (mutation === 'unverified-migration') row.disposition = 'migrated';
    if (mutation === 'reason-only-retirement') row.reason = 'Old tests no longer needed';
    if (mutation === 'redirected-oracle') row.current.method = successor.assertions[1].method;
    if (mutation === 'body-digest') row.current.body_sha256 = '0'.repeat(64);
    if (mutation === 'body-span') row.current.utf8_start++;
    if (mutation === 'negative-span') row.current.utf8_start = -1;
    if (mutation === 'fractional-span') row.current.utf8_length += 0.5;
    if (mutation === 'oversized-span') row.current.utf8_length = Number.MAX_SAFE_INTEGER;
    if (mutation === 'duplicate-source')
      successor.current_sources[1] = successor.current_sources[0];
    if (mutation === 'missing-source') successor.current_sources.pop();
    if (mutation === 'unknown-source') successor.current_sources[0].path = '../unclassified.cs';
    if (mutation === 'source-digest') successor.current_sources[0].sha256 = '0'.repeat(64);
    if (mutation === 'extra-row-field') row.authorized = true;
    if (mutation === 'extra-current-field') row.current.sdk_import_qualified = true;
    if (mutation === 'extra-authority') successor.deletion_authorized = true;
    if (mutation === 'swapped-bodies') {
      const other = successor.assertions[1].current;
      row.current.utf8_start = other.utf8_start;
      row.current.utf8_length = other.utf8_length;
      row.current.body_sha256 = other.body_sha256;
    }
    expect(() => verifyS5AssertionSuccessor(process.cwd(), successor)).toThrow();
  });
  test.each([
    'body-reintroduced',
    'original-body',
    'sdk-label',
    'reason',
    'disposition',
    'unmapped-row',
    'oracle-name',
    'extra-authority',
    'retained-relabel',
  ])('rejects individual retirement %s drift', (mutation) => {
    const successor = loadSuccessor();
    const row = successor.assertions.find((item: { current: unknown }) => item.current === null);
    if (mutation === 'body-reintroduced')
      row.current = structuredClone(successor.assertions[0].current);
    if (mutation === 'original-body') row.format_disposition.original_body_sha256 = '0'.repeat(64);
    if (mutation === 'sdk-label') row.format_disposition.sdk_context_import_qualified = true;
    if (mutation === 'reason') row.reason = 'format no longer needed';
    if (mutation === 'disposition') row.disposition = 'retained';
    if (mutation === 'unmapped-row') delete row.format_disposition;
    if (mutation === 'oracle-name') row.format_disposition.current_oracle_labels = ['unexecuted'];
    if (mutation === 'extra-authority') row.format_disposition.allow_fallback = true;
    if (mutation === 'retained-relabel')
      successor.assertions[0] = {
        ...successor.assertions[0],
        disposition: row.disposition,
        current: null,
        reason: row.reason,
        format_disposition: row.format_disposition,
      };
    expect(() => verifyS5AssertionSuccessor(process.cwd(), successor)).toThrow();
  });
  test.each([
    'original-bytes',
    'baseline-bytes',
    'body-change',
    'extra-method',
    'missing-successor',
    'missing-format-policy',
    'format-policy-change',
    'oracle-source-change',
  ])('rejects actual %s drift without a permissive fallback', (mutation) => {
    const temporary = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-census-'));
    try {
      const successor = loadSuccessor();
      const copy = (relative: string) => {
        const target = path.join(temporary, relative);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(path.join(process.cwd(), relative), target);
      };
      for (const name of [
        'native-assertions.json',
        's5-original-method-identities.json',
        's5-assertion-successor.json',
        's5-format-retirements.json',
      ])
        copy(path.join(fixtureDirectory, name));
      for (const source of successor.current_sources) copy(source.path);
      const format = JSON.parse(
        fs.readFileSync(
          path.join(process.cwd(), fixtureDirectory, 's5-format-retirements.json'),
          'utf8',
        ),
      );
      for (const oracle of format.oracles) copy(oracle.path);
      // Establish a valid complete temporary source closure before each mutation.
      expect(verifyS5AssertionSuccessor(temporary).currentNativeMethods).toBe(147);
      if (mutation === 'missing-format-policy')
        fs.unlinkSync(path.join(temporary, fixtureDirectory, 's5-format-retirements.json'));
      if (mutation === 'format-policy-change')
        fs.appendFileSync(
          path.join(temporary, fixtureDirectory, 's5-format-retirements.json'),
          '\n',
        );
      if (mutation === 'oracle-source-change')
        fs.appendFileSync(path.join(temporary, format.oracles[0].path), '\n');
      if (mutation === 'original-bytes')
        fs.appendFileSync(path.join(temporary, fixtureDirectory, 'native-assertions.json'), '\n');
      if (mutation === 'baseline-bytes')
        fs.appendFileSync(
          path.join(temporary, fixtureDirectory, 's5-original-method-identities.json'),
          '\n',
        );
      if (mutation === 'missing-successor')
        fs.unlinkSync(path.join(temporary, fixtureDirectory, 's5-assertion-successor.json'));
      if (mutation === 'body-change' || mutation === 'extra-method') {
        const row = successor.assertions[0];
        const target = path.join(temporary, row.source);
        let bytes = fs.readFileSync(target);
        if (mutation === 'body-change') {
          // Keep UTF8 and method identity valid, but change a real method body byte.
          const body = bytes.subarray(
            row.current.utf8_start,
            row.current.utf8_start + row.current.utf8_length,
          );
          const offset = body.indexOf(Buffer.from('Assert.'));
          expect(offset).toBeGreaterThanOrEqual(0);
          bytes[row.current.utf8_start + offset] = 'X'.charCodeAt(0);
        } else {
          bytes = Buffer.concat([
            bytes,
            Buffer.from('\n[Fact]\npublic void UnclassifiedAddedMethod() {}\n'),
          ]);
        }
        fs.writeFileSync(target, bytes);
        // Updating only the file fingerprint cannot hide changed bodies or an added method.
        successor.current_sources.find(
          (source: { path: string }) => source.path === row.source,
        ).sha256 = createHash('sha256').update(bytes).digest('hex');
        fs.writeFileSync(
          path.join(temporary, fixtureDirectory, 's5-assertion-successor.json'),
          JSON.stringify(successor),
        );
      }
      expect(() => verifyS5AssertionSuccessor(temporary)).toThrow();
    } finally {
      fs.rmSync(temporary, { recursive: true, force: true });
    }
  });
});
