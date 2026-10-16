import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { packageHashes } from '../../scripts/release/solus-package-contract.mjs';

describe('Solus package integrity identities', () => {
  const bytes = Buffer.from('synthetic signed archive');
  const logical = createHash('sha512').update('original package content').digest('base64');
  const raw = createHash('sha512').update(bytes).digest('base64');
  it('checks logical content and signed archive hashes independently', () => {
    expect(logical).not.toBe(raw);
    expect(packageHashes(bytes, logical, logical, raw)).toEqual({
      sha256: createHash('sha256').update(bytes).digest('hex'),
      contentHash: logical,
    });
  });
  it('rejects changed raw archive bytes', () => {
    expect(() => packageHashes(Buffer.from('changed'), logical, logical, raw)).toThrow(
      'APR_SOLUS_HASH',
    );
  });
  it('rejects inconsistent logical metadata or raw archive declaration', () => {
    expect(() => packageHashes(bytes, logical, raw, raw)).toThrow('APR_SOLUS_HASH');
    expect(() => packageHashes(bytes, logical, logical, logical)).toThrow('APR_SOLUS_HASH');
    expect(() => packageHashes(bytes, 'invalid', 'invalid', raw)).toThrow('APR_SOLUS_HASH');
  });
});
