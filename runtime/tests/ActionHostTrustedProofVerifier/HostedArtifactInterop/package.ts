// Bundled only by the explicit repository proof setup; never a release producer.
import { writeFile } from 'node:fs/promises';
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { releaseFixture } from '../../../../tests/distribution/release-payload-fixture.js';
import {
  collectManagedPublishTree,
  encodeManagedPackage,
  inspectHistoricalNativePackageForFixture,
  MANAGED_TARGET,
  sha256,
} from '../../../../scripts/release/build-payload.format.mjs';

const [repo, publish, work, source] = process.argv.slice(2);
const original = releaseFixture();
const inputs = inspectHistoricalNativePackageForFixture(original.archive, original.receipt).manifest
  .buildInputs;
const notices = Buffer.from(
  'Repository-only public-development transport companion fixture. Not an original Solus archive or release.\n',
);
const packaged = encodeManagedPackage(await collectManagedPublishTree(publish), notices, {
  ...inputs,
  sourceCommit: source,
  sourceTree: execFileSync('git', ['rev-parse', 'HEAD^{tree}'], {
    cwd: repo,
    encoding: 'utf8',
  }).trim(),
  assetsSha256: '3'.repeat(64),
  noticesSha256: sha256(notices),
  target: MANAGED_TARGET,
  toolchain: {
    sdk: '10.0.109',
    sdkDriverSha256: 'e'.repeat(64),
    node: process.versions.node,
    zlib: process.versions.zlib,
    os: 'ubuntu-24.04-x64',
  },
});
await writeFile(join(work, packaged.receipt.archiveName), packaged.archive, { mode: 0o600 });
await writeFile(join(work, 'payload-receipt.json'), JSON.stringify(packaged.receipt), {
  mode: 0o600,
});
