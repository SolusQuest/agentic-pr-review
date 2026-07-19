import { execFile as execFileCallback } from 'node:child_process';
import { mkdtemp, mkdir, rm } from 'node:fs/promises';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { promisify } from 'node:util';
import { describe, expect, it } from 'vitest';
import { readOutput } from './invoke-live-runtime.js';

const execFile = promisify(execFileCallback);

describe('live runtime output acceptance', () => {
  it('rejects directories before reading any output bytes', async () => {
    const root = await mkdtemp(path.join(os.tmpdir(), 'agentic-live-output-'));
    const directory = path.join(root, 'directory');
    await mkdir(directory);
    try {
      await expect(readOutput(directory, 'result-invalid')).rejects.toMatchObject({
        kind: 'unsafe-output-file',
      });
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('rejects a FIFO at every output position without blocking on open', async () => {
    if (process.platform !== 'linux') return;
    const root = await mkdtemp(path.join(os.tmpdir(), 'agentic-live-output-'));
    try {
      for (const name of [
        'result.json',
        'trace.json',
        'candidate-ledger.json',
        'provider-run-metadata.json',
      ]) {
        const fifo = path.join(root, name);
        await execFile('mkfifo', [fifo]);
        await expect(readOutput(fifo, 'result-invalid')).rejects.toMatchObject({
          kind: 'unsafe-output-file',
        });
      }
    } finally {
      await rm(root, { recursive: true, force: true });
    }
  });

  it('rejects a Unix socket before attempting a bounded read', async () => {
    if (process.platform !== 'linux') return;
    const root = await mkdtemp(path.join(os.tmpdir(), 'agentic-live-output-'));
    const socketPath = path.join(root, 'output.sock');
    const server = net.createServer();
    try {
      await new Promise<void>((resolve, reject) => {
        server.once('error', reject);
        server.listen(socketPath, resolve);
      });
      await expect(readOutput(socketPath, 'result-invalid')).rejects.toBeInstanceOf(Error);
    } finally {
      server.close();
      await rm(root, { recursive: true, force: true });
    }
  });
});
