import crypto from 'node:crypto';
import fs from 'node:fs';

function fail(code) {
  process.stderr.write(`APR_R4_E2P_ARCHITECTURE_INVALID ${code}\n`);
  process.exit(1);
}

function sha256(bytes) {
  return crypto.createHash('sha256').update(bytes).digest('hex');
}

function searchable(bytes) {
  const utf8 = bytes.toString('latin1');
  const utf16 = bytes.toString('utf16le');
  return `${utf8}\n${utf16}`;
}

const names = ['--proof', '--runtime', '--output'];
if (process.argv.length !== 2 + names.length * 2) fail('usage');
const options = new Map();
for (let index = 2; index < process.argv.length; index += 2) {
  if (!names.includes(process.argv[index]) || options.has(process.argv[index])) fail('arguments');
  options.set(process.argv[index], process.argv[index + 1]);
}
const proof = fs.readFileSync(options.get('--proof'));
const runtime = fs.readFileSync(options.get('--runtime'));
const proofText = searchable(proof);
const runtimeText = searchable(runtime);
const requiredProof = [
  'TrustedProofPayloadHost',
  'TrustedProofPayloadComposition',
  'TrustedProofDeterministicDeepSeekHandler',
  'TrustedProofControlTransport',
  'TrustedProofControlService',
  'TrustedProofStaleSignal',
  'ActionHostCompositionDependencies',
  'ActionHostDeepSeekProviderRunnerFactory',
  'DeepSeekTransport',
  'ActionHostGitHubAuthorizationTransportFactory',
  'AcceptedStateProductionDependencies',
  'BoundedGitHubPublisherTransportFactory',
  'PostAcceptanceInlinePublisherHook',
  'TrustedProofControlJsonContext',
];
const requiredRuntime = [
  'ActionHostComposition',
  'ActionHostCoordinator',
  'ActionHostTrustedWorkflowPolicy',
  'ActionHostTrustedWorkflowContract',
  'DeepSeekReasoningContinuationCodec',
  'ExactHeadRevalidation',
];
const forbiddenProof = [
  'ActionHostVerifierFixture',
  'FrameworkGitHubHandler',
  'FrameworkStateDependencies',
  'FrameworkTimeProvider',
  'FrameworkProviderHandler',
  'LiveAgentVerifierFixture',
  'System.Reflection.Emit',
  'Assembly.Load',
];
for (const name of requiredProof) if (!proofText.includes(name)) fail(`missing-proof-${name}`);
for (const name of requiredRuntime)
  if (!runtimeText.includes(name)) fail(`missing-runtime-${name}`);
for (const name of forbiddenProof) if (proofText.includes(name)) fail(`forbidden-${name}`);
const report = {
  kind: 'apr-r4-e2p-managed-architecture-v1',
  proof_managed_sha256: sha256(proof),
  runtime_managed_sha256: sha256(runtime),
  required_proof_types: requiredProof,
  required_runtime_types: requiredRuntime,
  forbidden_proof_families: forbiddenProof,
  result: 'passed',
};
fs.writeFileSync(options.get('--output'), `${JSON.stringify(report)}\n`, {
  flag: 'wx',
});
process.stdout.write('APR_R4_E2P_ARCHITECTURE_OK\n');
