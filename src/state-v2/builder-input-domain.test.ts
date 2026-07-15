import { describe, expect, it } from 'vitest';
import {
  BuilderInputRejectedError,
  BuilderValidationError,
  LedgerOverBoundError,
  MetadataOverBoundError,
  buildStateBundleV2,
  type StateManifestV2Input,
} from './index.js';
import { makeStateManifestV2Input } from './test-helpers.js';
import { LEDGER_MAX_BYTES, METADATA_MAX_BYTES } from './constants.js';

const LEDGER = new TextEncoder().encode('ledger');
const METADATA = new TextEncoder().encode('metadata');

function baseInput(): StateManifestV2Input {
  return makeStateManifestV2Input();
}

describe('buildStateBundleV2 input-domain rejection (canonical accepted domain)', () => {
  it('rejects enumerable getter without executing it', () => {
    const input = baseInput();
    let getterCalled = false;
    Object.defineProperty(input.stateKey, 'repository', {
      configurable: true,
      enumerable: true,
      get: () => {
        getterCalled = true;
        return 'SolusQuest/agentic-pr-review';
      },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow(BuilderInputRejectedError);
    expect(getterCalled).toBe(false);
  });

  it('rejects a symbol-keyed property on an accepted-looking object', () => {
    const input = baseInput() as unknown as Record<string | symbol, unknown>;
    (input.stateKey as unknown as Record<string | symbol, unknown>)[Symbol('hidden')] = 'x';
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a non-enumerable property', () => {
    const input = baseInput();
    Object.defineProperty(input.stateKey, 'hidden', {
      configurable: false,
      enumerable: false,
      writable: false,
      value: 'x',
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow(BuilderInputRejectedError);
  });

  it('rejects a class instance with a non-Object prototype', () => {
    class Fancy {
      value = 1;
    }
    const input = baseInput() as unknown as { fancy?: Fancy };
    (input as unknown as { fancy: Fancy }).fancy = new Fancy();
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a sparse array', () => {
    const input = baseInput() as unknown as { spare?: unknown[] };
    // eslint-disable-next-line no-sparse-arrays
    input.spare = [1, , 3];
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a cyclic structure', () => {
    const input = baseInput() as unknown as Record<string, unknown>;
    (input.stateKey as unknown as Record<string, unknown>).self = input.stateKey;
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a non-finite number', () => {
    const input = baseInput() as unknown as { generation: { stateGeneration: number } };
    input.generation.stateGeneration = Number.POSITIVE_INFINITY;
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a NaN number', () => {
    const input = baseInput() as unknown as { generation: { stateGeneration: number } };
    input.generation.stateGeneration = Number.NaN;
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderInputRejectedError);
  });

  it('rejects a lone high surrogate inside a string value at builder step 3 (shared string-safety traversal)', () => {
    const input = baseInput() as unknown as { cacheContractIdentity: { providerId: string } };
    input.cacheContractIdentity.providerId = 'anthropic-\uD800';
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderValidationError);
  });

  it('rejects a lone low surrogate inside a string value at builder step 3 (shared string-safety traversal)', () => {
    const input = baseInput() as unknown as { cacheContractIdentity: { providerId: string } };
    input.cacheContractIdentity.providerId = 'anthropic-\uDC00';
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderValidationError);
  });

  it('rejects a lone surrogate as a property name at builder step 3 (shared string-safety traversal)', () => {
    const input = baseInput() as unknown as { stateKey: Record<string, unknown> };
    input.stateKey['bad-\uD800-key'] = 'x';
    expect(() =>
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA),
    ).toThrow(BuilderValidationError);
  });

  it('BuilderInputRejectedError.message does not leak the caller property name', () => {
    const secretName = 'top-secret-accessor-name-that-should-not-appear-anywhere';
    const input = baseInput() as unknown as { stateKey: Record<string, unknown> };
    Object.defineProperty(input.stateKey, secretName, {
      configurable: true,
      enumerable: true,
      get: () => 'x',
    });
    try {
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA);
      throw new Error('expected BuilderInputRejectedError');
    } catch (err) {
      expect(err).toBeInstanceOf(BuilderInputRejectedError);
      if (err instanceof BuilderInputRejectedError) {
        expect(err.message).not.toContain(secretName);
        expect(err.path).not.toContain(secretName);
        // Path is collapsed to structural placeholders only.
        // path may collapse into either `<segment>` or a raw structural path,
        // depending on which stage raised. The security invariant is that
        // the secretName never leaks.
        expect(err.path).not.toContain(secretName);
      }
    }
  });

  it('BuilderInputRejectedError.message stays within the diagnostic byte cap', () => {
    // A pathological caller shaping deeply-nested own properties with
    // secret-like names must not produce an unbounded builder message.
    const input = baseInput() as unknown as Record<string, unknown>;
    const longKey = 'k'.repeat(500);
    Object.defineProperty(input, longKey, {
      configurable: true,
      enumerable: true,
      get: () => 'x',
    });
    try {
      buildStateBundleV2(input as unknown as StateManifestV2Input, LEDGER, METADATA);
      throw new Error('expected BuilderInputRejectedError');
    } catch (err) {
      expect(err).toBeInstanceOf(BuilderInputRejectedError);
      if (err instanceof BuilderInputRejectedError) {
        expect(new TextEncoder().encode(err.message).byteLength).toBeLessThanOrEqual(1024);
        expect(err.message).not.toContain(longKey);
      }
    }
  });

  it('cap check runs before any input-object traversal (ledger over cap)', () => {
    const input = baseInput();
    // Poison the input so canonical-domain traversal would reject it if it ran.
    Object.defineProperty(input.stateKey, 'repository', {
      configurable: true,
      enumerable: true,
      get: () => 'nope',
    });
    const oversized = new Uint8Array(LEDGER_MAX_BYTES + 1);
    expect(() => buildStateBundleV2(input, oversized, METADATA)).toThrow(LedgerOverBoundError);
  });

  it('cap check runs before any input-object traversal (metadata over cap)', () => {
    const input = baseInput();
    Object.defineProperty(input.stateKey, 'repository', {
      configurable: true,
      enumerable: true,
      get: () => 'nope',
    });
    const oversized = new Uint8Array(METADATA_MAX_BYTES + 1);
    expect(() => buildStateBundleV2(input, LEDGER, oversized)).toThrow(MetadataOverBoundError);
  });
});
