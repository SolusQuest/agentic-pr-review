// Semantic admission of exactly one dormant proof route. No hash-only bypass.
import { isDeepStrictEqual } from 'node:util';
import {
  actionSource,
  acknowledgement,
  actor,
  actorId,
  limits,
  repository,
  repositoryId,
  requireProof,
} from './cr2-hosted-artifact-policy.mjs';

const checkout = 'actions/checkout@9c091bb21b7c1c1d1991bb908d89e4e9dddfe3e0';
const dotnet = 'actions/setup-dotnet@26b0ec14cb23fa6904739307f278c14f94c95bf1';
const node = 'actions/setup-node@249970729cb0ef3589644e2896645e5dc5ba9c38';
function validateProofLimits() {
  requireProof(
    isDeepStrictEqual(limits, {
      encryptedBytes: 4096,
      carrierBytes: 8192,
      zipBytes: 16384,
      apiPerRun: 16,
      anonymousPerRun: 2,
      metadataPerRun: 524288,
      zipDownloadsPerRun: 32768,
      preflightRecords: 99,
      censusRecords: 100,
      listsA: 2,
      listsB: 1,
      uploadCalls: 1,
      deleteCalls: 1,
      retentionDays: 1,
      sdkCreateAttempts: 5,
      sdkFinalizeAttempts: 5,
      blobWriteAttempts: 8,
      resultsRedirectsPerAttempt: 50,
      blobRedirectsPerAttempt: 20,
      sdkHttpRequests: 678,
      uploadBodyBytes: 16777216,
      jobMinutes: 10,
      pairMinutes: 30,
    }),
  );
}
export function validateCr2HostedArtifactWorkflow(value) {
  validateProofLimits();
  const gate = [
    "github.event_name == 'workflow_dispatch' &&",
    `github.repository == '${repository}' &&`,
    `github.repository_id == '${repositoryId}' &&`,
    `github.actor == '${actor}' && github.actor_id == '${actorId}' &&`,
    "github.run_attempt == 1 && github.ref == 'refs/heads/main' &&",
    'inputs.expected-head-sha == github.sha && inputs.expected-head-sha == github.workflow_sha &&',
    `inputs.finite-approval == '${acknowledgement}'`,
  ].join(' ');
  const expected = {
    name: 'CR2 finite hosted artifact proof (manual, dormant)',
    on: {
      workflow_dispatch: {
        inputs: {
          'expected-head-sha': {
            description: 'Exact merged source separately approved for H2',
            required: true,
            type: 'string',
          },
          phase: {
            description: 'Two separately initiated runs, produce then restore',
            required: true,
            type: 'choice',
            options: ['produce', 'restore'],
          },
          'finite-approval': {
            description: 'Acknowledgement after separate user approval; never creates authority',
            required: true,
            type: 'string',
          },
          'producer-receipt': {
            description: 'Sanitized exact Run A receipt; required only for restore',
            required: false,
            default: '',
            type: 'string',
          },
        },
      },
    },
    permissions: {},
    jobs: {
      proof: {
        if: gate,
        'runs-on': 'ubuntu-24.04',
        'timeout-minutes': limits.jobMinutes,
        permissions: { contents: 'read', actions: 'write' },
        steps: [
          {
            uses: checkout,
            with: { ref: '${{ github.sha }}', 'fetch-depth': 0, 'persist-credentials': false },
          },
          { uses: dotnet, with: { 'dotnet-version': '10.0.109' } },
          { uses: node, with: { 'node-version': '24', architecture: 'x64' } },
          { run: 'npm ci' },
          {
            name: 'Bind finite source and build public-only transport companion',
            id: 'setup',
            env: {
              PROOF_MODE: 'hosted',
              PROOF_PHASE: '${{ inputs.phase }}',
              EXPECTED_HEAD: '${{ inputs.expected-head-sha }}',
              WORKFLOW_SHA: '${{ github.workflow_sha }}',
              PROOF_APPROVAL: '${{ inputs.finite-approval }}',
              PROOF_RECEIPT: '${{ inputs.producer-receipt }}',
            },
            run: 'node scripts/cr2-hosted-artifact-proof.mjs prepare',
          },
          {
            name: 'Actual immutable composite and official SDK child',
            id: 'transport',
            uses: `${repository}/.github/actions/agentic-pr-review@${actionSource}`,
            with: { 'github-token': '${{ secrets.GITHUB_TOKEN }}' },
            env: {
              APR_CR2_H1_CONFIG: '${{ steps.setup.outputs.config }}',
              NODE_OPTIONS: '${{ steps.setup.outputs.preload }}',
            },
          },
          {
            name: 'Verify exact transport receipt (no original archive or SESSION claim)',
            if: "always() && steps.setup.outcome == 'success'",
            env: {
              PROOF_CONFIG: '${{ steps.setup.outputs.config }}',
              PROOF_ACTION_OUTCOME: '${{ steps.transport.outcome }}',
            },
            run: 'node scripts/cr2-hosted-artifact-proof.mjs verify "$PROOF_CONFIG"',
          },
          {
            name: 'Stop only the owned loopback acquisition fixture',
            if: "always() && steps.setup.outcome == 'success'",
            env: { PROOF_CONFIG: '${{ steps.setup.outputs.config }}' },
            run: 'node scripts/cr2-hosted-artifact-proof.mjs stop "$PROOF_CONFIG"',
          },
        ],
      },
    },
  };
  // Explicit semantic structure locks every route/action/secret/permission/step;
  // unknown fields and appended routes fail as well as weakened guards.
  requireProof(isDeepStrictEqual(value, expected));
}

export function validateCr2CredentialFreeWorkflow(value) {
  validateProofLimits();
  requireProof(
    isDeepStrictEqual(value, {
      name: 'CR2 credential-free artifact rehearsal',
      on: { pull_request: null, push: { branches: ['main'] } },
      permissions: { contents: 'read' },
      jobs: {
        synthetic: {
          'runs-on': 'ubuntu-24.04',
          'timeout-minutes': 20,
          steps: [
            { uses: checkout, with: { 'fetch-depth': 0, 'persist-credentials': false } },
            { uses: dotnet, with: { 'dotnet-version': '10.0.109' } },
            { uses: node, with: { 'node-version': '24', architecture: 'x64' } },
            { run: 'npm ci' },
            { run: 'sudo apt-get update && sudo apt-get install -y strace' },
            {
              name: 'Closed synthetic service, two fresh processes, no service credentials',
              run: 'node scripts/cr2-hosted-artifact-rehearse.mjs',
            },
            {
              name: 'Authority, source, refusal and workflow controls',
              run: 'npm test -- scripts/cr2-hosted-artifact-policy.test.ts scripts/cr2-hosted-artifact-controls.test.ts --maxWorkers=2 --minWorkers=1',
            },
          ],
        },
      },
    }),
  );
}
