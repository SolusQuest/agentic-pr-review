import { afterEach, describe, expect, it } from 'vitest';
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { once } from 'node:events';
import { connect } from 'node:net';
import { spawn } from 'node:child_process';
import { build } from 'esbuild';
import { createArtifactBridgeServer } from '../src/action-wrapper/artifact-bridge/bridge-server.js';
import { ArtifactBridgeCorrelationRegistry } from '../src/action-wrapper/artifact-bridge/correlations.js';
import { encodeCommandMessage } from '../src/action-wrapper/artifact-bridge/framing.js';
import { readCorrelationObservation } from '../runtime/tests/ActionHostFixture/correlation-observation.mjs';

const roots: string[] = [];
function root() {
  const value = mkdtempSync(join(tmpdir(), 'apr-correlation-observation-'));
  roots.push(value);
  return value;
}
afterEach(() => {
  for (const value of roots.splice(0)) rmSync(value, { force: true, recursive: true });
});

describe('synthetic bridge correlation diagnostics', () => {
  it('keeps real bridge results unchanged when the diagnostic file cannot be written', async () => {
    const directory = root();
    const control = join(directory, 'control');
    const staging = join(directory, 'staging');
    mkdirSync(control, { mode: 0o700 });
    mkdirSync(staging, { mode: 0o700 });
    mkdirSync(join(control, 'correlation-observation.json'));
    const bundle = join(directory, 'bridge.mjs');
    await build({
      entryPoints: ['src/action-wrapper/artifact-bridge/synthetic-fixture-server.ts'],
      outfile: bundle,
      bundle: true,
      platform: 'node',
      format: 'esm',
      target: 'node24',
    });
    const endpoint = join(directory, 'bridge.sock');
    const child = spawn(process.execPath, [bundle, endpoint, 'diagnostic-test', staging, control], {
      env: {},
      stdio: ['ignore', 'pipe', 'ignore'],
    });
    try {
      const [ready] = await once(child.stdout!, 'data', { signal: AbortSignal.timeout(3000) });
      expect(ready.toString()).toBe('READY\n');
      for (const correlation of ['first', 'after-diagnostic-failure']) {
        const socket = connect(endpoint);
        socket.write(
          encodeCommandMessage({
            build_discriminator: 'diagnostic-test',
            payload: {
              operation: 'list_exact',
              correlation_id: correlation,
              name: 'opaque-diagnostic',
              maximum_objects: '1',
            },
          }),
        );
        const chunks = [];
        for await (const chunk of socket) chunks.push(Buffer.from(chunk));
        const bytes = Buffer.concat(chunks);
        const result = JSON.parse(bytes.subarray(4, 4 + bytes.readUInt32BE(0)).toString('utf8'));
        expect(result.payload).toEqual({
          operation: 'list_exact',
          correlation_id: correlation,
          failure: 'none',
          complete: true,
          objects: [],
        });
      }
      expect(readCorrelationObservation(control)).toBeNull();
    } finally {
      if (child.exitCode === null && child.signalCode === null) {
        child.kill('SIGTERM');
        await once(child, 'exit');
      }
    }
  });

  it('preserves server admission and shared terminal lifetime with explicit default registry', async () => {
    const observations = [];
    for (const explicit of [false, true]) {
      const registry = new ArtifactBridgeCorrelationRegistry();
      let calls = 0;
      const server = createArtifactBridgeServer({
        endpoint: 'unused-tcp-test-endpoint',
        buildDiscriminator: 'diagnostic-test',
        ...(explicit ? { correlations: registry } : {}),
        executor: {
          async execute(command) {
            calls++;
            return {
              operation: 'list_exact',
              correlation_id: command.correlation_id,
              failure: 'none',
              complete: true,
              objects: [],
            };
          },
        },
      });
      server.listen(0, '127.0.0.1');
      await once(server, 'listening');
      try {
        const address = server.address();
        if (address === null || typeof address === 'string') throw new Error('test-address');
        const results = [];
        for (let index = 0; index < 2; index++) {
          const socket = connect(address.port, '127.0.0.1');
          socket.write(
            encodeCommandMessage({
              build_discriminator: 'diagnostic-test',
              payload: {
                operation: 'list_exact',
                correlation_id: 'shared-terminal',
                name: 'opaque-diagnostic',
                maximum_objects: '1',
              },
            }),
          );
          const chunks = [];
          for await (const chunk of socket) chunks.push(Buffer.from(chunk));
          const bytes = Buffer.concat(chunks);
          results.push(JSON.parse(bytes.subarray(4, 4 + bytes.readUInt32BE(0)).toString('utf8')));
        }
        expect(calls).toBe(1);
        expect(results[0].payload.failure).toBe('none');
        expect(results[1].payload.failure).toBe('invalid');
        if (explicit) {
          expect(registry.activeCount).toBe(0);
          expect(registry.terminalCount).toBe(1);
        }
        observations.push(results);
      } finally {
        await new Promise<void>((resolve, reject) =>
          server.close((error) => (error ? reject(error) : resolve())),
        );
      }
    }
    expect(observations[0]).toEqual(observations[1]);
  });

  it.each([
    { active: 0, terminal: 0 },
    { active: 32, terminal: 4096 },
    { active: 1, terminal: 4095 },
  ])('reads only the closed numeric snapshot %o', (value) => {
    const directory = root();
    writeFileSync(join(directory, 'correlation-observation.json'), JSON.stringify(value));
    expect(readCorrelationObservation(directory)).toEqual(value);
  });

  it.each([
    '',
    'null',
    '[]',
    '{',
    '{"active":0,"terminal":0,"secret":"private-canary"}',
    '{"active":"private-canary","terminal":0}',
    '{"active":0,"terminal":"4096"}',
    '{"active":false,"terminal":0}',
    '{"active":0.5,"terminal":0}',
    '{"active":-1,"terminal":0}',
    '{"active":33,"terminal":0}',
    '{"active":0,"terminal":4097}',
    '{"active":0,"terminal":-1}',
    '{"active":0,"terminal":null}',
    '{"active":0,"active":1,"terminal":0}',
    '{"active":0}',
    '{"terminal":0}',
    'x'.repeat(129),
  ])('treats invalid observation %j as unknown', (bytes) => {
    const directory = root();
    writeFileSync(join(directory, 'correlation-observation.json'), bytes);
    expect(readCorrelationObservation(directory)).toBeNull();
  });

  it('does not follow a leaf or control-directory symlink', () => {
    const directory = root();
    const target = root();
    const file = join(target, 'correlation-observation.json');
    writeFileSync(file, '{"active":0,"terminal":4096}');
    symlinkSync(file, join(directory, 'correlation-observation.json'));
    expect(readCorrelationObservation(directory)).toBeNull();
    symlinkSync(target, join(directory, 'linked'));
    expect(readCorrelationObservation(join(directory, 'linked'))).toBeNull();
  });

  it('reports missing, directory and malformed UTF-8 observations as unknown', () => {
    const directory = root();
    expect(readCorrelationObservation(directory)).toBeNull();
    expect(readCorrelationObservation(join(directory, 'missing'))).toBeNull();
    mkdirSync(join(directory, 'correlation-observation.json'));
    expect(readCorrelationObservation(directory)).toBeNull();
    rmSync(join(directory, 'correlation-observation.json'), { recursive: true });
    writeFileSync(join(directory, 'correlation-observation.json'), Buffer.from([0xc3, 0x28]));
    expect(readCorrelationObservation(directory)).toBeNull();
  });
});
