import { createHash } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import {
  packageHashes,
  SOLUS_IDS,
  SOLUS_VERSION,
  SOLUS_FEED,
  dependencyVersionAllowed,
  assertSolusAssetPins,
} from '../../scripts/release/solus-package-contract.mjs';
import {
  verifyDependencies,
  managedDependencyPolicy,
} from '../../scripts/release/build-payload.mjs';
import { mkdtempSync, mkdirSync, writeFileSync, readFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

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

function assetPins(): any {
  return {
    libraries: Object.fromEntries(
      SOLUS_IDS.map((id) => [id + '/' + SOLUS_VERSION, { type: 'package' }]),
    ),
    project: {
      frameworks: {
        'net10.0': {
          dependencies: Object.fromEntries(
            SOLUS_IDS.map((id) => [
              id,
              { version: '[' + SOLUS_VERSION + ', ' + SOLUS_VERSION + ']' },
            ]),
          ),
        },
      },
    },
  };
}

describe('exact managed Solus selection', () => {
  it('admits the selected prerelease only in the managed policy', () => {
    expect(dependencyVersionAllowed('solusagent.runtime', SOLUS_VERSION, SOLUS_VERSION)).toBe(true);
    expect(dependencyVersionAllowed('solusagent.runtime', SOLUS_VERSION)).toBe(false);
    expect(dependencyVersionAllowed('jsonschema.net', '9.2.2')).toBe(true);
  });
  it.each(['*', '0.1.0-internal.2', '[0.1.0-internal.1,)', '0.1.0-internal.1+other', '1.0.0'])(
    'rejects alternate or floating private identity %s',
    (version) => {
      expect(dependencyVersionAllowed('solusagent.runtime', version, SOLUS_VERSION)).toBe(false);
    },
  );
  it('requires complete resolved and exact direct pins', () => {
    expect(() => assertSolusAssetPins(assetPins())).not.toThrow();
    for (const version of [SOLUS_VERSION, '[' + SOLUS_VERSION + ',)', '*']) {
      const assets = assetPins();
      assets.project.frameworks['net10.0'].dependencies['solusagent.api'].version = version;
      expect(() => assertSolusAssetPins(assets)).toThrow('APR_SOLUS_PIN');
    }
    const missing = assetPins();
    delete missing.libraries['solusagent.api/' + SOLUS_VERSION];
    expect(() => assertSolusAssetPins(missing)).toThrow('APR_SOLUS_PIN');
    const mixed = assetPins();
    mixed.libraries['solusagent.runtime/0.1.0-internal.2'] = { type: 'package' };
    expect(() => assertSolusAssetPins(mixed)).toThrow('APR_SOLUS_PIN');
  });
  it('rejects mixed, incomplete and duplicate managed catalogs', () => {
    const policy = JSON.parse(readFileSync('scripts/release/build-payload.inputs.json', 'utf8'));
    expect(managedDependencyPolicy(policy).dependencies).toHaveLength(9);
    for (const change of ['mixed', 'missing', 'duplicate']) {
      const copy = structuredClone(policy);
      if (change === 'mixed') copy.managed.additionalDependencies[0].version = '0.1.0-internal.2';
      if (change === 'missing') copy.managed.additionalDependencies.pop();
      if (change === 'duplicate')
        copy.managed.additionalDependencies[1] = copy.managed.additionalDependencies[0];
      expect(() => managedDependencyPolicy(copy)).toThrow('invalid_managed_build_policy');
    }
    expect(policy.dependencies.some((item: any) => item.id.startsWith('solusagent.'))).toBe(false);
  });
});

it('rejects alternate source with identical package bytes; distinguishes an explicit offline source', async () => {
  // Synthetic verifier fixture, never an installed-package or constructor proof.
  const root = mkdtempSync(join(tmpdir(), 'apr-solus-source-'));
  try {
    const assets = assetPins();
    assets.version = 3;
    assets.packageFolders = { [root]: {} };
    assets.project.restore = { packagesPath: root };
    const dependencies = SOLUS_IDS.map((id) => {
      const bytes = Buffer.from('synthetic package ' + id);
      const contentHash = createHash('sha512')
        .update('logical ' + id)
        .digest('base64');
      const directory = join(root, id, SOLUS_VERSION);
      mkdirSync(directory, { recursive: true });
      writeFileSync(join(directory, id + '.' + SOLUS_VERSION + '.nupkg'), bytes);
      writeFileSync(
        join(directory, id + '.' + SOLUS_VERSION + '.nupkg.sha512'),
        createHash('sha512').update(bytes).digest('base64'),
      );
      writeFileSync(
        join(directory, '.nupkg.metadata'),
        JSON.stringify({ contentHash, source: SOLUS_FEED }),
      );
      assets.libraries[id + '/' + SOLUS_VERSION] = {
        type: 'package',
        path: id + '/' + SOLUS_VERSION,
        sha512: contentHash,
      };
      return {
        id,
        version: SOLUS_VERSION,
        sha256: createHash('sha256').update(bytes).digest('hex'),
        contentHash,
        role: 'managed',
      };
    });
    assets.targets = {
      'net10.0': Object.fromEntries(
        SOLUS_IDS.map((id) => [id + '/' + SOLUS_VERSION, { type: 'package' }]),
      ),
    };
    const policy = { dependencies, solusVersion: SOLUS_VERSION };
    await expect(verifyDependencies(root, policy, assets)).resolves.toEqual(dependencies);
    const metadata = join(root, SOLUS_IDS[0], SOLUS_VERSION, '.nupkg.metadata');
    writeFileSync(
      metadata,
      JSON.stringify({
        contentHash: dependencies[0].contentHash,
        source: 'https://other.invalid/index.json',
      }),
    );
    await expect(verifyDependencies(root, policy, assets)).rejects.toThrow(
      'dependency_source_mismatch',
    );
    writeFileSync(
      metadata,
      JSON.stringify({ contentHash: dependencies[0].contentHash, source: SOLUS_FEED }),
    );
    for (const dependency of dependencies)
      writeFileSync(
        join(root, dependency.id, SOLUS_VERSION, '.nupkg.metadata'),
        JSON.stringify({
          contentHash: dependency.contentHash,
          source: join(root, 'verified-seed'),
        }),
      );
    await expect(verifyDependencies(root, policy, assets)).rejects.toThrow(
      'dependency_source_mismatch',
    );
    await expect(
      verifyDependencies(root, policy, assets, join(root, 'verified-seed')),
    ).resolves.toEqual(dependencies);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
