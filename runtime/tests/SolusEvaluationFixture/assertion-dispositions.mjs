import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';

const sources = [
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5EvaluationScorerTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5QualityCorpusTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5CompletedReplayTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5IncrementalReviewTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Session/R5SessionGrowthTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5ReplayAdmissionTests.cs',
  'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5VerifierCoverageTests.cs',
];
export function verifyAssertionDispositions(repository, supplied) {
  const mapping =
    supplied ??
    JSON.parse(readFileSync(join(import.meta.dirname, 'native-assertions.json'), 'utf8'));
  assert.equal(mapping.source_commit, '64084d97f6d4c65cfe41719fe74d72f5f9524f7e');
  assert.equal(mapping.deletion_authorized, false);
  assert.ok(Array.isArray(mapping.assertions));
  assert.deepEqual(
    [...new Set(mapping.assertions.map((row) => row.source))].sort(),
    sources.slice().sort(),
  );
  const actual = [];
  for (const source of sources) {
    const text = readFileSync(join(repository, source), 'utf8');
    let previous = 0;
    for (const match of text.matchAll(
      /public\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\(/gu,
    )) {
      const prefix = text.slice(previous, match.index);
      previous = match.index + match[0].length;
      if (/^\s*\[(Fact|Theory)(?:\(|\])/mu.test(prefix)) actual.push(source + ':' + match[1]);
    }
  }
  const declared = [];
  for (const row of mapping.assertions) {
    assert.deepEqual(
      Object.keys(row).sort(),
      ['source', 'method', 'disposition', 'replacement'].sort(),
    );
    assert.ok(
      ['replacement', 'retained-historical', 'justified-retirement'].includes(row.disposition),
    );
    assert.ok(
      [
        'shared-scorer',
        'managed-quality',
        'managed-admission',
        'authenticated-replay',
        'bounded-growth',
        'managed-report-verifier',
        'historical-only',
        'cross-owner-live',
      ].includes(row.replacement),
    );
    // V1 deletes nothing. Retirement requires its later owner's explicit record.
    assert.notEqual(row.disposition, 'justified-retirement');
    declared.push(row.source + ':' + row.method);
  }
  assert.equal(new Set(declared).size, declared.length);
  assert.deepEqual(declared.slice().sort(), actual.sort());
  return declared.length;
}
