import { createHash } from 'node:crypto';
import { access, chmod, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { runPrivateActionWrapperWithSeams } from '../index.js';
import { runHostProcess } from './host-process.js';

const root = path.resolve(process.argv[2]!);
const executable = path.join(root, 'host');
const event = path.join(root, 'event.json');
const ready = path.join(root, 'host-ready');
const cleanupMarker = path.join(root, 'cleanup-called');
const source = `#!${process.execPath}
const { spawn } = require('node:child_process');
const { writeFileSync } = require('node:fs');
spawn(process.execPath, ['-e', 'setTimeout(() => process.exit(0), 1000)'], {
  stdio: ['ignore', process.stdout, 'ignore']
});
process.stdin.resume();
process.on('SIGTERM', () => {});
writeFileSync(${JSON.stringify(ready)}, 'ready');
setInterval(() => {}, 1000);
`;
const bytes = Buffer.from(source);
await writeFile(executable, bytes);
await chmod(executable, 0o700);
await writeFile(event, '{}');
const controller = new AbortController();
void abortWhenReady(ready, controller);
await runPrivateActionWrapperWithSeams({
  toolkit: {
    getInput: (name) => (name === 'github-token' ? 'termination-canary' : ''),
    setSecret: () => undefined,
    writeSummary: async () => undefined,
    warning: () => undefined,
    error: () => undefined,
  },
  preparedPayload: {
    trustedRoot: root,
    executableRelativePath: 'host',
    actionSourceSha: 'a'.repeat(40),
    payloadSha256: createHash('sha256').update(bytes).digest('hex'),
    buildDiscriminator: 'r4-h1',
    wrapperBuildDiscriminator: 'r4-h1',
  },
  platform: 'linux',
  signal: controller.signal,
  runtimeFacts: () => ({
    eventJsonPath: event,
    repositoryName: 'SolusQuest/agentic-pr-review',
    repositoryId: '1',
    runId: '1',
    runAttempt: '1',
    workflowPath: '.github/workflows/r4-trusted-proof.yml',
    workflowRef:
      'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main',
    workflowSha: 'b'.repeat(40),
  }),
  bridgeRuntime: async () => {
    const stagingRoot = path.join(root, 'artifact-staging');
    await mkdir(stagingRoot, { mode: 0o700 });
    return {
      endpoint: path.join(root, 'bridge.sock'),
      stagingRoot,
      tempRoot: root,
      stopAndDrain: async () => undefined,
      cleanup: async () => {
        await writeFile(cleanupMarker, 'called');
      },
    };
  },
  createArtifactExecutor: async () => {
    throw new Error('must remain lazy');
  },
  hostProcessRunner: async (request) =>
    await runHostProcess({
      ...request,
      cancellationKillGraceMs: 20,
      postKillCloseGraceMs: 30,
    }),
  fatalExit: (code) => process.exit(code),
});
process.exit(99);

async function abortWhenReady(filePath: string, target: AbortController): Promise<void> {
  const deadline = Date.now() + 2_000;
  while (Date.now() < deadline) {
    try {
      await access(filePath);
      target.abort();
      return;
    } catch {
      await new Promise<void>((resolve) => setTimeout(resolve, 10));
    }
  }
  process.exit(98);
}
