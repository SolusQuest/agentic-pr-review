import { createHash } from 'node:crypto';
import { SOLUS_IDS, SOLUS_VERSION, dependencyVersionAllowed } from './solus-package-contract.mjs';
import { constants } from 'node:fs';
import { chmod, mkdir, mkdtemp, open, rm } from 'node:fs/promises';
import { join } from 'node:path';
import { crc32, deflateRawSync, inflateRawSync } from 'node:zlib';

// Original R7 format-1 codec: explicit historical fixtures only.
// Current production consumers use managed-v1; no native producer remains.
export const NOTICE_MAXIMUM_BYTES = 1024 * 1024;
export const RELEASE_VERSION_MAXIMUM_BYTES = 48;
export const HISTORICAL_NATIVE_LIMITS = /* @__PURE__ */ Object.freeze({
  archive: 32 * 1024 * 1024,
  executable: 64 * 1024 * 1024,
  notices: 1024 * 1024,
  manifest: 64 * 1024,
  expanded: 66 * 1024 * 1024,
  releaseVersion: 48,
});
export const HISTORICAL_NATIVE_MEMBERS = /* @__PURE__ */ Object.freeze([
  {
    path: 'agentic-pr-review/agentic-pr-review',
    mode: 0o755,
    limit: HISTORICAL_NATIVE_LIMITS.executable,
  },
  {
    path: 'agentic-pr-review/manifest.json',
    mode: 0o644,
    limit: HISTORICAL_NATIVE_LIMITS.manifest,
  },
  { path: 'agentic-pr-review/THIRD-PARTY-NOTICES.txt', mode: 0o644, limit: NOTICE_MAXIMUM_BYTES },
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
      version.length <= RELEASE_VERSION_MAXIMUM_BYTES &&
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
function validateInventory(members, specs = HISTORICAL_NATIVE_MEMBERS) {
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
      /^[a-z0-9][a-z0-9.-]{0,100}$/.test(item.id) &&
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
          (/^lib[a-zA-Z0-9_.+-]+\.so(?:\.[0-9]+)*$/.test(name) ||
            name === 'ld-linux-x86-64.so.2') &&
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
  validateInventory(manifest.members, [HISTORICAL_NATIVE_MEMBERS[0], HISTORICAL_NATIVE_MEMBERS[2]]);
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
      receipt.archiveSize <= HISTORICAL_NATIVE_LIMITS.archive &&
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
export function validateHistoricalNativeElfForFixture(bytes) {
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
    members: HISTORICAL_NATIVE_MEMBERS.map((spec, index) => inventory(spec, files[index])),
  };
}
export function encodeHistoricalNativePackageForFixture(
  executable,
  notices,
  buildInputs,
  nativeDependencies,
) {
  validateInputs(buildInputs);
  validateHistoricalNativeElfForFixture(executable);
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
    members: [
      inventory(HISTORICAL_NATIVE_MEMBERS[0], executable),
      inventory(HISTORICAL_NATIVE_MEMBERS[2], notices),
    ],
  };
  validateManifest(manifest);
  const files = [executable, Buffer.from(canonicalJson(manifest)), notices];
  validateInventory(HISTORICAL_NATIVE_MEMBERS.map((spec, index) => inventory(spec, files[index])));
  const tar = Buffer.concat([
    ...files.flatMap((bytes, index) => [
      entryHeader(HISTORICAL_NATIVE_MEMBERS[index], bytes.length),
      bytes,
      Buffer.alloc((512 - (bytes.length % 512)) % 512),
    ]),
    Buffer.alloc(1024),
  ]);
  requireThat(tar.length <= HISTORICAL_NATIVE_LIMITS.expanded, 'expanded_archive_too_large');
  const trailer = Buffer.alloc(8);
  trailer.writeUInt32LE(crc32(tar), 0);
  trailer.writeUInt32LE(tar.length, 4);
  const archive = Buffer.concat([gzipHeader, deflateRawSync(tar, { level: 9 }), trailer]);
  requireThat(archive.length <= HISTORICAL_NATIVE_LIMITS.archive, 'archive_too_large');
  return { archive, receipt: receiptFor(archive, manifest, files) };
}
export function inspectHistoricalNativePackageForFixture(bytes, expected) {
  validateReceipt(expected);
  requireThat(
    Buffer.isBuffer(bytes) &&
      bytes.length <= HISTORICAL_NATIVE_LIMITS.archive &&
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
    inflated = inflateRawSync(bytes.subarray(10), {
      maxOutputLength: HISTORICAL_NATIVE_LIMITS.expanded,
      info: true,
    });
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
  const files = HISTORICAL_NATIVE_MEMBERS.map((spec) => {
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
      canonicalJson([
        inventory(HISTORICAL_NATIVE_MEMBERS[0], files[0]),
        inventory(HISTORICAL_NATIVE_MEMBERS[2], files[2]),
      ]),
    'member_digest_mismatch',
  );
  validateHistoricalNativeElfForFixture(files[0]);
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
export async function materializeHistoricalNativePackageForFixture(bytes, expected, parent) {
  // Inspection is repeated inside this boundary; caller-owned views are never trusted.
  const inspected = inspectHistoricalNativePackageForFixture(Buffer.from(bytes), expected);
  const root = await mkdtemp(join(parent, 'apr-package-'));
  try {
    await chmod(root, 0o700);
    const directory = join(root, 'agentic-pr-review');
    await mkdir(directory, { mode: 0o700 });
    for (let index = 0; index < HISTORICAL_NATIVE_MEMBERS.length; index++) {
      const spec = HISTORICAL_NATIVE_MEMBERS[index];
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
    return {
      root,
      executable: join(root, HISTORICAL_NATIVE_MEMBERS[0].path),
      manifest: inspected.manifest,
    };
  } catch (error) {
    await rm(root, { recursive: true, force: true });
    throw error;
  }
}

// CR1-D1 producer contract. Native exports above remain selected until CR1-D6.
export const MANAGED_LIMITS = /* @__PURE__ */ Object.freeze({
  archive: 67_108_864,
  expanded: 134_217_728,
  file: 33_554_432,
  manifest: 262_144,
  entries: 512,
  path: 100,
  depth: 8,
});
const managedRoot = 'agentic-pr-review/';
const managedManifestPath = 'agentic-pr-review/manifest.json';
const managedNoticesPath = 'agentic-pr-review/THIRD-PARTY-NOTICES.txt';
export const MANAGED_TARGET = /* @__PURE__ */ Object.freeze({
  TargetFramework: 'net10.0',
  RuntimeIdentifier: 'linux-x64',
  RuntimeFrameworkVersion: '10.0.9',
  UseAppHost: false,
  PublishAot: false,
  PublishTrimmed: false,
  PublishSingleFile: false,
  SelfContained: false,
  IsAotCompatible: false,
  VerifyReferenceAotCompatibility: false,
  EnableRuntimePackDownload: false,
  EnableAppHostPackDownload: false,
  DisableTransitiveFrameworkReferenceDownloads: true,
});
function managedPath(path) {
  requireThat(
    typeof path === 'string' &&
      path.length <= MANAGED_LIMITS.path &&
      path.startsWith(managedRoot) &&
      /^[a-zA-Z0-9_.+/-]+$/.test(path) &&
      path.split('/').length <= MANAGED_LIMITS.depth &&
      path.split('/').every((part) => part && part !== '.' && part !== '..'),
    'invalid_managed_path',
  );
  return path;
}
function managedInventory(members) {
  requireThat(
    Array.isArray(members) && members.length > 0 && members.length <= MANAGED_LIMITS.entries,
    'invalid_managed_inventory',
  );
  const aliases = new Set();
  const spellings = new Map();
  let previous = '',
    total = 0;
  for (const item of members) {
    keys(item, ['path', 'mode', 'size', 'sha256'], 'invalid_managed_member');
    managedPath(item.path);
    requireThat(
      item.path > previous &&
        item.mode === 0o644 &&
        Number.isSafeInteger(item.size) &&
        item.size >= 0 &&
        item.size <=
          (item.path === managedManifestPath ? MANAGED_LIMITS.manifest : MANAGED_LIMITS.file) &&
        matches(item.sha256, hex64),
      'invalid_managed_member',
    );
    const alias = item.path.toLowerCase();
    requireThat(!aliases.has(alias), 'managed_path_collision');
    aliases.add(alias);
    const parts = item.path.split('/');
    for (let i = 1; i <= parts.length; i++) {
      const prefix = parts.slice(0, i).join('/'),
        key = prefix.toLowerCase();
      requireThat(!spellings.has(key) || spellings.get(key) === prefix, 'managed_path_collision');
      spellings.set(key, prefix);
    }
    total += 512 + Math.ceil(item.size / 512) * 512;
    previous = item.path;
  }
  for (const path of aliases) {
    const parts = path.split('/');
    for (let i = 1; i < parts.length; i++)
      requireThat(!aliases.has(parts.slice(0, i).join('/')), 'managed_path_collision');
  }
  requireThat(total + 1024 <= MANAGED_LIMITS.expanded, 'managed_expanded_limit');
}
function managedInputs(input) {
  keys(
    input,
    [
      'releaseVersion',
      'platform',
      'sourceCommit',
      'sourceTree',
      'policySha256',
      'lockSha256',
      'assetsSha256',
      'noticesSha256',
      'target',
      'toolchain',
      'dependencies',
    ],
    'invalid_managed_inputs',
  );
  validateReleaseVersion(input.releaseVersion);
  requireThat(
    input.platform === 'linux-x64' &&
      matches(input.sourceCommit, hex40) &&
      matches(input.sourceTree, hex40),
    'invalid_managed_inputs',
  );
  for (const field of ['policySha256', 'lockSha256', 'assetsSha256', 'noticesSha256'])
    requireThat(matches(input[field], hex64), 'invalid_managed_inputs');
  requireThat(
    canonicalJson(input.target) === canonicalJson(MANAGED_TARGET),
    'invalid_managed_target',
  );
  keys(
    input.toolchain,
    ['sdk', 'sdkDriverSha256', 'node', 'zlib', 'os'],
    'invalid_managed_toolchain',
  );
  requireThat(
    input.toolchain.sdk === '10.0.109' &&
      input.toolchain.os === 'ubuntu-24.04-x64' &&
      matches(input.toolchain.sdkDriverSha256, hex64) &&
      /^24\./.test(input.toolchain.node) &&
      boundedText(input.toolchain.node) &&
      boundedText(input.toolchain.zlib),
    'invalid_managed_toolchain',
  );
  requireThat(
    Array.isArray(input.dependencies) &&
      input.dependencies.length > 0 &&
      input.dependencies.length <= 16,
    'invalid_managed_dependencies',
  );
  let previous = '';
  for (const item of input.dependencies) {
    keys(item, ['id', 'version', 'sha256', 'contentHash', 'role'], 'invalid_managed_dependencies');
    requireThat(
      typeof item.id === 'string' &&
        /^[a-z0-9][a-z0-9.-]{0,100}$/.test(item.id) &&
        item.id > previous &&
        typeof item.version === 'string' &&
        dependencyVersionAllowed(item.id, item.version, SOLUS_VERSION) &&
        matches(item.sha256, hex64) &&
        typeof item.contentHash === 'string' &&
        /^[A-Za-z0-9+/]{86}==$/.test(item.contentHash) &&
        item.role === 'managed',
      'invalid_managed_dependencies',
    );
    previous = item.id;
  }
  const solus = input.dependencies.filter((item) => item.id.startsWith('solusagent.'));
  requireThat(
    solus.length === 0 ||
      (solus.length === SOLUS_IDS.length &&
        SOLUS_IDS.every((id) => solus.some((item) => item.id === id))),
    'invalid_managed_dependencies',
  );
}
function managedName(version) {
  return 'agentic-pr-review-' + validateReleaseVersion(version) + '-linux-x64-managed.tar.gz';
}
function managedIdentity(input) {
  const buildId = sha256(canonicalJson(input));
  return {
    releaseVersion: input.releaseVersion,
    platform: input.platform,
    sourceCommit: input.sourceCommit,
    sourceTree: input.sourceTree,
    buildId,
    informationalVersion: compiledIdentity(input.releaseVersion, buildId),
  };
}
export function validateManagedReceipt(receipt) {
  keys(
    receipt,
    ['formatVersion', 'archiveName', 'archiveSize', 'archiveSha256', 'identity', 'members'],
    'invalid_managed_receipt',
  );
  requireThat(
    receipt.formatVersion === 'managed-v1' &&
      Number.isSafeInteger(receipt.archiveSize) &&
      receipt.archiveSize > 0 &&
      receipt.archiveSize <= MANAGED_LIMITS.archive &&
      matches(receipt.archiveSha256, hex64),
    'invalid_managed_receipt',
  );
  validateIdentity(receipt.identity);
  requireThat(
    receipt.archiveName === managedName(receipt.identity.releaseVersion),
    'invalid_managed_receipt',
  );
  managedInventory(receipt.members);
  requireThat(
    receipt.members.some((item) => item.path === managedManifestPath),
    'missing_managed_manifest',
  );
}
export function validateManagedPublishFiles(files) {
  requireThat(Array.isArray(files), 'invalid_managed_files');
  const entries = files.map(({ path, bytes }) => {
    requireThat(Buffer.isBuffer(bytes), 'invalid_managed_file_bytes');
    return inventory({ path: managedPath(managedRoot + path), mode: 0o644 }, bytes);
  });
  managedInventory([...entries].sort((a, b) => (a.path < b.path ? -1 : a.path > b.path ? 1 : 0)));
  const byPath = new Map(files.map((item) => [item.path, item.bytes]));
  for (const path of [
    'AgenticPrReview.Runtime.dll',
    'AgenticPrReview.Runtime.deps.json',
    'AgenticPrReview.Runtime.runtimeconfig.json',
  ])
    requireThat(byPath.has(path) && byPath.get(path).length > 0, 'missing_managed_required_file');
  requireThat(
    !byPath.has('AgenticPrReview.Runtime') &&
      !byPath.has('AgenticPrReview.Runtime.exe') &&
      !byPath.has('manifest.json') &&
      !byPath.has('THIRD-PARTY-NOTICES.txt') &&
      ![...byPath.keys()].some((path) =>
        /^(?:lib)?(?:coreclr|hostfxr|hostpolicy)(?:\.|$)/i.test(path.split('/').at(-1)),
      ),
    'unexpected_managed_publish_file',
  );
  let config, deps;
  try {
    const decode = (bytes) => JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
    config = decode(byPath.get('AgenticPrReview.Runtime.runtimeconfig.json')).runtimeOptions;
    deps = decode(byPath.get('AgenticPrReview.Runtime.deps.json'));
  } catch {
    throw new Error('invalid_managed_configuration');
  }
  requireThat(
    config?.tfm === 'net10.0' &&
      config.framework?.name === 'Microsoft.NETCore.App' &&
      config.framework.version === '10.0.9' &&
      !config.includedFrameworks &&
      !config.frameworks &&
      !config.additionalProbingPaths,
    'invalid_managed_runtimeconfig',
  );
  const target = deps.targets?.[deps.runtimeTarget?.name];
  requireThat(
    deps.runtimeTarget?.name === '.NETCoreApp,Version=v10.0/linux-x64' &&
      target &&
      typeof target === 'object' &&
      Object.keys(target).length > 0,
    'invalid_managed_deps',
  );
  for (const library of Object.values(target)) {
    for (const kind of ['runtime', 'native', 'resources', 'runtimeTargets']) {
      for (const [path, meta] of Object.entries(library[kind] || {})) {
        requireThat(
          typeof path === 'string' &&
            !path.includes('\\') &&
            !path.startsWith('/') &&
            path.split('/').every((part) => part && part !== '.' && part !== '..'),
          'invalid_managed_deps_path',
        );
        if (path.split('/').at(-1) === '_._') continue;
        const flat =
          kind === 'resources' && meta.locale
            ? meta.locale + '/' + path.split('/').at(-1)
            : path.split('/').at(-1);
        requireThat(byPath.has(path) || byPath.has(flat), 'missing_managed_dependency_asset');
      }
    }
  }
  return entries;
}
export async function collectManagedPublishTree(root) {
  const { lstat, readdir } = await import('node:fs/promises');
  requireThat(
    (await lstat(root)).isDirectory() && !(await lstat(root)).isSymbolicLink(),
    'invalid_managed_publish_root',
  );
  const files = [];
  let visited = 0,
    total = 0;
  async function walk(relative) {
    for (const name of (await readdir(join(root, relative))).sort()) {
      const path = relative ? relative + '/' + name : name;
      managedPath(managedRoot + path);
      requireThat(++visited <= MANAGED_LIMITS.entries, 'managed_entry_limit');
      const source = join(root, path),
        stat = await lstat(source);
      if (stat.isDirectory() && !stat.isSymbolicLink()) {
        await walk(path);
        continue;
      }
      requireThat(
        stat.isFile() &&
          !stat.isSymbolicLink() &&
          stat.nlink === 1 &&
          stat.size <= MANAGED_LIMITS.file,
        'invalid_managed_publish_kind',
      );
      total += stat.size;
      requireThat(total <= MANAGED_LIMITS.expanded, 'managed_expanded_limit');
      const handle = await open(
        source,
        constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0) | (constants.O_NONBLOCK ?? 0),
      );
      try {
        const before = await handle.stat();
        requireThat(
          before.isFile() &&
            before.ino === stat.ino &&
            before.dev === stat.dev &&
            before.size === stat.size,
          'managed_publish_changed',
        );
        const bytes = Buffer.alloc(stat.size + 1);
        let offset = 0;
        while (offset < bytes.length) {
          const { bytesRead } = await handle.read(bytes, offset, bytes.length - offset, offset);
          if (!bytesRead) break;
          offset += bytesRead;
        }
        const after = await handle.stat();
        requireThat(
          offset === stat.size &&
            before.size === after.size &&
            before.mtimeMs === after.mtimeMs &&
            before.ctimeMs === after.ctimeMs,
          'managed_publish_changed',
        );
        files.push({ path, bytes: bytes.subarray(0, stat.size) });
      } finally {
        await handle.close();
      }
    }
  }
  await walk('');
  validateManagedPublishFiles(files);
  return files;
}
export function encodeManagedPackage(files, notices, buildInputs) {
  managedInputs(buildInputs);
  const publishMembers = validateManagedPublishFiles(files);
  requireThat(
    Buffer.isBuffer(notices) &&
      notices.length > 0 &&
      notices.length <= NOTICE_MAXIMUM_BYTES &&
      sha256(notices) === buildInputs.noticesSha256,
    'invalid_managed_notices',
  );
  new TextDecoder('utf-8', { fatal: true }).decode(notices);
  const identity = managedIdentity(buildInputs);
  const members = [
    ...publishMembers,
    inventory({ path: managedNoticesPath, mode: 0o644 }, notices),
  ].sort((a, b) => (a.path < b.path ? -1 : 1));
  const manifest = {
    formatVersion: 'managed-v1',
    ...identity,
    entrypoint: managedRoot + 'AgenticPrReview.Runtime.dll',
    buildInputs,
    members,
  };
  const manifestBytes = Buffer.from(canonicalJson(manifest));
  requireThat(manifestBytes.length <= MANAGED_LIMITS.manifest, 'managed_manifest_limit');
  const all = [
    ...files.map((file) => ({ path: managedRoot + file.path, bytes: file.bytes })),
    { path: managedNoticesPath, bytes: notices },
    { path: managedManifestPath, bytes: manifestBytes },
  ].sort((a, b) => (a.path < b.path ? -1 : 1));
  const inventoryAll = all.map((file) => inventory({ path: file.path, mode: 0o644 }, file.bytes));
  managedInventory(inventoryAll);
  const tar = Buffer.concat([
    ...all.flatMap((file) => [
      entryHeader({ path: file.path, mode: 0o644 }, file.bytes.length),
      file.bytes,
      Buffer.alloc((512 - (file.bytes.length % 512)) % 512),
    ]),
    Buffer.alloc(1024),
  ]);
  const trailer = Buffer.alloc(8);
  trailer.writeUInt32LE(crc32(tar));
  trailer.writeUInt32LE(tar.length, 4);
  const archive = Buffer.concat([gzipHeader, deflateRawSync(tar, { level: 9 }), trailer]);
  requireThat(archive.length <= MANAGED_LIMITS.archive, 'managed_archive_limit');
  return {
    archive,
    receipt: {
      formatVersion: 'managed-v1',
      archiveName: managedName(buildInputs.releaseVersion),
      archiveSize: archive.length,
      archiveSha256: sha256(archive),
      identity,
      members: inventoryAll,
    },
  };
}
export function inspectManagedPackage(bytes, expected) {
  validateManagedReceipt(expected);
  requireThat(
    Buffer.isBuffer(bytes) &&
      bytes.length === expected.archiveSize &&
      sha256(bytes) === expected.archiveSha256,
    'managed_archive_digest_mismatch',
  );
  requireThat(
    bytes.length >= 20 && bytes.subarray(0, 10).equals(gzipHeader),
    'invalid_gzip_header',
  );
  let inflated;
  try {
    inflated = inflateRawSync(bytes.subarray(10), {
      maxOutputLength: MANAGED_LIMITS.expanded,
      info: true,
    });
  } catch {
    throw new Error('invalid_or_excessive_gzip');
  }
  const tar = inflated.buffer,
    end = 10 + inflated.engine.bytesWritten;
  requireThat(
    end + 8 === bytes.length &&
      bytes.readUInt32LE(end) === crc32(tar) &&
      bytes.readUInt32LE(end + 4) === tar.length,
    'invalid_gzip_trailer',
  );
  let offset = 0;
  const files = expected.members.map((spec) => {
    requireThat(offset + 512 <= tar.length, 'truncated_tar_header');
    requireThat(
      tar.subarray(offset, offset + 512).equals(entryHeader(spec, spec.size)),
      'noncanonical_managed_tar_header',
    );
    offset += 512;
    const end = offset + spec.size,
      padded = end + ((512 - (spec.size % 512)) % 512);
    requireThat(
      padded <= tar.length && tar.subarray(end, padded).every((byte) => byte === 0),
      'invalid_tar_padding',
    );
    const bytes = tar.subarray(offset, end);
    offset = padded;
    requireThat(sha256(bytes) === spec.sha256, 'managed_member_digest_mismatch');
    return { path: spec.path, bytes };
  });
  requireThat(
    offset + 1024 === tar.length && tar.subarray(offset).every((byte) => byte === 0),
    'invalid_tar_end_or_inventory',
  );
  let manifest;
  const manifestFile = files.find((file) => file.path === managedManifestPath);
  try {
    manifest = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(manifestFile.bytes));
  } catch {
    throw new Error('invalid_managed_manifest_json');
  }
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
      'entrypoint',
      'buildInputs',
      'members',
    ],
    'invalid_managed_manifest',
  );
  requireThat(
    canonicalJson(manifest) === manifestFile.bytes.toString('utf8') &&
      manifest.formatVersion === 'managed-v1' &&
      manifest.entrypoint === managedRoot + 'AgenticPrReview.Runtime.dll',
    'invalid_managed_manifest',
  );
  managedInputs(manifest.buildInputs);
  const identity = managedIdentity(manifest.buildInputs);
  requireThat(
    canonicalJson(identity) === canonicalJson(expected.identity) &&
      Object.keys(identity).every((key) => manifest[key] === identity[key]),
    'managed_identity_mismatch',
  );
  const payloadMembers = expected.members.filter((item) => item.path !== managedManifestPath);
  requireThat(
    canonicalJson(manifest.members) === canonicalJson(payloadMembers),
    'managed_inventory_mismatch',
  );
  const notices = files.find((file) => file.path === managedNoticesPath);
  requireThat(
    notices &&
      notices.bytes.length > 0 &&
      notices.bytes.length <= NOTICE_MAXIMUM_BYTES &&
      sha256(notices.bytes) === manifest.buildInputs.noticesSha256,
    'invalid_managed_notices',
  );
  new TextDecoder('utf-8', { fatal: true }).decode(notices.bytes);
  validateManagedPublishFiles(
    files
      .filter((file) => file.path !== managedManifestPath && file.path !== managedNoticesPath)
      .map((file) => ({ path: file.path.slice(managedRoot.length), bytes: file.bytes })),
  );
  return { manifest, receipt: expected, files };
}
export async function materializeManagedPackage(bytes, expected, parent) {
  const inspected = inspectManagedPackage(Buffer.from(bytes), expected);
  const root = await mkdtemp(join(parent, 'apr-managed-package-'));
  try {
    await chmod(root, 0o700);
    for (const file of inspected.files) {
      const parts = file.path.split('/');
      for (let i = 1; i < parts.length; i++)
        await mkdir(join(root, ...parts.slice(0, i)), { mode: 0o700 }).catch((error) => {
          if (error.code !== 'EEXIST') throw error;
        });
      const handle = await open(
        join(root, file.path),
        constants.O_WRONLY | constants.O_CREAT | constants.O_EXCL | (constants.O_NOFOLLOW ?? 0),
        0o644,
      );
      try {
        await handle.writeFile(file.bytes);
        await handle.chmod(0o644);
      } finally {
        await handle.close();
      }
    }
    return {
      root,
      entrypoint: join(root, inspected.manifest.entrypoint),
      manifest: inspected.manifest,
    };
  } catch (error) {
    await rm(root, { recursive: true, force: true });
    throw error;
  }
}
