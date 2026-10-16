import { createHash } from 'node:crypto';

// NuGet's logical content hash and a signed archive's raw hash can differ.
// Both must be checked independently; the final pin also binds archive SHA256.
export function packageHashes(bytes, metadataHash, assetsHash, rawHash) {
  const logicalHash = /^[A-Za-z0-9+/]{86}==$/;
  if (
    !logicalHash.test(metadataHash) ||
    metadataHash !== assetsHash ||
    createHash('sha512').update(bytes).digest('base64') !== rawHash
  )
    throw new Error('APR_SOLUS_HASH');
  return { sha256: createHash('sha256').update(bytes).digest('hex'), contentHash: metadataHash };
}
