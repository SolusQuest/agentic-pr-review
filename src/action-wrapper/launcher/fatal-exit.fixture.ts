import { createHash } from 'node:crypto';
import { chmod, mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';

import { runContainedOfficialCall } from '../artifact-bridge/official-output.js';
import { runPrivateActionWrapperWithSeams } from '../index.js';

const root = path.resolve(process.argv[2]!);
const executable = path.join(root, 'host');
const event = path.join(root, 'event.json');
const bytes = Buffer.from('fatal-host');
await writeFile(executable, bytes);
if (process.platform !== 'win32') await chmod(executable, 0o700);
await writeFile(event, '{}');
await runPrivateActionWrapperWithSeams({
  toolkit: {
    getInput: () => '',
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
  signal: new AbortController().signal,
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
  bridgeRuntime: async (input) => {
    await mkdir(path.join(root, 'staging'), { recursive: true });
    await input.executorFactory(path.join(root, 'staging'));
    return {
      endpoint: '/tmp/fatal-bridge.sock',
      stagingRoot: path.join(root, 'staging'),
      tempRoot: root,
      stopAndDrain: async () => undefined,
      cleanup: async () => undefined,
    };
  },
  createArtifactExecutor: async (_context, tracker) => {
    const interval = setInterval(() => undefined, 1_000);
    const client = tracker.wrap({
      call: async () =>
        await new Promise<never>(() => {
          void interval;
        }),
    });
    await runContainedOfficialCall(() => client.call(), 1_000, new AbortController().signal).catch(
      () => undefined,
    );
    return { execute: async () => Promise.reject(new Error('unused')) };
  },
  hostProcessRunner: async () => ({
    exitCode: 0,
    completionBytes: Buffer.from(
      JSON.stringify({
        build_discriminator: 'r4-h1',
        status: 'reviewed',
        exit_class: 'success',
        process_exit_code: 0,
        summary: {
          reviewed_sha: 'c'.repeat(40),
          publication_url: 'https://github.com/SolusQuest/agentic-pr-review/pull/163',
          finding_count: 0,
          state_disposition: 'accepted',
        },
        annotations: [],
      }),
    ),
  }),
  fatalExit: (code) => process.exit(code),
  officialQuiescenceTimeoutMs: 20,
});
process.exit(99);
