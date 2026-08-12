import { createHash } from 'node:crypto';
import { lstat, open, realpath, type FileHandle } from 'node:fs/promises';
import path from 'node:path';

import type { PreparedPayloadIdentity } from './contracts.js';
import { boundedStructure, buildDiscriminator, fail, lowerHex } from './validation.js';

export interface PreparedPayloadProof extends PreparedPayloadIdentity {
  readonly trustedRoot: string;
  readonly executableRelativePath: string;
  readonly wrapperBuildDiscriminator: string;
}

export interface VerifiedPreparedPayload extends PreparedPayloadIdentity {
  readonly executablePath: string;
}

export async function verifyPreparedPayload(
  proof: PreparedPayloadProof,
): Promise<VerifiedPreparedPayload> {
  if (
    !path.isAbsolute(proof.trustedRoot) ||
    !boundedStructure(proof.executableRelativePath, 1024) ||
    path.isAbsolute(proof.executableRelativePath) ||
    !lowerHex(proof.payloadSha256, 64) ||
    !lowerHex(proof.actionSourceSha, 40) ||
    !buildDiscriminator(proof.buildDiscriminator) ||
    !buildDiscriminator(proof.wrapperBuildDiscriminator) ||
    proof.buildDiscriminator !== proof.wrapperBuildDiscriminator
  ) {
    fail('wrapper_prepared_payload_invalid');
  }
  try {
    const rootStat = await lstat(proof.trustedRoot);
    if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) {
      fail('wrapper_prepared_payload_invalid');
    }
    const canonicalRoot = await realpath(proof.trustedRoot);
    const candidate = path.resolve(canonicalRoot, proof.executableRelativePath);
    const relative = path.relative(canonicalRoot, candidate);
    if (
      relative.length === 0 ||
      relative === '..' ||
      relative.startsWith(`..${path.sep}`) ||
      path.isAbsolute(relative)
    ) {
      fail('wrapper_prepared_payload_invalid');
    }
    const namedBefore = await lstat(candidate);
    if (
      !namedBefore.isFile() ||
      namedBefore.isSymbolicLink() ||
      (process.platform !== 'win32' && (namedBefore.mode & 0o111) === 0)
    ) {
      fail('wrapper_prepared_payload_invalid');
    }
    if (path.relative(candidate, await realpath(candidate)) !== '') {
      fail('wrapper_prepared_payload_invalid');
    }
    const handle = await open(candidate, 'r');
    try {
      const openedBefore = await handle.stat();
      if (!sameFile(namedBefore, openedBefore)) fail('wrapper_prepared_payload_invalid');
      const digest = await digestHandle(handle);
      const openedAfter = await handle.stat();
      const namedAfter = await lstat(candidate);
      if (
        digest !== proof.payloadSha256 ||
        !sameFile(openedBefore, openedAfter) ||
        !sameFile(openedAfter, namedAfter) ||
        openedBefore.size !== openedAfter.size ||
        openedBefore.mtimeMs !== openedAfter.mtimeMs
      ) {
        fail('wrapper_prepared_payload_invalid');
      }
    } finally {
      await handle.close();
    }
    return {
      executablePath: candidate,
      actionSourceSha: proof.actionSourceSha,
      payloadSha256: proof.payloadSha256,
      buildDiscriminator: proof.buildDiscriminator,
    };
  } catch (error) {
    if (error instanceof Error && error.name === 'ActionWrapperContractError') throw error;
    fail('wrapper_prepared_payload_invalid');
  }
}

export async function digestEventJson(eventJsonPath: string): Promise<string> {
  try {
    const before = await lstat(eventJsonPath);
    if (!before.isFile() || before.isSymbolicLink()) fail('wrapper_event_json_invalid');
    const handle = await open(eventJsonPath, 'r');
    try {
      const openedBefore = await handle.stat();
      if (!sameFile(before, openedBefore)) fail('wrapper_event_json_invalid');
      const digest = await digestHandle(handle);
      const openedAfter = await handle.stat();
      const namedAfter = await lstat(eventJsonPath);
      if (
        !sameFile(openedBefore, openedAfter) ||
        !sameFile(openedAfter, namedAfter) ||
        openedBefore.size !== openedAfter.size ||
        openedBefore.mtimeMs !== openedAfter.mtimeMs
      ) {
        fail('wrapper_event_json_invalid');
      }
      return digest;
    } finally {
      await handle.close();
    }
  } catch (error) {
    if (error instanceof Error && error.name === 'ActionWrapperContractError') throw error;
    fail('wrapper_event_json_invalid');
  }
}

async function digestHandle(handle: FileHandle): Promise<string> {
  const hash = createHash('sha256');
  const stream = handle.createReadStream({ autoClose: false, start: 0 });
  for await (const chunk of stream) hash.update(chunk as Buffer);
  return hash.digest('hex');
}

function sameFile(
  left: { readonly dev: number; readonly ino: number },
  right: { readonly dev: number; readonly ino: number },
): boolean {
  return left.ino === right.ino && (process.platform === 'win32' || left.dev === right.dev);
}
