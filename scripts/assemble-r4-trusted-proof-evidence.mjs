import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  assembleTrustedProofEvidence,
  assertFinalizedPrivatePackage,
  buildFinalizedPrivatePackageManifest,
  canonicalJson,
  projectFinalizedTrustedProofEvidence,
  sha256,
} from './r4-trusted-proof-contract.mjs';

const markerName = '.apr-r4-e3-restricted-root.json';
const markerKind = 'apr-r4-e3-maintainer-approved-restricted-root-v1';
const maximumDocumentBytes = 256 * 1024;

function invalid() {
  throw new Error('APR_R4_E3_ASSEMBLY_INVALID');
}

function within(candidate, root) {
  const relative = path.relative(root, candidate);
  return (
    relative === '' ||
    (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative))
  );
}

function assertNoLinks(candidate, stop) {
  let current = candidate;
  while (within(current, stop)) {
    const stat = fs.lstatSync(current);
    if (stat.isSymbolicLink()) invalid();
    if (current === stop) return;
    current = path.dirname(current);
  }
  invalid();
}

function resolveExisting(root, relative) {
  if (path.isAbsolute(relative) || relative.length === 0 || Buffer.byteLength(relative) > 1024) {
    invalid();
  }
  const candidate = path.resolve(root, relative);
  if (!within(candidate, root) || candidate === root || !fs.statSync(candidate).isFile()) invalid();
  assertNoLinks(candidate, root);
  return candidate;
}

function resolveNew(root, relative) {
  if (path.isAbsolute(relative) || relative.length === 0 || Buffer.byteLength(relative) > 1024) {
    invalid();
  }
  const candidate = path.resolve(root, relative);
  const parent = path.dirname(candidate);
  if (
    !within(candidate, root) ||
    candidate === root ||
    fs.existsSync(candidate) ||
    !fs.statSync(parent).isDirectory()
  ) {
    invalid();
  }
  assertNoLinks(parent, root);
  return candidate;
}

function physicalIdentity(stat) {
  const mode = process.platform === 'win32' ? 0n : stat.mode;
  const owner = process.platform === 'win32' ? 0n : stat.uid;
  const canonical = `${stat.dev.toString(16).padStart(16, '0')}:${stat.ino
    .toString(16)
    .padStart(16, '0')}:${stat.nlink}:${stat.size}:${mode.toString(16).padStart(8, '0')}:${owner}`;
  return { canonical, digest: sha256(Buffer.from(canonical, 'utf8')) };
}

function readPinned(pathname, maximumBytes, expectedDevice, requireOwnerOnly = true) {
  const flags = fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0);
  const descriptor = fs.openSync(pathname, flags);
  try {
    const before = fs.fstatSync(descriptor, { bigint: true });
    if (
      !before.isFile() ||
      before.nlink !== 1n ||
      before.size < 1n ||
      before.size > BigInt(maximumBytes) ||
      (expectedDevice !== undefined && before.dev !== expectedDevice) ||
      (requireOwnerOnly &&
        process.platform !== 'win32' &&
        ((before.mode & 0x3fn) !== 0n || before.uid !== BigInt(process.geteuid())))
    ) {
      invalid();
    }
    const identity = physicalIdentity(before);
    const bytes = fs.readFileSync(descriptor);
    const after = fs.fstatSync(descriptor, { bigint: true });
    if (
      bytes.length !== Number(before.size) ||
      physicalIdentity(after).canonical !== identity.canonical
    ) {
      bytes.fill(0);
      invalid();
    }
    return { bytes, identity: identity.digest };
  } finally {
    fs.closeSync(descriptor);
  }
}

export function pinnedFileIdentity(pathname) {
  const pinned = readPinned(pathname, maximumDocumentBytes * 16);
  try {
    return pinned.identity;
  } finally {
    pinned.bytes.fill(0);
  }
}

function readCanonical(pathname, expectedDevice, requireOwnerOnly = true) {
  const pinned = readPinned(pathname, maximumDocumentBytes, expectedDevice, requireOwnerOnly);
  const { bytes } = pinned;
  if (
    bytes.length < 2 ||
    bytes.length > maximumDocumentBytes ||
    bytes.at(-1) !== 0x0a ||
    bytes.includes(0x0d)
  ) {
    invalid();
  }
  const text = bytes.toString('utf8');
  const value = JSON.parse(text);
  if (canonicalJson(value) !== text) invalid();
  return { value, bytes, identity: pinned.identity };
}

function resolvePackageFile(restrictedRoot, packageRoot, relative, maximumBytes) {
  if (path.isAbsolute(relative) || relative.length === 0 || Buffer.byteLength(relative) > 1024) {
    invalid();
  }
  const candidate = path.resolve(packageRoot, relative);
  if (!within(candidate, packageRoot) || candidate === packageRoot) invalid();
  const stat = fs.statSync(candidate);
  if (!stat.isFile() || stat.size < 1 || stat.size > maximumBytes) invalid();
  assertNoLinks(candidate, restrictedRoot);
  return candidate;
}

export function verifyCapturedFiles(restrictedRoot, manifestPath, manifest) {
  const packageRoot = path.dirname(manifestPath);
  const rootDevice = fs.statSync(restrictedRoot, { bigint: true }).dev;
  const capturedSourceBodies = new Map();
  for (const source of manifest.sources) {
    const pinned = readPinned(
      resolvePackageFile(restrictedRoot, packageRoot, source.body_path, maximumDocumentBytes),
      maximumDocumentBytes,
      rootDevice,
    );
    if (
      String(pinned.bytes.length) !== source.body_size ||
      sha256(pinned.bytes) !== source.body_sha256 ||
      pinned.identity !== source.body_file_identity
    ) {
      pinned.bytes.fill(0);
      invalid();
    }
    let text;
    try {
      text = pinned.bytes.toString('utf8');
      JSON.parse(text);
    } catch {
      pinned.bytes.fill(0);
      invalid();
    }
    capturedSourceBodies.set(source.source_id, {
      text,
    });
    pinned.bytes.fill(0);
  }
  for (const artifact of manifest.artifacts) {
    for (const [relative, maximum, size, digest] of [
      [artifact.archive_path, 4 * 1024 * 1024, artifact.archive_size, artifact.archive_sha256],
      [
        artifact.encrypted_object_path,
        2 * 1024 * 1024,
        artifact.encrypted_object_size,
        artifact.encrypted_object_sha256,
      ],
    ]) {
      const pinned = readPinned(
        resolvePackageFile(restrictedRoot, packageRoot, relative, maximum),
        maximum,
        rootDevice,
      );
      const expectedIdentity =
        relative === artifact.archive_path
          ? artifact.archive_file_identity
          : artifact.encrypted_object_file_identity;
      if (
        String(pinned.bytes.length) !== size ||
        sha256(pinned.bytes) !== digest ||
        pinned.identity !== expectedIdentity
      ) {
        pinned.bytes.fill(0);
        invalid();
      }
      pinned.bytes.fill(0);
    }
  }
  return capturedSourceBodies;
}

function parseArgs(args) {
  const names = [
    '--restricted-root',
    '--destination-identity',
    '--repository-root',
    '--worktree-root',
    '--source-bundle',
    '--capture-manifest',
    '--oracle-result',
    '--oracle-build-receipt',
    '--ui-attestation',
    '--cleanup-plan',
    '--cleanup-readbacks',
    '--public-leak-scan',
    '--restricted-package-readback',
    '--host-output',
    '--package-manifest-output',
    '--public-output',
  ];
  if (args.length !== names.length * 2) invalid();
  const values = new Map();
  for (let index = 0; index < args.length; index += 2) {
    if (!names.includes(args[index]) || values.has(args[index])) invalid();
    values.set(args[index], args[index + 1]);
  }
  if (names.some((name) => !values.has(name))) invalid();
  return Object.fromEntries(values);
}

if (process.argv[1] && path.resolve(process.argv[1]) === path.resolve(import.meta.filename)) {
  try {
    const options = parseArgs(process.argv.slice(2));
    const restrictedRoot = path.resolve(options['--restricted-root']);
    const repositoryRoot = path.resolve(options['--repository-root']);
    const worktreeRoot = path.resolve(options['--worktree-root']);
    const temporaryRoot = path.resolve(os.tmpdir());
    if (
      !path.isAbsolute(options['--restricted-root']) ||
      !fs.statSync(restrictedRoot).isDirectory() ||
      within(restrictedRoot, repositoryRoot) ||
      within(repositoryRoot, restrictedRoot) ||
      within(restrictedRoot, worktreeRoot) ||
      within(worktreeRoot, restrictedRoot) ||
      within(restrictedRoot, temporaryRoot) ||
      within(temporaryRoot, restrictedRoot)
    ) {
      invalid();
    }
    assertNoLinks(restrictedRoot, restrictedRoot);
    const rootStat = fs.statSync(restrictedRoot, { bigint: true });
    if (
      process.platform !== 'win32' &&
      ((rootStat.mode & 0x3fn) !== 0n || rootStat.uid !== BigInt(process.geteuid()))
    ) {
      invalid();
    }
    const marker = readCanonical(resolveExisting(restrictedRoot, markerName), rootStat.dev).value;
    if (
      JSON.stringify(Object.keys(marker)) !==
        JSON.stringify(['kind', 'destination_identity_sha256']) ||
      marker.kind !== markerKind ||
      marker.destination_identity_sha256 !== options['--destination-identity']
    ) {
      invalid();
    }

    const sourceMap = readCanonical(
      resolveExisting(
        repositoryRoot,
        path.join(
          'runtime',
          'tests',
          'fixtures',
          'action-host',
          'trusted-proof',
          'source-map.json',
        ),
      ),
      undefined,
      false,
    );
    const sourceBundle = readCanonical(
      resolveExisting(restrictedRoot, options['--source-bundle']),
      rootStat.dev,
    );
    const capturePath = resolveExisting(restrictedRoot, options['--capture-manifest']);
    const capture = readCanonical(capturePath, rootStat.dev);
    const oracle = readCanonical(
      resolveExisting(restrictedRoot, options['--oracle-result']),
      rootStat.dev,
    );
    const uiAttestation = readCanonical(
      resolveExisting(restrictedRoot, options['--ui-attestation']),
      rootStat.dev,
    );
    const payloadReceipt = readCanonical(
      resolveExisting(
        repositoryRoot,
        path.join(
          'runtime',
          'tests',
          'fixtures',
          'action-host',
          'trusted-proof',
          'trusted-proof-payload-receipt-v2.json',
        ),
      ),
      undefined,
      false,
    );
    const retainedInputs = [
      ['cleanup-plan', '--cleanup-plan'],
      ['oracle-build-receipt', '--oracle-build-receipt'],
      ['cleanup-readbacks', '--cleanup-readbacks'],
      ['public-leak-scan-result', '--public-leak-scan'],
      ['restricted-package-readback', '--restricted-package-readback'],
    ].map(([sourceId, option]) => [
      sourceId,
      readCanonical(resolveExisting(restrictedRoot, options[option]), rootStat.dev).value,
    ]);
    const credentialNames = ['github-token', 'current-state-key', 'previous-state-key'];
    if (credentialNames.some((name) => fs.existsSync(path.join(restrictedRoot, name)))) invalid();
    const capturedSourceBodies = verifyCapturedFiles(restrictedRoot, capturePath, capture.value);

    const assembled = assembleTrustedProofEvidence({
      sourceMap: sourceMap.value,
      sourceBundle: sourceBundle.value,
      captureManifest: capture.value,
      captureManifestSha256: sha256(capture.bytes),
      oracleResult: oracle.value,
      oracleResultSha256: sha256(oracle.bytes),
      capturedSourceBodies,
      uiAttestation: uiAttestation.value,
      uiAttestationSha256: sha256(uiAttestation.bytes),
      retainedDocuments: new Map([
        ['trusted-proof-payload-receipt-v2', payloadReceipt.value],
        ...retainedInputs,
      ]),
      credentialCopiesAbsent: true,
    });
    const hostOutput = resolveNew(restrictedRoot, options['--host-output']);
    const packageManifestOutput = resolveNew(restrictedRoot, options['--package-manifest-output']);
    fs.writeFileSync(hostOutput, canonicalJson(assembled.host), { flag: 'wx', mode: 0o600 });
    const privatePackageManifest = buildFinalizedPrivatePackageManifest({
      host: assembled.host,
      sourceBundle: sourceBundle.value,
      captureManifestSha256: sha256(capture.bytes),
      oracleResultSha256: sha256(oracle.bytes),
      cleanupPlan: assembled.cleanupPlan,
    });
    fs.writeFileSync(packageManifestOutput, canonicalJson(privatePackageManifest), {
      flag: 'wx',
      mode: 0o600,
    });
    const hostReadback = readCanonical(hostOutput, rootStat.dev);
    const manifestReadback = readCanonical(packageManifestOutput, rootStat.dev);
    if (canonicalJson(hostReadback.value) !== canonicalJson(assembled.host)) invalid();
    assertFinalizedPrivatePackage({
      host: hostReadback.value,
      sourceBundle: sourceBundle.value,
      captureManifestSha256: sha256(capture.bytes),
      oracleResultSha256: sha256(oracle.bytes),
      cleanupPlan: assembled.cleanupPlan,
      privatePackageManifest: manifestReadback.value,
    });
    if (assembled.recoveryOnly) {
      process.stdout.write(
        `APR_R4_E3_ASSEMBLY_RECOVERY_ONLY ${sha256(canonicalJson(assembled.host))}\n`,
      );
    } else {
      const publicOutput = resolveNew(worktreeRoot, options['--public-output']);
      const publicEvidence = projectFinalizedTrustedProofEvidence({
        host: hostReadback.value,
        sourceBundle: sourceBundle.value,
        captureManifestSha256: sha256(capture.bytes),
        oracleResultSha256: sha256(oracle.bytes),
        cleanupPlan: assembled.cleanupPlan,
        privatePackageManifest: manifestReadback.value,
      });
      fs.writeFileSync(publicOutput, canonicalJson(publicEvidence), {
        flag: 'wx',
        mode: 0o600,
      });
      process.stdout.write(
        `APR_R4_E3_ASSEMBLY_OK ${sha256(canonicalJson(assembled.host))} ${sha256(canonicalJson(publicEvidence))}\n`,
      );
    }
  } catch {
    process.stderr.write('APR_R4_E3_ASSEMBLY_INVALID\n');
    process.exitCode = 1;
  }
}
