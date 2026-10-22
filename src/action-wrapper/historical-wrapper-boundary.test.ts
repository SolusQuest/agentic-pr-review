import { createConnection } from 'node:net';
import { once } from 'node:events';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { describe, expect, it } from 'vitest';
const {
  generateActionBundle,
  generateTrustedProofActionBundle,
  generateFrameworkFixtureActionBundle,
  generateHistoricalTrustedProofFixtureActionBundle,
} = await import(pathToFileURL(path.resolve('scripts/build-action.mjs')).href);
const { verifyS5FixtureWrapperDigest, sha256 } = await import(
  pathToFileURL(path.resolve('scripts/check-r4-e2p-managed-receipt-v2.mjs')).href
);
import { startArtifactBridgeRuntime } from './launcher/historical-bridge-runtime.fixture.js';
import { encodeCommandMessage } from './artifact-bridge/historical-framing.fixture.js';

describe('explicit historical wrapper boundary', () => {
  it('keeps ordinary and protected production bundles free of historical REST', async () => {
    for (const generate of [generateActionBundle, generateTrustedProofActionBundle]) {
      const bundle = await generate();
      expect(
        Object.keys(bundle.metafile.inputs).filter((path) => path.includes('historical-')),
      ).toEqual([]);
    }
  });
  it('selects the historical wrapper only in the two named synthetic builders', async () => {
    for (const generate of [
      generateFrameworkFixtureActionBundle,
      generateHistoricalTrustedProofFixtureActionBundle,
    ]) {
      const bundle = await generate();
      const paths = Object.keys(bundle.metafile.inputs);
      expect(paths).toContain('src/action-wrapper/historical-wrapper.fixture.ts');
      expect(paths).toContain(
        'src/action-wrapper/artifact-bridge/historical-artifact-operations.fixture.ts',
      );
      expect(paths).toContain(
        'src/action-wrapper/artifact-bridge/historical-actions-rest-client.fixture.ts',
      );
      expect(paths).not.toContain('src/action-wrapper/index.ts');
    }
  });
  it('binds the managed fixture preparation receipt to the same historical bundle', async () => {
    const historical = await generateHistoricalTrustedProofFixtureActionBundle();
    await expect(
      verifyS5FixtureWrapperDigest(process.cwd(), sha256(historical.bytes)),
    ).resolves.toBeUndefined();
    const production = await generateTrustedProofActionBundle();
    await expect(
      verifyS5FixtureWrapperDigest(process.cwd(), sha256(production.bytes)),
    ).rejects.toThrow();
  });
  it.runIf(process.platform === 'linux')(
    'connects a historical producer to its historical result parser over real IPC',
    async () => {
      const bridge = await startArtifactBridgeRuntime({
        buildDiscriminator: 'historical-fixture',
        executorFactory: async () => ({
          execute: async (command) => ({
            operation: command.operation,
            correlation_id: command.correlation_id,
            failure: 'none',
            mutation_state: 'committed',
            metadata: {
              name: 'opaque',
              object_id: '1',
              producing_run_id: '2',
              producing_run_attempt: '1',
              archive_digest: '1'.repeat(64),
              encrypted_object_digest: '0'.repeat(64),
              expires_at_unix_seconds: '3',
              size: '1',
            },
          }),
        }),
      });
      try {
        const socket = createConnection(bridge.endpoint);
        const chunks: Buffer[] = [];
        socket.on('data', (chunk: Buffer) => chunks.push(chunk));
        const closed = once(socket, 'close');
        socket.write(
          encodeCommandMessage({
            build_discriminator: 'historical-fixture',
            payload: {
              operation: 'upload_immutable',
              correlation_id: 'history',
              name: 'opaque',
              source_relative_path: 'csharp/op/object.bin',
              encrypted_object_digest: '0'.repeat(64),
              minimum_expires_at_unix_seconds: '1',
            },
          }),
        );
        await closed;
        const bytes = Buffer.concat(chunks);
        expect(bytes.readUInt32BE(0)).toBe(bytes.length - 4);
        expect(JSON.parse(bytes.subarray(4).toString('utf8')).payload).toMatchObject({
          failure: 'none',
          mutation_state: 'committed',
          metadata: { object_id: '1' },
        });
      } finally {
        await bridge.stopAndDrain();
        await bridge.cleanup();
      }
    },
  );
});
