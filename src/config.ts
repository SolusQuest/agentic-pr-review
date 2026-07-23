import {
  type ActionConfig,
  type ApiKeyMode,
  type InlineCommentConfidence,
  type InlineCommentSeverity,
  type LiveProvider,
  type ReviewMode,
  type RuntimeBackend,
  type RuntimeProvider,
  type TargetMode,
  type TestRuntimeFixture,
  type ToolMode,
} from './types.js';
import {
  clamp,
  oneOf,
  parseBoolean,
  parseInteger,
  parseOptionalPositiveInteger,
  parsePositiveInteger,
  required,
} from './utils.js';

export interface InputReader {
  getInput(name: string): string;
}

const RUNTIME_PROVIDERS = ['test', 'claude-code-cli'] as const satisfies readonly RuntimeProvider[];
const LIVE_PROVIDERS = ['none', 'deepseek'] as const satisfies readonly LiveProvider[];
const RUNTIME_BACKENDS = [
  'legacy',
  'deterministic-csharp',
  'ledger-csharp',
] as const satisfies readonly RuntimeBackend[];
const TARGET_MODES = ['pull-request', 'synthetic-fixture'] as const satisfies readonly TargetMode[];
const REVIEW_MODES = ['auto', 'bootstrap', 'incremental'] as const satisfies readonly ReviewMode[];
const API_KEY_MODES = ['auth-token', 'api-key', 'both'] as const satisfies readonly ApiKeyMode[];
const TOOL_MODES = ['none', 'readonly'] as const satisfies readonly ToolMode[];
const INLINE_SEVERITIES = [
  'low',
  'medium',
  'high',
] as const satisfies readonly InlineCommentSeverity[];
const INLINE_CONFIDENCES = ['medium', 'high'] as const satisfies readonly InlineCommentConfidence[];
const TEST_RUNTIME_FIXTURES = [
  'valid',
  'no_findings',
  'null_location',
  'many_findings',
  'inline_commentable',
  'inline_non_commentable',
  'inline_many_findings',
  'invalid_json',
  'schema_invalid',
] as const satisfies readonly TestRuntimeFixture[];
const SYNTHETIC_RAW_DEBUG_ACKNOWLEDGEMENT = 'allow-raw-provider-debug';
const PUBLIC_PR_RAW_DEBUG_ACKNOWLEDGEMENT = 'allow-raw-provider-debug-public-pr';

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
  const runtimeBackend = oneOf(
    optionalInput(reader, 'runtime_backend') ?? 'legacy',
    'runtime_backend',
    RUNTIME_BACKENDS,
  );
  const liveProvider = oneOf(
    optionalInput(reader, 'live_provider') ?? 'none',
    'live_provider',
    LIVE_PROVIDERS,
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
  const toolMode = oneOf(optionalInput(reader, 'tool_mode') ?? 'none', 'tool_mode', TOOL_MODES);
  const testRuntimeFixture = oneOf(
    optionalInput(reader, 'test_runtime_fixture') ?? 'valid',
    'test_runtime_fixture',
    TEST_RUNTIME_FIXTURES,
  );

  const config: ActionConfig = {
    runtimeBackend,
    runtimeProvider,
    liveProvider,
    targetMode,
    reviewMode,
    verificationNamespace: optionalInput(reader, 'verification_namespace'),
    prNumber: parseOptionalPositiveInteger(optionalInput(reader, 'pr_number'), 'pr_number'),
    stateKey: optionalInput(reader, 'state_key'),
    stateArtifactRunId: parseOptionalPositiveInteger(
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
    toolMode,
    claudeMaxTurns: parsePositiveInteger(
      optionalInput(reader, 'claude_max_turns'),
      'claude_max_turns',
      6,
    ),
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
    maxFindings: parsePositiveInteger(optionalInput(reader, 'max_findings'), 'max_findings', 50),
    inlineComments: parseBoolean(
      optionalInput(reader, 'inline_comments'),
      'inline_comments',
      false,
    ),
    maxInlineComments: clamp(
      parseInteger(optionalInput(reader, 'max_inline_comments'), 'max_inline_comments', 5),
      0,
      10,
    ),
    inlineMinSeverity: oneOf(
      optionalInput(reader, 'inline_min_severity') ?? 'medium',
      'inline_min_severity',
      INLINE_SEVERITIES,
    ),
    inlineMinConfidence: oneOf(
      optionalInput(reader, 'inline_min_confidence') ?? 'high',
      'inline_min_confidence',
      INLINE_CONFIDENCES,
    ),
    testRuntimeFixture,
    usageBudgetLimits: {
      maxUncachedInputTokens: parseInteger(
        optionalInput(reader, 'max_uncached_input_tokens'),
        'max_uncached_input_tokens',
        0,
      ),
      maxCachedInputTokens: parseInteger(
        optionalInput(reader, 'max_cached_input_tokens'),
        'max_cached_input_tokens',
        0,
      ),
      maxOutputTokens: parseInteger(
        optionalInput(reader, 'max_output_tokens'),
        'max_output_tokens',
        0,
      ),
    },
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
  if (
    config.runtimeBackend === 'deterministic-csharp' &&
    config.targetMode === 'pull-request' &&
    config.reviewMode === 'incremental' &&
    eventName !== 'pull_request'
  ) {
    throw new Error(
      'config-invalid: pull-request incremental restore is only allowed on pull_request events',
    );
  }
  validateDeterministicRuntimeConfig(config);
  validateLedgerVerificationNamespace(config);
  validateLiveRuntimeConfig(config);
  validateDebugCapture(config, eventName);
  return config;
}

function validateLedgerVerificationNamespace(config: ActionConfig): void {
  if (config.runtimeBackend !== 'ledger-csharp') return;
  if (
    config.verificationNamespace !== undefined &&
    !/^[a-z0-9][a-z0-9-]{0,47}$/.test(config.verificationNamespace)
  ) {
    throw new Error('config-invalid: verification_namespace must match ^[a-z0-9][a-z0-9-]{0,47}$');
  }
}

function validateDeterministicRuntimeConfig(config: ActionConfig): void {
  if (
    config.runtimeBackend !== 'deterministic-csharp' &&
    config.runtimeBackend !== 'ledger-csharp'
  ) {
    return;
  }
  const backend = config.runtimeBackend;
  if (config.runtimeProvider !== 'test') {
    throw new Error(`config-invalid: runtime_backend=${backend} requires runtime_provider=test`);
  }
  if (backend === 'ledger-csharp' && config.targetMode !== 'pull-request') {
    throw new Error(
      'config-invalid: runtime_backend=ledger-csharp requires target_mode=pull-request',
    );
  }
  if (config.targetMode === 'synthetic-fixture' && config.postComment) {
    throw new Error(
      `config-invalid: runtime_backend=${backend} with target_mode=synthetic-fixture requires post_comment=false`,
    );
  }
  const invalid: string[] = [];
  if (config.toolMode !== 'none') invalid.push('tool_mode');
  if (config.inlineComments) invalid.push('inline_comments');
  if (config.maxInlineComments !== 5) invalid.push('max_inline_comments');
  if (config.inlineMinSeverity !== 'medium') invalid.push('inline_min_severity');
  if (config.inlineMinConfidence !== 'high') invalid.push('inline_min_confidence');
  if (config.testRuntimeFixture !== 'valid') invalid.push('test_runtime_fixture');
  if (config.debugCaptureRawApiBodies) invalid.push('debug_capture_raw_api_bodies');
  if (config.disablePromptCaching) invalid.push('disable_prompt_caching');
  if (config.apiKeyMode !== 'auth-token') invalid.push('api_key_mode');
  if (config.claudeMaxTurns !== 6) invalid.push('claude_max_turns');
  if (
    config.usageBudgetLimits.maxUncachedInputTokens !== 0 ||
    config.usageBudgetLimits.maxCachedInputTokens !== 0 ||
    config.usageBudgetLimits.maxOutputTokens !== 0
  ) {
    invalid.push('usage_budget');
  }
  if (config.modelBaseUrl) invalid.push('model_base_url');
  if (config.modelName) invalid.push('model_name');
  if (config.smallModelName) invalid.push('small_model_name');
  if (config.claudeCodeVersion) invalid.push('claude_code_version');
  if (config.apiKey) invalid.push('AGENTIC_REVIEW_API_KEY');
  if (config.debugAcknowledgement) invalid.push('debug_acknowledgement');
  if (backend === 'ledger-csharp') {
    if (config.reviewMode === 'bootstrap') invalid.push('review_mode');
    if (config.prNumber !== undefined) invalid.push('pr_number');
    if (config.stateKey) invalid.push('state_key');
    if (config.stateArtifactRunId !== undefined) invalid.push('state_artifact_run_id');
    if (config.instructions || config.instructionsPath) invalid.push('instructions');
    if (config.bootstrapContext || config.bootstrapContextPath) invalid.push('bootstrap_context');
    if (config.incrementalContext || config.incrementalContextPath)
      invalid.push('incremental_context');
    if (config.artifactRetentionDays !== 7) invalid.push('artifact_retention_days');
    if (config.maxContextChars !== 60_000) invalid.push('max_context_chars');
    if (config.maxPatchChars !== (config.liveProvider === 'deepseek' ? 20_000 : 120_000))
      invalid.push('max_patch_chars');
    if (config.maxReviewChars !== 12_000) invalid.push('max_review_chars');
    if (config.maxFindings !== 50) invalid.push('max_findings');
  }
  if (config.liveProvider === 'deepseek' && backend !== 'ledger-csharp') {
    throw new Error(
      'config-invalid: live_provider=deepseek requires runtime_backend=ledger-csharp',
    );
  }
  if (invalid.length > 0) {
    throw new Error(
      `config-invalid: runtime_backend=${backend} configuration is invalid: ${invalid.join(', ')}`,
    );
  }
}

function validateLiveRuntimeConfig(config: ActionConfig): void {
  if (
    config.runtimeBackend === 'deterministic-csharp' ||
    config.runtimeProvider !== 'claude-code-cli'
  ) {
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
  if (eventName !== 'workflow_dispatch') {
    throw new Error('debug_capture_raw_api_bodies is only allowed on workflow_dispatch');
  }

  if (
    config.targetMode === 'synthetic-fixture' &&
    config.debugAcknowledgement === SYNTHETIC_RAW_DEBUG_ACKNOWLEDGEMENT
  ) {
    return;
  }

  if (
    config.targetMode === 'pull-request' &&
    config.debugAcknowledgement === PUBLIC_PR_RAW_DEBUG_ACKNOWLEDGEMENT
  ) {
    return;
  }

  if (config.targetMode === 'pull-request') {
    throw new Error(
      `debug_capture_raw_api_bodies with target_mode=pull-request requires debug_acknowledgement=${PUBLIC_PR_RAW_DEBUG_ACKNOWLEDGEMENT}`,
    );
  }

  throw new Error(
    `debug_capture_raw_api_bodies requires debug_acknowledgement=${SYNTHETIC_RAW_DEBUG_ACKNOWLEDGEMENT}`,
  );
}
