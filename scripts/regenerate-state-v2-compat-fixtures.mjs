// Maintainer script: regenerates the committed StateManifestV2 compatibility
// fixtures under protocol/fixtures/state-manifest-v2-compat/. Run this only
// when the compatibility comparator or its input contract changes; review
// and commit the updated fixture bytes.
//
// Uses esbuild to bundle the TypeScript generator to a temp file and runs
// it in a child Node process. Same pattern as the positive-bundle
// regenerator.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import esbuild from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const entry = path.join(repoRoot, 'src', 'state-v2', 'regenerate-compat-fixtures.testhelper.ts');

const workdir = mkdtempSync(path.join(tmpdir(), 'state-v2-compat-regen-'));
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
process.exitCode = exitCode;
