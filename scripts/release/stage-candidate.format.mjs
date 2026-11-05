import { canonicalJson, sha256 } from './build-payload.format.mjs';
import {
  decimal,
  hex,
  parseCanonical,
  requireThat,
  timestamp,
  REPOSITORY,
  validateProducer,
} from './candidate.mjs';
import { exact, parseFinalizationRequest, parseFinalizedPair } from './finalized-pair.format.mjs';

export const STAGE_LIMITS = Object.freeze({
  request: 32768,
  intent: 65536,
  record: 65536,
  pages: 20,
});
export const STAGE_ENVIRONMENT = 'r7-staging';
export const COMPLETION_REVIEWER_ID = '16307884';
export const COMPLETION_DIAGNOSTIC =
  'candidate_staging_denied checkpoint=release_readback code=stage_create_unknown';
export const FINISH_DIAGNOSTIC = 'candidate_staging_denied checkpoint=prewrite code=unknown';
export const FINISH_DISPATCH_BEFORE = Date.parse('2026-11-06T03:00:00Z');
export const FINISH_HARD_END = Date.parse('2026-11-06T04:00:00Z');
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
export function completionIntentName(request) {
  return `r7-p4-completion-intent-${request.pair.payload.identity.releaseVersion}.json`;
}
export function finishIntentName(request) {
  return `r7-p4-finish-intent-${request.pair.payload.identity.releaseVersion}.json`;
}
export function parseFinishRequest(bytes, approvedSha256) {
  requireThat(
    hex(approvedSha256, 64) && sha256(bytes) === approvedSha256,
    'finish_approval_denied',
  );
  const r = parseCanonical(bytes, STAGE_LIMITS.request);
  exact(r, [
    'formatVersion',
    'kind',
    'previousRequest',
    'previousRequestSha256',
    'origin',
    'snapshot',
    'releaseId',
    'descriptorSha256',
    'controlCommit',
    'environmentId',
    'protectionEvidenceSha256',
    'approvalId',
    'validUntil',
    'ownerPolicy',
  ]);
  requireThat(
    r.formatVersion === 1 && r.kind === 'r7-stage-finish-request',
    'finish_request_invalid',
  );
  const previous = parseCompletionRequest(
    Buffer.from(canonicalJson(r.previousRequest)),
    r.previousRequestSha256,
  );
  exact(r.origin, [
    'runId',
    'runAttempt',
    'controlCommit',
    'stageJobId',
    'jobLogSha256',
    'intentId',
    'intentSha256',
    'diagnostic',
    'oldWireAttempt',
  ]);
  for (const value of [r.origin.runId, r.origin.stageJobId, r.origin.intentId])
    requireThat(typeof value === 'string' && decimal(value) === value, 'finish_identity_invalid');
  for (const value of [r.origin.jobLogSha256, r.origin.intentSha256, r.protectionEvidenceSha256])
    requireThat(hex(value, 64), 'finish_digest_invalid');
  requireThat(
    r.origin.runAttempt === '1' &&
      r.origin.controlCommit === previous.controlCommit &&
      r.origin.runId !== previous.origin.runId &&
      r.origin.intentId !== previous.origin.intentId &&
      r.origin.diagnostic === FINISH_DIAGNOSTIC &&
      r.origin.oldWireAttempt === 'UNKNOWN' &&
      hex(r.controlCommit, 40) &&
      ![previous.controlCommit, previous.origin.controlCommit].includes(r.controlCommit) &&
      r.releaseId === previous.releaseId &&
      r.descriptorSha256 === previous.descriptorSha256 &&
      r.environmentId === previous.environmentId &&
      r.approvalId === previous.approvalId &&
      r.ownerPolicy === previous.ownerPolicy &&
      timestamp(r.validUntil) <= Math.min(timestamp(previous.validUntil), FINISH_HARD_END),
    'finish_identity_invalid',
  );
  requireThat(Array.isArray(r.snapshot) && r.snapshot.length === 13, 'finish_snapshot_invalid');
  const names = new Set(),
    ids = new Set();
  for (const [i, a] of r.snapshot.entries()) {
    exact(a, ['name', 'id', 'size', 'sha256']);
    requireThat(
      typeof a.name === 'string' &&
        a.name !== 'qualification.zip' &&
        (i === 0 || r.snapshot[i - 1].name < a.name) &&
        !names.has(a.name) &&
        typeof a.id === 'string' &&
        decimal(a.id) === a.id &&
        !ids.has(a.id) &&
        Number.isSafeInteger(a.size) &&
        a.size > 0 &&
        hex(a.sha256, 64),
      'finish_snapshot_invalid',
    );
    names.add(a.name);
    ids.add(a.id);
  }
  return r;
}
export function finishIntentFor(
  r,
  requestSha256,
  descriptor,
  parentIntent,
  previousIntent,
  producer,
) {
  const zip = descriptor.assets.find((a) => a.name === 'qualification.zip');
  requireThat(zip, 'finish_snapshot_invalid');
  return {
    formatVersion: 1,
    kind: 'r7-stage-finish-intent',
    requestSha256,
    stageRequestSha256: r.previousRequest.stageRequestSha256,
    previousRequestSha256: r.previousRequestSha256,
    parentIntent,
    previousIntent,
    snapshotSha256: sha256(canonicalJson(r.snapshot)),
    releaseId: r.releaseId,
    descriptorSha256: sha256(canonicalJson(descriptor)),
    producer,
    operations: [{ id: 'asset:qualification.zip', inputSha256: sha256(canonicalJson(zip)) }],
  };
}
export function parseCompletionRequest(bytes, approvedSha256) {
  requireThat(
    hex(approvedSha256, 64) && sha256(bytes) === approvedSha256,
    'completion_approval_denied',
  );
  const r = parseCanonical(bytes, STAGE_LIMITS.request);
  exact(r, [
    'formatVersion',
    'kind',
    'stageRequest',
    'stageRequestSha256',
    'origin',
    'releaseId',
    'descriptorSha256',
    'controlCommit',
    'environmentId',
    'protectionEvidenceSha256',
    'approvalId',
    'validUntil',
    'ownerPolicy',
  ]);
  requireThat(
    r.formatVersion === 1 && r.kind === 'r7-stage-completion-request',
    'completion_request_invalid',
  );
  const original = parseStageRequest(
    Buffer.from(canonicalJson(r.stageRequest)),
    r.stageRequestSha256,
  );
  exact(r.origin, [
    'runId',
    'runAttempt',
    'controlCommit',
    'stageJobId',
    'jobLogSha256',
    'intentId',
    'intentSha256',
    'diagnostic',
  ]);
  for (const value of [
    r.releaseId,
    r.environmentId,
    r.origin.runId,
    r.origin.stageJobId,
    r.origin.intentId,
  ])
    requireThat(
      typeof value === 'string' && decimal(value) === value,
      'completion_identity_invalid',
    );
  for (const value of [
    r.descriptorSha256,
    r.protectionEvidenceSha256,
    r.origin.jobLogSha256,
    r.origin.intentSha256,
  ])
    requireThat(hex(value, 64), 'completion_digest_invalid');
  requireThat(
    hex(r.controlCommit, 40) &&
      r.controlCommit !== original.controlCommit &&
      r.origin.controlCommit === original.controlCommit &&
      r.origin.runAttempt === '1' &&
      r.origin.diagnostic === COMPLETION_DIAGNOSTIC &&
      r.environmentId === original.environmentId &&
      r.approvalId === original.approvalId &&
      r.ownerPolicy === 'root-normal-review-run-attempt1' &&
      timestamp(r.validUntil) <= timestamp(original.validUntil),
    'completion_identity_invalid',
  );
  return r;
}
export function completionOwnerFor(r, requestSha256, producer) {
  if (r.kind === 'r7-stage-finish-request')
    return {
      formatVersion: 1,
      kind: 'r7-stage-finish-owner',
      runId: producer.runId,
      runAttempt: '1',
      requestSha256,
      parentIntentId: r.previousRequest.origin.intentId,
      completionIntentId: r.origin.intentId,
      releaseId: r.releaseId,
      snapshotSha256: sha256(canonicalJson(r.snapshot)),
    };
  return {
    formatVersion: 1,
    kind: 'r7-stage-completion-owner',
    runId: producer.runId,
    runAttempt: '1',
    requestSha256,
    parentIntentId: r.origin.intentId,
    releaseId: r.releaseId,
  };
}
export function completionIntentFor(r, requestSha256, descriptor, parentIntent, producer) {
  return {
    formatVersion: 1,
    kind: 'r7-stage-completion-intent',
    requestSha256,
    stageRequestSha256: r.stageRequestSha256,
    parentIntent,
    releaseId: r.releaseId,
    descriptorSha256: sha256(canonicalJson(descriptor)),
    producer,
    operations: descriptor.assets.map((a) => ({
      id: 'asset:' + a.name,
      inputSha256: sha256(canonicalJson(a)),
    })),
  };
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
  requireThat([1, 2, 3].includes(r.formatVersion), 'stage_record_invalid');
  exact(r, [
    'formatVersion',
    'kind',
    'requestSha256',
    'descriptor',
    'intent',
    'releaseId',
    'assets',
    ...(r.formatVersion >= 2 ? ['completion'] : []),
  ]);
  requireThat(r.kind === 'r7-staged-pair' && hex(r.requestSha256, 64), 'stage_record_invalid');
  decimal(r.releaseId);
  requireThat(Array.isArray(r.assets) && r.assets.length === 14, 'stage_record_invalid');
  if (r.formatVersion >= 2) {
    exact(r.completion, ['request', 'requestSha256', 'intent']);
    const request = (r.formatVersion === 3 ? parseFinishRequest : parseCompletionRequest)(
      Buffer.from(canonicalJson(r.completion.request)),
      r.completion.requestSha256,
    );
    const c = r.formatVersion === 3 ? request.previousRequest : request;
    for (const fence of [r.intent, r.completion.intent]) {
      exact(fence, ['artifactId', 'sha256', 'producer', 'expiresAt']);
      decimal(fence.artifactId);
      requireThat(hex(fence.sha256, 64), 'completion_record_invalid');
      validateProducer(fence.producer);
      timestamp(fence.expiresAt);
    }
    requireThat(
      r.requestSha256 === c.stageRequestSha256 &&
        r.releaseId === c.releaseId &&
        sha256(canonicalJson(r.descriptor)) === c.descriptorSha256 &&
        r.intent.artifactId === c.origin.intentId &&
        r.intent.sha256 === c.origin.intentSha256 &&
        r.intent.producer.runId === c.origin.runId &&
        r.intent.producer.runAttempt === '1' &&
        r.intent.producer.workflowCommit === c.origin.controlCommit &&
        r.completion.intent.producer.workflowCommit === request.controlCommit &&
        r.completion.intent.producer.runAttempt === '1' &&
        r.completion.intent.producer.runId !== c.origin.runId,
      'completion_record_invalid',
    );
    if (r.formatVersion === 3)
      requireThat(
        r.completion.intent.producer.runId !== request.origin.runId &&
          request.snapshot.every((a) => r.assets.some((b) => same(a, b))),
        'finish_record_invalid',
      );
  }
  return r;
}
