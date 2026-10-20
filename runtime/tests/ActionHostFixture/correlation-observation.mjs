import { constants, closeSync, fstatSync, lstatSync, openSync, readSync } from 'node:fs';
import { join } from 'node:path';

// Test diagnostics only. Absence or malformed observations are unknown and never
// change the actual installed-package operation or qualification verdict.
export function readCorrelationObservation(controlRoot) {
  let descriptor;
  try {
    const directory = lstatSync(controlRoot);
    if (!directory.isDirectory() || directory.isSymbolicLink()) return null;
    descriptor = openSync(
      join(controlRoot, 'correlation-observation.json'),
      constants.O_RDONLY | constants.O_NOFOLLOW | constants.O_NONBLOCK,
    );
    const info = fstatSync(descriptor);
    if (!info.isFile() || info.size < 1 || info.size > 128) return null;
    const bytes = Buffer.alloc(129);
    const length = readSync(descriptor, bytes, 0, bytes.length, 0);
    if (length < 1 || length > 128) return null;
    const text = new TextDecoder('utf-8', { fatal: true }).decode(bytes.subarray(0, length));
    const value = JSON.parse(text);
    if (
      value === null ||
      Array.isArray(value) ||
      typeof value !== 'object' ||
      Object.keys(value).join(',') !== 'active,terminal' ||
      !Number.isSafeInteger(value.active) ||
      value.active < 0 ||
      value.active > 32 ||
      !Number.isSafeInteger(value.terminal) ||
      value.terminal < 0 ||
      value.terminal > 4096 ||
      JSON.stringify(value) !== text
    ) {
      return null;
    }
    return { active: value.active, terminal: value.terminal };
  } catch {
    return null;
  } finally {
    if (descriptor !== undefined) closeSync(descriptor);
  }
}
