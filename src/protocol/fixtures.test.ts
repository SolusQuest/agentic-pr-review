import { describe, it, expect } from 'vitest';
import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
import { validateReviewInputV1 } from './review-input.js';
import { validateReviewResultV1 } from './review-result.js';
import { validateReviewTraceV1 } from './review-trace.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixturesDir = join(here, '..', '..', 'protocol', 'fixtures', 'v1');

interface FixtureEntry {
  type: 'fixture';
  file: string;
  contract: 'input' | 'result' | 'trace';
  valid: boolean;
  expectedErrorIncludes?: string[];
}

interface CaseEntry {
  type: 'case';
  directory: string;
  contracts: {
    input: string;
    result: string;
    trace: string;
  };
  valid: boolean;
  verifyHashChain: string[];
}

type ManifestEntry = FixtureEntry | CaseEntry;

const manifestPath = join(fixturesDir, 'manifest.json');
const manifest: ManifestEntry[] = JSON.parse(readFileSync(manifestPath, 'utf8'));

function loadJson(filePath: string): unknown {
  return JSON.parse(readFileSync(filePath, 'utf8'));
}

function validateByContract(contract: string, value: unknown): { ok: boolean; errors?: string[] } {
  if (contract === 'input') return validateReviewInputV1(value);
  if (contract === 'result') return validateReviewResultV1(value);
  if (contract === 'trace') return validateReviewTraceV1(value);
  throw new Error(`Unknown contract: ${contract}`);
}

function sha256OfFile(filePath: string): string {
  const bytes = readFileSync(filePath);
  return createHash('sha256').update(bytes).digest('hex');
}

function getNestedValue(obj: unknown, path: string): unknown {
  const parts = path.split('.');
  let current: unknown = obj;
  for (const part of parts) {
    if (current === null || current === undefined || typeof current !== 'object') {
      return undefined;
    }
    current = (current as Record<string, unknown>)[part];
  }
  return current;
}

const fixtureEntries = manifest.filter((e): e is FixtureEntry => e.type === 'fixture');
const caseEntries = manifest.filter((e): e is CaseEntry => e.type === 'case');

describe('Protocol fixture matrix - manifest integrity', () => {
  it('manifest has expected number of entries', () => {
    expect(fixtureEntries.length).toBe(28);
    expect(caseEntries.length).toBe(1);
  });

  it('all fixture files referenced in manifest exist', () => {
    for (const entry of fixtureEntries) {
      const filePath = join(fixturesDir, entry.file);
      expect(() => readFileSync(filePath, 'utf8')).not.toThrow();
    }
  });

  it('all case directories referenced in manifest exist', () => {
    for (const entry of caseEntries) {
      for (const file of Object.values(entry.contracts)) {
        const filePath = join(fixturesDir, entry.directory, file);
        expect(() => readFileSync(filePath, 'utf8')).not.toThrow();
      }
    }
  });
});

describe('Protocol fixture matrix - positive fixtures', () => {
  const positives = fixtureEntries.filter((e) => e.valid);
  for (const entry of positives) {
    it(`${entry.file} passes ${entry.contract} validation`, () => {
      const value = loadJson(join(fixturesDir, entry.file));
      const result = validateByContract(entry.contract, value);
      expect(result.ok).toBe(true);
    });
  }
});

describe('Protocol fixture matrix - negative fixtures', () => {
  const negatives = fixtureEntries.filter((e) => !e.valid);
  for (const entry of negatives) {
    it(`${entry.file} fails ${entry.contract} validation with expected error`, () => {
      const value = loadJson(join(fixturesDir, entry.file));
      const result = validateByContract(entry.contract, value);
      expect(result.ok).toBe(false);
      expect(result.errors).toBeDefined();
      const errors = (result.errors ?? []).join(' ');
      for (const expected of entry.expectedErrorIncludes ?? []) {
        expect(errors).toContain(expected);
      }
    });
  }
});

describe('Protocol fixture matrix - paired case hash-chain verification', () => {
  for (const entry of caseEntries) {
    describe(`case: ${entry.directory}`, () => {
      const caseDir = join(fixturesDir, entry.directory);
      const inputPath = join(caseDir, entry.contracts.input);
      const resultPath = join(caseDir, entry.contracts.result);
      const tracePath = join(caseDir, entry.contracts.trace);

      const input = loadJson(inputPath);
      const result = loadJson(resultPath);
      const trace = loadJson(tracePath);

      it('input passes validation', () => {
        expect(validateByContract('input', input).ok).toBe(true);
      });

      it('result passes validation', () => {
        expect(validateByContract('result', result).ok).toBe(true);
      });

      it('trace passes validation', () => {
        expect(validateByContract('trace', trace).ok).toBe(true);
      });

      it('trace.resultSha256 is omitted (non-circular)', () => {
        const traceObj = trace as Record<string, unknown>;
        expect(traceObj.resultSha256).toBeUndefined();
      });

      for (const hashRef of entry.verifyHashChain) {
        const [contract, ...pathParts] = hashRef.split('.');
        const pathStr = pathParts.join('.');

        it(`${hashRef} matches sha256 of source file bytes`, () => {
          let sourceFile: string;

          if (hashRef.startsWith('result.inputSha256')) {
            sourceFile = inputPath;
          } else if (hashRef.startsWith('trace.inputSha256')) {
            sourceFile = inputPath;
          } else if (hashRef.startsWith('result.trace.sha256')) {
            sourceFile = tracePath;
          } else {
            throw new Error(`Unknown hash reference: ${hashRef}`);
          }

          const expectedHash = getNestedValue(contract === 'result' ? result : trace, pathStr);
          const actualHash = sha256OfFile(sourceFile);
          expect(expectedHash).toBe(actualHash);
        });
      }
    });
  }
});

describe('Protocol fixture matrix - no orphan fixtures', () => {
  it('all .json files in fixtures dir are referenced by manifest', () => {
    const allFiles = readdirSync(fixturesDir).filter(
      (f) => f.endsWith('.json') && f !== 'manifest.json',
    );
    const manifestFiles = new Set(fixtureEntries.map((e) => e.file));
    for (const file of allFiles) {
      expect(manifestFiles.has(file)).toBe(true);
    }
  });
});
