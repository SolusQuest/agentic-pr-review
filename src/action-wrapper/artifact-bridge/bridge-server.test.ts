import { Duplex } from 'node:stream';
import { describe, expect, it } from 'vitest';

import { handleArtifactBridgeConnection } from './bridge-server.js';
import type { ArtifactBridgeCommandEnvelope, ArtifactBridgeResult } from './contracts.js';
import { ArtifactBridgeCorrelationRegistry } from './correlations.js';
import { encodeCommandMessage, encodeJsonFrame } from './framing.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';
import type { ArtifactBridgeExecutor } from './official-artifact-operations.js';

const buildDiscriminator = 'build-1';

function uploadEnvelope(correlationId: string): ArtifactBridgeCommandEnvelope {
  return {
    build_discriminator: buildDiscriminator,
    payload: {
      operation: 'upload_immutable',
      correlation_id: correlationId,
      name: 'opaque-state',
      source_relative_path: 'csharp/op/object.bin',
      encrypted_object_digest: '0'.repeat(64),
      minimum_expires_at_unix_seconds: '1',
    },
  };
}

function uploadResult(
  correlationId: string,
  mutationState: 'committed' | 'outcome_unknown',
): ArtifactBridgeResult {
  return {
    operation: 'upload_immutable',
    correlation_id: correlationId,
    failure: mutationState === 'committed' ? 'none' : 'outcome_unknown',
    mutation_state: mutationState,
    ...(mutationState === 'committed'
      ? {
          metadata: {
            name: 'opaque-state',
            object_id: '1',
            producing_run_id: '2',
            producing_run_attempt: '1',
            archive_digest: '1'.repeat(64),
            encrypted_object_digest: '0'.repeat(64),
            expires_at_unix_seconds: '3',
            size: '1',
          },
        }
      : {}),
  };
}

describe('artifact bridge mutation admission', () => {
  it('maps active and terminal mutation duplicates to outcome unknown', async () => {
    const registry = new ArtifactBridgeCorrelationRegistry();
    let releaseFirst!: () => void;
    const firstGate = new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });
    let calls = 0;
    const executor = {
      execute: async () => {
        calls += 1;
        await firstGate;
        return uploadResult('duplicate', 'committed');
      },
    };

    const first = startExchange(uploadEnvelope('duplicate'), executor, registry);
    await waitFor(() => calls === 1);
    const activeDuplicate = await exchange(uploadEnvelope('duplicate'), executor, registry);
    expect(activeDuplicate).toMatchObject({
      failure: 'outcome_unknown',
      mutation_state: 'outcome_unknown',
    });
    expect(calls).toBe(1);

    releaseFirst();
    await first;
    const terminalDuplicate = await exchange(uploadEnvelope('duplicate'), executor, registry);
    expect(terminalDuplicate).toMatchObject({
      failure: 'outcome_unknown',
      mutation_state: 'outcome_unknown',
    });
    expect(calls).toBe(1);
  });

  it('keeps outcome-unknown terminals ambiguous and saturation not committed', async () => {
    const registry = new ArtifactBridgeCorrelationRegistry();
    let calls = 0;
    const executor = {
      execute: async () => {
        calls += 1;
        return uploadResult('ambiguous', 'outcome_unknown');
      },
    };
    await exchange(uploadEnvelope('ambiguous'), executor, registry);
    const duplicate = await exchange(uploadEnvelope('ambiguous'), executor, registry);
    expect(duplicate).toMatchObject({
      failure: 'outcome_unknown',
      mutation_state: 'outcome_unknown',
    });
    expect(calls).toBe(1);

    const saturated = new ArtifactBridgeCorrelationRegistry();
    for (let index = 0; index < ARTIFACT_BRIDGE_LIMITS.maximumActiveCorrelations; index += 1) {
      expect(saturated.admit(`occupied-${index}`).accepted).toBe(true);
    }
    const rejected = await exchange(uploadEnvelope('saturated'), executor, saturated);
    expect(rejected).toMatchObject({
      failure: 'invalid',
      mutation_state: 'not_committed',
    });
    expect(calls).toBe(1);
  });

  it('maps a malformed post-dispatch mutation result to outcome unknown', async () => {
    const result = await exchange(uploadEnvelope('malformed'), {
      execute: async () => uploadResult('wrong-correlation', 'committed'),
    });
    expect(result).toMatchObject({
      failure: 'outcome_unknown',
      mutation_state: 'outcome_unknown',
    });
  });

  it('does not dispatch a mutation before the request boundary', async () => {
    const [client, server] = duplexPair();
    let calls = 0;
    const handling = handleArtifactBridgeConnection(server, {
      buildDiscriminator,
      executor: {
        execute: async () => {
          calls += 1;
          return uploadResult('delayed-trailing', 'committed');
        },
      },
    });
    client.write(encodeJsonFrame(uploadEnvelope('delayed-trailing')));
    await new Promise<void>((resolve) => setImmediate(resolve));
    client.end(Buffer.of(1));
    await handling;
    expect(calls).toBe(0);
  });
});

async function exchange(
  envelope: ArtifactBridgeCommandEnvelope,
  executor: ArtifactBridgeExecutor,
  correlations?: ArtifactBridgeCorrelationRegistry,
): Promise<ArtifactBridgeResult> {
  return await startExchange(envelope, executor, correlations);
}

async function startExchange(
  envelope: ArtifactBridgeCommandEnvelope,
  executor: ArtifactBridgeExecutor,
  correlations?: ArtifactBridgeCorrelationRegistry,
): Promise<ArtifactBridgeResult> {
  const [client, server] = duplexPair();
  const handling = handleArtifactBridgeConnection(server, {
    buildDiscriminator,
    executor,
    correlations,
  });
  const response = collect(client);
  client.end(encodeCommandMessage(envelope));
  await handling;
  const bytes = await response;
  const length = bytes.readUInt32BE(0);
  const parsed = JSON.parse(bytes.subarray(4, 4 + length).toString('utf8')) as {
    readonly payload: ArtifactBridgeResult;
  };
  return parsed.payload;
}

function duplexPair(): readonly [Duplex, Duplex] {
  const first = new MemoryDuplex();
  const second = new MemoryDuplex();
  first.peer = second;
  second.peer = first;
  return [first, second];
}

class MemoryDuplex extends Duplex {
  peer?: MemoryDuplex;

  override _read(): void {}

  override _write(
    chunk: Buffer,
    _encoding: BufferEncoding,
    callback: (error?: Error | null) => void,
  ): void {
    this.peer?.push(Buffer.from(chunk));
    callback();
  }

  override _final(callback: (error?: Error | null) => void): void {
    this.peer?.push(null);
    callback();
  }
}

async function collect(stream: Duplex): Promise<Buffer> {
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(Buffer.from(chunk));
  return Buffer.concat(chunks);
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setImmediate(resolve));
  }
  throw new Error('condition_not_reached');
}
