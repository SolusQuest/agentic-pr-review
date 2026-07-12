import { afterEach, describe, expect, it } from 'vitest';
import { createTempRootRegistry, expectFailure } from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

describe('invokeRuntime - stream contract', () => {
  it('flags stdout leak on exit 0 as process-contract-violation before file validation', async () => {
    // Even without result files, stream shape violates first.
    const err = await expectFailure(
      { scenario: 'stdout-leak-no-output' },
      registry,
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stdout-nonempty')).toBe(true);
  });
  it('flags stdout leak on exit 0 as process-contract-violation even with valid output', async () => {
    const err = await expectFailure(
      { scenario: 'stdout-leak-small' },
      registry,
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stdout-nonempty')).toBe(true);
  });
  it('flags stderr over contract limit on exit 0 as process-contract-violation', async () => {
    const err = await expectFailure(
      { scenario: 'stderr-over-contract-success' },
      registry,
      'process-contract-violation',
    );
    expect(err.contractViolations?.some((v) => v.kind === 'stderr-over-contract')).toBe(true);
  });
  it('terminates the child and reports stream-limit-exceeded for stdout flood', async () => {
    const err = await expectFailure(
      { scenario: 'stdout-flood', timeoutMs: 10_000 },
      registry,
      'stream-limit-exceeded',
    );
    expect(err.contractViolations?.[0]?.kind).toBe('stdout-over-capture');
  });
  it('terminates the child and reports stream-limit-exceeded for stderr flood', async () => {
    const err = await expectFailure(
      { scenario: 'stderr-flood', timeoutMs: 10_000 },
      registry,
      'stream-limit-exceeded',
    );
    expect(err.contractViolations?.[0]?.kind).toBe('stderr-over-capture');
  });
  it('sanitizes stderrSnippet: drops non-UTF-8 first line', async () => {
    const err = await expectFailure({ scenario: 'stderr-non-utf8' }, registry, 'runtime-exit');
    expect(err.stderrSnippet).toBeUndefined();
  });
  it('sanitizes stderrSnippet: strips control characters', async () => {
    const err = await expectFailure({ scenario: 'stderr-control-chars' }, registry, 'runtime-exit');
    expect(err.stderrSnippet).toBeDefined();
    expect(err.stderrSnippet!).toMatch(/^[\x20-\x7e]+$/);
  });
  it('sanitizes stderrSnippet: drops when it contains the invocation path', async () => {
    const err = await expectFailure({ scenario: 'stderr-path-leak' }, registry, 'runtime-exit');
    expect(err.stderrSnippet).toBeUndefined();
  });
});
