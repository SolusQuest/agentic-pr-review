import { MAX_SENSITIVE_VALUES, MAX_SENSITIVE_VALUES_TOTAL_UTF8_BYTES } from './constants.js';
import { LiveRuntimeInvocationError } from './errors.js';

export function copySensitiveValues(values: readonly string[] | undefined): readonly Uint8Array[] {
  const source = values ? [...values] : [];
  if (source.length > MAX_SENSITIVE_VALUES || source.some((value) => value.length === 0)) {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'sensitiveValues is empty or exceeds its entry cap.',
    });
  }
  const encoded = source.map((value) => new TextEncoder().encode(value));
  const total = encoded.reduce((sum, value) => sum + value.byteLength, 0);
  if (total > MAX_SENSITIVE_VALUES_TOTAL_UTF8_BYTES || new Set(source).size !== source.length) {
    throw new LiveRuntimeInvocationError({
      kind: 'options-invalid',
      message: 'sensitiveValues exceeds its byte cap or contains duplicates.',
    });
  }
  return encoded.map((value) => new Uint8Array(value));
}

export function assertPrivateBytes(
  channels: readonly Uint8Array[],
  sensitiveValues: readonly Uint8Array[],
): void {
  const matcher = buildMatcher(sensitiveValues);
  for (const channel of channels) {
    if (matcher.contains(channel)) {
      throw new LiveRuntimeInvocationError({
        kind: 'privacy-violation',
        message: 'Sensitive content crossed the live runtime boundary.',
      });
    }
  }
}

interface MatcherNode {
  readonly next: Map<number, number>;
  fail: number;
  terminal: boolean;
}

interface ByteMatcher {
  contains(channel: Uint8Array): boolean;
}

function buildMatcher(sensitiveValues: readonly Uint8Array[]): ByteMatcher {
  const nodes: MatcherNode[] = [{ next: new Map(), fail: 0, terminal: false }];
  for (const secret of sensitiveValues) {
    if (secret.byteLength === 0) continue;
    let node = 0;
    for (const byte of secret) {
      let next = nodes[node].next.get(byte);
      if (next === undefined) {
        next = nodes.length;
        nodes[node].next.set(byte, next);
        nodes.push({ next: new Map(), fail: 0, terminal: false });
      }
      node = next;
    }
    nodes[node].terminal = true;
  }

  const queue: number[] = [];
  for (const child of nodes[0].next.values()) queue.push(child);
  for (let head = 0; head < queue.length; head += 1) {
    const node = queue[head];
    for (const [byte, child] of nodes[node].next) {
      let fallback = nodes[node].fail;
      while (fallback !== 0 && !nodes[fallback].next.has(byte)) {
        fallback = nodes[fallback].fail;
      }
      nodes[child].fail = nodes[fallback].next.get(byte) ?? 0;
      nodes[child].terminal ||= nodes[nodes[child].fail].terminal;
      queue.push(child);
    }
  }

  return {
    contains(channel) {
      let node = 0;
      for (const byte of channel) {
        while (node !== 0 && !nodes[node].next.has(byte)) node = nodes[node].fail;
        node = nodes[node].next.get(byte) ?? 0;
        if (nodes[node].terminal) return true;
      }
      return false;
    },
  };
}
