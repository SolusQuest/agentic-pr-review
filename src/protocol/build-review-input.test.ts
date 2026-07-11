import { describe, expect, it } from 'vitest';
import { buildReviewInputV1, type BuildReviewInputConfig } from './build-review-input.js';
import { validateReviewInputV1 } from './review-input.js';
import { sha256 } from '../utils.js';
import type {
  ActionConfig,
  ChangedFile,
  LoadedBlock,
  Phase,
  RestoredState,
  ReviewTarget,
} from '../types.js';

const REPO = { owner: 'SolusQuest', name: 'agentic-pr-review' };

const baseSafeConfig: BuildReviewInputConfig = {
  runtimeProvider: 'test',
  toolMode: 'readonly',
  maxFindings: 20,
  maxPatchChars: 60000,
  maxContextChars: 50000,
  maxReviewChars: 40000,
  stateKey: 'pr-42',
  inlineComments: true,
  maxInlineComments: 10,
  inlineMinSeverity: 'medium',
  inlineMinConfidence: 'high',
  instructions: 'All new modules must include unit tests.',
};

function makeTarget(overrides: Partial<ReviewTarget> = {}): ReviewTarget {
  return {
    mode: 'pull-request',
    prNumber: 42,
    title: 'Add feature',
    body: 'PR body text.',
    baseRef: 'main',
    baseSha: 'base-sha-0001',
    headRef: 'feature',
    headSha: 'head-sha-0001',
    draft: false,
    changedFiles: [
      {
        filename: 'src/main.ts',
        status: 'modified',
        additions: 3,
        deletions: 1,
        changes: 4,
        patch: '@@ -1,1 +1,3 @@\n-const a = 1;\n+const a = 2;\n+const b = 3;',
      },
    ],
    ...overrides,
  };
}

function makeBlock(name: LoadedBlock['name'], text: string): LoadedBlock {
  return {
    name,
    source: 'input',
    text,
    bytes: Buffer.byteLength(text, 'utf8'),
    sha256: sha256(text),
  };
}

function makeRestoredState(overrides: Partial<RestoredState> = {}): RestoredState {
  return {
    stateKey: 'pr-42',
    sessionId: 'session-xyz',
    sessionName: 'session-name',
    runtimeProvider: 'test',
    reviewedHeadSha: 'prev-head-sha',
    usage: null,
    manifestPath: '/tmp/state/manifest.json',
    ...overrides,
  };
}

/** Recursive scan that walks arrays, objects, keys (case-insensitive), and string values. */
function findBannedTraces(
  value: unknown,
  bannedKeys: Set<string>,
  bannedSubstrings: string[],
): { keys: string[]; values: string[] } {
  const foundKeys: string[] = [];
  const foundValues: string[] = [];
  const walk = (node: unknown): void => {
    if (Array.isArray(node)) {
      node.forEach(walk);
      return;
    }
    if (node !== null && typeof node === 'object') {
      for (const [key, child] of Object.entries(node as Record<string, unknown>)) {
        if (bannedKeys.has(key.toLowerCase())) {
          foundKeys.push(key);
        }
        walk(child);
      }
      return;
    }
    if (typeof node === 'string') {
      for (const substring of bannedSubstrings) {
        if (node.includes(substring)) {
          foundValues.push(substring);
        }
      }
    }
  };
  walk(value);
  return { keys: foundKeys, values: foundValues };
}

describe('buildReviewInputV1', () => {
  it('builds a bootstrap input with no restored state and one instructions block', () => {
    const built = buildReviewInputV1({
      target: makeTarget(),
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [makeBlock('instructions', 'Review for security.')],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });

    expect(built.previousState.present).toBe(false);
    expect(built.previousState.findingFingerprints).toEqual([]);
    expect(built.previousState.lineage).toBeUndefined();
    expect(built.subject.contextDocuments).toEqual([
      { name: 'instructions', text: 'Review for security.' },
    ]);
    expect(built.host.review.phase).toBe('bootstrap');
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('builds an incremental input with distinct previous-state and comment fingerprints', () => {
    const built = buildReviewInputV1({
      target: makeTarget(),
      config: baseSafeConfig,
      phase: 'incremental',
      blocks: [],
      restoredState: makeRestoredState(),
      previousFindingFingerprints: ['prev-fp-1', 'prev-fp-2'],
      existingCommentFingerprints: ['comment-fp-1'],
      repository: REPO,
    });

    expect(built.previousState.present).toBe(true);
    expect(built.previousState.reviewedHeadSha).toBe('prev-head-sha');
    expect(built.previousState.findingFingerprints).toEqual(['prev-fp-1', 'prev-fp-2']);
    expect(built.commentEvidence.existingFindingFingerprints).toEqual(['comment-fp-1']);
    expect(built.previousState.lineage).toBeUndefined();

    // Cross-contamination guard: the two lists must remain disjoint sources.
    expect(built.previousState.findingFingerprints).not.toContain('comment-fp-1');
    expect(built.commentEvidence.existingFindingFingerprints).not.toContain('prev-fp-1');
    expect(built.commentEvidence.existingFindingFingerprints).not.toContain('prev-fp-2');

    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('builds an input with empty changed files', () => {
    const built = buildReviewInputV1({
      target: makeTarget({ changedFiles: [] }),
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });
    expect(built.subject.changedFiles).toEqual([]);
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('builds an input from a synthetic-fixture target', () => {
    const target: ReviewTarget = {
      mode: 'synthetic-fixture',
      title: 'Synthetic',
      body: 'Synthetic body',
      baseRef: 'synthetic-base',
      baseSha: 'synthetic-base-sha',
      headRef: 'synthetic-head',
      headSha: 'synthetic-head-sha',
      draft: false,
      changedFiles: [
        {
          filename: 'synthetic.md',
          status: 'modified',
          additions: 1,
          deletions: 0,
          changes: 1,
          patch: '@@ -1 +1,2 @@\n line\n+added',
        },
      ],
    };
    const built = buildReviewInputV1({
      target,
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });
    expect(target.mode).toBe('synthetic-fixture');
    expect(built.subject.pullRequest.number).toBe(1);
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  describe('patch truncation matrix', () => {
    const maxPatchChars = 20;
    const config: BuildReviewInputConfig = { ...baseSafeConfig, maxPatchChars };

    function buildOne(rawPatch: string) {
      const target = makeTarget({
        changedFiles: [
          {
            filename: 'src/x.ts',
            status: 'modified',
            additions: 1,
            deletions: 0,
            changes: 1,
            patch: rawPatch,
          },
        ],
      });
      const built = buildReviewInputV1({
        target,
        config,
        phase: 'bootstrap',
        blocks: [],
        restoredState: null,
        previousFindingFingerprints: [],
        existingCommentFingerprints: [],
        repository: REPO,
      });
      return built.subject.changedFiles[0].patch!;
    }

    it('does not truncate when raw length < maxPatchChars', () => {
      const raw = 'a'.repeat(maxPatchChars - 5);
      const patch = buildOne(raw);
      expect(patch.truncated).toBe(false);
      expect(patch.text).toBe(raw);
      expect(patch.sha256).toBe(sha256(patch.text));
      expect(patch.maxChars).toBe(maxPatchChars);
    });

    it('does not truncate when raw length === maxPatchChars', () => {
      const raw = 'b'.repeat(maxPatchChars);
      const patch = buildOne(raw);
      expect(patch.truncated).toBe(false);
      expect(patch.text.length).toBe(maxPatchChars);
      expect(patch.sha256).toBe(sha256(patch.text));
    });

    it('truncates when raw length > maxPatchChars and hashes the truncated text', () => {
      const raw = 'c'.repeat(maxPatchChars + 10);
      const patch = buildOne(raw);
      expect(patch.truncated).toBe(true);
      expect(patch.text.length).toBe(maxPatchChars);
      expect(patch.sha256).toBe(sha256(patch.text));
    });
  });

  it('omits the patch object entirely when ChangedFile.patch is undefined', () => {
    const target = makeTarget({
      changedFiles: [
        {
          filename: 'src/binary.png',
          status: 'modified',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });
    const built = buildReviewInputV1({
      target,
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });
    const emitted = built.subject.changedFiles[0];
    expect(emitted.patch).toBeUndefined();
    expect('patch' in emitted).toBe(false);
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('preserves previousFilename as previousPath for renamed files', () => {
    const target = makeTarget({
      changedFiles: [
        {
          filename: 'src/new-name.ts',
          previousFilename: 'src/old-name.ts',
          status: 'renamed',
          additions: 0,
          deletions: 0,
          changes: 0,
        },
      ],
    });
    const built = buildReviewInputV1({
      target,
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });
    expect(built.subject.changedFiles[0].previousPath).toBe('src/old-name.ts');
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('produces no leaked secrets when a full ActionConfig with sentinel values is passed through', () => {
    const leakyConfig: ActionConfig = {
      ...baseSafeConfig,
      targetMode: 'pull-request',
      reviewMode: 'auto',
      artifactRetentionDays: 7,
      postComment: true,
      apiKeyMode: 'api-key',
      claudeMaxTurns: 5,
      testRuntimeFixture: 'valid',
      usageBudgetLimits: {
        maxUncachedInputTokens: 0,
        maxCachedInputTokens: 0,
        maxOutputTokens: 0,
      },
      disablePromptCaching: false,
      debugCaptureRawApiBodies: true,
      debugAcknowledgement: 'DEBUG_TESTFAKE',
      githubToken: 'ghp_TESTFAKE',
      apiKey: 'sk-TESTFAKE',
    };

    // The public `config` type exposes only safe fields, but TypeScript structural
    // typing does not prevent a variable already typed as `ActionConfig` from
    // being passed at the call site. This test simulates a caller that
    // erroneously forwards the full `ActionConfig`; the recursive scan and
    // schema validation below are the runtime and shape gates that catch it.
    const built = buildReviewInputV1({
      target: makeTarget(),
      config: leakyConfig as unknown as BuildReviewInputConfig,
      phase: 'bootstrap',
      blocks: [makeBlock('instructions', 'Standard instructions.')],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });

    const banned = new Set([
      'githubtoken',
      'apikey',
      'anthropicapikey',
      'openaiapikey',
      'authheader',
      'authorization',
      'rawrequest',
      'rawresponse',
      'debugcapturerawapibodies',
      'debugacknowledgement',
    ]);
    const sentinels = ['ghp_TESTFAKE', 'sk-TESTFAKE', 'DEBUG_TESTFAKE'];
    const found = findBannedTraces(built, banned, sentinels);
    expect(found.keys).toEqual([]);
    expect(found.values).toEqual([]);
    expect(validateReviewInputV1(built).ok).toBe(true);
  });

  it('fails validation for an unsafe changed-file path (fail-closed; builder does not rewrite)', () => {
    const unsafeFile: ChangedFile = {
      filename: '../secret.ts',
      status: 'modified',
      additions: 1,
      deletions: 0,
      changes: 1,
    };
    const built = buildReviewInputV1({
      target: makeTarget({ changedFiles: [unsafeFile] }),
      config: baseSafeConfig,
      phase: 'bootstrap',
      blocks: [],
      restoredState: null,
      previousFindingFingerprints: [],
      existingCommentFingerprints: [],
      repository: REPO,
    });
    // Builder does not silently normalize; it lets the schema reject.
    expect(built.subject.changedFiles[0].path).toBe('../secret.ts');
    const result = validateReviewInputV1(built);
    expect(result.ok).toBe(false);
  });
});
