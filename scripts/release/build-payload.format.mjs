import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { chmod, mkdir, mkdtemp, open, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { crc32, deflateRawSync, inflateRawSync } from 'node:zlib';

export const LIMITS = Object.freeze({
  archive: 32 * 1024 * 1024,
  executable: 64 * 1024 * 1024,
  notices: 1024 * 1024,
  manifest: 64 * 1024,
  expanded: 66 * 1024 * 1024,
  releaseVersion: 48,
});
export const MEMBERS = Object.freeze([
  { path: 'agentic-pr-review/agentic-pr-review', mode: 0o755, limit: LIMITS.executable },
  { path: 'agentic-pr-review/manifest.json', mode: 0o644, limit: LIMITS.manifest },
  { path: 'agentic-pr-review/THIRD-PARTY-NOTICES.txt', mode: 0o644, limit: LIMITS.notices },
]);
const gzipHeader = Buffer.from('1f8b0800000000000203', 'hex');
const hex40 = /^[a-f0-9]{40}$/;
const hex64 = /^[a-f0-9]{64}$/;
const versionPattern =
  /^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)-internal\.([1-9][0-9]*)$/;

function requireThat(condition, code) {
  if (!condition) throw new Error(code);
}
function keys(value, expected, code) {
  requireThat(value !== null && typeof value === 'object' && !Array.isArray(value), code);
  requireThat(Object.keys(value).sort().join('\0') === [...expected].sort().join('\0'), code);
}
function matches(value, expression) {
  return typeof value === 'string' && expression.test(value);
}
function boundedText(value, max = 160) {
  return typeof value === 'string' && value.length <= max && /^[\x20-\x7e]+$/.test(value);
}
export function sha256(bytes) {
  return createHash('sha256').update(bytes).digest('hex');
}
export function canonicalJson(value) {
  if (Array.isArray(value)) return '[' + value.map(canonicalJson).join(',') + ']';
  if (value !== null && typeof value === 'object') {
    return (
      '{' +
      Object.keys(value)
        .sort()
        .map((k) => JSON.stringify(k) + ':' + canonicalJson(value[k]))
        .join(',') +
      '}'
    );
  }
  requireThat(
    value === null ||
      typeof value === 'string' ||
      typeof value === 'boolean' ||
      (typeof value === 'number' && Number.isSafeInteger(value)),
    'invalid_canonical_value',
  );
  return JSON.stringify(value);
}
export function validateReleaseVersion(version) {
  requireThat(
    typeof version === 'string' &&
      version.length <= LIMITS.releaseVersion &&
      versionPattern.test(version),
    'invalid_release_version',
  );
  return version;
}
export function compiledIdentity(version, buildId) {
  validateReleaseVersion(version);
  requireThat(matches(buildId, hex64), 'invalid_build_id');
  return version + '+build.' + buildId;
}
function validateIdentity(identity) {
  keys(
    identity,
    ['releaseVersion', 'platform', 'sourceCommit', 'sourceTree', 'buildId', 'informationalVersion'],
    'invalid_identity',
  );
  validateReleaseVersion(identity.releaseVersion);
  requireThat(
    identity.platform === 'linux-x64' &&
      matches(identity.sourceCommit, hex40) &&
      matches(identity.sourceTree, hex40) &&
      matches(identity.buildId, hex64),
    'invalid_identity',
  );
  requireThat(
    identity.informationalVersion === compiledIdentity(identity.releaseVersion, identity.buildId),
    'invalid_compiled_identity',
  );
}
function validateInventory(members, specs = MEMBERS) {
  requireThat(Array.isArray(members) && members.length === specs.length, 'invalid_inventory');
  members.forEach((member, index) => {
    keys(member, ['path', 'mode', 'size', 'sha256'], 'invalid_member');
    const spec = specs[index];
    requireThat(
      member.path === spec.path &&
        member.mode === spec.mode &&
        Number.isSafeInteger(member.size) &&
        member.size > 0 &&
        member.size <= spec.limit &&
        matches(member.sha256, hex64),
      'invalid_member',
    );
  });
}
function validateInputs(input) {
  keys(
    input,
    [
      'releaseVersion',
      'platform',
      'sourceCommit',
      'sourceTree',
      'policySha256',
      'lockSha256',
      'noticesSha256',
      'toolchain',
      'dependencies',
    ],
    'invalid_build_inputs',
  );
  validateReleaseVersion(input.releaseVersion);
  requireThat(
    input.platform === 'linux-x64' &&
      matches(input.sourceCommit, hex40) &&
      matches(input.sourceTree, hex40),
    'invalid_build_inputs',
  );
  for (const field of ['policySha256', 'lockSha256', 'noticesSha256'])
    requireThat(matches(input[field], hex64), 'invalid_build_inputs');
  const tools = input.toolchain;
  keys(
    tools,
    [
      'sdk',
      'sdkDriverSha256',
      'clang',
      'clangSha256',
      'linker',
      'linkerSha256',
      'node',
      'zlib',
      'os',
      'systemPackages',
    ],
    'invalid_toolchain',
  );
  requireThat(
    tools.sdk === '10.0.109' && tools.clang === '18.1.3' && tools.os === 'ubuntu-24.04-x64',
    'unsupported_toolchain',
  );
  for (const field of ['sdkDriverSha256', 'clangSha256', 'linkerSha256'])
    requireThat(matches(tools[field], hex64), 'invalid_toolchain');
  for (const field of ['linker', 'node', 'zlib'])
    requireThat(boundedText(tools[field]), 'invalid_toolchain');
  requireThat(
    Array.isArray(tools.systemPackages) &&
      tools.systemPackages.length > 0 &&
      tools.systemPackages.length <= 16,
    'invalid_system_inventory',
  );
  let previous = '';
  for (const item of tools.systemPackages) {
    keys(item, ['name', 'version'], 'invalid_system_inventory');
    requireThat(
      /^[a-z0-9][a-z0-9+.-]{0,80}$/.test(item.name) &&
        typeof item.name === 'string' &&
        item.name > previous &&
        boundedText(item.version),
      'invalid_system_inventory',
    );
    previous = item.name;
  }
  requireThat(
    Array.isArray(input.dependencies) &&
      input.dependencies.length > 0 &&
      input.dependencies.length <= 16,
    'invalid_dependency_inventory',
  );
  previous = '';
  for (const item of input.dependencies) {
    keys(item, ['id', 'version', 'sha256', 'contentHash', 'role'], 'invalid_dependency_inventory');
    requireThat(
      /^[a-z0-9][a-z0-9.]{0,100}$/.test(item.id) &&
        typeof item.id === 'string' &&
        item.id > previous &&
        /^[0-9]+\.[0-9]+\.[0-9]+$/.test(item.version) &&
        typeof item.version === 'string' &&
        matches(item.sha256, hex64) &&
        /^[A-Za-z0-9+/]{86}==$/.test(item.contentHash) &&
        typeof item.contentHash === 'string' &&
        ['managed', 'native-runtime', 'build'].includes(item.role),
      'invalid_dependency_inventory',
    );
    previous = item.id;
  }
}
function validateNative(native) {
  keys(native, ['needed', 'runtimeLoaded'], 'invalid_native_inventory');
  for (const field of ['needed', 'runtimeLoaded']) {
    requireThat(
      Array.isArray(native[field]) && native[field].length <= 16 && native[field].length > 0,
      'invalid_native_inventory',
    );
    let previous = '';
    for (const name of native[field]) {
      requireThat(
        typeof name === 'string' &&
          /^lib[a-zA-Z0-9_.+-]+\.so(?:\.[0-9]+)*$/.test(name) &&
          name > previous,
        'invalid_native_inventory',
      );
      previous = name;
    }
  }
}
function validateManifest(manifest) {
  keys(
    manifest,
    [
      'formatVersion',
      'releaseVersion',
      'platform',
      'sourceCommit',
      'sourceTree',
      'buildId',
      'informationalVersion',
      'launcher',
      'buildInputs',
      'nativeDependencies',
      'members',
    ],
    'invalid_manifest',
  );
  requireThat(
    manifest.formatVersion === 1 && manifest.launcher === 'r7-d0',
    'unsupported_manifest',
  );
  validateIdentity(
    Object.fromEntries(
      [
        'releaseVersion',
        'platform',
        'sourceCommit',
        'sourceTree',
        'buildId',
        'informationalVersion',
      ].map((key) => [key, manifest[key]]),
    ),
  );
  validateInputs(manifest.buildInputs);
  requireThat(
    manifest.buildId === sha256(canonicalJson(manifest.buildInputs)),
    'build_id_mismatch',
  );
  for (const key of ['releaseVersion', 'platform', 'sourceCommit', 'sourceTree'])
    requireThat(manifest[key] === manifest.buildInputs[key], 'build_input_identity_mismatch');
  requireThat(
    manifest.buildInputs.noticesSha256 === manifest.members?.[1]?.sha256,
    'notice_input_mismatch',
  );
  validateNative(manifest.nativeDependencies);
  validateInventory(manifest.members, [MEMBERS[0], MEMBERS[2]]);
}
function validateReceipt(receipt) {
  keys(
    receipt,
    ['formatVersion', 'archiveName', 'archiveSize', 'archiveSha256', 'identity', 'members'],
    'invalid_expected_receipt',
  );
  requireThat(
    receipt.formatVersion === 1 &&
      Number.isSafeInteger(receipt.archiveSize) &&
      receipt.archiveSize > 0 &&
      receipt.archiveSize <= LIMITS.archive &&
      matches(receipt.archiveSha256, hex64),
    'invalid_expected_receipt',
  );
  validateIdentity(receipt.identity);
  requireThat(
    receipt.archiveName === archiveName(receipt.identity.releaseVersion),
    'invalid_archive_name',
  );
  validateInventory(receipt.members);
}
export function archiveName(version) {
  return 'agentic-pr-review-' + validateReleaseVersion(version) + '-linux-x64.tar.gz';
}
function entryHeader(spec, size) {
  const header = Buffer.alloc(512);
  header.write(spec.path, 0, 'ascii');
  const octal = (value, width) => value.toString(8).padStart(width - 1, '0') + '\0';
  header.write(octal(spec.mode, 8), 100, 'ascii');
  header.write(octal(0, 8), 108, 'ascii');
  header.write(octal(0, 8), 116, 'ascii');
  header.write(octal(size, 12), 124, 'ascii');
  header.write(octal(0, 12), 136, 'ascii');
  header.fill(32, 148, 156);
  header[156] = 48;
  header.write('ustar\0', 257, 'ascii');
  header.write('00', 263, 'ascii');
  const checksum = header.reduce((sum, byte) => sum + byte, 0);
  header.write(checksum.toString(8).padStart(6, '0') + '\0 ', 148, 'ascii');
  return header;
}
function inventory(spec, bytes) {
  return { path: spec.path, mode: spec.mode, size: bytes.length, sha256: sha256(bytes) };
}
export function validateElf(bytes) {
  requireThat(
    bytes.length >= 64 &&
      bytes.subarray(0, 7).equals(Buffer.from('7f454c46020101', 'hex')) &&
      [0, 3].includes(bytes[7]) &&
      [2, 3].includes(bytes.readUInt16LE(16)) &&
      bytes.readUInt16LE(18) === 62 &&
      bytes.readUInt32LE(20) === 1 &&
      bytes.readUInt16LE(52) === 64,
    'invalid_linux_x64_executable',
  );
}
function receiptFor(archive, manifest, files) {
  return {
    formatVersion: 1,
    archiveName: archiveName(manifest.releaseVersion),
    archiveSize: archive.length,
    archiveSha256: sha256(archive),
    identity: Object.fromEntries(
      [
        'releaseVersion',
        'platform',
        'sourceCommit',
        'sourceTree',
        'buildId',
        'informationalVersion',
      ].map((key) => [key, manifest[key]]),
    ),
    members: MEMBERS.map((spec, index) => inventory(spec, files[index])),
  };
}
export function encodePackage(executable, notices, buildInputs, nativeDependencies) {
  validateInputs(buildInputs);
  validateElf(executable);
  const buildId = sha256(canonicalJson(buildInputs));
  const manifest = {
    formatVersion: 1,
    releaseVersion: buildInputs.releaseVersion,
    platform: buildInputs.platform,
    sourceCommit: buildInputs.sourceCommit,
    sourceTree: buildInputs.sourceTree,
    buildId,
    informationalVersion: compiledIdentity(buildInputs.releaseVersion, buildId),
    launcher: 'r7-d0',
    buildInputs,
    nativeDependencies,
    members: [inventory(MEMBERS[0], executable), inventory(MEMBERS[2], notices)],
  };
  validateManifest(manifest);
  const files = [executable, Buffer.from(canonicalJson(manifest)), notices];
  validateInventory(MEMBERS.map((spec, index) => inventory(spec, files[index])));
  const tar = Buffer.concat([
    ...files.flatMap((bytes, index) => [
      entryHeader(MEMBERS[index], bytes.length),
      bytes,
      Buffer.alloc((512 - (bytes.length % 512)) % 512),
    ]),
    Buffer.alloc(1024),
  ]);
  requireThat(tar.length <= LIMITS.expanded, 'expanded_archive_too_large');
  const trailer = Buffer.alloc(8);
  trailer.writeUInt32LE(crc32(tar), 0);
  trailer.writeUInt32LE(tar.length, 4);
  const archive = Buffer.concat([gzipHeader, deflateRawSync(tar, { level: 9 }), trailer]);
  requireThat(archive.length <= LIMITS.archive, 'archive_too_large');
  return { archive, receipt: receiptFor(archive, manifest, files) };
}
export function inspectPackage(bytes, expected) {
  validateReceipt(expected);
  requireThat(
    Buffer.isBuffer(bytes) &&
      bytes.length <= LIMITS.archive &&
      bytes.length === expected.archiveSize &&
      sha256(bytes) === expected.archiveSha256,
    'archive_digest_mismatch',
  );
  requireThat(
    bytes.length >= 20 && bytes.subarray(0, 10).equals(gzipHeader),
    'invalid_gzip_header',
  );
  let inflated;
  try {
    inflated = inflateRawSync(bytes.subarray(10), { maxOutputLength: LIMITS.expanded, info: true });
  } catch {
    throw new Error('invalid_or_excessive_gzip');
  }
  const tar = inflated.buffer;
  const trailerOffset = 10 + inflated.engine.bytesWritten;
  requireThat(
    trailerOffset + 8 === bytes.length &&
      bytes.readUInt32LE(trailerOffset) === crc32(tar) &&
      bytes.readUInt32LE(trailerOffset + 4) === tar.length,
    'invalid_gzip_trailer',
  );
  let offset = 0;
  const files = MEMBERS.map((spec) => {
    requireThat(offset + 512 <= tar.length, 'truncated_tar_header');
    const header = tar.subarray(offset, offset + 512);
    const sizeField = header.subarray(124, 136).toString('ascii');
    requireThat(/^[0-7]{11}\0$/.test(sizeField), 'invalid_tar_size');
    const size = Number.parseInt(sizeField, 8);
    requireThat(size > 0 && size <= spec.limit, 'member_too_large_or_empty');
    requireThat(header.equals(entryHeader(spec, size)), 'noncanonical_tar_header');
    offset += 512;
    const end = offset + size;
    const paddingEnd = end + ((512 - (size % 512)) % 512);
    requireThat(
      paddingEnd <= tar.length && tar.subarray(end, paddingEnd).every((byte) => byte === 0),
      'truncated_or_invalid_tar_padding',
    );
    const member = tar.subarray(offset, end);
    offset = paddingEnd;
    return member;
  });
  requireThat(
    offset + 1024 === tar.length && tar.subarray(offset).every((byte) => byte === 0),
    'invalid_tar_end_or_inventory',
  );
  let manifest;
  try {
    manifest = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(files[1]));
  } catch {
    throw new Error('invalid_manifest_json');
  }
  requireThat(canonicalJson(manifest) === files[1].toString('utf8'), 'noncanonical_manifest_json');
  validateManifest(manifest);
  requireThat(
    canonicalJson(manifest.members) ===
      canonicalJson([inventory(MEMBERS[0], files[0]), inventory(MEMBERS[2], files[2])]),
    'member_digest_mismatch',
  );
  validateElf(files[0]);
  try {
    new TextDecoder('utf-8', { fatal: true }).decode(files[2]);
  } catch {
    throw new Error('invalid_notices_utf8');
  }
  const receipt = receiptFor(bytes, manifest, files);
  requireThat(
    canonicalJson(receipt.identity) === canonicalJson(expected.identity),
    'expected_identity_mismatch',
  );
  requireThat(
    canonicalJson(receipt.members) === canonicalJson(expected.members),
    'expected_inventory_mismatch',
  );
  return { manifest, receipt, files };
}
export async function readBoundedFile(path, cap) {
  const handle = await open(
    path,
    constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0),
  );
  try {
    const before = await handle.stat();
    requireThat(
      before.isFile() && before.size > 0 && before.size <= cap,
      'invalid_or_excessive_file',
    );
    const bytes = Buffer.alloc(before.size);
    let offset = 0;
    while (offset < bytes.length) {
      const { bytesRead } = await handle.read(bytes, offset, bytes.length - offset, offset);
      requireThat(bytesRead > 0, 'file_changed');
      offset += bytesRead;
    }
    const extra = Buffer.alloc(1);
    const { bytesRead } = await handle.read(extra, 0, 1, bytes.length);
    const after = await handle.stat();
    requireThat(
      bytesRead === 0 &&
        before.size === after.size &&
        before.mtimeMs === after.mtimeMs &&
        before.ctimeMs === after.ctimeMs,
      'file_changed',
    );
    return bytes;
  } finally {
    await handle.close();
  }
}
export async function materializePackage(bytes, expected, parent) {
  // Inspection is repeated inside this boundary; caller-owned views are never trusted.
  const inspected = inspectPackage(Buffer.from(bytes), expected);
  const root = await mkdtemp(join(parent, 'apr-package-'));
  try {
    await chmod(root, 0o700);
    const directory = join(root, 'agentic-pr-review');
    await mkdir(directory, { mode: 0o700 });
    for (let index = 0; index < MEMBERS.length; index++) {
      const spec = MEMBERS[index];
      const path = join(root, spec.path);
      const handle = await open(
        path,
        constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0),
        spec.mode,
      );
      try {
        await handle.writeFile(inspected.files[index]);
        await handle.chmod(spec.mode);
      } finally {
        await handle.close();
      }
    }
    return { root, executable: join(root, MEMBERS[0].path), manifest: inspected.manifest };
  } catch (error) {
    await rm(root, { recursive: true, force: true });
    throw error;
  }
}
