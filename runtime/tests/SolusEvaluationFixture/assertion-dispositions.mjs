import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const directory = 'runtime/tests/SolusEvaluationFixture/';
const originalDigest = 'd4c090fdc10e94c330ebe9b47a6b4c0c1db31f47a787010fc4b9c6761d26f041';
const baselineDigest = '1c7a22573326b2378de4325d266b1e0a472767f8dba182ef4fbda06cd646af73';
const protocolDigest = '57cc345395f40d923704fbff6194d822a889fde4299e25bc87fd023cf71a6458';
const successorDigest = '0195ffc9a35483704c5725000a3ae36174e2ea402564d0646982f2c1a8ff6236';
const sha = (bytes) => createHash('sha256').update(bytes).digest('hex');
const identity = (row) => row.source + ':' + row.method;
function read(repository, relative) {
  assert.ok(
    typeof relative === 'string' && relative.startsWith('runtime/') && !relative.includes('..'),
  );
  const path = join(repository, relative);
  const info = lstatSync(path);
  assert.ok(
    info.isFile() && !info.isSymbolicLink() && info.size > 0 && info.size <= 4194304,
    relative,
  );
  return readFileSync(path);
}
function pinned(repository, relative, digest) {
  const bytes = read(repository, relative);
  assert.equal(sha(bytes), digest, 'source identity: ' + relative);
  return bytes;
}
function attributed(bytes) {
  const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
  const names = [];
  let previous = 0;
  for (const match of text.matchAll(
    /public\s+(?:async\s+)?(?:Task(?:<[^>]+>)?|void)\s+(\w+)\(/gu,
  )) {
    if (/^\s*\[(Fact|Theory)(?:\(|\])/mu.test(text.slice(previous, match.index)))
      names.push(match[1]);
    previous = match.index + match[0].length;
  }
  return names;
}
function verifyBody(repository, row) {
  const bytes = pinned(repository, row.source, row.source_sha256);
  const start = row.utf8_start,
    length = row.utf8_length;
  assert.ok(
    Number.isSafeInteger(start) &&
      start >= 0 &&
      Number.isSafeInteger(length) &&
      length > 0 &&
      start + length <= bytes.length,
  );
  const body = bytes.subarray(start, start + length);
  assert.equal(sha(body), row.body_sha256, identity(row));
  assert.match(body.toString('utf8'), new RegExp('\\b' + row.method + '\\s*\\('));
  return body.toString('utf8');
}

// The V1 artifact remains immutable historical evidence. Its supplied-object
// route validates that artifact only; it never labels deleted native tests live.
export function verifyAssertionDispositions(repository, supplied) {
  if (supplied === undefined) return verifyS5AssertionSuccessor(repository).currentNativeMethods;
  const original = JSON.parse(
    pinned(repository, directory + 'native-assertions.json', originalDigest),
  );
  assert.deepEqual(supplied, original);
  return original.assertions.length;
}

// One final closure replaces the intermediate per-batch source checkers. Every
// original row, assertion expression and variant remains in its frozen record.
// Source ownership is not execution qualification of an installed SDK archive.
export function verifyS5AssertionSuccessor(repository, supplied) {
  const original = JSON.parse(
    pinned(repository, directory + 'native-assertions.json', originalDigest),
  );
  const baseline = JSON.parse(
    pinned(repository, directory + 's5-original-method-identities.json', baselineDigest),
  );
  const protocol = JSON.parse(
    pinned(repository, directory + 's5-protocol-migrations.json', protocolDigest),
  );
  const expected = JSON.parse(
    pinned(repository, directory + 's5-assertion-successor.json', successorDigest),
  );
  const successor = supplied ?? expected;
  assert.deepEqual(successor, expected, 'closed S5 successor dispositions');
  assert.equal(protocol.kind, 'apr-cr1-s5-current-consumer-migrations-v7');
  assert.equal(protocol.owner_issue, 409);
  assert.equal(protocol.sdk_context_import_qualified, false);
  assert.equal(original.deletion_authorized, false);
  assert.equal(baseline.deletion_authorized, false);
  assert.equal(successor.original_deletion_authorized, false);
  assert.equal(successor.original_mapping_sha256, originalDigest);
  assert.equal(successor.baseline_method_identities_sha256, baselineDigest);
  for (const row of protocol.immutable_records) pinned(repository, row.path, row.sha256);
  for (const row of protocol.oracles) pinned(repository, row.path, row.sha256);
  for (const row of protocol.removed_sources)
    assert.equal(existsSync(join(repository, row)), false, 'retired source: ' + row);
  for (const row of [...protocol.preserved_owners, ...protocol.generic_migration_owners]) {
    const body = verifyBody(repository, row);
    for (const expression of row.assertions ?? [])
      assert.ok(body.includes(expression), identity(row));
    for (const attribute of row.attributes ?? [])
      assert.ok(body.includes('[' + attribute + ']'), identity(row));
  }
  assert.equal(
    protocol.supplementary_retirement_input_commit,
    '9527171f42dbee9503a3fd465cf28fc6926469f8',
  );
  for (const row of protocol.native_consumer_dispositions) {
    assert.ok(row.variant_attributes.length > 0 && row.original_assertions.length > 0);
    assert.ok(
      [
        'obsolete-format-retired',
        'migrated-generic-predicate',
        'mixed-format-and-generic',
      ].includes(row.disposition),
    );
    if (row.disposition === 'obsolete-format-retired') assert.equal(row.current_owner, undefined);
    else {
      assert.ok(
        protocol.generic_migration_owners.some(
          (owner) =>
            identity(owner) === identity(row.current_owner) &&
            owner.body_sha256 === row.current_owner.body_sha256,
        ),
      );
      verifyBody(repository, row.current_owner);
    }
  }
  const originalById = new Map(original.assertions.map((row) => [identity(row), row]));
  const baselineById = new Map(baseline.assertions.map((row) => [identity(row), row]));
  assert.equal(originalById.size, 152);
  assert.equal(baselineById.size, 152);
  assert.equal(successor.assertions.length, 152);
  const formats = new Map(
    JSON.parse(read(repository, directory + 's5-format-retirements.json')).rows.map((row) => [
      identity(row),
      row,
    ]),
  );
  const migrations = new Map(protocol.rows.map((row) => [identity(row), row]));
  const scenarios = new Map(protocol.retired_consumers.map((row) => [identity(row), row]));
  assert.equal(formats.size, 5);
  assert.equal(migrations.size, 9);
  assert.equal(scenarios.size, 31);
  const actual = [],
    declared = [],
    all = [];
  const files = new Map();
  for (const row of successor.current_sources) {
    assert.ok(!files.has(row.path), row.path);
    const bytes = pinned(repository, row.path, row.sha256);
    files.set(row.path, bytes);
    actual.push(...attributed(bytes).map((name) => row.path + ':' + name));
  }
  const ranges = new Map();
  for (const row of successor.assertions) {
    const key = identity(row),
      old = originalById.get(key),
      origin = baselineById.get(key);
    assert.ok(old && origin, key);
    assert.equal(row.original_disposition, old.disposition);
    assert.equal(row.original_replacement, old.replacement);
    all.push(key);
    if (formats.has(key) || scenarios.has(key)) {
      const disposition = formats.get(key) ?? scenarios.get(key);
      assert.deepEqual(row.format_disposition ?? row.migration_disposition, disposition);
      assert.equal(disposition.original_body_sha256, origin.body_sha256);
      assert.equal(row.disposition, disposition.disposition);
      assert.equal(row.reason, disposition.reason);
      assert.equal(row.current, null);
      assert.ok(!actual.includes(key), key);
      continue;
    }
    if (migrations.has(key)) {
      const migration = migrations.get(key);
      assert.deepEqual(row.migration_disposition, migration);
      assert.equal(migration.original_body_sha256, origin.body_sha256);
      assert.equal(row.disposition, 'migrated');
      assert.equal(row.current.body_sha256, migration.current_body_sha256);
    } else {
      assert.equal(row.disposition, 'retained');
      assert.equal(row.current.body_sha256, origin.body_sha256, key);
    }
    assert.equal(row.current.source, row.source);
    assert.equal(row.current.method, row.method);
    assert.ok(files.has(row.source), key);
    const text = verifyBody(repository, {
      ...row.current,
      source_sha256: sha(files.get(row.source)),
    });
    assert.ok(attributed(Buffer.from(text)).includes(row.method), key);
    const sourceRanges = ranges.get(row.source) ?? [];
    sourceRanges.push([row.current.utf8_start, row.current.utf8_start + row.current.utf8_length]);
    ranges.set(row.source, sourceRanges);
    declared.push(key);
  }
  for (const span of ranges.values()) {
    span.sort((a, b) => a[0] - b[0]);
    for (let index = 1; index < span.length; index++)
      assert.ok(span[index - 1][1] <= span[index][0]);
  }
  assert.equal(new Set(all).size, 152);
  assert.deepEqual(all.sort(), [...originalById.keys()].sort());
  assert.equal(new Set(actual).size, actual.length);
  assert.deepEqual(declared.sort(), actual.sort());
  assert.equal(actual.length, protocol.current_native_methods);
  return {
    originalMethods: 152,
    currentNativeMethods: actual.length,
    authorizedDeletions: formats.size,
    migratedScenarios: scenarios.size,
  };
}
