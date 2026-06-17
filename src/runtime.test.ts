import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import {
  allowedToolsForMode,
  buildClaudeArgs,
  computeLineageTotals,
  TestRuntime,
  UsageBudgetExceededError,
  UsageTracker,
} from './runtime.js';
import { type ActionConfig, type RestoredState } from './types.js';

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
      });
      expect(result.toolMode).toBe('readonly');
      expect(result.allowedTools).toEqual([]);
      expect(result.usageBudgetStatus.status).toBe('not_applicable');
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
    const result = computeLineageTotals(restoredState, null, null);
    expect(result.observedTurns).toBe(5);
    expect(result.usage.inputTokens).toBe(200);
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
