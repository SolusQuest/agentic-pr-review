import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { verifyWideReplyDisposition } from './wide-reply-disposition.mjs';
import { verifyNativeGrammarDispositions } from './native-grammar-dispositions.mjs';
import { verifyR3ApplicationDispositions } from '../SolusProductionCutoverFixture/r3-application-dispositions.mjs';

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
  if (supplied === undefined) return verifyS5AssertionSuccessor(repository).currentNativeMethods;
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

const fixtureDirectory = 'runtime/tests/SolusEvaluationFixture';
const originalMappingSha256 = 'd4c090fdc10e94c330ebe9b47a6b4c0c1db31f47a787010fc4b9c6761d26f041';
const baselineIdentitiesSha256 = '1c7a22573326b2378de4325d266b1e0a472767f8dba182ef4fbda06cd646af73';
const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
const identity = (row) => row.source + ':' + row.method;
const exactKeys = (value, keys) => assert.deepEqual(Object.keys(value).sort(), keys.slice().sort());

// Five pinned format retirements and seven current consumer migrations are closed independently.
// Further migrations require new closed dispositions and independent current oracles.
// Source fingerprints establish a census, never SDK execution qualification.
export function verifyS5AssertionSuccessor(repository, supplied) {
  const readFixture = (name) => readFileSync(join(repository, fixtureDirectory, name));
  const originalBytes = readFixture('native-assertions.json');
  const baselineBytes = readFixture('s5-original-method-identities.json');
  assert.equal(sha256(originalBytes), originalMappingSha256);
  assert.equal(sha256(baselineBytes), baselineIdentitiesSha256);
  const original = JSON.parse(originalBytes);
  const baseline = JSON.parse(baselineBytes);
  verifyWideReplyDisposition(repository, baseline);
  assert.equal(verifyNativeGrammarDispositions(repository), 5);
  assert.equal(verifyR3ApplicationDispositions(repository), 13);
  const formatBytes = readFixture('s5-format-retirements.json');
  assert.equal(
    sha256(formatBytes),
    'c9b5aad779fd604cbeef603b1b91c99aa71e07cc7705391c14ed73e0668f66c5',
  );
  const format = JSON.parse(formatBytes);
  assert.equal(format.kind, 'apr-cr1-s5-five-native-format-dispositions-v1');
  assert.equal(format.owner_issue, 409);
  assert.equal(format.sdk_context_import_qualified, false);
  assert.equal(format.rows.length, 5);
  const formats = new Map(format.rows.map((row) => [identity(row), row]));
  assert.equal(formats.size, 5);
  const oracleText = format.oracles
    .map((oracle) => {
      const bytes = readFileSync(join(repository, oracle.path));
      assert.equal(sha256(bytes), oracle.sha256, oracle.path);
      return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    })
    .join('\n');
  for (const row of format.rows) {
    assert.equal(
      baseline.assertions.find((old) => identity(old) === identity(row))?.body_sha256,
      row.original_body_sha256,
    );
    assert.ok(row.current_oracle_labels.length > 0);
    for (const label of row.current_oracle_labels) assert.ok(oracleText.includes(label), label);
  }

  const protocolBytes = readFixture('s5-protocol-migrations.json');
  assert.equal(
    sha256(protocolBytes),
    'e0ede293e706de64ef462843ee08b7eb3b661d503f0c34e0eb17f2fa8681bf30',
  );
  const protocol = JSON.parse(protocolBytes);
  exactKeys(protocol, [
    'kind',
    'owner_issue',
    'sdk_context_import_qualified',
    'role',
    'original_methods',
    'oracles',
    'rows',
  ]);
  assert.equal(protocol.kind, 'apr-cr1-s5-current-consumer-migrations-v4');
  assert.equal(protocol.owner_issue, 409);
  assert.equal(protocol.sdk_context_import_qualified, false);
  assert.equal(protocol.role, 'current-test-consumers-only');
  assert.equal(protocol.original_methods, 7);
  const protocolSource =
    'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5CompletedReplayTests.cs';
  assert.deepEqual(protocol.rows.map(identity), [
    protocolSource + ':ChildProtocolRejectsMalformedOrIncompleteDocuments',
    protocolSource + ':PrivateProtocolBoundsUtf8AndStreams',
    protocolSource + ':ProductionExhaustionReplyAdmits512AndRejectsAdjacentForgery',
    'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5EvaluationScorerTests.cs:NoncompletedFailuresRemainDistinctWithoutGuessingUnknownCauses',
    protocolSource + ':CurrentReplayStillRejects17MemberResponsesAnd65TurnScripts',
    'runtime/tests/AgenticPrReview.Runtime.Tests/Agent/Quality/R5IncrementalReviewTests.cs:ProductionHostPublishesTheSameRevisionScenariosInTwoFreshWorlds',
    protocolSource + ':CurrentReplyAdmitsRealWideReplayAndRetainsIndependentBounds',
  ]);
  const migrations = new Map(protocol.rows.map((row) => [identity(row), row]));
  assert.equal(migrations.size, 7);
  const protocolText = protocol.oracles
    .map((oracle) => {
      exactKeys(oracle, ['path', 'sha256']);
      const bytes = readFileSync(join(repository, oracle.path));
      assert.equal(sha256(bytes), oracle.sha256, oracle.path);
      return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    })
    .join('\n');
  for (const row of protocol.rows) {
    exactKeys(row, [
      'source',
      'method',
      'original_body_sha256',
      'current_body_sha256',
      'disposition',
      'reason',
      'current_oracle_labels',
    ]);
    assert.equal(row.disposition, 'migrated');
    assert.equal(
      baseline.assertions.find((old) => identity(old) === identity(row))?.body_sha256,
      row.original_body_sha256,
    );
    assert.ok(!formats.has(identity(row)));
    for (const label of row.current_oracle_labels) assert.ok(protocolText.includes(label), label);
  }

  const successor = supplied ?? JSON.parse(readFixture('s5-assertion-successor.json'));
  exactKeys(successor, [
    'kind',
    'owner_issue',
    'original_source_commit',
    'original_mapping_sha256',
    'retirement_source_commit',
    'retirement_source_tree',
    'baseline_method_identities_sha256',
    'original_deletion_authorized',
    'execution_qualification',
    'current_sources',
    'assertions',
  ]);
  assert.equal(successor.kind, 'apr-cr1-s5-native-assertion-successor-v1');
  assert.equal(successor.owner_issue, 409);
  assert.equal(successor.original_source_commit, original.source_commit);
  assert.equal(successor.original_mapping_sha256, originalMappingSha256);
  assert.equal(successor.retirement_source_commit, 'eb2d4d0a0380cf6083fa6dc8f9ebee8c904dc4e2');
  assert.equal(successor.retirement_source_tree, '484c40c6aae3087428fb5169a18685eb744aa590');
  assert.equal(successor.baseline_method_identities_sha256, baselineIdentitiesSha256);
  assert.equal(successor.original_deletion_authorized, false);
  assert.equal(
    successor.execution_qualification,
    'Separate exact-source Ubuntu execution and actual installed .4 qualification remain mandatory; this census grants no SDK proof or blanket retirement.',
  );
  assert.equal(original.deletion_authorized, false);
  assert.equal(baseline.deletion_authorized, false);
  assert.equal(original.assertions.length, 152);
  assert.equal(baseline.assertions.length, 152);
  const originalById = new Map(original.assertions.map((row) => [identity(row), row]));
  const baselineById = new Map(baseline.assertions.map((row) => [identity(row), row]));
  assert.equal(originalById.size, 152);
  assert.equal(baselineById.size, 152);
  assert.deepEqual([...originalById.keys()].sort(), [...baselineById.keys()].sort());
  assert.ok(Array.isArray(successor.current_sources));
  assert.deepEqual(successor.current_sources.map((row) => row.path).sort(), sources.slice().sort());
  const currentFiles = new Map();
  const actual = [];
  for (const row of successor.current_sources) {
    exactKeys(row, ['path', 'sha256']);
    const bytes = readFileSync(join(repository, row.path));
    assert.equal(sha256(bytes), row.sha256, row.path);
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    currentFiles.set(row.path, bytes);
    let previous = 0;
    for (const match of text.matchAll(
      /public\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\(/gu,
    )) {
      const prefix = text.slice(previous, match.index);
      previous = match.index + match[0].length;
      if (/^\s*\[(Fact|Theory)(?:\(|\])/mu.test(prefix)) actual.push(row.path + ':' + match[1]);
    }
  }
  assert.ok(Array.isArray(successor.assertions));
  assert.equal(successor.assertions.length, 152);
  const declared = [];
  const currentDeclared = [];
  const ranges = new Map();
  for (const row of successor.assertions) {
    exactKeys(row, [
      'source',
      'method',
      'original_disposition',
      'original_replacement',
      'disposition',
      'current',
      'reason',
      ...(formats.has(identity(row)) ? ['format_disposition'] : []),
      ...(migrations.has(identity(row)) ? ['migration_disposition'] : []),
    ]);
    const key = identity(row);
    const old = originalById.get(key);
    assert.ok(old, key);
    assert.ok(baselineById.has(key), key);
    assert.equal(row.original_disposition, old.disposition);
    assert.equal(row.original_replacement, old.replacement);
    declared.push(key);
    if (formats.has(key)) {
      const disposition = formats.get(key);
      assert.deepEqual(row.format_disposition, disposition);
      assert.equal(row.disposition, disposition.disposition);
      assert.equal(row.reason, disposition.reason);
      assert.equal(row.current, null);
      assert.ok(!actual.includes(key), key);
      continue;
    }
    if (migrations.has(key)) {
      const migration = migrations.get(key);
      assert.deepEqual(row.migration_disposition, migration);
      assert.equal(row.disposition, migration.disposition);
      assert.equal(row.reason, migration.reason);
      assert.equal(row.current?.body_sha256, migration.current_body_sha256);
    } else {
      assert.equal(row.disposition, 'retained');
      assert.equal(
        row.current?.body_sha256,
        baselineById.get(key).body_sha256,
        'retained body must match immutable baseline: ' + key,
      );
      assert.equal(
        row.reason,
        'Keep the original attributed regression and its actual source body while individual state-format consumers are audited; source identity is not SDK execution qualification.',
      );
    }
    exactKeys(row.current, ['source', 'method', 'utf8_start', 'utf8_length', 'body_sha256']);
    assert.equal(row.current.source, row.source);
    assert.equal(row.current.method, row.method);
    const { utf8_start: start, utf8_length: length } = row.current;
    const bytes = currentFiles.get(row.source);
    assert.ok(Number.isSafeInteger(start) && start >= 0);
    assert.ok(Number.isSafeInteger(length) && length > 0 && start + length <= bytes.length);
    const body = bytes.subarray(start, start + length);
    assert.equal(sha256(body), row.current.body_sha256, key);
    const text = new TextDecoder('utf-8', { fatal: true }).decode(body);
    assert.match(text, /^\s*\[(Fact|Theory)(?:\(|\])/mu);
    assert.match(
      text,
      new RegExp('public\\s+(?:async\\s+)?(?:Task(?:<[^>]+>)?|void)\\s+' + row.method + '\\('),
    );
    const sourceRanges = ranges.get(row.source) ?? [];
    sourceRanges.push({ start, end: start + length });
    ranges.set(row.source, sourceRanges);
    currentDeclared.push(key);
  }
  for (const sourceRanges of ranges.values()) {
    sourceRanges.sort((a, b) => a.start - b.start);
    for (let index = 1; index < sourceRanges.length; index++) {
      assert.ok(sourceRanges[index - 1].end <= sourceRanges[index].start);
    }
  }
  assert.equal(new Set(declared).size, 152);
  assert.equal(new Set(actual).size, actual.length);
  assert.deepEqual(declared.slice().sort(), [...originalById.keys()].sort());
  assert.deepEqual(currentDeclared.slice().sort(), actual.sort());
  assert.equal(actual.length, 147);
  return { originalMethods: 152, currentNativeMethods: actual.length, authorizedDeletions: 5 };
}
