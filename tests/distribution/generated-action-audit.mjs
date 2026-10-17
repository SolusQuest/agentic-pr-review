import { readFile } from 'node:fs/promises';

/** A running writer may expose a partial tail; after close every byte is evidence. */
export async function readGeneratedActionAudit(path, { settled = false } = {}) {
  const bytes = await readFile(path);
  const lastNewline = bytes.lastIndexOf(0x0a);
  if (settled && bytes.length > 0 && lastNewline !== bytes.length - 1)
    throw new SyntaxError('generated_action_audit_incomplete_record');
  if (lastNewline < 0) return [];
  const complete = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }).decode(
    bytes.subarray(0, lastNewline),
  );
  return complete.split('\n').map((line) => JSON.parse(line));
}
