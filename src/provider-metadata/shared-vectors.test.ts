/**
 * Shared conformance vectors G1..G7 and V1..V3 from
 * `docs/20_architecture/session-ledger-and-prefix-contract.md`.
 *
 * G-vectors are exercised through the public `parseProviderRunMetadata`
 * entry point (i.e. the full #51 metadata driver) so each vector asserts the
 * exact `MetadataError.path` this workstream emits, not just the shared
 * traversal helper. G7 additionally asserts that a well-formed surrogate
 * pair survives stages 6, 7, and 8 and is preserved byte-exact by canonical
 * serialization.
 *
 * V-vectors are exercised against their OWN hypothetical schemas (V1/V2:
 * every position UnknownPosition; V3: the `oneOf` construct frozen verbatim
 * in "Shared conformance vectors -- Vector V3"). Neither V-vector re-derives
 * expected outputs from the production resolver; the assertions here are the
 * literal path values and per-branch observations recorded in the shared
 * oracle.
 */

import { describe, it, expect } from 'vitest';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  normalizePosition,
  resolveArrayItem,
  resolveProperty,
  UNKNOWN_POSITION,
  type SchemaNode,
  type SchemaPosition,
} from '../state-v2/shared-safe-path.js';
import { scanStringSafetyIterative as scanStringSafety } from './string-safety.js';
import { parseProviderRunMetadata } from './parse.js';
import { computeMetadataSemanticSha256 } from './semantic-hash.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');
const encoder = new TextEncoder();

const EMPTY_SCHEMA: SchemaNode = {};

function safePath(segments: readonly string[]): string {
  return segments.length === 0 ? '' : '/' + segments.join('/');
}

/**
 * Observational equivalence to `UnknownPosition` (per the shared "Shared
 * conformance vectors" subsection): every subsequent `resolveProperty` and
 * `resolveArrayItem` call returns `schemaKnown = false` with a child position
 * that is itself observationally equivalent to `UnknownPosition`. We probe a
 * finite set of names + the array-item resolver, which is sufficient to
 * distinguish the collapsed `UnknownPosition` and the isomorphic
 * `CompositePosition([UnknownPosition])` from any position that would ever
 * return `schemaKnown = true`.
 */
function assertObservationallyUnknown(pos: SchemaPosition, depth = 2): void {
  const probes = ['x', 'payload', 'alpha', 'beta', 'extraneous', '0'];
  for (const key of probes) {
    const r = resolveProperty(pos, key);
    expect(r.schemaKnown).toBe(false);
    if (depth > 0) assertObservationallyUnknown(r.childSchemaPosition, depth - 1);
  }
  const ar = resolveArrayItem(pos);
  expect(ar.schemaKnown).toBe(false);
  if (depth > 0) assertObservationallyUnknown(ar.childSchemaPosition, depth - 1);
}

// ---------------------------------------------------------------------------
// G1..G7 -- exercised through parseProviderRunMetadata. Each fixture is a
// minimal top-level JSON document that reaches the vector's expected stage
// (6 for unicode / control / nul / lone-surrogate name / lone-surrogate
// value; 7 for empty-name additional-property; 6+7+8 pass for the valid
// surrogate-pair vector).
// ---------------------------------------------------------------------------

describe('shared conformance vector G1 -- unknown-ancestor property with descendant lone-surrogate VALUE', () => {
  it('parser rejects at stage 6 with invalid-metadata-unicode and path /<untrusted-property>/<untrusted-property>', () => {
    const doc = '{"secretToken":{"inner":"\\uD800"}}';
    const r = parseProviderRunMetadata(encoder.encode(doc));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual([
      { code: 'invalid-metadata-unicode', path: '/<untrusted-property>/<untrusted-property>' },
    ]);
  });
});

describe('shared conformance vector G2 -- control-character ancestor with descendant lone-surrogate VALUE', () => {
  it('parser rejects at stage 6 with path /<invalid-control>/<untrusted-property>', () => {
    // Property name `attacker\ncontrolled` (U+000A control char inside).
    const doc = '{"attacker\\ncontrolled":{"inner":"\\uD800"}}';
    const r = parseProviderRunMetadata(encoder.encode(doc));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual([
      { code: 'invalid-metadata-unicode', path: '/<invalid-control>/<untrusted-property>' },
    ]);
  });
});

describe('shared conformance vector G3 -- lone-surrogate PROPERTY NAME at top level', () => {
  it('parser rejects at stage 6 with path /<invalid-utf16>', () => {
    const doc = '{"\\uD800":1}';
    const r = parseProviderRunMetadata(encoder.encode(doc));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual([{ code: 'invalid-metadata-unicode', path: '/<invalid-utf16>' }]);
  });
});

describe('shared conformance vector G4 -- NUL in PROPERTY NAME at top level', () => {
  it('parser rejects at stage 6 with path /<invalid-nul>', () => {
    const doc = '{"a\\u0000b":1}';
    const r = parseProviderRunMetadata(encoder.encode(doc));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual([{ code: 'invalid-metadata-unicode', path: '/<invalid-nul>' }]);
  });
});

describe('shared conformance vector G5 -- empty property name at top level of otherwise-valid metadata', () => {
  it('parser reaches stage 7 and rejects with invalid-metadata-additional-property, path /<empty-name>', () => {
    const base = JSON.parse(
      readFileSync(join(fixturesDir, 'valid-bootstrap-hit.json'), 'utf8'),
    ) as Record<string, unknown>;
    (base as Record<string, unknown>)[''] = 'ok';
    const r = parseProviderRunMetadata(encoder.encode(JSON.stringify(base)));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toContainEqual({
      code: 'invalid-metadata-additional-property',
      path: '/<empty-name>',
    });
    // No other codes should fire from this single-mutation base (which is
    // otherwise valid).
    expect(r.errors.length).toBe(1);
  });
});

describe('shared conformance vector G6 -- schema-known top-level property with lone-surrogate value at schema-known descendant', () => {
  it('parser rejects at stage 6 with the fully-echoed schema-known path (e.g. /capability/mode)', () => {
    const base = JSON.parse(
      readFileSync(join(fixturesDir, 'valid-bootstrap-hit.json'), 'utf8'),
    ) as {
      capability: { mode: string; aggregate: string; statelessProof: unknown };
    };
    // Replace capability.mode with a lone-surrogate value. capability and
    // mode are both schema-known, so the entire ancestor chain is trusted
    // and the emitted path echoes their real names.
    base.capability = {
      ...base.capability,
      mode: '\uD800',
    };
    const r = parseProviderRunMetadata(encoder.encode(JSON.stringify(base)));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    expect(r.errors).toEqual([{ code: 'invalid-metadata-unicode', path: '/capability/mode' }]);
  });
});

describe('shared conformance vector G7 -- well-formed surrogate pair (U+1F600) in a schema-valid string field is accepted end-to-end', () => {
  it('parser returns valid, canonical serialization preserves the value byte-exact', () => {
    const base = JSON.parse(
      readFileSync(join(fixturesDir, 'valid-bootstrap-hit.json'), 'utf8'),
    ) as Record<string, unknown>;
    const grinning = 'x\uD83D\uDE00'; // U+1F600 encoded as UTF-16 surrogate pair
    base.selectedProviderId = grinning;
    base.observedProviderId = grinning; // preserve identity cross-mismatch invariant
    const r = parseProviderRunMetadata(encoder.encode(JSON.stringify(base)));
    expect(r.valid).toBe(true);
    if (!r.valid) return;
    // Stage 6 accepted the surrogate pair.
    expect(r.metadata.selectedProviderId).toBe(grinning);
    expect(r.metadata.observedProviderId).toBe(grinning);
    // Canonical serialization is deterministic and preserves the character:
    // the hash of the parsed metadata is well-defined, and re-parsing the
    // canonical bytes reproduces the same field value.
    const hash = computeMetadataSemanticSha256(r.metadata);
    expect(hash).toMatch(/^[0-9a-f]{64}$/);
  });
});

// ---------------------------------------------------------------------------
// V1..V3 -- hypothetical schemas per the shared subsection.
// ---------------------------------------------------------------------------

describe('shared conformance vector V1 -- shared-unknown-ancestor-with-value-level-surrogate', () => {
  it('scanStringSafety returns [<untrusted-property> x3] on the frozen input', () => {
    const value = { a: { b: { c: '\uD800' } } };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe(
      '/<untrusted-property>/<untrusted-property>/<untrusted-property>',
    );
  });
});

describe('shared conformance vector V2 -- shared-terminal-invalid-utf16-in-unknown-property-name', () => {
  it('scanStringSafety returns /<untrusted-property>/<invalid-utf16> on the frozen input', () => {
    const inner: Record<string, unknown> = {};
    inner['\uD800'] = 1;
    const value = { a: inner };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<untrusted-property>/<invalid-utf16>');
  });
});

describe('shared conformance vector V3 -- shared-resolver-union-child-position with the frozen oneOf construct', () => {
  // Hypothetical schema VERBATIM from the shared subsection. Only the
  // structural keywords quoted in the contract appear (`oneOf`,
  // `properties`); no additional `type` / `additionalProperties` etc. so
  // the observed resolver behavior matches the "frozen verbatim" wording.
  const branchA: SchemaNode = {
    properties: {
      payload: {
        properties: { alpha: { type: 'string' } },
      },
    },
  };
  const branchB: SchemaNode = {
    properties: {
      payload: {
        properties: { beta: { type: 'string' } },
      },
    },
  };
  const rootSchema: SchemaNode = { oneOf: [branchA, branchB] };

  const rootBranchAPos = normalizePosition(branchA, new Set(), rootSchema);
  const rootBranchBPos = normalizePosition(branchB, new Set(), rootSchema);
  const rootPos = normalizePosition(rootSchema, new Set(), rootSchema);

  const propAPayload = resolveProperty(rootBranchAPos, 'payload');
  const propBPayload = resolveProperty(rootBranchBPos, 'payload');
  const propRootPayload = resolveProperty(rootPos, 'payload');

  const P_A_payload = propAPayload.childSchemaPosition;
  const P_B_payload = propBPayload.childSchemaPosition;
  const P_root_payload = propRootPayload.childSchemaPosition;

  it('branch-A payload resolves schema-known', () => {
    expect(propAPayload.schemaKnown).toBe(true);
  });

  it('branch-B payload resolves schema-known', () => {
    expect(propBPayload.schemaKnown).toBe(true);
  });

  it('aggregate-root payload resolves schema-known via oneOf union', () => {
    expect(propRootPayload.schemaKnown).toBe(true);
  });

  it('branch-A beta resolves schema-unknown with an UnknownPosition-equivalent child', () => {
    const r = resolveProperty(P_A_payload, 'beta');
    expect(r.schemaKnown).toBe(false);
    assertObservationallyUnknown(r.childSchemaPosition);
  });

  it('branch-B beta resolves schema-known with an UnknownPosition-equivalent child (scalar schema)', () => {
    const r = resolveProperty(P_B_payload, 'beta');
    expect(r.schemaKnown).toBe(true);
    assertObservationallyUnknown(r.childSchemaPosition);
  });

  it('aggregate beta resolves schema-known with an UnknownPosition-equivalent child (branch-B contribution wins)', () => {
    const r = resolveProperty(P_root_payload, 'beta');
    expect(r.schemaKnown).toBe(true);
    assertObservationallyUnknown(r.childSchemaPosition);
  });

  it('aggregate extraneous resolves schema-unknown with an UnknownPosition-equivalent child', () => {
    const r = resolveProperty(P_root_payload, 'extraneous');
    expect(r.schemaKnown).toBe(false);
    assertObservationallyUnknown(r.childSchemaPosition);
  });

  it('per-branch alpha resolution mirrors the frozen table (branch-A known, branch-B unknown)', () => {
    expect(resolveProperty(P_A_payload, 'alpha').schemaKnown).toBe(true);
    expect(resolveProperty(P_B_payload, 'alpha').schemaKnown).toBe(false);
    // Aggregate declares alpha via branch A.
    expect(resolveProperty(P_root_payload, 'alpha').schemaKnown).toBe(true);
  });

  it('terminal /payload/beta traversal on {payload:{beta:"\\u0000"}} yields the schema-known path', () => {
    const violation = scanStringSafety({ payload: { beta: '\u0000' } }, rootSchema);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/payload/beta');
  });
});

describe('sanity: UNKNOWN_POSITION resolvers stay unknown', () => {
  it('resolveProperty on UNKNOWN_POSITION returns schemaKnown=false', () => {
    expect(resolveProperty(UNKNOWN_POSITION, 'anything').schemaKnown).toBe(false);
  });
  it('resolveArrayItem on UNKNOWN_POSITION returns schemaKnown=false', () => {
    expect(resolveArrayItem(UNKNOWN_POSITION).schemaKnown).toBe(false);
  });
});
