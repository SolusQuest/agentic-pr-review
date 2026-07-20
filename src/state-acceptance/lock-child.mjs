import { createHash } from 'node:crypto';
import net from 'node:net';

function canonical(value) {
  if (Array.isArray(value)) return value.map(canonical);
  if (value !== null && typeof value === 'object') {
    return Object.fromEntries(
      Object.keys(value)
        .sort()
        .map((key) => [key, canonical(value[key])]),
    );
  }
  return value;
}

const stateKey = JSON.parse(process.argv[3]);
const stateKeyBytes = new TextEncoder().encode(JSON.stringify(canonical(stateKey)));
const stateKeyHash = createHash('sha256').update(stateKeyBytes).digest('hex');
const address = `\0agentic-pr-review-m4-${stateKeyHash}`;
const server = net.createServer((socket) => socket.destroy());
server.once('error', (error) => {
  console.error(error.code ?? 'lock_error');
  process.exitCode = 2;
});
server.listen({ path: address }, () => {
  process.stdout.write('READY\n');
});
setInterval(() => undefined, 1000);
