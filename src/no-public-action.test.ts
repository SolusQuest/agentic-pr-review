import { copyFile, mkdir, mkdtemp, rm, symlink, writeFile } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { afterEach, describe, expect, it } from 'vitest';

const fixtures: string[] = [];
const sourceGuard = path.resolve('scripts/check-no-public-action.mjs');

afterEach(async () => {
  await Promise.all(
    fixtures.splice(0).map((fixture) => rm(fixture, { recursive: true, force: true })),
  );
});

describe('no-public-Action packaging guard', () => {
  it('accepts a clean repository even when invoked from an unrelated working directory', async () => {
    const fixture = await createFixture();
    const unrelated = await createUnrelatedDirectory();

    const result = runGuard(fixture, unrelated);

    expect(result.status).toBe(0);
    expect(result.stderr).toBe('');
  });

  it.each([
    [
      'Action metadata',
      '.github/actions/agentic-pr-review/action.yml',
      '.github/actions/agentic-pr-review',
    ],
    [
      'ignored Action bundle',
      '.github/actions/agentic-pr-review/dist/index.js',
      '.github/actions/agentic-pr-review',
    ],
    ['bundle builder', 'scripts/build.mjs', 'scripts/build.mjs'],
    [
      'local synthetic runner',
      'scripts/run-local-synthetic.mjs',
      'scripts/run-local-synthetic.mjs',
    ],
  ])('rejects a retired %s path', async (_name, relativePath, reportedPath) => {
    const fixture = await createFixture();
    await write(relativePath, 'retired\n', fixture);

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain(reportedPath);
  });

  it('rejects a retired Action symlink while anchored away from cwd', async () => {
    const fixture = await createFixture();
    const target = path.join(fixture, 'retired-action-target');
    const link = path.join(fixture, '.github/actions/agentic-pr-review');
    await mkdir(target, { recursive: true });
    await mkdir(path.dirname(link), { recursive: true });
    await symlink(target, link, process.platform === 'win32' ? 'junction' : 'dir');
    const unrelated = await createUnrelatedDirectory();

    const result = runGuard(fixture, unrelated);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('.github/actions/agentic-pr-review (retired-action-path)');
  });

  it.each([
    ['unquoted yml', 'uses: ./.github/actions/agentic-pr-review', 'review.yml'],
    [
      'single-quoted scalar with comment',
      "  - uses: './.github/actions/agentic-pr-review' # retired",
      'review.yml',
    ],
    [
      'double-quoted whitespace variant',
      '  - uses : "./.github/actions/agentic-pr-review/" # retired',
      'review.yaml',
    ],
    [
      'flow mapping',
      '  - { name: review, uses: ./.github/actions/agentic-pr-review/ }',
      'nested/review.yaml',
    ],
  ])('rejects a %s workflow invocation', async (_name, line, relativeWorkflow) => {
    const fixture = await createFixture();
    await write(`.github/workflows/${relativeWorkflow}`, `${line}\n`, fixture);

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('retired-local-action-invocation');
  });

  it.each([
    ['literal', '|'],
    ['folded', '>-'],
  ])('rejects a block-style %s uses value', async (_name, indicator) => {
    const fixture = await createFixture();
    await write(
      '.github/workflows/review.yml',
      ['steps:', `  - uses: ${indicator}`, '      ./.github/actions/agentic-pr-review', ''].join(
        '\n',
      ),
      fixture,
    );

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('retired-local-action-invocation');
  });

  it.each([
    ['literal', '|'],
    ['folded', '>-'],
  ])('ignores retired uses text inside a %s run block', async (_name, indicator) => {
    const fixture = await createFixture();
    await write(
      '.github/workflows/clean.yml',
      [
        'steps:',
        `  - run: ${indicator}`,
        '      uses: ./.github/actions/agentic-pr-review',
        '',
      ].join('\n'),
      fixture,
    );

    const result = runGuard(fixture);

    expect(result.status).toBe(0);
  });

  it.each([
    [
      'top-level step',
      [
        'steps:',
        '  - name: |-',
        '      Retired review',
        '    uses: ./.github/actions/agentic-pr-review',
        '',
      ],
    ],
    [
      'nested step',
      [
        'jobs:',
        '  review:',
        '    steps:',
        '      - name: >',
        '          Review',
        '        uses: ./.github/actions/agentic-pr-review/',
        '',
      ],
    ],
  ])('scans a uses sibling after a block-style %s name', async (_name, lines) => {
    const fixture = await createFixture();
    await write('.github/workflows/review.yml', lines.join('\n'), fixture);

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('retired-local-action-invocation');
  });

  it('ignores comments and similar paths', async () => {
    const fixture = await createFixture();
    await write(
      '.github/workflows/clean.yml',
      [
        '# uses: ./.github/actions/agentic-pr-review',
        'steps:',
        '  - uses: ./.github/actions/agentic-pr-review-next',
        '  - run: echo "uses: ./.github/actions/agentic-pr-review"',
        '-uses: |-',
        '  ./.github/actions/agentic-pr-review',
        '',
      ].join('\n'),
      fixture,
    );

    const result = runGuard(fixture);

    expect(result.status).toBe(0);
  });

  it('reports multiple violations in deterministic path order', async () => {
    const fixture = await createFixture();
    await write('scripts/run-local-synthetic.mjs', 'retired\n', fixture);
    await write('scripts/build.mjs', 'retired\n', fixture);
    await write(
      '.github/workflows/z.yaml',
      '- uses: ./.github/actions/agentic-pr-review\n',
      fixture,
    );

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    const reported = result.stderr.split(/\r?\n/u).filter((line) => line.startsWith('- '));
    expect(reported).toEqual([
      '- .github/workflows/z.yaml (retired-local-action-invocation)',
      '- scripts/build.mjs (retired-bundle-builder)',
      '- scripts/run-local-synthetic.mjs (retired-local-runner)',
    ]);
  });

  it('fails closed when the workflow root is not a directory', async () => {
    const fixture = await createFixture();
    await write('.github/workflows', 'not a directory\n', fixture);

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('.github/workflows (workflow-root-not-directory)');
  });

  it('fails closed when a workflow-shaped entry is a directory', async () => {
    const fixture = await createFixture();
    await mkdir(path.join(fixture, '.github/workflows/not-a-file.yml'), {
      recursive: true,
    });

    const result = runGuard(fixture);

    expect(result.status).toBe(1);
    expect(result.stderr).toContain('.github/workflows/not-a-file.yml (workflow-file-not-regular)');
  });
});

async function createFixture() {
  const fixture = await mkdtemp(path.join(os.tmpdir(), 'apr-no-public-action-'));
  fixtures.push(fixture);
  await mkdir(path.join(fixture, 'scripts'), { recursive: true });
  await copyFile(sourceGuard, path.join(fixture, 'scripts/check-no-public-action.mjs'));
  return fixture;
}

async function createUnrelatedDirectory() {
  const directory = await mkdtemp(path.join(os.tmpdir(), 'apr-unrelated-cwd-'));
  fixtures.push(directory);
  return directory;
}

async function write(relativePath: string, contents: string, fixture: string) {
  const absolutePath = path.join(fixture, relativePath);
  await mkdir(path.dirname(absolutePath), { recursive: true });
  await writeFile(absolutePath, contents);
}

function runGuard(fixture: string, cwd = fixture) {
  return spawnSync(process.execPath, [path.join(fixture, 'scripts/check-no-public-action.mjs')], {
    cwd,
    encoding: 'utf8',
    windowsHide: true,
  });
}
