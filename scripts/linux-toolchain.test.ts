import { spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';

const root = path.resolve(import.meta.dirname, '..');
const scripts = path.join(root, 'runtime', 'scripts');
const helper = path.join(scripts, 'require-linux-toolchain.sh');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const directory of temporaryRoots.splice(0)) {
    fs.rmSync(directory, { recursive: true, force: true });
  }
});

function fixture() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-linux-tools-'));
  temporaryRoots.push(directory);
  for (const name of ['dotnet', 'node']) {
    fs.copyFileSync('/bin/true', path.join(directory, name));
    fs.chmodSync(path.join(directory, name), 0o755);
  }
  return directory;
}

function run(directory: string, system = 'Linux', dotnet = 'dotnet') {
  return spawnSync(
    '/bin/bash',
    [
      '-c',
      'set -euo pipefail; uname() { printf "%s\\n" "$TEST_SYSTEM"; }; source "$1"; apr_require_linux_toolchain "$2" node',
      'platform-test',
      helper,
      dotnet,
    ],
    {
      env: { PATH: `${directory}:/usr/bin:/bin`, TEST_SYSTEM: system },
      encoding: 'utf8',
      timeout: 10_000,
    },
  );
}

describe.skipIf(process.platform !== 'linux')('Linux-native toolchain preflight', () => {
  test('admits Linux tools without inspecting WSL2 kernel branding', () => {
    const result = run(fixture());
    expect(result.status).toBe(0);
    expect(result.stdout).toBe('');
    expect(result.stderr).toBe('');
    expect(fs.readFileSync(helper, 'utf8')).not.toContain('/proc/sys/kernel/osrelease');
  });

  test.each(['Darwin', 'MINGW64_NT-10.0', 'Windows_NT'])('rejects non-Linux host %s', (system) => {
    expect(run(fixture(), system).status).not.toBe(0);
  });

  test('admits symlinked tools and an explicit dotnet path containing spaces', () => {
    const directory = fixture();
    const actual = path.join(directory, 'native dotnet');
    fs.renameSync(path.join(directory, 'dotnet'), actual);
    fs.symlinkSync(actual, path.join(directory, 'dotnet'));
    expect(run(directory).status).toBe(0);
    expect(run(directory, 'Linux', actual).status).toBe(0);
  });

  test.each(['dotnet', 'node'])('rejects renamed Windows-format %s', (tool) => {
    const directory = fixture();
    fs.writeFileSync(path.join(directory, tool), Buffer.from('MZ\0\0windows'));
    expect(run(directory).status).not.toBe(0);
  });

  test('rejects a Windows binary reached through a symlink', () => {
    const directory = fixture();
    const target = path.join(directory, 'dotnet.exe');
    fs.renameSync(path.join(directory, 'dotnet'), target);
    fs.writeFileSync(target, 'MZ\0\0windows');
    fs.symlinkSync(target, path.join(directory, 'dotnet'));
    expect(run(directory).status).not.toBe(0);
  });

  test('rejects an interop shell shim without executing it', () => {
    const directory = fixture();
    fs.writeFileSync(path.join(directory, 'dotnet'), '#!/bin/sh\necho SHIM_EXECUTED\n');
    const result = run(directory);
    expect(result.status).not.toBe(0);
    expect(result.stdout).not.toContain('SHIM_EXECUTED');
  });

  test('rejects a missing explicit tool and a non-executable native file', () => {
    const directory = fixture();
    expect(run(directory, 'Linux', path.join(directory, 'missing')).status).not.toBe(0);
    fs.chmodSync(path.join(directory, 'dotnet'), 0o644);
    expect(run(directory, 'Linux', path.join(directory, 'dotnet')).status).not.toBe(0);
  });

  test.each([
    ['verify-r4-trusted-proof-payload-v2.sh', [], 'native-linux-required'],
    ['verify-action-host.sh', ['aot'], 'requires Linux-native'],
    ['verify-live-agent.sh', ['live', '--prepare'], 'APR_R3_MANAGED_REQUIRED'],
    ['verify-live-agent.sh', ['live', '--verify-prepared'], 'APR_R3_MANAGED_REQUIRED'],
    ['verify-live-agent.sh', ['live', '--dry-run'], 'APR_R3_CURRENT_COMPONENT_TOOLCHAIN_INVALID'],
    [
      'prepare-r4-trusted-proof-payload-current.sh',
      [
        '--source-root',
        '/source',
        '--control-root',
        '/control',
        '--expected-source-sha',
        '0'.repeat(40),
        '--output-root',
        '/output',
        '--github-output',
        '/github-output',
      ],
      'native-linux-required',
    ],
    [
      'prepare-r4-trusted-proof-payload-v2.sh',
      [
        '--source-root',
        '/source',
        '--control-root',
        '/control',
        '--receipt',
        '/receipt',
        '--output-root',
        '/output',
        '--github-output',
        '/github-output',
      ],
      'native-linux-required',
    ],
  ] as const)('current entry %s %j rejects non-native tools', (script, args, diagnostic) => {
    const directory = fixture();
    fs.writeFileSync(path.join(directory, 'dotnet'), 'MZ\0\0windows');
    const result = spawnSync('/bin/bash', [path.join(scripts, script), ...args], {
      env: { PATH: `${directory}:/usr/bin:/bin` },
      encoding: 'utf8',
      timeout: 10_000,
    });
    expect(result.status).not.toBe(0);
    expect(result.stderr).toContain(diagnostic);
  });
});

test('retired live execution rejects before platform helper loading', () => {
  const source = fs.readFileSync(path.join(scripts, 'verify-live-agent.sh'), 'utf8');
  const dispatch = source.slice(0, source.indexOf('\nSCRIPT_DIR='));
  expect(dispatch).toContain('APR_R3_MANAGED_REQUIRED');
  expect(dispatch).toContain('exit 2');
  expect(dispatch).not.toContain('exec ');
  expect(dispatch).not.toMatch(/AGENTIC_REVIEW|source |require-linux-toolchain/u);
});

test('capacity shell refuses a successful build command that leaves no artifact', () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-capacity-shell-'));
  temporaryRoots.push(directory);
  const target = path.join(directory, 'runtime', 'scripts', 'verify-r7-capacity.sh');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.copyFileSync(path.join(scripts, 'verify-r7-capacity.sh'), target);
  for (const args of [
    ['init', '--quiet'],
    ['add', 'runtime'],
    [
      '-c',
      'user.name=Fixture',
      '-c',
      'user.email=fixture@example.invalid',
      'commit',
      '--quiet',
      '-m',
      'fixture',
    ],
  ]) {
    const setup = spawnSync('git', args, { cwd: directory, encoding: 'utf8' });
    expect(setup.status, setup.stderr).toBe(0);
  }
  // Execute the real shell entrypoint. Only the external build/runner command
  // is a successful no-op, deliberately leaving its promised artifact absent.
  const result = spawnSync('/bin/bash', [target, 'framework'], {
    cwd: directory,
    env: { ...process.env, DOTNET_CMD: '/usr/bin/true' },
    encoding: 'utf8',
    timeout: 10_000,
  });
  expect(result.status).not.toBe(0);
  expect(result.stderr).toContain('sha256sum:');
  expect(result.stdout).not.toContain('result=verified');
  expect(result.stdout).not.toContain('APR_R7_CAPACITY_GATE_PASSED');
});

function capacityFixture() {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'apr-capacity-diagnostics-'));
  temporaryRoots.push(directory);
  const repository = path.join(directory, 'repository');
  const tools = path.join(directory, 'tools');
  const target = path.join(repository, 'runtime/scripts/verify-r7-capacity.sh');
  fs.mkdirSync(path.dirname(target), { recursive: true });
  fs.mkdirSync(tools);
  fs.copyFileSync(path.join(scripts, 'verify-r7-capacity.sh'), target);
  for (const args of [
    ['init', '--quiet'],
    ['add', 'runtime'],
    [
      '-c',
      'user.name=Fixture',
      '-c',
      'user.email=fixture@example.invalid',
      'commit',
      '--quiet',
      '-m',
      'fixture',
    ],
  ])
    expect(spawnSync('git', args, { cwd: repository }).status).toBe(0);
  const executable = (name: string, source: string) => {
    const file = path.join(tools, name);
    fs.writeFileSync(file, source, { mode: 0o755 });
    return file;
  };
  const sdk = executable(
    'sdk tool',
    `#!/bin/bash
set -eu
command="$1"; shift
case "$command" in build) phase=framework-build;; test) phase=focused-tests;; publish) phase=aot-publish;; *) phase=runner;; esac
if [[ "$FAIL_PHASE" == "$phase" ]]; then
  if [[ "$EMPTY_LOG" != 1 ]]; then
    printf 'EARLY_PUBLIC_DIAGNOSTIC\\n'
    if [[ "$LARGE_LOG" == 1 ]]; then head -c 32768 /dev/zero | tr '\\0' x; fi
    printf '\\nLAST_PUBLIC_DIAGNOSTIC\\n' >&2
  fi
  case "$phase" in framework-build) exit 31;; focused-tests) exit 37;; aot-publish) exit 43;; *) exit 47;; esac
fi
if [[ "$command" == build ]]; then
  out=''
  while (( $# )); do if [[ "$1" == -o ]]; then shift; out="$1"; fi; shift; done
  mkdir -p "$out"; printf 'synthetic-artifact' > "$out/AgenticPrReview.Runtime.ReviewEvaluationFixture.dll"
fi
`,
  );
  executable(
    'mktemp',
    `#!/bin/bash
set -eu
owned=$(/usr/bin/mktemp "$@")
printf '%s' "$owned" > "$ROOT_RECORD"
printf '%s\\n' "$owned"
`,
  );
  executable('uname', '#!/bin/bash\nif [[ "$1" == -s ]]; then echo Linux; else echo x86_64; fi\n');
  executable(
    'rm',
    `#!/bin/bash
if [[ "$FAIL_CLEANUP" == 1 ]]; then exit 91; fi
exec /bin/rm "$@"
`,
  );
  executable(
    'tail',
    `#!/bin/bash
if [[ "$FAIL_TAIL" == 1 ]]; then exit 92; fi
exec /usr/bin/tail "$@"
`,
  );
  // Hash the actual promised artifact bytes, including on Darwin.
  fs.writeFileSync(
    path.join(tools, 'hash.mjs'),
    `import fs from 'node:fs'; import {createHash} from 'node:crypto'; const file=process.argv[2]; console.log(createHash('sha256').update(fs.readFileSync(file)).digest('hex')+'  '+file);`,
  );
  const quote = (value: string) => "'" + value.replaceAll("'", "'\\''") + "'";
  executable(
    'sha256sum',
    `#!/bin/bash\nexec ${quote(process.execPath)} ${quote(path.join(tools, 'hash.mjs'))} "$@"\n`,
  );
  return (phase = '', options: Record<string, string> = {}) => {
    const record = path.join(directory, 'owned-root');
    const result = spawnSync('/bin/bash', [target, phase === 'aot-publish' ? 'aot' : 'framework'], {
      cwd: repository,
      env: {
        ...process.env,
        PATH: `${tools}:${process.env.PATH}`,
        DOTNET_CMD: sdk,
        ROOT_RECORD: record,
        FAIL_PHASE: phase,
        EMPTY_LOG: '0',
        LARGE_LOG: '0',
        FAIL_CLEANUP: '0',
        FAIL_TAIL: '0',
        ...options,
      },
      encoding: 'utf8',
      timeout: 10_000,
    });
    const owned = fs.readFileSync(record, 'utf8');
    expect(owned).toMatch(/^\/tmp\/apr-r7-capacity-gate-/u);
    const removed = !fs.existsSync(owned);
    // A deliberately refused cleanup leaves only this test's marked root.
    if (!removed) {
      expect(fs.existsSync(path.join(owned, '.r7-capacity-root'))).toBe(true);
      fs.rmSync(owned, { recursive: true, force: true });
    }
    return { ...result, removed };
  };
}

describe('capacity shell failure diagnostics and cleanup', () => {
  test.each([
    ['framework-build', 31],
    ['focused-tests', 37],
    ['aot-publish', 43],
  ] as const)('reports %s before removing its owned log', (phase, status) => {
    const result = capacityFixture()(phase);
    expect(result.status).toBe(status);
    expect(result.stderr).toContain(`APR_R7_CAPACITY_STEP_FAILED phase=${phase} exit=${status}`);
    expect(result.stderr).toContain('LAST_PUBLIC_DIAGNOSTIC');
    expect(result.stdout).not.toContain('trusted_config_and_focused_tests=verified');
    expect(result.stdout).not.toContain('APR_R7_CAPACITY_GATE_PASSED');
    expect(result.removed).toBe(true);
  });
  test('bounds the failed command log tail', () => {
    const result = capacityFixture()('framework-build', { LARGE_LOG: '1' });
    expect(result.status).toBe(31);
    expect(result.stderr).toContain('LAST_PUBLIC_DIAGNOSTIC');
    expect(result.stderr).not.toContain('EARLY_PUBLIC_DIAGNOSTIC');
    expect(Buffer.byteLength(result.stderr)).toBeLessThan(17000);
    expect(result.removed).toBe(true);
  });
  test.each([{ EMPTY_LOG: '1' }, { FAIL_TAIL: '1' }])(
    'retains status without readable log bytes %j',
    (options) => {
      const result = capacityFixture()('framework-build', options);
      expect(result.status).toBe(31);
      expect(result.stderr).toContain('phase=framework-build exit=31');
      expect(result.stdout).not.toContain('APR_R7_CAPACITY_GATE_PASSED');
      expect(result.removed).toBe(true);
    },
  );
  test.each(['framework-build', ''])('fails closed on cleanup refusal after %s', (phase) => {
    const result = capacityFixture()(phase, { FAIL_CLEANUP: '1' });
    expect(result.status).toBe(phase ? 31 : 91);
    expect(result.stderr).toContain('APR_R7_CAPACITY_CLEANUP_FAILED');
    expect(result.stdout).not.toContain('APR_R7_CAPACITY_GATE_PASSED');
    expect(result.removed).toBe(false);
  });
  test('retains an unlogged runner failure', () => {
    const result = capacityFixture()('runner');
    expect(result.status).toBe(47);
    expect(result.stderr).toContain('LAST_PUBLIC_DIAGNOSTIC');
    expect(result.stdout).not.toContain('APR_R7_CAPACITY_GATE_PASSED');
    expect(result.removed).toBe(true);
  });
  test('retains successful framework receipts, actual artifact hash and cleanup', () => {
    const result = capacityFixture()();
    expect(result.status, result.stderr).toBe(0);
    expect(result.stdout).toContain('cases=15 host_cases=8 result=verified');
    expect(result.stdout).toContain('trusted_config_and_focused_tests=verified');
    expect(result.stdout).toContain('APR_R7_CAPACITY_GATE_PASSED cleanup=verified');
    expect(result.stderr).toBe('');
    expect(result.removed).toBe(true);
  });
});
