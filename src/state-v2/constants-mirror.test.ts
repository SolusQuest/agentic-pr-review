import { describe, expect, it } from 'vitest';
import schema from '../../protocol/schemas/state-manifest.v2.json' with { type: 'json' };
import {
  LEDGER_FILENAME,
  LEDGER_MAX_BYTES,
  LEDGER_SCHEMA_VERSION,
  MANIFEST_FILENAME,
  METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_FILENAME,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION,
  STATE_NAMESPACE,
} from './constants.js';

// The refined AC declares the JSON Schema authoritative for descriptor byte
// caps, filename `const`s, and schema-version `const`s. `constants.ts` is a
// TypeScript ergonomic mirror. A schema-only update that changed one side
// without the other would silently let Ajv accept manifest bytes that the
// builder / classifier still enforce at the old value. This suite pins the
// mirror to the authoritative schema so a divergence fails compilation
// early in CI, not at runtime.

type JsonNode = {
  readonly [key: string]: JsonNode | JsonNode[] | string | number | boolean | null;
};

function get(node: JsonNode, ...path: string[]): JsonNode {
  let current: JsonNode = node;
  for (const key of path) {
    const next = current[key];
    if (next === null || typeof next !== 'object' || Array.isArray(next)) {
      throw new Error(`unexpected shape at ${path.join('.')}`);
    }
    current = next as JsonNode;
  }
  return current;
}

const schemaRoot = schema as unknown as JsonNode;

describe('state-v2 constants mirror the authoritative JSON Schema', () => {
  it('STATE_NAMESPACE matches stateNamespace const', () => {
    const stateNamespace = get(schemaRoot, 'properties', 'stateNamespace');
    expect(stateNamespace.const).toBe(STATE_NAMESPACE);
  });

  it('LEDGER_FILENAME matches ledger.path const', () => {
    const ledgerPath = get(schemaRoot, 'properties', 'ledger', 'properties', 'path');
    expect(ledgerPath.const).toBe(LEDGER_FILENAME);
  });

  it('PROVIDER_RUN_METADATA_FILENAME matches providerRunMetadata.path const', () => {
    const metadataPath = get(schemaRoot, 'properties', 'providerRunMetadata', 'properties', 'path');
    expect(metadataPath.const).toBe(PROVIDER_RUN_METADATA_FILENAME);
  });

  it('LEDGER_SCHEMA_VERSION matches ledger.schemaVersion const', () => {
    const ledgerSchemaVersion = get(
      schemaRoot,
      'properties',
      'ledger',
      'properties',
      'schemaVersion',
    );
    expect(ledgerSchemaVersion.const).toBe(LEDGER_SCHEMA_VERSION);
  });

  it('PROVIDER_RUN_METADATA_SCHEMA_VERSION matches providerRunMetadata.schemaVersion const', () => {
    const metadataSchemaVersion = get(
      schemaRoot,
      'properties',
      'providerRunMetadata',
      'properties',
      'schemaVersion',
    );
    expect(metadataSchemaVersion.const).toBe(PROVIDER_RUN_METADATA_SCHEMA_VERSION);
  });

  it('LEDGER_MAX_BYTES matches ledger.bytes maximum', () => {
    const ledgerBytes = get(schemaRoot, 'properties', 'ledger', 'properties', 'bytes');
    expect(ledgerBytes.maximum).toBe(LEDGER_MAX_BYTES);
  });

  it('METADATA_MAX_BYTES matches providerRunMetadata.bytes maximum', () => {
    const metadataBytes = get(
      schemaRoot,
      'properties',
      'providerRunMetadata',
      'properties',
      'bytes',
    );
    expect(metadataBytes.maximum).toBe(METADATA_MAX_BYTES);
  });

  it('MANIFEST_FILENAME is a mirror-only constant with no counterpart in the manifest schema itself', () => {
    // The manifest does not carry its own filename, so this constant has
    // no `const` counterpart to compare against. This assertion documents
    // that intentional gap so a future schema addition either supplies
    // a mirror or updates this test.
    expect(MANIFEST_FILENAME).toBe('manifest.json');
  });
});
