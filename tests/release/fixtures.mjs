import { encodePackage, sha256 } from '../../scripts/release/build-payload.format.mjs';
import {
  REPOSITORY,
  REPOSITORY_ID,
  WORKFLOW,
  artifactName,
} from '../../scripts/release/candidate.mjs';

// Every source/version/run/artifact below is an explicit synthetic fixture.
export const NOW = Date.parse('2026-10-05T10:00:00Z');
export function producerFixture() {
  return {
    repository: REPOSITORY,
    repositoryId: REPOSITORY_ID,
    workflowPath: WORKFLOW,
    workflowCommit: 'b'.repeat(40),
    runId: '101',
    runAttempt: '2',
    runner: 'github-hosted-ubuntu-24.04-x64',
  };
}
export function storageFixture(producer = producerFixture(), role = 'payload') {
  return {
    artifactId: role === 'payload' ? '201' : '202',
    name: artifactName(role, producer),
    size: 10240,
    sha256: 'd'.repeat(64),
    createdAt: '2026-10-05T09:55:00Z',
    expiresAt: '2026-10-12T09:55:00Z',
  };
}
export function packageFixture() {
  const executable = Buffer.alloc(128);
  Buffer.from('7f454c4602010100', 'hex').copy(executable);
  executable.writeUInt16LE(3, 16);
  executable.writeUInt16LE(62, 18);
  executable.writeUInt32LE(1, 20);
  executable.writeUInt16LE(64, 52);
  const notices = Buffer.from(
    'Explicit synthetic candidate fixture. No real release authorization.\n',
  );
  const inputs = {
    releaseVersion: 'v0.0.0-internal.1',
    platform: 'linux-x64',
    sourceCommit: 'a'.repeat(40),
    sourceTree: 'c'.repeat(40),
    policySha256: '1'.repeat(64),
    lockSha256: '2'.repeat(64),
    noticesSha256: sha256(notices),
    toolchain: {
      sdk: '10.0.109',
      sdkDriverSha256: '3'.repeat(64),
      clang: '18.1.3',
      clangSha256: '4'.repeat(64),
      linker: 'synthetic GNU ld',
      linkerSha256: '5'.repeat(64),
      node: '24.21.0',
      zlib: '1.3.1',
      os: 'ubuntu-24.04-x64',
      systemPackages: [{ name: 'libc6', version: '2.39' }],
    },
    dependencies: [
      {
        id: 'synthetic.test.only',
        version: '1.0.0',
        sha256: '6'.repeat(64),
        contentHash: 'A'.repeat(86) + '==',
        role: 'managed',
      },
    ],
  };
  return encodePackage(executable, notices, inputs, {
    needed: ['libc.so.6'],
    runtimeLoaded: ['libssl.so.3'],
  });
}
export function runFixture(producer = producerFixture()) {
  return {
    id: producer.runId,
    run_attempt: producer.runAttempt,
    repository: { id: REPOSITORY_ID, full_name: REPOSITORY },
    head_repository: { id: REPOSITORY_ID, full_name: REPOSITORY },
    path: WORKFLOW,
    head_sha: producer.workflowCommit,
    head_branch: 'main',
    event: 'workflow_dispatch',
    status: 'completed',
    conclusion: 'success',
    run_started_at: '2026-10-05T09:50:00Z',
    updated_at: '2026-10-05T09:59:00Z',
  };
}
export function artifactFixture(storage = storageFixture(), producer = producerFixture()) {
  return {
    id: storage.artifactId,
    name: storage.name,
    size_in_bytes: storage.size,
    digest: 'sha256:' + storage.sha256,
    expired: false,
    created_at: storage.createdAt,
    expires_at: storage.expiresAt,
    workflow_run: {
      id: producer.runId,
      repository_id: REPOSITORY_ID,
      head_repository_id: REPOSITORY_ID,
      head_branch: 'main',
      head_sha: producer.workflowCommit,
    },
  };
}
