import {
  type ActionConfig,
  type ApiKeyMode,
  type ReviewMode,
  type RuntimeProvider,
  type TargetMode,
} from './types.js';
import {
  clamp,
  oneOf,
  parseBoolean,
  parseInteger,
  parseOptionalInteger,
  required,
} from './utils.js';

export interface InputReader {
  getInput(name: string): string;
}

const RUNTIME_PROVIDERS = ['test', 'claude-code-cli'] as const satisfies readonly RuntimeProvider[];
const TARGET_MODES = ['pull-request', 'synthetic-fixture'] as const satisfies readonly TargetMode[];
const REVIEW_MODES = ['auto', 'bootstrap', 'incremental'] as const satisfies readonly ReviewMode[];
const API_KEY_MODES = ['auth-token', 'api-key', 'both'] as const satisfies readonly ApiKeyMode[];

function optionalInput(reader: InputReader, name: string): string | undefined {
  const value = reader.getInput(name).trim();
  return value === '' ? undefined : value;
}

function assertMutuallyExclusive(
  config: ActionConfig,
  left: keyof ActionConfig,
  right: keyof ActionConfig,
): void {
  if (config[left] && config[right]) {
    throw new Error(`${String(left)} and ${String(right)} are mutually exclusive`);
  }
}

export function parseActionConfig(
  reader: InputReader,
  env: NodeJS.ProcessEnv,
  eventName: string,
): ActionConfig {
  const runtimeProvider = oneOf(
    optionalInput(reader, 'runtime_provider') ?? 'test',
    'runtime_provider',
    RUNTIME_PROVIDERS,
  );
  const targetMode = oneOf(
    optionalInput(reader, 'target_mode') ?? 'pull-request',
    'target_mode',
    TARGET_MODES,
  );
  const reviewMode = oneOf(
    optionalInput(reader, 'review_mode') ?? 'auto',
    'review_mode',
    REVIEW_MODES,
  );
  const apiKeyMode = oneOf(
    optionalInput(reader, 'api_key_mode') ?? 'auth-token',
    'api_key_mode',
    API_KEY_MODES,
  );

  const config: ActionConfig = {
    runtimeProvider,
    targetMode,
    reviewMode,
    prNumber: parseOptionalInteger(optionalInput(reader, 'pr_number'), 'pr_number'),
    stateKey: optionalInput(reader, 'state_key'),
    stateArtifactRunId: parseOptionalInteger(
      optionalInput(reader, 'state_artifact_run_id'),
      'state_artifact_run_id',
    ),
    artifactRetentionDays: clamp(
      parseInteger(optionalInput(reader, 'artifact_retention_days'), 'artifact_retention_days', 7),
      1,
      7,
    ),
    postComment: parseBoolean(optionalInput(reader, 'post_comment'), 'post_comment', false),
    modelBaseUrl: optionalInput(reader, 'model_base_url'),
    modelName: optionalInput(reader, 'model_name'),
    smallModelName: optionalInput(reader, 'small_model_name'),
    apiKeyMode,
    claudeCodeVersion: optionalInput(reader, 'claude_code_version'),
    instructions: optionalInput(reader, 'instructions'),
    instructionsPath: optionalInput(reader, 'instructions_path'),
    bootstrapContext: optionalInput(reader, 'bootstrap_context'),
    bootstrapContextPath: optionalInput(reader, 'bootstrap_context_path'),
    incrementalContext: optionalInput(reader, 'incremental_context'),
    incrementalContextPath: optionalInput(reader, 'incremental_context_path'),
    maxContextChars: parseInteger(
      optionalInput(reader, 'max_context_chars'),
      'max_context_chars',
      60000,
    ),
    maxPatchChars: parseInteger(
      optionalInput(reader, 'max_patch_chars'),
      'max_patch_chars',
      120000,
    ),
    maxReviewChars: parseInteger(
      optionalInput(reader, 'max_review_chars'),
      'max_review_chars',
      12000,
    ),
    disablePromptCaching: parseBoolean(
      optionalInput(reader, 'disable_prompt_caching'),
      'disable_prompt_caching',
      false,
    ),
    debugCaptureRawApiBodies: parseBoolean(
      optionalInput(reader, 'debug_capture_raw_api_bodies'),
      'debug_capture_raw_api_bodies',
      false,
    ),
    debugAcknowledgement: optionalInput(reader, 'debug_acknowledgement'),
    githubToken: required(env.GITHUB_TOKEN, 'GITHUB_TOKEN env'),
    apiKey: env.AGENTIC_REVIEW_API_KEY?.trim() || undefined,
  };

  assertMutuallyExclusive(config, 'instructions', 'instructionsPath');
  assertMutuallyExclusive(config, 'bootstrapContext', 'bootstrapContextPath');
  assertMutuallyExclusive(config, 'incrementalContext', 'incrementalContextPath');
  validateLiveRuntimeConfig(config);
  validateDebugCapture(config, eventName);
  return config;
}

function validateLiveRuntimeConfig(config: ActionConfig): void {
  if (config.runtimeProvider !== 'claude-code-cli') {
    return;
  }
  required(config.modelBaseUrl, 'model_base_url');
  required(config.modelName, 'model_name');
  required(config.apiKey, 'AGENTIC_REVIEW_API_KEY env');
  const version = required(config.claudeCodeVersion, 'claude_code_version');
  if (!/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(version)) {
    throw new Error('claude_code_version must be an explicit semver, for example 2.1.118');
  }
}

export function validateDebugCapture(config: ActionConfig, eventName: string): void {
  if (!config.debugCaptureRawApiBodies) {
    return;
  }
  if (config.runtimeProvider !== 'claude-code-cli') {
    throw new Error('debug_capture_raw_api_bodies requires runtime_provider=claude-code-cli');
  }
  if (config.targetMode !== 'synthetic-fixture') {
    throw new Error('debug_capture_raw_api_bodies requires target_mode=synthetic-fixture');
  }
  if (eventName !== 'workflow_dispatch') {
    throw new Error('debug_capture_raw_api_bodies is only allowed on workflow_dispatch');
  }
  if (config.debugAcknowledgement !== 'allow-raw-provider-debug') {
    throw new Error(
      'debug_capture_raw_api_bodies requires debug_acknowledgement=allow-raw-provider-debug',
    );
  }
}
