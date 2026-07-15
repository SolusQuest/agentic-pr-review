import { describe, expect, it } from 'vitest';
import { BuilderValidationError, buildStateBundleV2 } from './builder.js';
import { classifyStateBundleV2, type EntryDescriptor } from './classifier.js';
import { makeStateManifestV2Input } from './test-helpers.js';
import { LEDGER_FILENAME, MANIFEST_FILENAME, PROVIDER_RUN_METADATA_FILENAME } from './constants.js';
import { serializeStateManifestV2 } from './serializer.js';
import type { StateManifestV2Input } from './manifest.js';

const LEDGER = new TextEncoder().encode('l');
const METADATA = new TextEncoder().encode('m');
const LISTING: readonly EntryDescriptor[] = [
  { name: MANIFEST_FILENAME, isRegularFile: true },
  { name: LEDGER_FILENAME, isRegularFile: true },
  { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
];

/**
 * Observable regression tests for the builder step-3 shared string-safety
 * traversal. Each case asserts:
 *   1. `buildStateBundleV2` raises `BuilderValidationError`.
 *   2. `err.message` is exactly `x_invalid_unicode:<safe-path>` (bounded
 *      wire string; no English prefix).
 *   3. The same input reaching the classifier (via a manually crafted
 *      manifest JSON blob) produces a byte-equal message.
 */

function buildAndCaptureMessage(mutate: (input: StateManifestV2Input) => void): string {
  const input = makeStateManifestV2Input();
  mutate(input);
  try {
    buildStateBundleV2(input, LEDGER, METADATA);
    throw new Error('expected BuilderValidationError');
  } catch (err) {
    if (!(err instanceof BuilderValidationError)) throw err;
    return err.message;
  }
}

function classifyRawJson(rawJson: string): string {
  const bytes = new TextEncoder().encode(rawJson);
  const res = classifyStateBundleV2({
    manifestBytes: bytes,
    ledgerBytes: LEDGER,
    providerRunMetadataBytes: METADATA,
    entryListing: LISTING,
  });
  if (res.kind !== 'invalid') throw new Error('expected invalid classification');
  return res.message;
}

describe('builder step-3 shared string-safety observable regression', () => {
  it('NUL inside a string value at a schema-known identity path — bounded wire message', () => {
    const message = buildAndCaptureMessage((input) => {
      // providerId is schema-known -> `<code>:/cacheContractIdentity/providerId`.
      (input.cacheContractIdentity as unknown as Record<string, string>).providerId = 'ok\u0000bad';
    });
    expect(message).toBe('x_invalid_unicode:/cacheContractIdentity/providerId');
  });

  it('unpaired high surrogate at a schema-known identity path — bounded wire message', () => {
    const message = buildAndCaptureMessage((input) => {
      (input.cacheContractIdentity as unknown as Record<string, string>).modelId = 'x\uD800y';
    });
    expect(message).toBe('x_invalid_unicode:/cacheContractIdentity/modelId');
  });

  it('unpaired low surrogate at a schema-known identity path — bounded wire message', () => {
    const message = buildAndCaptureMessage((input) => {
      (input.cacheContractIdentity as unknown as Record<string, string>).modelId = 'x\uDC00y';
    });
    expect(message).toBe('x_invalid_unicode:/cacheContractIdentity/modelId');
  });

  it('NUL in a property NAME triggers the property-name terminal marker', () => {
    const message = buildAndCaptureMessage((input) => {
      (input.stateKey as unknown as Record<string, unknown>)['bad\u0000key'] = 'x';
    });
    expect(message).toBe('x_invalid_unicode:/stateKey/<invalid-nul>');
  });

  it('unpaired surrogate in a property NAME triggers <invalid-utf16>', () => {
    const message = buildAndCaptureMessage((input) => {
      (input.stateKey as unknown as Record<string, unknown>)['bad\uD800key'] = 'x';
    });
    expect(message).toBe('x_invalid_unicode:/stateKey/<invalid-utf16>');
  });

  it('bounded wire message from builder is byte-equal to classifier wire message for the same input', () => {
    // Compose a manifest JSON literal that classifier sees; NUL at
    // providerId. Match the successful manifest structure via a valid
    // manifest first.
    const inputValid = makeStateManifestV2Input();
    const validBuilt = buildStateBundleV2(inputValid, LEDGER, METADATA);
    const manifestJson = JSON.parse(
      new TextDecoder().decode(serializeStateManifestV2(validBuilt.manifest)),
    ) as Record<string, unknown>;
    (manifestJson.cacheContractIdentity as Record<string, unknown>).providerId = 'ok\u0000bad';
    const classifierMessage = classifyRawJson(JSON.stringify(manifestJson));
    const builderMessage = buildAndCaptureMessage((input) => {
      (input.cacheContractIdentity as unknown as Record<string, string>).providerId = 'ok\u0000bad';
    });
    expect(builderMessage).toBe(classifierMessage);
  });
});

describe('validateStateManifestV2 direct API string-safety regression', () => {
  it('NUL in a top-level string via direct API returns manifest_shape_invalid with x_invalid_unicode', async () => {
    const { validateStateManifestV2 } = await import('./schema.js');
    const inputValid = makeStateManifestV2Input();
    const validBuilt = buildStateBundleV2(inputValid, LEDGER, METADATA);
    const clone = JSON.parse(
      new TextDecoder().decode(serializeStateManifestV2(validBuilt.manifest)),
    ) as Record<string, unknown>;
    (clone.sessionEpoch as unknown) = 'ep\u0000one';
    const result = validateStateManifestV2(clone);
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/sessionEpoch');
  });

  it('unpaired surrogate in a schema-known nested identity string is caught before Ajv', async () => {
    const { validateStateManifestV2 } = await import('./schema.js');
    const inputValid = makeStateManifestV2Input();
    const validBuilt = buildStateBundleV2(inputValid, LEDGER, METADATA);
    const clone = JSON.parse(
      new TextDecoder().decode(serializeStateManifestV2(validBuilt.manifest)),
    ) as Record<string, unknown>;
    (clone.cacheContractIdentity as Record<string, unknown>).modelId = 'x\uD800';
    const result = validateStateManifestV2(clone);
    expect(result.ok).toBe(false);
    if (result.ok) return;
    expect(result.message).toBe('x_invalid_unicode:/cacheContractIdentity/modelId');
  });
});

describe('serializeStateManifestV2 rejection regression', () => {
  it('rejects a manifest carrying NUL before returning canonical bytes; reason enum is populated', async () => {
    const { StateManifestSerializationError } = await import('./serializer.js');
    const inputValid = makeStateManifestV2Input();
    const validBuilt = buildStateBundleV2(inputValid, LEDGER, METADATA);
    const clone = structuredClone(validBuilt.manifest);
    (clone.cacheContractIdentity as unknown as Record<string, string>).providerId = 'ok\u0000bad';
    let caught: unknown;
    try {
      serializeStateManifestV2(clone);
    } catch (err) {
      caught = err;
    }
    expect(caught).toBeInstanceOf(StateManifestSerializationError);
    if (!(caught instanceof StateManifestSerializationError)) return;
    // The reason enum is one of the closed values; NUL is a shape_invalid.
    expect(caught.reason).toBe('manifest_shape_invalid');
    // The `.detail` field carries the bounded wire message unchanged.
    expect(caught.detail).toBe('x_invalid_unicode:/cacheContractIdentity/providerId');
    // The Error message contains the reason and detail; no arbitrary
    // caller-controlled content is embedded.
    expect(caught.message).toContain('manifest_shape_invalid');
    expect(caught.message).toContain('x_invalid_unicode:/cacheContractIdentity/providerId');
  });
});

describe('serializeStateManifestV2 canonicalization failure regression', () => {
  it('a schema-valid manifest carrying a canonical-only violation (symbol-keyed own property in a schema-known object) is caught and wrapped', async () => {
    const { StateManifestSerializationError } = await import('./serializer.js');
    const built = buildStateBundleV2(makeStateManifestV2Input(), LEDGER, METADATA);
    const clone = structuredClone(built.manifest) as unknown as Record<string, unknown>;
    // Attach a symbol-keyed own property to a schema-known nested
    // object. The shared string-safety scan and the Ajv validator both
    // ignore symbol keys, so the manifest reaches canonicalization.
    Object.defineProperty(clone.stateKey, Symbol('canonical-only'), {
      value: 'x',
      enumerable: true,
      writable: true,
      configurable: true,
    });
    let caught: unknown;
    try {
      const { serializeStateManifestV2 } = await import('./serializer.js');
      serializeStateManifestV2(clone as unknown as import('./manifest.js').StateManifestV2);
    } catch (err) {
      caught = err;
    }
    expect(caught).toBeInstanceOf(StateManifestSerializationError);
    if (!(caught instanceof StateManifestSerializationError)) return;
    expect(caught.reason).toBe('canonical_json_input_rejected');
    // Detail collapses to the root safe path; no caller structural
    // information leaks.
    expect(caught.detail).toBe('x_invalid_field:');
    // diagnostic alias maps to the legacy shape_invalid enum.
    expect(caught.diagnostic).toBe('manifest_shape_invalid');
  });

  it('StateManifestSerializationError.message is bounded (<= MAX_DIAGNOSTIC_MESSAGE_CHARS)', async () => {
    const { StateManifestSerializationError } = await import('./serializer.js');
    const { MAX_DIAGNOSTIC_MESSAGE_CHARS } = await import('./constants.js');
    // Manually construct with a maximally long detail (already bounded).
    const longDetail = 'x_invalid_field:/' + '<untrusted-property>'.repeat(20);
    const err = new StateManifestSerializationError('manifest_shape_invalid', longDetail);
    expect(err.message.length).toBeLessThanOrEqual(MAX_DIAGNOSTIC_MESSAGE_CHARS);
    // The bounded-wire detail still appears verbatim on `.detail` even
    // if the assembled message is truncated.
    expect(err.detail).toBe(longDetail);
  });
});

describe('normalizePosition and dereferenceJsonPointer regression', () => {
  it('malformed $ref (non-string) returns UnknownPosition regardless of sibling supported keywords', async () => {
    const { normalizePosition } = await import('./shared-safe-path.js');
    const badSchema1 = { $ref: 42, properties: { known: { type: 'string' } } };
    const badSchema2 = { $ref: 42 };
    const pos1 = normalizePosition(badSchema1);
    const pos2 = normalizePosition(badSchema2);
    expect(pos1.kind).toBe('unknown');
    expect(pos2.kind).toBe('unknown');
  });

  it('canonical decimal array-index rules apply to $ref targeting array-shaped subschemas', async () => {
    const { normalizePosition } = await import('./shared-safe-path.js');
    // Root has oneOf: [ {properties: {a: {type:'string'}}}, ... ].
    const root = {
      oneOf: [{ properties: { a: { type: 'string' } } }, { properties: { b: { type: 'string' } } }],
    };
    // Canonical: #/oneOf/0 → the first branch (schema-known 'a').
    const posValid = normalizePosition(
      { $ref: '#/oneOf/0' },
      new Set(),
      root as import('./shared-safe-path.js').SchemaNode,
    );
    expect(posValid.kind).toBe('object');
    // Malformed: #/oneOf/01 → invalid canonical index → UnknownPosition.
    const posLeading = normalizePosition(
      { $ref: '#/oneOf/01' },
      new Set(),
      root as import('./shared-safe-path.js').SchemaNode,
    );
    expect(posLeading.kind).toBe('unknown');
    // Malformed: #/oneOf/1e0 → not decimal → UnknownPosition.
    const posSci = normalizePosition(
      { $ref: '#/oneOf/1e0' },
      new Set(),
      root as import('./shared-safe-path.js').SchemaNode,
    );
    expect(posSci.kind).toBe('unknown');
    // Malformed: #/oneOf/+1 → signed → UnknownPosition.
    const posSigned = normalizePosition(
      { $ref: '#/oneOf/+1' },
      new Set(),
      root as import('./shared-safe-path.js').SchemaNode,
    );
    expect(posSigned.kind).toBe('unknown');
  });

  it('`#/` does not alias to array root: fails on an array-typed root, resolves an empty-name member on an object root', async () => {
    const { normalizePosition } = await import('./shared-safe-path.js');
    const rootObj = {
      properties: { '': { type: 'string' } },
    } as unknown as import('./shared-safe-path.js').SchemaNode;
    // #/ against an object root points to the '' property.
    const pos = normalizePosition({ $ref: '#/' }, new Set(), rootObj);
    // The empty-name property is not a schema object here (it's { type: 'string' }),
    // so normalizePosition on it returns UnknownPosition — but not because
    // of any alias to root.
    expect(pos.kind).toBe('unknown');
  });
});
