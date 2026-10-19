// Trusted construction; the isolated consumer receives only original archives.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFile, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runIsolatedFixture } from './verify-network.mjs';
import { prepareCutoverSupport } from './cutover-proof.mjs';
import { verifyManagedCandidateArchive } from '../../../../tests/release/verify-managed-candidate-archive.mjs';
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
for (const role of ['first', 'independent-second']) {
  await runIsolatedFixture({
    childName: 'archive-child.mjs',
    prefix: 'APR_D5_MANAGED_ARCHIVE ',
    maximumSeconds: 1200,
    prepare: async (root, { source, tree, builderEnv }) => {
      await prepareCutoverSupport(root, repo, builderEnv);
      // A pure API-construction diagnostic with the archive application's JSON
      // switch. Its receipt is separate and never counts as archive evidence.
      const probeProject = join(
        repo,
        'runtime/tests/ActionHostTrustedProofVerifier/ManagedArchive/PreparationProbe/PreparationProbe.csproj',
      );
      for (const reflection of ['false', 'true']) {
        const probe = spawnSync(
          'dotnet',
          [
            'publish',
            probeProject,
            '-c',
            'Release',
            '-r',
            'linux-x64',
            '-o',
            join(root, 'preparation-' + reflection),
            '-p:APRManagedPackage=true',
            '-p:APRReleaseVersion=v0.0.0-internal.1',
            '-p:APRBuildId=' + '0'.repeat(64),
            '-p:JsonSerializerIsReflectionEnabledByDefault=' + reflection,
            '-p:UseSharedCompilation=false',
            '-p:RestoreIgnoreFailedSources=true',
          ],
          {
            cwd: repo,
            env: { ...builderEnv, NUGET_PACKAGES: process.env.NUGET_PACKAGES },
            encoding: 'utf8',
            timeout: 180_000,
            maxBuffer: 1024 * 1024,
          },
        );
        if (probe.error || probe.status !== 0) {
          const output = (probe.stdout ?? '') + (probe.stderr ?? '');
          const codes =
            [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)[0-9]{4}\b/gu) ?? [])]
              .slice(0, 8)
              .join(',') || 'none';
          process.stderr.write('APR_D5_PREPARATION_BUILD failure=compiler codes=' + codes + '\n');
        }
        assert.equal(probe.error, undefined);
        assert.equal(probe.status, 0, 'd5_preparation_probe_build_failed');
        const executed = spawnSync(
          'dotnet',
          [
            join(
              root,
              'preparation-' + reflection,
              'AgenticPrReview.Runtime.ActionHostTrustedProofVerifier.dll',
            ),
          ],
          { cwd: root, env: builderEnv, encoding: 'utf8', timeout: 30_000, maxBuffer: 8192 },
        );
        if (executed.status !== 0)
          process.stderr.write('APR_D5_PREPARATION_BUILD failure=execution codes=none\n');
        assert.equal(executed.status, 0);
        const diagnostic = executed.stdout
          .split('\n')
          .filter((line) =>
            /^APR_D5_PREPARATION_PROBE descriptors=[0-5] reflection=(enabled|disabled) failure=(none|invalid-operation|not-supported|type-initialization|argument|other) archive_qualified=false$/u.test(
              line,
            ),
          );
        assert.equal(diagnostic.length, 1);
        process.stderr.write(diagnostic[0] + '\n');
      }
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
      const candidateCheck = verifyManagedCandidateArchive({
        archive: await readFile(join(output, receipt.archiveName)),
        receipt,
        sourceCommit: source,
        sourceTree: tree,
      });
      process.stderr.write(
        'APR_D6_MANAGED_CANDIDATE role=' +
          role +
          ' original_archive_sha256=' +
          candidateCheck.originalArchiveSha256 +
          ' negative_cases=' +
          candidateCheck.negativeCases +
          ' metadata=synthetic-not-provenance\n',
      );
      await writeFile(
        join(root, 'archive-settings.json'),
        JSON.stringify({ role, source, tree, compiled, repo }),
        { flag: 'wx', mode: 0o600 },
      );
    },
  });
  if (process.exitCode) break;
}
