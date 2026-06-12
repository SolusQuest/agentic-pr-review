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
    expect(config.targetMode).toBe('pull-request');
    expect(config.reviewMode).toBe('auto');
    expect(config.apiKeyMode).toBe('auth-token');
    expect(config.disablePromptCaching).toBe(false);
  });

  it('parses explicit prompt caching disable switch', () => {
    const config = parseActionConfig(
      new Inputs({ disable_prompt_caching: 'true' }),
      baseEnv,
      'pull_request',
    );
    expect(config.disablePromptCaching).toBe(true);
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
});
