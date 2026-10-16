export function packageHashes(
  bytes: Uint8Array,
  metadataHash: string,
  assetsHash: string,
  rawHash: string,
): { sha256: string; contentHash: string };
