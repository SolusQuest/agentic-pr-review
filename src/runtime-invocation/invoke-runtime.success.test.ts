import { readdir } from 'node:fs/promises';
import { afterEach, describe, expect, it } from 'vitest';
import {
  createTempRootRegistry,
  expectFailure,
  readBootstrapInput,
  runScenario,
} from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

describe('invokeRuntime - success path', () => {
  it('returns validated result and trace on a clean run', async () => {
    const { invoke } = await runScenario({ scenario: 'success' }, registry);
    const success = await invoke;
    expect(success.result.protocolVersion).toBe(1);
    expect(success.trace.protocolVersion).toBe(1);
    expect(success.inputSha256).toMatch(/^[0-9a-f]{64}$/);
    expect(success.result.inputSha256).toBe(success.inputSha256);
    expect(success.trace.inputSha256).toBe(success.inputSha256);
    expect(success.result.trace?.sha256).toMatch(/^[0-9a-f]{64}$/);
    expect(success.runtimeVersion).toBe('0.1.0-dev');
  });
  it('cleans up the invocation directory on success', async () => {
    const { invoke, tempRoot } = await runScenario({ scenario: 'success' }, registry);
    await invoke;
    const entries = await readdir(tempRoot);
    expect(entries).toEqual([]);
  });
  it('accepts a matching requestedRuntimeVersion', async () => {
    const input = readBootstrapInput();
    input.requestedRuntimeVersion = 'pinned-1.2.3';
    const { invoke } = await runScenario(
      {
        scenario: 'success-with-requested-version',
        input,
        fakeVersion: 'pinned-1.2.3',
      },
      registry,
    );
    const success = await invoke;
    expect(success.runtimeVersion).toBe('pinned-1.2.3');
  });
});

describe('invokeRuntime - success validation failures', () => {
  it('reports missing-output when result.json is absent', async () => {
    await expectFailure({ scenario: 'missing-result' }, registry, 'missing-output');
  });
  it('reports missing-output when trace.json is absent', async () => {
    await expectFailure({ scenario: 'missing-trace' }, registry, 'missing-output');
  });
  it('reports unsafe-output-file when result.json is a symlink', async () => {
    if (process.platform === 'win32') return;
    await expectFailure({ scenario: 'symlink-result' }, registry, 'unsafe-output-file');
  });
  it('reports unsafe-output-file when trace.json is a directory (non-regular)', async () => {
    await expectFailure({ scenario: 'directory-trace' }, registry, 'unsafe-output-file');
  });
  it('reports result-invalid for non-UTF8 bytes', async () => {
    await expectFailure({ scenario: 'invalid-utf8-result' }, registry, 'result-invalid');
  });
  it('reports result-invalid for non-JSON', async () => {
    await expectFailure({ scenario: 'invalid-json-result' }, registry, 'result-invalid');
  });
  it('reports result-invalid for schema failure', async () => {
    const err = await expectFailure(
      { scenario: 'schema-invalid-result' },
      registry,
      'result-invalid',
    );
    expect(err.message).toMatch(/^ReviewResultV1 schema validation failed \(\d+ errors\)\.$/);
  });
  it('reports trace-invalid for non-JSON trace', async () => {
    await expectFailure({ scenario: 'invalid-json-trace' }, registry, 'trace-invalid');
  });
  it('reports process-contract-violation when result.inputSha256 missing', async () => {
    await expectFailure(
      { scenario: 'missing-result-inputsha' },
      registry,
      'process-contract-violation',
    );
  });
  it('reports process-contract-violation when result.trace missing', async () => {
    await expectFailure(
      { scenario: 'missing-result-trace' },
      registry,
      'process-contract-violation',
    );
  });
  it('reports process-contract-violation when result.trace.sha256 missing', async () => {
    await expectFailure(
      { scenario: 'missing-result-trace-sha' },
      registry,
      'process-contract-violation',
    );
  });
  it('reports process-contract-violation when result.trace.path is present', async () => {
    await expectFailure(
      { scenario: 'result-trace-path-present' },
      registry,
      'process-contract-violation',
    );
  });
  it('reports process-contract-violation when trace.resultSha256 is present', async () => {
    await expectFailure(
      { scenario: 'trace-result-sha-present' },
      registry,
      'process-contract-violation',
    );
  });
  it('reports hash-mismatch when result.inputSha256 differs', async () => {
    await expectFailure({ scenario: 'result-inputsha-mismatch' }, registry, 'hash-mismatch');
  });
  it('reports hash-mismatch when trace.inputSha256 differs', async () => {
    await expectFailure({ scenario: 'trace-inputsha-mismatch' }, registry, 'hash-mismatch');
  });
  it('reports hash-mismatch when trace bytes hash differs from result.trace.sha256', async () => {
    await expectFailure({ scenario: 'trace-sha-mismatch' }, registry, 'hash-mismatch');
  });
  it('reports version-mismatch when result/trace runtime versions differ', async () => {
    await expectFailure(
      { scenario: 'result-trace-version-mismatch' },
      registry,
      'version-mismatch',
    );
  });
  it('reports version-mismatch when requestedRuntimeVersion is unmet', async () => {
    const input = readBootstrapInput();
    input.requestedRuntimeVersion = 'requested-abc';
    await expectFailure(
      { scenario: 'requested-version-mismatch', input, fakeVersion: 'requested-abc' },
      registry,
      'version-mismatch',
    );
  });
});

describe('invokeRuntime - byte budgets', () => {
  it('reports unsafe-output-file when result.json exceeds cap', async () => {
    // The oversized-result scenario writes a filler > 8 MiB cap.
    await expectFailure(
      { scenario: 'oversized-result', fillerBytes: 10 * 1024 * 1024 },
      registry,
      'unsafe-output-file',
    );
  }, 8000);
});
