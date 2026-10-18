import { spawnSync } from 'node:child_process';
import { mkdirSync, mkdtempSync, readFileSync, realpathSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const [suite = 'all', ...args] = process.argv.slice(2);
const suites = ['all', 'node', 'runtime', 'node-all', 'runtime-all'];
if (['help', '--help', '-h'].includes(suite)) {
  console.log(`Usage: npm run test:native -- [${suites.join('|')}] [runner arguments]

node: src/ and tests/; pass Vitest paths/options to select other tests.
runtime: output, snapshot, tools, state, replay and evidence tests; accepts dotnet --filter.
node-all/runtime-all: unfiltered diagnostics, including unsupported platform mechanisms.
all: both focused defaults (no extra arguments).

Darwin uses physical /private/tmp to avoid system aliases and long Unix socket paths.
VSTest and socket fixtures require local socket permission. These are development
checks; complete hosted Linux CI and qualification scripts remain required.`);
  process.exit(0);
}
if (!suites.includes(suite) || (suite === 'all' && args.length !== 0)) {
  console.error('Select a runner before passing its arguments; use --help for examples.');
  process.exit(2);
}

// This changes only the test children's environment. Product admission continues
// to reject symlinks and noncanonical caller-selected roots.
const temp = realpathSync(process.platform === 'darwin' ? '/tmp' : os.tmpdir());
const env = { ...process.env, TMPDIR: temp, TMP: temp, TEMP: temp };
const runtimeFilter = [
  'RuntimeApplicationTests',
  'AgentToolsTests',
  'ReviewedSnapshot',
  'ReviewedTreeSnapshotTests',
  'ReviewedRootMaterializerTests',
  'Tests.Host.State',
  'R5ReplayAdmissionTests',
  'R5CompletedReplayTests.ClosedChildEnvironmentAndAllTransientChannelsExcludeAmbientCredentials',
  'R6EconomicsRunnerTests.EnvironmentAndPrivateStateCanariesNeverReachPublicOutputOrChildEnvironment',
  'TrustedProofEvidenceBoundaryTests',
  'SyntheticPrimaryBucket',
  'NativeFileOperationsTests',
  'DarwinDirectoryAuthorityIsRejectedBeforeCapabilityIssuance',
]
  .map((name) => `FullyQualifiedName~${name}`)
  .join('|');
// Physical bridge operations still require anchored directory paths. Keep all
// four portable bridge contract/codec controls; runtime-all retains every
// transport/staging diagnostic, including unsupported-platform failures.
const portableBridgeTests = [
  'FreezesExactPrivateBounds',
  'UsesStrictSourceGeneratedPrivateSerialization',
  'H1CodecRejectsUnknownDuplicateAndBuildMismatch',
  'GitHubArtifactAdapterTypesStayInternalAndCapabilityNarrow',
]
  .map((name) => `FullyQualifiedName~ArtifactBridgeContractTests.${name}`)
  .join('|');
const focusedRuntimeFilter =
  process.platform === 'darwin'
    ? `(${runtimeFilter})&(FullyQualifiedName!~Tests.Host.State.GitHubArtifacts|${portableBridgeTests})`
    : runtimeFilter;

function run(command, commandArgs) {
  const result = spawnSync(command, commandArgs, { cwd: root, env, stdio: 'inherit' });
  if (result.error || result.signal) console.error('Native test execution did not complete.');
  return result.status ?? 1;
}

function nodeTests(unfiltered) {
  const selection = args.length > 0 ? args : unfiltered ? [] : ['src', 'tests'];
  console.log(`Native Node ${unfiltered ? 'diagnostic' : 'selection'}; physical temp: ${temp}`);
  return run(process.execPath, [
    path.join(root, 'node_modules/vitest/vitest.mjs'),
    'run',
    '--maxWorkers=2',
    '--minWorkers=1',
    ...selection,
  ]);
}

function runtimeTests(unfiltered) {
  const directory = path.join(root, '.local/native-tests');
  mkdirSync(directory, { recursive: true });
  const results = mkdtempSync(path.join(directory, 'runtime-'));
  const selected =
    unfiltered || args.some((arg) => arg === '--filter' || arg.startsWith('--filter='))
      ? []
      : ['--filter', focusedRuntimeFilter];
  console.log(`Native .NET ${unfiltered ? 'diagnostic' : 'selection'}; physical temp: ${temp}`);
  const status = run('dotnet', [
    'test',
    path.join(root, 'runtime/tests/AgenticPrReview.Runtime.Tests'),
    '--nologo',
    '--maxcpucount:1',
    ...selected,
    ...args,
    '--logger',
    'trx;LogFileName=results.trx',
    '--results-directory',
    results,
  ]);
  try {
    const xml = readFileSync(path.join(results, 'results.trx'), 'utf8');
    const counters = xml.match(/<Counters\b[^>]*\/>/u)?.[0] ?? '';
    const count = (name) =>
      Number(counters.match(new RegExp(`\\b${name}="(\\d+)"`, 'u'))?.[1] ?? 0);
    const total = count('total');
    const executed = count('executed');
    console.log(
      `Runner counts: total=${total} executed=${executed} passed=${count('passed')} failed=${count('failed')} not-executed=${total - executed}`,
    );
    console.log(`TRX: ${path.join(results, 'results.trx')}`);
    if (total === 0 || executed === 0) {
      console.error(
        'No tests executed; discovery, an empty filter or a blocked runner is not a pass.',
      );
      return status || 1;
    }
  } catch {
    console.error('No complete TRX result; native test execution is unverified.');
    return status || 1;
  }
  return status;
}

if (suite === 'all') {
  const nodeStatus = nodeTests(false);
  const runtimeStatus = runtimeTests(false);
  process.exitCode = nodeStatus || runtimeStatus;
} else {
  process.exitCode = suite.startsWith('node')
    ? nodeTests(suite === 'node-all')
    : runtimeTests(suite === 'runtime-all');
}
