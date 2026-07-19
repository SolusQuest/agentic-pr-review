import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * Import-boundary checks (issue #50): the test-only oracle generator must not
 * be reachable from production code, and canonicalization has a single source
 * (src/canonical-json/).
 */

const MODULE_DIR = path.resolve('src/prefix-contract');

function productionFiles(): string[] {
  return readdirSync(MODULE_DIR)
    .filter(
      (name) =>
        name.endsWith('.ts') && !name.endsWith('.test.ts') && !name.endsWith('.testhelper.ts'),
    )
    .map((name) => path.join(MODULE_DIR, name));
}

describe('prefix-contract import boundary', () => {
  it('exports exactly the frozen D9 TypeScript surface', async () => {
    const publicApi = await import('./index.js');
    expect(Object.keys(publicApi).sort()).toEqual([
      'computeAdapterId',
      'computeCacheConfigId',
      'computePolicyId',
      'computeTemplateId',
      'computeToolDefinitionId',
      'deriveInteractionId',
      'validateIdentity',
      'validateModelSnapshot',
    ]);
  });

  it('production files never import the oracle generator', () => {
    for (const file of productionFiles()) {
      const text = readFileSync(file, 'utf8');
      expect(text, file).not.toContain('generate-fixtures.testhelper');
    }
  });

  it('canonicalization comes only from src/canonical-json/', () => {
    for (const file of productionFiles()) {
      const text = readFileSync(file, 'utf8');
      expect(text, file).not.toContain('JSON.stringify(');
      const imports = [...text.matchAll(/from '([^']+)'/g)].map((match) => match[1]);
      for (const specifier of imports) {
        if (specifier.includes('canonical')) {
          expect(specifier, file).toBe('../canonical-json/index.js');
        }
      }
    }
  });

  it('the action entry graph does not import the oracle generator', () => {
    // Static one-hop check on the action entry points: no production entry
    // file imports anything under prefix-contract test tooling.
    const entries = ['src/main.ts', 'src/runtime.ts'];
    for (const entry of entries) {
      const text = readFileSync(entry, 'utf8');
      expect(text, entry).not.toContain('generate-fixtures.testhelper');
    }
  });
});
