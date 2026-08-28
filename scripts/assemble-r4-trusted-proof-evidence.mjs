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

function publicSurfaceCorpus(repositoryRoot, worktreeRoot, logRoot) {
  const result = new Map();
  const excluded = new Set(['.git', 'node_modules', 'bin', 'obj']);
  let totalBytes = 0;
  const visit = (root, label, current = root) => {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      if (
        excluded.has(entry.name) ||
        (entry.name === 'worktrees' && path.basename(current) === '.codex')
      ) {
        continue;
      }
      const pathname = path.join(current, entry.name);
      if (entry.isSymbolicLink()) invalid();
      if (entry.isDirectory()) {
        visit(root, label, pathname);
      } else if (entry.isFile()) {
        const stat = fs.statSync(pathname);
        if (stat.size > maximumDocumentBytes * 16) invalid();
        const bytes = fs.readFileSync(pathname);
        totalBytes += bytes.length;
        if (result.size >= 4096 || totalBytes > maximumDocumentBytes * 256) invalid();
        result.set(`${label}:${path.relative(root, pathname).split(path.sep).join('/')}`, bytes);
      }
    }
  };
  visit(repositoryRoot, 'repository');
  if (worktreeRoot !== repositoryRoot) visit(worktreeRoot, 'worktree');
  if (logRoot !== repositoryRoot && logRoot !== worktreeRoot) visit(logRoot, 'logs');
  return result;
}

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

function directoryDevice(pathname) {
  const descriptor = fs.openSync(pathname, fs.constants.O_RDONLY);
  try {
    return fs.fstatSync(descriptor, { bigint: true }).dev;
  } finally {
    fs.closeSync(descriptor);
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
  const rootDevice = directoryDevice(restrictedRoot);
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
    '--public-log-root',
    '--source-bundle',
    '--capture-manifest',
    '--post-cleanup-capture-manifest',
    '--cleanup-authorization-readback',
    '--correction-gate-receipt',
    '--credential-admission-receipt',
    '--credential-disposition-receipt-output',
    '--oracle-result',
    '--oracle-build-receipt',
    '--ui-attestation',
    '--cleanup-plan',
    '--cleanup-execution',
    '--public-leak-scan',
    '--restricted-package-readback',
    '--oracle-assembly',
    '--production-assembly',
    '--host-output',
    '--package-manifest-output',
    '--public-scan-output',
    '--public-candidate-output',
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
    const logRoot = path.resolve(options['--public-log-root']);
    const temporaryRoot = path.resolve(os.tmpdir());
    if (
      !path.isAbsolute(options['--restricted-root']) ||
      !fs.statSync(restrictedRoot).isDirectory() ||
      within(restrictedRoot, repositoryRoot) ||
      within(repositoryRoot, restrictedRoot) ||
      within(restrictedRoot, worktreeRoot) ||
      within(worktreeRoot, restrictedRoot) ||
      within(restrictedRoot, logRoot) ||
      within(logRoot, restrictedRoot) ||
      within(restrictedRoot, temporaryRoot) ||
      within(temporaryRoot, restrictedRoot)
    ) {
      invalid();
    }
    assertNoLinks(restrictedRoot, restrictedRoot);
    const rootStat = fs.statSync(restrictedRoot, { bigint: true });
    const rootDevice = directoryDevice(restrictedRoot);
    if (
      process.platform !== 'win32' &&
      ((rootStat.mode & 0x3fn) !== 0n || rootStat.uid !== BigInt(process.geteuid()))
    ) {
      invalid();
    }
    const marker = readCanonical(resolveExisting(restrictedRoot, markerName), rootDevice).value;
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
      rootDevice,
    );
    const capturePath = resolveExisting(restrictedRoot, options['--capture-manifest']);
    const capture = readCanonical(capturePath, rootDevice);
    const postCleanupCapturePath = resolveExisting(
      restrictedRoot,
      options['--post-cleanup-capture-manifest'],
    );
    const postCleanupCapture = readCanonical(postCleanupCapturePath, rootDevice);
    const producerJournalSeal = readCanonical(
      resolveExisting(
        restrictedRoot,
        path.join(capture.value.producer_journal_directory, 'seal.json'),
      ),
      rootDevice,
    );
    if (
      sha256(producerJournalSeal.bytes) !== capture.value.producer_journal_seal_sha256 ||
      producerJournalSeal.identity !== capture.value.producer_journal_seal_file_identity
    ) {
      invalid();
    }
    const oracle = readCanonical(
      resolveExisting(restrictedRoot, options['--oracle-result']),
      rootDevice,
    );
    const uiAttestation = readCanonical(
      resolveExisting(restrictedRoot, options['--ui-attestation']),
      rootDevice,
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
      ['cleanup-authorization-readback', '--cleanup-authorization-readback'],
      ['correction-gate-receipt', '--correction-gate-receipt'],
      ['credential-admission-receipt', '--credential-admission-receipt'],
      ['credential-disposition-receipt', '--credential-disposition-receipt-output'],
      ['cleanup-execution', '--cleanup-execution'],
      ['cleanup-plan', '--cleanup-plan'],
      ['oracle-build-receipt', '--oracle-build-receipt'],
      ['public-leak-scan-result', '--public-leak-scan'],
      ['restricted-package-readback', '--restricted-package-readback'],
    ].map(([sourceId, option]) => [
      sourceId,
      readCanonical(resolveExisting(restrictedRoot, options[option]), rootDevice).value,
    ]);
    const capturedSourceBodies = verifyCapturedFiles(restrictedRoot, capturePath, capture.value);
    const postCleanupCapturedSourceBodies = verifyCapturedFiles(
      restrictedRoot,
      postCleanupCapturePath,
      postCleanupCapture.value,
    );
    const oracleAssemblyPath = resolveExisting(restrictedRoot, options['--oracle-assembly']);
    const productionAssemblyPath = resolveExisting(
      restrictedRoot,
      options['--production-assembly'],
    );
    const oracleAssembly = readPinned(oracleAssemblyPath, maximumDocumentBytes * 16, rootDevice);
    const productionAssembly = readPinned(
      productionAssemblyPath,
      maximumDocumentBytes * 16,
      rootDevice,
    );
    const oracleBinaries = {
      oracle_assembly_path: options['--oracle-assembly'],
      oracle_assembly_sha256: sha256(oracleAssembly.bytes),
      production_assembly_path: options['--production-assembly'],
      production_assembly_sha256: sha256(productionAssembly.bytes),
    };
    oracleAssembly.bytes.fill(0);
    productionAssembly.bytes.fill(0);

    if (!path.isAbsolute(logRoot) || !fs.statSync(logRoot).isDirectory()) invalid();
    assertNoLinks(logRoot, logRoot);
    const publicCorpus = publicSurfaceCorpus(repositoryRoot, worktreeRoot, logRoot);
    const assembled = assembleTrustedProofEvidence({
      sourceMap: sourceMap.value,
      sourceBundle: sourceBundle.value,
      captureManifest: capture.value,
      captureManifestSha256: sha256(capture.bytes),
      postCleanupCaptureManifest: postCleanupCapture.value,
      postCleanupCaptureManifestSha256: sha256(postCleanupCapture.bytes),
      oracleResult: oracle.value,
      oracleResultSha256: sha256(oracle.bytes),
      capturedSourceBodies,
      postCleanupCapturedSourceBodies,
      uiAttestation: uiAttestation.value,
      uiAttestationSha256: sha256(uiAttestation.bytes),
      retainedDocuments: new Map([
        ['trusted-proof-payload-receipt-v2', payloadReceipt.value],
        ['producer-journal-seal', producerJournalSeal.value],
        ...retainedInputs,
      ]),
      oracleBinaries,
      publicSurfaceCorpus: publicCorpus,
      credentialCopiesAbsent: true,
    });
    for (const bytes of publicCorpus.values()) bytes.fill(0);
    const hostOutput = resolveNew(restrictedRoot, options['--host-output']);
    const packageManifestOutput = resolveNew(restrictedRoot, options['--package-manifest-output']);
    const publicScanOutput = resolveNew(restrictedRoot, options['--public-scan-output']);
    fs.writeFileSync(hostOutput, canonicalJson(assembled.host), { flag: 'wx', mode: 0o600 });
    fs.writeFileSync(publicScanOutput, canonicalJson(assembled.publicScanManifest), {
      flag: 'wx',
      mode: 0o600,
    });
    const privatePackageManifest = buildFinalizedPrivatePackageManifest({
      host: assembled.host,
      sourceBundle: sourceBundle.value,
      captureManifestSha256: sha256(capture.bytes),
      postCleanupCaptureManifestSha256: sha256(postCleanupCapture.bytes),
      oracleResultSha256: sha256(oracle.bytes),
      cleanupPlan: assembled.cleanupPlan,
      credentialAdmissionReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('credential-admission-receipt')),
      ),
      credentialDispositionReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('credential-disposition-receipt')),
      ),
      correctionGateReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('correction-gate-receipt')),
      ),
      producerJournalSealSha256: capture.value.producer_journal_seal_sha256,
      producerJournalSealFileIdentity: capture.value.producer_journal_seal_file_identity,
      oracleBuildReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('oracle-build-receipt')),
      ),
      oracleAssemblySha256: oracleBinaries.oracle_assembly_sha256,
      productionAssemblySha256: oracleBinaries.production_assembly_sha256,
      publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
      publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
    });
    fs.writeFileSync(packageManifestOutput, canonicalJson(privatePackageManifest), {
      flag: 'wx',
      mode: 0o600,
    });
    const hostReadback = readCanonical(hostOutput, rootDevice);
    const manifestReadback = readCanonical(packageManifestOutput, rootDevice);
    if (canonicalJson(hostReadback.value) !== canonicalJson(assembled.host)) invalid();
    assertFinalizedPrivatePackage({
      host: hostReadback.value,
      sourceBundle: sourceBundle.value,
      captureManifestSha256: sha256(capture.bytes),
      postCleanupCaptureManifestSha256: sha256(postCleanupCapture.bytes),
      oracleResultSha256: sha256(oracle.bytes),
      cleanupPlan: assembled.cleanupPlan,
      credentialAdmissionReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('credential-admission-receipt')),
      ),
      credentialDispositionReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('credential-disposition-receipt')),
      ),
      correctionGateReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('correction-gate-receipt')),
      ),
      producerJournalSealSha256: capture.value.producer_journal_seal_sha256,
      producerJournalSealFileIdentity: capture.value.producer_journal_seal_file_identity,
      oracleBuildReceiptSha256: sha256(
        canonicalJson(new Map(retainedInputs).get('oracle-build-receipt')),
      ),
      oracleAssemblySha256: oracleBinaries.oracle_assembly_sha256,
      productionAssemblySha256: oracleBinaries.production_assembly_sha256,
      publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
      publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
      privatePackageManifest: manifestReadback.value,
    });
    if (assembled.recoveryOnly) {
      process.stdout.write(
        `APR_R4_E3_ASSEMBLY_RECOVERY_ONLY ${sha256(canonicalJson(assembled.host))}\n`,
      );
    } else {
      const publicCandidateOutput = resolveNew(
        restrictedRoot,
        options['--public-candidate-output'],
      );
      const publicEvidence = projectFinalizedTrustedProofEvidence({
        host: hostReadback.value,
        sourceBundle: sourceBundle.value,
        captureManifestSha256: sha256(capture.bytes),
        postCleanupCaptureManifestSha256: sha256(postCleanupCapture.bytes),
        oracleResultSha256: sha256(oracle.bytes),
        cleanupPlan: assembled.cleanupPlan,
        credentialAdmissionReceiptSha256: sha256(
          canonicalJson(new Map(retainedInputs).get('credential-admission-receipt')),
        ),
        credentialDispositionReceiptSha256: sha256(
          canonicalJson(new Map(retainedInputs).get('credential-disposition-receipt')),
        ),
        correctionGateReceiptSha256: sha256(
          canonicalJson(new Map(retainedInputs).get('correction-gate-receipt')),
        ),
        producerJournalSealSha256: capture.value.producer_journal_seal_sha256,
        producerJournalSealFileIdentity: capture.value.producer_journal_seal_file_identity,
        oracleBuildReceiptSha256: sha256(
          canonicalJson(new Map(retainedInputs).get('oracle-build-receipt')),
        ),
        oracleAssemblySha256: oracleBinaries.oracle_assembly_sha256,
        productionAssemblySha256: oracleBinaries.production_assembly_sha256,
        publicCandidateSha256: sha256(canonicalJson(assembled.publicCandidate)),
        publicScanManifestSha256: sha256(canonicalJson(assembled.publicScanManifest)),
        privatePackageManifest: manifestReadback.value,
      });
      if (canonicalJson(publicEvidence) !== canonicalJson(assembled.publicCandidate)) invalid();
      fs.writeFileSync(publicCandidateOutput, canonicalJson(publicEvidence), {
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
