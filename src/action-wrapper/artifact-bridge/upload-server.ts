import net, { type Server, type Socket } from 'node:net';
import { readUploadFrame, writeUploadFrame } from './upload-framing.js';
import {
  boundedUploadText,
  isUploadReply,
  isUploadRequest,
  UPLOAD_LIMITS,
  UPLOAD_PROTOCOL,
  type UploadReply,
  type UploadRequest,
} from './upload-protocol.js';

export interface UploadExecutor {
  execute(request: UploadRequest, signal: AbortSignal): Promise<UploadReply>;
  sealAdmission(): void;
  awaitQuiescence(timeoutMs: number): Promise<boolean>;
  dispose(): Promise<void>;
}

export function createUploadServer(options: {
  readonly endpoint: string;
  readonly buildDiscriminator: string;
  readonly executor: UploadExecutor;
}): Server {
  const seen = new Set<string>();
  let saturated = false;
  const handle = async (socket: Socket): Promise<void> => {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), UPLOAD_LIMITS.timeoutMs);
    const disconnect = (): void => controller.abort();
    socket.once('close', disconnect);
    try {
      const value = await readUploadFrame(
        socket,
        AbortSignal.any([controller.signal, AbortSignal.timeout(UPLOAD_LIMITS.observationMs)]),
      );
      const correlation: unknown =
        typeof value === 'object' && value !== null
          ? (value as Record<string, unknown>).correlation_id
          : undefined;
      if (!boundedUploadText(correlation, 256)) throw new Error('upload_identity_invalid');
      const invalid: UploadReply = {
        protocol: UPLOAD_PROTOCOL,
        build_discriminator: options.buildDiscriminator,
        correlation_id: correlation,
        dispatch: 'not_dispatched',
        failure: 'invalid',
        receipt: null,
      };
      if (saturated || seen.has(correlation)) {
        await writeUploadFrame(socket, invalid);
        return;
      }
      seen.add(correlation);
      if (seen.size >= UPLOAD_LIMITS.terminalCorrelations) saturated = true;
      // Even an identifiable invalid request consumes its invocation correlation.
      if (!isUploadRequest(value) || value.build_discriminator !== options.buildDiscriminator) {
        await writeUploadFrame(socket, invalid);
        return;
      }
      const reply = await options.executor.execute(value, controller.signal);
      if (
        !isUploadReply(reply) ||
        reply.build_discriminator !== options.buildDiscriminator ||
        reply.correlation_id !== correlation
      )
        throw new Error('upload_reply_invalid');
      await writeUploadFrame(socket, reply);
    } catch {
      socket.destroy();
    } finally {
      clearTimeout(timer);
      socket.off('close', disconnect);
    }
  };
  return net.createServer({ allowHalfOpen: true }, (socket) => {
    void handle(socket);
  });
}
