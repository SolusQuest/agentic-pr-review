import { mkdirSync, readFileSync, rmSync, statSync, symlinkSync, writeFileSync } from 'node:fs';
import { delimiter, join } from 'node:path';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { githubReader, verifyStorage } from '../../scripts/release/candidate.mjs';
import {
  CHECK_MANIFEST_PATH,
  PAIR_LIMITS,
  inspectQualificationContent,
  inspectQualificationArchive,
  parseExpectedChecks,
} from '../../scripts/release/finalized-pair.format.mjs';
import {
  finalizeCandidate,
  main,
  verifyQualifiedPair,
} from '../../scripts/release/finalize-candidate.mjs';
import { finalizationFixture } from './finalization.fixtures.mjs';
import { createTestZip } from '../../src/action-wrapper/artifact-bridge/zip-test-helper.js';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
});

describe('bounded qualification transport ZIP', () => {
  const entries = () => [
    { name: 'receipt.json', data: Buffer.from('{}') },
    { name: 'case-results.json', data: Buffer.from('{}') },
    { name: 'evidence/restored-review.json', data: Buffer.from('{}') },
  ];
  test.each([
    'duplicate',
    'traversal',
    'symlink',
    'executable',
    'oversized-member',
    'crc',
    'local-name',
    'offset',
    'local-flags',
    'extra-field',
    'method',
    'multi-disk',
    'zip64',
    'trailing',
    'truncated',
    'oversized-archive',
  ])('rejects malformed or outside-profile ZIP despite recomputed hash: %s', (kind) => {
    const files = entries();
    if (kind === 'duplicate') files.push(files[0]!);
    if (kind === 'traversal') files[2]!.name = 'evidence/../receipt.json';
    if (kind === 'symlink') (files[2] as any).mode = 0o120644;
    if (kind === 'executable') (files[2] as any).mode = 0o100755;
    if (kind === 'oversized-member') files[2]!.data = Buffer.alloc(PAIR_LIMITS.evidence + 1);
    let bytes = createTestZip(files);
    const end = bytes.length - 22,
      central = bytes.readUInt32LE(end + 16);
    if (kind === 'crc') {
      bytes.writeUInt32LE(123, 14);
      bytes.writeUInt32LE(123, central + 16);
    }
    if (kind === 'local-name') bytes[30] = 120;
    if (kind === 'offset') bytes.writeUInt32LE(1, central + 42);
    if (kind === 'local-flags') bytes.writeUInt16LE(1, 6);
    if (kind === 'extra-field') bytes.writeUInt16LE(1, central + 30);
    if (kind === 'method') bytes.writeUInt16LE(9, central + 10);
    if (kind === 'multi-disk') bytes.writeUInt16LE(1, end + 4);
    if (kind === 'zip64') {
      bytes.writeUInt16LE(0xffff, end + 8);
      bytes.writeUInt16LE(0xffff, end + 10);
    }
    if (kind === 'trailing') bytes = Buffer.concat([bytes, Buffer.from([0])]);
    if (kind === 'truncated') bytes = bytes.subarray(0, -1);
    if (kind === 'oversized-archive') bytes = Buffer.alloc(PAIR_LIMITS.qualificationArtifact + 1);
    expect(() => inspectQualificationArchive(bytes, sha256(bytes))).toThrow();
  });
});
async function fixture() {
  const f = finalizationFixture();
  roots.push(f.root);
  vi.stubGlobal('fetch', f.fetch);
  vi.stubEnv('PATH', f.bin + delimiter + process.env.PATH);
  vi.spyOn(Date, 'now').mockReturnValue(f.now);
  const out = await finalizeCandidate(f.input, {}),
    pairPath = join(f.input.output, 'finalized-pair.json'),
    pairBytes = readFileSync(pairPath);
  // Models a positive protocol execution under FAKE service identities only.
  // This cannot be accepted by the real GitHub API and is not qualification.
  const q = f.qualificationFiles(pairBytes, { executionKind: 'original-candidate' });
  const input = {
    ...f.input,
    pairPath,
    pairSha256: out.pairSha256,
    qualifierCommit: f.qualifier,
    qualificationArchivePath: q.archivePath,
    receiptSha256: q.receiptSha256,
    qualificationId: f.qualification.artifactId,
    qualificationSha256: f.qualification.sha256,
  };
  function inspect() {
    const saved = q.save();
    return inspectQualificationContent({
      pairBytes,
      pairSha256: out.pairSha256,
      receiptBytes: saved.receiptBytes,
      receiptSha256: saved.receiptSha256,
      expectedChecksBytes: f.expectedBytes,
      resultBytes: saved.resultBytes,
      evidence: q.evidence,
    });
  }
  function inputAfterSelfHash() {
    return {
      ...input,
      receiptSha256: q.save().receiptSha256,
      qualificationSha256: f.qualification.sha256,
    };
  }
  return { f, q, input, pairBytes, inspect, inputAfterSelfHash };
}

describe('P3 frozen V2/P4 receipt interchange, never an operational fixture proof', () => {
  test('rejects substituted consistent PASS files when the authentic artifact contains refusal', async () => {
    const x = await fixture();
    const results = structuredClone(x.q.results);
    const restored = results.checks.find((c: any) => c.id === 'restored-review');
    restored.status = 'not-run';
    restored.evidenceSha256 = null;
    const resultBytes = Buffer.from(canonicalJson(results));
    const receiptBytes = Buffer.from(
      canonicalJson({
        ...x.q.receipt,
        result: 'refusal',
        refusalReason: 'not-run',
        caseResultsSha256: sha256(resultBytes),
      }),
    );
    const archive = createTestZip([
      { name: 'receipt.json', data: receiptBytes },
      { name: 'case-results.json', data: resultBytes },
      ...[...x.q.evidence]
        .filter(([id]) => id !== 'restored-review')
        .map(([id, data]) => ({ name: 'evidence/' + id + '.json', data })),
    ]);
    const qualificationArchivePath = join(x.f.root, 'original-qualification.zip');
    writeFileSync(qualificationArchivePath, archive);
    x.f.qArtifact.digest = 'sha256:' + sha256(archive);
    x.f.qArtifact.size_in_bytes = archive.length;
    // The separately consistent PASS directory and its self-hashes remain unchanged.
    await expect(
      verifyQualifiedPair(
        {
          ...x.input,
          qualificationArchivePath,
          qualificationSha256: sha256(archive),
        },
        {},
      ),
    ).rejects.toThrow();
    // A forged, internally consistent PASS ZIP also cannot borrow the refusal
    // artifact's authenticated transport hash or select a different local hash.
    await expect(
      verifyQualifiedPair(
        {
          ...x.input,
          qualificationSha256: sha256(archive),
        },
        {},
      ),
    ).rejects.toThrow('finalization_digest_mismatch');
    await expect(verifyQualifiedPair(x.input, {})).rejects.toThrow(
      'candidate_storage_identity_drift',
    );
    await expect(
      verifyQualifiedPair(
        {
          ...x.input,
          qualificationArchivePath,
          qualificationSha256: sha256(archive),
          receiptSha256: sha256(receiptBytes),
        },
        {},
      ),
    ).rejects.toThrow('qualification_refused');
  });
  test.each([0, 6])(
    'accepts actual locked Actions SDK ZIP bytes (compression %s)',
    async (level) => {
      const x = await fixture();
      const moduleUrl = new URL(
        '../../node_modules/@actions/artifact/lib/internal/upload/zip.js',
        import.meta.url,
      );
      const { createZipUploadStream } = await import(/* @vite-ignore */ moduleUrl.href);
      const stream = await createZipUploadStream(
        [
          ...x.q.entries().map((e: any) => ({
            sourcePath: join(x.q.directory, e.name),
            destinationPath: e.name,
            stats: statSync(join(x.q.directory, e.name)),
          })),
          { sourcePath: null, destinationPath: 'evidence/' },
        ],
        level,
      );
      const chunks = [];
      for await (const chunk of stream) chunks.push(chunk);
      const archive = Buffer.concat(chunks);
      x.q.pack(archive);
      await expect(
        verifyQualifiedPair({ ...x.input, qualificationSha256: sha256(archive) }, {}),
      ).resolves.toMatchObject({ qualified: true });
      if (level === 6) {
        const broken = Buffer.from(archive);
        const central = broken.readUInt32LE(broken.length - 6);
        const offset = broken.readUInt32LE(central + 42);
        const dataEnd =
          offset + 30 + broken.readUInt16LE(offset + 26) + broken.readUInt32LE(central + 20);
        broken[dataEnd + 4] ^= 1; // authenticated bytes but inconsistent descriptor CRC
        expect(() => inspectQualificationArchive(broken, sha256(broken))).toThrow();
      }
    },
  );
  test('binds exact concrete checks, retained evidence and approved Q/run/artifact; content alone never qualifies', async () => {
    const x = await fixture();
    expect(
      x.q.results.checks
        .filter((c: any) => c.group === 'fresh-restored-review')
        .map((c: any) => c.id),
    ).toEqual(['fresh-review', 'restored-review']);
    expect(x.inspect()).toMatchObject({
      contentVerified: true,
      operationallyQualified: false,
      receipt: { result: 'pass' },
    });
    await expect(verifyQualifiedPair(x.input, {})).resolves.toMatchObject({
      qualified: true,
      pairSha256: x.input.pairSha256,
      receiptSha256: x.input.receiptSha256,
      qualifierCommit: x.f.qualifier,
      checkCount: 12,
    });
    expect(x.f.calls.every((call: any) => call.method === 'GET')).toBe(true);
    expect(readFileSync(x.input.pairPath).equals(x.pairBytes)).toBe(true); // no pair rewrite/cycle
  });
  test('operational CLI requires the original ZIP and rejects the extracted-directory option', async () => {
    const x = await fixture();
    const args = [
      'verify-qualification',
      '--repo',
      x.f.repo,
      '--source',
      x.f.source,
      '--version',
      x.f.input.releaseVersion,
      '--request',
      x.f.requestPath,
      '--archive',
      x.f.paths.archivePath,
      '--candidate',
      x.f.paths.candidatePath,
      '--map',
      x.f.paths.mapPath,
      '--bundle',
      x.f.paths.bundlePath,
      '--pair',
      x.input.pairPath,
      '--sha256',
      x.input.pairSha256,
      '--qualifier',
      x.input.qualifierCommit,
      '--receipt-sha256',
      x.input.receiptSha256,
      '--qualification-id',
      x.input.qualificationId,
      '--qualification-sha256',
      x.input.qualificationSha256,
      '--qualification-archive',
      x.input.qualificationArchivePath,
    ];
    await expect(main(args, {})).resolves.toMatchObject({ qualified: true });
    await expect(
      main(
        args.map((arg) => (arg === '--qualification-archive' ? '--qualification-dir' : arg)),
        {},
      ),
    ).rejects.toThrow('finalization_arguments_invalid');
  });
  test('requires authenticated transport size as well as digest', async () => {
    const x = await fixture();
    x.f.qArtifact.size_in_bytes++;
    await expect(verifyQualifiedPair(x.input, {})).rejects.toThrow(
      'qualification_archive_size_drift',
    );
  });
  test.each([
    'omitted-restored',
    'duplicate',
    'unknown',
    'wrong-group',
    'not-run',
    'failed',
    'evidence-missing',
    'evidence-digest',
    'evidence-pair',
    'evidence-outcome',
    'expected-manifest',
    'count',
    'unknown-field',
  ])('cannot hide concrete-case incompleteness with recomputed self hashes: %s', async (kind) => {
    const x = await fixture(),
      q = x.q;
    const restored = q.results.checks.find((c: any) => c.id === 'restored-review');
    const index = q.results.checks.indexOf(restored);
    if (kind === 'omitted-restored') q.results.checks.splice(index, 1);
    if (kind === 'duplicate')
      q.results.checks[index] = { ...q.results.checks.find((c: any) => c.id === 'fresh-review') };
    if (kind === 'unknown') restored.id = 'unknown-check';
    if (kind === 'wrong-group') restored.group = 'original-archive';
    if (kind === 'not-run') {
      restored.status = 'not-run';
      restored.evidenceSha256 = null;
      q.evidence.delete('restored-review');
    }
    if (kind === 'failed') restored.status = 'refused';
    if (kind === 'evidence-missing') q.evidence.delete('restored-review');
    if (kind === 'evidence-digest') restored.evidenceSha256 = 'f'.repeat(64);
    if (kind === 'evidence-pair' || kind === 'evidence-outcome') {
      const record = JSON.parse(q.evidence.get('restored-review')!.toString());
      if (kind === 'evidence-pair') record.pairSha256 = 'f'.repeat(64);
      else record.outcome = 'refused';
      const bytes = Buffer.from(canonicalJson(record));
      q.evidence.set('restored-review', bytes);
      restored.evidenceSha256 = sha256(bytes);
    }
    if (kind === 'expected-manifest') {
      q.receipt.expectedChecksSha256 = 'f'.repeat(64);
      q.results.expectedChecksSha256 = q.receipt.expectedChecksSha256;
    }
    if (kind === 'count') q.receipt.checkCount--;
    if (kind === 'unknown-field') (q.receipt as any).PRIVATE_SOURCE_CANARY = 'not allowed';
    expect(x.inspect).toThrow();
  });
  test.each([
    ['sourceCommit', 40],
    ['sourceTree', 40],
    ['workflowCommit', 40],
    ['actionCommit', 40],
    ['archiveSha256', 64],
    ['candidateSha256', 64],
    ['mapSha256', 64],
    ['inventorySha256', 64],
    ['buildInputsSha256', 64],
    ['provenanceBundleSha256', 64],
    ['assetsSha256', 64],
    ['runtimePolicySha256', 64],
  ])('rejects a receipt for another exact pair identity: %s', async (key, length) => {
    const x = await fixture();
    (x.q.receipt.bindings as any)[key] = 'f'.repeat(length as number);
    expect(x.inspect).toThrow('qualification_receipt_identity_drift');
  });
  test('represents refusal/incomplete cases explicitly and never promotes them', async () => {
    const x = await fixture(),
      q = x.q,
      c = q.results.checks.find((c: any) => c.id === 'restored-review');
    c.status = 'not-run';
    c.evidenceSha256 = null;
    q.evidence.delete(c.id);
    rmSync(join(q.directory, 'evidence', c.id + '.json'));
    q.receipt.result = 'refusal';
    q.receipt.refusalReason = 'not-run';
    expect(x.inspect()).toMatchObject({
      operationallyQualified: false,
      receipt: { result: 'refusal' },
    });
    await expect(verifyQualifiedPair(x.inputAfterSelfHash(), {})).rejects.toThrow(
      'qualification_refused',
    );
  });
  test.each([
    'synthetic',
    'pr',
    'push',
    'wrong-Q',
    'wrong-ref',
    'wrong-workflow',
    'failed-run',
    'in-progress',
    'expired',
    'missing',
    'transport-drift',
    'wrong-artifact-producer',
  ])('requires external operational execution/storage authority: %s', async (kind) => {
    const x = await fixture();
    let input = x.input;
    if (kind === 'synthetic') {
      x.q.receipt.executionKind = 'synthetic';
      input = x.inputAfterSelfHash();
      expect(x.inspect().operationallyQualified).toBe(false);
    }
    if (kind === 'pr') x.f.qRun.event = 'pull_request';
    if (kind === 'push') x.f.qRun.event = 'push';
    if (kind === 'wrong-Q') {
      x.q.receipt.producer.workflowCommit = x.f.action;
      input = x.inputAfterSelfHash();
    }
    if (kind === 'wrong-ref') x.f.qRun.head_branch = 'Main';
    if (kind === 'wrong-workflow') x.f.qRun.path = '.github/workflows/other.yml';
    if (kind === 'failed-run') x.f.qRun.conclusion = 'failure';
    if (kind === 'in-progress') {
      x.f.qRun.status = 'in_progress';
      (x.f.qRun as any).conclusion = null;
    }
    if (kind === 'expired') x.f.qArtifact.expired = true;
    if (kind === 'missing') x.f.missing.add('/actions/artifacts/401');
    if (kind === 'transport-drift') input = { ...input, qualificationSha256: 'f'.repeat(64) };
    if (kind === 'wrong-artifact-producer') x.f.qArtifact.workflow_run.id = '302';
    await expect(verifyQualifiedPair(input, {})).rejects.toThrow();
  });
  test('does not let a synthetic=false label or self-selected expected manifest replace approved Q', async () => {
    const x = await fixture();
    x.f.qRun.event = 'pull_request';
    // Every concrete result is pass-shaped, but its execution origin is wrong.
    expect(x.inspect()).toMatchObject({
      receipt: { executionKind: 'original-candidate', result: 'pass' },
    });
    await expect(verifyQualifiedPair(x.input, {})).rejects.toThrow('candidate_run_identity_drift');
    x.f.qRun.event = 'workflow_dispatch';
    x.f.write(
      CHECK_MANIFEST_PATH,
      canonicalJson({
        ...x.f.expected,
        checks: x.f.expected.checks.filter((c: any) => c.id !== 'restored-review'),
      }),
    );
    x.q.results.checks = x.q.results.checks.filter((c: any) => c.id !== 'restored-review');
    x.q.receipt.checkCount--;
    x.q.evidence.delete('restored-review');
    await expect(verifyQualifiedPair(x.inputAfterSelfHash(), {})).rejects.toThrow(); // exact Q Git blob still expects restored
    await expect(
      verifyQualifiedPair({ ...x.input, qualifierCommit: x.f.workflow }, {}),
    ).rejects.toThrow(); // no Q-owned manifest
  });
  test.each([
    'missing-evidence',
    'extra-file',
    'evidence-symlink',
    'directory-symlink',
    'oversized',
    'renamed-file',
  ])('rejects missing/unsafe qualification artifact members: %s', async (kind) => {
    const x = await fixture(),
      target = join(x.q.directory, 'evidence', 'restored-review.json');
    if (kind === 'missing-evidence') rmSync(target);
    if (kind === 'extra-file') writeFileSync(join(x.q.directory, 'raw-log.txt'), 'PRIVATE_CANARY');
    if (kind === 'evidence-symlink') {
      const outside = join(x.f.root, 'outside');
      writeFileSync(outside, x.q.evidence.get('restored-review')!);
      rmSync(target);
      symlinkSync(outside, target);
    }
    if (kind === 'directory-symlink') {
      const outside = join(x.f.root, 'outside-evidence');
      mkdirSync(outside);
      rmSync(join(x.q.directory, 'evidence'), { recursive: true });
      symlinkSync(outside, join(x.q.directory, 'evidence'));
    }
    if (kind === 'oversized') writeFileSync(target, Buffer.alloc(PAIR_LIMITS.evidence + 1));
    if (kind === 'renamed-file')
      writeFileSync(join(x.q.directory, 'evidence', 'Restored-review.json'), '{}');
    x.q.pack();
    await expect(
      verifyQualifiedPair({ ...x.input, qualificationSha256: x.f.qualification.sha256 }, {}),
    ).rejects.toThrow();
  });
  test('qualification storage cannot reuse preparation in-progress authority', async () => {
    const x = await fixture();
    x.f.qRun.status = 'in_progress';
    (x.f.qRun as any).conclusion = null;
    await expect(
      verifyStorage(x.f.qProducer, x.f.qualification, githubReader(''), {
        role: 'qualification',
        currentProducer: x.f.qProducer,
      }),
    ).rejects.toThrow('qualification_in_progress_denied');
  });
  test('validates filename-safe canonical expected checks, all required groups and size limits', async () => {
    const x = await fixture();
    for (const id of ['../outside', 'Fresh', 'a/b', '.', 'é', '']) {
      const manifest = structuredClone(x.f.expected);
      manifest.checks[0].id = id;
      expect(() => parseExpectedChecks(Buffer.from(canonicalJson(manifest)))).toThrow();
    }
    const missingGroup = structuredClone(x.f.expected);
    missingGroup.checks = missingGroup.checks.filter(
      (c: any) => c.group !== 'external-confinement',
    );
    expect(() => parseExpectedChecks(Buffer.from(canonicalJson(missingGroup)))).toThrow();
    expect(() => parseExpectedChecks(Buffer.alloc(PAIR_LIMITS.expectedChecks + 1))).toThrow();
    expect(() =>
      inspectQualificationContent({
        pairBytes: x.pairBytes,
        pairSha256: x.input.pairSha256,
        receiptBytes: Buffer.alloc(PAIR_LIMITS.receipt + 1),
        receiptSha256: 'f'.repeat(64),
        expectedChecksBytes: x.f.expectedBytes,
        resultBytes: x.q.resultBytes,
        evidence: x.q.evidence,
      }),
    ).toThrow();
  });
});
