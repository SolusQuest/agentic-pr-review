import { canonicalJson, sha256 } from './build-payload.format.mjs';
import { decimal, hex, parseCanonical, requireThat, timestamp, REPOSITORY } from './candidate.mjs';
import { exact, parseFinalizationRequest, parseFinalizedPair } from './finalized-pair.format.mjs';

export const STAGE_LIMITS = Object.freeze({
  request: 32768,
  intent: 65536,
  record: 65536,
  pages: 20,
});
export const STAGE_ENVIRONMENT = 'r7-staging';
export const same = (a, b) => canonicalJson(a) === canonicalJson(b);
export function parseStageRequest(bytes, approvedSha256) {
  requireThat(hex(approvedSha256, 64) && sha256(bytes) === approvedSha256, 'stage_approval_denied');
  const r = parseCanonical(bytes, STAGE_LIMITS.request);
  exact(r, [
    'formatVersion',
    'kind',
    'pair',
    'pairSha256',
    'finalization',
    'qualifierCommit',
    'qualificationId',
    'qualificationSha256',
    'receiptSha256',
    'approvalId',
    'validUntil',
    'controlCommit',
    'environmentId',
    'protectionEvidenceSha256',
  ]);
  requireThat(r.formatVersion === 1 && r.kind === 'r7-stage-request', 'stage_request_invalid');
  parseFinalizedPair(Buffer.from(canonicalJson(r.pair)), r.pairSha256);
  parseFinalizationRequest(Buffer.from(canonicalJson(r.finalization)));
  for (const k of ['qualificationSha256', 'receiptSha256', 'protectionEvidenceSha256'])
    requireThat(hex(r[k], 64), 'stage_digest_invalid');
  for (const k of ['controlCommit', 'qualifierCommit'])
    requireThat(hex(r[k], 40), 'stage_revision_invalid');
  for (const k of ['qualificationId', 'environmentId'])
    requireThat(typeof r[k] === 'string' && decimal(r[k]) === r[k], 'stage_id_invalid');
  requireThat(
    typeof r.approvalId === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/.test(r.approvalId),
    'stage_approval_invalid',
  );
  timestamp(r.validUntil);
  return r;
}
export function intentName(request) {
  return `r7-p4-intent-${request.pair.payload.identity.releaseVersion}.json`;
}
export function descriptorFor(r, assets) {
  return {
    formatVersion: 1,
    kind: 'r7-draft-candidate',
    repository: REPOSITORY,
    tag: 'payload-' + r.pair.payload.identity.releaseVersion,
    sourceCommit: r.pair.payload.identity.sourceCommit,
    workflowCommit: r.pair.producer.workflowCommit,
    actionCommit: r.pair.action.commit,
    qualifierCommit: r.qualifierCommit,
    pairSha256: r.pairSha256,
    qualificationSha256: r.qualificationSha256,
    receiptSha256: r.receiptSha256,
    provenanceSha256: r.pair.provenance.bundleSha256,
    approvalId: r.approvalId,
    protectionEvidenceSha256: r.protectionEvidenceSha256,
    assets: assets.map(({ name, size, sha256 }) => ({ name, size, sha256 })),
  };
}
export function intentFor(r, requestSha256, descriptor, producer) {
  return {
    formatVersion: 1,
    kind: 'r7-stage-intent',
    requestSha256,
    descriptorSha256: sha256(canonicalJson(descriptor)),
    producer,
    // A batch reservation consumes EVERY operation on runner loss. It is not a resume journal.
    operations: [
      { id: 'release-create', inputSha256: sha256(canonicalJson(descriptor)) },
      ...descriptor.assets.map((a) => ({
        id: 'asset:' + a.name,
        inputSha256: sha256(canonicalJson(a)),
      })),
    ],
  };
}
export function parseStagedRecord(bytes, expectedSha256) {
  requireThat(
    hex(expectedSha256, 64) && sha256(bytes) === expectedSha256,
    'stage_record_digest_invalid',
  );
  const r = parseCanonical(bytes, STAGE_LIMITS.record);
  exact(r, [
    'formatVersion',
    'kind',
    'requestSha256',
    'descriptor',
    'intent',
    'releaseId',
    'assets',
  ]);
  requireThat(
    r.formatVersion === 1 && r.kind === 'r7-staged-pair' && hex(r.requestSha256, 64),
    'stage_record_invalid',
  );
  decimal(r.releaseId);
  requireThat(Array.isArray(r.assets) && r.assets.length === 14, 'stage_record_invalid');
  return r;
}
