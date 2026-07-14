import { describe, expect, it } from 'vitest';
import {
  buildStateBundleV2,
  crossFieldValidate,
  validateStateManifestV2,
  type EpochId,
  type StateManifestV2,
  type StateManifestV2Transition,
} from './index.js';
import { makeStateManifestV2Input, sha256Hex } from './test-helpers.js';

const LEDGER = new TextEncoder().encode('ledger');
const METADATA = new TextEncoder().encode('metadata');
const PRED_MANIFEST = sha256Hex('pred-manifest');
const PRED_LEDGER = sha256Hex('pred-ledger');

function build(overrides?: Parameters<typeof makeStateManifestV2Input>[0]) {
  return buildStateBundleV2(makeStateManifestV2Input(overrides), LEDGER, METADATA);
}

function invalidate(
  manifest: StateManifestV2,
  patch: (m: StateManifestV2) => void,
): { message: string; codes: string[] } {
  const clone = structuredClone(manifest) as StateManifestV2;
  patch(clone);
  const errs = crossFieldValidate(clone);
  const validation = validateStateManifestV2(clone);
  const message = validation.ok ? '' : validation.message;
  return { message, codes: errs.map((e) => e.split(':')[0]) };
}

describe('cross-field validation matrix', () => {
  it('x_state_namespace_mismatch when stateKey.namespace differs from stateNamespace', () => {
    const built = build();
    const result = invalidate(built.manifest, (m) => {
      (m.stateKey as unknown as Record<string, unknown>).namespace = 'other';
    });
    expect(result.codes).toContain('x_state_namespace_mismatch');
  });

  it('x_metadata_producing_state_generation when producing generation disagrees', () => {
    const built = build();
    const result = invalidate(built.manifest, (m) => {
      m.providerRunMetadata.producingGeneration.stateGeneration = 99;
    });
    expect(result.codes).toContain('x_metadata_producing_state_generation');
  });

  it('bootstrap must have stateGeneration 0 and ordinal 0', () => {
    const built = build();
    const result = invalidate(built.manifest, (m) => {
      m.generation.stateGeneration = 1;
      m.transaction.interactionOrdinal = 1;
      m.providerRunMetadata.producingGeneration.stateGeneration = 1;
    });
    expect(result.codes).toContain('x_bootstrap_generation_nonzero');
    expect(result.codes).toContain('x_bootstrap_ordinal_nonzero');
  });

  const continuation: StateManifestV2Transition = {
    kind: 'continuation',
    predecessorManifestSha256: PRED_MANIFEST,
    predecessorLedgerSha256: PRED_LEDGER,
    predecessorStateGeneration: 4,
    predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId,
  };

  it('valid continuation: predecessor+1 == generation and ordinal >= 1', () => {
    const built = build({
      transition: continuation,
      generation: { stateGeneration: 5, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId },
      transaction: { interactionOrdinal: 1 },
    });
    const result = validateStateManifestV2(built.manifest);
    expect(result.ok).toBe(true);
  });

  it('continuation rejects zero ordinal', () => {
    const input = makeStateManifestV2Input({
      transition: continuation,
      generation: { stateGeneration: 5, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId },
      transaction: { interactionOrdinal: 0 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow();
  });

  it('continuation rejects predecessor epoch mismatch', () => {
    const input = makeStateManifestV2Input({
      transition: {
        ...continuation,
        predecessorLedgerEpoch: 'BBBBBBBBBBBBBBBBBBBBBB' as EpochId,
      },
      generation: { stateGeneration: 5, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId },
      transaction: { interactionOrdinal: 1 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow();
  });

  const reset: StateManifestV2Transition = {
    kind: 'reset',
    predecessorManifestSha256: PRED_MANIFEST,
    predecessorLedgerSha256: PRED_LEDGER,
    predecessorStateGeneration: 4,
    predecessorLedgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId,
    reason: 'base_change',
  };

  it('valid reset: fresh ledgerEpoch and ordinal 0', () => {
    const built = build({
      transition: reset,
      generation: { stateGeneration: 5, ledgerEpoch: 'BBBBBBBBBBBBBBBBBBBBBB' as EpochId },
      transaction: { interactionOrdinal: 0 },
    });
    const result = validateStateManifestV2(built.manifest);
    expect(result.ok).toBe(true);
  });

  it('reset rejects same ledger epoch', () => {
    const input = makeStateManifestV2Input({
      transition: reset,
      generation: { stateGeneration: 5, ledgerEpoch: 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId },
      transaction: { interactionOrdinal: 0 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow();
  });

  it('reset rejects nonzero ordinal', () => {
    const input = makeStateManifestV2Input({
      transition: reset,
      generation: { stateGeneration: 5, ledgerEpoch: 'BBBBBBBBBBBBBBBBBBBBBB' as EpochId },
      transaction: { interactionOrdinal: 3 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow();
  });

  it('recovery_root must have stateGeneration 0 and ordinal 0', () => {
    const input = makeStateManifestV2Input({
      transition: {
        kind: 'recovery_root',
        predecessorManifestSha256: 'bootstrap',
        predecessorLedgerSha256: 'bootstrap',
        reason: 'corrupt_accepted_artifact',
      },
      generation: { stateGeneration: 3, ledgerEpoch: 'CCCCCCCCCCCCCCCCCCCCCC' as EpochId },
      transaction: { interactionOrdinal: 1 },
    });
    expect(() => buildStateBundleV2(input, LEDGER, METADATA)).toThrow();
  });
});
