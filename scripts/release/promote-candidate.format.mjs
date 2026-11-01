import { canonicalJson, sha256 } from './build-payload.format.mjs';
import { decimal, hex, parseCanonical, requireThat, timestamp } from './candidate.mjs';
import { exact } from './finalized-pair.format.mjs';
import { parseStageRequest, parseStagedRecord } from './stage-candidate.format.mjs';

export const PUBLICATION_ENVIRONMENT = 'r7-release';
export const PROMOTION_LIMITS = Object.freeze({ request: 49152, record: 65536 });
export function parsePromotionRequest(bytes, approvedSha256) {
  requireThat(
    hex(approvedSha256, 64) && sha256(bytes) === approvedSha256,
    'promotion_approval_denied',
  );
  const r = parseCanonical(bytes, PROMOTION_LIMITS.request);
  exact(r, [
    'formatVersion',
    'kind',
    'channel',
    'stageRequest',
    'stageRequestSha256',
    'stagedRecord',
    'stagedRecordSha256',
    'approvalId',
    'validUntil',
    'controlCommit',
    'environmentId',
    'actionRulesetId',
    'redistributionApprovalSha256',
  ]);
  requireThat(
    r.formatVersion === 1 && r.kind === 'r7-promotion-request' && r.channel === 'PUBLIC',
    'promotion_request_invalid',
  );
  parseStageRequest(Buffer.from(canonicalJson(r.stageRequest)), r.stageRequestSha256);
  parseStagedRecord(Buffer.from(canonicalJson(r.stagedRecord)), r.stagedRecordSha256);
  requireThat(
    r.stagedRecord.requestSha256 === r.stageRequestSha256 &&
      hex(r.controlCommit, 40) &&
      hex(r.redistributionApprovalSha256, 64),
    'promotion_identity_invalid',
  );
  for (const k of ['environmentId', 'actionRulesetId'])
    requireThat(typeof r[k] === 'string' && decimal(r[k]) === r[k], 'promotion_id_invalid');
  requireThat(
    typeof r.approvalId === 'string' &&
      /^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$/.test(r.approvalId) &&
      r.approvalId !== r.stageRequest.approvalId,
    'promotion_fresh_approval_required',
  );
  timestamp(r.validUntil);
  return r;
}
export function promotionIntentName(r) {
  return `r7-p5-intent-${r.stageRequest.pair.payload.identity.releaseVersion}.json`;
}
export function publicIdentity(r) {
  return {
    releaseId: r.stagedRecord.releaseId,
    assets: r.stagedRecord.assets,
    runtimePolicySha256: r.stageRequest.pair.action.runtimePolicySha256,
  };
}
export function promotionIntent(r, requestSha256, producer) {
  return {
    formatVersion: 1,
    kind: 'r7-promotion-intent',
    requestSha256,
    stagedRecordSha256: r.stagedRecordSha256,
    producer,
    operations: [
      {
        id: 'release-publish',
        inputSha256: sha256(
          canonicalJson({
            releaseId: r.stagedRecord.releaseId,
            draft: false,
            prerelease: true,
            make_latest: 'false',
          }),
        ),
      },
      {
        id: 'action-tag-create',
        inputSha256: sha256(
          canonicalJson({
            ref: 'refs/tags/' + r.stageRequest.pair.payload.identity.releaseVersion,
            sha: r.stageRequest.pair.action.commit,
            publicIdentity: publicIdentity(r),
          }),
        ),
      },
    ],
  };
}
