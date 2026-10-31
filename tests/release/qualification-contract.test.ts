import { mkdirSync, readFileSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { delimiter, join } from 'node:path';
import { afterEach, describe, expect, test, vi } from 'vitest';
import { canonicalJson, sha256 } from '../../scripts/release/build-payload.format.mjs';
import { githubReader, verifyStorage } from '../../scripts/release/candidate.mjs';
import {
  CHECK_MANIFEST_PATH,
  PAIR_LIMITS,
  inspectQualificationContent,
  parseExpectedChecks,
} from '../../scripts/release/finalized-pair.format.mjs';
import {
  finalizeCandidate,
  verifyQualifiedPair,
} from '../../scripts/release/finalize-candidate.mjs';
import { finalizationFixture } from './finalization.fixtures.mjs';
const roots: string[] = [];
afterEach(() => {
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true });
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
    qualificationDirectory: q.directory,
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
    return { ...input, receiptSha256: q.save().receiptSha256 };
  }
  return { f, q, input, pairBytes, inspect, inputAfterSelfHash };
}

describe('P3 frozen V2/P4 receipt interchange, never an operational fixture proof', () => {
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
    await expect(verifyQualifiedPair(x.input, {})).rejects.toThrow();
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
