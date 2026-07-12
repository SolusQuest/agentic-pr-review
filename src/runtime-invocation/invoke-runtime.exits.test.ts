import { spawn } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';
import { RuntimeInvocationError } from './invoke-runtime.js';
import { invokeRuntimeForTests } from './invoke-runtime.test-support.js';
import {
  createTempRootRegistry,
  expectFailure,
  fakeRuntimePath,
  readBootstrapInput,
} from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

describe('invokeRuntime - non-zero exits and APR codes', () => {
  const cases: Array<{ scenario: string; exitCode: number; exitClass: string; aprCode?: string }> =
    [
      { scenario: 'exit-2', exitCode: 2, exitClass: 'usage', aprCode: 'APR_USAGE_INVALID' },
      {
        scenario: 'exit-10',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_RUNTIME_VERSION_MISMATCH',
      },
      { scenario: 'exit-10-input-read', exitCode: 10, exitClass: 'contract' },
      {
        scenario: 'exit-10-input-json',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_INPUT_JSON_INVALID',
      },
      {
        scenario: 'exit-10-protocol-version',
        exitCode: 10,
        exitClass: 'contract',
        aprCode: 'APR_PROTOCOL_VERSION_UNSUPPORTED',
      },
      { scenario: 'exit-20', exitCode: 20, exitClass: 'runtime', aprCode: 'APR_RUNTIME_INTERNAL' },
      {
        scenario: 'exit-20-self-validation',
        exitCode: 20,
        exitClass: 'runtime',
        aprCode: 'APR_OUTPUT_SELF_VALIDATION_FAILED',
      },
      { scenario: 'exit-30', exitCode: 30, exitClass: 'provider', aprCode: 'APR_PROVIDER_FAILED' },
      {
        scenario: 'exit-40-trace-write',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_TRACE_WRITE_FAILED',
      },
      {
        scenario: 'exit-40',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_RESULT_WRITE_FAILED',
      },
      {
        scenario: 'exit-40-input-read',
        exitCode: 40,
        exitClass: 'file-io',
        aprCode: 'APR_INPUT_READ_FAILED',
      },
    ];
  for (const c of cases) {
    it(`maps ${c.scenario} to ${c.exitCode} (${c.exitClass}${c.aprCode ? '/' + c.aprCode : ''})`, async () => {
      const err = await expectFailure({ scenario: c.scenario }, registry, 'runtime-exit');
      expect(err.exitCode).toBe(c.exitCode);
      expect(err.exitClass).toBe(c.exitClass);
      if (c.aprCode) expect(err.diagnosticCode).toBe(c.aprCode);
    });
  }
  it('maps unknown non-zero exit to unknown-exit', async () => {
    const err = await expectFailure({ scenario: 'exit-77' }, registry, 'unknown-exit');
    expect(err.exitCode).toBe(77);
    expect(err.diagnosticCode).toBeUndefined();
  });
  it('drops APR_* code when its class does not match the observed exit class', async () => {
    // exit-2 emitted with a provider APR code should be dropped
    const err = await expectFailure(
      { scenario: 'exit-2-mismatched-apr' },
      registry,
      'runtime-exit',
    );
    expect(err.exitCode).toBe(2);
    expect(err.exitClass).toBe('usage');
    expect(err.diagnosticCode).toBeUndefined();
  });
});

describe('invokeRuntime - failure trace diagnostics', () => {
  it('exposes failure trace diagnostics when provenance holds', async () => {
    const err = await expectFailure(
      { scenario: 'exit-10-with-failure-trace' },
      registry,
      'runtime-exit',
    );
    expect(err.failureTraceDiagnostics?.[0]?.code).toBe('FAKE_OK');
    // Positive-class case for APR_INPUT_SCHEMA_INVALID (documented at exit 10 contract).
    expect(err.diagnosticCode).toBe('APR_INPUT_SCHEMA_INVALID');
  });
  it('omits diagnostics when failure trace inputSha256 does not match', async () => {
    const err = await expectFailure(
      { scenario: 'exit-20-with-mismatched-failure-trace' },
      registry,
      'runtime-exit',
    );
    expect(err.failureTraceDiagnostics).toBeUndefined();
  });
  it('treats orphan trace on exit 40 as diagnostic only', async () => {
    const err = await expectFailure({ scenario: 'orphan-trace-exit-40' }, registry, 'runtime-exit');
    expect(err.exitClass).toBe('file-io');
    expect(err.failureTraceDiagnostics?.[0]?.code).toBe('FAKE_OK');
  });
});

describe('invokeRuntime - error hygiene', () => {
  it('does not embed input content in error messages', async () => {
    const err = await expectFailure(
      {
        scenario: 'success',
        input: {
          ...readBootstrapInput(),
          protocolVersion: 999 as unknown as 1,
        },
      },
      registry,
      'input-invalid',
    );
    expect(err.message).not.toMatch(/999/);
    expect(err.message).not.toMatch(/protocolVersion/);
  }, 8000);
  it('does not embed invocation directory paths in error messages or stderrSnippet', async () => {
    const err = await expectFailure({ scenario: 'exit-20' }, registry, 'runtime-exit');
    expect(err.message).not.toMatch(/runtime-\w{6,}/);
    if (err.stderrSnippet) expect(err.stderrSnippet).not.toMatch(/runtime-\w{6,}/);
  }, 8000);
  it('exposes a sanitized diagnosticCode on spawn-failed instead of raw cause', async () => {
    const tempRoot = await registry.acquire();
    const err = await invokeRuntimeForTests(
      {
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        tempRoot,
      },
      {
        spawnOverride: (() => {
          const e = new Error('spawn EACCES') as NodeJS.ErrnoException;
          e.code = 'EACCES';
          throw e;
        }) as unknown as typeof spawn,
      },
    ).catch((e) => e);
    expect(err).toBeInstanceOf(RuntimeInvocationError);
    expect((err as RuntimeInvocationError).kind).toBe('spawn-failed');
    expect((err as RuntimeInvocationError).diagnosticCode).toBe('EACCES');
  }, 8000);
});
