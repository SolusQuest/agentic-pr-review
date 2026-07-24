import { describe, expect, it } from 'vitest';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { LiveRuntimeInvocationError } from './errors.js';
import { copySensitiveValues, assertPrivateBytes } from './privacy.js';

describe('live runtime privacy barriers', () => {
  it('copies values and scans literal UTF-8 subsequences', () => {
    const values = copySensitiveValues(['credential-秘密']);
    expect(() =>
      assertPrivateBytes([new TextEncoder().encode('prefix credential-秘密 suffix')], values),
    ).toThrow(LiveRuntimeInvocationError);
    expect(() =>
      assertPrivateBytes([new TextEncoder().encode('credential-\u79d8')], values),
    ).not.toThrow();
  });

  it('still rejects arbitrary sensitive values at every serialized channel', () => {
    const values = copySensitiveValues(['null']);
    expect(() =>
      assertPrivateBytes([new TextEncoder().encode('{"requestedRuntimeVersion":null}')], values),
    ).toThrow(LiveRuntimeInvocationError);
  });

  it('rejects empty, duplicate, and over-bound values before asynchronous work', () => {
    for (const values of [
      [''],
      ['same', 'same'],
      Array.from({ length: 65 }, (_, index) => String(index)),
    ]) {
      expect(() => copySensitiveValues(values)).toThrow(LiveRuntimeInvocationError);
    }
  });

  it('scans overlapping sensitive prefixes with one multi-pattern pass', () => {
    const values = copySensitiveValues(
      Array.from({ length: 64 }, (_, index) => `${'a'.repeat(index + 1)}b`),
    );
    expect(() =>
      assertPrivateBytes([new TextEncoder().encode('a'.repeat(100_000))], values),
    ).not.toThrow();
    expect(() =>
      assertPrivateBytes([new TextEncoder().encode(`${'a'.repeat(63)}b`)], values),
    ).toThrow(LiveRuntimeInvocationError);
  });

  it('scans the canonical JSON-escaped form of sensitive values', () => {
    const values = copySensitiveValues(['quote" and newline\n']);
    const serialized = canonicalJsonBytes({ value: 'prefix quote" and newline\n suffix' });
    expect(() => assertPrivateBytes([serialized], values)).toThrow(LiveRuntimeInvocationError);
  });
});
