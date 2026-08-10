import { mkdir, readFile, rm } from 'node:fs/promises';
import path from 'node:path';

import { createArtifactBridgeServer } from './bridge-server.js';
import {
  type ArtifactBridgeCommand,
  type ArtifactBridgeResult,
  type ArtifactMetadataWire,
} from './contracts.js';
import type { ArtifactBridgeExecutor } from './official-artifact-operations.js';
import { ArtifactBridgeStaging } from './staging.js';
import { digestBytes } from './transport-envelope.js';

class SyntheticArtifactExecutor implements ArtifactBridgeExecutor {
  private readonly objects = new Map<
    string,
    { readonly metadata: ArtifactMetadataWire; readonly bytes: Buffer }
  >();
  private nextId = 1000;

  constructor(
    private readonly staging: ArtifactBridgeStaging,
    private readonly controlRoot: string,
  ) {}

  async execute(
    command: ArtifactBridgeCommand,
    signal: AbortSignal,
  ): Promise<ArtifactBridgeResult> {
    if (signal.aborted) throw signal.reason;
    switch (command.operation) {
      case 'list_exact': {
        if (await this.consume('stall-list')) {
          await waitForCancellation(signal);
        }
        const matches = [...this.objects.values()]
          .filter((item) => item.metadata.name === command.name)
          .map((item) => ({
            name: item.metadata.name,
            object_id: item.metadata.object_id,
          }));
        if (matches.length > Number(command.maximum_objects)) {
          return failure(command, 'incomplete', { complete: false });
        }
        if ((await this.consume('duplicate-list')) && matches.length > 0) {
          matches.push(matches[0]!);
        }
        return {
          operation: command.operation,
          correlation_id: command.correlation_id,
          failure: 'none',
          complete: true,
          objects: matches,
        };
      }
      case 'metadata': {
        const value = this.objects.get(command.object_id);
        return value && value.metadata.name === command.name
          ? success(command, value.metadata)
          : failure(command, 'not_found');
      }
      case 'download': {
        const value = this.objects.get(command.expected.object_id);
        if (!value) return failure(command, 'not_found');
        if (!sameMetadata(value.metadata, command.expected)) {
          return failure(command, 'conflict');
        }
        if (Number(value.metadata.expires_at_unix_seconds) <= (await this.now())) {
          return failure(command, 'expired');
        }
        if (value.bytes.length > Number(command.maximum_bytes)) {
          return failure(command, 'digest_mismatch');
        }
        await this.staging.writeDestination(command.destination_relative_path, value.bytes);
        return success(command, value.metadata);
      }
      case 'upload_immutable': {
        const bytes = await this.staging.readSource(command.source_relative_path);
        if (digestBytes(bytes) !== command.encrypted_object_digest) {
          return failure(command, 'invalid', {
            mutation_state: 'not_committed',
          });
        }
        const objectId = String(this.nextId++);
        const metadata: ArtifactMetadataWire = {
          name: command.name,
          object_id: objectId,
          producing_run_id: '7001',
          producing_run_attempt: '2',
          archive_digest: digestBytes(Buffer.concat([Buffer.from('synthetic-archive\0'), bytes])),
          encrypted_object_digest: command.encrypted_object_digest,
          expires_at_unix_seconds: String(Number(command.minimum_expires_at_unix_seconds) + 600),
          size: String(bytes.length),
        };
        this.objects.set(objectId, { metadata, bytes });
        if (await this.consume('may-commit-upload')) {
          return failure(command, 'io', {
            mutation_state: 'committed',
            metadata,
          });
        }
        return {
          ...success(command, metadata),
          mutation_state: 'committed',
        };
      }
      case 'readback_exact': {
        if (await this.consume('missing-readback')) {
          return failure(command, 'not_found');
        }
        const value = this.objects.get(command.expected.object_id);
        if (!value) return failure(command, 'not_found');
        return sameMetadata(value.metadata, command.expected)
          ? success(command, value.metadata)
          : failure(command, 'conflict');
      }
      case 'delete_exact': {
        const value = this.objects.get(command.expected.object_id);
        if (!value) {
          return failure(command, 'not_found', {
            mutation_state: 'not_committed',
          });
        }
        if (!sameMetadata(value.metadata, command.expected)) {
          return failure(command, 'conflict', {
            mutation_state: 'not_committed',
          });
        }
        if (await this.consume('unknown-delete')) {
          return failure(command, 'outcome_unknown', {
            mutation_state: 'outcome_unknown',
          });
        }
        this.objects.delete(command.expected.object_id);
        return {
          operation: command.operation,
          correlation_id: command.correlation_id,
          failure: 'none',
          mutation_state: 'committed',
        };
      }
    }
  }

  private async consume(name: string): Promise<boolean> {
    const target = path.join(this.controlRoot, name);
    try {
      await rm(target);
      return true;
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return false;
      throw error;
    }
  }

  private async now(): Promise<number> {
    try {
      const value = await readFile(path.join(this.controlRoot, 'now'), 'utf8');
      return Number(value);
    } catch (error) {
      if ((error as NodeJS.ErrnoException).code === 'ENOENT') return 0;
      throw error;
    }
  }
}

async function waitForCancellation(signal: AbortSignal): Promise<never> {
  if (signal.aborted) throw signal.reason;
  return await new Promise<never>((_resolve, reject) => {
    signal.addEventListener('abort', () => reject(signal.reason), { once: true });
  });
}

function success(
  command: ArtifactBridgeCommand,
  metadata: ArtifactMetadataWire,
): ArtifactBridgeResult {
  return {
    operation: command.operation,
    correlation_id: command.correlation_id,
    failure: 'none',
    metadata,
  };
}

function failure(
  command: ArtifactBridgeCommand,
  failureCode: ArtifactBridgeResult['failure'],
  extra: Partial<ArtifactBridgeResult> = {},
): ArtifactBridgeResult {
  return {
    operation: command.operation,
    correlation_id: command.correlation_id,
    failure: failureCode,
    ...extra,
  };
}

function sameMetadata(left: ArtifactMetadataWire, right: ArtifactMetadataWire): boolean {
  return JSON.stringify(left) === JSON.stringify(right);
}

async function main(): Promise<void> {
  const [endpoint, buildDiscriminator, stagingRoot, controlRoot] = process.argv.slice(2);
  if (!endpoint || !buildDiscriminator || !stagingRoot || !controlRoot) {
    process.exitCode = 2;
    return;
  }
  await mkdir(controlRoot, { recursive: true });
  const staging = await ArtifactBridgeStaging.create(stagingRoot);
  const executor = new SyntheticArtifactExecutor(staging, controlRoot);
  const server = createArtifactBridgeServer({
    endpoint,
    buildDiscriminator,
    executor,
  });
  server.once('error', () => {
    process.exitCode = 3;
  });
  server.listen(endpoint, () => {
    process.stdout.write('READY\n');
  });
}

void main();
