import { describe, expect, it, vi } from 'vitest';
import {
  computeSelectionSnapshotId,
  ContractValidationError,
  StoreCorruptionError,
  StoreTransactionError,
  type StateSelectionSnapshot,
} from './state-acceptance/index.js';
import {
  actionSourceIsTrustedDefaultBranchAncestor,
  ledgerStateKey,
  LEDGER_TRUSTED_EXECUTION_DOMAIN,
  LEDGER_WORKFLOW_IDENTITY,
  ledgerErrorKindFor,
  planLedgerInvocation,
  targetForLedgerContinuation,
  VERIFICATION_TRUSTED_EXECUTION_DOMAIN,
  VERIFICATION_WORKFLOW_IDENTITY,
} from './ledger-csharp.js';
import type { ActionConfig, ReviewTarget } from './types.js';
import { LiveRuntimeInvocationError } from './live-runtime-invocation/errors.js';

const config: ActionConfig = {
  runtimeBackend: 'ledger-csharp',
  runtimeProvider: 'test',
  targetMode: 'pull-request',
  reviewMode: 'auto',
  artifactRetentionDays: 7,
  postComment: false,
  apiKeyMode: 'auth-token',
  toolMode: 'none',
  claudeMaxTurns: 6,
  maxContextChars: 60_000,
  maxPatchChars: 120_000,
  maxReviewChars: 12_000,
  maxFindings: 50,
  inlineComments: false,
  maxInlineComments: 5,
  inlineMinSeverity: 'medium',
  inlineMinConfidence: 'high',
  testRuntimeFixture: 'valid',
  usageBudgetLimits: {
    maxUncachedInputTokens: 0,
    maxCachedInputTokens: 0,
    maxOutputTokens: 0,
  },
  disablePromptCaching: false,
  debugCaptureRawApiBodies: false,
  githubToken: 'test-token',
};

const target: ReviewTarget = {
  mode: 'pull-request',
  prNumber: 53,
  title: 'M4 stateful review',
  body: '',
  baseRef: 'main',
  baseSha: 'a'.repeat(40),
  headRef: 'feature/m4',
  headSha: 'b'.repeat(40),
  headRepoFullName: 'owner/repo',
  draft: false,
  changedFiles: [],
};

describe('ledger-csharp host plan', () => {
  it('maps store, corruption, and unknown failures to the frozen state_error_kind vocabulary', () => {
    expect(ledgerErrorKindFor(new StoreTransactionError('store_capability_unsupported'))).toBe(
      'store_capability_unsupported',
    );
    expect(ledgerErrorKindFor(new StoreTransactionError('store_transaction_failed'))).toBe(
      'store_transaction_failed',
    );
    expect(ledgerErrorKindFor(new ContractValidationError('schema_version_invalid'))).toBe(
      'store_corrupt',
    );
    expect(ledgerErrorKindFor(new StoreCorruptionError())).toBe('store_corrupt');
    expect(ledgerErrorKindFor(new Error('unclassified runtime failure'))).toBe('runtime_failed');
  });

  it('preserves closed live-provider failure kinds at the ledger action boundary', () => {
    for (const kind of [
      'provider-timeout',
      'provider-cancelled',
      'provider-rate-limited',
      'provider-4xx',
      'provider-5xx',
      'provider-transport',
      'provider-response',
      'provider-config',
      'provider-persistence',
    ] as const) {
      expect(
        ledgerErrorKindFor(
          new LiveRuntimeInvocationError({ kind, message: `Live provider failed (${kind}).` }),
        ),
      ).toBe(kind);
    }
  });

  it('accepts only an action source that is an ancestor of the trusted default branch', async () => {
    const compareCommits = async ({ base }: { base: string }) => ({
      data: { status: base === 'a'.repeat(40) ? 'ahead' : 'diverged' },
    });
    const octokit = { rest: { repos: { compareCommits } } };

    await expect(
      actionSourceIsTrustedDefaultBranchAncestor(
        octokit,
        'owner/repo',
        'a'.repeat(40),
        'b'.repeat(40),
      ),
    ).resolves.toBe('trusted');
    await expect(
      actionSourceIsTrustedDefaultBranchAncestor(
        octokit,
        'owner/repo',
        'c'.repeat(40),
        'b'.repeat(40),
      ),
    ).resolves.toBe('untrusted');

    await expect(
      actionSourceIsTrustedDefaultBranchAncestor(
        { rest: { repos: { compareCommits: async () => Promise.reject(new Error('offline')) } } },
        'owner/repo',
        'a'.repeat(40),
        'b'.repeat(40),
      ),
    ).resolves.toBe('unknown');
  });

  it('uses the frozen M4 state identity and bootstrap transition', () => {
    const stateKey = ledgerStateKey('owner/repo', 53);
    const draft = {
      schemaVersion: 1 as const,
      kind: 'bootstrap_selected' as const,
      transitionPlan: 'bootstrap' as const,
      stateKey,
      currentHeadSha: target.headSha,
      currentBaseSha: target.baseSha,
      currentBaseRef: 'refs/heads/main',
      observedSelectorBytes: null,
      observedSelectorRevision: 'bootstrap' as const,
      observedSelectorSnapshotSha256: 'c'.repeat(64),
      selectionSnapshotId: '' as never,
    };
    const selection = {
      ...draft,
      selectionSnapshotId: computeSelectionSnapshotId(draft as unknown as StateSelectionSnapshot),
    } as StateSelectionSnapshot;

    const plan = planLedgerInvocation({
      config,
      target,
      stateKey,
      selection: selection as Exclude<
        StateSelectionSnapshot,
        { readonly kind: 'explicit_restore_invalid' }
      >,
      eventName: 'workflow_run',
    });

    expect(plan.phase).toBe('bootstrap');
    expect(plan.context.stateKey).toMatchObject({
      namespace: 'm4-ledger-v2',
      workflowIdentity: LEDGER_WORKFLOW_IDENTITY,
      trustedExecutionDomain: LEDGER_TRUSTED_EXECUTION_DOMAIN,
    });
    expect(plan.context.cacheContractIdentity).toMatchObject({
      providerId: 'provider',
      modelId: 'model-2024-01-01',
    });
    expect(plan.context.transition).toMatchObject({ kind: 'bootstrap' });
  });

  it('isolates an administrator verification namespace from production state', () => {
    const production = ledgerStateKey('owner/repo', 53);
    const verification = ledgerStateKey('owner/repo', 53, 'smoke-1');

    expect(verification.workflowIdentity).toBe(`${VERIFICATION_WORKFLOW_IDENTITY}/smoke-1`);
    expect(verification.trustedExecutionDomain).toBe(
      `${VERIFICATION_TRUSTED_EXECUTION_DOMAIN}/smoke-1`,
    );
    expect(verification.workflowIdentity).not.toBe(production.workflowIdentity);
  });

  it('uses the predecessor-to-head comparison rather than the cumulative PR diff for continuations', async () => {
    const predecessorHeadSha = 'c'.repeat(40);
    const compareCommits = vi.fn().mockResolvedValue({
      data: {
        status: 'ahead',
        files: [
          {
            filename: 'src/new-change.ts',
            status: 'modified',
            additions: 1,
            deletions: 1,
            changes: 2,
            patch: '@@ -1 +1 @@\n-old\n+new',
          },
        ],
      },
    });

    const selected = await targetForLedgerContinuation({
      target: {
        ...target,
        changedFiles: [
          {
            filename: 'src/cumulative.ts',
            status: 'modified',
            additions: 200,
            deletions: 0,
            changes: 200,
          },
        ],
      },
      previousHeadSha: predecessorHeadSha,
      octokit: { rest: { repos: { compareCommits } } },
      repository: 'owner/repo',
    });

    expect(compareCommits).toHaveBeenCalledWith(
      expect.objectContaining({ base: predecessorHeadSha, head: target.headSha }),
    );
    expect(selected.changedFiles).toEqual([
      expect.objectContaining({ filename: 'src/new-change.ts', patch: '@@ -1 +1 @@\n-old\n+new' }),
    ]);
  });

  it('fails closed at the GitHub compare file truncation boundary', async () => {
    await expect(
      targetForLedgerContinuation({
        target,
        previousHeadSha: 'c'.repeat(40),
        octokit: {
          rest: {
            repos: {
              compareCommits: async () => ({
                data: {
                  status: 'ahead',
                  files: Array.from({ length: 300 }, (_, index) => ({
                    filename: `src/file-${index}.ts`,
                    status: 'modified',
                    additions: 1,
                    deletions: 0,
                    changes: 1,
                  })),
                },
              }),
            },
          },
        },
        repository: 'owner/repo',
      }),
    ).rejects.toMatchObject({ errorKind: 'target_revalidation_failed' });
  });
});
