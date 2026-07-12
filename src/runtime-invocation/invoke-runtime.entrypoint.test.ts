import { symlink } from 'node:fs/promises';
import { join } from 'node:path';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import type { ReviewInputV1 } from '../protocol/review-input.js';
import { invokeRuntime } from './invoke-runtime.js';
import {
  createTempRootRegistry,
  expectFailure,
  fakeRuntimePath,
  readBootstrapInput,
} from './invoke-runtime.test-helpers.js';

const registry = createTempRootRegistry();
afterEach(() => registry.cleanup());

describe('invokeRuntime - public entrypoint', () => {
  it('is a single-argument function without test seams on its signature', () => {
    expect(invokeRuntime.length).toBe(1);
  });
  it('rejects a malformed options object', async () => {
    await expect(invokeRuntime(null as unknown as never)).rejects.toMatchObject({
      kind: 'options-invalid',
    });
  });
});

describe('invokeRuntime - options validation', () => {
  const input = readBootstrapInput();
  const validCommand = { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] };

  it('rejects a null command', async () => {
    await expect(
      invokeRuntime({ command: null as unknown as never, input, timeoutMs: 1000 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a null input', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input: null as unknown as ReviewInputV1,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-positive timeout', async () => {
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 0 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-integer timeout', async () => {
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 1.5 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a relative tempRoot', async () => {
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 1000, tempRoot: 'relative-path' }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a non-absolute executablePath', async () => {
    await expect(
      invokeRuntime({ command: { executablePath: 'relative-runtime' }, input, timeoutMs: 1000 }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects prefixArgs containing non-strings', async () => {
    await expect(
      invokeRuntime({
        command: { executablePath: process.execPath, prefixArgs: [123 as unknown as string] },
        input,
        timeoutMs: 1000,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a signal missing addEventListener', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: { aborted: false } as unknown as AbortSignal,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('rejects a signal with a non-boolean aborted', async () => {
    await expect(
      invokeRuntime({
        command: validCommand,
        input,
        timeoutMs: 1000,
        signal: {
          aborted: 'nope',
          addEventListener: () => undefined,
          removeEventListener: () => undefined,
        } as unknown as AbortSignal,
      }),
    ).rejects.toMatchObject({ kind: 'options-invalid' });
  });
  it('fails as cancelled if signal already aborted', async () => {
    const ctrl = new AbortController();
    ctrl.abort();
    await expect(
      invokeRuntime({ command: validCommand, input, timeoutMs: 1000, signal: ctrl.signal }),
    ).rejects.toMatchObject({ kind: 'cancelled' });
  });
});

describe('invokeRuntime - preflight validation', () => {
  it('fails as input-invalid when ReviewInputV1 schema fails', async () => {
    const err = await expectFailure(
      {
        scenario: 'success',
        input: { ...readBootstrapInput(), protocolVersion: 2 as unknown as 1 },
      },
      registry,
      'input-invalid',
    );
    expect(err.message).toBe('ReviewInputV1 schema validation failed (1 errors).');
    // Sanity: no raw property names or values leaked.
    expect(err.message).not.toMatch(/protocolVersion|additionalProperty/i);
  });
  it('fails as executable-invalid for a missing binary', async () => {
    const tempRoot = await registry.acquire();
    await expect(
      invokeRuntime({
        command: { executablePath: path.join(tempRoot, 'does-not-exist') },
        input: readBootstrapInput(),
        timeoutMs: 1000,
        tempRoot,
      }),
    ).rejects.toMatchObject({ kind: 'executable-invalid' });
  });
  it('fails as executable-invalid for an executable symlink', async () => {
    if (process.platform === 'win32') return;
    const tempRoot = await registry.acquire();
    const link = path.join(tempRoot, 'node-link');
    await symlink(process.execPath, link);
    await expect(
      invokeRuntime({
        command: { executablePath: link, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        tempRoot,
      }),
    ).rejects.toMatchObject({ kind: 'executable-invalid' });
  });
  it('fails as cancelled when signal aborts mid-preflight', async () => {
    const ctrl = new AbortController();
    // Abort between options validation and mkdtemp; use a signal that aborts before we get there.
    setImmediate(() => ctrl.abort());
    await expect(
      invokeRuntime({
        command: { executablePath: process.execPath, prefixArgs: [fakeRuntimePath] },
        input: readBootstrapInput(),
        timeoutMs: 5000,
        signal: ctrl.signal,
      }),
    ).rejects.toMatchObject({ kind: 'cancelled' });
  });
});
