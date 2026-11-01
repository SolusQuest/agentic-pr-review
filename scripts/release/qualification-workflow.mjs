// Closed admission for V2 only. The parent verifier still checks every old lane.
import assert from 'node:assert/strict';

export const qualifierJob = {
  if: "github.event_name == 'workflow_dispatch' && github.repository == 'SolusQuest/agentic-pr-review' && github.ref == 'refs/heads/main' && inputs.qualify-pair",
  'runs-on': 'ubuntu-24.04',
  'timeout-minutes': 150,
  permissions: {
    contents: 'read',
    actions: 'read',
  },
  steps: [
    {
      uses: 'actions/checkout@v6',
      with: {
        ref: '${{ github.workflow_sha }}',
        path: 'control',
        'fetch-depth': 0,
        'persist-credentials': false,
      },
    },
    {
      uses: 'actions/setup-node@v6',
      with: {
        'node-version': 24,
        cache: 'npm',
        'cache-dependency-path': 'control/package-lock.json',
      },
    },
    {
      name: 'Install locked qualifier tooling',
      'working-directory': 'control',
      run: 'npm ci',
    },
    {
      name: 'Admit exact main/manual Q and pair before artifact access',
      id: 'request',
      env: {
        GITHUB_TOKEN: '${{ github.token }}',
        QUALIFIER_COMMIT: '${{ inputs.qualifier-commit }}',
        QUALIFICATION_PAIR: '${{ inputs.qualification-pair }}',
        QUALIFICATION_PAIR_SHA256: '${{ inputs.qualification-pair-sha256 }}',
        QUALIFICATION_REQUEST: '${{ inputs.qualification-request }}',
      },
      run: 'node control/scripts/release/qualify-pair.mjs admit --output "$RUNNER_TEMP/r7-v2-inputs"',
    },
    {
      uses: 'actions/download-artifact@v8',
      with: {
        'artifact-ids': '${{ steps.request.outputs.payload_id }}',
        'run-id': '${{ steps.request.outputs.producer_run_id }}',
        path: '${{ runner.temp }}/r7-v2-artifacts/payload',
        'merge-multiple': true,
        'github-token': '${{ github.token }}',
        'skip-decompress': false,
        'digest-mismatch': 'error',
      },
    },
    {
      uses: 'actions/download-artifact@v8',
      with: {
        'artifact-ids': '${{ steps.request.outputs.metadata_id }}',
        'run-id': '${{ steps.request.outputs.producer_run_id }}',
        path: '${{ runner.temp }}/r7-v2-artifacts/metadata',
        'merge-multiple': true,
        'github-token': '${{ github.token }}',
        'skip-decompress': false,
        'digest-mismatch': 'error',
      },
    },
    {
      uses: 'actions/download-artifact@v8',
      with: {
        'artifact-ids': '${{ steps.request.outputs.provenance_id }}',
        'run-id': '${{ steps.request.outputs.producer_run_id }}',
        path: '${{ runner.temp }}/r7-v2-artifacts/provenance',
        'merge-multiple': true,
        'github-token': '${{ github.token }}',
        'skip-decompress': false,
        'digest-mismatch': 'error',
      },
    },
    {
      name: 'Install exact maintained provenance verifier',
      run: 'curl --fail --silent --show-error --location --proto \'=https\' --tlsv1.2 https://github.com/cli/cli/releases/download/v2.103.3/gh_2.103.3_linux_amd64.tar.gz --output "$RUNNER_TEMP/r7-v2-gh.tar.gz"\necho "4c806978ffce1cec70853302a56d70ac6da4f19bae7adca4e1a8ab00633dd91d  $RUNNER_TEMP/r7-v2-gh.tar.gz" | sha256sum --check --strict\nmkdir "$RUNNER_TEMP/r7-v2-gh"\ntar -xzf "$RUNNER_TEMP/r7-v2-gh.tar.gz" --strip-components=2 -C "$RUNNER_TEMP/r7-v2-gh" gh_2.103.3_linux_amd64/bin/gh\n',
    },
    {
      uses: 'actions/setup-dotnet@v5',
      env: {
        DOTNET_INSTALL_DIR: '${{ runner.temp }}/r7-v2-dotnet',
      },
      with: {
        'dotnet-version': '10.0.109',
      },
    },
    {
      name: 'Install isolated-consumer observation dependencies',
      run: 'sudo apt-get update\nsudo apt-get install -y gcc strace\n',
    },
    {
      name: 'Execute unchanged original B and exact composite T in isolated consumer',
      id: 'qualify',
      env: {
        GITHUB_TOKEN: '${{ github.token }}',
        QUALIFIER_COMMIT: '${{ inputs.qualifier-commit }}',
        QUALIFICATION_PAIR_SHA256: '${{ inputs.qualification-pair-sha256 }}',
      },
      run: 'PATH="$RUNNER_TEMP/r7-v2-gh:$PATH" node control/scripts/release/qualify-pair.mjs run --inputs "$RUNNER_TEMP/r7-v2-inputs" --artifacts "$RUNNER_TEMP/r7-v2-artifacts" --output "$RUNNER_TEMP/r7-v2-evidence"',
    },
    {
      name: 'Retain only validated bounded qualification evidence',
      if: "${{ always() && steps.qualify.outputs.evidence_ready == 'true' }}",
      uses: 'actions/upload-artifact@v7',
      with: {
        name: 'r7-v2-qualification-${{ github.run_id }}-${{ github.run_attempt }}',
        path: '${{ runner.temp }}/r7-v2-evidence/',
        archive: true,
        'compression-level': 0,
        'if-no-files-found': 'error',
        'include-hidden-files': false,
        overwrite: false,
        'retention-days': 7,
      },
    },
  ],
};
const inputPolicy = {
  'qualify-pair': {
    required: false,
    type: 'boolean',
    default: false,
  },
  'qualifier-commit': {
    required: false,
    type: 'string',
  },
  'qualification-pair': {
    required: false,
    type: 'string',
  },
  'qualification-pair-sha256': {
    required: false,
    type: 'string',
  },
  'qualification-request': {
    required: false,
    type: 'string',
  },
};

export function verifyQualificationWorkflow(workflow) {
  assert.deepEqual(
    workflow.jobs['qualify-original-pair'],
    qualifierJob,
    'The exact-pair qualification route drifted',
  );
  const inputs = workflow.on.workflow_dispatch?.inputs;
  assert.ok(inputs && typeof inputs === 'object');
  const observed = Object.fromEntries(
    Object.entries(inputs).map(([key, value]) => {
      const { description, ...policy } = value;
      assert.ok(
        typeof description === 'string' && description.length > 0 && description.length <= 200,
      );
      return [key, policy];
    }),
  );
  assert.deepEqual(observed, inputPolicy, 'Qualification dispatch inputs drifted');
  return true;
}
