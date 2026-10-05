import {
  LIMITS,
  MEMBERS,
  archiveName,
  compiledIdentity,
  validateReleaseVersion,
  type PackageReceipt,
} from '../../../scripts/release/build-payload.format.mjs';
import { parseStrictJson } from './strict-json.js';
import { exactRecord, fail, lowerHex } from './validation.js';

export const PAYLOAD_REPOSITORY = 'SolusQuest/agentic-pr-review';
export const PAYLOAD_MAP_MAXIMUM_BYTES = 16 * 1024;

export interface PayloadMap {
  readonly formatVersion: 1;
  readonly repository: typeof PAYLOAD_REPOSITORY;
  readonly tag: string;
  readonly builder: {
    readonly repository: typeof PAYLOAD_REPOSITORY;
    readonly workflowPath: string;
    readonly workflowCommit: string;
  };
  readonly payload: PackageReceipt;
}

/** Only trusted Action-owned bytes may enter here; network metadata is never map authority. */
export function parsePayloadMap(bytes: Uint8Array): PayloadMap {
  try {
    const map = exactRecord(parseStrictJson(bytes, PAYLOAD_MAP_MAXIMUM_BYTES), [
      'formatVersion',
      'repository',
      'tag',
      'builder',
      'payload',
    ]);
    const builder = exactRecord(map.builder, ['repository', 'workflowPath', 'workflowCommit']);
    const receipt = exactRecord(map.payload, [
      'formatVersion',
      'archiveName',
      'archiveSize',
      'archiveSha256',
      'identity',
      'members',
    ]);
    const identity = exactRecord(receipt.identity, [
      'releaseVersion',
      'platform',
      'sourceCommit',
      'sourceTree',
      'buildId',
      'informationalVersion',
    ]);
    const version = validateReleaseVersion(identity.releaseVersion);
    if (
      map.formatVersion !== 1 ||
      map.repository !== PAYLOAD_REPOSITORY ||
      map.tag !== `payload-${version}` ||
      builder.repository !== PAYLOAD_REPOSITORY ||
      typeof builder.workflowPath !== 'string' ||
      !/^\.github\/workflows\/[A-Za-z0-9][A-Za-z0-9_-]{0,79}\.ya?ml$/u.test(builder.workflowPath) ||
      !lowerHex(builder.workflowCommit, 40) ||
      receipt.formatVersion !== 1 ||
      receipt.archiveName !== archiveName(version) ||
      !positiveSize(receipt.archiveSize, LIMITS.archive) ||
      !lowerHex(receipt.archiveSha256, 64) ||
      identity.platform !== 'linux-x64' ||
      !lowerHex(identity.sourceCommit, 40) ||
      !lowerHex(identity.sourceTree, 40) ||
      !lowerHex(identity.buildId, 64) ||
      identity.informationalVersion !== compiledIdentity(version, identity.buildId) ||
      !Array.isArray(receipt.members) ||
      receipt.members.length !== MEMBERS.length
    ) {
      fail('wrapper_payload_map_invalid');
    }
    const members = receipt.members.map((value: unknown, index: number) => {
      const member = exactRecord(value, ['path', 'mode', 'size', 'sha256']);
      const spec = MEMBERS[index];
      if (
        member.path !== spec.path ||
        member.mode !== spec.mode ||
        !positiveSize(member.size, spec.limit) ||
        !lowerHex(member.sha256, 64)
      ) {
        fail('wrapper_payload_map_invalid');
      }
      return Object.freeze({
        path: spec.path,
        mode: spec.mode,
        size: member.size,
        sha256: member.sha256,
      });
    });
    return Object.freeze({
      formatVersion: 1,
      repository: PAYLOAD_REPOSITORY,
      tag: `payload-${version}`,
      builder: Object.freeze({
        repository: PAYLOAD_REPOSITORY,
        workflowPath: builder.workflowPath,
        workflowCommit: builder.workflowCommit,
      }),
      payload: Object.freeze({
        formatVersion: 1,
        archiveName: receipt.archiveName,
        archiveSize: receipt.archiveSize,
        archiveSha256: receipt.archiveSha256,
        identity: Object.freeze({
          releaseVersion: version,
          platform: 'linux-x64',
          sourceCommit: identity.sourceCommit,
          sourceTree: identity.sourceTree,
          buildId: identity.buildId,
          informationalVersion: identity.informationalVersion,
        }),
        members: Object.freeze(members),
      }),
    });
  } catch {
    fail('wrapper_payload_map_invalid');
  }
}

function positiveSize(value: unknown, maximum: number): value is number {
  return Number.isSafeInteger(value) && (value as number) > 0 && (value as number) <= maximum;
}
