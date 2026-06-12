import { mkdtemp, rm, readFile } from 'node:fs/promises';
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
    maxContextChars: 1000,
    maxPatchChars: 1000,
    maxReviewChars: 1000,
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
      await writeStateBundle({
        bundleDir: dir,
        config: config(),
        target: {
          mode: 'synthetic-fixture',
          title: 'Synthetic',
          baseSha: 'base',
          headSha: 'head',
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
          reviewMarkdown: 'review',
          debugFiles: [],
        },
      });
      const restored = await readRestoredState(dir);
      expect(restored.sessionId).toBe('session-1');
      const manifest = await readFile(path.join(dir, 'manifest.json'), 'utf8');
      expect(manifest).not.toContain('do not persist this body');
    } finally {
      await rm(dir, { recursive: true, force: true });
    }
  });
});
