import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { existsSync, lstatSync, readFileSync } from 'node:fs';
import { join } from 'node:path';

const directory = 'runtime/tests/SolusEvaluationFixture/';
const originalDigest = 'd4c090fdc10e94c330ebe9b47a6b4c0c1db31f47a787010fc4b9c6761d26f041';
const baselineDigest = '1c7a22573326b2378de4325d266b1e0a472767f8dba182ef4fbda06cd646af73';
const protocolDigest = '57cc345395f40d923704fbff6194d822a889fde4299e25bc87fd023cf71a6458';
const successorDigest = '0195ffc9a35483704c5725000a3ae36174e2ea402564d0646982f2c1a8ff6236';
const r7Digest = '01d6258cfddaefc935f3a273a299dc8ba29064a4c70f850d17f27c3d764e2564';
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

// R7 extends this primary closure. Frozen S5 records and relationships retain
// their original meaning; every current override names an exact old body.
export function verifyR7AssertionSuccessor(repository) {
  const record = JSON.parse(
    pinned(repository, directory + 'r7-assertion-successor.json', r7Digest),
  );
  assert.equal(record.kind, 'apr-cr1-r7-assertion-successor-v1');
  assert.equal(record.owner_issue, 403);
  assert.equal(record.original_source_commit, '239efde4f4065b5ac414610e4a0413d53a54f6f7');
  assert.equal(record.sdk_context_import_qualified, false);
  assert.equal(record.source_identity_is_execution_qualification, false);
  assert.deepEqual(record.historical_counts, {
    original_native_mapping: 152,
    format_retirements: 5,
    s5_retired_scenarios: 31,
    s5_current_methods: 116,
  });
  for (const row of record.historical_records) pinned(repository, row.path, row.sha256);
  const sources = new Map();
  for (const row of record.current_sources) {
    assert.ok(!sources.has(row.path), row.path);
    sources.set(row.path, pinned(repository, row.path, row.sha256));
  }
  const retired = new Set();
  for (const row of record.retired_sources) {
    assert.ok(!retired.has(row.path) && !sources.has(row.path), row.path);
    assert.match(row.original_sha256, /^[0-9a-f]{64}$/u);
    assert.ok(row.reason.length > 80);
    if (row.disposition === 'relocated-live-value') {
      assert.ok(sources.has(row.relocated_to), row.path);
      assert.equal(sha(sources.get(row.relocated_to)), row.original_sha256, row.path);
    } else {
      assert.equal(row.disposition, 'native-source-retired', row.path);
      assert.equal(row.relocated_to, undefined, row.path);
    }
    assert.equal(existsSync(join(repository, row.path)), false, 'R7 retired source: ' + row.path);
    retired.add(row.path);
  }
  const owners = new Map();
  for (const row of [...record.current_owners, ...record.code_owners]) {
    const key = identity(row);
    assert.ok(!owners.has(key) && sources.has(row.source), key);
    const body = verifyBody(repository, row);
    for (const attribute of row.attributes ?? [])
      assert.ok(body.includes('[' + attribute + ']'), key);
    owners.set(key, { row, body });
  }
  const origins = new Map();
  for (const row of record.original_methods) {
    const key = identity(row.original);
    assert.ok(!origins.has(key), key);
    assert.match(row.original.body_sha256, /^[0-9a-f]{64}$/u);
    assert.ok(row.disposition.reason.length > 80, key);
    assert.equal(row.disposition.equivalence_claimed, false);
    const current = row.current === null ? null : owners.get(row.current);
    assert.ok(row.current === null || current, key);
    if (row.disposition.kind === 'unchanged-body') {
      assert.ok(current, key);
      assert.equal(current.row.body_sha256, row.original.body_sha256, key);
      assert.deepEqual(current.row.attributes, row.original.attributes, key);
    }
    assert.deepEqual(
      row.assertion_dispositions.map((item) => item.original_expression),
      row.original.assertions ?? [],
      key,
    );
    for (const item of row.assertion_dispositions) {
      assert.ok(typeof item.disposition === 'string' && item.disposition.length > 10, key);
      if (item.current_expression !== undefined) {
        assert.ok(current && current.body.includes(item.current_expression), key);
      } else {
        assert.ok(typeof item.reason === 'string' && item.reason.length > 20, key);
      }
    }
    for (const related of row.related_current_owners) assert.ok(owners.has(related), related);
    origins.set(key, row);
  }
  const rollforwards = new Map();
  for (const row of record.source_rollforwards) {
    const key = row.path + ':' + row.original_sha256;
    assert.ok(!rollforwards.has(key), key);
    if (row.current_sha256 === null) assert.ok(retired.has(row.path), row.path);
    else {
      assert.ok(sources.has(row.path), row.path);
      assert.equal(sha(sources.get(row.path)), row.current_sha256, row.path);
    }
    rollforwards.set(key, row);
  }
  const native = new Set();
  for (const row of record.current_native_sources) {
    const bytes = sources.get(row.path);
    assert.ok(bytes, row.path);
    assert.equal(sha(bytes), row.sha256, row.path);
    assert.deepEqual(attributed(bytes), row.methods, row.path);
    for (const method of row.methods) {
      const key = row.path + ':' + method;
      assert.ok(!native.has(key) && owners.has(key), key);
      native.add(key);
    }
  }
  assert.equal(native.size, record.current_native_methods);
  const scenarios = record.aot_scenario_dispositions;
  const historicalCases = read(
    repository,
    'runtime/tests/fixtures/agent/agent-loop/generic-negative-cases.txt',
  )
    .toString('utf8')
    .trim()
    .split('\n');
  assert.deepEqual(
    scenarios.map((row) => row.scenario),
    historicalCases,
  );
  for (const row of scenarios) {
    assert.equal(row.disposition, 'retired-native-cli-route');
    assert.equal(row.replacement_equivalence_claimed, false);
    assert.ok(row.reason.length > 80 && row.current_related_owners.length > 0);
  }
  return {
    record,
    sources,
    native,
    source(row) {
      const override = rollforwards.get(row.path + ':' + row.sha256);
      if (!override) return pinned(repository, row.path, row.sha256);
      return override.current_sha256 === null ? null : sources.get(row.path);
    },
    owner(row) {
      const origin = origins.get(identity(row));
      if (!origin) return { row, body: verifyBody(repository, row), changed: false };
      assert.equal(row.body_sha256, origin.original.body_sha256, identity(row));
      assert.equal(row.source_sha256, origin.original.source_sha256, identity(row));
      if (origin.current === null) return null;
      const current = owners.get(origin.current);
      return { ...current, changed: current.row.body_sha256 !== row.body_sha256 };
    },
  };
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
  const r7 = verifyR7AssertionSuccessor(repository);
  for (const row of protocol.oracles) r7.source(row);
  for (const row of protocol.removed_sources)
    assert.equal(existsSync(join(repository, row)), false, 'retired source: ' + row);
  for (const row of [...protocol.preserved_owners, ...protocol.generic_migration_owners]) {
    const current = r7.owner(row);
    if (current && !current.changed) {
      for (const expression of row.assertions ?? [])
        assert.ok(current.body.includes(expression), identity(row));
      for (const attribute of row.attributes ?? [])
        assert.ok(current.body.includes('[' + attribute + ']'), identity(row));
    }
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
      r7.owner(row.current_owner);
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
    const bytes = r7.source(row);
    assert.ok(bytes, row.path);
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
    const current = r7.owner({
      ...row.current,
      source_sha256: successor.current_sources.find((source) => source.path === row.source).sha256,
    });
    if (current === null) continue;
    assert.ok(attributed(Buffer.from(current.body)).includes(current.row.method), key);
    const sourceRanges = ranges.get(current.row.source) ?? [];
    sourceRanges.push([current.row.utf8_start, current.row.utf8_start + current.row.utf8_length]);
    ranges.set(current.row.source, sourceRanges);
    declared.push(identity(current.row));
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
  assert.equal(actual.length, r7.record.current_native_methods);
  assert.deepEqual(actual, [...r7.native].sort());
  return {
    originalMethods: 152,
    currentNativeMethods: actual.length,
    authorizedDeletions: formats.size,
    migratedScenarios: scenarios.size,
  };
}
