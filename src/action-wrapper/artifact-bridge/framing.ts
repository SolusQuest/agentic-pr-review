import { once } from 'node:events';
import type { Duplex } from 'node:stream';

import {
  type ArtifactBridgeCommandEnvelope,
  type ArtifactBridgeResultEnvelope,
  isValidArtifactBridgeResult,
  parseArtifactBridgeCommandEnvelope,
} from './contracts.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';
import { strictParseArtifactBridgeJson } from './strict-json.js';

const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false });
const requestTerminatorBytes = 4;

export class ArtifactBridgeFrameError extends Error {
  constructor() {
    super('artifact_bridge_frame_invalid');
    this.name = 'ArtifactBridgeFrameError';
  }
}

export function encodeResultFrame(envelope: ArtifactBridgeResultEnvelope): Buffer {
  if (!isValidArtifactBridgeResult(envelope.payload)) {
    throw new ArtifactBridgeFrameError();
  }
  return encodeJsonFrame(envelope);
}

export function encodeCommandMessage(envelope: ArtifactBridgeCommandEnvelope): Buffer {
  return Buffer.concat([encodeJsonFrame(envelope), Buffer.alloc(requestTerminatorBytes)]);
}

export function decodeCommandFrame(frame: Uint8Array): ArtifactBridgeCommandEnvelope {
  const bytes = decodePayload(frame);
  let parsed: unknown;
  try {
    if (bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) {
      throw new ArtifactBridgeFrameError();
    }
    const text = decoder.decode(bytes);
    parsed = strictParseArtifactBridgeJson(text);
  } catch {
    throw new ArtifactBridgeFrameError();
  }
  const envelope = parseArtifactBridgeCommandEnvelope(parsed);
  if (!envelope) throw new ArtifactBridgeFrameError();
  return envelope;
}

export async function readCommandFrame(
  stream: Duplex,
  signal: AbortSignal,
): Promise<ArtifactBridgeCommandEnvelope> {
  const frame = await readFrame(stream, signal);
  return decodeCommandFrame(frame);
}

export async function writeResultFrame(
  stream: Duplex,
  envelope: ArtifactBridgeResultEnvelope,
  signal: AbortSignal,
): Promise<void> {
  const frame = encodeResultFrame(envelope);
  if (signal.aborted) throw signal.reason;
  const accepted = stream.write(frame);
  if (!accepted) {
    await Promise.race([
      once(stream, 'drain'),
      abortPromise(signal),
      once(stream, 'error').then(([error]) => Promise.reject(error)),
    ]);
  }
  stream.end();
}

export function encodeJsonFrame(value: unknown): Buffer {
  let payload: Buffer;
  try {
    payload = Buffer.from(JSON.stringify(value), 'utf8');
  } catch {
    throw new ArtifactBridgeFrameError();
  }
  if (payload.length === 0 || payload.length > ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes) {
    throw new ArtifactBridgeFrameError();
  }
  const frame = Buffer.allocUnsafe(4 + payload.length);
  frame.writeUInt32BE(payload.length, 0);
  payload.copy(frame, 4);
  return frame;
}

function decodePayload(frame: Uint8Array): Uint8Array {
  if (frame.byteLength < 4) throw new ArtifactBridgeFrameError();
  const buffer = Buffer.from(frame.buffer, frame.byteOffset, frame.byteLength);
  const length = buffer.readUInt32BE(0);
  if (
    length === 0 ||
    length > ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes ||
    buffer.length !== 4 + length
  ) {
    throw new ArtifactBridgeFrameError();
  }
  return buffer.subarray(4);
}

async function readFrame(stream: Duplex, signal: AbortSignal): Promise<Buffer> {
  if (signal.aborted) throw signal.reason;
  return await new Promise<Buffer>((resolve, reject) => {
    let settled = false;
    let chunks: Buffer[] = [];
    let total = 0;
    let expected: number | undefined;
    let boundarySeen = false;

    const cleanup = (): void => {
      stream.off('data', onData);
      stream.off('end', onEnd);
      stream.off('error', onError);
      signal.removeEventListener('abort', onAbort);
      chunks = [];
    };
    const fail = (): void => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(new ArtifactBridgeFrameError());
    };
    const finish = (): void => {
      if (settled) return;
      if (expected === undefined) {
        fail();
        return;
      }
      settled = true;
      const result = Buffer.concat(chunks, total).subarray(0, expected);
      cleanup();
      resolve(result);
    };
    const onAbort = (): void => {
      if (settled) return;
      settled = true;
      cleanup();
      reject(signal.reason);
    };
    const onError = (): void => fail();
    const onEnd = (): void => {
      if (expected !== undefined && expected + requestTerminatorBytes === total) finish();
      else fail();
    };
    const onData = (chunk: Buffer): void => {
      if (boundarySeen) {
        fail();
        return;
      }
      total += chunk.length;
      if (total > ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes + 4 + requestTerminatorBytes) {
        fail();
        return;
      }
      chunks.push(chunk);
      if (expected === undefined && total >= 4) {
        const prefix = Buffer.concat(chunks, total);
        const length = prefix.readUInt32BE(0);
        if (length === 0 || length > ARTIFACT_BRIDGE_LIMITS.maximumDocumentBytes) {
          fail();
          return;
        }
        expected = 4 + length;
      }
      if (expected !== undefined && total > expected + requestTerminatorBytes) {
        fail();
      } else if (expected !== undefined && total === expected + requestTerminatorBytes) {
        const message = Buffer.concat(chunks, total);
        if (!message.subarray(expected).equals(Buffer.alloc(requestTerminatorBytes))) {
          fail();
        } else {
          boundarySeen = true;
          setImmediate(finish);
        }
      }
    };

    stream.on('data', onData);
    stream.once('end', onEnd);
    stream.once('error', onError);
    signal.addEventListener('abort', onAbort, { once: true });
  });
}

function abortPromise(signal: AbortSignal): Promise<never> {
  return new Promise((_, reject) => {
    if (signal.aborted) {
      reject(signal.reason);
      return;
    }
    signal.addEventListener('abort', () => reject(signal.reason), {
      once: true,
    });
  });
}
