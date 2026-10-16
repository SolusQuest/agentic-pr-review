export function packageHashes(
  bytes: Uint8Array,
  metadataHash: string,
  assetsHash: string,
  rawHash: string,
): { sha256: string; contentHash: string };
export const SOLUS_VERSION: string;
export const SOLUS_FEED: string;
export const PUBLIC_FEED: string;
export const SOLUS_IDS: readonly string[];
export function dependencyVersionAllowed(
  id: string,
  version: string,
  selectedSolusVersion?: string | null,
): boolean;
export function packageSource(id: string): string;
export function assertSolusAssetPins(assets: unknown): void;
