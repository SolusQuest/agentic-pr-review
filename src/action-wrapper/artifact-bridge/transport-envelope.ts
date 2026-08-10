import { createHash } from 'node:crypto';
import { constants } from 'node:fs';
import { open, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { createInflateRaw } from 'node:zlib';

import { safePositiveDecimal, sha256 } from './contracts.js';
import {
  ARTIFACT_BRIDGE_LIMITS,
  ARTIFACT_ENVELOPE_DISCRIMINATOR,
  ARTIFACT_ENVELOPE_ENTRY,
} from './limits.js';
import { strictParseArtifactBridgeJson } from './strict-json.js';

export interface ArtifactTransportEnvelopeMetadata {
  readonly producingRunId: string;
  readonly producingRunAttempt: string;
  readonly encryptedObjectDigest: string;
  readonly encryptedObjectSize: number;
}

export interface DecodedArtifactTransportEnvelope extends ArtifactTransportEnvelopeMetadata {
  readonly encryptedBytes: Buffer;
}

export class ArtifactTransportEnvelopeError extends Error {
  constructor() {
    super('artifact_transport_envelope_invalid');
    this.name = 'ArtifactTransportEnvelopeError';
  }
}

export async function writeArtifactTransportEnvelope(
  operationDirectory: string,
  producingRunId: string,
  producingRunAttempt: string,
  encryptedBytes: Buffer,
  encryptedObjectDigest: string,
): Promise<string> {
  const digest = digestBytes(encryptedBytes);
  if (
    !safePositiveDecimal(producingRunId) ||
    !safePositiveDecimal(producingRunAttempt) ||
    !sha256(encryptedObjectDigest) ||
    digest !== encryptedObjectDigest ||
    encryptedBytes.length < 1 ||
    encryptedBytes.length > ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  const document = {
    discriminator: ARTIFACT_ENVELOPE_DISCRIMINATOR,
    producing_run_id: producingRunId,
    producing_run_attempt: producingRunAttempt,
    encrypted_object_digest: encryptedObjectDigest,
    encrypted_object_size: String(encryptedBytes.length),
    encrypted_object_base64: encryptedBytes.toString('base64'),
  };
  const encoded = Buffer.from(JSON.stringify(document), 'utf8');
  if (encoded.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes) {
    throw new ArtifactTransportEnvelopeError();
  }
  const destination = path.join(operationDirectory, ARTIFACT_ENVELOPE_ENTRY);
  await writeFile(destination, encoded, { flag: 'wx', mode: 0o600 });
  return destination;
}

export async function readArtifactArchive(
  archivePath: string,
  expectedArchiveDigest: string,
): Promise<DecodedArtifactTransportEnvelope> {
  if (!sha256(expectedArchiveDigest)) {
    throw new ArtifactTransportEnvelopeError();
  }
  const handle = await open(archivePath, constants.O_RDONLY | (constants.O_NOFOLLOW ?? 0)).catch(
    () => {
      throw new ArtifactTransportEnvelopeError();
    },
  );
  let archive: Buffer;
  try {
    const before = await handle.stat();
    if (
      !before.isFile() ||
      before.size < 1 ||
      before.size > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes
    ) {
      throw new ArtifactTransportEnvelopeError();
    }
    archive = Buffer.allocUnsafe(before.size);
    let offset = 0;
    while (offset < archive.length) {
      const result = await handle.read(archive, offset, archive.length - offset, offset);
      if (result.bytesRead === 0) throw new ArtifactTransportEnvelopeError();
      offset += result.bytesRead;
    }
    const after = await handle.stat();
    if (
      after.size !== before.size ||
      after.mtimeMs !== before.mtimeMs ||
      after.ino !== before.ino
    ) {
      throw new ArtifactTransportEnvelopeError();
    }
  } finally {
    await handle.close();
  }
  if (digestBytes(archive) !== expectedArchiveDigest) {
    throw new ArtifactTransportEnvelopeError();
  }
  const envelopeBytes = await extractOneBoundedEntry(archive);
  return decodeEnvelope(envelopeBytes);
}

export function digestBytes(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

async function extractOneBoundedEntry(archive: Buffer): Promise<Buffer> {
  const eocd = findEndOfCentralDirectory(archive);
  const diskEntries = archive.readUInt16LE(eocd + 8);
  const entries = archive.readUInt16LE(eocd + 10);
  const centralSize = archive.readUInt32LE(eocd + 12);
  const centralOffset = archive.readUInt32LE(eocd + 16);
  if (
    diskEntries !== 1 ||
    entries !== 1 ||
    centralOffset + centralSize !== eocd ||
    centralOffset + 46 > archive.length ||
    archive.readUInt32LE(centralOffset) !== 0x02014b50
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  const flags = archive.readUInt16LE(centralOffset + 8);
  const method = archive.readUInt16LE(centralOffset + 10);
  const expectedCrc = archive.readUInt32LE(centralOffset + 16);
  const compressedSize = archive.readUInt32LE(centralOffset + 20);
  const uncompressedSize = archive.readUInt32LE(centralOffset + 24);
  const nameLength = archive.readUInt16LE(centralOffset + 28);
  const extraLength = archive.readUInt16LE(centralOffset + 30);
  const commentLength = archive.readUInt16LE(centralOffset + 32);
  const diskStart = archive.readUInt16LE(centralOffset + 34);
  const externalAttributes = archive.readUInt32LE(centralOffset + 38);
  const localOffset = archive.readUInt32LE(centralOffset + 42);
  const centralEnd = centralOffset + 46 + nameLength + extraLength + commentLength;
  if (
    centralEnd !== eocd ||
    ![0, 0x8, 0x800, 0x808].includes(flags) ||
    (method !== 0 && method !== 8) ||
    uncompressedSize > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes ||
    compressedSize > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes ||
    localOffset !== 0 ||
    localOffset + 30 > centralOffset ||
    extraLength !== 0 ||
    commentLength !== 0 ||
    diskStart !== 0
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  const name = decodeZipName(
    archive.subarray(centralOffset + 46, centralOffset + 46 + nameLength),
    flags,
  );
  const unixMode = externalAttributes >>> 16;
  const fileType = unixMode & 0o170000;
  if (
    name !== ARTIFACT_ENVELOPE_ENTRY ||
    name.includes('/') ||
    name.includes('\\') ||
    (fileType !== 0 && fileType !== 0o100000) ||
    (unixMode & 0o111) !== 0
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  if (archive.readUInt32LE(localOffset) !== 0x04034b50) {
    throw new ArtifactTransportEnvelopeError();
  }
  const localFlags = archive.readUInt16LE(localOffset + 6);
  const localMethod = archive.readUInt16LE(localOffset + 8);
  const localCrc = archive.readUInt32LE(localOffset + 14);
  const localCompressedSize = archive.readUInt32LE(localOffset + 18);
  const localUncompressedSize = archive.readUInt32LE(localOffset + 22);
  const localNameLength = archive.readUInt16LE(localOffset + 26);
  const localExtraLength = archive.readUInt16LE(localOffset + 28);
  const localNameStart = localOffset + 30;
  const dataStart = localNameStart + localNameLength + localExtraLength;
  const dataEnd = dataStart + compressedSize;
  if (
    localFlags !== flags ||
    localMethod !== method ||
    localExtraLength !== 0 ||
    dataEnd > centralOffset ||
    decodeZipName(
      archive.subarray(localNameStart, localNameStart + localNameLength),
      localFlags,
    ) !== name
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  const descriptor = archive.subarray(dataEnd, centralOffset);
  if ((flags & 0x8) !== 0) {
    const hasSignature = descriptor.length === 16 && descriptor.readUInt32LE(0) === 0x08074b50;
    const valueOffset = hasSignature ? 4 : 0;
    if (
      (!hasSignature && descriptor.length !== 12) ||
      descriptor.readUInt32LE(valueOffset) !== expectedCrc ||
      descriptor.readUInt32LE(valueOffset + 4) !== compressedSize ||
      descriptor.readUInt32LE(valueOffset + 8) !== uncompressedSize
    ) {
      throw new ArtifactTransportEnvelopeError();
    }
  } else if (
    descriptor.length !== 0 ||
    localCrc !== expectedCrc ||
    localCompressedSize !== compressedSize ||
    localUncompressedSize !== uncompressedSize
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  const compressed = archive.subarray(dataStart, dataEnd);
  const output =
    method === 0
      ? Buffer.from(compressed)
      : await inflateBounded(compressed, ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes);
  if (
    output.length !== uncompressedSize ||
    crc32(output) !== expectedCrc ||
    output.length > ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  return output;
}

function decodeEnvelope(bytes: Buffer): DecodedArtifactTransportEnvelope {
  let parsed: unknown;
  try {
    parsed = strictParseArtifactBridgeJson(new TextDecoder('utf-8', { fatal: true }).decode(bytes));
  } catch {
    throw new ArtifactTransportEnvelopeError();
  }
  if (!isExactEnvelope(parsed)) throw new ArtifactTransportEnvelopeError();
  const size = Number(parsed.encrypted_object_size);
  const encoded = parsed.encrypted_object_base64;
  if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/u.test(encoded)) {
    throw new ArtifactTransportEnvelopeError();
  }
  const encryptedBytes = Buffer.from(encoded, 'base64');
  if (
    encryptedBytes.length !== size ||
    encryptedBytes.length < 1 ||
    encryptedBytes.length > ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes ||
    encryptedBytes.toString('base64') !== encoded ||
    digestBytes(encryptedBytes) !== parsed.encrypted_object_digest
  ) {
    throw new ArtifactTransportEnvelopeError();
  }
  return {
    producingRunId: parsed.producing_run_id,
    producingRunAttempt: parsed.producing_run_attempt,
    encryptedObjectDigest: parsed.encrypted_object_digest,
    encryptedObjectSize: size,
    encryptedBytes,
  };
}

function isExactEnvelope(value: unknown): value is {
  discriminator: string;
  producing_run_id: string;
  producing_run_attempt: string;
  encrypted_object_digest: string;
  encrypted_object_size: string;
  encrypted_object_base64: string;
} {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false;
  }
  const record = value as Record<string, unknown>;
  const keys = [
    'discriminator',
    'producing_run_id',
    'producing_run_attempt',
    'encrypted_object_digest',
    'encrypted_object_size',
    'encrypted_object_base64',
  ];
  return (
    Object.keys(record).length === keys.length &&
    keys.every((key) => key in record) &&
    record.discriminator === ARTIFACT_ENVELOPE_DISCRIMINATOR &&
    safePositiveDecimal(record.producing_run_id) !== undefined &&
    safePositiveDecimal(record.producing_run_attempt) !== undefined &&
    sha256(record.encrypted_object_digest) !== undefined &&
    safePositiveDecimal(record.encrypted_object_size) !== undefined &&
    Number(record.encrypted_object_size) <= ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes &&
    typeof record.encrypted_object_base64 === 'string'
  );
}

function findEndOfCentralDirectory(archive: Buffer): number {
  const minimum = Math.max(0, archive.length - 65_557);
  for (let offset = archive.length - 22; offset >= minimum; offset -= 1) {
    if (
      archive.readUInt32LE(offset) === 0x06054b50 &&
      offset + 22 === archive.length &&
      archive.readUInt16LE(offset + 20) === 0 &&
      archive.readUInt16LE(offset + 4) === 0 &&
      archive.readUInt16LE(offset + 6) === 0
    ) {
      return offset;
    }
  }
  throw new ArtifactTransportEnvelopeError();
}

function decodeZipName(bytes: Buffer, flags: number): string {
  try {
    if ((flags & 0x800) !== 0) {
      return new TextDecoder('utf-8', { fatal: true }).decode(bytes);
    }
    if (bytes.some((byte) => byte > 0x7f)) {
      throw new ArtifactTransportEnvelopeError();
    }
    return bytes.toString('ascii');
  } catch {
    throw new ArtifactTransportEnvelopeError();
  }
}

async function inflateBounded(compressed: Buffer, maximum: number): Promise<Buffer> {
  return await new Promise<Buffer>((resolve, reject) => {
    const inflater = createInflateRaw();
    const chunks: Buffer[] = [];
    let total = 0;
    inflater.on('data', (chunk: Buffer) => {
      total += chunk.length;
      if (total > maximum) {
        inflater.destroy(new ArtifactTransportEnvelopeError());
        return;
      }
      chunks.push(chunk);
    });
    inflater.once('error', () => reject(new ArtifactTransportEnvelopeError()));
    inflater.once('end', () => resolve(Buffer.concat(chunks, total)));
    inflater.end(compressed);
  });
}

const crcTable = (() => {
  const table = new Uint32Array(256);
  for (let index = 0; index < table.length; index += 1) {
    let value = index;
    for (let bit = 0; bit < 8; bit += 1) {
      value = (value & 1) !== 0 ? 0xedb88320 ^ (value >>> 1) : value >>> 1;
    }
    table[index] = value >>> 0;
  }
  return table;
})();

function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff;
  for (const byte of bytes) {
    crc = crcTable[(crc ^ byte) & 0xff]! ^ (crc >>> 8);
  }
  return (crc ^ 0xffffffff) >>> 0;
}
