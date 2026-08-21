import { spawnSync } from 'node:child_process';

const markerCommit = 'APR_R4_W13_SOURCE_COMMIT';
const markerTree = 'APR_R4_W13_SOURCE_TREE';

function fail(message, exitCode = 1) {
  process.stderr.write(`clean-source proof failed: ${message}\n`);
  process.exit(exitCode);
}

function git(repo, args, encoding = 'utf8') {
  const result = spawnSync('git', ['-C', repo, ...args], {
    encoding,
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  if (result.status !== 0) {
    const detail = typeof result.stderr === 'string' ? result.stderr.trim() : '';
    fail(`git ${args.join(' ')}${detail === '' ? '' : `: ${detail}`}`);
  }
  return result.stdout;
}

function identity(repo) {
  const commit = git(repo, ['rev-parse', '--verify', 'HEAD^{commit}']).trim();
  const tree = git(repo, ['rev-parse', '--verify', 'HEAD^{tree}']).trim();
  if (!/^[0-9a-f]{40}$/u.test(commit) || !/^[0-9a-f]{40}$/u.test(tree)) {
    fail('HEAD commit or tree is not a canonical SHA-1 object identity');
  }
  return { commit, tree };
}

function assertClean(repo, phase) {
  const staged = spawnSync('git', ['-C', repo, 'diff', '--cached', '--quiet', '--'], {
    stdio: 'ignore',
  });
  if (staged.status !== 0) fail(`${phase}: staged tracked changes are present`);

  const unstaged = spawnSync('git', ['-C', repo, 'diff', '--quiet', '--'], {
    stdio: 'ignore',
  });
  if (unstaged.status !== 0) fail(`${phase}: unstaged tracked changes are present`);

  const untracked = git(repo, ['ls-files', '--others', '--exclude-standard', '-z'], 'buffer');
  if (untracked.length !== 0) fail(`${phase}: non-ignored untracked paths are present`);
}

const separator = process.argv.indexOf('--');
if (process.argv[2] !== '--repo' || separator < 4 || separator === process.argv.length - 1) {
  fail('usage: node scripts/run-clean-source-proof.mjs --repo <path> -- <command> [args...]', 2);
}

const repo = process.argv[3];
const command = process.argv[separator + 1];
const args = process.argv.slice(separator + 2);

assertClean(repo, 'before command');
const before = identity(repo);
const child = spawnSync(command, args, {
  cwd: repo,
  env: process.env,
  stdio: 'inherit',
});
assertClean(repo, 'after command');
const after = identity(repo);

if (after.commit !== before.commit || after.tree !== before.tree) {
  fail('HEAD commit or tree changed while the proof command ran');
}
if (child.error !== undefined) fail(`could not start child command: ${child.error.message}`);
if (child.signal !== null) fail(`child command ended from signal ${child.signal}`);
if (child.status !== 0) fail(`child command exited with status ${child.status}`, child.status ?? 1);

process.stdout.write(`${markerCommit} ${before.commit}\n${markerTree} ${before.tree}\n`);
