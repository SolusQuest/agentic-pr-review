import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, readdir, rm, writeFile } from 'node:fs/promises';
import { readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, test } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  CAPS,
  createCandidate,
  humanSummary,
  inspectCandidate,
  parseCanonical,
  proposedMap,
} from '../../scripts/release/candidate.mjs';
import {
  recordPreparedCandidate,
  writeCandidate,
} from '../../scripts/release/prepare-candidate.mjs';
import { packageFixture, producerFixture, storageFixture } from './fixtures.mjs';

function fixture() {
  const p = packageFixture(),
    producer = producerFixture(),
    storage = storageFixture(producer);
  const expected = { ...p.receipt.identity, archiveSha256: p.receipt.archiveSha256 };
  const record = createCandidate(p.archive, p.receipt, producer, storage, expected);
  return { ...p, producer, storage, expected, record };
}
const inspect = (f: any, candidate: any = f.record.candidate, mapBytes = f.record.mapBytes) => {
  const bytes = Buffer.from(canonicalJson(candidate));
  return inspectCandidate(f.archive, bytes, mapBytes, sha256(bytes));
};
describe('immutable candidate and the shared D2 consumer map projection', () => {
  test('preserves distinct S/W, original B, build inputs, storage digest and external content hashes', () => {
    const f = fixture(),
      c = inspect(f),
      map = parseCanonical(f.record.mapBytes, CAPS.map);
    expect(c.receipt.identity.sourceCommit).not.toBe(c.producer.workflowCommit);
    expect(c.storage.sha256).not.toBe(c.receipt.archiveSha256);
    expect(c.proposedMapSha256).toBe(sha256(f.record.mapBytes));
    expect(c).not.toHaveProperty('candidateSha256');
    expect(c).not.toHaveProperty('actionSourceSha');
    expect(map).toEqual({
      formatVersion: 1,
      repository: 'SolusQuest/agentic-pr-review',
      tag: 'payload-v0.0.0-internal.1',
      builder: {
        repository: c.producer.repository,
        workflowPath: c.producer.workflowPath,
        workflowCommit: c.producer.workflowCommit,
      },
      payload: f.receipt,
    });
    expect(c.buildInputs).toEqual(JSON.parse(f.record.bytes.toString()).buildInputs);
    expect(c.receipt.members).toHaveLength(3);
  });
  test('matches the exact shared #343 synthetic map fixture without activating a consumer', () => {
    // Snapshot from the coordinator-approved #343 map-contract revision 2.
    // All version/source/workflow/member identities are synthetic, non-executable.
    const shared = JSON.parse(readFileSync('tests/release/shared-map.fixture.json', 'utf8'));
    const producer = { ...producerFixture(), workflowCommit: shared.builder.workflowCommit };
    const map = proposedMap(shared.payload, producer);
    expect(map).toEqual(shared);
    expect(sha256(canonicalJson(map))).toBe(
      'e829f6b9b8a7d10d3e563d934214ce8f7e9e8d1c412eee634a145d49eabdbf21',
    );
  });
  test.each(['sourceCommit', 'sourceTree', 'releaseVersion', 'archiveSha256'])(
    'rejects independently expected %s drift before output',
    (field) => {
      const f = fixture();
      const expected = {
        ...f.expected,
        [field]:
          field === 'releaseVersion'
            ? 'v0.0.0-internal.2'
            : field === 'archiveSha256'
              ? 'f'.repeat(64)
              : 'f'.repeat(40),
      };
      expect(() => createCandidate(f.archive, f.receipt, f.producer, f.storage, expected)).toThrow(
        'candidate_input_drift',
      );
    },
  );
  test('rechecks actual original archive bytes, never accepts a replacement with the same receipt', () => {
    const f = fixture();
    const changed = Buffer.from(f.archive);
    changed[20] ^= 1;
    expect(() =>
      inspectCandidate(changed, f.record.bytes, f.record.mapBytes, f.record.candidateSha256),
    ).toThrow('archive_digest_mismatch');
  });
  test.each(['archiveSize', 'archiveSha256'])('detects receipt %s drift', (field) => {
    const f = fixture();
    const c = structuredClone(f.record.candidate);
    c.receipt[field] = field === 'archiveSize' ? c.receipt.archiveSize + 1 : 'f'.repeat(64);
    expect(() => inspect(f, c)).toThrow();
  });
  test.each(['size', 'sha256', 'mode', 'path'])('detects internal inventory %s drift', (field) => {
    const f = fixture();
    const c = structuredClone(f.record.candidate);
    c.receipt.members[0][field] =
      field === 'sha256' ? 'f'.repeat(64) : field === 'path' ? '../evil' : 1;
    expect(() => inspect(f, c)).toThrow();
  });
  test.each(['repository', 'tag', 'builder', 'payload', 'extra'])(
    'rejects proposed map %s drift even with recomputed outer fixture hashes',
    (field) => {
      const f = fixture();
      const map = parseCanonical(f.record.mapBytes, CAPS.map);
      if (field === 'builder') map.builder.workflowCommit = 'f'.repeat(40);
      else if (field === 'payload') map.payload.identity.sourceCommit = 'f'.repeat(40);
      else map[field] = 'DRIFT';
      const mapBytes = Buffer.from(canonicalJson(map)),
        c = structuredClone(f.record.candidate);
      c.proposedMapSha256 = sha256(mapBytes);
      expect(() => inspect(f, c, mapBytes)).toThrow('candidate_map_or_inputs_drift');
    },
  );
  test.each(['buildInputs', 'producer', 'storage', 'root'])(
    'closed %s rejects arbitrary metadata/canaries',
    (target) => {
      const f = fixture(),
        c = structuredClone(f.record.candidate);
      if (target === 'root') c.environment = 'PRIVATE_CANARY';
      else c[target].environment = 'PRIVATE_CANARY';
      expect(() => inspect(f, c)).toThrow();
    },
  );
  test('rejects detached candidate/map digests and unsupported format', () => {
    const f = fixture();
    expect(() =>
      inspectCandidate(f.archive, f.record.bytes, f.record.mapBytes, 'f'.repeat(64)),
    ).toThrow('candidate_digest_mismatch');
    const c = structuredClone(f.record.candidate);
    c.formatVersion = 2;
    expect(() => inspect(f, c)).toThrow('unsupported_candidate');
  });
  test('canonical JSON rejects duplicate/escaped alias keys, whitespace, BOM and malformed UTF-8', () => {
    for (const text of ['{"x":1,"x":2}', '{"x":1,"\\u0078":2}', '{ "x":1}', '\ufeff{"x":1}', '{'])
      expect(() => parseCanonical(Buffer.from(text), CAPS.metadata)).toThrow();
    expect(() => parseCanonical(Buffer.from([0xff]), CAPS.metadata)).toThrow(
      'invalid_metadata_json',
    );
  });
  test('enforces metadata/map bounds before hash/parsing and bounds the public summary', () => {
    const f = fixture();
    expect(() =>
      inspectCandidate(
        f.archive,
        Buffer.alloc(CAPS.metadata + 1),
        f.record.mapBytes,
        f.record.candidateSha256,
      ),
    ).toThrow('metadata_too_large');
    expect(() =>
      inspectCandidate(
        f.archive,
        f.record.bytes,
        Buffer.alloc(CAPS.map + 1),
        f.record.candidateSha256,
      ),
    ).toThrow('metadata_too_large');
    const summary = humanSummary(f.record);
    expect(Buffer.byteLength(summary)).toBeLessThanOrEqual(CAPS.summary);
    expect(summary).not.toMatch(/archivePath|PRIVATE_CANARY|token|environment/);
  });
  test('summary follows validated canonical bytes, immune to caller-object mutation', () => {
    const f = fixture();
    f.producer.workflowCommit = 'PRIVATE_CANARY';
    f.record.candidate.storage.artifactId = 'PRIVATE_CANARY';
    expect(humanSummary(f.record)).not.toContain('PRIVATE_CANARY');
    f.record.bytes[0] = 0;
    expect(() => humanSummary(f.record)).toThrow();
  });
  test('same inputs/new run or attempt create a new candidate without replacing original bytes', () => {
    const f = fixture();
    for (const producer of [
      { ...f.producer, runId: '102' },
      { ...f.producer, runAttempt: '3' },
    ]) {
      const record = createCandidate(
        f.archive,
        f.receipt,
        producer,
        storageFixture(producer),
        f.expected,
      );
      expect(record.candidateSha256).not.toBe(f.record.candidateSha256);
      expect(record.candidate.receipt.archiveSha256).toBe(f.receipt.archiveSha256);
      expect(record.candidate.storage.name).not.toBe(f.storage.name);
    }
  });
  test('writes one immutable bounded record with hash readback, refuses overwrite and partial retry', async () => {
    const f = fixture(),
      parent = await mkdtemp(join(tmpdir(), 'apr-p1-record-')),
      output = join(parent, 'record');
    try {
      await writeCandidate(f.record, output);
      expect((await readdir(output)).sort()).toEqual([
        'candidate.json',
        'candidate.sha256',
        'proposed-action-map.json',
        'proposed-action-map.sha256',
        'summary.txt',
      ]);
      expect((await readFile(join(output, 'candidate.sha256'), 'utf8')).trim()).toBe(
        sha256(await readFile(join(output, 'candidate.json'))),
      );
      await expect(writeCandidate(f.record, output)).rejects.toThrow('output_already_exists');
      expect(
        inspectCandidate(
          f.archive,
          await readFile(join(output, 'candidate.json')),
          await readFile(join(output, 'proposed-action-map.json')),
          f.record.candidateSha256,
        ),
      ).toBeDefined();
    } finally {
      await rm(parent, { recursive: true, force: true });
    }
  });
  test('file composition requires independent raw receipt and archive hashes before visibility', async () => {
    const f = fixture(),
      parent = await mkdtemp(join(tmpdir(), 'apr-p1-files-'));
    const archivePath = join(parent, f.receipt.archiveName),
      receiptPath = join(parent, 'receipt.json');
    const receiptBytes = Buffer.from(canonicalJson(f.receipt) + '\n');
    try {
      await writeFile(archivePath, f.archive);
      await writeFile(receiptPath, receiptBytes);
      const args = {
        archivePath,
        receiptPath,
        producer: f.producer,
        storage: f.storage,
        expected: { ...f.expected, receiptSha256: sha256(receiptBytes) },
        output: join(parent, 'record'),
      };
      await expect(
        recordPreparedCandidate({
          ...args,
          expected: { ...args.expected, receiptSha256: 'f'.repeat(64) },
        }),
      ).rejects.toThrow('receipt_digest_drift');
      await expect(
        recordPreparedCandidate({
          ...args,
          expected: { ...args.expected, archiveSha256: 'f'.repeat(64) },
        }),
      ).rejects.toThrow('candidate_input_drift');
      expect(await readdir(parent)).toHaveLength(2);
      const record = await recordPreparedCandidate(args);
      // Exercise the actual CLI on the retained original bytes, not only exports.
      const result = execFileSync(
        process.execPath,
        [
          'scripts/release/prepare-candidate.mjs',
          'inspect',
          '--archive',
          archivePath,
          '--candidate',
          join(args.output, 'candidate.json'),
          '--map',
          join(args.output, 'proposed-action-map.json'),
          '--sha256',
          record.candidateSha256,
        ],
        { encoding: 'utf8' },
      );
      expect(JSON.parse(result)).toEqual({ contentVerified: true, storageVerified: false });
      const failed = spawnSync(
        process.execPath,
        [
          'scripts/release/prepare-candidate.mjs',
          'inspect',
          '--archive',
          'PRIVATE_PATH_CANARY',
          '--candidate',
          'x',
          '--map',
          'y',
          '--sha256',
          record.candidateSha256,
        ],
        { encoding: 'utf8', env: { ...process.env, PRIVATE_ENV_CANARY: 'PRIVATE_ENV_CANARY' } },
      );
      expect(failed.status).toBe(1);
      expect(failed.stderr).toBe('prepare_candidate_failed\n');
      expect(failed.stdout).toBe('');
    } finally {
      await rm(parent, { recursive: true, force: true });
    }
  });
});
