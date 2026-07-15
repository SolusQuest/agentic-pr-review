import { describe, expect, it } from 'vitest';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));

/**
 * Enforce that `src/provider-metadata/` does not host a second RFC 8785 /
 * canonical-JSON implementation. Merged `#48` (`src/canonical-json/`) is the
 * single source; a violation here would silently fork the byte oracle used
 * by `#48`, `#53`, and `#54`.
 */
describe('provider-metadata canonical-JSON import boundary', () => {
  it('forbids any function, arrow function, class, or const that names itself a canonical serializer', () => {
    const files = readdirSync(here).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'));
    const suspicious =
      /(function|const|let|var|class)\s+(jcs\w*|canonical(Json|Serialize|Bytes)\w*|rfc8785\w*|serializeCanonical\w*|toCanonicalJson\w*)\b/i;
    for (const file of files) {
      const content = readFileSync(join(here, file), 'utf8');
      if (suspicious.test(content)) {
        throw new Error(
          `${file} defines a canonical-JSON serializer. Import from src/canonical-json/ instead.`,
        );
      }
    }
  });

  it('imports canonicalJsonBytes ONLY from src/canonical-json/', () => {
    const files = readdirSync(here).filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'));
    // Anywhere canonicalJsonBytes is imported must be from ../canonical-json.
    const importFrom = /import\s*\{[^}]*\bcanonicalJsonBytes\b[^}]*\}\s*from\s*['"]([^'"]+)['"]/g;
    for (const file of files) {
      const content = readFileSync(join(here, file), 'utf8');
      let m: RegExpExecArray | null;
      while ((m = importFrom.exec(content)) !== null) {
        expect(m[1]).toMatch(/^\.\.\/canonical-json\//);
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
