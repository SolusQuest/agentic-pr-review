// Maintainer script: regenerates the committed prefix-contract golden
// vectors under protocol/fixtures/prefix-contract/v1/. Run this only when
// the canonical projection or hash framing is intentionally changed, then
// review and commit the updated fixture bytes.
//
// Mirrors scripts/regenerate-state-v2-fixtures.mjs: bundles the TypeScript
// generator with esbuild and executes it in a child Node process.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import esbuild from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const entry = path.join(repoRoot, 'src', 'prefix-contract', 'generate-fixtures.testhelper.ts');

const workdir = mkdtempSync(path.join(tmpdir(), 'prefix-contract-regen-'));
let exitCode = 0;
try {
  const bundlePath = path.join(workdir, 'regen.mjs');
  await esbuild.build({
    entryPoints: [entry],
    bundle: true,
    platform: 'node',
    target: 'node22',
    format: 'esm',
    outfile: bundlePath,
    logLevel: 'error',
  });
  const result = spawnSync(process.execPath, [bundlePath], {
    stdio: 'inherit',
    cwd: repoRoot,
  });
  exitCode = result.status ?? 1;
} finally {
  rmSync(workdir, { recursive: true, force: true });
}
process.exit(exitCode);
