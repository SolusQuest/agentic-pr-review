import { readFileSync, mkdtempSync, mkdirSync, writeFileSync, renameSync, rmSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { crc32, deflateRawSync, gunzipSync } from 'node:zlib';
import { describe, expect, test } from 'vitest';
import {
  LIMITS,
  MEMBERS,
  canonicalJson,
  compiledIdentity,
  encodePackage,
  inspectPackage,
  materializePackage,
  readBoundedFile,
  sha256,
  validateReleaseVersion,
} from '../../scripts/release/build-payload.format.mjs';
import {
  admitSource,
  admitOutput,
  admitToolchain,
  admitPublishInventory,
  verifyDependencies,
} from '../../scripts/release/build-payload.mjs';

const fixtureBinary = () => {
  const bytes = Buffer.alloc(128);
  Buffer.from('7f454c4602010100', 'hex').copy(bytes);
  bytes.writeUInt16LE(3, 16);
  bytes.writeUInt16LE(62, 18);
  bytes.writeUInt32LE(1, 20);
  bytes.writeUInt16LE(64, 52);
  return bytes;
};
const notices = Buffer.from('Synthetic test-only notice; no real binary or release candidate.\n');
const native = {
  needed: ['ld-linux-x86-64.so.2', 'libc.so.6', 'libm.so.6'],
  runtimeLoaded: ['libssl.so.3'],
};
function inputs(version = 'v0.0.0-internal.1') {
  return {
    releaseVersion: version,
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
      linkerSha256: '1'.repeat(64),
      node: '24.19.0',
      zlib: '1.3.1',
      os: 'ubuntu-24.04-x64',
      systemPackages: [{ name: 'libc6', version: '2.39' }],
    },
    dependencies: [
      {
        id: 'synthetic.test.only',
        version: '1.0.0',
        sha256: '2'.repeat(64),
        contentHash: 'A'.repeat(86) + '==',
        role: 'managed',
      },
    ],
  };
}
function packageFixture(value = inputs(), binary = fixtureBinary(), notice = notices) {
  return encodePackage(binary, notice, value, native);
}
function gzip(tar: Buffer) {
  const trailer = Buffer.alloc(8);
  trailer.writeUInt32LE(crc32(tar));
  trailer.writeUInt32LE(tar.length, 4);
  return Buffer.concat([
    Buffer.from('1f8b0800000000000203', 'hex'),
    deflateRawSync(tar, { level: 9 }),
    trailer,
  ]);
}
function expectationFor(bytes: Buffer, original: any) {
  // Only the transport digest is changed to reach structural/semantic rejection.
  // The independently trusted identities and member inventory remain unchanged.
  return { ...original, archiveSize: bytes.length, archiveSha256: sha256(bytes) };
}
function headers(tar: Buffer) {
  let offset = 0;
  const result: number[] = [];
  for (let index = 0; index < 3; index++) {
    result.push(offset);
    const size = Number.parseInt(tar.subarray(offset + 124, offset + 136).toString('ascii'), 8);
    offset += 512 + Math.ceil(size / 512) * 512;
  }
  return result;
}
function checksum(tar: Buffer, offset: number) {
  tar.fill(32, offset + 148, offset + 156);
  const sum = tar.subarray(offset, offset + 512).reduce((a, b) => a + b, 0);
  tar.write(sum.toString(8).padStart(6, '0') + '\0 ', offset + 148, 'ascii');
}
function modifiedTar(change: (tar: Buffer, offsets: number[]) => Buffer | void) {
  const original = packageFixture();
  const tar = gunzipSync(original.archive);
  const changed = change(tar, headers(tar)) || tar;
  const bytes = gzip(changed);
  return { bytes, expected: expectationFor(bytes, original.receipt) };
}
describe('R7-D1 closed package format', () => {
  test('round-trips exactly three files and keeps all self/outer digests external', () => {
    const p = packageFixture();
    const result = inspectPackage(p.archive, p.receipt);
    expect(result.files).toHaveLength(3);
    expect(result.files[0]).toEqual(fixtureBinary());
    expect(result.receipt.members.map((item: any) => item.path)).toEqual(
      MEMBERS.map((item) => item.path),
    );
    expect(result.manifest.members).toHaveLength(2);
    expect(result.manifest).not.toHaveProperty('archiveSha256');
    expect(result.manifest.members.some((item: any) => item.path.endsWith('manifest.json'))).toBe(
      false,
    );
    expect(p.receipt.members[1].sha256).toBe(sha256(result.files[1]));
    expect(result.manifest.buildId).toBe(sha256(canonicalJson(result.manifest.buildInputs)));
  });
  test('preserves maximum admitted compiled identity under both existing protocol bounds', () => {
    const version = 'v999999999.999999999.99999999-internal.999999999';
    expect(version.length).toBe(48);
    const p = packageFixture(inputs(version));
    expect(inspectPackage(p.archive, p.receipt).manifest.informationalVersion.length).toBe(119);
    const resultSchema = JSON.parse(readFileSync('protocol/schemas/review-result.v1.json', 'utf8'));
    const traceSchema = JSON.parse(readFileSync('protocol/schemas/review-trace.v1.json', 'utf8'));
    for (const schema of [resultSchema, traceSchema])
      expect(compiledIdentity(version, 'a'.repeat(64)).length).toBeLessThanOrEqual(
        schema.properties.runtimeVersion.maxLength,
      );
  });
  test.each([
    'v999999999.999999999.999999999-internal.999999999',
    'v01.0.0-internal.1',
    'v0.00.0-internal.1',
    'v0.0.00-internal.1',
    'v0.0.0-internal.01',
    'v0.0.0-internal.0',
    '0.0.0-internal.1',
    'v0.0.0',
    'v0.0.0-internal.1+extra',
    'v0.0.0-internal.1\n',
  ])('refuses noncanonical/over-limit version %j', (version) => {
    expect(() => validateReleaseVersion(version)).toThrow('invalid_release_version');
  });
  test.each(['releaseVersion', 'sourceCommit', 'sourceTree'])(
    'rejects a well-formed wrong %s with original trusted identity',
    (field) => {
      const baseline = packageFixture();
      const value = inputs();
      value[field] = field === 'releaseVersion' ? 'v0.0.0-internal.2' : '9'.repeat(40);
      const wrong = packageFixture(value);
      expect(() => inspectPackage(wrong.archive, baseline.receipt)).toThrow(
        'archive_digest_mismatch',
      );
      expect(() =>
        inspectPackage(wrong.archive, expectationFor(wrong.archive, baseline.receipt)),
      ).toThrow('expected_identity_mismatch');
    },
  );
  test('rejects an independently expected wrong build digest and wrong member inventory', () => {
    const p = packageFixture();
    const expected = structuredClone(p.receipt);
    expected.identity.buildId = '8'.repeat(64);
    expected.identity.informationalVersion = compiledIdentity(
      expected.identity.releaseVersion,
      expected.identity.buildId,
    );
    expect(() => inspectPackage(p.archive, expected)).toThrow('expected_identity_mismatch');
    const member = structuredClone(p.receipt);
    member.members[0].sha256 = '7'.repeat(64);
    expect(() => inspectPackage(p.archive, member)).toThrow('expected_inventory_mismatch');
  });
  test('rejects platform changes even when self-consistent', () => {
    const value = inputs();
    value.platform = 'win-x64';
    expect(() => packageFixture(value)).toThrow('invalid_build_inputs');
    const p = packageFixture();
    const expected = structuredClone(p.receipt);
    expected.identity.platform = 'win-x64';
    expect(() => inspectPackage(p.archive, expected)).toThrow('invalid_identity');
  });
  test.each(['archiveSize', 'archiveSha256'])(
    'compares the independent outer %s before parsing',
    (field) => {
      const p = packageFixture();
      const expected = {
        ...p.receipt,
        [field]: field === 'archiveSize' ? p.archive.length + 1 : '0'.repeat(64),
      };
      expect(() => inspectPackage(p.archive, expected)).toThrow('archive_digest_mismatch');
    },
  );
  test('rejects changed executable bytes against original manifest and independently trusted inventory', () => {
    const corrupt = modifiedTar((tar) => {
      tar[512 + 100] ^= 1;
    });
    expect(() => inspectPackage(corrupt.bytes, corrupt.expected)).toThrow('member_digest_mismatch');
    const baseline = packageFixture();
    const binary = fixtureBinary();
    binary[100] = 17;
    const wrong = packageFixture(inputs(), binary);
    expect(() =>
      inspectPackage(wrong.archive, expectationFor(wrong.archive, baseline.receipt)),
    ).toThrow('expected_inventory_mismatch');
  });
  test.each([0, 1, 19, 50])('rejects truncated compressed input at %i bytes', (size) => {
    const p = packageFixture();
    const bytes = p.archive.subarray(0, size);
    expect(() => inspectPackage(bytes, expectationFor(bytes, p.receipt))).toThrow();
  });
  test.each(['concatenated', 'trailing-byte', 'crc', 'size', 'header-flags', 'timestamp'])(
    'rejects gzip %s',
    (kind) => {
      const p = packageFixture();
      let bytes = Buffer.from(p.archive);
      if (kind === 'concatenated') bytes = Buffer.concat([bytes, bytes]);
      else if (kind === 'trailing-byte') bytes = Buffer.concat([bytes, Buffer.from([0])]);
      else if (kind === 'crc') bytes[bytes.length - 8] ^= 1;
      else if (kind === 'size') bytes[bytes.length - 4] ^= 1;
      else if (kind === 'header-flags') bytes[3] = 8;
      else bytes[4] = 1;
      expect(() => inspectPackage(bytes, expectationFor(bytes, p.receipt))).toThrow();
    },
  );
  test.each([
    '../escape',
    '/absolute',
    'agentic-pr-review/../escape',
    'agentic-pr-review\\binary',
    'agentic-pr-review//binary',
    'agentic-pr-review/manifest.json',
    'extra-file',
  ])('rejects a checksummed noncanonical name %j', (name) => {
    const p = modifiedTar((tar) => {
      tar.fill(0, 0, 100);
      tar.write(name, 0, 'ascii');
      checksum(tar, 0);
    });
    expect(() => inspectPackage(p.bytes, p.expected)).toThrow('noncanonical_tar_header');
  });
  test.each(['1', '2', '3', '4', '5', '6', 'x', 'g', 'L', 'K', '\0'])(
    'rejects tar type %j even with a valid checksum',
    (type) => {
      const p = modifiedTar((tar) => {
        tar.write(type, 156, 'ascii');
        checksum(tar, 0);
      });
      expect(() => inspectPackage(p.bytes, p.expected)).toThrow('noncanonical_tar_header');
    },
  );
  test.each([100, 108, 116, 136, 148, 157, 257, 263, 265, 297, 329, 337, 345, 500])(
    'rejects header field drift at %i',
    (offset) => {
      const p = modifiedTar((tar) => {
        tar[offset] ^= 1;
        if (offset !== 148) checksum(tar, 0);
      });
      expect(() => inspectPackage(p.bytes, p.expected)).toThrow('noncanonical_tar_header');
    },
  );
  test.each([
    'padding',
    'missing-end',
    'extra-end',
    'nonzero-end',
    'missing-member',
    'duplicate-member',
    'fourth-member',
  ])('rejects tar %s', (kind) => {
    const p = modifiedTar((tar, offsets) => {
      if (kind === 'padding') tar[512 + 128] = 1;
      if (kind === 'missing-end') return tar.subarray(0, tar.length - 512);
      if (kind === 'extra-end') return Buffer.concat([tar, Buffer.alloc(512)]);
      if (kind === 'nonzero-end') tar[tar.length - 1] = 1;
      if (kind === 'missing-member')
        return Buffer.concat([tar.subarray(0, offsets[2]), Buffer.alloc(1024)]);
      if (kind === 'duplicate-member') tar.copy(tar, offsets[1], 0, 512);
      if (kind === 'fourth-member')
        return Buffer.concat([
          tar.subarray(0, tar.length - 1024),
          tar.subarray(0, offsets[1]),
          Buffer.alloc(1024),
        ]);
    });
    expect(() => inspectPackage(p.bytes, p.expected)).toThrow();
  });
  test('rejects malformed size before member slicing/allocation', () => {
    for (const size of ['00000000009\0', '77777777777\0', '00000000000\0']) {
      const p = modifiedTar((tar) => {
        tar.write(size, 124, 'ascii');
        checksum(tar, 0);
      });
      expect(() => inspectPackage(p.bytes, p.expected)).toThrow();
    }
  });
  test.each([
    'duplicate-key',
    'unknown-field',
    'build-preimage',
    'compiled-identity',
    'elf-machine',
    'manifest-digest',
  ])('rejects semantic mutation %s after transport integrity', (kind) => {
    const p = modifiedTar((tar, offsets) => {
      if (kind === 'elf-machine') {
        tar.writeUInt16LE(183, 512 + 18);
        return;
      }
      const at = offsets[1] + 512;
      const size = Number.parseInt(
        tar.subarray(offsets[1] + 124, offsets[1] + 136).toString('ascii'),
        8,
      );
      const manifest = JSON.parse(tar.subarray(at, at + size).toString('utf8'));
      let text;
      if (kind === 'duplicate-key') text = '{"formatVersion":1,' + canonicalJson(manifest).slice(1);
      else {
        if (kind === 'unknown-field') manifest.extra = 1;
        if (kind === 'build-preimage') manifest.buildInputs.policySha256 = '0'.repeat(64);
        if (kind === 'compiled-identity') manifest.informationalVersion = 'v0.0.0-internal.9';
        if (kind === 'manifest-digest') manifest.members[0].sha256 = '0'.repeat(64);
        text = canonicalJson(manifest);
      }
      const body = Buffer.from(text);
      const header = Buffer.from(tar.subarray(offsets[1], at));
      header.write(body.length.toString(8).padStart(11, '0') + '\0', 124, 'ascii');
      checksum(header, 0);
      return Buffer.concat([
        tar.subarray(0, offsets[1]),
        header,
        body,
        Buffer.alloc((512 - (body.length % 512)) % 512),
        tar.subarray(offsets[2]),
      ]);
    });
    expect(() => inspectPackage(p.bytes, p.expected)).toThrow();
  });
  test('caps archive and gzip expansion before parsing', () => {
    const p = packageFixture();
    const large = Buffer.alloc(LIMITS.archive + 1);
    expect(() => inspectPackage(large, { ...p.receipt, archiveSize: large.length })).toThrow(
      'invalid_expected_receipt',
    );
    const bomb = gzip(Buffer.alloc(LIMITS.expanded + 1));
    expect(() => inspectPackage(bomb, expectationFor(bomb, p.receipt))).toThrow(
      'invalid_or_excessive_gzip',
    );
  });
  test('accepts exact notice/binary caps and rejects their immediate neighbors', () => {
    const exactNotices = Buffer.alloc(LIMITS.notices, 65);
    const input = inputs();
    input.noticesSha256 = sha256(exactNotices);
    const binary = Buffer.alloc(LIMITS.executable);
    fixtureBinary().copy(binary);
    const p = packageFixture(input, binary, exactNotices);
    expect(inspectPackage(p.archive, p.receipt).files[0].length).toBe(LIMITS.executable);
    const excessive = Buffer.alloc(LIMITS.notices + 1);
    const wrong = inputs();
    wrong.noticesSha256 = sha256(excessive);
    expect(() => packageFixture(wrong, fixtureBinary(), excessive)).toThrow('invalid_member');
    expect(() => packageFixture(inputs(), Buffer.concat([binary, Buffer.from([0])]))).toThrow(
      'invalid_member',
    );
  });
  test('materializes only a fully inspected package into a private fresh directory', async () => {
    const parent = mkdtempSync(join(tmpdir(), 'apr-d1-extract-test-'));
    try {
      const p = packageFixture();
      const extracted = await materializePackage(p.archive, p.receipt, parent);
      expect(readFileSync(extracted.executable)).toEqual(fixtureBinary());
      const bad = Buffer.from(p.archive);
      bad[10] ^= 1;
      await expect(materializePackage(bad, p.receipt, parent)).rejects.toThrow();
      const file = join(parent, 'bounded');
      writeFileSync(file, 'a');
      expect(await readBoundedFile(file, 1)).toEqual(Buffer.from('a'));
      await expect(readBoundedFile(file, 0)).rejects.toThrow('invalid_or_excessive_file');
    } finally {
      rmSync(parent, { recursive: true, force: true });
    }
  });
});
describe('R7-D1 strict source admission', () => {
  function repository() {
    const root = mkdtempSync(join(tmpdir(), 'apr-d1-source-test-'));
    const git = (...args: string[]) =>
      execFileSync('git', args, { cwd: root, stdio: ['ignore', 'pipe', 'pipe'] })
        .toString('utf8')
        .trim();
    git('init');
    git('config', 'core.autocrlf', 'false');
    mkdirSync(join(root, 'runtime'));
    writeFileSync(join(root, 'runtime', 'source'), 'committed');
    writeFileSync(join(root, '.gitignore'), '.scratch/\n');
    git('add', '.');
    git(
      '-c',
      'user.name=Fixture',
      '-c',
      'user.email=fixture@example.invalid',
      'commit',
      '--no-gpg-sign',
      '-m',
      'synthetic source',
    );
    return { root, git, head: git('rev-parse', 'HEAD') };
  }
  test.each(['modified', 'staged', 'untracked', 'mismatched', 'invalid-source', 'unsafe-closure'])(
    'fails closed for %s',
    (kind) => {
      const repo = repository();
      try {
        let source = repo.head;
        if (kind === 'modified' || kind === 'staged') {
          writeFileSync(join(repo.root, 'runtime', 'source'), 'changed');
          if (kind === 'staged') repo.git('add', '.');
        }
        if (kind === 'untracked') writeFileSync(join(repo.root, 'untracked'), 'new input');
        if (kind === 'mismatched') source = '0'.repeat(40);
        if (kind === 'invalid-source') source = 'HEAD';
        if (kind === 'unsafe-closure') {
          repo.git(
            'update-index',
            '--add',
            '--cacheinfo',
            '120000,' + repo.git('rev-parse', 'HEAD:runtime/source') + ',runtime/unsafe',
          );
          repo.git(
            '-c',
            'user.name=Fixture',
            '-c',
            'user.email=fixture@example.invalid',
            'commit',
            '--no-gpg-sign',
            '-m',
            'synthetic unsafe entry',
          );
          repo.git('config', 'core.symlinks', 'false');
          repo.git('checkout-index', '-f', '--', 'runtime/unsafe');
          source = repo.git('rev-parse', 'HEAD');
        }
        expect(() => admitSource(repo.root, source)).toThrow(
          kind === 'unsafe-closure'
            ? 'unsafe_source_entry'
            : kind === 'mismatched'
              ? 'source_head_mismatch'
              : kind === 'invalid-source'
                ? 'invalid_source_commit'
                : 'dirty_source',
        );
      } finally {
        rmSync(repo.root, { recursive: true, force: true });
      }
    },
  );
  test('ignored ambient output does not enter the clean source identity', () => {
    const repo = repository();
    try {
      const tree = admitSource(repo.root, repo.head);
      mkdirSync(join(repo.root, '.scratch'));
      writeFileSync(join(repo.root, '.scratch', 'untrusted-output'), 'ignored');
      expect(admitSource(repo.root, repo.head)).toBe(tree);
    } finally {
      rmSync(repo.root, { recursive: true, force: true });
    }
  });
});

describe('R7-D1 production build admission', () => {
  test.each(['10.0.110', '10.0.109-preview.1', '9.0.0'])('rejects selected SDK drift %s', (sdk) => {
    expect(() =>
      admitToolchain(sdk, 'Ubuntu clang version 18.1.3', { sdk: '10.0.109', clang: '18.1.3' }),
    ).toThrow('toolchain_version_mismatch');
  });
  test.each(['18.1.4', '18.1.30', '19.0.0'])(
    'rejects selected native compiler drift %s',
    (version) => {
      expect(() =>
        admitToolchain('10.0.109', 'Ubuntu clang version ' + version, {
          sdk: '10.0.109',
          clang: '18.1.3',
        }),
      ).toThrow('toolchain_version_mismatch');
    },
  );
  test.each([
    [],
    ['AgenticPrReview.Runtime', 'AgenticPrReview.Runtime.dbg'],
    ['AgenticPrReview.Runtime.dll'],
    ['AgenticPrReview.Runtime', 'other'],
    ['AgenticPrReview.Runtime', 'AgenticPrReview.Runtime'],
  ])('rejects unexpected publish inventory %j', (...names) => {
    expect(() => admitPublishInventory(names)).toThrow('unexpected_publish_inventory');
  });
  test('accepts only the selected toolchain and single ordinary executable', () => {
    expect(() =>
      admitToolchain('10.0.109', 'Ubuntu clang version 18.1.3 (1ubuntu1)', {
        sdk: '10.0.109',
        clang: '18.1.3',
      }),
    ).not.toThrow();
    expect(() => admitPublishInventory(['AgenticPrReview.Runtime'])).not.toThrow();
  });
  test.each(['sourceCommit', 'sourceTree', 'policySha256', 'lockSha256', 'noticesSha256'])(
    'rejects coerced non-string build input %s',
    (field) => {
      const value = inputs();
      value[field] = [value[field]];
      expect(() => packageFixture(value)).toThrow('invalid_build_inputs');
    },
  );
  test.each([0, 4, 5, 16, 18, 20, 52])(
    'rejects unsupported ELF identity at offset %i',
    (offset) => {
      const binary = fixtureBinary();
      if (offset === 16) binary.writeUInt16LE(1, 16);
      else binary[offset] ^= 1;
      expect(() => packageFixture(inputs(), binary)).toThrow('invalid_linux_x64_executable');
    },
  );
  test('caps manifest bytes before JSON parsing at the first over-limit size', () => {
    const p = modifiedTar((tar, offsets) => {
      tar.write(
        (LIMITS.manifest + 1).toString(8).padStart(11, '0') + '\0',
        offsets[1] + 124,
        'ascii',
      );
      checksum(tar, offsets[1]);
    });
    expect(() => inspectPackage(p.bytes, p.expected)).toThrow('member_too_large_or_empty');
  });
});

describe('R7-D1 input/output filesystem admission', () => {
  test('rejects source-tree output before publication and existing outside destinations', () => {
    const parent = mkdtempSync(join(tmpdir(), 'apr-d1-output-test-'));
    const repo = join(parent, 'source');
    mkdirSync(repo);
    try {
      expect(() => admitOutput(repo, join(repo, 'new-package'))).toThrow('output_inside_source');
      expect(() => admitOutput(repo, repo)).toThrow('output_inside_source');
      expect(() => admitOutput(repo, parent)).toThrow('output_already_exists');
      expect(admitOutput(repo, join(parent, 'new-package'))).toBe(join(parent, 'new-package'));
    } finally {
      rmSync(parent, { recursive: true, force: true });
    }
  });
  test.skipIf(process.platform !== 'linux')(
    'refuses FIFO and symlink inputs without waiting on a writer',
    async () => {
      const parent = mkdtempSync(join(tmpdir(), 'apr-d1-special-input-test-'));
      try {
        const fifo = join(parent, 'fifo');
        execFileSync('/usr/bin/mkfifo', [fifo]);
        await expect(readBoundedFile(fifo, LIMITS.archive)).rejects.toThrow(
          'invalid_or_excessive_file',
        );
        const regular = join(parent, 'regular');
        writeFileSync(regular, 'x');
        execFileSync('/usr/bin/ln', ['-s', regular, join(parent, 'link')]);
        await expect(readBoundedFile(join(parent, 'link'), LIMITS.archive)).rejects.toThrow();
        const repo = join(parent, 'source');
        mkdirSync(repo);
        execFileSync('/usr/bin/ln', ['-s', repo, join(parent, 'source-link')]);
        expect(() => admitOutput(repo, join(parent, 'source-link', 'out'))).toThrow(
          'output_inside_source',
        );
      } finally {
        rmSync(parent, { recursive: true, force: true });
      }
    },
  );
});

test('package schema admits the exact pinned production dependency inventory', () => {
  const policy = JSON.parse(readFileSync('scripts/release/build-payload.inputs.json', 'utf8'));
  const value = inputs();
  value.dependencies = policy.dependencies;
  const p = packageFixture(value);
  expect(inspectPackage(p.archive, p.receipt).manifest.buildInputs.dependencies).toEqual(
    policy.dependencies,
  );
});
test.each([
  '../escape',
  'id/path',
  'id\\path',
  'id space',
  'id+suffix',
  '.leading',
  'a'.repeat(102),
])('package schema rejects unsafe dependency id %j', (id) => {
  const value = inputs();
  value.dependencies[0].id = id;
  expect(() => packageFixture(value)).toThrow('invalid_dependency_inventory');
});

test.each([
  'ld-musl-x86_64.so.1',
  '../ld-linux-x86-64.so.2',
  '/lib64/ld-linux-x86-64.so.2',
  'libc.so.6\0extra',
])('rejects a wrong-platform or unsafe native soname %j', (name) => {
  const p = inputs();
  expect(() =>
    encodePackage(fixtureBinary(), notices, p, { needed: [name], runtimeLoaded: ['libssl.so.3'] }),
  ).toThrow('invalid_native_inventory');
});

describe('R7-D1 complete restored package inventory', () => {
  function fixture() {
    const packages = mkdtempSync(join(tmpdir(), 'apr-d1-dependencies-'));
    const dependencies = [
      { id: 'fixture.managed', version: '1.2.3', role: 'managed' },
      { id: 'fixture.runtime', version: '10.0.9', role: 'native-runtime' },
    ].map((item) => {
      const archive = Buffer.from('Synthetic raw package: ' + item.id);
      const entry = {
        ...item,
        sha256: sha256(archive),
        contentHash: createHash('sha512')
          .update('synthetic content identity: ' + item.id)
          .digest('base64'),
      };
      const directory = join(packages, item.id, item.version);
      mkdirSync(directory, { recursive: true });
      writeFileSync(join(directory, item.id + '.' + item.version + '.nupkg'), archive);
      writeFileSync(
        join(directory, '.nupkg.metadata'),
        JSON.stringify({ contentHash: entry.contentHash }),
      );
      writeFileSync(
        join(directory, item.id + '.' + item.version + '.nupkg.sha512'),
        createHash('sha512').update(archive).digest('base64'),
      );
      return entry;
    });
    const managed = dependencies[0];
    const assets: any = {
      version: 3,
      libraries: {
        'Fixture.Managed/1.2.3': {
          type: 'package',
          path: 'fixture.managed/1.2.3',
          sha512: managed.contentHash,
        },
      },
      targets: {
        'net10.0/linux-x64': { 'Fixture.Managed/1.2.3': { type: 'package' } },
      },
      packageFolders: { [packages]: {} },
      project: {
        restore: { packagesPath: packages },
        frameworks: {
          'net10.0': {
            downloadDependencies: [
              { name: 'Fixture.Runtime', version: '[10.0.9, 10.0.9]' },
              { name: 'Fixture.Managed', version: '[1.2.3]' },
            ],
          },
        },
      },
    };
    return { packages, assets, policy: { dependencies } };
  }
  const cases: [string, (value: ReturnType<typeof fixture>) => void][] = [
    [
      'undeclared physical package',
      ({ packages }) => mkdirSync(join(packages, 'extra.download', '1.0.0'), { recursive: true }),
    ],
    [
      'additional runtime-pack version',
      ({ packages }) => mkdirSync(join(packages, 'fixture.runtime', '10.0.10')),
    ],
    [
      'wrong physical version',
      ({ packages }) =>
        renameSync(
          join(packages, 'fixture.runtime', '10.0.9'),
          join(packages, 'fixture.runtime', '10.0.10'),
        ),
    ],
    [
      'missing physical download',
      ({ packages }) => rmSync(join(packages, 'fixture.runtime'), { recursive: true }),
    ],
    [
      'non-package cache entry',
      ({ packages }) => writeFileSync(join(packages, 'unexpected-file'), 'x'),
    ],
    [
      'metadata content hash drift',
      ({ packages }) =>
        writeFileSync(
          join(packages, 'fixture.runtime', '10.0.9', '.nupkg.metadata'),
          JSON.stringify({ contentHash: 'changed' }),
        ),
    ],
    [
      'raw archive drift',
      ({ packages }) =>
        writeFileSync(
          join(packages, 'fixture.runtime', '10.0.9', 'fixture.runtime.10.0.9.nupkg'),
          'changed',
        ),
    ],
    [
      'NuGet hash-file drift',
      ({ packages }) =>
        writeFileSync(
          join(packages, 'fixture.runtime', '10.0.9', 'fixture.runtime.10.0.9.nupkg.sha512'),
          'changed',
        ),
    ],
    [
      'library version drift',
      ({ assets }) => {
        assets.libraries['Fixture.Managed/1.2.4'] = assets.libraries['Fixture.Managed/1.2.3'];
        delete assets.libraries['Fixture.Managed/1.2.3'];
      },
    ],
    [
      'library package-path drift',
      ({ assets }) => {
        assets.libraries['Fixture.Managed/1.2.3'].path = 'fixture.managed/1.2.4';
      },
    ],
    [
      'library content hash drift',
      ({ assets }) => {
        assets.libraries['Fixture.Managed/1.2.3'].sha512 = 'changed';
      },
    ],
    [
      'non-package library',
      ({ assets }) => {
        assets.libraries['Fixture.Managed/1.2.3'].type = 'project';
      },
    ],
    [
      'undeclared download metadata',
      ({ assets }) =>
        assets.project.frameworks['net10.0'].downloadDependencies.push({
          name: 'Extra.Download',
          version: '[1.0.0]',
        }),
    ],
    [
      'wrong runtime-pack download version',
      ({ assets }) => {
        assets.project.frameworks['net10.0'].downloadDependencies[0].version = '[10.0.10, 10.0.10]';
      },
    ],
    [
      'unpinned runtime-pack download range',
      ({ assets }) => {
        assets.project.frameworks['net10.0'].downloadDependencies[0].version = '[10.0.9, 10.0.10]';
      },
    ],
    [
      'missing download metadata',
      ({ assets }) => {
        assets.project.frameworks['net10.0'].downloadDependencies.shift();
      },
    ],
    [
      'wrong target version',
      ({ assets }) => {
        assets.targets['net10.0/linux-x64']['Fixture.Managed/1.2.4'] = { type: 'package' };
      },
    ],
    [
      'wrong target dependency version',
      ({ assets }) => {
        assets.targets['net10.0/linux-x64']['Fixture.Managed/1.2.3'].dependencies = {
          'Fixture.Managed': '1.2.4',
        };
      },
    ],
    [
      'duplicate download identity',
      ({ assets }) =>
        assets.project.frameworks['net10.0'].downloadDependencies.push({
          name: 'fixture.runtime',
          version: '[10.0.9]',
        }),
    ],
    [
      'missing target library',
      ({ assets }) => {
        assets.targets['net10.0/linux-x64'] = {};
      },
    ],
    [
      'alternate restore cache',
      ({ packages, assets }) => {
        assets.project.restore.packagesPath = join(packages, 'other');
      },
    ],
    [
      'fallback package folder',
      ({ packages, assets }) => {
        assets.packageFolders[join(packages, 'other')] = {};
      },
    ],
  ];
  test.each(cases)('rejects %s before publishing', async (_name, mutate) => {
    const value = fixture();
    try {
      mutate(value);
      await expect(
        verifyDependencies(value.packages, value.policy, value.assets),
      ).rejects.toThrow();
    } finally {
      rmSync(value.packages, { recursive: true, force: true });
    }
  });
  test('accepts a complete physical and metadata inventory including library/download overlap', async () => {
    const value = fixture();
    try {
      await expect(verifyDependencies(value.packages, value.policy, value.assets)).resolves.toEqual(
        value.policy.dependencies,
      );
      value.assets.project.frameworks['net10.0'].downloadDependencies.reverse();
      await expect(verifyDependencies(value.packages, value.policy, value.assets)).resolves.toEqual(
        value.policy.dependencies,
      );
    } finally {
      rmSync(value.packages, { recursive: true, force: true });
    }
  });
  test.skipIf(process.platform !== 'linux')('rejects a symlinked package directory', async () => {
    const value = fixture();
    const external = mkdtempSync(join(tmpdir(), 'apr-d1-external-package-'));
    try {
      renameSync(join(value.packages, 'fixture.runtime'), join(external, 'runtime'));
      execFileSync('/usr/bin/ln', [
        '-s',
        join(external, 'runtime'),
        join(value.packages, 'fixture.runtime'),
      ]);
      await expect(verifyDependencies(value.packages, value.policy, value.assets)).rejects.toThrow(
        'unexpected_restored_inventory',
      );
    } finally {
      rmSync(value.packages, { recursive: true, force: true });
      rmSync(external, { recursive: true, force: true });
    }
  });
});
