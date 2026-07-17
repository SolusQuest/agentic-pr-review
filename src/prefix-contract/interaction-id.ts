import { createHash } from 'node:crypto';
import { isValidDigest, isValidGitSha, isValidOrdinal } from './identity.js';
import { PREFIX_CODES, fail, ok, type PrefixResult } from './result.js';

/**
 * Host-authoritative interaction-id deriver (issue #50, D7/D9). Preimage and
 * framing per the design contract's Prefix Contract section; sessionEpoch is
 * never encoded.
 */

export type PredecessorLedgerReference =
  | { readonly kind: 'bootstrap' }
  | { readonly kind: 'ledger'; readonly sha256Hex: string };

const INTERACTION_TAG = 'agentic-pr-review/interaction/v1';

function encodeIdentity(out: number[], value: string): void {
  const bytes = new TextEncoder().encode(value);
  writeUInt32BE(out, bytes.byteLength);
  for (const byte of bytes) {
    out.push(byte);
  }
}

function writeUInt32BE(out: number[], value: number): void {
  out.push((value >>> 24) & 0xff, (value >>> 16) & 0xff, (value >>> 8) & 0xff, value & 0xff);
}

export function deriveInteractionId(
  predecessor: PredecessorLedgerReference,
  consumedInputSha256: string,
  currentHeadSha: string,
  interactionOrdinal: number,
): PrefixResult<string> {
  let predecessorComponent: string;
  if (predecessor.kind === 'bootstrap') {
    predecessorComponent = 'bootstrap';
  } else if (predecessor.kind === 'ledger') {
    if (!isValidDigest(predecessor.sha256Hex)) {
      return fail(PREFIX_CODES.digestInvalid, '/predecessor');
    }
    predecessorComponent = predecessor.sha256Hex;
  } else {
    return fail(PREFIX_CODES.identityInvalid, '/predecessor');
  }

  if (!isValidDigest(consumedInputSha256)) {
    return fail(PREFIX_CODES.digestInvalid, '/consumedInputSha256');
  }

  if (!isValidGitSha(currentHeadSha)) {
    return fail(PREFIX_CODES.gitShaInvalid, '/currentHeadSha');
  }

  if (!isValidOrdinal(interactionOrdinal)) {
    return fail(PREFIX_CODES.ordinalInvalid, '/interactionOrdinal');
  }

  const bytes: number[] = [];
  const tagBytes = new TextEncoder().encode(INTERACTION_TAG);
  for (const byte of tagBytes) {
    bytes.push(byte);
  }
  bytes.push(0);
  encodeIdentity(bytes, predecessorComponent);
  encodeIdentity(bytes, consumedInputSha256);
  encodeIdentity(bytes, currentHeadSha);
  encodeIdentity(bytes, String(interactionOrdinal));

  return ok(createHash('sha256').update(Uint8Array.from(bytes)).digest('hex'));
}
