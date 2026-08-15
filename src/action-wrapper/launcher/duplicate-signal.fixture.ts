import { chmod, open, readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { createTerminationSignal } from '../index.js';
import { runHostProcess } from './host-process.js';

const root = path.resolve(process.argv[2]!);
const executable = path.join(root, 'host');
const ready = path.join(root, 'host-ready');
const signalCount = path.join(root, 'host-signals');
const release = path.join(root, 'host-release');
const source = `#!${process.execPath}
const { appendFileSync, existsSync, writeFileSync } = require('node:fs');
process.stdin.resume();
let cancelling = false;
process.on('SIGTERM', () => {
  appendFileSync(${JSON.stringify(signalCount)}, 'x');
  if (cancelling) return;
  cancelling = true;
  const releasePoll = setInterval(() => {
    if (!existsSync(${JSON.stringify(release)})) return;
    clearInterval(releasePoll);
    const body = Buffer.from('{"reconciled":true}');
    const output = Buffer.alloc(4 + body.length);
    output.writeUInt32BE(body.length, 0);
    body.copy(output, 4);
    process.stdout.write(output, () => process.exit(0));
  }, 10);
});
writeFileSync(${JSON.stringify(ready)}, 'ready');
setInterval(() => {}, 1000);
`;
await writeFile(executable, source);
await chmod(executable, 0o700);
// The test-only Vite server owns SIGTERM in its CLI process; production does not run under Vite.
process.removeAllListeners('SIGTERM');
process.removeAllListeners('SIGINT');
const termination = createTerminationSignal();
const executableHandle = await open(executable, 'r');
try {
  const result = await runHostProcess({
    executableHandle,
    launchBytes: Buffer.from('{}'),
    tempRoot: root,
    signal: termination.signal,
  });
  if (result.exitCode !== 0 || (await readFile(signalCount, 'utf8')) !== 'x') process.exit(97);
} finally {
  await executableHandle.close();
  termination.dispose();
}
