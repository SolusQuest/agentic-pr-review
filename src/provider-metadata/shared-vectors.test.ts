/**
 * Shared conformance vectors G1..G7 (from the design contract's
 * `### Safe diagnostic path for Unicode / additional-property rejections`)
 * exercised through the metadata pipeline's stage-6 traversal and the shared
 * scanStringSafety helper.
 *
 * V1..V3 (from `### Shared conformance vectors`) are executed against their
 * OWN hypothetical schemas as required by the shared subsection; expected
 * outputs are the literal paths documented in the shared oracle, not values
 * re-derived from the production resolver.
 */

import { describe, it, expect } from 'vitest';
import {
  scanStringSafety,
  normalizePosition,
  resolveProperty,
  UNKNOWN_POSITION,
  type SchemaNode,
} from '../state-v2/shared-safe-path.js';

const EMPTY_SCHEMA: SchemaNode = {};

function safePath(segments: readonly string[]): string {
  return segments.length === 0 ? '' : '/' + segments.join('/');
}

// ---------------------------------------------------------------------------
// G1..G7 -- executed against a schemaless / partially-known schema tree via
// scanStringSafety. Each vector confirms the shared safe-path segments the
// #51 stage-6 pipeline will report.
// ---------------------------------------------------------------------------

describe('shared conformance vector G1 -- unknown ancestor with descendant lone-surrogate value', () => {
  it('emits <untrusted-property>/<untrusted-property> to the lone surrogate', () => {
    const value = { attacker: { inner: '\uD83D' } };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<untrusted-property>/<untrusted-property>');
  });
});

describe('shared conformance vector G2 -- control-character ancestor with descendant lone-surrogate value', () => {
  it('emits <invalid-control>/<untrusted-property>', () => {
    // Property name with a C0 control character (U+0007).
    const value: Record<string, unknown> = {};
    (value as Record<string, unknown>)['ctl\u0007'] = { inner: '\uD83D' };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<invalid-control>/<untrusted-property>');
  });
});

describe('shared conformance vector G3 -- lone-surrogate property name at top level', () => {
  it('emits <invalid-utf16>', () => {
    const value: Record<string, unknown> = {};
    value['\uD83D'] = 1;
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<invalid-utf16>');
  });
});

describe('shared conformance vector G4 -- NUL in property name at top level', () => {
  it('emits <invalid-nul>', () => {
    const value: Record<string, unknown> = {};
    value['a\u0000b'] = 1;
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<invalid-nul>');
  });
});

describe('shared conformance vector G5 -- empty property name at top level', () => {
  it('shared traversal treats empty key as a valid schema position; only downstream schema stage flags it', () => {
    // Stage 6 has no violation for an empty-name key that carries safe content.
    const value: Record<string, unknown> = { '': 'ok' };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeUndefined();
  });
});

describe('shared conformance vector G6 -- schema-known ancestor chain with descendant lone-surrogate value', () => {
  it('preserves the schema-known ancestor names verbatim (RFC 6901-escaped)', () => {
    // Minimal schema: root object with a known property `stateKey` -> object
    // with a known property `workflowIdentity` -> string.
    const schema: SchemaNode = {
      type: 'object',
      properties: {
        stateKey: {
          type: 'object',
          properties: { workflowIdentity: { type: 'string' } },
          additionalProperties: false,
        },
      },
      additionalProperties: false,
    };
    const value = { stateKey: { workflowIdentity: '\uD83D' } };
    const violation = scanStringSafety(value, schema);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/stateKey/workflowIdentity');
  });
});

describe('shared conformance vector G7 -- well-formed surrogate pair in a schema-valid string field is accepted', () => {
  it('no violation for U+1F600 grinning face', () => {
    const value = { greeting: '\uD83D\uDE00' };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// V1..V3 -- executed against the hypothetical schemas defined in the shared
// subsection. NOT the metadata schema. Expected outputs come from the shared
// oracle constants recorded here so a resolver regression is detected without
// re-running the resolver against itself.
// ---------------------------------------------------------------------------

describe('shared conformance vector V1 -- unknown ancestor + value-level lone surrogate at depth 3', () => {
  it('scanStringSafety returns [<untrusted-property> x3]', () => {
    const value = { a: { b: { c: '\uD83D' } } };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe(
      '/<untrusted-property>/<untrusted-property>/<untrusted-property>',
    );
  });
});

describe('shared conformance vector V2 -- terminal <invalid-utf16> in an unknown property name', () => {
  it('scanStringSafety returns /<untrusted-property>/<invalid-utf16>', () => {
    const inner: Record<string, unknown> = {};
    inner['\uD83D'] = 1;
    const value = { outer: inner };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/<untrusted-property>/<invalid-utf16>');
  });
});

describe('shared conformance vector V3 -- resolver union child position (oneOf, per-branch assertions)', () => {
  it('per-branch: branchA.payload.beta is unknown; branchB.payload.beta is known; aggregate is known', () => {
    // Hypothetical schema from the shared subsection. Two oneOf branches share
    // a `payload.beta` position but only branch B declares it.
    const branchA: SchemaNode = {
      type: 'object',
      properties: { payload: { type: 'object', properties: { alpha: { type: 'string' } } } },
      additionalProperties: false,
    };
    const branchB: SchemaNode = {
      type: 'object',
      properties: {
        payload: {
          type: 'object',
          properties: { alpha: { type: 'string' }, beta: { type: 'string' } },
          additionalProperties: false,
        },
      },
      additionalProperties: false,
    };
    const rootSchema: SchemaNode = { oneOf: [branchA, branchB] };

    // Per-branch checks: normalize each branch separately.
    const posA = normalizePosition(branchA, new Set(), rootSchema);
    const posB = normalizePosition(branchB, new Set(), rootSchema);
    const posPayloadA = resolveProperty(posA, 'payload').childSchemaPosition;
    const posPayloadB = resolveProperty(posB, 'payload').childSchemaPosition;
    const resolveA = resolveProperty(posPayloadA, 'beta');
    const resolveB = resolveProperty(posPayloadB, 'beta');
    expect(resolveA.schemaKnown).toBe(false);
    expect(resolveB.schemaKnown).toBe(true);

    // Aggregate: normalized root sees `payload.beta` as schema-known through
    // the union of oneOf branches.
    const posRoot = normalizePosition(rootSchema, new Set(), rootSchema);
    const posPayloadRoot = resolveProperty(posRoot, 'payload').childSchemaPosition;
    const resolveRoot = resolveProperty(posPayloadRoot, 'beta');
    expect(resolveRoot.schemaKnown).toBe(true);
  });
});

describe('sanity: normalizePosition on UNKNOWN gracefully returns unknown for downstream resolvers', () => {
  it('UNKNOWN_POSITION resolvers stay unknown', () => {
    const r = resolveProperty(UNKNOWN_POSITION, 'anything');
    expect(r.schemaKnown).toBe(false);
  });
});
