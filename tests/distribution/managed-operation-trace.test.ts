import { EventEmitter } from 'node:events';
import { describe, expect, test } from 'vitest';
import {
  createOperationTrace,
  observeResponse,
} from '../../runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/operation-trace.mjs';

function fixture() {
  let time = 100;
  const trace = createOperationTrace({
    now: () => time,
    utc: () => '2026-10-22T21:00:00.000Z',
  });
  trace.beginCase();
  return {
    trace,
    advance: (value: number) => {
      time += value;
    },
  };
}
describe('bounded archive external-operation observations', () => {
  test('correlates SDK and REST by aliases without exposing identifiers or semantic claims', () => {
    const { trace, advance } = fixture();
    const finish = trace.start('sdk_finalize', 'PRIVATE_ID_NAME_URL');
    advance(20);
    finish(200);
    const read = trace.start('meta', 'PRIVATE_ID_NAME_URL');
    advance(10);
    read(200);
    const value = trace.snapshot();
    expect(value.started_at).toBe('2026-10-22T21:00:00.000Z');
    expect(value.elapsed_ms).toBe(30);
    expect(value.tail).toEqual([
      [1, 20, 'sdk_finalize', 'o1', 200, 20, 'finished'],
      [2, 30, 'meta', 'o1', 200, 10, 'finished'],
    ]);
    expect(value.last_mutation).toEqual(value.tail[0]);
    expect(value.last_readback_observation).toEqual(value.tail[1]);
    expect(JSON.stringify(value)).not.toMatch(
      /PRIVATE|semantic_readback|payload|https:|artifact_id/,
    );
  });
  test('distinguishes unfinished/closed HTTP observations and completes each once', () => {
    const { trace } = fixture();
    const response = Object.assign(new EventEmitter(), { statusCode: 200 });
    observeResponse(trace, 'meta', response, 123);
    expect(trace.snapshot().in_flight).toHaveLength(1);
    response.emit('close');
    response.emit('finish');
    expect(trace.snapshot().counts.meta).toEqual([1, 0, 1]);
    expect(trace.snapshot().last_completed).toBeNull();
    expect(trace.snapshot().tail[0][6]).toBe('closed');
  });
  test('keeps a bounded tail, in-flight sample and independent snapshots', () => {
    const { trace } = fixture();
    for (let i = 0; i < 4000; i++) trace.start('meta', i)(200);
    for (let i = 0; i < 64; i++) trace.start('zip_body', i);
    const value = trace.snapshot();
    expect(value.tail).toHaveLength(12);
    expect(value.in_flight).toHaveLength(8);
    expect(value.in_flight_total).toBe(64);
    expect(Buffer.byteLength(JSON.stringify(value))).toBeLessThan(4096 - 40);
    trace.start('delete', 123)(204);
    expect(value.last_mutation).toBeNull();
  });
  test('resets case counters and preserves object identity without mixing old completions', () => {
    const { trace } = fixture();
    const old = trace.start('meta', 'opaque');
    trace.beginCase();
    old(200);
    trace.start('meta', 'opaque')(404);
    const value = trace.snapshot();
    expect(value.case_ordinal).toBe(2);
    expect(value.counts.meta).toEqual([1, 1, 0]);
    expect(value.tail[0][3]).toBe('o1');
    expect(value.last_readback_observation).toBeNull();
  });
  test('does not register listeners without an observer or expose unknown operation text', () => {
    observeResponse(undefined, 'meta', {});
    const { trace } = fixture();
    trace.start('PRIVATE_BAD_OPERATION')(500);
    expect(trace.snapshot().overflow).toBe(true);
    expect(JSON.stringify(trace.snapshot())).not.toContain('PRIVATE_BAD');
  });
});
