import { describe, expect, it } from 'vitest';
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

  it('rejects empty, duplicate, and over-bound values before asynchronous work', () => {
    for (const values of [
      [''],
      ['same', 'same'],
      Array.from({ length: 65 }, (_, index) => String(index)),
    ]) {
      expect(() => copySensitiveValues(values)).toThrow(LiveRuntimeInvocationError);
    }
  });
});
