import {
  KNOWN_APR_CODES,
  type RuntimeContractViolation,
  type RuntimeExitClass,
} from './runtime-errors.js';
import { decodeStrictUtf8 } from './runtime-files.js';
import type { StreamCaptureResult } from './process-runner.js';

export const STDERR_CONTRACT_LIMIT = 1000;
export function sanitizeStderrSnippet(
  bytes: Uint8Array,
  invocationDir: string,
): string | undefined {
  if (bytes.length === 0) return undefined;
  const nlIndex = bytes.indexOf(0x0a);
  const cap = STDERR_CONTRACT_LIMIT;
  const end = nlIndex >= 0 ? Math.min(nlIndex, cap) : Math.min(bytes.length, cap);
  const lineBytes = bytes.subarray(0, end);
  if (lineBytes.length === 0) return undefined;
  let text: string;
  try {
    text = decodeStrictUtf8(lineBytes);
  } catch {
    return undefined;
  }
  if (invocationDir.length > 0 && text.includes(invocationDir)) return undefined;
  const normalized = text
    .split('')
    .map((ch) => {
      const code = ch.charCodeAt(0);
      if (code === 0x09) return ' ';
      if (code < 0x20 || code > 0x7e) return ' ';
      return ch;
    })
    .join('')
    .replace(/ +/g, ' ')
    .trim();
  return normalized.length > 0 ? normalized : undefined;
}

export function parseAprCode(
  snippet: string | undefined,
  exitClass: RuntimeExitClass,
): string | undefined {
  if (!snippet) return undefined;
  const match = snippet.match(/^(APR_[A-Z0-9_]+)/);
  if (!match) return undefined;
  const code = match[1];
  const expected = KNOWN_APR_CODES.get(code);
  if (!expected || expected !== exitClass) return undefined;
  return code;
}

export function buildContractViolations(
  capture: StreamCaptureResult,
): RuntimeContractViolation[] | undefined {
  const violations: RuntimeContractViolation[] = [];
  if (capture.stdoutBytes.length > 0) {
    violations.push({ kind: 'stdout-nonempty', observedBytes: capture.stdoutBytes.length });
  }
  if (capture.stderrBytes.length > STDERR_CONTRACT_LIMIT) {
    violations.push({ kind: 'stderr-over-contract', observedBytes: capture.stderrBytes.length });
  }
  return violations.length > 0 ? violations : undefined;
}
