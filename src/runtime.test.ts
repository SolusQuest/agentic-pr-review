import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  allowedToolsForMode,
  buildClaudeArgs,
  computeLineageTotals,
  preserveLineageTotalsForSkipped,
  RuntimeObservationTracker,
  TestRuntime,
  UsageBudgetExceededError,
  UsageTracker,
} from './runtime.js';
import { type ActionConfig, type RestoredState, type ReviewTarget } from './types.js';

function observe(tracker: UsageTracker, value: unknown): void {
  tracker.observeLine(JSON.stringify(value));
}

function disabledTracker(): UsageTracker {
  return new UsageTracker({
    maxUncachedInputTokens: 0,
    maxCachedInputTokens: 0,
    maxOutputTokens: 0,
  });
}

function target(): ReviewTarget {
  return {
    mode: 'pull-request',
    prNumber: 1,
    title: 'Synthetic PR',
    body: 'Synthetic body',
    baseRef: 'main',
    baseSha: 'base-sha',
    headRef: 'branch',
    headSha: 'head-sha',
    headRepoFullName: 'example/repo',
    draft: false,
    changedFiles: [
      {
        filename: 'src/file.ts',
        status: 'modified',
        additions: 3,
        deletions: 0,
        changes: 3,
        patch: '@@ -10,2 +10,3 @@\n context\n+added\n context',
      },
    ],
    htmlUrl: 'https://github.com/example/repo/pull/1',
  };
}

describe('Claude Code runtime arguments', () => {
  it('keeps default tool mode as no-tool', () => {
    const allowedTools = allowedToolsForMode('none');
    const args = buildClaudeArgs({
      config: { modelName: 'model', claudeMaxTurns: 6, toolMode: 'none' },
      phase: 'bootstrap',
      sessionName: 'session',
      allowedTools,
    });
    expect(allowedTools).toEqual([]);
    expect(args[args.indexOf('--tools') + 1]).toBe('');
    expect(args).toContain('--strict-mcp-config');
    expect(args).toContain('--disable-slash-commands');
  });

  it('restricts readonly mode to Read, Glob, and Grep only', () => {
    const allowedTools = allowedToolsForMode('readonly');
    const args = buildClaudeArgs({
      config: { modelName: 'model', claudeMaxTurns: 4, toolMode: 'readonly' },
      phase: 'bootstrap',
      sessionName: 'session',
      allowedTools,
    });
    expect(allowedTools).toEqual(['Read', 'Glob', 'Grep']);
    expect(args[args.indexOf('--tools') + 1]).toBe('Read,Glob,Grep');
    expect(args[args.indexOf('--max-turns') + 1]).toBe('4');
    for (const forbidden of ['Bash', 'PowerShell', 'Edit', 'Write', 'Agent', 'Skill', 'WebFetch']) {
      expect(args[args.indexOf('--tools') + 1]).not.toContain(forbidden);
    }
  });
});

describe('TestRuntime', () => {
  it('accepts readonly tool mode without enabling real tools', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-runtime-'));
    try {
      const config: ActionConfig = {
        runtimeProvider: 'test',
        targetMode: 'synthetic-fixture',
        reviewMode: 'auto',
        artifactRetentionDays: 7,
        postComment: false,
        apiKeyMode: 'auth-token',
        toolMode: 'readonly',
        claudeMaxTurns: 6,
        maxContextChars: 1000,
        maxPatchChars: 1000,
        maxReviewChars: 1000,
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
        githubToken: 'token',
      };
      const result = await new TestRuntime().run({
        config,
        phase: 'bootstrap',
        stateKey: 'synthetic',
        prompt: 'review',
        promptHash: 'hash',
        workspace: dir,
        tempDir: dir,
        runtimeDir: path.join(dir, 'runtime'),
        target: target(),
      });
      expect(result.toolMode).toBe('readonly');
      expect(result.allowedTools).toEqual([]);
      expect(result.usageBudgetStatus.status).toBe('not_applicable');
      expect(result.modelReviewJson).toContain('"schemaVersion":1');
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });

  it('emits selectable structured fixtures', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-runtime-'));
    try {
      const baseConfig: ActionConfig = {
        runtimeProvider: 'test',
        targetMode: 'synthetic-fixture',
        reviewMode: 'auto',
        artifactRetentionDays: 7,
        postComment: false,
        apiKeyMode: 'auth-token',
        toolMode: 'none',
        claudeMaxTurns: 6,
        maxContextChars: 1000,
        maxPatchChars: 1000,
        maxReviewChars: 1000,
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
        githubToken: 'token',
      };
      for (const fixture of [
        'valid',
        'no_findings',
        'null_location',
        'many_findings',
        'inline_commentable',
        'inline_non_commentable',
        'inline_many_findings',
        'invalid_json',
        'schema_invalid',
      ] as const) {
        const result = await new TestRuntime().run({
          config: { ...baseConfig, testRuntimeFixture: fixture },
          phase: 'bootstrap',
          stateKey: `synthetic-${fixture}`,
          prompt: 'review',
          promptHash: `hash-${fixture}`,
          workspace: dir,
          tempDir: dir,
          runtimeDir: path.join(dir, 'runtime', fixture),
          target: target(),
        });
        if (fixture === 'invalid_json') {
          expect(result.modelReviewJson).toBe('this is not json');
        } else {
          expect(result.modelReviewJson).toContain('"schemaVersion":1');
        }
      }
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });

  it('emits inline smoke fixtures from target diff metadata', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-runtime-'));
    try {
      const baseConfig: ActionConfig = {
        runtimeProvider: 'test',
        targetMode: 'pull-request',
        reviewMode: 'auto',
        artifactRetentionDays: 7,
        postComment: true,
        apiKeyMode: 'auth-token',
        toolMode: 'none',
        claudeMaxTurns: 6,
        maxContextChars: 1000,
        maxPatchChars: 1000,
        maxReviewChars: 1000,
        maxFindings: 50,
        inlineComments: true,
        maxInlineComments: 5,
        inlineMinSeverity: 'medium',
        inlineMinConfidence: 'high',
        testRuntimeFixture: 'inline_commentable',
        usageBudgetLimits: {
          maxUncachedInputTokens: 0,
          maxCachedInputTokens: 0,
          maxOutputTokens: 0,
        },
        disablePromptCaching: false,
        debugCaptureRawApiBodies: false,
        githubToken: 'token',
      };
      const commentable = await new TestRuntime().run({
        config: baseConfig,
        phase: 'bootstrap',
        stateKey: 'inline-commentable',
        prompt: 'review',
        promptHash: 'hash-inline-commentable',
        workspace: dir,
        tempDir: dir,
        runtimeDir: path.join(dir, 'runtime', 'inline-commentable'),
        target: target(),
      });
      const commentableJson = JSON.parse(commentable.modelReviewJson);
      expect(commentableJson.findings[0]).toMatchObject({
        path: 'src/file.ts',
        startLine: 10,
        endLine: 10,
      });

      const nonCommentable = await new TestRuntime().run({
        config: { ...baseConfig, testRuntimeFixture: 'inline_non_commentable' },
        phase: 'bootstrap',
        stateKey: 'inline-non-commentable',
        prompt: 'review',
        promptHash: 'hash-inline-non-commentable',
        workspace: dir,
        tempDir: dir,
        runtimeDir: path.join(dir, 'runtime', 'inline-non-commentable'),
        target: target(),
      });
      const nonCommentableJson = JSON.parse(nonCommentable.modelReviewJson);
      expect(nonCommentableJson.findings[0]).toMatchObject({
        path: 'src/file.ts',
        startLine: 999999,
      });
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});

describe('UsageTracker', () => {
  it('aggregates delta-only usage records', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'assistant',
      message: { usage: { input_tokens: 3, output_tokens: 5 } },
    });
    observe(tracker, {
      type: 'assistant',
      message: { usage: { input_tokens: 7, output_tokens: 11 } },
    });
    expect(tracker.getUsage()).toMatchObject({ inputTokens: 10, outputTokens: 16 });
  });

  it('uses final cumulative usage records', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'result',
      usage: { input_tokens: 30, cache_read_input_tokens: 20, output_tokens: 10 },
    });
    expect(tracker.getUsage()).toMatchObject({
      inputTokens: 30,
      cacheReadInputTokens: 20,
      outputTokens: 10,
    });
  });

  it('does not double-count mixed delta and final cumulative records', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'assistant',
      message: { usage: { input_tokens: 30, output_tokens: 10 } },
    });
    observe(tracker, {
      type: 'result',
      usage: { input_tokens: 30, output_tokens: 10 },
    });
    expect(tracker.getUsage()).toMatchObject({ inputTokens: 30, outputTokens: 10 });
  });

  it('treats categories omitted from final cumulative records as zero', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'assistant',
      message: { usage: { cache_read_input_tokens: 20 } },
    });
    observe(tracker, {
      type: 'result',
      usage: { input_tokens: 30, output_tokens: 10 },
    });
    expect(tracker.getUsage()).toMatchObject({
      inputTokens: 30,
      cacheReadInputTokens: 0,
      outputTokens: 10,
    });
  });

  it('treats cache-hit field names as aliases within a record', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'assistant',
      message: {
        usage: {
          cache_read_input_tokens: 100,
          prompt_cache_hit_tokens: 100,
          cache_creation_input_tokens: 25,
        },
      },
    });
    expect(tracker.getUsage()).toMatchObject({
      cacheReadInputTokens: 100,
      cacheCreationInputTokens: 25,
    });
  });

  it('fails when uncached input exceeds the configured limit', () => {
    const tracker = new UsageTracker({
      maxUncachedInputTokens: 9,
      maxCachedInputTokens: 0,
      maxOutputTokens: 0,
    });
    expect(() =>
      observe(tracker, { type: 'assistant', message: { usage: { input_tokens: 10 } } }),
    ).toThrow(UsageBudgetExceededError);
    expect(tracker.getStatus().exceeded).toMatchObject({ category: 'uncached_input' });
  });

  it('fails when cached input exceeds the configured limit', () => {
    const tracker = new UsageTracker({
      maxUncachedInputTokens: 0,
      maxCachedInputTokens: 9,
      maxOutputTokens: 0,
    });
    expect(() =>
      observe(tracker, {
        type: 'assistant',
        message: { usage: { prompt_cache_hit_tokens: 10 } },
      }),
    ).toThrow(UsageBudgetExceededError);
    expect(tracker.getStatus().exceeded).toMatchObject({ category: 'cached_input' });
  });

  it('fails when output exceeds the configured limit', () => {
    const tracker = new UsageTracker({
      maxUncachedInputTokens: 0,
      maxCachedInputTokens: 0,
      maxOutputTokens: 9,
    });
    expect(() =>
      observe(tracker, { type: 'assistant', message: { usage: { output_tokens: 10 } } }),
    ).toThrow(UsageBudgetExceededError);
    expect(tracker.getStatus().exceeded).toMatchObject({ category: 'output' });
  });

  it('exposes fail-closed status when budgets are configured but no records appear', () => {
    const tracker = new UsageTracker({
      maxUncachedInputTokens: 100,
      maxCachedInputTokens: 0,
      maxOutputTokens: 0,
    });
    expect(tracker.getStatus()).toMatchObject({
      status: 'within_limit',
      usageRecordsObserved: 0,
    });
  });

  it('returns null getUsage() when no records observed', () => {
    const tracker = disabledTracker();
    expect(tracker.getUsage()).toBeNull();
  });

  it('includes recordsObserved in getUsage() result', () => {
    const tracker = disabledTracker();
    observe(tracker, {
      type: 'result',
      usage: { input_tokens: 30, output_tokens: 10 },
    });
    const usage = tracker.getUsage();
    expect(usage).not.toBeNull();
    expect(usage!.recordsObserved).toBe(1);
  });
});

describe('TestRuntime', () => {
  it('returns null usage and not_applicable turn source', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-runtime-'));
    try {
      const config: ActionConfig = {
        runtimeProvider: 'test',
        targetMode: 'synthetic-fixture',
        reviewMode: 'auto',
        artifactRetentionDays: 7,
        postComment: false,
        apiKeyMode: 'auth-token',
        toolMode: 'none',
        claudeMaxTurns: 6,
        maxContextChars: 1000,
        maxPatchChars: 1000,
        maxReviewChars: 1000,
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
        githubToken: 'token',
      };
      const result = await new TestRuntime().run({
        config,
        phase: 'bootstrap',
        stateKey: 'synthetic',
        prompt: 'review',
        promptHash: 'hash',
        workspace: dir,
        tempDir: dir,
        runtimeDir: path.join(dir, 'runtime'),
        target: target(),
      });
      expect(result.observedTurns).toBe(0);
      expect(result.observedTurnSource).toBe('not_applicable');
      expect(result.usage).toBeNull();
      expect(result.lineageTotals.source).toBe('current_run_only');
      expect(result.lineageTotals.observedTurns).toBe(0);
      expect(result.lineageTotals.partial).toBe(false);
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});

describe('RuntimeObservationTracker', () => {
  function observe(tracker: RuntimeObservationTracker, obj: unknown): void {
    tracker.observeLine(JSON.stringify(obj));
  }

  it('counts distinct assistant message IDs', () => {
    const tracker = new RuntimeObservationTracker();
    observe(tracker, {
      type: 'assistant',
      message: { role: 'assistant', id: 'msg_a', content: [] },
    });
    observe(tracker, {
      type: 'assistant',
      message: { role: 'assistant', id: 'msg_b', content: [] },
    });
    expect(tracker.getObservedTurns()).toBe(2);
    expect(tracker.getObservedTurnSource()).toBe('unique_assistant_message_ids');
  });

  it('deduplicates shared message IDs', () => {
    const tracker = new RuntimeObservationTracker();
    observe(tracker, {
      type: 'assistant',
      message: { role: 'assistant', id: 'msg_a', content: [] },
    });
    observe(tracker, {
      type: 'assistant',
      message: { role: 'assistant', id: 'msg_a', content: [] },
    });
    expect(tracker.getObservedTurns()).toBe(1);
  });

  it('ignores non-assistant type records', () => {
    const tracker = new RuntimeObservationTracker();
    observe(tracker, { type: 'custom-title', message: { id: 'x' } });
    observe(tracker, { type: 'agent-name', message: { id: 'x' } });
    observe(tracker, { type: 'queue-operation', message: { id: 'x' } });
    observe(tracker, { type: 'last-prompt', message: { id: 'x' } });
    expect(tracker.getObservedTurns()).toBeNull();
    expect(tracker.getObservedTurnSource()).toBe('unavailable');
  });

  it('ignores records with non-assistant message role', () => {
    const tracker = new RuntimeObservationTracker();
    observe(tracker, {
      type: 'assistant',
      message: { role: 'user', id: 'msg_a', content: [] },
    });
    expect(tracker.getObservedTurns()).toBeNull();
    expect(tracker.getObservedTurnSource()).toBe('unavailable');
  });

  it('ignores empty message id', () => {
    const tracker = new RuntimeObservationTracker();
    observe(tracker, {
      type: 'assistant',
      message: { role: 'assistant', id: '', content: [] },
    });
    expect(tracker.getObservedTurns()).toBeNull();
  });

  it('returns null when no assistant records seen', () => {
    const tracker = new RuntimeObservationTracker();
    expect(tracker.getObservedTurns()).toBeNull();
    expect(tracker.getObservedTurnSource()).toBe('unavailable');
  });
});

describe('computeLineageTotals', () => {
  it('returns current_run_only for bootstrap without prior state', () => {
    const result = computeLineageTotals(undefined, 3, {
      inputTokens: 100,
      cacheReadInputTokens: 50,
      cacheCreationInputTokens: 25,
      outputTokens: 30,
      recordsObserved: 1,
    });
    expect(result.source).toBe('current_run_only');
    expect(result.observedTurns).toBe(3);
    expect(result.usage.inputTokens).toBe(100);
    expect(result.partial).toBe(false);
  });

  it('returns restored_manifest_plus_current_run for incremental with prior lineage', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: 5,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'current_run_only',
        partial: false,
      },
    };
    const result = computeLineageTotals(restoredState, 3, {
      inputTokens: 100,
      cacheReadInputTokens: 50,
      cacheCreationInputTokens: 25,
      outputTokens: 30,
      recordsObserved: 1,
    });
    expect(result.source).toBe('restored_manifest_plus_current_run');
    expect(result.observedTurns).toBe(8);
    expect(result.usage.inputTokens).toBe(300);
    expect(result.usage.cacheReadInputTokens).toBe(150);
    expect(result.usage.outputTokens).toBe(90);
    expect(result.partial).toBe(false);
  });

  it('treats null current usage as zero for lineage math', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: 5,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'current_run_only',
        partial: false,
      },
    };
    const result = computeLineageTotals(restoredState, 2, null);
    expect(result.observedTurns).toBe(7);
    expect(result.usage.inputTokens).toBe(200);
  });

  it('propagates null observed turns when current is null', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: 5,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'current_run_only',
        partial: false,
      },
    };
    const result = computeLineageTotals(restoredState, null, null);
    expect(result.observedTurns).toBeNull();
    expect(result.partial).toBe(true);
    expect(result.usage.inputTokens).toBe(200);
  });

  it('propagates null when prior lineage turns is null', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: null,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'current_run_only',
        partial: true,
      },
    };
    const result = computeLineageTotals(restoredState, 3, null);
    expect(result.observedTurns).toBeNull();
  });

  it('detects legacy manifest fallback', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: {
        inputTokens: 50,
        cacheReadInputTokens: 20,
        cacheCreationInputTokens: 10,
        outputTokens: 15,
        recordsObserved: 0,
      },
      manifestPath: '',
    };
    const result = computeLineageTotals(restoredState, 3, {
      inputTokens: 100,
      cacheReadInputTokens: 50,
      cacheCreationInputTokens: 25,
      outputTokens: 30,
      recordsObserved: 1,
    });
    expect(result.source).toBe('legacy_manifest_fallback');
    expect(result.partial).toBe(true);
    expect(result.observedTurns).toBe(3);
  });
});

describe('preserveLineageTotalsForSkipped', () => {
  it('preserves prior lineage and sets skipped source', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: 5,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'restored_manifest_plus_current_run',
        partial: false,
      },
    };
    const result = preserveLineageTotalsForSkipped(restoredState);
    expect(result.source).toBe('restored_manifest_preserved_for_skipped');
    expect(result.observedTurns).toBe(5);
    expect(result.usage.inputTokens).toBe(200);
    expect(result.partial).toBe(false);
  });

  it('preserves null observedTurns in prior lineage', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
      lineageTotals: {
        observedTurns: null,
        usage: {
          inputTokens: 200,
          cacheReadInputTokens: 100,
          cacheCreationInputTokens: 50,
          outputTokens: 60,
        },
        source: 'unavailable',
        partial: true,
      },
    };
    const result = preserveLineageTotalsForSkipped(restoredState);
    expect(result.source).toBe('restored_manifest_preserved_for_skipped');
    expect(result.observedTurns).toBeNull();
  });

  it('falls back to legacy when no prior lineage', () => {
    const restoredState: RestoredState = {
      stateKey: 'test',
      sessionId: 's1',
      sessionName: 'sn',
      runtimeProvider: 'test',
      usage: null,
      manifestPath: '',
    };
    const result = preserveLineageTotalsForSkipped(restoredState);
    expect(result.source).toBe('legacy_manifest_fallback');
    expect(result.partial).toBe(true);
    expect(result.observedTurns).toBe(0);
  });
});
