import { encodePackage, sha256 } from '../../scripts/release/build-payload.format.mjs';

export const ACTION_SOURCE = '2'.repeat(40);
export function releaseFixture(executable?: Buffer) {
  if (executable === undefined) {
    executable = Buffer.alloc(128);
    Buffer.from('7f454c4602010100', 'hex').copy(executable);
    executable.writeUInt16LE(3, 16);
    executable.writeUInt16LE(62, 18);
    executable.writeUInt32LE(1, 20);
    executable.writeUInt16LE(64, 52);
  }
  const notices = Buffer.from('Synthetic test only; not a release candidate.\n');
  const packaged = encodePackage(
    executable,
    notices,
    {
      releaseVersion: 'v0.0.0-internal.1',
      platform: 'linux-x64',
      sourceCommit: 'a'.repeat(40),
      sourceTree: 'b'.repeat(40),
      policySha256: 'c'.repeat(64),
      lockSha256: 'd'.repeat(64),
      noticesSha256: sha256(notices),
      toolchain: {
        sdk: '10.0.109',
        sdkDriverSha256: 'e'.repeat(64),
        clang: '18.1.3',
        clangSha256: 'f'.repeat(64),
        linker: 'synthetic GNU ld',
        linkerSha256: '3'.repeat(64),
        node: '24.19.0',
        zlib: '1.3.1',
        os: 'ubuntu-24.04-x64',
        systemPackages: [{ name: 'libc6', version: '2.39' }],
      },
      dependencies: [
        {
          id: 'synthetic.test.only',
          version: '1.0.0',
          sha256: '4'.repeat(64),
          contentHash: 'A'.repeat(86) + '==',
          role: 'managed',
        },
      ],
    },
    { needed: ['libc.so.6'], runtimeLoaded: ['libssl.so.3'] },
  );
  const map = {
    formatVersion: 1,
    repository: 'SolusQuest/agentic-pr-review',
    tag: 'payload-v0.0.0-internal.1',
    builder: {
      repository: 'SolusQuest/agentic-pr-review',
      workflowPath: '.github/workflows/release.yml',
      workflowCommit: '1'.repeat(40),
    },
    payload: packaged.receipt,
  };
  return { ...packaged, map, mapBytes: Buffer.from(JSON.stringify(map)) };
}
