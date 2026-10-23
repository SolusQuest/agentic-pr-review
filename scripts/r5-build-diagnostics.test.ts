import {
  mkdtemp,
  writeFile,
  readFile,
  lstat,
  rm,
  symlink,
  mkdir,
  truncate,
  readdir,
} from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, test } from 'vitest';
import {
  buildFailureSummary,
  retainBuildFailure,
} from '../runtime/scripts/r5-build-diagnostics.mjs';

const roots: string[] = [];
afterEach(async () => {
  for (const root of roots.splice(0)) await rm(root, { recursive: true, force: true });
});
async function fixture() {
  const root = await mkdtemp(join(tmpdir(), 'apr-r5-diagnostic-test-'));
  roots.push(root);
  const sourceRoot = join(root, 'build');
  await mkdir(sourceRoot, { mode: 0o700 });
  await writeFile(join(sourceRoot, '.r5-v1-temp-root'), '');
  const rawPath = join(sourceRoot, 'build-framework.log');
  const binlogPath = join(sourceRoot, 'build-framework.binlog');
  const raw =
    'error MSB4018: The "GenerateDepsFile" task failed unexpectedly.\n' +
    'error MSB4018: System.IO.IOException: /PRIVATE_JOB_PATH/AgenticPrReview.Runtime.deps.json is being used by another process.\n' +
    'PRIVATE_SOURCE_CANARY PRIVATE_TOKEN=PRIVATE_ENV_VALUE\n';
  const binary = Buffer.from([0, 255, 128, 4, 10, 0]);
  await writeFile(rawPath, raw);
  await writeFile(binlogPath, binary);
  return { root, sourceRoot, rawPath, binlogPath, raw, binary };
}
describe('private R5 build failure diagnostics', () => {
  test('retains the complete raw and binary bytes in owner-only files with source binding', async () => {
    const f = await fixture();
    const result = await retainBuildFailure({ ...f, parentDirectory: f.root });
    expect(await readFile(join(result.destination, 'build.raw.log'), 'utf8')).toBe(f.raw);
    expect(await readFile(join(result.destination, 'build.binlog'))).toEqual(f.binary);
    expect((await lstat(result.destination)).mode & 0o777).toBe(0o700);
    for (const file of ['build.raw.log', 'build.binlog', 'manifest.json'])
      expect((await lstat(join(result.destination, file))).mode & 0o777).toBe(0o600);
    const manifest = JSON.parse(await readFile(join(result.destination, 'manifest.json'), 'utf8'));
    expect(manifest.source.commit).toMatch(/^[a-f0-9]{40}$/);
    expect(manifest.source.tree).toMatch(/^[a-f0-9]{40}$/);
    expect(manifest.files).toHaveLength(2);
    expect(manifest.files[1].bytes).toBe(f.binary.length);
    expect(manifest.files[1].sha256).toMatch(/^[a-f0-9]{64}$/);
    expect(result.publicSummary).toMatchObject({
      retained: true,
      codes: ['MSB4018'],
      tasks: ['GenerateDepsFile'],
      exceptions: ['System.IO.IOException'],
      reasons: ['sharing_violation'],
      targets: ['runtime_deps'],
    });
    expect(JSON.stringify(result.publicSummary)).not.toMatch(/PRIVATE|\/|TOKEN|ENV_VALUE/);
    await rm(f.sourceRoot, { recursive: true });
    expect(await readFile(join(result.destination, 'build.raw.log'), 'utf8')).toBe(f.raw);
  });

  test('does not reflect unknown task names, exception text, source or environment material', () => {
    const result = buildFailureSummary(
      'error MSB4018: The "PRIVATE_TASK" task failed unexpectedly.\n' +
        'PRIVATE_EXCEPTION: PRIVATE_TOKEN /private/path\nAt PRIVATE_SOURCE.cs:1\n',
    );
    expect(result).toEqual({
      codes: ['MSB4018'],
      tasks: [],
      exceptions: [],
      reasons: [],
      targets: [],
    });
  });

  test('keeps a closed bounded summary even when every known diagnostic kind occurs', () => {
    const text = [
      'error MSB4018: The "GenerateDepsFile" task failed unexpectedly.',
      'error CS0001: System.OutOfMemoryException: out of memory',
      'error NU1301: System.UnauthorizedAccessException: permission denied',
      'error NETSDK1004: System.IO.FileNotFoundException: no such file or directory project.assets.json',
    ]
      .join('\n')
      .repeat(1000);
    const value = buildFailureSummary(text);
    expect(value.codes).toHaveLength(4);
    expect(value.exceptions).toHaveLength(3);
    expect(Buffer.byteLength(JSON.stringify(value))).toBeLessThan(2048);
  });

  test('actual CLI prints only the closed report, including when capture fails', async () => {
    const f = await fixture();
    const entry = resolve('runtime/scripts/r5-build-diagnostics.mjs');
    const good = spawnSync(process.execPath, [entry, f.rawPath, f.binlogPath, f.root], {
      encoding: 'utf8',
    });
    expect(good.status).toBe(0);
    expect(good.stderr).toBe('');
    expect(good.stdout).toMatch(/^APR_R5_EVAL_BUILD_DIAGNOSTIC \{/);
    expect(good.stdout).not.toContain(f.root);
    expect(good.stdout).not.toContain('PRIVATE');
    const bad = spawnSync(process.execPath, [entry, f.rawPath, '/PRIVATE_ABSENT_BINLOG', f.root], {
      encoding: 'utf8',
    });
    expect(bad.status).toBe(1);
    expect(bad.stderr).toBe('');
    expect(bad.stdout).toContain('"retained":false');
    expect(bad.stdout).not.toContain('PRIVATE');
  });

  test('refuses symlinks, nonfiles, unexpected names and split input parents', async () => {
    const f = await fixture();
    const other = await fixture();
    await rm(other.rawPath);
    await symlink(f.rawPath, other.rawPath);
    await expect(
      retainBuildFailure({
        rawPath: other.rawPath,
        binlogPath: other.binlogPath,
        parentDirectory: f.root,
      }),
    ).rejects.toThrow();
    await rm(other.rawPath);
    await mkdir(other.rawPath);
    await expect(
      retainBuildFailure({
        rawPath: other.rawPath,
        binlogPath: other.binlogPath,
        parentDirectory: f.root,
      }),
    ).rejects.toThrow();
    await expect(
      retainBuildFailure({
        rawPath: f.rawPath,
        binlogPath: other.binlogPath,
        parentDirectory: f.root,
      }),
    ).rejects.toThrow();
  });

  test('refuses unmarked roots, oversized raw files and cleanup-owned destinations', async () => {
    const f = await fixture();
    await expect(retainBuildFailure({ ...f, parentDirectory: f.sourceRoot })).rejects.toThrow(
      'diagnostic_destination_refused',
    );
    await rm(join(f.sourceRoot, '.r5-v1-temp-root'));
    await expect(retainBuildFailure({ ...f, parentDirectory: f.root })).rejects.toThrow();
    await writeFile(join(f.sourceRoot, '.r5-v1-temp-root'), '');
    await truncate(f.rawPath, 64 * 1024 * 1024 + 1);
    await expect(retainBuildFailure({ ...f, parentDirectory: f.root })).rejects.toThrow(
      'diagnostic_input_refused',
    );
  });

  test('refuses evidence destinations in the checkout', async () => {
    const f = await fixture();
    await expect(retainBuildFailure({ ...f, parentDirectory: process.cwd() })).rejects.toThrow(
      'diagnostic_destination_refused',
    );
  });

  test('R5 changes only logging and failure retention, preserving original build parameters', async () => {
    const script = await readFile('runtime/scripts/verify-r5-evaluation.sh', 'utf8');
    expect(script).toContain(
      '--artifacts-path "${BUILD}/artifacts-framework" -o "${BUILD}/framework"',
    );
    expect(script).toContain('"-bl:${binlog}" >"${log}" 2>&1');
    expect(script).toContain('"${RUNNER_TEMP:-${TMPDIR:-/tmp}}" >&2 2>/dev/null || true');
    expect(script).toContain('_fail "APR_R5_EVAL_FRAMEWORK_BUILD_FAILED"');
    expect(script).not.toContain('maxcpucount:1 --artifacts-path');
    expect(script).not.toContain('upload-artifact');
  });

  test('actual shell preserves build failure and private evidence through its EXIT cleanup', async () => {
    const parent = await mkdtemp(join(tmpdir(), 'apr-r5-shell-failure-'));
    roots.push(parent);
    const command = join(parent, 'failed-build.sh');
    await writeFile(
      command,
      `#!/usr/bin/env bash
for task_arg in "$@"; do
  case "$task_arg" in -bl:*) printf 'PRIVATE_BINARY_LOG' > "\${task_arg#-bl:}";; esac
done
printf '%s\\n' 'error MSB4018: The "GenerateDepsFile" task failed unexpectedly.'
printf '%s\\n' 'error MSB4018: System.IO.IOException: PRIVATE_BUILD_PATH/AgenticPrReview.Runtime.deps.json is being used by another process.'
printf '%s\\n' 'PRIVATE_SOURCE_CANARY PRIVATE_ENV_VALUE'
exit 1
`,
      { mode: 0o700 },
    );
    const result = spawnSync('bash', ['runtime/scripts/verify-r5-evaluation.sh', 'framework'], {
      env: { ...process.env, DOTNET_CMD: command, RUNNER_TEMP: parent, TMPDIR: parent },
      encoding: 'utf8',
      timeout: 10000,
    });
    expect(result.status).toBe(1);
    expect(result.stdout).toBe('');
    expect(result.stderr).toContain('APR_R5_EVAL_FRAMEWORK_BUILD_FAILED');
    expect(result.stderr).toContain('APR_R5_EVAL_BUILD_ERROR code=MSB4018');
    expect(result.stderr).toContain('"retained":true');
    expect(result.stderr).toContain('"tasks":["GenerateDepsFile"]');
    expect(result.stderr).not.toContain('PRIVATE');
    expect(result.stderr).not.toContain(parent);
    const entries = await readdir(parent, { withFileTypes: true });
    const directories = entries.filter((entry) => entry.isDirectory());
    expect(directories).toHaveLength(1);
    expect(directories[0].name).toMatch(/^apr-r5-build-diagnostic-/);
    expect(await readFile(join(parent, directories[0].name, 'build.binlog'), 'utf8')).toBe(
      'PRIVATE_BINARY_LOG',
    );
  });
});
