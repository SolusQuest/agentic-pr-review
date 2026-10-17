import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { appendFile, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { afterEach, expect, it } from 'vitest';

import { readGeneratedActionAudit } from './generated-action-audit.mjs';

const roots = [];
afterEach(async () => {
  await Promise.all(roots.splice(0).map((root) => rm(root, { recursive: true, force: true })));
});
async function fixture(bytes = '') {
  const root = await mkdtemp(path.join(tmpdir(), 'apr-audit-test-'));
  roots.push(root);
  const filename = path.join(root, 'audit.jsonl');
  await writeFile(filename, bytes);
  return filename;
}

it('polls a real running writer without admitting its partial tail, then validates after close', async () => {
  const filename = await fixture();
  const child = spawn(
    process.execPath,
    [
      '--input-type=module',
      '-e',
      `
    import { appendFileSync } from 'node:fs';
    const file = process.argv[1];
    appendFileSync(file, '{"kind":"wrapper"}\\n');
    appendFileSync(file, '{"kind":"spawn","pid":42');
    process.send('partial-tail-visible');
    process.once('message', () => {
      appendFileSync(file, '}\\n');
      process.disconnect();
    });
  `,
      filename,
    ],
    { stdio: ['ignore', 'ignore', 'pipe', 'ipc'] },
  );
  const closed = once(child, 'close');
  try {
    expect((await once(child, 'message'))[0]).toBe('partial-tail-visible');
    await expect(readGeneratedActionAudit(filename)).resolves.toEqual([{ kind: 'wrapper' }]);
    await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toThrow(
      'incomplete_record',
    );
    child.send('finish');
    expect(await closed).toEqual([0, null]);
    await expect(readGeneratedActionAudit(filename, { settled: true })).resolves.toEqual([
      { kind: 'wrapper' },
      { kind: 'spawn', pid: 42 },
    ]);
  } finally {
    if (child.exitCode === null && child.signalCode === null) child.kill('SIGKILL');
    await closed;
  }
});

it('ignores a partial first record only while the writer is running', async () => {
  const filename = await fixture('{"kind":');
  await expect(readGeneratedActionAudit(filename)).resolves.toEqual([]);
  await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toThrow(
    'incomplete_record',
  );
});

it('requires the final newline even when the unterminated JSON happens to be valid', async () => {
  const filename = await fixture('{"kind":"spawn"}');
  await expect(readGeneratedActionAudit(filename)).resolves.toEqual([]);
  await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toThrow(
    'incomplete_record',
  );
});

it.each(['{bad}\n', '{}\n{bad}\n', '{}\n\n', '\ufeff{}\n'])(
  'rejects malformed complete records during polling and after close: %j',
  async (source) => {
    const filename = await fixture(source);
    await expect(readGeneratedActionAudit(filename)).rejects.toThrow();
    await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toThrow();
  },
);

it('does not hide a malformed complete record behind a later partial tail', async () => {
  const filename = await fixture('{bad}\n{"kind":');
  await expect(readGeneratedActionAudit(filename)).rejects.toThrow();
});

it('waits for a split UTF-8 tail without replacing bytes in complete records', async () => {
  const bytes = Buffer.from('{"label":"😀"}\n');
  const split = bytes.indexOf(Buffer.from('😀')) + 2;
  const filename = await fixture(Buffer.concat([Buffer.from('{}\n'), bytes.subarray(0, split)]));
  await expect(readGeneratedActionAudit(filename)).resolves.toEqual([{}]);
  await appendFile(filename, bytes.subarray(split));
  await expect(readGeneratedActionAudit(filename, { settled: true })).resolves.toEqual([
    {},
    { label: '😀' },
  ]);
  await writeFile(
    filename,
    Buffer.from([0x7b, 0x22, 0x78, 0x22, 0x3a, 0x22, 0xff, 0x22, 0x7d, 0x0a]),
  );
  await expect(readGeneratedActionAudit(filename)).rejects.toThrow();
  await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toThrow();
});

it('accepts an empty file but propagates read errors in both phases', async () => {
  const filename = await fixture();
  await expect(readGeneratedActionAudit(filename)).resolves.toEqual([]);
  await expect(readGeneratedActionAudit(filename, { settled: true })).resolves.toEqual([]);
  await rm(filename);
  await expect(readGeneratedActionAudit(filename)).rejects.toMatchObject({ code: 'ENOENT' });
  await expect(readGeneratedActionAudit(filename, { settled: true })).rejects.toMatchObject({
    code: 'ENOENT',
  });
});
