// Trusted construction; the isolated consumer receives only original archives.
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmod, lstat, mkdtemp, readFile, realpath, rmdir, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createArchiveCustody, runIsolatedFixture } from './verify-network.mjs';
import { ROLE_DURATION_MS } from './role-deadline.mjs';
import { prepareCutoverSupport } from './cutover-proof.mjs';
import { verifyManagedCandidateArchive } from '../../../../tests/release/verify-managed-candidate-archive.mjs';
const repo = fileURLToPath(new URL('../../../../', import.meta.url));
const unresolvedQualificationCustodies = new Set();
// Fixed TEST_ONLY stage caps; entry includes all future stages, never average time.
export const ORIGINAL_PREVIEW_PREPARATION_STOP_MS = 600_000;
export const ORIGINAL_PREVIEW_SETTLEMENT_MS = 180_000;
export const ORIGINAL_PREVIEW_PAIR_ENTRY_MS = 3_180_000;
export const ORIGINAL_PREVIEW_CACHE_M1_MS = 480_000;
export function requireOriginalPreviewRouteBudget(until, rolesRemaining, cacheM1 = 0) {
  assert.ok(Number.isSafeInteger(until));
  assert.ok([0, 1, 2].includes(rolesRemaining));
  assert.ok([0, ORIGINAL_PREVIEW_CACHE_M1_MS].includes(cacheM1));
  const required =
    cacheM1 +
    rolesRemaining * ROLE_DURATION_MS +
    ORIGINAL_PREVIEW_PREPARATION_STOP_MS +
    ORIGINAL_PREVIEW_SETTLEMENT_MS +
    ORIGINAL_PREVIEW_PAIR_ENTRY_MS +
    ORIGINAL_PREVIEW_SETTLEMENT_MS;
  assert.ok(until - Date.now() >= required, 'original_preview_remaining_route_does_not_fit');
  return required;
}

export async function qualifyManagedArchivePair({
  custodyParent,
  consumeCustody = false,
  consumeOriginalPreview = false,
  originalPreviewOuterDeadline,
} = {}) {
  assert.ok(!(consumeCustody && consumeOriginalPreview));
  assert.ok(!(consumeCustody || consumeOriginalPreview) || custodyParent !== undefined);
  const custody =
    custodyParent === undefined ? undefined : await createArchiveCustody(custodyParent, repo);
  const observations = [];
  let handedOff = false;
  let unresolved = false;
  let consumed;
  // Fixed public origin only; no exception fields or execution authority.
  let originalPreviewFailureStage = 'qualification-roles';
  try {
    for (const role of ['first', 'independent-second']) {
      if (consumeOriginalPreview)
        requireOriginalPreviewRouteBudget(originalPreviewOuterDeadline, role === 'first' ? 2 : 1);
      const observation = await runIsolatedFixture({
        childName: 'archive-child.mjs',
        prefix: 'APR_D5_MANAGED_ARCHIVE ',
        maximumSeconds: 5400,
        archiveRole: role,
        returnObservation: Boolean(custody),
        prepare: async (root, { source, tree, builderEnv, roleDeadline }) => {
          await prepareCutoverSupport(root, repo, builderEnv, { roleDeadline });
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
                timeout: roleDeadline.supportingMilliseconds(180_000),
                killSignal: 'SIGKILL',
                maxBuffer: 1024 * 1024,
              },
            );
            if (probe.error || probe.status !== 0) {
              const output = (probe.stdout ?? '') + (probe.stderr ?? '');
              const codes =
                [...new Set(output.match(/\b(?:CS|MSB|NU|NETSDK)[0-9]{4}\b/gu) ?? [])]
                  .slice(0, 8)
                  .join(',') || 'none';
              process.stderr.write(
                'APR_D5_PREPARATION_BUILD failure=compiler codes=' + codes + '\n',
              );
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
              {
                cwd: root,
                env: builderEnv,
                encoding: 'utf8',
                timeout: roleDeadline.supportingMilliseconds(30_000),
                killSignal: 'SIGKILL',
                maxBuffer: 8192,
              },
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
              timeout: roleDeadline.supportingMilliseconds(600_000),
              killSignal: 'SIGKILL',
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
          if (custody && role === 'first')
            await custody.stage({
              fixtureRoot: root,
              archivePath: join(output, receipt.archiveName),
              receiptPath: join(output, 'receipt.json'),
              source,
              tree,
            });
          await writeFile(
            join(root, 'archive-settings.json'),
            JSON.stringify({ role, source, tree, compiled, repo }),
            { flag: 'wx', mode: 0o600 },
          );
        },
      });
      // returnObservation only resolves after actual role execution, namespace close
      // and finally teardown. A thrown/failed second role cannot seal staged bytes.
      if (custody) observations.push(observation);
      if (process.exitCode) break;
    }
    if (custody) {
      assert.equal(process.exitCode ?? 0, 0);
      const handoff = await custody.seal(observations);
      if (consumeOriginalPreview) {
        originalPreviewFailureStage = 'qualification-consume';
        consumed = await custody.consumeOriginalRuntimePreview();
      } else if (consumeCustody) consumed = await custody.consume();
      else {
        process.stdout.write('APR_TEST_ONLY_MANAGED_CUSTODY ' + JSON.stringify(handoff) + '\n');
        handedOff = true;
      }
    }
  } catch (error) {
    unresolved = error?.code === 'APR_TEST_ONLY_ORIGINAL_OWNERSHIP_UNKNOWN';
    if (unresolved) unresolvedQualificationCustodies.add({ custody, observations });
    if (consumeOriginalPreview) {
      // The driver's aggregate failure and finally/unknown precedence remain.
      try {
        process.stderr.write(
          'APR_TEST_ONLY_ORIGINAL_PREVIEW_FAILED stage=' +
            originalPreviewFailureStage +
            ' settlement=unknown\n',
        );
      } catch {}
    }
    throw error;
  } finally {
    if (custody && !handedOff && !unresolved) await custody.dispose();
  }
  return consumed;
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  assert.ok(
    process.argv.length === 2 ||
      (process.argv.length === 3 && process.argv[2] === '--consume-custody') ||
      (process.argv.length === 4 && process.argv[2] === '--custody-parent'),
  );
  if (process.argv[2] === '--consume-custody') {
    assert.equal(process.env.GITHUB_REPOSITORY, 'SolusQuest/agentic-pr-review');
    assert.equal(process.env.GITHUB_JOB, 'install-proof');
    assert.match(process.env.EXPECTED_HEAD ?? '', /^[a-f0-9]{40}$/u);
    assert.equal(process.env.WORKFLOW_HEAD, process.env.EXPECTED_HEAD);
    assert.equal(process.env.GITHUB_WORKFLOW_SHA, process.env.EXPECTED_HEAD);
    assert.ok(
      process.env.GITHUB_WORKFLOW_REF?.startsWith(
        'SolusQuest/agentic-pr-review/.github/workflows/solus-agent-package-proof.yml@refs/heads/',
      ),
    );
    assert.match(process.env.GITHUB_RUN_ID ?? '', /^[1-9][0-9]{0,18}$/u);
    assert.match(process.env.GITHUB_RUN_ATTEMPT ?? '', /^[1-9][0-9]{0,18}$/u);
    const temporary = await realpath(process.env.RUNNER_TEMP);
    assert.equal(temporary, process.env.RUNNER_TEMP);
    const parent = await mkdtemp(join(temporary, 'apr-managed-consume-parent-'));
    await chmod(parent, 0o700);
    const identity = await lstat(parent);
    let result;
    try {
      result = await qualifyManagedArchivePair({ custodyParent: parent, consumeCustody: true });
    } finally {
      // Empty-parent removal only; an unknown/unclosed child is never recursively erased.
      const current = await lstat(parent);
      assert.equal(await realpath(parent), parent);
      assert.ok(current.isDirectory() && !current.isSymbolicLink());
      assert.equal(current.uid, process.getuid());
      assert.equal(current.dev, identity.dev);
      assert.equal(current.ino, identity.ino);
      assert.equal(current.mode & 0o077, 0);
      await rmdir(parent);
    }
    assert.ok(result);
    process.stdout.write(
      'APR_TEST_ONLY_MANAGED_CUSTODY_CONSUMED ' +
        JSON.stringify({
          ...result,
          custodyCleanupConfirmed: true,
          parentCleanupConfirmed: true,
        }) +
        '\n',
    );
  } else await qualifyManagedArchivePair({ custodyParent: process.argv[3] });
}
