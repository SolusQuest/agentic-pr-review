// Closed, bounded external-service observations, never C# semantic readback.
import { performance } from 'node:perf_hooks';
const operations = new Set([
  'sdk_create',
  'sdk_finalize',
  'blob_block',
  'blob_commit',
  'list',
  'meta',
  'zip_redirect',
  'zip_body',
  'delete',
  'producer_run',
  'sticky_post',
  'sticky_patch',
  'sticky_read',
]);
const mutations = new Set(['sdk_finalize', 'delete', 'sticky_post', 'sticky_patch']);
const reads = new Set(['meta', 'zip_body', 'producer_run']);
export function createOperationTrace({
  now = () => performance.now(),
  utc = () => new Date().toISOString(),
} = {}) {
  const aliases = new Map();
  const active = new Map();
  let generation = 0,
    sequence = 0,
    started = 0,
    startedAt = null,
    overflow = false;
  let tail = [],
    counts = {},
    lastCompleted = null,
    lastMutation = null,
    lastUpload = null,
    lastRead = null;
  return {
    beginCase() {
      generation++;
      sequence = 0;
      started = now();
      startedAt = utc();
      overflow = false;
      tail = [];
      counts = {};
      lastCompleted = lastMutation = lastUpload = lastRead = null;
    },
    start(operation, objectId = null) {
      if (!generation || !operations.has(operation) || sequence >= 20000 || active.size >= 64) {
        overflow = true;
        return () => {};
      }
      let alias = '';
      if (objectId !== null) {
        const key = String(objectId);
        if (!aliases.has(key) && aliases.size < 4096) aliases.set(key, 'o' + (aliases.size + 1));
        alias = aliases.get(key) ?? 'overflow';
        if (alias === 'overflow') overflow = true;
      }
      const ownGeneration = generation,
        ordinal = ++sequence,
        at = now();
      const token = {};
      const entry = [ordinal, Math.max(0, Math.round(at - started)), operation, alias];
      active.set(token, { generation, entry });
      const counter = (counts[operation] ??= [0, 0, 0]);
      counter[0]++;
      let done = false;
      return (status, phase = 'finished') => {
        if (done) return;
        done = true;
        active.delete(token);
        if (ownGeneration !== generation) return;
        const code = Number.isInteger(status) && status >= 100 && status <= 599 ? status : 0;
        const terminal = phase === 'finished' ? 'finished' : 'closed';
        counter[terminal === 'finished' ? 1 : 2]++;
        const row = [
          ordinal,
          Math.max(0, Math.round(now() - started)),
          operation,
          alias,
          code,
          Math.max(0, Math.round(now() - at)),
          terminal,
        ];
        tail.push(row);
        if (tail.length > 12) tail.shift();
        if (terminal === 'finished') {
          lastCompleted = row;
          if (code >= 200 && code < 300) {
            if (mutations.has(operation)) lastMutation = row;
            if (operation === 'sdk_finalize') lastUpload = row;
            if (reads.has(operation)) lastRead = row;
          }
        }
      };
    },
    snapshot() {
      return structuredClone({
        kind: 'external-service-operation-tail-v1',
        case_ordinal: generation,
        started_at: startedAt,
        elapsed_ms: Math.max(0, Math.round(now() - started)),
        overflow,
        counts,
        tail,
        last_completed: lastCompleted,
        last_mutation: lastMutation,
        last_sdk_finalize: lastUpload,
        last_readback_observation: lastRead,
        in_flight: [...active.values()]
          .filter((x) => x.generation === generation)
          .slice(-8)
          .map((x) => x.entry),
        in_flight_total: [...active.values()].filter((x) => x.generation === generation).length,
        prior_case_in_flight: [...active.values()].filter((x) => x.generation !== generation)
          .length,
      });
    },
  };
}
export function observeResponse(trace, operation, response, objectId = null) {
  if (!trace) return;
  const finish = trace.start(operation, objectId);
  response.once('finish', () => finish(response.statusCode, 'finished'));
  response.once('close', () => finish(response.statusCode, 'closed'));
}
