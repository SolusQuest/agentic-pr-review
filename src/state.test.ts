import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { readRestoredState, stateArtifactName, writeStateBundle } from './state.js';
import { type ActionConfig } from './types.js';
import { sanitizeStateKey } from './utils.js';

function config(): ActionConfig {
  return {
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
    apiKey: 'secret-value',
  };
}

describe('state helpers', () => {
  it('sanitizes state keys and names artifacts', () => {
    const key = sanitizeStateKey(' pr #42 / test ');
    expect(key).toBe('pr-42-test');
    expect(stateArtifactName(key)).toBe('agentic-pr-review-state-pr-42-test');
  });

  it('writes restorable sanitized state without context bodies', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-state-'));
    try {
      const runtimeDir = path.join(dir, 'runtime-source');
      const bundleDir = path.join(dir, 'bundle');
      await mkdir(runtimeDir, { recursive: true });
      await writeFile(
        path.join(runtimeDir, 'session.jsonl'),
        '{"authorization":"Bearer secret-value","type":"result"}\n',
        'utf8',
      );
      await writeStateBundle({
        bundleDir,
        config: config(),
        target: {
          mode: 'synthetic-fixture',
          title: 'Synthetic',
          body: '',
          baseRef: 'main',
          baseSha: 'base',
          headRef: 'branch',
          headSha: 'head',
          draft: false,
          changedFiles: [],
        },
        stateKey: 'synthetic-test',
        phase: 'bootstrap',
        promptSha256: 'prompt-hash',
        blocks: [
          {
            name: 'instructions',
            source: 'input',
            text: 'do not persist this body',
            bytes: 24,
            sha256: 'block-hash',
          },
        ],
        runtimeResult: {
          sessionId: 'session-1',
          sessionName: 'session-name',
          reviewMarkdown: 'review',
          debugFiles: [],
          toolMode: 'readonly',
          allowedTools: ['Read', 'Glob', 'Grep'],
          observedTurns: 3,
          observedTurnSource: 'unique_assistant_message_ids',
          usage: {
            inputTokens: 10,
            cacheReadInputTokens: 5,
            cacheCreationInputTokens: 2,
            outputTokens: 3,
            recordsObserved: 1,
          },
          usageBudgetStatus: {
            status: 'disabled',
            limits: config().usageBudgetLimits,
            usageRecordsObserved: 1,
          },
          lineageTotals: {
            observedTurns: 3,
            usage: {
              inputTokens: 10,
              cacheReadInputTokens: 5,
              cacheCreationInputTokens: 2,
              outputTokens: 3,
            },
            source: 'current_run_only',
            partial: false,
          },
        },
        runtimeDir,
      });
      const restored = await readRestoredState(bundleDir);
      expect(restored.sessionId).toBe('session-1');
      const manifest = await readFile(path.join(bundleDir, 'manifest.json'), 'utf8');
      expect(manifest).not.toContain('do not persist this body');
      expect(manifest).toContain('"toolMode": "readonly"');
      expect(manifest).toContain('"Read"');
      expect(manifest).toContain('"usageBudgetStatus"');
      const runtimeFile = await readFile(
        path.join(bundleDir, 'runtime', 'test', 'session.jsonl'),
        'utf8',
      );
      expect(runtimeFile).toContain('***REDACTED***');
      expect(runtimeFile).not.toContain('secret-value');
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });

  it('does not reject transcript text solely for secret-like variable names', async () => {
    const dir = await mkdtemp(path.join(tmpdir(), 'agentic-pr-review-state-'));
    try {
      const runtimeDir = path.join(dir, 'runtime-source');
      const bundleDir = path.join(dir, 'bundle');
      await mkdir(runtimeDir, { recursive: true });
      await writeFile(
        path.join(runtimeDir, 'session.jsonl'),
        JSON.stringify({
          type: 'assistant',
          text: 'Reviewed code that references apiKey, authToken, secretValue, and passwordHash variable names.',
        }) + '\n',
        'utf8',
      );
      await expect(
        writeStateBundle({
          bundleDir,
          config: config(),
          target: {
            mode: 'synthetic-fixture',
            title: 'Synthetic',
            body: '',
            baseRef: 'main',
            baseSha: 'base',
            headRef: 'branch',
            headSha: 'head',
            draft: false,
            changedFiles: [],
          },
          stateKey: 'synthetic-test',
          phase: 'bootstrap',
          promptSha256: 'prompt-hash',
          blocks: [],
          runtimeResult: {
            sessionId: 'session-1',
            sessionName: 'session-name',
            reviewMarkdown: 'review',
            debugFiles: [],
            toolMode: 'none',
            allowedTools: [],
            observedTurns: 0,
            observedTurnSource: 'not_applicable',
            usage: null,
            usageBudgetStatus: {
              status: 'disabled',
              limits: config().usageBudgetLimits,
              usageRecordsObserved: 0,
            },
            lineageTotals: {
              observedTurns: 0,
              usage: {
                inputTokens: 0,
                cacheReadInputTokens: 0,
                cacheCreationInputTokens: 0,
                outputTokens: 0,
              },
              source: 'current_run_only',
              partial: false,
            },
          },
          runtimeDir,
        }),
      ).resolves.toBeTruthy();
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
