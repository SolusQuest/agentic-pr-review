import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

describe('provider-metadata canonical-JSON import boundary', () => {
  it('forbids a second RFC 8785 canonical-JSON implementation in src/provider-metadata/', () => {
    // Any *.ts file under src/provider-metadata/ that appears to define a
    // canonical-JSON serializer (function named jcs*, canonical*Bytes,
    // canonicalJson*, rfc8785*) fails this test. The module MUST import from
    // src/canonical-json/ and MUST NOT vendor a second implementation.
    const files = readdirSync(here).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'));
    const forbidden =
      /export\s+function\s+(jcs\w*|canonical(Json|Serialize|Bytes)\w*|rfc8785\w*|jcsSerialize\w*)/i;
    for (const file of files) {
      const content = readFileSync(join(here, file), 'utf8');
      if (forbidden.test(content)) {
        throw new Error(
          `${file} defines a canonical-JSON serializer. Import from src/canonical-json/ instead.`,
        );
      }
    }
  });

  it('semantic-hash.ts imports canonicalJsonBytes from src/canonical-json/', () => {
    const content = readFileSync(join(here, 'semantic-hash.ts'), 'utf8');
    expect(content).toMatch(/from ['"]\.\.\/canonical-json\/index\.js['"]/);
    expect(content).toMatch(/canonicalJsonBytes/);
  });

  it('src/provider-metadata contains no jcs.ts', () => {
    const files = readdirSync(here);
    expect(files).not.toContain('jcs.ts');
    expect(files).not.toContain('jcs.test.ts');
  });
});
