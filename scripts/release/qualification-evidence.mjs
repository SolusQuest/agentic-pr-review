// Producer for P3's existing interchange. No new receipt/schema interpretation.
import { mkdir, readdir, lstat, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { canonicalJson, readBoundedFile, sha256 } from './build-payload.format.mjs';
import { admitOutput } from './build-payload.mjs';
import { requireThat } from './candidate.mjs';
import {
  inspectQualificationContent,
  parseExpectedChecks,
  parseFinalizedPair,
  qualificationBindings,
  QUALIFICATION_SCOPE,
  PAIR_LIMITS,
} from './finalized-pair.format.mjs';

export function qualificationEvidence({
  pairBytes,
  pairSha256,
  expectedChecksBytes,
  producer,
  executionKind,
  observations,
  refusalReason = null,
}) {
  const pair = parseFinalizedPair(pairBytes, pairSha256);
  const manifest = parseExpectedChecks(expectedChecksBytes);
  requireThat(observations instanceof Map && observations.size > 0, 'qualification_scope_unknown');
  const ids = new Set(manifest.checks.map((check) => check.id));
  requireThat(
    [...observations.keys()].every((id) => ids.has(id)),
    'qualification_observation_unknown',
  );
  const expectedChecksSha256 = sha256(expectedChecksBytes);
  const evidence = new Map();
  const checks = manifest.checks.map(({ id, group }) => {
    const observed = observations.get(id);
    if (!observed) return { id, group, status: 'not-run', evidenceSha256: null };
    requireThat(
      ['pass', 'refused'].includes(observed.status) && observed.observation !== undefined,
      'qualification_observation_invalid',
    );
    const bytes = Buffer.from(
      canonicalJson({
        formatVersion: 1,
        kind: 'r7-qualification-case-evidence',
        checkId: id,
        group,
        pairSha256,
        outcome: observed.status,
        observationSha256: sha256(canonicalJson(observed.observation)),
      }),
    );
    evidence.set(id, bytes);
    return { id, group, status: observed.status, evidenceSha256: sha256(bytes) };
  });
  const resultBytes = Buffer.from(
    canonicalJson({
      formatVersion: 1,
      kind: 'r7-qualification-case-results',
      pairSha256,
      scope: QUALIFICATION_SCOPE,
      expectedChecksSha256,
      checks,
    }),
  );
  const passed = checks.every((check) => check.status === 'pass');
  const receiptBytes = Buffer.from(
    canonicalJson({
      formatVersion: 1,
      kind: 'r7-qualification-receipt',
      pairSha256,
      bindings: qualificationBindings(pair),
      scope: QUALIFICATION_SCOPE,
      executionKind,
      producer,
      expectedChecksSha256,
      caseResultsSha256: sha256(resultBytes),
      checkCount: checks.length,
      result: passed ? 'pass' : 'refusal',
      refusalReason: passed
        ? null
        : (refusalReason ??
          (checks.some((c) => c.status === 'refused') ? 'check-refused' : 'not-run')),
    }),
  );
  const receiptSha256 = sha256(receiptBytes);
  inspectQualificationContent({
    pairBytes,
    pairSha256,
    expectedChecksBytes,
    receiptBytes,
    receiptSha256,
    resultBytes,
    evidence,
  });
  return { passed, receiptSha256, receiptBytes, resultBytes, evidence };
}

export async function writeQualificationEvidence(output, repo, content) {
  output = admitOutput(repo, output);
  await mkdir(output, { mode: 0o700 }); // exclusive; never overwrite an earlier run
  await mkdir(join(output, 'evidence'), { mode: 0o700 });
  const files = new Map([
    ['receipt.json', content.receiptBytes],
    ['case-results.json', content.resultBytes],
    ...[...content.evidence].map(([id, bytes]) => [`evidence/${id}.json`, bytes]),
  ]);
  for (const [name, bytes] of files)
    await writeFile(join(output, name), bytes, { flag: 'wx', mode: 0o600 });
  requireThat(
    JSON.stringify((await readdir(output)).sort()) ===
      JSON.stringify(['case-results.json', 'evidence', 'receipt.json']),
    'qualification_output_inventory_invalid',
  );
  requireThat(
    (await lstat(join(output, 'evidence'))).isDirectory() &&
      !(await lstat(join(output, 'evidence'))).isSymbolicLink(),
    'qualification_output_inventory_invalid',
  );
  requireThat(
    JSON.stringify((await readdir(join(output, 'evidence'))).sort()) ===
      JSON.stringify([...content.evidence.keys()].map((id) => id + '.json').sort()),
    'qualification_output_inventory_invalid',
  );
  for (const [name, bytes] of files) {
    const cap =
      name === 'receipt.json'
        ? PAIR_LIMITS.receipt
        : name === 'case-results.json'
          ? PAIR_LIMITS.results
          : PAIR_LIMITS.evidence;
    const stat = await lstat(join(output, name));
    requireThat(
      stat.isFile() &&
        !stat.isSymbolicLink() &&
        (await readBoundedFile(join(output, name), cap)).equals(bytes),
      'qualification_output_readback_failed',
    );
  }
}
