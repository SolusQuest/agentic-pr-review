// Trusted construction; the isolated consumer receives only original archives.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runIsolatedFixture } from './verify-network.mjs';
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
for (const role of ['first', 'independent-second']) {
  await runIsolatedFixture({
    childName: 'archive-child.mjs',
    prefix: 'APR_D5_MANAGED_ARCHIVE ',
    maximumSeconds: 1200,
    prepare: async (root, { source, tree, builderEnv }) => {
      const output = join(root, 'package');
      const result = spawnSync(
        process.execPath,
        [
          join(repo, 'scripts/release/build-payload.mjs'),
          'build-managed',
          '--source',
          source,
          '--version',
          'v0.0.0-internal.1',
          '--output',
          output,
        ],
        {
          cwd: repo,
          env: { ...builderEnv, NUGET_PACKAGES: process.env.NUGET_PACKAGES },
          encoding: 'utf8',
          timeout: 600_000,
          maxBuffer: 1024 * 1024,
        },
      );
      assert.equal(result.error, undefined, 'd5_builder_error');
      assert.equal(result.status, 0, 'd5_builder_failed');
      const identityLines = result.stdout
        .split('\n')
        .filter((x) => x.startsWith('CR1_D1_MANAGED_ARCHIVE '));
      assert.equal(identityLines.length, 1);
      const compiled = JSON.parse(identityLines[0].slice('CR1_D1_MANAGED_ARCHIVE '.length));
      const receipt = JSON.parse(await readFile(join(output, 'receipt.json'), 'utf8'));
      assert.equal(compiled.sourceCommit, source);
      assert.equal(compiled.archiveSha256, receipt.archiveSha256);
      assert.equal(receipt.identity.sourceCommit, source);
      await writeFile(
        join(root, 'archive-settings.json'),
        JSON.stringify({ role, source, tree, compiled, repo }),
        { flag: 'wx', mode: 0o600 },
      );
    },
  });
}
