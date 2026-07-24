import { describe, expect, it } from 'vitest';
import { parseActionConfig } from './config.js';

class Inputs {
  constructor(private readonly values: Record<string, string | undefined>) {}
  getInput(name: string): string {
    return this.values[name] ?? '';
  }
}

const baseEnv = {
  GITHUB_TOKEN: 'token',
};

describe('parseActionConfig', () => {
  it('parses defaults for test runtime', () => {
    const config = parseActionConfig(new Inputs({}), baseEnv, 'pull_request');
    expect(config.runtimeProvider).toBe('test');
    expect(config.liveProvider).toBe('none');
    expect(config.runtimeBackend).toBe('legacy');
    expect(config.targetMode).toBe('pull-request');
    expect(config.reviewMode).toBe('auto');
    expect(config.apiKeyMode).toBe('auth-token');
    expect(config.toolMode).toBe('none');
    expect(config.claudeMaxTurns).toBe(6);
    expect(config.maxFindings).toBe(50);
    expect(config.inlineComments).toBe(false);
    expect(config.maxInlineComments).toBe(5);
    expect(config.inlineMinSeverity).toBe('medium');
    expect(config.inlineMinConfidence).toBe('high');
    expect(config.testRuntimeFixture).toBe('valid');
    expect(config.usageBudgetLimits).toEqual({
      maxUncachedInputTokens: 0,
      maxCachedInputTokens: 0,
      maxOutputTokens: 0,
    });
    expect(config.disablePromptCaching).toBe(false);
  });

  it('parses readonly tool mode and usage watchdog limits', () => {
    const config = parseActionConfig(
      new Inputs({
        tool_mode: 'readonly',
        claude_max_turns: '4',
        max_uncached_input_tokens: '100',
        max_cached_input_tokens: '200',
        max_output_tokens: '300',
      }),
      baseEnv,
      'pull_request',
    );
    expect(config.toolMode).toBe('readonly');
    expect(config.claudeMaxTurns).toBe(4);
    expect(config.usageBudgetLimits).toEqual({
      maxUncachedInputTokens: 100,
      maxCachedInputTokens: 200,
      maxOutputTokens: 300,
    });
  });

  it('parses structured fixture selector and finding cap', () => {
    const config = parseActionConfig(
      new Inputs({
        test_runtime_fixture: 'many_findings',
        max_findings: '12',
        inline_comments: 'true',
        max_inline_comments: '99',
        inline_min_severity: 'low',
        inline_min_confidence: 'medium',
      }),
      baseEnv,
      'pull_request',
    );
    expect(config.testRuntimeFixture).toBe('many_findings');
    expect(config.maxFindings).toBe(12);
    expect(config.inlineComments).toBe(true);
    expect(config.maxInlineComments).toBe(10);
    expect(config.inlineMinSeverity).toBe('low');
    expect(config.inlineMinConfidence).toBe('medium');
  });

  it('rejects invalid tool mode and claude turn values', () => {
    expect(() =>
      parseActionConfig(new Inputs({ tool_mode: 'write' }), baseEnv, 'pull_request'),
    ).toThrow(/tool_mode must be one of/);
    expect(() =>
      parseActionConfig(new Inputs({ claude_max_turns: '0' }), baseEnv, 'pull_request'),
    ).toThrow(/claude_max_turns must be a positive integer/);
    expect(() =>
      parseActionConfig(new Inputs({ max_uncached_input_tokens: '-1' }), baseEnv, 'pull_request'),
    ).toThrow(/max_uncached_input_tokens must be an integer/);
    expect(() =>
      parseActionConfig(new Inputs({ max_findings: '0' }), baseEnv, 'pull_request'),
    ).toThrow(/max_findings must be a positive integer/);
    expect(() =>
      parseActionConfig(new Inputs({ test_runtime_fixture: 'unknown' }), baseEnv, 'pull_request'),
    ).toThrow(/test_runtime_fixture must be one of/);
    expect(() =>
      parseActionConfig(new Inputs({ inline_min_severity: 'critical' }), baseEnv, 'pull_request'),
    ).toThrow(/inline_min_severity must be one of/);
    expect(() =>
      parseActionConfig(new Inputs({ inline_min_confidence: 'low' }), baseEnv, 'pull_request'),
    ).toThrow(/inline_min_confidence must be one of/);
  });

  it('parses explicit prompt caching disable switch', () => {
    const config = parseActionConfig(
      new Inputs({ disable_prompt_caching: 'true' }),
      baseEnv,
      'pull_request',
    );
    expect(config.disablePromptCaching).toBe(true);
  });

  it('accepts the guarded deterministic C# matrix', () => {
    const config = parseActionConfig(
      new Inputs({ runtime_backend: 'deterministic-csharp', target_mode: 'synthetic-fixture' }),
      baseEnv,
      'workflow_dispatch',
    );
    expect(config.runtimeBackend).toBe('deterministic-csharp');
    expect(config.runtimeProvider).toBe('test');
  });

  it('rejects deterministic provider settings and synthetic comments', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'deterministic-csharp',
          runtime_provider: 'claude-code-cli',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(
      /config-invalid: runtime_backend=deterministic-csharp requires runtime_provider=test/,
    );
    expect(() =>
      parseActionConfig(
        new Inputs({ runtime_backend: 'deterministic-csharp', model_name: 'ignored' }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/configuration is invalid/);
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'deterministic-csharp',
          target_mode: 'synthetic-fixture',
          post_comment: 'true',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/requires post_comment=false/);
  });

  it('accepts the controlled ledger matrix and rejects caller-controlled state inputs', () => {
    expect(
      parseActionConfig(
        new Inputs({ runtime_backend: 'ledger-csharp', target_mode: 'pull-request' }),
        baseEnv,
        'workflow_run',
      ).runtimeBackend,
    ).toBe('ledger-csharp');
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'ledger-csharp',
          target_mode: 'pull-request',
          instructions: 'untrusted override',
          state_key: 'caller-owned',
          review_mode: 'bootstrap',
        }),
        baseEnv,
        'workflow_run',
      ),
    ).toThrow(/configuration is invalid/);
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'ledger-csharp',
          target_mode: 'pull-request',
          verification_namespace: 'Not-allowed',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/verification_namespace/);
  });

  it('accepts DeepSeek live with a lower runtime finding cap', () => {
    const config = parseActionConfig(
      new Inputs({
        runtime_backend: 'ledger-csharp',
        live_provider: 'deepseek',
        max_findings: '7',
        max_patch_chars: '20000',
        post_comment: 'true',
      }),
      baseEnv,
      'workflow_dispatch',
    );
    expect(config.liveProvider).toBe('deepseek');
    expect(config.maxFindings).toBe(7);
  });

  it('rejects a DeepSeek runtime finding cap above the provider contract', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'ledger-csharp',
          live_provider: 'deepseek',
          max_findings: '51',
          max_patch_chars: '20000',
          post_comment: 'true',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/max_findings<=50/);
  });

  it('rejects mutually exclusive instruction inputs', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({ instructions: 'inline', instructions_path: 'instructions.md' }),
        baseEnv,
        'pull_request',
      ),
    ).toThrow(/mutually exclusive/);
  });

  it('requires positive integer ids', () => {
    expect(() =>
      parseActionConfig(new Inputs({ pr_number: '0' }), baseEnv, 'pull_request'),
    ).toThrow(/pr_number must be a positive integer/);
    expect(() =>
      parseActionConfig(new Inputs({ state_artifact_run_id: '0' }), baseEnv, 'pull_request'),
    ).toThrow(/state_artifact_run_id must be a positive integer/);
  });

  it('limits pull-request incremental restore to pull_request events', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_backend: 'deterministic-csharp',
          target_mode: 'pull-request',
          review_mode: 'incremental',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/only allowed on pull_request events/);
  });

  it('keeps legacy pull-request incremental restore compatible with workflow_dispatch', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({ target_mode: 'pull-request', review_mode: 'incremental' }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).not.toThrow();
  });

  it('requires live runtime configuration', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({ runtime_provider: 'claude-code-cli' }),
        { ...baseEnv, AGENTIC_REVIEW_API_KEY: 'secret-value' },
        'workflow_dispatch',
      ),
    ).toThrow(/model_base_url/);
  });

  it('enforces raw diagnostic gates', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_provider: 'test',
          target_mode: 'synthetic-fixture',
          debug_capture_raw_api_bodies: 'true',
          debug_acknowledgement: 'allow-raw-provider-debug',
        }),
        baseEnv,
        'workflow_dispatch',
      ),
    ).toThrow(/claude-code-cli/);
  });

  it('accepts live raw diagnostic gates when fully acknowledged', () => {
    const config = parseActionConfig(
      new Inputs({
        runtime_provider: 'claude-code-cli',
        target_mode: 'synthetic-fixture',
        model_base_url: 'https://example.invalid',
        model_name: 'model',
        claude_code_version: '2.1.118',
        debug_capture_raw_api_bodies: 'true',
        debug_acknowledgement: 'allow-raw-provider-debug',
      }),
      { ...baseEnv, AGENTIC_REVIEW_API_KEY: 'secret-value' },
      'workflow_dispatch',
    );
    expect(config.debugCaptureRawApiBodies).toBe(true);
  });

  it('requires stronger acknowledgement for pull request raw diagnostics', () => {
    expect(() =>
      parseActionConfig(
        new Inputs({
          runtime_provider: 'claude-code-cli',
          target_mode: 'pull-request',
          model_base_url: 'https://example.invalid',
          model_name: 'model',
          claude_code_version: '2.1.118',
          debug_capture_raw_api_bodies: 'true',
          debug_acknowledgement: 'allow-raw-provider-debug',
        }),
        { ...baseEnv, AGENTIC_REVIEW_API_KEY: 'secret-value' },
        'workflow_dispatch',
      ),
    ).toThrow(/allow-raw-provider-debug-public-pr/);

    const config = parseActionConfig(
      new Inputs({
        runtime_provider: 'claude-code-cli',
        target_mode: 'pull-request',
        model_base_url: 'https://example.invalid',
        model_name: 'model',
        claude_code_version: '2.1.118',
        debug_capture_raw_api_bodies: 'true',
        debug_acknowledgement: 'allow-raw-provider-debug-public-pr',
      }),
      { ...baseEnv, AGENTIC_REVIEW_API_KEY: 'secret-value' },
      'workflow_dispatch',
    );
    expect(config.debugCaptureRawApiBodies).toBe(true);
  });
});
