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
import { parseProviderRunMetadata } from './parse.js';
import metadataSchema from '../../protocol/schemas/provider-run-metadata.v1.json' with { type: 'json' };

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
  it('shared traversal accepts empty-name key at stage 6 (no violation)', () => {
    const value: Record<string, unknown> = { '': 'ok' };
    const violation = scanStringSafety(value, EMPTY_SCHEMA);
    expect(violation).toBeUndefined();
  });
  it('metadata parser rejects at stage 7 with invalid-metadata-additional-property and marker <empty-name>', () => {
    const shape: Record<string, unknown> = {
      schemaVersion: 1,
      selectedProviderId: 'a',
      observedProviderId: 'a',
      resolvedModelId: 'm',
      adapterId: 'a'.repeat(64),
      logicalPrefixSha256: 'a'.repeat(64),
      prefixSha256: 'a'.repeat(64),
      capability: { mode: 'standard', aggregate: 'unknown', statelessProof: null },
      cacheStatus: 'unknown',
      normalizedUsage: {
        attempts: [],
        requests: [],
        aggregate: {
          totalInputTokens: null,
          uncachedInputTokens: null,
          cacheWriteInputTokens: null,
          cacheReadInputTokens: null,
          outputTokens: null,
          requestCount: 0,
          attemptCount: 0,
        },
      },
      retryObservations: {
        requests: [],
        aggregate: {
          requestCount: 0,
          attemptCount: 0,
          succeededCount: 0,
          failedCount: 0,
          cancelledCount: 0,
        },
      },
      errorCodes: [],
      telemetryCompleteness: {
        usage: 'missing',
        cache: 'missing',
        statelessProof: 'notApplicable',
        aggregate: 'missing',
      },
      producingRunId: '1',
      runAttempt: 1,
      interactionId: 'a'.repeat(64),
      consumedInputSha256: 'a'.repeat(64),
      resultSha256: 'a'.repeat(64),
      traceSha256: 'a'.repeat(64),
      predecessorLedgerSha256: 'bootstrap',
      candidateLedgerSha256: 'a'.repeat(64),
    };
    shape[''] = 'unknown';
    const r = parseProviderRunMetadata(new TextEncoder().encode(JSON.stringify(shape)));
    expect(r.valid).toBe(false);
    if (r.valid) return;
    const emptyErr = r.errors.find(
      (e) => e.code === 'invalid-metadata-additional-property' && e.path === '/<empty-name>',
    );
    expect(emptyErr).toBeDefined();
  });
});

describe('shared conformance vector G6 -- schema-known ancestor chain with descendant lone-surrogate value (metadata driver)', () => {
  it('preserves the schema-known metadata ancestor names verbatim', () => {
    // Use the actual metadata schema. Take a valid document, then inject a
    // lone surrogate into a schema-valid string field. The stage-6 traversal
    // must produce the exact schema-known safe path for that field.
    const rawMetaSchema: SchemaNode = metadataSchema as unknown as SchemaNode;
    const value = { selectedProviderId: 'a\uD83Db', unrelated: 1 };
    const violation = scanStringSafety(value, rawMetaSchema);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/selectedProviderId');
  });
});

describe('shared conformance vector G7 -- well-formed surrogate pair in a schema-valid string field is accepted', () => {
  it('no stage-6 violation for U+1F600 grinning face inside a schema-known identity field', () => {
    const rawMetaSchema: SchemaNode = metadataSchema as unknown as SchemaNode;
    const value = { selectedProviderId: '\uD83D\uDE00', otherField: 1 };
    const violation = scanStringSafety(value, rawMetaSchema);
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
  it('per-branch: branchA payload.beta unknown; branchB payload.beta known; aggregate payload.beta known; extraneous unknown', () => {
    // Hypothetical schema from the shared subsection. Branch A declares only
    // alpha under payload; branch B declares only beta under payload. Neither
    // branch declares 'extraneous'.
    const branchA: SchemaNode = {
      type: 'object',
      properties: {
        payload: {
          type: 'object',
          properties: { alpha: { type: 'string' } },
          additionalProperties: false,
        },
      },
      additionalProperties: false,
    };
    const branchB: SchemaNode = {
      type: 'object',
      properties: {
        payload: {
          type: 'object',
          properties: { beta: { type: 'string' } },
          additionalProperties: false,
        },
      },
      additionalProperties: false,
    };
    const rootSchema: SchemaNode = { oneOf: [branchA, branchB] };

    const posA = normalizePosition(branchA, new Set(), rootSchema);
    const posB = normalizePosition(branchB, new Set(), rootSchema);
    const posPayloadA = resolveProperty(posA, 'payload').childSchemaPosition;
    const posPayloadB = resolveProperty(posB, 'payload').childSchemaPosition;

    expect(resolveProperty(posPayloadA, 'beta').schemaKnown).toBe(false);
    expect(resolveProperty(posPayloadB, 'beta').schemaKnown).toBe(true);
    expect(resolveProperty(posPayloadA, 'alpha').schemaKnown).toBe(true);
    expect(resolveProperty(posPayloadB, 'alpha').schemaKnown).toBe(false);

    // Aggregate: root sees payload.beta as schema-known via union of branches.
    const posRoot = normalizePosition(rootSchema, new Set(), rootSchema);
    const posPayloadRoot = resolveProperty(posRoot, 'payload').childSchemaPosition;
    expect(resolveProperty(posPayloadRoot, 'beta').schemaKnown).toBe(true);
    expect(resolveProperty(posPayloadRoot, 'alpha').schemaKnown).toBe(true);

    // Extraneous property is unknown in every position.
    expect(resolveProperty(posPayloadA, 'extraneous').schemaKnown).toBe(false);
    expect(resolveProperty(posPayloadB, 'extraneous').schemaKnown).toBe(false);
    expect(resolveProperty(posPayloadRoot, 'extraneous').schemaKnown).toBe(false);

    // Terminal NUL traversal: scanStringSafety on { payload: { beta: '\u0000' } }
    // yields /payload/beta because beta is schema-known at the aggregate root.
    const violation = scanStringSafety({ payload: { beta: '\u0000' } }, rootSchema);
    expect(violation).toBeDefined();
    expect(safePath(violation!.segments)).toBe('/payload/beta');
  });
});

describe('sanity: normalizePosition on UNKNOWN gracefully returns unknown for downstream resolvers', () => {
  it('UNKNOWN_POSITION resolvers stay unknown', () => {
    const r = resolveProperty(UNKNOWN_POSITION, 'anything');
    expect(r.schemaKnown).toBe(false);
  });
});
