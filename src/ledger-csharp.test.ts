import { describe, expect, it } from 'vitest';
import {
  computeSelectionSnapshotId,
  type StateSelectionSnapshot,
} from './state-acceptance/index.js';
import {
  actionSourceIsTrustedDefaultBranchAncestor,
  ledgerStateKey,
  LEDGER_TRUSTED_EXECUTION_DOMAIN,
  LEDGER_WORKFLOW_IDENTITY,
  planLedgerInvocation,
  VERIFICATION_TRUSTED_EXECUTION_DOMAIN,
  VERIFICATION_WORKFLOW_IDENTITY,
} from './ledger-csharp.js';
import type { ActionConfig, ReviewTarget } from './types.js';

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
    ).resolves.toBe(true);
    await expect(
      actionSourceIsTrustedDefaultBranchAncestor(
        octokit,
        'owner/repo',
        'c'.repeat(40),
        'b'.repeat(40),
      ),
    ).resolves.toBe(false);
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
});
