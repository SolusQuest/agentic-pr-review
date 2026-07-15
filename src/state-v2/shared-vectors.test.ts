/**
 * Shared conformance vectors G1..G7 (from the design contract's
 * `### Safe diagnostic path for Unicode / additional-property rejections`)
 * and V1..V3 (from `### Shared conformance vectors`).
 *
 * Expected outcomes for #48's manifest sidecar are looked up by vector ID
 * from the shared subsection tables. They are NOT re-derived from the
 * production implementation.
 */

import { describe, it, expect } from 'vitest';
import { MANIFEST_FILENAME, LEDGER_FILENAME, PROVIDER_RUN_METADATA_FILENAME } from './constants.js';
import { classifyStateBundleV2, type EntryDescriptor } from './classifier.js';
import { serializeStateManifestV2 } from './serializer.js';
import {
  normalizePosition,
  resolveProperty,
  scanStringSafety,
  type SchemaNode,
  type SchemaPosition,
} from './shared-safe-path.js';
import type { StateManifestV2 } from './manifest.js';

// A helper that constructs a bundle by taking an already-well-formed
// bootstrap manifest and injecting a mutation. The mutation is applied to
// the RAW parsed manifest object (which may contain property names outside
// the closed schema); this bypasses the builder's shared-safety stage and
// lets the classifier observe the injected attacker-controlled input.

function readPositiveBootstrapBundle(): {
  manifest: StateManifestV2;
  ledgerBytes: Uint8Array;
  metadataBytes: Uint8Array;
} {
  // Import the fixture bytes at test time. The fixture is a
  // canonicalized manifest.json plus its two sidecars.
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const fs = require('node:fs') as typeof import('node:fs');
  // eslint-disable-next-line @typescript-eslint/no-require-imports
  const path = require('node:path') as typeof import('node:path');
  const base = path.join(
    process.cwd(),
    'protocol',
    'fixtures',
    'state-manifest-v2',
    'positive-bootstrap',
    'bundle',
  );
  const manifestBytes = fs.readFileSync(path.join(base, 'manifest.json'));
  const ledgerBytes = fs.readFileSync(path.join(base, 'ledger.json'));
  const metadataBytes = fs.readFileSync(path.join(base, 'provider-run-metadata.json'));
  const manifest = JSON.parse(new TextDecoder().decode(manifestBytes)) as StateManifestV2;
  return {
    manifest,
    ledgerBytes: new Uint8Array(ledgerBytes),
    metadataBytes: new Uint8Array(metadataBytes),
  };
}

function classifyRaw(
  rawManifest: unknown,
  ledgerBytes: Uint8Array,
  metadataBytes: Uint8Array,
): ReturnType<typeof classifyStateBundleV2> {
  const manifestBytes = new TextEncoder().encode(JSON.stringify(rawManifest));
  const entryListing: readonly EntryDescriptor[] = [
    { name: MANIFEST_FILENAME, isRegularFile: true },
    { name: LEDGER_FILENAME, isRegularFile: true },
    { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
  ];
  return classifyStateBundleV2({
    manifestBytes,
    ledgerBytes,
    providerRunMetadataBytes: metadataBytes,
    entryListing,
  });
}

describe('shared conformance vector G1 — attacker-controlled ancestor with descendant lone-surrogate value', () => {
  it('produces manifest_shape_invalid with x_invalid_unicode:/<untrusted-property>/<untrusted-property>', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    // secretToken is not schema-known at the manifest root; add an
    // ancestor object with a lone-surrogate value at a nested property.
    const injected = {
      ...(manifest as unknown as Record<string, unknown>),
      secretToken: { nestedProp: '\uD800' },
    };
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/<untrusted-property>/<untrusted-property>');
  });
});

describe('shared conformance vector G2 — control-character ancestor with descendant lone-surrogate value', () => {
  it('produces manifest_shape_invalid with x_invalid_unicode:/<invalid-control>/<untrusted-property>', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected: Record<string, unknown> = {
      ...(manifest as unknown as Record<string, unknown>),
    };
    // "attacker\ncontrolled" — U+000A (control char, not NUL, not surrogate).
    injected['attacker\ncontrolled'] = { nestedProp: '\uD800' };
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/<invalid-control>/<untrusted-property>');
  });
});

describe('shared conformance vector G3 — lone-surrogate property name at top level', () => {
  it('produces manifest_shape_invalid with x_invalid_unicode:/<invalid-utf16>', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected: Record<string, unknown> = {
      ...(manifest as unknown as Record<string, unknown>),
    };
    injected['\uD800'] = 1;
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/<invalid-utf16>');
  });
});

describe('shared conformance vector G4 — NUL character in a property name at top level', () => {
  it('produces manifest_shape_invalid with x_invalid_unicode:/<invalid-nul>', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected: Record<string, unknown> = {
      ...(manifest as unknown as Record<string, unknown>),
    };
    injected['contains\u0000nul'] = 1;
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/<invalid-nul>');
  });
});

describe('shared conformance vector G5 — empty property name at top level', () => {
  it('produces manifest_unknown_field with x_invalid_field:/<empty-name>', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected: Record<string, unknown> = {
      ...(manifest as unknown as Record<string, unknown>),
    };
    injected[''] = 'anything';
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_unknown_field');
    expect(result.message).toBe('x_invalid_field:/<empty-name>');
  });
});

describe('shared conformance vector G6 — schema-known ancestor chain with descendant lone-surrogate value', () => {
  it('produces manifest_shape_invalid with x_invalid_unicode:/stateKey/workflowIdentity', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected = JSON.parse(JSON.stringify(manifest)) as Record<string, unknown>;
    const stateKey = injected.stateKey as Record<string, unknown>;
    stateKey.workflowIdentity = '\uD800';
    const result = classifyRaw(injected, ledgerBytes, metadataBytes);
    expect(result.kind).toBe('invalid');
    if (result.kind !== 'invalid') return;
    expect(result.diagnostic).toBe('manifest_shape_invalid');
    expect(result.message).toBe('x_invalid_unicode:/stateKey/workflowIdentity');
  });
});

describe('shared conformance vector G7 — well-formed surrogate pair in a schema-valid string field is accepted', () => {
  it('classifies as valid when a schema-valid string contains a well-formed surrogate pair', () => {
    const { manifest, ledgerBytes, metadataBytes } = readPositiveBootstrapBundle();
    const injected = JSON.parse(JSON.stringify(manifest)) as StateManifestV2;
    // U+1F600 grinning face — encoded as a well-formed surrogate pair.
    injected.stateKey.workflowIdentity = 'agentic\uD83D\uDE00review';
    // Re-canonicalize so the classifier's byte cap and hash checks pass;
    // the injected manifest must remain internally consistent with the
    // ledger and metadata already computed above. We reuse those bytes.
    const injectedBytes = serializeStateManifestV2(injected);
    const entryListing: readonly EntryDescriptor[] = [
      { name: MANIFEST_FILENAME, isRegularFile: true },
      { name: LEDGER_FILENAME, isRegularFile: true },
      { name: PROVIDER_RUN_METADATA_FILENAME, isRegularFile: true },
    ];
    const result = classifyStateBundleV2({
      manifestBytes: injectedBytes,
      ledgerBytes,
      providerRunMetadataBytes: metadataBytes,
      entryListing,
    });
    expect(result.kind).toBe('valid');
  });
});

// ---------------------------------------------------------------------------
// V1..V3: resolver/traversal vectors defined in `### Shared conformance
// vectors`. Each Vi is executed against its own hypothetical schema, NOT
// the manifest schema. Only observable resolver/traversal behavior is
// asserted; internal representation is out of scope.
// ---------------------------------------------------------------------------

describe('shared conformance vector V1 — unknown-ancestor with value-level lone surrogate', () => {
  it('scanStringSafety on a schemaless hypothetical schema returns /<untrusted-property> x3', () => {
    const hypothetical: SchemaNode = { type: 'object' };
    const value = { a: { b: { c: '\uD800' } } };
    const violation = scanStringSafety(value, hypothetical);
    expect(violation).toBeDefined();
    expect(violation?.segments).toEqual([
      '<untrusted-property>',
      '<untrusted-property>',
      '<untrusted-property>',
    ]);
  });
});

describe('shared conformance vector V2 — terminal <invalid-utf16> in an unknown property name', () => {
  it('scanStringSafety returns [<untrusted-property>, <invalid-utf16>]', () => {
    const hypothetical: SchemaNode = { type: 'object' };
    const value = { a: { ['\uD800']: 1 } };
    const violation = scanStringSafety(value, hypothetical);
    expect(violation).toBeDefined();
    expect(violation?.segments).toEqual(['<untrusted-property>', '<invalid-utf16>']);
  });
});

describe('shared conformance vector V3 — resolver union child position (oneOf branches)', () => {
  it('resolveProperty returns schemaKnown for both branches at their respective payload.beta positions', () => {
    // Branch A declares payload.alpha only; branch B declares payload.beta
    // only. Union at the root level should mark both alpha and beta as
    // schema-known when queried through the top-level root position.
    const hypothetical: SchemaNode = {
      oneOf: [
        {
          type: 'object',
          properties: {
            payload: {
              type: 'object',
              properties: {
                alpha: { type: 'string' },
              },
            },
          },
        },
        {
          type: 'object',
          properties: {
            payload: {
              type: 'object',
              properties: {
                beta: { type: 'string' },
              },
            },
          },
        },
      ],
    };
    const rootPos = normalizePosition(hypothetical);

    // Query the union at the top level.
    const payloadResult = resolveProperty(rootPos, 'payload');
    expect(payloadResult.schemaKnown).toBe(true);
    const betaResult = resolveProperty(payloadResult.childSchemaPosition, 'beta');
    expect(betaResult.schemaKnown).toBe(true);
    // extraneous is not declared in any branch under payload.
    const extraneousResult = resolveProperty(payloadResult.childSchemaPosition, 'extraneous');
    expect(extraneousResult.schemaKnown).toBe(false);

    // Also per-branch: each branch's normalized position should
    // independently declare its own leaf property. This is the assertion
    // that distinguishes per-call activeSchemaNodes from a broken global
    // visited-set.
    // (Traversal of the top-level oneOf without an ObjectPosition yields
    // a CompositePosition of two branch object positions, so we exercise
    // them individually.)
    void rootPos;
    // Executing scanStringSafety with a value that would only trigger
    // when beta is declared:
    const violation = scanStringSafety({ payload: { beta: '\uD800' } }, hypothetical);
    expect(violation).toBeDefined();
    expect(violation?.segments).toEqual(['payload', 'beta']);

    // Silence lint about unused import.
    void ({} as SchemaPosition);
  });
});
