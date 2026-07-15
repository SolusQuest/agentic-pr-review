import { describe, expect, it } from 'vitest';
import * as pub from './index.js';
import type {
  BundleClassification,
  BuildResult,
  BuildStateBundleV2Result,
  CompatibilityOutcome,
  DiagnosticCode,
  EntryDescriptor,
  EpochId,
  GitSha,
  Sha256Hex,
  StateManifestV2,
  StateManifestV2Input,
  StateManifestV2Transition,
  ValidationResult,
  InvalidDiagnosticCode,
  UnsupportedLegacyDiagnostic,
  StateManifestSerializationDiagnostic,
  StateManifestSerializationReason,
} from './index.js';

// The public surface of src/state-v2/index.ts is the only supported way for
// sibling packages to consume this library. This test type-asserts that the
// AC-visible names still exist and remain assignable — a hard type error
// here breaks the compile and is the intended signal for API drift.
describe('state-v2 public surface', () => {
  it('exports the AC-visible functions', () => {
    // Runtime existence is a sanity check; typechecking is what enforces the
    // real contract.
    expect(typeof pub.buildStateBundleV2).toBe('function');
    expect(typeof pub.classifyStateBundleV2).toBe('function');
    expect(typeof pub.checkStateManifestV2Compatibility).toBe('function');
    expect(typeof pub.serializeStateManifestV2).toBe('function');
    expect(typeof pub.validateStateManifestV2).toBe('function');
    expect(typeof pub.crossFieldValidate).toBe('function');
    expect(typeof pub.semanticIdentityValidate).toBe('function');
    expect(typeof pub.canonicalJsonBytes).toBe('function');
  });

  it('exports the AC-visible error classes', () => {
    expect(pub.BuilderValidationError).toBeInstanceOf(Function);
    expect(pub.BuilderInputRejectedError).toBeInstanceOf(Function);
    expect(pub.LedgerOverBoundError).toBeInstanceOf(Function);
    expect(pub.MetadataOverBoundError).toBeInstanceOf(Function);
    expect(pub.StateManifestSerializationError).toBeInstanceOf(Function);
    expect(pub.CanonicalJsonInputError).toBeInstanceOf(Function);
  });

  it('BuildResult and BuildStateBundleV2Result are assignable to each other', () => {
    type Both = BuildResult extends BuildStateBundleV2Result ? true : false;
    type Reverse = BuildStateBundleV2Result extends BuildResult ? true : false;
    const both: Both = true;
    const reverse: Reverse = true;
    expect(both && reverse).toBe(true);
  });

  it('branded types cannot be assigned from bare string literals without a cast', () => {
    // These lines must *not* compile without an explicit brand cast. The
    // type assertions here document the intended shape; the presence of the
    // `as` cast is what makes them assignable.
    const epoch: EpochId = 'AAAAAAAAAAAAAAAAAAAAAA' as EpochId;
    const sha: Sha256Hex = 'a'.repeat(64) as Sha256Hex;
    const gitSha: GitSha = 'a'.repeat(40) as GitSha;
    expect(epoch.length).toBe(22);
    expect(sha.length).toBe(64);
    expect(gitSha.length).toBe(40);
  });

  it('re-exports every AC-required type name', () => {
    // Compile-only asserts. If any of these type imports get renamed or
    // removed from the public index, `tsc --noEmit` will fail before the
    // test suite ever runs.
    const _typeCheck: [
      BundleClassification | undefined,
      DiagnosticCode | undefined,
      EntryDescriptor | undefined,
      StateManifestV2 | undefined,
      StateManifestV2Input | undefined,
      StateManifestV2Transition | undefined,
      ValidationResult | undefined,
      CompatibilityOutcome | undefined,
    ] = [undefined, undefined, undefined, undefined, undefined, undefined, undefined, undefined];
    void _typeCheck;
    expect(true).toBe(true);
  });
  it('BundleClassification.invalid.diagnostic is narrowed to InvalidDiagnosticCode (no legacy code allowed)', () => {
    type InvalidBranchDiag = Extract<BundleClassification, { kind: 'invalid' }>['diagnostic'];
    const okDiag: InvalidBranchDiag = 'manifest_shape_invalid';
    expect(okDiag).toBe('manifest_shape_invalid');
    type LegacyOnlyIsExcluded = UnsupportedLegacyDiagnostic extends InvalidBranchDiag
      ? false
      : true;
    const excluded: LegacyOnlyIsExcluded = true;
    expect(excluded).toBe(true);
  });

  it('StateManifestSerializationReason is EXACTLY the four-member union (bidirectional structural equality)', () => {
    const reasons: readonly StateManifestSerializationReason[] = [
      'manifest_shape_invalid',
      'manifest_unknown_field',
      'manifest_unknown_version',
      'canonical_json_input_rejected',
    ];
    expect(reasons.length).toBe(4);
    type Expected =
      | 'manifest_shape_invalid'
      | 'manifest_unknown_field'
      | 'manifest_unknown_version'
      | 'canonical_json_input_rejected';
    type Exact = [StateManifestSerializationReason] extends [Expected]
      ? [Expected] extends [StateManifestSerializationReason]
        ? true
        : false
      : false;
    const exact: Exact = true;
    expect(exact).toBe(true);
  });

  it('canonicalJsonBytes public overload accepts CanonicalJsonValue without a manual cast', () => {
    const value: import('./index.js').CanonicalJsonValue = { a: 1, b: [null, true, 'x'] };
    const bytes = pub.canonicalJsonBytes(value);
    expect(bytes.byteLength).toBeGreaterThan(0);
    const unk: unknown = { c: 42 };
    const bytes2 = pub.canonicalJsonBytes(unk);
    expect(bytes2.byteLength).toBeGreaterThan(0);
  });

  it('InvalidDiagnosticCode structurally excludes state_unsupported_legacy_v1', () => {
    type Excludes = 'state_unsupported_legacy_v1' extends InvalidDiagnosticCode ? false : true;
    const c: Excludes = true;
    expect(c).toBe(true);
  });

  it('StateManifestSerializationDiagnostic is EXACTLY the three-member union (bidirectional structural equality)', () => {
    const diagnostics: readonly StateManifestSerializationDiagnostic[] = [
      'manifest_shape_invalid',
      'manifest_unknown_field',
      'manifest_unknown_version',
    ];
    expect(diagnostics.length).toBe(3);
    // Compile-only: assignable to InvalidDiagnosticCode.
    type Assignable = StateManifestSerializationDiagnostic extends InvalidDiagnosticCode
      ? true
      : false;
    const a: Assignable = true;
    expect(a).toBe(true);
    // Compile-only: bidirectional structural equality with the intended
    // three-member union. Removal, rename, or expansion breaks the build.
    type Expected =
      | 'manifest_shape_invalid'
      | 'manifest_unknown_field'
      | 'manifest_unknown_version';
    type Exact = [StateManifestSerializationDiagnostic] extends [Expected]
      ? [Expected] extends [StateManifestSerializationDiagnostic]
        ? true
        : false
      : false;
    const exact: Exact = true;
    expect(exact).toBe(true);
  });
});
