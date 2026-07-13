import { mkdtemp, mkdir, realpath, rm, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { resolveTrustedRuntimeCommand } from './command-resolver.js';

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});

async function fixtureRoot(): Promise<{ workspace: string; outside: string; executable: string }> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'agentic-command-resolver-'));
  roots.push(root);
  const workspace = path.join(root, 'workspace');
  const outside = path.join(root, 'outside');
  await mkdir(workspace);
  await mkdir(outside);
  const executable = path.join(outside, process.platform === 'win32' ? 'runtime.exe' : 'runtime');
  await writeFile(executable, 'fixture');
  return { workspace, outside, executable };
}

describe('resolveTrustedRuntimeCommand', () => {
  it('resolves a trusted executable and opaque prefix flags', async () => {
    const fixture = await fixtureRoot();
    const resolved = await resolveTrustedRuntimeCommand({
      GITHUB_WORKSPACE: fixture.workspace,
      AGENTIC_REVIEW_RUNTIME_EXECUTABLE: fixture.executable,
      AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON: '["--framework", "@/opaque/path"]',
    });
    expect(resolved.command.executablePath).toBe(fixture.executable);
    expect(resolved.command.prefixArgs).toEqual(['--framework', '@/opaque/path']);
  });

  it('canonicalizes absolute prefix paths before returning the command', async () => {
    const fixture = await fixtureRoot();
    const prefixPath = path.join(fixture.outside, 'framework.dll');
    await writeFile(prefixPath, 'fixture');
    const resolved = await resolveTrustedRuntimeCommand({
      GITHUB_WORKSPACE: fixture.workspace,
      AGENTIC_REVIEW_RUNTIME_EXECUTABLE: fixture.executable,
      AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON: JSON.stringify([prefixPath]),
    });
    expect(resolved.command.prefixArgs).toEqual([await realpath(prefixPath)]);
  });

  it('rejects malformed prefix JSON', async () => {
    const fixture = await fixtureRoot();
    await expect(
      resolveTrustedRuntimeCommand({
        GITHUB_WORKSPACE: fixture.workspace,
        AGENTIC_REVIEW_RUNTIME_EXECUTABLE: fixture.executable,
        AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON: '{',
      }),
    ).rejects.toThrow(/config-invalid/);
  });

  it('rejects executables and absolute prefix paths inside the checkout', async () => {
    const fixture = await fixtureRoot();
    const checkoutExecutable = path.join(fixture.workspace, 'runtime');
    await writeFile(checkoutExecutable, 'fixture');
    await expect(
      resolveTrustedRuntimeCommand({
        GITHUB_WORKSPACE: fixture.workspace,
        AGENTIC_REVIEW_RUNTIME_EXECUTABLE: checkoutExecutable,
      }),
    ).rejects.toThrow(/command-unavailable/);

    const trustedExecutable = path.join(fixture.outside, 'runtime-2');
    await writeFile(trustedExecutable, 'fixture');
    const checkoutArg = path.join(fixture.workspace, 'framework.dll');
    await writeFile(checkoutArg, 'fixture');
    await expect(
      resolveTrustedRuntimeCommand({
        GITHUB_WORKSPACE: fixture.workspace,
        AGENTIC_REVIEW_RUNTIME_EXECUTABLE: trustedExecutable,
        AGENTIC_REVIEW_RUNTIME_PREFIX_ARGS_JSON: JSON.stringify([checkoutArg]),
      }),
    ).rejects.toThrow(/command-unavailable/);
  });
});
