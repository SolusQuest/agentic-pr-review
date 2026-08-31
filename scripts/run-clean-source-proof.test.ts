import { execFileSync, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';

const repositoryRoot = path.resolve(import.meta.dirname, '..');
const runner = path.join(repositoryRoot, 'scripts', 'run-clean-source-proof.mjs');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) {
    fs.rmSync(root, { force: true, recursive: true });
  }
});

function git(root: string, ...args: string[]) {
  return execFileSync('git', ['-C', root, ...args], { encoding: 'utf8' }).trim();
}

function fixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-clean-source-proof-'));
  temporaryRoots.push(root);
  git(root, 'init', '--quiet');
  git(root, 'config', 'user.name', 'APR Test');
  git(root, 'config', 'user.email', 'apr-test@example.invalid');
  fs.writeFileSync(path.join(root, 'tracked.txt'), 'baseline\n');
  git(root, 'add', 'tracked.txt');
  git(root, 'commit', '--quiet', '-m', 'baseline');
  return root;
}

function run(root: string, command: string, args: string[] = []) {
  return spawnSync(process.execPath, [runner, '--repo', root, '--', command, ...args], {
    cwd: root,
    encoding: 'utf8',
  });
}

function expectNoMarkers(result: ReturnType<typeof run>) {
  expect(result.status).not.toBe(0);
  expect(result.stdout).not.toContain('APR_R4_W13_SOURCE_COMMIT');
  expect(result.stdout).not.toContain('APR_R4_W13_SOURCE_TREE');
}

describe('clean-source proof runner', () => {
  test('emits the exact clean commit and tree only after a successful command', () => {
    const root = fixture();
    const result = run(root, process.execPath, ['-e', 'process.stdout.write("proof-ok\\n")']);

    expect(result.status).toBe(0);
    expect(result.stdout).toBe(
      `proof-ok\nAPR_R4_W13_SOURCE_COMMIT ${git(root, 'rev-parse', 'HEAD^{commit}')}\n` +
        `APR_R4_W13_SOURCE_TREE ${git(root, 'rev-parse', 'HEAD^{tree}')}\n`,
    );
  }, 15_000);

  test('rejects staged tracked drift without emitting markers', () => {
    const root = fixture();
    fs.writeFileSync(path.join(root, 'staged.txt'), 'staged\n');
    git(root, 'add', 'staged.txt');
    expectNoMarkers(run(root, process.execPath, ['-e', 'process.exit(0)']));
  });

  test('rejects unstaged tracked drift without emitting markers', () => {
    const root = fixture();
    fs.appendFileSync(path.join(root, 'tracked.txt'), 'dirty\n');
    expectNoMarkers(run(root, process.execPath, ['-e', 'process.exit(0)']));
  });

  test('rejects non-ignored untracked input without emitting markers', () => {
    const root = fixture();
    fs.writeFileSync(path.join(root, 'build-input.txt'), 'untracked\n');
    expectNoMarkers(run(root, process.execPath, ['-e', 'process.exit(0)']));
  });

  test('suppresses markers after a failed child command', () => {
    const root = fixture();
    expectNoMarkers(run(root, process.execPath, ['-e', 'process.exit(7)']));
  });

  test.skipIf(process.platform === 'win32')(
    'strict framework wrapper preserves an early failure before a later success',
    () => {
      const root = fixture();
      const result = run(root, 'bash', [
        '-euo',
        'pipefail',
        '-c',
        'false; printf "later-command-ran\\n"',
      ]);

      expectNoMarkers(result);
      expect(result.stdout).not.toContain('later-command-ran');
    },
  );

  test('rejects post-command drift without emitting markers', () => {
    const root = fixture();
    const mutation = 'require("node:fs").appendFileSync("tracked.txt", "post\\n")';
    expectNoMarkers(run(root, process.execPath, ['-e', mutation]));
  });
});
