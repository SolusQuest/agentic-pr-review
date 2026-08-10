import net, { type Server } from 'node:net';
import type { Duplex } from 'node:stream';

import {
  type ArtifactBridgeCommand,
  type ArtifactBridgeResult,
  isValidArtifactBridgeResult,
} from './contracts.js';
import { ArtifactBridgeCorrelationRegistry } from './correlations.js';
import { readCommandFrame, writeResultFrame } from './framing.js';
import { ARTIFACT_BRIDGE_LIMITS } from './limits.js';
import type { ArtifactBridgeExecutor } from './official-artifact-operations.js';

export interface ArtifactBridgeServerOptions {
  readonly endpoint: string;
  readonly buildDiscriminator: string;
  readonly executor: ArtifactBridgeExecutor;
  readonly correlations?: ArtifactBridgeCorrelationRegistry;
}

export async function handleArtifactBridgeConnection(
  stream: Duplex,
  options: Omit<ArtifactBridgeServerOptions, 'endpoint'>,
): Promise<void> {
  const outer = new AbortController();
  const outerTimer = setTimeout(
    () => outer.abort(),
    ARTIFACT_BRIDGE_LIMITS.logicalOperationTimeoutMs,
  );
  outerTimer.unref();
  const disconnect = (): void => outer.abort();
  stream.once('close', disconnect);
  const registry = options.correlations ?? new ArtifactBridgeCorrelationRegistry();
  let command: ArtifactBridgeCommand | undefined;
  let admitted = false;
  try {
    const readController = innerController(outer.signal);
    const envelope = await (async () => {
      try {
        return await readCommandFrame(stream, readController.signal);
      } finally {
        readController.dispose();
      }
    })();
    command = envelope.payload;
    if (envelope.build_discriminator !== options.buildDiscriminator) {
      await writeInvalid(stream, options.buildDiscriminator, command, outer.signal);
      return;
    }
    const admission = registry.admit(command.correlation_id);
    if (!admission.accepted) {
      await writeInvalid(stream, options.buildDiscriminator, command, outer.signal);
      return;
    }
    admitted = true;
    let result = await options.executor.execute(command, outer.signal);
    if (
      !isValidArtifactBridgeResult(result) ||
      result.operation !== command.operation ||
      result.correlation_id !== command.correlation_id
    ) {
      result = invalidResult(command);
    }
    const writeController = innerController(outer.signal);
    try {
      await writeResultFrame(
        stream,
        {
          build_discriminator: options.buildDiscriminator,
          payload: result,
        },
        writeController.signal,
      );
    } finally {
      writeController.dispose();
    }
  } catch {
    stream.destroy();
  } finally {
    if (admitted && command) registry.complete(command.correlation_id);
    clearTimeout(outerTimer);
    stream.off('close', disconnect);
  }
}

export function createArtifactBridgeServer(options: ArtifactBridgeServerOptions): Server {
  const correlations = options.correlations ?? new ArtifactBridgeCorrelationRegistry();
  return net.createServer((socket) => {
    void handleArtifactBridgeConnection(socket, {
      buildDiscriminator: options.buildDiscriminator,
      executor: options.executor,
      correlations,
    });
  });
}

function invalidResult(command: ArtifactBridgeCommand): ArtifactBridgeResult {
  const mutation =
    command.operation === 'upload_immutable' || command.operation === 'delete_exact'
      ? { mutation_state: 'not_committed' as const }
      : {};
  return {
    operation: command.operation,
    correlation_id: command.correlation_id,
    failure: 'invalid',
    ...mutation,
    ...(command.operation === 'list_exact' ? { complete: false } : {}),
  };
}

async function writeInvalid(
  stream: Duplex,
  buildDiscriminator: string,
  command: ArtifactBridgeCommand,
  outerSignal: AbortSignal,
): Promise<void> {
  const controller = innerController(outerSignal);
  try {
    await writeResultFrame(
      stream,
      {
        build_discriminator: buildDiscriminator,
        payload: invalidResult(command),
      },
      controller.signal,
    );
  } finally {
    controller.dispose();
  }
}

function innerController(outerSignal: AbortSignal): {
  readonly signal: AbortSignal;
  readonly dispose: () => void;
} {
  const controller = new AbortController();
  const abort = (): void => controller.abort();
  outerSignal.addEventListener('abort', abort, { once: true });
  const timer = setTimeout(abort, ARTIFACT_BRIDGE_LIMITS.requestTimeoutMs);
  timer.unref();
  return {
    signal: controller.signal,
    dispose: () => {
      clearTimeout(timer);
      outerSignal.removeEventListener('abort', abort);
    },
  };
}
