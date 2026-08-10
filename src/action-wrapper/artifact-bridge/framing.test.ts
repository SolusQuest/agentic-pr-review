import { PassThrough } from 'node:stream';
import { describe, expect, it } from 'vitest';

import { ArtifactBridgeCorrelationRegistry } from './correlations.js';
import {
  ArtifactBridgeFrameError,
  decodeCommandFrame,
  encodeJsonFrame,
  readCommandFrame,
} from './framing.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';

function command() {
  return {
    build_discriminator: 'build-1',
    payload: {
      operation: 'list_exact',
      correlation_id: 'correlation-1',
      name: 'state',
      maximum_objects: '8',
    },
  };
}

describe('artifact bridge framing', () => {
  it('reads a header and body fragmented at every byte', async () => {
    const input = new PassThrough();
    const controller = new AbortController();
    const reading = readCommandFrame(input, controller.signal);
    for (const byte of encodeJsonFrame(command())) input.write(Buffer.of(byte));
    await expect(reading).resolves.toEqual(command());
  });

  it('rejects trailing, truncated, zero, oversized, BOM, and invalid UTF-8 frames', () => {
    const valid = encodeJsonFrame(command());
    expect(() => decodeCommandFrame(Buffer.concat([valid, Buffer.of(0)]))).toThrow(
      ArtifactBridgeFrameError,
    );
    expect(() => decodeCommandFrame(valid.subarray(0, -1))).toThrow(ArtifactBridgeFrameError);
    expect(() => decodeCommandFrame(Buffer.alloc(4))).toThrow(ArtifactBridgeFrameError);
    const oversized = Buffer.alloc(4);
    oversized.writeUInt32BE(ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes + 1);
    expect(() => decodeCommandFrame(oversized)).toThrow(ArtifactBridgeFrameError);
    const bomPayload = Buffer.concat([
      Buffer.from([0xef, 0xbb, 0xbf]),
      Buffer.from(JSON.stringify(command())),
    ]);
    const bomFrame = Buffer.alloc(4 + bomPayload.length);
    bomFrame.writeUInt32BE(bomPayload.length);
    bomPayload.copy(bomFrame, 4);
    expect(() => decodeCommandFrame(bomFrame)).toThrow(ArtifactBridgeFrameError);
    const invalid = Buffer.from(valid);
    invalid[4] = 0xff;
    expect(() => decodeCommandFrame(invalid)).toThrow(ArtifactBridgeFrameError);
  });

  it('accepts the 256 KiB document boundary and rejects cap plus one', () => {
    const overhead = Buffer.byteLength(JSON.stringify({ value: '' }), 'utf8');
    const atCap = { value: 'x'.repeat(ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes - overhead) };
    expect(encodeJsonFrame(atCap)).toHaveLength(ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes + 4);
    expect(() => encodeJsonFrame({ value: `${atCap.value}x` })).toThrow(ArtifactBridgeFrameError);
  });

  it('rejects duplicate JSON keys without reflecting the key', () => {
    const json = '{"build_discriminator":"build-1","build_discriminator":"canary","payload":{}}';
    const payload = Buffer.from(json);
    const frame = Buffer.alloc(4 + payload.length);
    frame.writeUInt32BE(payload.length);
    payload.copy(frame, 4);
    let error: unknown;
    try {
      decodeCommandFrame(frame);
    } catch (caught) {
      error = caught;
    }
    expect(error).toBeInstanceOf(ArtifactBridgeFrameError);
    expect(String(error)).not.toContain('canary');
    expect(String(error)).not.toContain('build_discriminator');
  });
});

describe('artifact bridge correlation registry', () => {
  it('rejects active and completed duplicates', () => {
    const registry = new ArtifactBridgeCorrelationRegistry();
    expect(registry.admit('one')).toEqual({ accepted: true });
    expect(registry.admit('one')).toEqual({
      accepted: false,
      reason: 'duplicate',
    });
    registry.complete('one');
    expect(registry.admit('one')).toEqual({
      accepted: false,
      reason: 'duplicate',
    });
  });

  it('fails closed at the active and terminal capacity', () => {
    const active = new ArtifactBridgeCorrelationRegistry();
    for (let index = 0; index < ARTIFACT_BRIDGE_LIMITS.maximumActiveCorrelations; index += 1) {
      expect(active.admit(`active-${index}`).accepted).toBe(true);
    }
    expect(active.admit('active-over')).toEqual({
      accepted: false,
      reason: 'saturated',
    });

    const terminal = new ArtifactBridgeCorrelationRegistry();
    for (let index = 0; index < ARTIFACT_BRIDGE_LIMITS.maximumTerminalCorrelations; index += 1) {
      expect(terminal.admit(`terminal-${index}`).accepted).toBe(true);
      terminal.complete(`terminal-${index}`);
    }
    expect(terminal.admit('terminal-over')).toEqual({
      accepted: false,
      reason: 'saturated',
    });
  });
});
