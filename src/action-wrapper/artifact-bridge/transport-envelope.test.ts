import { deflateRawSync } from 'node:zlib';
import { describe, expect, it, vi } from 'vitest';

import {
  ArtifactTransportEnvelopeError,
  digestBytes,
  encodeArtifactTransportEnvelope,
  readArtifactArchive,
} from './transport-envelope.js';
import { ARTIFACT_BRIDGE_LIMITS, ARTIFACT_ENVELOPE_ENTRY } from './limits.js';
import { ArtifactBridgeDeadlineError, ArtifactBridgeOperationBudget } from './operation-budget.js';

describe('private artifact transport envelope', () => {
  it('round-trips one fixed envelope through a bounded raw ZIP', async () => {
    const encrypted = Buffer.from([0, 7, 255, 0, 9]);
    const envelope = encodeArtifactTransportEnvelope(
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
      testBudget(),
    );
    const archive = zip([
      {
        name: ARTIFACT_ENVELOPE_ENTRY,
        data: envelope,
      },
    ]);

    await expect(readArtifactArchive(archive, digestBytes(archive), testBudget())).resolves.toEqual(
      {
        producingRunId: '7001',
        producingRunAttempt: '2',
        encryptedObjectDigest: digestBytes(encrypted),
        encryptedObjectSize: encrypted.length,
        encryptedBytes: encrypted,
      },
    );
    expect(digestBytes(archive)).not.toBe(digestBytes(encrypted));
  });

  it('accepts the streamed ASCII ZIP shape emitted by the official upload binding', async () => {
    const encrypted = Buffer.from('official-shape');
    const envelope = encodeArtifactTransportEnvelope(
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
      testBudget(),
    );
    const archive = zip([
      {
        name: ARTIFACT_ENVELOPE_ENTRY,
        data: envelope,
        streamed: true,
        utf8: false,
      },
    ]);
    await expect(
      readArtifactArchive(archive, digestBytes(archive), testBudget()),
    ).resolves.toMatchObject({
      producingRunId: '7001',
      producingRunAttempt: '2',
      encryptedObjectDigest: digestBytes(encrypted),
      encryptedBytes: encrypted,
    });
  });

  it('accepts the 2 MiB encrypted-object boundary and rejects cap plus one', async () => {
    const atCap = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes, 0xa5);
    const encoded = encodeArtifactTransportEnvelope(
      '1',
      '1',
      atCap,
      digestBytes(atCap),
      testBudget(),
    );
    expect(encoded.length).toBeLessThanOrEqual(ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes);
    const aboveCap = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumEncryptedObjectBytes + 1);
    expect(() =>
      encodeArtifactTransportEnvelope('1', '1', aboveCap, digestBytes(aboveCap), testBudget()),
    ).toThrow(ArtifactTransportEnvelopeError);
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
    const archive = zip(entries);
    await expect(
      readArtifactArchive(archive, digestBytes(archive), testBudget()),
    ).rejects.toBeInstanceOf(ArtifactTransportEnvelopeError);
  });

  it('rejects compressed expansion beyond 4 MiB before accepting output', async () => {
    const expanded = Buffer.alloc(ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes + 1, 0);
    const archive = zip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: expanded }]);
    expect(archive.length).toBeLessThan(ARTIFACT_BRIDGE_LIMITS.maximumStagingFileBytes);
    await expect(
      readArtifactArchive(archive, digestBytes(archive), testBudget()),
    ).rejects.toBeInstanceOf(ArtifactTransportEnvelopeError);
  });

  it('zeroes newly decoded encrypted bytes when post-allocation validation fails', async () => {
    const encrypted = Buffer.from('must-not-survive-invalid-envelope');
    const valid = encodeArtifactTransportEnvelope(
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
      testBudget(),
    );
    const malformed = Buffer.from(
      valid
        .toString('utf8')
        .replace(digestBytes(encrypted), '0'.repeat(digestBytes(encrypted).length)),
      'utf8',
    );
    const archive = zip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: malformed }]);
    const fill = vi.spyOn(Buffer.prototype, 'fill');

    await expect(
      readArtifactArchive(archive, digestBytes(archive), testBudget()),
    ).rejects.toBeInstanceOf(ArtifactTransportEnvelopeError);

    expect(
      fill.mock.contexts.some(
        (context, index) =>
          fill.mock.calls[index]?.[0] === 0 &&
          Buffer.isBuffer(context) &&
          context.equals(Buffer.alloc(encrypted.length)),
      ),
    ).toBe(true);
  });

  it('zeroes an encoded envelope when its post-allocation deadline check fails', () => {
    const encrypted = Buffer.from('must-not-survive-encoded-deadline');
    const expected = encodeArtifactTransportEnvelope(
      '7001',
      '2',
      encrypted,
      digestBytes(encrypted),
      testBudget(),
    );
    let clockReads = 0;
    const expiredAfterEncoding = new ArtifactBridgeOperationBudget(
      new AbortController().signal,
      () => {
        clockReads += 1;
        return clockReads >= 2 ? 120_000 : 0;
      },
      0,
    );
    const fill = vi.spyOn(Buffer.prototype, 'fill');

    expect(() =>
      encodeArtifactTransportEnvelope(
        '7001',
        '2',
        encrypted,
        digestBytes(encrypted),
        expiredAfterEncoding,
      ),
    ).toThrow(ArtifactBridgeDeadlineError);

    expect(
      fill.mock.contexts.some(
        (context, index) =>
          fill.mock.calls[index]?.[0] === 0 &&
          Buffer.isBuffer(context) &&
          context.equals(Buffer.alloc(expected.length)),
      ),
    ).toBe(true);
    expiredAfterEncoding.dispose();
  });

  it('observes the shared operation deadline before archive processing', async () => {
    const controller = new AbortController();
    const budget = new ArtifactBridgeOperationBudget(controller.signal, () => 0, 0);
    controller.abort();
    const archive = zip([{ name: ARTIFACT_ENVELOPE_ENTRY, data: Buffer.from('{}') }]);
    await expect(readArtifactArchive(archive, digestBytes(archive), budget)).rejects.toBeInstanceOf(
      ArtifactBridgeDeadlineError,
    );
    budget.dispose();
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

function testBudget(now: () => number = () => 0): ArtifactBridgeOperationBudget {
  return new ArtifactBridgeOperationBudget(new AbortController().signal, now, now());
}
