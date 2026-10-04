// NEW D0 protocol smoke through the checked wrapper. The child is the actual
// production Runtime, never a linked Host or fake provider. Full review is V2.
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { chmod, copyFile, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { build } from 'esbuild';

assert.equal(process.platform, 'linux', 'checked fd launcher smoke requires Linux');
const [mode, executable, assembly] = process.argv.slice(2);
assert.ok(mode === 'framework' || mode === 'native');
assert.ok(executable && (mode !== 'framework' || assembly));
const discriminator = 'r7-d0';
const profileVariable = 'AGENTIC_PR_REVIEW_R4_REQUEST_BUDGET_PROFILE';
const oldProfile = process.env[profileVariable];
const repo = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const root = await mkdtemp(join(tmpdir(), 'apr-d0-wrapper-'));
const canary = 'PRIVATE_D0_WRAPPER_CANARY';
const stateKey = Buffer.alloc(32, 71).toString('base64');
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');
const shellQuote = (value) => `'${value.replaceAll("'", "'\"'\"'")}'`;
let cases = 0;

try {
  const bundle = join(root, 'checked-wrapper.cjs');
  await build({
    absWorkingDir: repo,
    stdin: {
      contents: `
        export { runPrivateActionWrapperWithSeams } from './src/action-wrapper/index.ts';
        export { runHostProcess } from './src/action-wrapper/launcher/host-process.ts';
        export { startArtifactBridgeRuntime } from './src/action-wrapper/launcher/bridge-runtime.ts';
        export { parseCompletionDocument } from './src/action-wrapper/presentation/completion.ts';
        export { projectCompletionOutputs, FIXED_WRAPPER_FAILURE_OUTPUTS } from './src/action-wrapper/presentation/outputs.ts';`,
      resolveDir: repo,
      loader: 'ts',
    },
    outfile: bundle,
    bundle: true,
    format: 'cjs',
    platform: 'node',
    target: 'node24',
    logLevel: 'silent',
    plugins: [
      {
        name: 'optional-debug-color-stub',
        setup(context) {
          context.onResolve({ filter: /^supports-color$/ }, () => ({
            path: 'supports-color',
            namespace: 'color-stub',
          }));
          context.onLoad({ filter: /.*/, namespace: 'color-stub' }, () => ({
            contents: 'module.exports = false;',
            loader: 'js',
          }));
        },
      },
    ],
  });
  const wrapper = createRequire(import.meta.url)(bundle);
  const payload = join(root, 'payload');
  // Framework tooling shim only: exec the real DLL, preserving its zero argv,
  // streams and process signals. Native executes the exact binary through fd 3.
  if (mode === 'framework') {
    await writeFile(
      payload,
      `#!/bin/sh\nexec ${shellQuote(resolve(executable))} ${shellQuote(resolve(assembly))}\n`,
    );
  } else await copyFile(resolve(executable), payload);
  await chmod(payload, 0o700);
  const runtimeSha256 = hash(await readFile(resolve(mode === 'native' ? executable : assembly)));
  const proof = {
    trustedRoot: root,
    executableRelativePath: 'payload',
    actionSourceSha: 'b'.repeat(40),
    payloadSha256: hash(await readFile(payload)),
    buildDiscriminator: discriminator,
    wrapperBuildDiscriminator: discriminator,
  };
  const event = join(root, 'event.json');
  await writeFile(
    event,
    JSON.stringify({
      inputs: { 'pr-number': '341' },
      repository: { id: 42, full_name: 'SolusQuest/agentic-pr-review' },
      sender: { id: 7, login: 'maintainer' },
    }),
  );
  const facts = {
    eventJsonPath: event,
    repositoryName: 'SolusQuest/agentic-pr-review',
    repositoryId: '42',
    runId: '900',
    runAttempt: '1',
    workflowPath: '.github/workflows/r4-trusted-proof.yml',
    workflowRef:
      'SolusQuest/agentic-pr-review/.github/workflows/r4-trusted-proof.yml@refs/heads/main',
    workflowSha: 'a'.repeat(40),
  };
  const scenarios = [
    { name: 'ordinary', status: 'credentials_missing' },
    { name: 'ambient-measurement', profile: 'measurement', status: 'credentials_missing' },
    { name: 'ambient-final', profile: 'final-bootstrap', status: 'credentials_missing' },
    { name: 'ambient-invalid', profile: 'invalid', status: 'credentials_missing' },
    { name: 'requested-cancellation', status: 'cancelled' },
    { name: 'event-denial', status: 'authorization_failed' },
    { name: 'malformed-completion', invalidCompletion: true },
    { name: 'foreign-completion', invalidCompletion: true },
  ];
  for (const scenario of scenarios) {
    if (scenario.profile === undefined) delete process.env[profileVariable];
    else process.env[profileVariable] = scenario.profile;
    const outputs = {};
    const summaries = [];
    const errors = [];
    const receipts = [];
    const masked = [];
    const input = {
      'provider-api-key': canary,
      'state-key': stateKey,
      'config-path': canary,
      'pr-number': '341',
    };
    let completion;
    let spawned = 0;
    let bridgeCommands = 0;
    let cleaned = false;
    let timedOut = false;
    const controller = new AbortController();
    const timeout = setTimeout(() => {
      timedOut = true;
      controller.abort();
    }, 15_000);
    try {
      const exit = await wrapper.runPrivateActionWrapperWithSeams({
        toolkit: {
          getInput: (name) => input[name] ?? '',
          setSecret: (secret) => masked.push(secret),
          setOutput: (name, value) => {
            outputs[name] = value;
          },
          writeSummary: async (text) => {
            summaries.push(text);
          },
          warning: (text) => {
            errors.push(text);
          },
          error: (text) => {
            errors.push(text);
          },
        },
        preparedPayload: proof,
        platform: 'linux',
        signal: controller.signal,
        runtimeFacts: () => facts,
        bridgeRuntime: async (request) => {
          const bridge = await wrapper.startArtifactBridgeRuntime(request);
          return {
            ...bridge,
            cleanup: async () => {
              await bridge.cleanup();
              cleaned = true;
            },
          };
        },
        createArtifactExecutor: async (context) => {
          assert.equal(context.artifactRestRequestBudget.protectedRoute, false);
          return {
            execute: async () => {
              bridgeCommands++;
              throw new Error('unexpected credential-free command');
            },
          };
        },
        hostProcessRunner: async (request) => {
          spawned++;
          assert.equal(request.requestBudgetProfile, undefined);
          const launch = JSON.parse(Buffer.from(request.launchBytes).toString('utf8'));
          assert.equal(launch.build_discriminator, discriminator);
          if (scenario.name === 'requested-cancellation') launch.cancellation = 'requested';
          if (scenario.name === 'event-denial') launch.event_json_sha256 = '0'.repeat(64);
          const result = await wrapper.runHostProcess({
            ...request,
            launchBytes: Buffer.from(JSON.stringify(launch)),
            // Supervisor recovery bounds only; production defaults are unchanged.
            cancellationKillGraceMs: 1_500,
            postKillCloseGraceMs: 1_000,
          });
          assert.deepEqual(result.trustedProofBudgetReceiptLines, []);
          completion = wrapper.parseCompletionDocument(
            result.completionBytes,
            discriminator,
            result.exitCode,
          );
          if (scenario.name === 'malformed-completion')
            return { ...result, completionBytes: Buffer.from(canary) };
          if (scenario.name === 'foreign-completion')
            return {
              ...result,
              completionBytes: Buffer.from(
                JSON.stringify({ ...completion, build_discriminator: 'r4-w2' }),
              ),
            };
          return result;
        },
        trustedProofBudgetReceiptSink: (frame) => receipts.push(frame),
        fatalExit: () => {
          throw new Error('unexpected fatal exit');
        },
      });
      assert.equal(timedOut, false);
      assert.equal(spawned, 1);
      assert.ok(
        completion,
        `${scenario.name}: actual Runtime did not return an admitted completion`,
      );
      assert.equal(cleaned, true);
      assert.equal(bridgeCommands, 0);
      assert.deepEqual(receipts, []);
      assert.ok(masked.includes(canary));
      assert.equal(exit, 1);
      if (scenario.invalidCompletion) {
        assert.deepEqual(outputs, wrapper.FIXED_WRAPPER_FAILURE_OUTPUTS);
        assert.deepEqual(errors, ['The private review wrapper failed.']);
      } else {
        assert.equal(completion.status, scenario.status);
        assert.equal(completion.termination_reason, 'not_started');
        assert.equal(completion.accounting.provider_attempts, '0');
        assert.equal(completion.accounting.attempt_accounting_completeness, 'complete');
        assert.deepEqual(outputs, wrapper.projectCompletionOutputs(completion));
        assert.equal(summaries.length, 1);
        assert.ok(summaries[0].includes(`| Status | ${scenario.status} |`));
        assert.deepEqual(errors, [completion.annotations[0].message]);
      }
      const presentation = JSON.stringify({ outputs, summaries, errors, receipts });
      for (const secret of [canary, stateKey, root])
        assert.equal(presentation.includes(secret), false);
      cases++;
    } finally {
      clearTimeout(timeout);
    }
  }
  console.log(
    `D0_CHECKED_WRAPPER mode=${mode} real_process_cases=${cases} runtime_sha256=${runtimeSha256} framework_tooling_shim=${mode === 'framework'}`,
  );
} finally {
  if (oldProfile === undefined) delete process.env[profileVariable];
  else process.env[profileVariable] = oldProfile;
  await rm(root, { recursive: true, force: true });
}
