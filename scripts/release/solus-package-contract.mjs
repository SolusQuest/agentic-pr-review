import { createHash } from 'node:crypto';

export const SOLUS_VERSION = '0.1.0-internal.4';
// Publisher/tag provenance anchor; archive hashes are measured independently.
export const SOLUS_RELEASE_SOURCE = 'cb5f5ef8fc39a24f888065052078d40722e7680b';
export const SOLUS_FEED = 'https://nuget.pkg.github.com/SolusQuest/index.json';
export const PUBLIC_FEED = 'https://api.nuget.org/v3/index.json';
export const SOLUS_IDS = Object.freeze([
  'solusagent.api',
  'solusagent.providers.deepseek',
  'solusagent.runtime',
  'solusagent.runtime.api',
  'solusagent.tools.api',
]);

export function dependencyVersionAllowed(id, version, selectedSolusVersion = null) {
  if (typeof id !== 'string' || id !== id.toLowerCase() || typeof version !== 'string')
    return false;
  if (id.startsWith('solusagent.'))
    return (
      SOLUS_IDS.includes(id) &&
      selectedSolusVersion === SOLUS_VERSION &&
      version === selectedSolusVersion
    );
  return /^\d+\.\d+\.\d+$/.test(version);
}

export function packageSource(id) {
  return SOLUS_IDS.includes(id) ? SOLUS_FEED : PUBLIC_FEED;
}

export function assertSolusAssetPins(assets) {
  const entries = Object.entries(assets?.libraries || {}).filter(([key]) =>
    key.toLowerCase().startsWith('solusagent.'),
  );
  const exact = new RegExp(
    '^\\[' +
      SOLUS_VERSION.replaceAll('.', '\\.') +
      '(?:,\\s*' +
      SOLUS_VERSION.replaceAll('.', '\\.') +
      ')?\\]$',
  );
  const frameworks = Object.values(assets?.project?.frameworks || {});
  if (
    entries.length !== SOLUS_IDS.length ||
    !SOLUS_IDS.every((id) =>
      entries.some(
        ([key, value]) =>
          key.toLowerCase() === id + '/' + SOLUS_VERSION && value.type === 'package',
      ),
    ) ||
    frameworks.length === 0 ||
    !frameworks.every((framework) =>
      SOLUS_IDS.every((id) =>
        Object.entries(framework.dependencies || {}).some(
          ([key, value]) => key.toLowerCase() === id && exact.test(value.version),
        ),
      ),
    )
  )
    throw new Error('APR_SOLUS_PIN');
}

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
