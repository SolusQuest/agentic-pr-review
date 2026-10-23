import type { Duplex } from 'node:stream';
import { decodeUploadJson, encodeUploadFrame, UPLOAD_LIMITS } from './upload-protocol.js';

/** EOF is the request boundary. The peer must retain its read half for the reply. */
export async function readUploadFrame(stream: Duplex, signal: AbortSignal): Promise<unknown> {
  if (signal.aborted) throw new Error('upload_frame_cancelled');
  return await new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    let size = 0;
    let expected: number | undefined;
    let finished = false;
    const cleanup = (): void => {
      stream.off('data', data);
      stream.off('end', end);
      stream.off('error', fail);
      stream.off('close', fail);
      signal.removeEventListener('abort', fail);
      chunks.length = 0;
    };
    const fail = (): void => {
      if (finished) return;
      finished = true;
      cleanup();
      reject(new Error('upload_frame_invalid'));
    };
    const data = (chunk: Buffer): void => {
      size += chunk.length;
      if (size > UPLOAD_LIMITS.documentBytes + 4 || (expected !== undefined && size > expected))
        return fail();
      chunks.push(chunk);
      if (expected === undefined && size >= 4) {
        const prefix = Buffer.concat(chunks, size);
        const length = prefix.readUInt32BE();
        if (length === 0 || length > UPLOAD_LIMITS.documentBytes) return fail();
        expected = length + 4;
        if (size > expected) fail();
      }
    };
    const end = (): void => {
      if (finished) return;
      if (expected === undefined || size !== expected) return fail();
      try {
        const result = decodeUploadJson(Buffer.concat(chunks, size).subarray(4));
        finished = true;
        cleanup();
        resolve(result);
      } catch {
        fail();
      }
    };
    stream.on('data', data);
    stream.once('end', end);
    stream.once('error', fail);
    stream.once('close', fail);
    signal.addEventListener('abort', fail, { once: true });
  });
}

export async function writeUploadFrame(stream: Duplex, value: unknown): Promise<void> {
  const frame = encodeUploadFrame(value);
  await new Promise<void>((resolve, reject) => {
    const fail = (): void => {
      cleanup();
      reject(new Error('upload_frame_write_failed'));
    };
    const cleanup = (): void => {
      stream.off('error', fail);
      stream.off('close', fail);
    };
    stream.once('error', fail);
    stream.once('close', fail);
    stream.end(frame, () => {
      cleanup();
      resolve();
    });
  });
}
