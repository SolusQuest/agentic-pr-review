import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseProviderRunMetadata } from '../src/provider-metadata/parse.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', 'protocol', 'fixtures', 'provider-run-metadata', 'v1');
const manifest = JSON.parse(readFileSync(join(fixturesDir, 'manifest.json'), 'utf8')) as Array<{
  file: string;
  valid: boolean;
  expectedCodes?: string[];
  encoding?: 'binary';
}>;
const enc = new TextEncoder();

for (const entry of manifest) {
  if (entry.valid) continue;
  const bytes =
    entry.encoding === 'binary'
      ? new Uint8Array(readFileSync(join(fixturesDir, entry.file)))
      : enc.encode(readFileSync(join(fixturesDir, entry.file), 'utf8'));
  const r = parseProviderRunMetadata(bytes);
  if (r.valid) throw new Error(`unexpected valid: ${entry.file}`);
  const oracle = { errors: r.errors };
  writeFileSync(
    join(fixturesDir, entry.file + '.expected.json'),
    JSON.stringify(oracle, null, 2) + '\n',
  );
  console.log('wrote oracle for', entry.file, `(${r.errors.length} errors)`);
}
