import { describe, expect, it } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseProviderRunMetadata } from './parse.js';
import { computeMetadataSemanticSha256 } from './semantic-hash.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');

interface ValidEntry {
  file: string;
  valid: true;
  purpose?: string;
}
interface InvalidEntry {
  file: string;
  valid: false;
  expectedCodes: string[];
}
type ManifestEntry = ValidEntry | InvalidEntry;

const manifest: ManifestEntry[] = JSON.parse(
  readFileSync(join(fixturesDir, 'manifest.json'), 'utf8'),
);
const encoder = new TextEncoder();

describe('fixture manifest integrity', () => {
  it('every fixture file exists', () => {
    for (const entry of manifest) {
      expect(() => readFileSync(join(fixturesDir, entry.file), 'utf8')).not.toThrow();
    }
  });
  it('every file on disk is listed in the manifest (other than manifest.json)', () => {
    const listed = new Set(manifest.map((e) => e.file));
    const onDisk = readdirSync(fixturesDir).filter((f) => f !== 'manifest.json');
    for (const f of onDisk) expect(listed.has(f)).toBe(true);
  });
});

describe('valid fixtures round-trip through parseProviderRunMetadata', () => {
  for (const entry of manifest) {
    if (!entry.valid) continue;
    if (entry.file.startsWith('golden-hash-')) continue; // covered by the goldens block
    it(`parses ${entry.file}`, () => {
      const text = readFileSync(join(fixturesDir, entry.file), 'utf8');
      const r = parseProviderRunMetadata(encoder.encode(text));
      if (!r.valid) {
        // eslint-disable-next-line no-console
        console.error(entry.file, r.errors);
      }
      expect(r.valid).toBe(true);
    });
  }
});

describe('golden-hash fixtures reproduce metadataSemanticSha256', () => {
  const goldens = manifest.filter(
    (e): e is ValidEntry => e.valid && e.file.startsWith('golden-hash-'),
  );
  for (const entry of goldens) {
    it(`${entry.file} hash matches wrapper.metadataSemanticSha256`, () => {
      const wrapper = JSON.parse(readFileSync(join(fixturesDir, entry.file), 'utf8')) as {
        metadata: unknown;
        metadataSemanticSha256: string;
      };
      const text = JSON.stringify(wrapper.metadata);
      const r = parseProviderRunMetadata(encoder.encode(text));
      expect(r.valid).toBe(true);
      if (!r.valid) return;
      const computed = computeMetadataSemanticSha256(r.metadata);
      expect(computed).toBe(wrapper.metadataSemanticSha256);
    });
  }
});

describe('invalid fixtures produce the expected error codes', () => {
  for (const entry of manifest) {
    if (entry.valid) continue;
    it(`${entry.file} produces every expected code`, () => {
      const text = readFileSync(join(fixturesDir, entry.file), 'utf8');
      const r = parseProviderRunMetadata(encoder.encode(text));
      expect(r.valid).toBe(false);
      if (r.valid) return;
      const emittedCodes = new Set(r.errors.map((e) => e.code));
      for (const expected of entry.expectedCodes) {
        expect(emittedCodes.has(expected as never)).toBe(true);
      }
    });
  }
});
