import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

import {
  assembleTrustedProofEvidence,
  canonicalJson,
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

function readCanonical(pathname) {
  const bytes = fs.readFileSync(pathname);
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
  return { value, bytes };
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
  for (const source of manifest.sources) {
    const bytes = fs.readFileSync(
      resolvePackageFile(restrictedRoot, packageRoot, source.body_path, maximumDocumentBytes),
    );
    if (String(bytes.length) !== source.body_size || sha256(bytes) !== source.body_sha256) {
      invalid();
    }
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
      const bytes = fs.readFileSync(
        resolvePackageFile(restrictedRoot, packageRoot, relative, maximum),
      );
      if (String(bytes.length) !== size || sha256(bytes) !== digest) invalid();
    }
  }
}

function parseArgs(args) {
  const names = [
    '--restricted-root',
    '--destination-identity',
    '--repository-root',
    '--worktree-root',
    '--assembly-input',
    '--capture-manifest',
    '--oracle-result',
    '--host-output',
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
      within(restrictedRoot, worktreeRoot) ||
      within(restrictedRoot, temporaryRoot)
    ) {
      invalid();
    }
    assertNoLinks(restrictedRoot, restrictedRoot);
    const marker = readCanonical(resolveExisting(restrictedRoot, markerName)).value;
    if (
      JSON.stringify(Object.keys(marker)) !==
        JSON.stringify(['kind', 'destination_identity_sha256']) ||
      marker.kind !== markerKind ||
      marker.destination_identity_sha256 !== options['--destination-identity']
    ) {
      invalid();
    }

    const hostInput = readCanonical(resolveExisting(restrictedRoot, options['--assembly-input']));
    const capturePath = resolveExisting(restrictedRoot, options['--capture-manifest']);
    const capture = readCanonical(capturePath);
    const oracle = readCanonical(resolveExisting(restrictedRoot, options['--oracle-result']));
    const credentialNames = ['github-token', 'current-state-key', 'previous-state-key'];
    if (credentialNames.some((name) => fs.existsSync(path.join(restrictedRoot, name)))) invalid();
    verifyCapturedFiles(restrictedRoot, capturePath, capture.value);

    const assembled = assembleTrustedProofEvidence({
      host: hostInput.value,
      captureManifest: capture.value,
      captureManifestSha256: sha256(capture.bytes),
      oracleResult: oracle.value,
      oracleResultSha256: sha256(oracle.bytes),
      credentialCopiesAbsent: true,
    });
    const hostOutput = resolveNew(restrictedRoot, options['--host-output']);
    const publicOutput = resolveNew(worktreeRoot, options['--public-output']);
    fs.writeFileSync(hostOutput, canonicalJson(assembled.host), { flag: 'wx', mode: 0o600 });
    fs.writeFileSync(publicOutput, canonicalJson(assembled.publicEvidence), {
      flag: 'wx',
      mode: 0o600,
    });
    process.stdout.write(
      `APR_R4_E3_ASSEMBLY_OK ${sha256(canonicalJson(assembled.host))} ${sha256(canonicalJson(assembled.publicEvidence))}\n`,
    );
  } catch {
    process.stderr.write('APR_R4_E3_ASSEMBLY_INVALID\n');
    process.exitCode = 1;
  }
}
