import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

// Authored outside the C# producer/reader. Values are controlled-response
// expectations, not population counts or prices supplied by a candidate report.
export const cases = [
  'known',
  'zero',
  'total-only',
  'hit-only',
  'miss-only',
  'missing-usage',
  'invalid-final',
  'invalid-usage',
  'capture-failure',
  'transport-failure',
  'reject-import',
  'stale-publication',
  'cancel-after-usage',
];
const guid = /^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/u;
const sha = /^[a-f0-9]{64}$/u;
const reference = 'synthetic-usd-1-4-5-per-million';
export function hash(domain, text) {
  const value = Buffer.from(text, 'utf8');
  const size = Buffer.alloc(4);
  size.writeUInt32BE(value.length);
  return createHash('sha256')
    .update('apr.r6.solus.' + domain + '\0')
    .update(size)
    .update(value)
    .digest('hex');
}
function detail(attempt, kind) {
  const matches = attempt.details.filter((d) => d.kind === kind);
  assert.ok(matches.length <= 1);
  if (!matches.length) return null;
  assert.equal(matches[0].relationship, 'IncludedInInput');
  return matches[0].value;
}
function amount(view, input, output, complete) {
  assert.equal(view.known_input, input);
  assert.equal(view.known_output, output);
  assert.equal(
    view.total,
    complete ? (Math.round(input * 1e6) + Math.round(output * 1e6)) / 1e6 : null,
  );
  assert.equal(view.input_complete, input !== null);
  assert.equal(view.output_complete, output !== null);
}
export function verifyReport(report, selection, mode, seed = null) {
  assert.equal(report.format, 'apr.r6.solus-evidence.v1');
  assert.equal(report.execution_kind, 'controlled');
  assert.equal(report.scenario, mode);
  assert.deepEqual(report.source, selection.source);
  assert.match(report.startup_sha256, sha);
  assert.ok(Number.isInteger(report.process_id) && report.process_id > 0);
  assert.equal(report.native_provider_creates, 0);
  assert.equal(report.price.reference, reference);
  const noWork = ['missing-key', 'recovery', 'reject-import'].includes(mode);
  if (noWork) {
    assert.equal(report.history_reported, false);
    assert.equal(report.current, null);
    assert.deepEqual(report.exchanges, []);
    assert.deepEqual(report.wire, []);
    assert.deepEqual(report.history, []);
    assert.equal(report.current_tools, 0);
    assert.equal(report.host_accounting_zero, true);
    assert.equal(report.product_accepted, false);
    assert.equal(report.durable_accepted, mode === 'recovery');
    assert.equal(report.recovered_without_work, mode === 'recovery');
    assert.equal(report.admission, mode === 'reject-import' ? 'Rejected' : null);
    amount(report.price.observed, 0, 0, true);
    return;
  }
  const fresh = mode === 'seed';
  assert.equal(report.history_reported, true);
  const early = [
    'missing-usage',
    'invalid-usage',
    'transport-failure',
    'cancel-after-usage',
  ].includes(mode);
  const count = fresh ? 5 : early ? 1 : 2;
  const accepted = ['seed', 'known', 'zero', 'total-only', 'hit-only', 'miss-only'].includes(mode);
  const validWork = accepted || ['capture-failure', 'stale-publication'].includes(mode);
  assert.equal(report.product_accepted, validWork);
  assert.equal(report.durable_accepted, accepted);
  assert.equal(report.recovered_without_work, false);
  assert.equal(report.host_accounting_zero, false);
  assert.equal(report.admission, fresh ? 'Fresh' : 'Supplied');
  assert.equal(report.current_tools, fresh ? 4 : early ? 0 : 1);
  assert.equal(report.current.coverage, 'Complete');
  assert.equal(report.current.continued_calls, 0);
  assert.match(report.current.execution_id, guid);
  assert.equal(report.current.attempts.length, count);
  assert.equal(report.exchanges.length, count);
  assert.equal(report.wire.length, count);
  const unknown = ['missing-usage', 'transport-failure'].includes(mode);
  const input = unknown ? null : mode === 'zero' ? 0 : fresh ? 10 : 11;
  const output = unknown ? null : mode === 'zero' ? 0 : fresh ? 5 : 7;
  assert.deepEqual(report.current.input, {
    observed_tokens: input === null ? null : input * count,
    coverage: unknown ? 'Unavailable' : 'Complete',
  });
  assert.deepEqual(report.current.output, {
    observed_tokens: output === null ? null : output * count,
    coverage: unknown ? 'Unavailable' : 'Complete',
  });
  const physical = new Set(),
    logical = new Set();
  for (let i = 0; i < count; i++) {
    const attempt = report.current.attempts[i];
    const wire = report.wire[i];
    assert.equal(attempt.execution_id, report.current.execution_id);
    assert.match(attempt.logical_call_id, guid);
    assert.match(attempt.physical_attempt_id, guid);
    assert.ok(!physical.has(attempt.physical_attempt_id) && !logical.has(attempt.logical_call_id));
    physical.add(attempt.physical_attempt_id);
    logical.add(attempt.logical_call_id);
    assert.equal(attempt.attempt_number, 1); // Selected production profile; no retries enabled.
    assert.equal(attempt.exposure, mode === 'transport-failure' ? 'Unknown' : 'Dispatched');
    assert.equal(attempt.input_tokens, input);
    assert.equal(attempt.output_tokens, output);
    const hit =
      unknown || ['total-only', 'miss-only', 'invalid-usage'].includes(mode)
        ? null
        : mode === 'zero'
          ? 0
          : fresh
            ? 4
            : 3;
    const miss =
      unknown || ['total-only', 'hit-only'].includes(mode)
        ? null
        : mode === 'zero'
          ? 0
          : fresh
            ? 6
            : 8;
    assert.equal(detail(attempt, 'CacheRead'), hit);
    assert.equal(detail(attempt, 'UncachedInput'), miss);
    assert.deepEqual(report.exchanges[i].observation, attempt);
    // Provider acceptance is separate from later usage/product/capture failure.
    const result =
      mode === 'invalid-usage'
        ? ['Rejected', 'InvalidResponse']
        : mode === 'transport-failure'
          ? ['Failed', 'ProviderFailed']
          : ['Succeeded', 'None'];
    assert.equal(report.exchanges[i].outcome, result[0]);
    assert.equal(report.exchanges[i].error, result[1]);
    assert.equal(wire.execution_id, attempt.execution_id);
    assert.equal(wire.logical_call_id, attempt.logical_call_id);
    assert.equal(wire.physical_attempt_id, attempt.physical_attempt_id);
    assert.equal(wire.attempt_number, 1);
    assert.equal(wire.messages.length, fresh ? 3 + i * 2 : 14 + i * 2);
    assert.equal(wire.messages[0].role, 'system');
    assert.ok(wire.messages.slice(1).every((m) => m.role !== 'system'));
    assert.match(wire.whole.sha256, sha);
    assert.ok(wire.whole.bytes > 0);
    assert.deepEqual(wire.settings, report.wire[0].settings);
  }
  if (fresh) {
    assert.deepEqual(report.history, []);
    assert.deepEqual(report.final_response, {
      content_sha256: hash('content', selection.seedFinal),
      reasoning_sha256: hash('reasoning', 'V2_RESTRICTED_REASONING_CANARY_seed_4'),
      calls: 0,
    });
  } else {
    assert.ok(seed);
    assert.equal(report.history.length, 1);
    assert.equal(report.history[0].logical_work_id, seed.logical_work_id);
    assert.equal(report.history[0].round_id, seed.round_id);
    assert.deepEqual(report.history[0].usage, seed.current);
    assert.notEqual(report.current.execution_id, seed.current.execution_id);
    for (const historical of seed.current.attempts) {
      assert.ok(
        !physical.has(historical.physical_attempt_id) && !logical.has(historical.logical_call_id),
      );
    }
    for (const wire of report.wire) {
      assert.deepEqual(wire.messages.slice(0, 11), seed.wire.at(-1).messages);
      assert.equal(wire.messages[11].role, 'assistant');
      assert.equal(wire.messages[11].content_sha256, seed.final_response.content_sha256);
      assert.equal(wire.messages[11].reasoning_sha256, seed.final_response.reasoning_sha256);
      assert.equal(wire.messages[11].tool_association_sha256, null);
      assert.equal(wire.messages[12].role, 'user');
      assert.equal(wire.messages[13].role, 'user');
    }
    assert.deepEqual(
      report.final_response,
      early
        ? null
        : {
            content_sha256: hash(
              'content',
              mode === 'invalid-final' ? '{}' : selection.targetFinal,
            ),
            reasoning_sha256: hash('reasoning', 'V2_RESTRICTED_REASONING_CANARY_current_1'),
            calls: 0,
          },
    );
  }
  if (accepted) {
    assert.ok(report.domain);
    assert.equal(report.capture, 'Delivered');
    assert.equal(report.domain.package_version, '0.1.0-internal.4');
    assert.equal(report.domain.profile, 'cr1-solus-internal4-dev');
    assert.equal(
      report.domain.closure_sha256,
      'ade505cdff69a526ae95109b6258004304e7b504b2eb3b7c9b1fec3964d74949',
    );
    assert.equal(report.domain.implementation_id, '3f520f02-3b59-431e-8acf-a62544f28760');
    assert.equal(report.domain.format_version, 2);
    assert.equal(report.domain.compatibility_version, 1);
    assert.match(report.domain.runtime_configuration_sha256, sha);
    assert.match(report.domain.provider_configuration_sha256, sha);
    assert.match(report.logical_work_id, guid);
    assert.match(report.round_id, guid);
    if (seed) {
      assert.deepEqual(report.domain, seed.domain);
      assert.notEqual(report.round_id, seed.round_id);
      assert.notEqual(report.logical_work_id, seed.logical_work_id);
    }
  }
  if (unknown) amount(report.price.observed, null, null, false);
  else if (['total-only', 'hit-only', 'miss-only', 'invalid-usage'].includes(mode))
    amount(report.price.observed, null, (output * count * 5) / 1e6, false);
  else
    amount(
      report.price.observed,
      mode === 'zero' ? 0 : fresh ? (28 * count) / 1e6 : (35 * count) / 1e6,
      (output * count * 5) / 1e6,
      true,
    );
  if (!unknown)
    amount(
      report.price.same_token_all_miss_counterfactual,
      (input * count * 4) / 1e6,
      (output * count * 5) / 1e6,
      true,
    );
}
