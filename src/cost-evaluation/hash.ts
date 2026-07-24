/**
 * SHA-256 helpers for the cost-evaluation harness.
 *
 * Thin wrapper over `node:crypto`. Domain-tagged digests follow the frozen
 * shape `SHA256(UTF8(tag) || 0x00 || body)` used across M4 contracts.
 */
import { createHash } from 'node:crypto';

/** Lowercase hex SHA-256 of a byte payload. */
export function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

/**
 * Domain-tagged digest: `SHA256(UTF8(tag) || 0x00 || body)`. The single NUL
 * octet separates the tag from the body; `tag` must not contain a NUL.
 */
export function digestId(tag: string, body: Uint8Array): string {
  if (tag.includes('\x00')) {
    throw new Error('digest tag must not contain a NUL octet');
  }
  return createHash('sha256')
    .update(tag, 'utf8')
    .update(Uint8Array.of(0x00))
    .update(body)
    .digest('hex');
}
