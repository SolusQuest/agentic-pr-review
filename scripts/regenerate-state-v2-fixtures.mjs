// Maintainer script: regenerates the committed StateManifestV2 positive
// fixtures under protocol/fixtures/state-manifest-v2/. Run this only when
// the canonical serializer or builder is intentionally changed, then
// review and commit the updated fixture bytes.
//
// Uses esbuild (already a dev dependency) to bundle the TypeScript
// generator to a temporary ESM file and executes it in a child Node
// process. Keeping this file as .mjs avoids depending on Node's
// experimental TypeScript stripping.

import { spawnSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import esbuild from 'esbuild';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..');
const entry = path.join(repoRoot, 'src', 'state-v2', 'regenerate-fixtures.testhelper.ts');

const workdir = mkdtempSync(path.join(tmpdir(), 'state-v2-regen-'));
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
// Setting process.exitCode (rather than calling process.exit) lets Node
// flush any pending output and lets the finally block above complete
// cleanly before the process terminates.
process.exitCode = exitCode;
