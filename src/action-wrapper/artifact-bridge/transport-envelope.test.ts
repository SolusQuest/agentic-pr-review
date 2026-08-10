import { deflateRawSync } from 'node:zlib';
import { mkdtemp, readFile, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';

import {
  ArtifactTransportEnvelopeError,
  digestBytes,
  readArtifactArchive,
  writeArtifactTransportEnvelope,
} from './transport-envelope.js';
import { ARTIFACT_BRIDGE_LIMITS, ARTIFACT_ENVELOPE_ENTRY } from './limits.js';

const roots: string[] = [];

afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true })));
});

describe('private artifact transport envelope', () => {
  it('round-trips one fixed envelope through a bounded raw ZIP', async () => {
    const root = await temporaryRoot();
    const encrypted = Buffer.from([0, 7, 255, 0, 9]);
    const envelopePath = await writeArtifactTransportEnvelope(
      root,
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
    );
    const archive = zip([
      {
        name: ARTIFACT_ENVELOPE_ENTRY,
        data: await readFile(envelopePath),
      },
    ]);
    const archivePath = path.join(root, 'artifact.zip');
    await writeFile(archivePath, archive);

    await expect(readArtifactArchive(archivePath, digestBytes(archive))).resolves.toEqual({
      producingRunId: '7001',
      producingRunAttempt: '2',
      encryptedObjectDigest: digestBytes(encrypted),
      encryptedObjectSize: encrypted.length,
      encryptedBytes: encrypted,
    });
    expect(digestBytes(archive)).not.toBe(digestBytes(encrypted));
  });

  it('accepts the streamed ASCII ZIP shape emitted by the official upload binding', async () => {
    const root = await temporaryRoot();
    const encrypted = Buffer.from('official-shape');
    const envelopePath = await writeArtifactTransportEnvelope(
      root,
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
    );
    const archive = zip([
      {
        name: ARTIFACT_ENVELOPE_ENTRY,
        data: await readFile(envelopePath),
        streamed: true,
        utf8: false,
      },
    ]);
    const archivePath = path.join(root, 'official-shape.zip');
    await writeFile(archivePath, archive);

    await expect(readArtifactArchive(archivePath, digestBytes(archive))).resolves.toMatchObject({
      producingRunId: '7001',
      producingRunAttempt: '2',
      encryptedObjectDigest: digestBytes(encrypted),
      encryptedBytes: encrypted,
    });
  });

  it('accepts the 2 MiB encrypted-object boundary and rejects cap plus one', async () => {
    const root = await temporaryRoot();
    const atCap = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes, 0xa5);
    await expect(
      writeArtifactTransportEnvelope(root, '1', '1', atCap, digestBytes(atCap)),
    ).resolves.toContain(ARTIFACT_ENVELOPE_ENTRY);
    const aboveCap = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes + 1);
    await expect(
      writeArtifactTransportEnvelope(root, '1', '1', aboveCap, digestBytes(aboveCap)),
    ).rejects.toBeInstanceOf(ArtifactTransportEnvelopeError);
  });

  it.each([
    [
      'multiple entries',
      [
        { name: ARTIFACT_ENVELOPE_ENTRY, data: Buffer.from('{}') },
        { name: 'extra', data: Buffer.from('x') },
      ],
    ],
    ['nested entry', [{ name: `nested/${ARTIFACT_ENVELOPE_ENTRY}`, data: Buffer.from('{}') }]],
    ['traversal entry', [{ name: `../${ARTIFACT_ENVELOPE_ENTRY}`, data: Buffer.from('{}') }]],
    [
      'symlink entry',
      [
        {
          name: ARTIFACT_ENVELOPE_ENTRY,
          data: Buffer.from('target'),
          mode: 0o120777,
        },
      ],
    ],
    [
      'executable entry',
      [
        {
          name: ARTIFACT_ENVELOPE_ENTRY,
          data: Buffer.from('{}'),
          mode: 0o100700,
        },
      ],
    ],
    [
      'device entry',
      [
        {
          name: ARTIFACT_ENVELOPE_ENTRY,
          data: Buffer.from('{}'),
          mode: 0o020600,
        },
      ],
    ],
  ] as const)('rejects %s', async (_label, entries) => {
    const root = await temporaryRoot();
    const archive = zip(entries);
    const archivePath = path.join(root, 'invalid.zip');
    await writeFile(archivePath, archive);
    await expect(readArtifactArchive(archivePath, digestBytes(archive))).rejects.toBeInstanceOf(
      ArtifactTransportEnvelopeError,
    );
  });

  it('rejects compressed expansion beyond 4 MiB before accepting output', async () => {
    const root = await temporaryRoot();
    const expanded = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes + 1, 0);
    const archive = zip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: expanded }]);
    expect(archive.length).toBeLessThan(ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes);
    const archivePath = path.join(root, 'expansion.zip');
    await writeFile(archivePath, archive);
    await expect(readArtifactArchive(archivePath, digestBytes(archive))).rejects.toBeInstanceOf(
      ArtifactTransportEnvelopeError,
    );
  });

  it('rejects a raw archive symlink where the platform permits links', async () => {
    const root = await temporaryRoot();
    const outside = await temporaryRoot();
    const encrypted = Buffer.from('symlink-canary');
    const envelopePath = await writeArtifactTransportEnvelope(
      root,
      '1',
      '1',
      encrypted,
      digestBytes(encrypted),
    );
    const archive = zip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: await readFile(envelopePath) }]);
    const outsideArchive = path.join(outside, 'outside.zip');
    await writeFile(outsideArchive, archive);
    const linkedArchive = path.join(root, 'linked.zip');
    try {
      await symlink(outsideArchive, linkedArchive, 'file');
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'EPERM') return;
      throw error;
    }
    await expect(readArtifactArchive(linkedArchive, digestBytes(archive))).rejects.toBeInstanceOf(
      ArtifactTransportEnvelopeError,
    );
  });
});

interface ZipEntry {
  readonly name: string;
  readonly data: Buffer;
  readonly mode?: number;
  readonly streamed?: boolean;
  readonly utf8?: boolean;
}

function zip(entries: readonly ZipEntry[]): Buffer {
  const locals: Buffer[] = [];
  const centrals: Buffer[] = [];
  let offset = 0;
  for (const entry of entries) {
    const name = Buffer.from(entry.name, 'utf8');
    const compressed = deflateRawSync(entry.data);
    const checksum = crc32(entry.data);
    const flags = (entry.utf8 === false ? 0 : 0x800) | (entry.streamed ? 0x8 : 0);
    const local = Buffer.alloc(30);
    local.writeUInt32LE(0x04034b50, 0);
    local.writeUInt16LE(20, 4);
    local.writeUInt16LE(flags, 6);
    local.writeUInt16LE(8, 8);
    local.writeUInt32LE(entry.streamed ? 0 : checksum, 14);
    local.writeUInt32LE(entry.streamed ? 0 : compressed.length, 18);
    local.writeUInt32LE(entry.streamed ? 0 : entry.data.length, 22);
    local.writeUInt16LE(name.length, 26);
    const descriptor = entry.streamed ? Buffer.alloc(16) : Buffer.alloc(0);
    if (entry.streamed) {
      descriptor.writeUInt32LE(0x08074b50, 0);
      descriptor.writeUInt32LE(checksum, 4);
      descriptor.writeUInt32LE(compressed.length, 8);
      descriptor.writeUInt32LE(entry.data.length, 12);
    }
    const localRecord = Buffer.concat([local, name, compressed, descriptor]);
    locals.push(localRecord);

    const central = Buffer.alloc(46);
    central.writeUInt32LE(0x02014b50, 0);
    central.writeUInt16LE((3 << 8) | 20, 4);
    central.writeUInt16LE(20, 6);
    central.writeUInt16LE(flags, 8);
    central.writeUInt16LE(8, 10);
    central.writeUInt32LE(checksum, 16);
    central.writeUInt32LE(compressed.length, 20);
    central.writeUInt32LE(entry.data.length, 24);
    central.writeUInt16LE(name.length, 28);
    central.writeUInt32LE(((entry.mode ?? 0o100600) << 16) >>> 0, 38);
    central.writeUInt32LE(offset, 42);
    centrals.push(Buffer.concat([central, name]));
    offset += localRecord.length;
  }
  const centralDirectory = Buffer.concat(centrals);
  const eocd = Buffer.alloc(22);
  eocd.writeUInt32LE(0x06054b50, 0);
  eocd.writeUInt16LE(entries.length, 8);
  eocd.writeUInt16LE(entries.length, 10);
  eocd.writeUInt32LE(centralDirectory.length, 12);
  eocd.writeUInt32LE(offset, 16);
  return Buffer.concat([...locals, centralDirectory, eocd]);
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

async function temporaryRoot(): Promise<string> {
  const root = await mkdtemp(path.join(os.tmpdir(), 'apr-envelope-test-'));
  roots.push(root);
  return root;
}
