import { createHash } from 'node:crypto';
import {
  computeAdapterId,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
} from '../prefix-contract/digest.js';
import { canonicalJsonBytes } from '../canonical-json/index.js';

export const DEEPSEEK_LIVE_PROVIDER = 'deepseek' as const;
export const DEEPSEEK_LIVE_MODEL = 'deepseek-v4-flash' as const;
export const DEEPSEEK_LIVE_ADAPTER_BUILD_VERSION = 'deepseek-openai-chat-v1' as const;

export const DEEPSEEK_FIXED_INSTRUCTION =
  'Return exactly one JSON object with this shape; do not include Markdown fences or explanatory text:\n' +
  '{"schemaVersion":1,"summary":"string","findings":[{"severity":"low|medium|high","confidence":"medium|high","category":"correctness|security|requirements|test_coverage|build|performance|maintainability|documentation","title":"string","body":"string","path":"string or null","startLine":"positive integer or null","endLine":"positive integer or null","suggestedAction":"string or omitted"}],"limitations":["string"]}\n' +
  'The root keys schemaVersion, summary, findings, and limitations are required; every finding key shown except suggestedAction is required; no other keys are allowed. Use the exact enum and bound values from this closed schema: summary <=4000 Unicode characters, at most min(runtime policy maxFindings, 50) findings, title <=240, body <=4000, repo-relative path <=500 or null, evidence is not a model field, suggestedAction <=1600 when present, at most 16 limitations with each limitation <=1200. If both line values are present, endLine >= startLine. Return JSON, not a tool call.';

export const DEEPSEEK_REQUEST_CONTRACT = {
  endpoint: 'https://api.deepseek.com/chat/completions',
  method: 'POST',
  request: {
    model: DEEPSEEK_LIVE_MODEL,
    messages: 'provider-neutral plan projection',
    stream: false,
    temperature: 0,
    max_tokens: 4096,
    thinking: { type: 'disabled' },
    response_format: { type: 'json_object' },
  },
  headers: {
    authorizationScheme: 'Bearer',
    contentType: 'application/json',
  },
  fixedInstruction: DEEPSEEK_FIXED_INSTRUCTION,
  instructionPlacement: { afterStableSegments: 3, beforeHistoricalMessages: true },
  responseParserVersion: 1,
  transport: {
    useProxy: false,
    allowAutoRedirect: false,
    oneRequest: true,
    oneAttempt: true,
    streaming: false,
    toolCalls: false,
  },
  limits: {
    requestBodyBytes: 1_048_576,
    responseBodyBytes: 1_048_576,
    retainedErrorBodyBytes: 8_192,
    modelContentBytes: 262_144,
    providerTimeoutSeconds: 120,
    hostBudgetSeconds: 150,
    keyBytes: { min: 1, max: 256 },
  },
  response: {
    object: 'chat.completion',
    choices: 1,
    finish_reason: 'stop',
    usage: [
      'prompt_tokens',
      'completion_tokens',
      'total_tokens',
      'prompt_cache_hit_tokens',
      'prompt_cache_miss_tokens',
    ],
  },
} as const;

export const DEEPSEEK_REQUEST_CONTRACT_SHA256 = createHash('sha256')
  .update(canonicalJsonBytes(DEEPSEEK_REQUEST_CONTRACT))
  .digest('hex');

function digest(result: { readonly ok: boolean; readonly value?: string }, name: string): string {
  if (!result.ok || result.value === undefined)
    throw new Error(`invalid DeepSeek ${name} contract`);
  return result.value;
}

export const DEEPSEEK_CACHE_CONTRACT_ENVELOPES = {
  template: {
    schemaVersion: 1,
    templateVersion: 3,
    definition: { role: 'system', text: 'You are a precise code reviewer.' },
  },
  policy: {
    schemaVersion: 1,
    policyVersion: 2,
    instructions: 'Review the delta carefully and return only the requested structured result.',
    constraints: { maxFindings: 50, tone: 'strict' },
  },
  tools: {
    schemaVersion: 1,
    toolsetVersion: 1,
    definitions: [
      {
        name: 'submit_review',
        description: 'Submit the structured review.',
        inputSchema: {
          type: 'object',
          properties: { summary: { type: 'string' } },
          required: ['summary'],
        },
        policyMetadata: { risk: 'low' },
      },
    ],
  },
  cacheConfig: {
    schemaVersion: 1,
    cacheConfigVersion: 1,
    markerPolicy: 'none',
    eligibility: 'automatic',
    statelessMode: false,
  },
  adapter: {
    schemaVersion: 2,
    capabilityProfileVersion: 1,
    adapterBuildVersion: DEEPSEEK_LIVE_ADAPTER_BUILD_VERSION,
    requestContractSha256: DEEPSEEK_REQUEST_CONTRACT_SHA256,
  },
} as const;

export const DEEPSEEK_CACHE_CONTRACT_IDENTITY = {
  ledgerSchemaVersion: 1 as const,
  prefixContractVersion: 1 as const,
  providerId: DEEPSEEK_LIVE_PROVIDER,
  modelId: DEEPSEEK_LIVE_MODEL,
  templateId: digest(computeTemplateId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.template), 'template'),
  policyId: digest(computePolicyId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.policy), 'policy'),
  toolDefinitionId: digest(
    computeToolDefinitionId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.tools),
    'tools',
  ),
  cacheConfigId: digest(
    computeCacheConfigId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.cacheConfig),
    'cache config',
  ),
  adapterId: digest(computeAdapterId(DEEPSEEK_CACHE_CONTRACT_ENVELOPES.adapter), 'adapter'),
} as const;
