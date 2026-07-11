import { constants as fsConstants } from 'node:fs';
import { access, lstat, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createHash } from 'node:crypto';
import os from 'node:os';
import path from 'node:path';
import { RuntimeInvocationError } from './runtime-errors.js';

/**
 * Byte budgets for the three protocol files (see #33 D10). These are host-side safety
 * bounds; they are not part of the public protocol and may change based on evidence.
 */
export const BYTE_LIMITS = {
  input: 64 * 1024 * 1024,
  result: 8 * 1024 * 1024,
  trace: 4 * 1024 * 1024,
} as const;

/**
 * Filesystem seams. Tests inject alternate implementations to exercise
 * host-io/cleanup failure kinds without needing the real filesystem to misbehave.
 */
export interface FsSeams {
  mkdtemp: typeof mkdtemp;
  writeFile: typeof writeFile;
  lstat: typeof lstat;
  readFile: typeof readFile;
  access: typeof access;
  rm: typeof rm;
}

export const defaultFsSeams: FsSeams = {
  mkdtemp,
  writeFile,
  lstat,
  readFile,
  access,
  rm,
};

/**
 * Serialize a ReviewInputV1 (or any host-owned JSON value) using the repository's
 * existing pretty-JSON convention: `${JSON.stringify(value, null, 2)}\n`, UTF-8.
 * Called exactly once per invocation; the same bytes are hashed and written to disk.
 * See #33 D5.
 */
export function serializeInputBytes(value: unknown): Uint8Array {
  const text = `${JSON.stringify(value, null, 2)}\n`;
  return Buffer.from(text, 'utf8');
}

/**
 * Lowercase-hex SHA-256 of the exact bytes provided. No canonicalization occurs here.
 */
export function sha256Hex(bytes: Uint8Array): string {
  return createHash('sha256').update(bytes).digest('hex');
}

/**
 * Create a fresh invocation directory under `tempRoot`. Directory names use the
 * `agentic-pr-review-runtime-` prefix documented in #33 D6.
 */
export async function createInvocationDir(
  tempRoot: string | undefined,
  seams: FsSeams,
): Promise<string> {
  const base = tempRoot ?? os.tmpdir();
  try {
    return await seams.mkdtemp(path.join(base, 'agentic-pr-review-runtime-'));
  } catch (cause) {
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: 'Failed to create runtime invocation directory.',
      cause,
    });
  }
}

/**
 * Write the pre-serialized input bytes to `input.json`. Any filesystem failure is
 * mapped to `host-io-failed` so it never masquerades as an input contract error.
 */
export async function writeInputFile(
  invocationDir: string,
  inputBytes: Uint8Array,
  seams: FsSeams,
): Promise<string> {
  const inputPath = path.join(invocationDir, 'input.json');
  try {
    await seams.writeFile(inputPath, inputBytes);
    return inputPath;
  } catch (cause) {
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: 'Failed to materialize input.json.',
      cause,
    });
  }
}

export type SafeFileCheckKind = 'result' | 'trace';

export interface SafeFileCheckOptions {
  /**
   * When true (non-zero exit / failure-trace path), missing/unsafe/host-io failures
   * are surfaced by returning null so the caller can silently omit failure-trace
   * diagnostics. When false (exit 0 path), failures raise RuntimeInvocationError.
   */
  silentOnFailure: boolean;
}

/**
 * Validate one of `result.json` / `trace.json` before reading it. See #33 D11 file safety.
 * Steps: lstat -> ENOENT -> missing-output; other errors -> host-io-failed; symlink or
 * non-regular -> unsafe-output-file; size cap -> unsafe-output-file.
 */
export async function statSafeOutputFile(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: { silentOnFailure: false },
): Promise<{ size: number }>;
export async function statSafeOutputFile(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: { silentOnFailure: true },
): Promise<{ size: number } | null>;
export async function statSafeOutputFile(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: SafeFileCheckOptions,
): Promise<{ size: number } | null> {
  let stat;
  try {
    stat = await seams.lstat(fullPath);
  } catch (cause) {
    const nodeCause = cause as NodeJS.ErrnoException;
    if (nodeCause && nodeCause.code === 'ENOENT') {
      if (options.silentOnFailure) return null;
      throw new RuntimeInvocationError({
        kind: 'missing-output',
        message: `Runtime exited with code 0 but ${fileKind}.json was not produced.`,
      });
    }
    if (options.silentOnFailure) return null;
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: `Failed to inspect ${fileKind}.json.`,
      cause,
    });
  }
  if (stat.isSymbolicLink() || !stat.isFile()) {
    if (options.silentOnFailure) return null;
    throw new RuntimeInvocationError({
      kind: 'unsafe-output-file',
      message: `${fileKind}.json is not a regular file.`,
    });
  }
  const cap = fileKind === 'result' ? BYTE_LIMITS.result : BYTE_LIMITS.trace;
  if (stat.size > cap) {
    if (options.silentOnFailure) return null;
    throw new RuntimeInvocationError({
      kind: 'unsafe-output-file',
      message: `${fileKind}.json exceeds the host byte cap (${stat.size} > ${cap}).`,
    });
  }
  return { size: stat.size };
}

/**
 * Read output/trace bytes into memory after file-safety has passed. Host filesystem
 * failures during the read map to host-io-failed on the success path or null on the
 * failure-trace path.
 */
export async function readSafeOutputBytes(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: { silentOnFailure: false },
): Promise<Uint8Array>;
export async function readSafeOutputBytes(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: { silentOnFailure: true },
): Promise<Uint8Array | null>;
export async function readSafeOutputBytes(
  fileKind: SafeFileCheckKind,
  fullPath: string,
  seams: FsSeams,
  options: SafeFileCheckOptions,
): Promise<Uint8Array | null> {
  try {
    const buf = await seams.readFile(fullPath);
    return new Uint8Array(buf.buffer, buf.byteOffset, buf.byteLength);
  } catch (cause) {
    if (options.silentOnFailure) return null;
    throw new RuntimeInvocationError({
      kind: 'host-io-failed',
      message: `Failed to read ${fileKind}.json.`,
      cause,
    });
  }
}

/**
 * Strict UTF-8 decode. Throws when the buffer contains invalid UTF-8 sequences,
 * so callers can classify decode failures as `result-invalid` / `trace-invalid`.
 */
export function decodeStrictUtf8(bytes: Uint8Array): string {
  return new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(bytes);
}

/**
 * Return true if the running process can execute the target file. Used only to fail
 * fast; the child spawn itself remains the authoritative check.
 */
export async function isExecutableFile(fullPath: string, seams: FsSeams): Promise<boolean> {
  try {
    const stat = await seams.lstat(fullPath);
    if (stat.isSymbolicLink() || !stat.isFile()) return false;
  } catch {
    return false;
  }
  if (process.platform === 'win32') return true;
  try {
    await seams.access(fullPath, fsConstants.X_OK);
    return true;
  } catch {
    return false;
  }
}
