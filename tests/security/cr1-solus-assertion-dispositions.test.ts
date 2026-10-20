import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { createHash } from 'node:crypto';
import { describe, expect, test } from 'vitest';
import {
  verifyAssertionDispositions,
  verifyS5AssertionSuccessor,
} from '../../runtime/tests/SolusEvaluationFixture/assertion-dispositions.mjs';
import {
  verifyWideReplyContract,
  verifyWideReplySources,
} from '../../runtime/tests/SolusEvaluationFixture/wide-reply-disposition.mjs';
import {
  verifyNativeGrammarContract,
  verifyNativeGrammarSources,
  verifyNativeGrammarDispositions,
} from '../../runtime/tests/SolusEvaluationFixture/native-grammar-dispositions.mjs';
import {
  verifyCommitConsumerContract,
  verifyCommitConsumerSources,
  verifyCommitConsumerDispositions,
} from '../../runtime/tests/SolusProductionCutoverFixture/commit-consumer-dispositions.mjs';
import {
  verifyR3ApplicationContract,
  verifyR3ApplicationSources,
  verifyR3ApplicationDispositions,
} from '../../runtime/tests/SolusProductionCutoverFixture/r3-application-dispositions.mjs';
const r3PolicyPath =
  'runtime/tests/SolusProductionCutoverFixture/s5-r3-application-dispositions.json';
type R3Oracle = {
  id: string;
  role: string;
  source: string;
  method: string;
  source_sha256: string;
  utf8_start: number;
  utf8_length: number;
  body_sha256: string;
};
type R3Policy = {
  owner_issue: number;
  sdk_context_import_qualified: boolean;
  removed_source: {
    path: string;
    source_sha256: string;
    attributed_methods: number;
    expanded_native_cases: number;
  };
  current_source: { path: string; sha256: string; attributed_methods: number };
  shared_members: {
    name: string;
    current_utf8_start: number;
    utf8_length: number;
    body_sha256: string;
  }[];
  rows: {
    original: { source: string; method: string; body_sha256: string; attributes: string[] };
    retirement_input: { body_sha256: string };
    original_triggers: string[];
    disposition: string;
    precise_dispositions: string[];
    primary_oracle_ids: string[];
    supporting_source_ids: string[];
  }[];
  oracles: R3Oracle[];
  cli_source: {
    path: string;
    current_sha256: string;
    removed_methods: number;
    removed_expanded_cases: number;
  };
  cli_consumers: {
    original: { source: string; method: string; body_sha256: string; attributes: string[] };
    retirement_input: { body_sha256: string };
    expanded_native_cases: number;
    original_triggers: string[];
    disposition: string;
    precise_dispositions: string[];
    primary_oracle_ids: string[];
  }[];
  trusted_profile_consumers: {
    original: { source: string; method: string; body_sha256: string };
    current_method: string;
    precise_dispositions: string[];
  }[];
};
const loadR3 = (): R3Policy => JSON.parse(fs.readFileSync(r3PolicyPath, 'utf8'));

const grammarPolicyPath =
  'runtime/tests/SolusEvaluationFixture/s5-native-grammar-dispositions.json';
type GrammarIdentity = {
  source: string;
  method: string;
  body_sha256: string;
  attributes: string[];
};
type GrammarPolicy = {
  owner_issue: number;
  sdk_context_import_qualified: boolean;
  baseline_source_commit: string;
  original_v1_membership: string;
  current_source: {
    path: string;
    sha256: string;
    attributed_methods_before: number;
    attributed_methods_after: number;
  };
  rows: {
    original: GrammarIdentity;
    retirement_input: GrammarIdentity;
    input_body_difference: string;
    triggers: string[];
    independent_current_oracle_ids: string[];
    disposition: string;
    equivalence_limit: string;
  }[];
  oracles: (GrammarIdentity & {
    id: string;
    role: string;
    source_sha256: string;
    utf8_start: number;
    utf8_length: number;
  })[];
};
const loadGrammar = (): GrammarPolicy => JSON.parse(fs.readFileSync(grammarPolicyPath, 'utf8'));

const commitPolicyPath =
  'runtime/tests/SolusProductionCutoverFixture/s5-native-commit-consumer-dispositions.json';
const loadCommit = () =>
  JSON.parse(fs.readFileSync(path.join(process.cwd(), commitPolicyPath), 'utf8'));
describe('S5 three native commit consumers', () => {
  test('binds all original triggers to actual current bodies', () => {
    expect(verifyCommitConsumerDispositions(process.cwd())).toBe(3);
  });
  test.each([0, 1, 2])('rejects missing trigger, wrong owner or fake retained row %s', (index) => {
    for (const mutation of [
      'missing-trigger',
      'missing-oracle',
      'wrong-owner',
      'retained',
      'original-body',
      'missing-variant',
      'current-body',
    ]) {
      const policy = loadCommit();
      const row = policy.rows[index];
      if (mutation === 'missing-trigger') row.triggers.pop();
      if (mutation === 'missing-oracle') row.oracle_ids.pop();
      if (mutation === 'wrong-owner') row.oracle_ids[0] = 'same-name-helper';
      if (mutation === 'retained') row.disposition = 'retained';
      if (mutation === 'original-body') row.original_body_sha256 = '0'.repeat(64);
      if (mutation === 'missing-variant') row.variants = ['unclassified'];
      if (mutation === 'current-body')
        row.current = { source: row.source, method: row.method, body_sha256: '0'.repeat(64) };
      expect(() => verifyCommitConsumerContract(policy)).toThrow(
        'exact original triggers and actual oracle owners/bodies',
      );
    }
  });
  test.each([0, 1, 2, 3, 4])('rejects re-signed or misattributed body %s', (index) => {
    for (const mutation of ['wrong-file', 'wrong-method', 'no-body', 'resigned-body']) {
      const policy = loadCommit();
      const oracle = policy.oracles[index];
      if (mutation === 'wrong-file') oracle.source = 'same-name-fake.cs';
      if (mutation === 'wrong-method') oracle.method = 'UnexecutedHelper';
      if (mutation === 'no-body') oracle.utf8_length = 0;
      if (mutation === 'resigned-body')
        oracle.body_sha256 = oracle.source_sha256 = createHash('sha256')
          .update('fake body')
          .digest('hex');
      expect(() => verifyCommitConsumerContract(policy)).toThrow();
    }
  });
  test.each([
    'missing',
    'source-change',
    'body-change',
    'source-and-manifest-resigned',
    'drop-two-generations',
    'drop-privacy',
    'wrapper-noop',
  ])('rejects actual %s', (mutation) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-commit-'));
    try {
      const policy = loadCommit();
      for (const oracle of policy.oracles) {
        const target = path.join(root, oracle.source);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(path.join(process.cwd(), oracle.source), target);
      }
      verifyCommitConsumerSources(root, policy);
      const oracle =
        mutation === 'drop-two-generations'
          ? policy.oracles[0]
          : mutation === 'wrapper-noop'
            ? policy.rows[2].current
            : policy.oracles[1];
      const target = path.join(root, oracle.source);
      if (mutation === 'missing') fs.unlinkSync(target);
      else {
        let bytes = fs.readFileSync(target);
        if (mutation === 'source-change') bytes = Buffer.concat([bytes, Buffer.from('\n')]);
        else if (['drop-two-generations', 'drop-privacy', 'wrapper-noop'].includes(mutation)) {
          const before = bytes
            .subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length)
            .toString('utf8');
          const after =
            mutation === 'drop-two-generations'
              ? before.replace('Generation: 1', 'Generation: 0')
              : mutation === 'drop-privacy'
                ? before.replace('bytes.AsSpan().IndexOf(key) < 0', 'true')
                : before.replace(
                    'await CurrentPreparedFactoryFailureZerosCallerPlaintext();',
                    'await Task.CompletedTask;',
                  );
          expect(after).not.toBe(before);
          const body = Buffer.from(after);
          bytes = Buffer.concat([
            bytes.subarray(0, oracle.utf8_start),
            body,
            bytes.subarray(oracle.utf8_start + oracle.utf8_length),
          ]);
          oracle.utf8_length = body.length;
        } else bytes[oracle.utf8_start + oracle.utf8_length - 1] ^= 1;
        fs.writeFileSync(target, bytes);
        if (
          [
            'source-and-manifest-resigned',
            'drop-two-generations',
            'drop-privacy',
            'wrapper-noop',
          ].includes(mutation)
        ) {
          oracle.source_sha256 = createHash('sha256').update(bytes).digest('hex');
          oracle.body_sha256 = createHash('sha256')
            .update(bytes.subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length))
            .digest('hex');
        }
      }
      if (
        [
          'source-and-manifest-resigned',
          'drop-two-generations',
          'drop-privacy',
          'wrapper-noop',
        ].includes(mutation)
      )
        expect(() => verifyCommitConsumerSources(root, policy)).toThrow(
          'exact original triggers and actual oracle owners/bodies',
        );
      else expect(() => verifyCommitConsumerSources(root, policy)).toThrow();
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
});

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
const loadWide = () =>
  JSON.parse(
    fs.readFileSync(
      path.join(process.cwd(), fixtureDirectory, 's5-wide-reply-disposition.json'),
      'utf8',
    ),
  );

describe('S5 individually attributed wide reply guards', () => {
  test('binds fourteen actual test/scenario bodies and all thirteen original counterexamples', () => {
    const policy = loadWide();
    expect(policy.oracles).toHaveLength(14);
    expect(policy.triggers).toHaveLength(13);
    verifyWideReplyContract(policy);
    verifyWideReplySources(process.cwd(), policy);
  });
  test.each(Array.from({ length: 13 }, (_, index) => index))(
    'rejects missing or misattributed counterexample for trigger %s',
    (index) => {
      for (const mutation of [
        'missing',
        'wrong-owner',
        'wrong-variant',
        'helper-as-oracle',
        'duplicate',
      ]) {
        const policy = loadWide();
        const row = policy.triggers[index];
        if (mutation === 'missing') delete row.primary_counterexample;
        if (mutation === 'wrong-owner') row.primary_counterexample.oracle_id = 'wide-positive';
        if (mutation === 'wrong-variant') row.primary_counterexample.variant = 'positive-only';
        if (mutation === 'helper-as-oracle')
          row.primary_counterexample.oracle_id = 'SourceFault.Capture';
        if (mutation === 'duplicate') row.trigger = policy.triggers[(index + 1) % 13].trigger;
        expect(() => verifyWideReplyContract(policy)).toThrow();
      }
    },
  );
  test.each(Array.from({ length: 14 }, (_, index) => index))(
    'rejects wrong source, no body or self-resigned oracle %s',
    (index) => {
      for (const mutation of ['wrong-source', 'same-name-fake', 'no-body', 'resigned-body']) {
        const policy = loadWide();
        const oracle = policy.oracles[index];
        if (mutation === 'wrong-source') oracle.source = 'wrong-owner.cs';
        if (mutation === 'same-name-fake') oracle.source = 'same-name-in-comment.cs';
        if (mutation === 'no-body') oracle.utf8_length = 0;
        if (mutation === 'resigned-body') {
          oracle.body_sha256 = createHash('sha256').update(oracle.method).digest('hex');
          oracle.source_sha256 = oracle.body_sha256;
        }
        // This path bypasses the outer file seal: fixed AST-derived owners and
        // actual complete body identities must reject the alleged evidence.
        expect(() => verifyWideReplyContract(policy)).toThrow(
          'fixed actual oracle owner and complete body',
        );
      }
    },
  );
  test('rejects a physical same-name empty method even after source and body hashes are re-signed', () => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-wide-owner-'));
    try {
      const policy = loadWide();
      const oracle = policy.oracles.find((item: { id: string }) => item.id === 'diagnostic-counts');
      const fake = Buffer.from(`[Fact]\npublic void ${oracle.method}() {}\n`);
      const target = path.join(root, oracle.source);
      fs.mkdirSync(path.dirname(target), { recursive: true });
      fs.writeFileSync(target, fake);
      oracle.source_sha256 = createHash('sha256').update(fake).digest('hex');
      oracle.body_sha256 = oracle.source_sha256;
      oracle.utf8_start = 0;
      oracle.utf8_length = fake.length;
      expect(() => verifyWideReplySources(root, policy)).toThrow(
        'fixed actual oracle owner and complete body',
      );
    } finally {
      fs.rmSync(root, { force: true, recursive: true });
    }
  });
});

describe('S5 source-bound native assertion census', () => {
  test('binds all 152 originals, 138 original retained bodies, nine migrations and five retirements', () => {
    expect(verifyS5AssertionSuccessor(process.cwd())).toEqual({
      originalMethods: 152,
      currentNativeMethods: 147,
      authorizedDeletions: 5,
    });
    const rows = loadSuccessor().assertions;
    expect(
      rows.filter((row: { migration_disposition?: unknown }) => row.migration_disposition),
    ).toHaveLength(9);
    expect(rows.filter((row: { current: unknown }) => row.current === null)).toHaveLength(5);
    expect(
      rows.filter((row: { disposition: string }) => row.disposition === 'retained'),
    ).toHaveLength(138);
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
    'original-body',
    'current-body',
    'disposition',
    'reason',
    'oracle-name',
    'unmapped',
    'retained-relabel',
    'sdk-overclaim',
    'redirected-current-source',
    'extra-authority',
  ])('rejects current protocol migration %s drift', (mutation) => {
    const successor = loadSuccessor();
    const row = successor.assertions.find(
      (item: { migration_disposition?: unknown }) => item.migration_disposition,
    );
    expect(row).toBeDefined();
    if (mutation === 'original-body')
      row.migration_disposition.original_body_sha256 = '0'.repeat(64);
    if (mutation === 'current-body') row.current.body_sha256 = '0'.repeat(64);
    if (mutation === 'disposition') row.disposition = 'retained';
    if (mutation === 'reason') row.reason = 'No current execution required';
    if (mutation === 'oracle-name')
      row.migration_disposition.current_oracle_labels = ['unexecuted'];
    if (mutation === 'unmapped') delete row.migration_disposition;
    if (mutation === 'retained-relabel')
      successor.assertions[0] = {
        ...successor.assertions[0],
        disposition: 'migrated',
        migration_disposition: row.migration_disposition,
        reason: row.reason,
      };
    if (mutation === 'sdk-overclaim') row.migration_disposition.sdk_context_import_qualified = true;
    if (mutation === 'redirected-current-source')
      row.current.source = successor.current_sources.find(
        (source: { path: string }) => source.path !== row.source,
      ).path;
    if (mutation === 'extra-authority') row.migration_disposition.allow_fallback = true;
    expect(() => verifyS5AssertionSuccessor(process.cwd(), successor)).toThrow();
  });
  test.each([
    'NoncompletedFailuresRemainDistinctWithoutGuessingUnknownCauses',
    'CurrentReplayStillRejects17MemberResponsesAnd65TurnScripts',
    'ProductionHostPublishesTheSameRevisionScenariosInTwoFreshWorlds',
    'RehashedFailureCannotKeepCompletedQ1Outcome',
    'GrowthReportNamesToolResultsAsObservations',
  ])('independently closes prior rewritten consumer %s', (method) => {
    for (const mutation of [
      'original-body',
      'current-body',
      'oracle',
      'reason',
      'retained-relabel',
    ]) {
      const successor = loadSuccessor();
      const row = successor.assertions.find((item: { method: string }) => item.method === method);
      expect(row.disposition).toBe('migrated');
      if (mutation === 'original-body')
        row.migration_disposition.original_body_sha256 = '0'.repeat(64);
      if (mutation === 'current-body') row.current.body_sha256 = '0'.repeat(64);
      if (mutation === 'oracle') row.migration_disposition.current_oracle_labels = ['unexecuted'];
      if (mutation === 'reason') row.reason = 'No independent current evidence';
      if (mutation === 'retained-relabel') {
        row.disposition = 'retained';
        delete row.migration_disposition;
      }
      expect(() => verifyS5AssertionSuccessor(process.cwd(), successor)).toThrow();
    }
  });
  test.each([
    'original-bytes',
    'baseline-bytes',
    'body-change',
    'body-and-source-resigned',
    'extra-method',
    'missing-successor',
    'missing-format-policy',
    'format-policy-change',
    'oracle-source-change',
    'missing-protocol-policy',
    'protocol-policy-change',
    'protocol-oracle-source-change',
    'missing-wide-policy',
    'wide-policy-change',
    'wide-oracle-source-change',
    'wide-oracle-body-change',
    'wide-helper-source-change',
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
        's5-protocol-migrations.json',
        's5-wide-reply-disposition.json',
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
      const protocol = JSON.parse(
        fs.readFileSync(
          path.join(process.cwd(), fixtureDirectory, 's5-protocol-migrations.json'),
          'utf8',
        ),
      );
      for (const oracle of protocol.oracles) copy(oracle.path);
      const wide = loadWide();
      for (const oracle of wide.oracles) copy(oracle.source);
      for (const helper of wide.helpers) copy(helper.source);
      const grammar = loadGrammar();
      copy(grammarPolicyPath);
      copy(grammar.current_source.path);
      for (const oracle of grammar.oracles) copy(oracle.source);
      const r3 = loadR3();
      copy(r3PolicyPath);
      copy(r3.current_source.path);
      for (const oracle of r3.oracles) copy(oracle.source);
      // Establish a valid complete temporary source closure before each mutation.
      expect(verifyS5AssertionSuccessor(temporary).currentNativeMethods).toBe(147);
      if (mutation === 'missing-wide-policy')
        fs.unlinkSync(path.join(temporary, fixtureDirectory, 's5-wide-reply-disposition.json'));
      if (mutation === 'wide-policy-change')
        fs.appendFileSync(
          path.join(temporary, fixtureDirectory, 's5-wide-reply-disposition.json'),
          '\n',
        );
      if (mutation === 'wide-oracle-source-change')
        fs.appendFileSync(path.join(temporary, wide.oracles[0].source), '\n');
      if (mutation === 'wide-oracle-body-change') {
        const oracle = wide.oracles[0];
        const target = path.join(temporary, oracle.source);
        const bytes = fs.readFileSync(target);
        bytes[oracle.utf8_start + oracle.utf8_length - 1] ^= 1;
        fs.writeFileSync(target, bytes);
      }
      if (mutation === 'wide-helper-source-change')
        fs.appendFileSync(path.join(temporary, wide.helpers[0].source), '\n');
      if (mutation === 'missing-protocol-policy')
        fs.unlinkSync(path.join(temporary, fixtureDirectory, 's5-protocol-migrations.json'));
      if (mutation === 'protocol-policy-change')
        fs.appendFileSync(
          path.join(temporary, fixtureDirectory, 's5-protocol-migrations.json'),
          '\n',
        );
      if (mutation === 'protocol-oracle-source-change')
        fs.appendFileSync(path.join(temporary, protocol.oracles[1].path), '\n');
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
      if (
        mutation === 'body-change' ||
        mutation === 'body-and-source-resigned' ||
        mutation === 'extra-method'
      ) {
        // Select a retained source outside all sealed oracle-file closures so
        // the new baseline-body guard, independently, must reject re-signing.
        const oracleSources = new Set(
          [...format.oracles, ...protocol.oracles]
            .map((oracle) => oracle.path)
            .concat(wide.oracles.map((oracle: { source: string }) => oracle.source)),
        );
        const row =
          mutation === 'body-and-source-resigned'
            ? successor.assertions.find(
                (item: { disposition: string; source: string }) =>
                  item.disposition === 'retained' && !oracleSources.has(item.source),
              )
            : successor.assertions[0];
        expect(row).toBeDefined();
        const target = path.join(temporary, row.source);
        let bytes = fs.readFileSync(target);
        if (mutation === 'body-change' || mutation === 'body-and-source-resigned') {
          // Keep UTF8 and method identity valid, but change a real method body byte.
          const body = bytes.subarray(
            row.current.utf8_start,
            row.current.utf8_start + row.current.utf8_length,
          );
          const offset = body.indexOf(Buffer.from('Assert.'));
          expect(offset).toBeGreaterThanOrEqual(0);
          bytes[row.current.utf8_start + offset] = 'X'.charCodeAt(0);
          if (mutation === 'body-and-source-resigned')
            row.current.body_sha256 = createHash('sha256').update(body).digest('hex');
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
      if (mutation === 'body-and-source-resigned')
        expect(() => verifyS5AssertionSuccessor(temporary)).toThrow(
          /retained body must match immutable baseline/u,
        );
      else expect(() => verifyS5AssertionSuccessor(temporary)).toThrow();
    } finally {
      fs.rmSync(temporary, { recursive: true, force: true });
    }
  });
});

describe('S5 five native grammar-only retirements outside V1', () => {
  test('keeps five actual baseline/input identities and independent current owners', () => {
    const policy = loadGrammar();
    expect(verifyNativeGrammarDispositions(process.cwd())).toBe(5);
    expect(policy.current_source.attributed_methods_before).toBe(53);
    expect(policy.current_source.attributed_methods_after).toBe(48);
    const originalV1 = JSON.parse(
      fs.readFileSync(
        'runtime/tests/SolusEvaluationFixture/s5-original-method-identities.json',
        'utf8',
      ),
    );
    for (const row of policy.rows) {
      expect(
        originalV1.assertions.some(
          (entry: { source: string; method: string }) =>
            entry.source === row.original.source && entry.method === row.original.method,
        ),
      ).toBe(false);
      expect(row.original.attributes).toEqual(['Fact']);
      expect(row.retirement_input.attributes).toEqual(['Fact']);
    }
    const changed = policy.rows.filter(
      (row) => row.original.body_sha256 !== row.retirement_input.body_sha256,
    );
    expect(changed.map((row) => row.original.method)).toEqual([
      'RequiredRootFieldsRejectMissingNullTypeAndDomainMutations',
    ]);
    expect(changed[0].input_body_difference).toContain('only preexisting authorized');
  });
  test.each([0, 1, 2, 3, 4])('rejects original trigger or method misclassification %s', (index) => {
    for (const mutation of [
      'trigger',
      'owner',
      'original-body',
      'input-body',
      'retained',
      'attributes',
      'method',
      'sdk-equivalence',
      'false-body-equality',
    ]) {
      const policy = loadGrammar();
      const row = policy.rows[index];
      if (mutation === 'trigger') row.triggers.pop();
      if (mutation === 'owner') row.independent_current_oracle_ids[0] = 'same-name-helper';
      if (mutation === 'original-body') row.original.body_sha256 = '0'.repeat(64);
      if (mutation === 'input-body')
        row.retirement_input.body_sha256 = row.original.body_sha256 + '0';
      if (mutation === 'retained') row.disposition = 'retained';
      if (mutation === 'attributes') row.original.attributes = [];
      if (mutation === 'method') row.original.method = 'UnclassifiedNativeMethod';
      if (mutation === 'sdk-equivalence') row.equivalence_limit = 'Native JSON is the SDK payload';
      if (mutation === 'false-body-equality') row.input_body_difference = 'all bodies equal';
      expect(() => verifyNativeGrammarContract(policy)).toThrow(
        'exact native grammar triggers and actual owners',
      );
    }
  });
  test.each(Array.from({ length: 11 }, (_, index) => index))(
    'rejects fake current oracle body %s',
    (index) => {
      for (const mutation of [
        'wrong-file',
        'wrong-method',
        'empty-body',
        'resigned-body',
        'sdk-role',
      ]) {
        const policy = loadGrammar();
        const oracle = policy.oracles[index];
        if (mutation === 'wrong-file') oracle.source = 'fake-same-name.cs';
        if (mutation === 'wrong-method') oracle.method = 'HelperWithoutCurrentAssertion';
        if (mutation === 'empty-body') oracle.utf8_length = 0;
        if (mutation === 'resigned-body')
          oracle.source_sha256 = oracle.body_sha256 = 'a'.repeat(64);
        if (mutation === 'sdk-role') oracle.role = 'SDK context import qualified';
        expect(() => verifyNativeGrammarContract(policy)).toThrow();
      }
    },
  );
  test.each([
    'owner',
    'sdk',
    'missing',
    'duplicate',
    'count-swap',
    'baseline',
    'v1',
    'source-count',
  ])('rejects closed policy substitution %s', (mutation) => {
    const policy = loadGrammar();
    if (mutation === 'owner') policy.owner_issue = 403;
    if (mutation === 'sdk') policy.sdk_context_import_qualified = true;
    if (mutation === 'missing') policy.rows.pop();
    if (mutation === 'duplicate') policy.rows.push(policy.rows[0]);
    if (mutation === 'count-swap') policy.rows[1] = policy.rows[0];
    if (mutation === 'baseline') policy.baseline_source_commit = '0'.repeat(40);
    if (mutation === 'v1') policy.original_v1_membership = 'Replace immutable V1';
    if (mutation === 'source-count') policy.current_source.attributed_methods_after++;
    expect(() => verifyNativeGrammarContract(policy)).toThrow();
  });
  test.each([
    'missing-policy',
    'policy-bytes',
    'duplicate-policy-key',
    'missing-source',
    'source-change',
    'reintroduced-method',
    'symlink',
    'directory',
    'oversize',
    'missing-oracle',
    'oracle-change',
    'oracle-noop-resigned',
    'archive-refusal-dropped-resigned',
    'source-resigned',
  ])('rejects actual %s drift after a valid complete closure', (mutation) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-grammar-'));
    try {
      const policy = loadGrammar();
      const copy = (relative: string) => {
        const target = path.join(root, relative);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(relative, target);
      };
      for (const relative of new Set<string>([
        grammarPolicyPath,
        policy.current_source.path,
        ...policy.oracles.map((oracle) => oracle.source),
      ]))
        copy(relative);
      expect(verifyNativeGrammarDispositions(root)).toBe(5);
      const policyTarget = path.join(root, grammarPolicyPath);
      const target = path.join(root, policy.current_source.path);
      if (mutation === 'missing-policy') fs.unlinkSync(policyTarget);
      if (mutation === 'policy-bytes') fs.appendFileSync(policyTarget, '\n');
      if (mutation === 'duplicate-policy-key')
        fs.writeFileSync(
          policyTarget,
          fs
            .readFileSync(policyTarget, 'utf8')
            .replace('"owner_issue": 409,', '"owner_issue": 409, "owner_issue": 409,'),
        );
      if (mutation === 'missing-source') fs.unlinkSync(target);
      if (mutation === 'source-change') fs.appendFileSync(target, '\n');
      if (mutation === 'reintroduced-method')
        fs.writeFileSync(
          target,
          fs
            .readFileSync(target, 'utf8')
            .replace(
              /\n\}\s*$/u,
              `\n[Fact]\npublic void ${policy.rows[0].original.method}() {}\n}\n`,
            ),
        );
      if (mutation === 'symlink') {
        fs.renameSync(target, target + '.actual');
        fs.symlinkSync(path.basename(target) + '.actual', target);
      }
      if (mutation === 'directory') {
        fs.unlinkSync(target);
        fs.mkdirSync(target);
      }
      if (mutation === 'oversize') fs.writeFileSync(target, Buffer.alloc(1048577, 0x20));
      if (mutation === 'missing-oracle') fs.unlinkSync(path.join(root, policy.oracles[0].source));
      if (mutation === 'oracle-change')
        fs.appendFileSync(path.join(root, policy.oracles[0].source), '\n');
      if (mutation === 'source-resigned') {
        fs.appendFileSync(target, '\n');
        policy.current_source.sha256 = createHash('sha256')
          .update(fs.readFileSync(target))
          .digest('hex');
        expect(() => verifyNativeGrammarSources(root, policy)).toThrow(
          'exact native grammar triggers and actual owners',
        );
      }
      if (mutation === 'oracle-noop-resigned' || mutation === 'archive-refusal-dropped-resigned') {
        const oracle = policy.oracles.find(
          (entry) =>
            entry.method ===
            (mutation === 'oracle-noop-resigned'
              ? 'ActualFreshThinkingRunRejectsDetachedOrResumedMetadata'
              : 'Main'),
        );
        expect(oracle).toBeDefined();
        if (!oracle) throw new Error('missing actual grammar oracle');
        const file = path.join(root, oracle.source);
        const bytes = fs.readFileSync(file);
        const before = bytes
          .subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length)
          .toString();
        let after: string;
        if (mutation === 'oracle-noop-resigned') {
          after =
            before.slice(0, before.indexOf('{') + 1) +
            '\nawait Task.CompletedTask;\n' +
            before.slice(before.lastIndexOf('}'));
        } else {
          // Select the unique actual refusal predicate, preserving its original
          // diagnostic label without coupling this mutant to a local variable name.
          const refusals = [
            ...before.matchAll(
              /Require\([A-Za-z_]\w*\.Result\(\) is \{[^}]*\},\s*("[^"\r\n]+_rejected_before_provider")\);/gu,
            ),
          ];
          expect(refusals).toHaveLength(1);
          after = before.replace(refusals[0][0], `Require(true, ${refusals[0][1]});`);
        }
        expect(after).not.toBe(before);
        const body = Buffer.from(after);
        const changed = Buffer.concat([
          bytes.subarray(0, oracle.utf8_start),
          body,
          bytes.subarray(oracle.utf8_start + oracle.utf8_length),
        ]);
        fs.writeFileSync(file, changed);
        oracle.utf8_length = body.length;
        oracle.body_sha256 = createHash('sha256').update(body).digest('hex');
        oracle.source_sha256 = createHash('sha256').update(changed).digest('hex');
        expect(() => verifyNativeGrammarSources(root, policy)).toThrow(
          'exact native grammar triggers and actual owners',
        );
      } else if (mutation !== 'source-resigned')
        expect(() => verifyNativeGrammarDispositions(root)).toThrow();
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
});

describe('S5 thirteen native R3 application consumers', () => {
  test.each([
    'cli-current-argv',
    'cli-current-phase',
    'cli-secret-inputs',
    'restore-method',
    'restore-route',
  ])('rejects actual CLI source restoration or resigned no-op %s', (mutation) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-cli-'));
    try {
      const policy = loadR3();
      for (const relative of new Set([
        r3PolicyPath,
        policy.current_source.path,
        ...policy.oracles.map((oracle) => oracle.source),
      ])) {
        const target = path.join(root, relative);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(relative, target);
      }
      expect(verifyR3ApplicationDispositions(root)).toBe(13);
      if (mutation.startsWith('cli-')) {
        const oracle = policy.oracles.find((owner) => owner.id === mutation)!;
        expect(oracle).toBeDefined();
        const file = path.join(root, oracle.source),
          bytes = fs.readFileSync(file);
        const before = bytes
          .subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length)
          .toString();
        const body = Buffer.from(
          before.slice(0, before.indexOf('{') + 1) +
            (mutation === 'cli-current-argv' ? '\nawait Task.CompletedTask;\n' : '\nreturn;\n') +
            before.slice(before.lastIndexOf('}')),
        );
        const changed = Buffer.concat([
          bytes.subarray(0, oracle.utf8_start),
          body,
          bytes.subarray(oracle.utf8_start + oracle.utf8_length),
        ]);
        fs.writeFileSync(file, changed);
        oracle.body_sha256 = createHash('sha256').update(body).digest('hex');
        oracle.source_sha256 = createHash('sha256').update(changed).digest('hex');
        oracle.utf8_length = body.length;
      } else {
        const file = path.join(root, policy.cli_source.path),
          before = fs.readFileSync(file, 'utf8');
        const end = before.lastIndexOf('\n}');
        const changed =
          mutation === 'restore-method'
            ? before.slice(0, end) +
              '\n[Fact] public void ' +
              policy.cli_consumers[0].original.method +
              '() {}\n' +
              before.slice(end)
            : before.replace(
                'start.ArgumentList.Add(typeof(RuntimeApplication).Assembly.Location);',
                'start.ArgumentList.Add(typeof(LiveAgentVerifierFixture.Program).Assembly.Location);\n' +
                  'start.ArgumentList.Add("historical-direct");',
              );
        expect(changed).not.toBe(before);
        fs.writeFileSync(file, changed);
        policy.cli_source.current_sha256 = createHash('sha256').update(changed).digest('hex');
      }
      expect(() => verifyR3ApplicationSources(root, policy)).toThrow(
        'exact R3 original triggers, current bodies and ownership limits',
      );
      expect(() => verifyR3ApplicationDispositions(root)).toThrow();
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
  test('closes ten original CLI bootstrap consumers without activating historical execution', () => {
    const policy = loadR3();
    expect(verifyR3ApplicationDispositions(process.cwd())).toBe(13);
    expect(policy.cli_consumers).toHaveLength(10);
    expect(policy.cli_consumers.reduce((n, row) => n + row.expanded_native_cases, 0)).toBe(15);
    expect(policy.cli_source.removed_methods).toBe(10);
    const source = fs.readFileSync(policy.cli_source.path, 'utf8');
    expect(source).not.toContain('historical-direct');
    expect(source).toContain('typeof(RuntimeApplication).Assembly.Location');
    for (const row of policy.cli_consumers)
      expect(source).not.toContain(' ' + row.original.method + '(');
  });
  test.each(Array.from({ length: 10 }, (_, index) => index))(
    'rejects relabeled or incomplete original CLI consumer %s',
    (index) => {
      for (const mutation of [
        'body',
        'attribute',
        'cases',
        'trigger',
        'disposition',
        'owner',
        'limit',
      ]) {
        const policy = loadR3(),
          row = policy.cli_consumers[index];
        if (mutation === 'body') row.original.body_sha256 = 'f'.repeat(64);
        if (mutation === 'attribute') row.original.attributes.pop();
        if (mutation === 'cases') row.expanded_native_cases++;
        if (mutation === 'trigger') row.original_triggers.pop();
        if (mutation === 'disposition') row.disposition = 'retained';
        if (mutation === 'owner') row.primary_oracle_ids = ['cli-process-fixture'];
        if (mutation === 'limit')
          row.precise_dispositions = ['Authored metadata proves SDK acceptance'];
        expect(() => verifyR3ApplicationContract(policy)).toThrow();
      }
    },
  );
  test('binds all native triggers while preserving the actual shared partial members', () => {
    const policy = loadR3();
    expect(verifyR3ApplicationDispositions(process.cwd())).toBe(13);
    expect(policy.removed_source.expanded_native_cases).toBe(25);
    expect(policy.current_source.attributed_methods).toBe(16);
    expect(policy.shared_members.map((m) => m.name).sort()).toEqual(['Identity', 'StateSecret']);
    const originalV1 = JSON.parse(
      fs.readFileSync(
        'runtime/tests/SolusEvaluationFixture/s5-original-method-identities.json',
        'utf8',
      ),
    );
    for (const row of policy.rows)
      expect(
        originalV1.assertions.some(
          (entry: { source: string; method: string }) =>
            entry.source === row.original.source && entry.method === row.original.method,
        ),
      ).toBe(false);
  });
  test.each(Array.from({ length: 13 }, (_, i) => i))(
    'rejects incomplete or relabeled native consumer %s',
    (index) => {
      for (const mutation of [
        'trigger',
        'disposition',
        'original-body',
        'input-body',
        'owner',
        'support-as-primary',
        'missing-variant',
        'sdk-equivalence',
      ]) {
        const policy = loadR3(),
          row = policy.rows[index];
        if (mutation === 'trigger') row.original_triggers.pop();
        if (mutation === 'disposition') row.disposition = 'retained';
        if (mutation === 'original-body') row.original.body_sha256 = '0'.repeat(64);
        if (mutation === 'input-body')
          row.retirement_input.body_sha256 = row.original.body_sha256 + '0';
        if (mutation === 'owner') row.primary_oracle_ids[0] = 'unexecuted-helper';
        if (mutation === 'support-as-primary') row.primary_oracle_ids = ['flow'];
        if (mutation === 'missing-variant') row.precise_dispositions.pop();
        if (mutation === 'sdk-equivalence')
          row.precise_dispositions = [
            'Native request objects and SDK opaque context are byte equivalent',
          ];
        expect(() => verifyR3ApplicationContract(policy)).toThrow(
          'exact R3 original triggers, current bodies and ownership limits',
        );
      }
    },
  );
  test.each(Array.from({ length: 34 }, (_, i) => i))(
    'rejects misattributed or empty current R3 owner %s',
    (index) => {
      for (const mutation of ['source', 'method', 'no-body', 'resigned', 'role']) {
        const policy = loadR3(),
          oracle = policy.oracles[index];
        if (mutation === 'source') oracle.source = 'fake-same-name.cs';
        if (mutation === 'method') oracle.method = 'BodylessHelper';
        if (mutation === 'no-body') oracle.utf8_length = 0;
        if (mutation === 'resigned') oracle.source_sha256 = oracle.body_sha256 = 'f'.repeat(64);
        if (mutation === 'role') oracle.role = 'SDK context qualified by source count';
        expect(() => verifyR3ApplicationContract(policy)).toThrow();
      }
    },
  );
  test.each(['owner', 'sdk', 'missing', 'duplicate', 'count-swap', 'shared-field', 'case-count'])(
    'rejects R3 contract substitution %s',
    (mutation) => {
      const policy = loadR3();
      if (mutation === 'owner') policy.owner_issue = 403;
      if (mutation === 'sdk') policy.sdk_context_import_qualified = true;
      if (mutation === 'missing') policy.rows.pop();
      if (mutation === 'duplicate') policy.rows.push(policy.rows[0]);
      if (mutation === 'count-swap') policy.rows[1] = policy.rows[0];
      if (mutation === 'shared-field') policy.shared_members.pop();
      if (mutation === 'case-count') policy.removed_source.expanded_native_cases--;
      expect(() => verifyR3ApplicationContract(policy)).toThrow();
    },
  );
  test.each(['original-body', 'current-method', 'SDK-equivalence'])(
    'rejects trusted profile consumer substitution %s',
    (mutation) => {
      const policy = loadR3();
      for (const consumer of policy.trusted_profile_consumers) {
        const changed = structuredClone(policy);
        const row = changed.trusted_profile_consumers.find(
          (r) => r.original.method === consumer.original.method,
        )!;
        if (mutation === 'original-body') row.original.body_sha256 = 'f'.repeat(64);
        if (mutation === 'current-method') row.current_method = row.original.method;
        if (mutation === 'SDK-equivalence')
          row.precise_dispositions = ['Authored receipt tuple proves current SDK acceptance'];
        expect(() => verifyR3ApplicationContract(changed)).toThrow();
      }
    },
  );
  test.each([
    'missing-policy',
    'raw-policy',
    'reintroduced-source',
    'dangling-source-link',
    'missing-current',
    'shared-value-change',
    'current-resigned',
    'missing-oracle',
    'oracle-noop',
    'macro-noop-resigned',
    'genuine-reader-noop-resigned',
    'trusted-projection-noop-resigned',
    'flow-source-change',
  ])('rejects actual R3 %s after verified complete closure', (mutation) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-s5-r3-'));
    try {
      const policy = loadR3();
      const copy = (relative: string) => {
        const target = path.join(root, relative);
        fs.mkdirSync(path.dirname(target), { recursive: true });
        fs.copyFileSync(relative, target);
      };
      for (const relative of new Set([
        r3PolicyPath,
        policy.current_source.path,
        ...policy.oracles.map((o) => o.source),
      ]))
        copy(relative);
      expect(verifyR3ApplicationDispositions(root)).toBe(13);
      const current = path.join(root, policy.current_source.path),
        removed = path.join(root, policy.removed_source.path);
      if (mutation === 'missing-policy') fs.unlinkSync(path.join(root, r3PolicyPath));
      if (mutation === 'raw-policy') fs.appendFileSync(path.join(root, r3PolicyPath), '\n');
      if (mutation === 'reintroduced-source')
        fs.writeFileSync(removed, '// Removed native fixture cannot become a current owner.\n');
      if (mutation === 'dangling-source-link') fs.symlinkSync('missing-source.cs', removed);
      if (mutation === 'missing-current') fs.unlinkSync(current);
      if (mutation === 'shared-value-change') {
        const member = policy.shared_members.find((m) => m.name === 'Identity');
        expect(member).toBeDefined();
        if (!member) throw new Error('missing shared field');
        const bytes = fs.readFileSync(current),
          body = bytes
            .subarray(member.current_utf8_start, member.current_utf8_start + member.utf8_length)
            .toString();
        const changed = body.replace('107', '108');
        expect(changed).not.toBe(body);
        fs.writeFileSync(
          current,
          Buffer.concat([
            bytes.subarray(0, member.current_utf8_start),
            Buffer.from(changed),
            bytes.subarray(member.current_utf8_start + member.utf8_length),
          ]),
        );
      }
      if (mutation === 'current-resigned') {
        fs.appendFileSync(current, '\n');
        policy.current_source.sha256 = createHash('sha256')
          .update(fs.readFileSync(current))
          .digest('hex');
        expect(() => verifyR3ApplicationSources(root, policy)).toThrow(
          'exact R3 original triggers, current bodies and ownership limits',
        );
      }
      if (mutation === 'missing-oracle')
        fs.unlinkSync(path.join(root, policy.oracles.find((o) => o.id === 'privacy')!.source));
      if (
        mutation === 'oracle-noop' ||
        mutation === 'macro-noop-resigned' ||
        mutation === 'genuine-reader-noop-resigned' ||
        mutation === 'trusted-projection-noop-resigned'
      ) {
        const oracle = policy.oracles.find(
          (o) =>
            o.id ===
            (mutation === 'oracle-noop'
              ? 'privacy'
              : mutation === 'genuine-reader-noop-resigned'
                ? 'genuine-domain-refusal'
                : mutation === 'trusted-projection-noop-resigned'
                  ? 'trusted-quality-projection'
                  : 'macro'),
        );
        expect(oracle).toBeDefined();
        if (!oracle) throw new Error('missing real R3 oracle');
        const file = path.join(root, oracle.source),
          bytes = fs.readFileSync(file),
          before = bytes
            .subarray(oracle.utf8_start, oracle.utf8_start + oracle.utf8_length)
            .toString();
        const changed =
          before.slice(0, before.indexOf('{') + 1) +
          (mutation === 'trusted-projection-noop-resigned'
            ? '\nreturn;\n'
            : '\nawait Task.CompletedTask;\n') +
          before.slice(before.lastIndexOf('}'));
        expect(changed).not.toBe(before);
        const body = Buffer.from(changed),
          all = Buffer.concat([
            bytes.subarray(0, oracle.utf8_start),
            body,
            bytes.subarray(oracle.utf8_start + oracle.utf8_length),
          ]);
        fs.writeFileSync(file, all);
        if (
          mutation === 'macro-noop-resigned' ||
          mutation === 'genuine-reader-noop-resigned' ||
          mutation === 'trusted-projection-noop-resigned'
        ) {
          oracle.body_sha256 = createHash('sha256').update(body).digest('hex');
          oracle.source_sha256 = createHash('sha256').update(all).digest('hex');
          oracle.utf8_length = body.length;
          expect(() => verifyR3ApplicationSources(root, policy)).toThrow(
            'exact R3 original triggers, current bodies and ownership limits',
          );
        }
      }
      if (mutation === 'flow-source-change')
        fs.appendFileSync(
          path.join(root, policy.oracles.find((o) => o.id === 'flow')!.source),
          '\n',
        );
      if (!['current-resigned', 'macro-noop-resigned'].includes(mutation))
        expect(() => verifyR3ApplicationDispositions(root)).toThrow();
    } finally {
      fs.rmSync(root, { recursive: true, force: true });
    }
  });
});

describe('S5 readonly historical growth migrations', () => {
  for (const method of [
    'RehashedFailureCannotKeepCompletedQ1Outcome',
    'GrowthReportNamesToolResultsAsObservations',
  ])
    test.each(['missing', 'drop', 'substitute'])(
      'rejects altered original variants ' + method + ' %s',
      (mutation) => {
        const successor = loadSuccessor();
        const row = successor.assertions.find((item: { method: string }) => item.method === method);
        if (mutation === 'missing') delete row.migration_disposition.variant_attributes;
        if (mutation === 'drop') row.migration_disposition.variant_attributes.pop();
        if (mutation === 'substitute')
          row.migration_disposition.variant_attributes[1] = 'InlineData("unexecuted")';
        expect(() => verifyS5AssertionSuccessor(process.cwd(), successor)).toThrow();
      },
    );
});
