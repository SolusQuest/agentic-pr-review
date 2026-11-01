// Repository proof policy. Never imported by a product entry or selected by Action inputs.
import { createCipheriv, createHash } from 'node:crypto';
import { readFileSync, mkdirSync, writeFileSync, lstatSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { execFileSync } from 'node:child_process';
import { isDeepStrictEqual } from 'node:util';

export const repository = 'SolusQuest/agentic-pr-review';
export const repositoryId = '1263978368';
export const actor = 'Yuee98';
export const actorId = '16307884';
export const workflow = '.github/workflows/cr2-hosted-artifact-proof.yml';
// Stable merged Action identity; V1 qualification retains its own source binding.
export const actionSource = '5207bf8058b3d1301c0a6e805e7f21334832223e';
export const format = 'apr.cr2.hosted-artifact-proof.v1';
export const acknowledgement = 'ONE-SYNTHETIC-OBJECT-TWO-RUNS-ONE-EXACT-DELETE';
export const assets = Object.freeze([
  'action.yml',
  'payload-map.json',
  'internal/action.yml',
  'internal/prepare/action.yml',
  'internal/dist/index.js',
  'internal/dist/sdk-upload.js',
  'internal/dist/prepare.js',
  'internal/dist/cleanup.js',
]);
export const limits = Object.freeze({
  encryptedBytes: 4096,
  carrierBytes: 8192,
  zipBytes: 16384,
  apiPerRun: 16,
  anonymousPerRun: 2,
  metadataPerRun: 524288,
  zipDownloadsPerRun: 32768,
  preflightRecords: 99,
  censusRecords: 100,
  listsA: 2,
  listsB: 1,
  uploadCalls: 1,
  deleteCalls: 1,
  retentionDays: 1,
  sdkCreateAttempts: 5,
  sdkFinalizeAttempts: 5,
  blobWriteAttempts: 8,
  resultsRedirectsPerAttempt: 50,
  blobRedirectsPerAttempt: 20,
  sdkHttpRequests: 678,
  uploadBodyBytes: 16777216,
  jobMinutes: 10,
  pairMinutes: 30,
});
export function requireProof(value) {
  if (!value) throw new Error('cr2_hosted_artifact_proof_refused');
}
export const sha256 = (bytes) => createHash('sha256').update(bytes).digest('hex');
export const blobSha = (bytes) =>
  createHash('sha1').update(`blob ${bytes.length}\0`).update(bytes).digest('hex');
export function fixture() {
  const key = createHash('sha256').update('apr.cr2.h1.public-test-key.v1').digest();
  const nonce = createHash('sha256')
    .update('apr.cr2.h1.public-test-nonce.v1')
    .digest()
    .subarray(0, 12);
  const plain = Buffer.alloc(4068, 0x5a);
  Buffer.from('CR2-H1-SYNTHETIC-OPAQUE-V1').copy(plain);
  const cipher = createCipheriv('aes-256-gcm', key, nonce);
  cipher.setAAD(Buffer.from('apr.cr2.h1.synthetic-only.v1'));
  const bytes = Buffer.concat([nonce, cipher.update(plain), cipher.final(), cipher.getAuthTag()]);
  key.fill(0);
  plain.fill(0);
  requireProof(bytes.length === limits.encryptedBytes && sha256(bytes) === fixtureDigest);
  return bytes;
}
export const fixtureDigest = '15d392b30989f7b08f94615c1c57202a47a95408e35abdf286662a4ab8c3ce86';
export function exactKeys(value, keys) {
  requireProof(value && typeof value === 'object' && !Array.isArray(value));
  requireProof(Object.keys(value).sort().join('\0') === [...keys].sort().join('\0'));
}
export function positive(value) {
  return (
    typeof value === 'string' &&
    /^[1-9][0-9]{0,15}$/u.test(value) &&
    Number.isSafeInteger(Number(value))
  );
}
export function admitHosted(env, inputs, system = process) {
  requireProof(
    env.GITHUB_EVENT_NAME === 'workflow_dispatch' && env.GITHUB_REPOSITORY === repository,
  );
  requireProof(
    env.GITHUB_REPOSITORY_ID === repositoryId &&
      env.GITHUB_ACTOR === actor &&
      env.GITHUB_ACTOR_ID === actorId,
  );
  requireProof(env.GITHUB_RUN_ATTEMPT === '1' && positive(env.GITHUB_RUN_ID));
  requireProof(
    /^[a-f0-9]{40}$/u.test(inputs.source) &&
      inputs.source === env.GITHUB_SHA &&
      inputs.source === env.WORKFLOW_SHA,
  );
  requireProof(env.GITHUB_WORKFLOW_REF === `${repository}/${workflow}@refs/heads/main`);
  requireProof(
    inputs.approval === acknowledgement && ['produce', 'restore'].includes(inputs.phase),
  );
  requireProof(system.platform === 'linux' && system.arch === 'x64');
}
export function census(total, phase, beforeUpload) {
  requireProof(Number.isSafeInteger(total) && total >= 0);
  requireProof(
    total <= (phase === 'produce' && beforeUpload ? limits.preflightRecords : limits.censusRecords),
  );
}
// Explicit historical fixture for rehearsal/tests only; never a hosted authority.
export function historicalActionFixture(repo, root) {
  mkdirSync(root, { recursive: false, mode: 0o700 });
  for (const name of assets) {
    const path = `.github/actions/agentic-pr-review/${name}`;
    const entry = execFileSync(
      'git',
      ['--no-replace-objects', 'ls-tree', actionSource, '--', path],
      { cwd: repo, encoding: 'utf8' },
    );
    requireProof(entry.startsWith('100644 blob ') && entry.trimEnd().endsWith('\t' + path));
    const bytes = execFileSync('git', ['--no-replace-objects', 'show', `${actionSource}:${path}`], {
      cwd: repo,
      maxBuffer: 9 * 1024 * 1024,
    });
    mkdirSync(dirname(join(root, name)), { recursive: true, mode: 0o700 });
    writeFileSync(join(root, name), bytes, { flag: 'wx', mode: 0o600 });
  }
  inspectAssets(repo, undefined, root);
  return root;
}
export function inspectAssets(repo, installed, root = `${repo}/.github/actions/agentic-pr-review`) {
  const found = [];
  function walk(directory, prefix = '') {
    requireProof(lstatSync(directory).isDirectory() && !lstatSync(directory).isSymbolicLink());
    for (const name of readdirSync(directory)) {
      const file = join(directory, name),
        relative = prefix + name,
        stat = lstatSync(file);
      requireProof(!stat.isSymbolicLink());
      if (stat.isDirectory()) walk(file, relative + '/');
      else {
        requireProof(stat.isFile());
        found.push(relative);
      }
    }
  }
  walk(root);
  requireProof(isDeepStrictEqual(found.sort(), [...assets].sort()));
  const result = {};
  for (const name of assets) {
    const original = execFileSync(
      'git',
      ['--no-replace-objects', 'show', `${actionSource}:.github/actions/agentic-pr-review/${name}`],
      { cwd: repo, maxBuffer: 9 * 1024 * 1024 },
    );
    const checked = readFileSync(join(root, name));
    requireProof(original.equals(checked));
    if (installed) requireProof(readFileSync(`${installed}/${name}`).equals(original));
    if (name === 'payload-map.json') requireProof(JSON.parse(original) === null);
    result[name] = { sha256: sha256(original), blob: blobSha(original), size: original.length };
  }
  return result;
}
export function sourceMetadata(url, config) {
  requireProof(url.origin === 'https://api.github.com');
  const prefix = `/repos/${repository}/contents/.github/actions/agentic-pr-review/`;
  requireProof(url.pathname.startsWith(prefix) && url.search === `?ref=${actionSource}`);
  const name = url.pathname.slice(prefix.length);
  requireProof(assets.includes(name));
  const info =
    name === 'payload-map.json'
      ? { size: Buffer.byteLength(config.mapBytes), blob: blobSha(Buffer.from(config.mapBytes)) }
      : config.assets[name];
  return {
    type: 'file',
    path: `.github/actions/agentic-pr-review/${name}`,
    size: info.size,
    sha: info.blob,
  };
}
function receiptShape(receipt, source) {
  exactKeys(receipt, [
    'format',
    'mode',
    'phase',
    'source',
    'actionSource',
    'repositoryId',
    'actorId',
    'runId',
    'runA',
    'artifact',
    'counts',
    'outcome',
    'mutationState',
    'syntheticMap',
    'companionEntry',
    'publicDevelopmentProfile',
  ]);
  requireProof(receipt.format === format && receipt.outcome === 'passed');
  requireProof(['hosted', 'rehearsal'].includes(receipt.mode));
  requireProof(
    receipt.source === source &&
      receipt.actionSource === actionSource &&
      receipt.repositoryId === repositoryId &&
      receipt.actorId === actorId,
  );
  requireProof(positive(receipt.runId) && positive(receipt.runA));
  requireProof(
    receipt.syntheticMap === true &&
      receipt.companionEntry === true &&
      receipt.publicDevelopmentProfile === true,
  );
  exactKeys(receipt.artifact, [
    'id',
    'name',
    'physicalName',
    'encryptedDigest',
    'archiveDigest',
    'expires',
    'size',
    'producer',
    'attempt',
  ]);
  requireProof(
    positive(receipt.artifact.id) &&
      receipt.artifact.producer === receipt.runA &&
      receipt.artifact.attempt === 1,
  );
  requireProof(
    receipt.artifact.name === `cr2-h1-${receipt.runA}` &&
      receipt.artifact.encryptedDigest === fixtureDigest &&
      receipt.artifact.size === 4096,
  );
  requireProof(/^[a-f0-9]{64}$/u.test(receipt.artifact.archiveDigest));
  const family = sha256(Buffer.from(`apr-artifact-family\0${receipt.artifact.name}`));
  requireProof(receipt.artifact.physicalName === `apr-object-${family}-${fixtureDigest}`);
  requireProof(Number.isSafeInteger(receipt.artifact.expires) && receipt.artifact.expires > 0);
  exactKeys(receipt.counts, [
    'api',
    'anonymous',
    'metadataBytes',
    'archiveBytes',
    'lists',
    'uploads',
    'deletes',
  ]);
  for (const value of Object.values(receipt.counts))
    requireProof(Number.isSafeInteger(value) && value >= 0);
  requireProof(
    receipt.counts.api <= 16 &&
      receipt.counts.anonymous <= 2 &&
      receipt.counts.metadataBytes <= 524288 &&
      receipt.counts.archiveBytes <= 32768,
  );
  requireProof(
    receipt.counts.anonymous >= 1 &&
      receipt.counts.metadataBytes > 0 &&
      receipt.counts.archiveBytes >= 22,
  );
}
export function admitReceipt(receipt, source, runB) {
  receiptShape(receipt, source);
  requireProof(
    receipt.phase === 'produce' && receipt.runId === receipt.runA && receipt.runId !== runB,
  );
  requireProof(receipt.mutationState === 'upload_committed' && receipt.counts.api >= 6);
  requireProof(
    receipt.counts.lists === 2 && receipt.counts.uploads === 1 && receipt.counts.deletes === 0,
  );
  return receipt;
}
export function admitRestoreReceipt(receipt, producer, source, runB) {
  admitReceipt(producer, source, runB);
  receiptShape(receipt, source);
  requireProof(
    receipt.phase === 'restore' && receipt.runId === runB && receipt.runA === producer.runId,
  );
  requireProof(
    receipt.mode === producer.mode && isDeepStrictEqual(receipt.artifact, producer.artifact),
  );
  requireProof(receipt.mutationState === 'delete_committed' && receipt.counts.api >= 9);
  requireProof(
    receipt.counts.lists === 1 && receipt.counts.uploads === 0 && receipt.counts.deletes === 1,
  );
  return receipt;
}
