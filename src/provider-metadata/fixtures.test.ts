import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseProviderRunMetadata } from './parse.js';
import { computeMetadataSemanticSha256 } from './semantic-hash.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');

function bytes(text: string): Uint8Array {
  return new TextEncoder().encode(text);
}

describe('valid provider-metadata fixtures round-trip through parseProviderRunMetadata', () => {
  const validFiles = readdirSync(fixturesDir).filter(
    (f) => f.startsWith('valid-') && f.endsWith('.json'),
  );
  for (const name of validFiles) {
    it(`parses ${name} successfully and yields a schemaVersion=1 value`, () => {
      const text = readFileSync(join(fixturesDir, name), 'utf8');
      const r = parseProviderRunMetadata(bytes(text));
      if (!r.valid) {
        // eslint-disable-next-line no-console
        console.error(name, r.errors);
      }
      expect(r.valid).toBe(true);
      if (r.valid) {
        expect(r.metadata.schemaVersion).toBe(1);
      }
    });
  }
});

describe('golden-hash fixtures reproduce their stored metadataSemanticSha256', () => {
  const goldens = readdirSync(fixturesDir).filter(
    (f) => f.startsWith('golden-hash-') && f.endsWith('.json'),
  );
  for (const name of goldens) {
    it(`${name} hash matches wrapper.metadataSemanticSha256`, () => {
      const wrapper = JSON.parse(readFileSync(join(fixturesDir, name), 'utf8')) as {
        metadata: unknown;
        metadataSemanticSha256: string;
      };
      const text = JSON.stringify(wrapper.metadata);
      const r = parseProviderRunMetadata(bytes(text));
      expect(r.valid).toBe(true);
      if (!r.valid) return;
      const computed = computeMetadataSemanticSha256(r.metadata);
      expect(computed).toBe(wrapper.metadataSemanticSha256);
    });
  }
});
