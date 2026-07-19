import { Ajv } from 'ajv';
import schema from '../../protocol/schemas/live-runtime-invocation-context.v1.json' with { type: 'json' };
import { LIVE_CONTEXT_MAX_BYTES } from './constants.js';
import {
  computeAdapterId,
  computeCacheContractDigest,
  computeCacheConfigId,
  computePolicyId,
  computeTemplateId,
  computeToolDefinitionId,
} from '../prefix-contract/digest.js';

export type LiveContextErrorCode =
  | 'live-context-over-bound'
  | 'live-context-bom'
  | 'live-context-invalid-utf8'
  | 'live-context-invalid-json'
  | 'live-context-duplicate-property'
  | 'live-context-version'
  | 'live-context-unicode'
  | 'live-context-schema'
  | 'live-context-semantic';

export interface LiveRuntimeInvocationContextV1 {
  readonly schemaVersion: 1;
  readonly stateKey: Record<string, unknown>;
  readonly sessionEpoch: string;
  readonly cacheContractIdentity: Record<string, unknown>;
  readonly generation: Record<string, unknown>;
  readonly transition: Record<string, unknown>;
  readonly currentInteraction: {
    readonly interactionId: string;
    readonly interactionOrdinal: number;
    readonly consumedInputSha256: string;
    readonly subjectDigest: string;
    readonly cacheContractDigest: string;
  };
  readonly cacheContractEnvelopes: {
    readonly template: unknown;
    readonly policy: unknown;
    readonly tools: unknown;
    readonly cacheConfig: unknown;
    readonly adapter: unknown;
  };
  readonly providerMode: 'synthetic' | 'live';
  readonly producingRun: Record<string, unknown>;
}

export type LiveContextParseResult =
  | {
      readonly valid: true;
      readonly context: LiveRuntimeInvocationContextV1;
      readonly bytes: Uint8Array;
    }
  | { readonly valid: false; readonly code: LiveContextErrorCode };

const ajv = new Ajv({ strict: true, allErrors: true, allowUnionTypes: true });
const validateSchema = ajv.compile(schema);

export function parseLiveRuntimeInvocationContext(bytes: Uint8Array): LiveContextParseResult {
  const owned = new Uint8Array(bytes);
  if (owned.byteLength > LIVE_CONTEXT_MAX_BYTES)
    return { valid: false, code: 'live-context-over-bound' };
  if (owned[0] === 0xef && owned[1] === 0xbb && owned[2] === 0xbf)
    return { valid: false, code: 'live-context-bom' };

  let text: string;
  try {
    text = new TextDecoder('utf-8', { fatal: true, ignoreBOM: false }).decode(owned);
  } catch {
    return { valid: false, code: 'live-context-invalid-utf8' };
  }

  let value: unknown;
  try {
    value = JSON.parse(text);
  } catch {
    return { valid: false, code: 'live-context-invalid-json' };
  }
  if (hasDuplicateJsonProperty(text))
    return { valid: false, code: 'live-context-duplicate-property' };
  if (
    value === null ||
    typeof value !== 'object' ||
    Array.isArray(value) ||
    (value as { schemaVersion?: unknown }).schemaVersion !== 1
  )
    return { valid: false, code: 'live-context-version' };
  if (hasLoneSurrogate(value)) return { valid: false, code: 'live-context-unicode' };
  if (!validateSchema(value)) return { valid: false, code: 'live-context-schema' };
  if (!validateSemanticDomains(value as unknown as LiveRuntimeInvocationContextV1))
    return { valid: false, code: 'live-context-semantic' };
  return { valid: true, context: value as unknown as LiveRuntimeInvocationContextV1, bytes: owned };
}

function validateSemanticDomains(context: LiveRuntimeInvocationContextV1): boolean {
  const controlFields: unknown[] = [
    context.sessionEpoch,
    context.stateKey,
    context.cacheContractIdentity,
    context.generation,
    context.transition,
    context.currentInteraction,
    context.providerMode,
    context.producingRun,
  ];
  if (controlFields.some((value) => containsForbiddenControl(value))) return false;
  const stateKey = context.stateKey;
  const identity = context.cacheContractIdentity;
  const repositoryPattern = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/u;
  const boundedIdentity = [
    stateKey.workflowIdentity,
    stateKey.trustedExecutionDomain,
    identity.providerId,
    identity.modelId,
  ];
  if (
    ![stateKey.repository, stateKey.headRepository].every(
      (repository) =>
        typeof repository === 'string' &&
        repositoryPattern.test(repository) &&
        new TextEncoder().encode(repository).byteLength <= 200,
    ) ||
    boundedIdentity.some(
      (value) => typeof value !== 'string' || new TextEncoder().encode(value).byteLength > 256,
    ) ||
    identity.modelId === 'latest'
  )
    return false;
  const transition = context.transition as Record<string, unknown>;
  const generation = context.generation as Record<string, unknown>;
  const stateGeneration = generation.stateGeneration;
  if (transition.kind === 'bootstrap' || transition.kind === 'recovery_root') {
    if (stateGeneration !== 0 || context.currentInteraction.interactionOrdinal !== 0) return false;
  } else if (
    typeof stateGeneration !== 'number' ||
    typeof transition.predecessorStateGeneration !== 'number' ||
    stateGeneration !== transition.predecessorStateGeneration + 1
  ) {
    return false;
  }
  const envelopes = context.cacheContractEnvelopes;
  const computed = [
    [computeTemplateId(envelopes.template), identity.templateId],
    [computePolicyId(envelopes.policy), identity.policyId],
    [computeToolDefinitionId(envelopes.tools), identity.toolDefinitionId],
    [computeCacheConfigId(envelopes.cacheConfig), identity.cacheConfigId],
    [computeAdapterId(envelopes.adapter), identity.adapterId],
  ] as const;
  const cacheDigest = computeCacheContractDigest({
    adapterId: String(identity.adapterId),
    cacheConfigId: String(identity.cacheConfigId),
    modelId: String(identity.modelId),
    policyId: String(identity.policyId),
    providerId: String(identity.providerId),
    templateId: String(identity.templateId),
    toolDefinitionId: String(identity.toolDefinitionId),
  });
  return (
    computed.every(([result, expected]) => result.ok && result.value === expected) &&
    cacheDigest.ok &&
    cacheDigest.value === context.currentInteraction.cacheContractDigest
  );
}

function containsForbiddenControl(value: unknown): boolean {
  if (typeof value === 'string') {
    for (let index = 0; index < value.length; index += 1) {
      const code = value.charCodeAt(index);
      if (code === 0x7f || code < 0x20) return true;
    }
    return false;
  }
  if (Array.isArray(value)) return value.some(containsForbiddenControl);
  if (value !== null && typeof value === 'object') {
    return Object.entries(value).some(
      ([key, child]) => containsForbiddenControl(key) || containsForbiddenControl(child),
    );
  }
  return false;
}

function hasLoneSurrogate(value: unknown): boolean {
  if (typeof value === 'string') {
    for (let index = 0; index < value.length; index += 1) {
      const code = value.charCodeAt(index);
      if (code >= 0xd800 && code <= 0xdbff) {
        const next = index + 1 < value.length ? value.charCodeAt(index + 1) : 0;
        if (next < 0xdc00 || next > 0xdfff) return true;
        index += 1;
      } else if (code >= 0xdc00 && code <= 0xdfff) return true;
    }
    return false;
  }
  if (Array.isArray(value)) return value.some(hasLoneSurrogate);
  if (value !== null && typeof value === 'object') {
    return Object.entries(value).some(
      ([key, child]) => hasLoneSurrogate(key) || hasLoneSurrogate(child),
    );
  }
  return false;
}

/** Duplicate-aware scan performed only after JSON.parse has accepted syntax. */
function hasDuplicateJsonProperty(text: string): boolean {
  let index = 0;
  const skipWhitespace = () => {
    while (/\s/.test(text[index] ?? '')) index += 1;
  };
  const parseString = (): string => {
    const start = index;
    index += 1;
    let escaped = false;
    while (index < text.length) {
      const ch = text[index++];
      if (escaped) {
        escaped = false;
        continue;
      }
      if (ch === '\\') escaped = true;
      else if (ch === '"') return JSON.parse(text.slice(start, index)) as string;
    }
    return '';
  };
  const skipValue = (): boolean => {
    skipWhitespace();
    if (text[index] === '"') {
      parseString();
      return false;
    }
    if (text[index] === '{') {
      index += 1;
      const keys = new Set<string>();
      skipWhitespace();
      if (text[index] === '}') {
        index += 1;
        return false;
      }
      while (index < text.length) {
        skipWhitespace();
        const key = parseString();
        if (keys.has(key)) return true;
        keys.add(key);
        skipWhitespace();
        index += 1; // ':'; syntax was already validated by JSON.parse.
        if (skipValue()) return true;
        skipWhitespace();
        if (text[index] === '}') {
          index += 1;
          return false;
        }
        index += 1; // ','
      }
      return false;
    }
    if (text[index] === '[') {
      index += 1;
      skipWhitespace();
      if (text[index] === ']') {
        index += 1;
        return false;
      }
      while (index < text.length) {
        if (skipValue()) return true;
        skipWhitespace();
        if (text[index] === ']') {
          index += 1;
          return false;
        }
        index += 1; // ','
      }
      return false;
    }
    while (index < text.length && !',]}'.includes(text[index]) && !/\s/.test(text[index] ?? ''))
      index += 1;
    return false;
  };
  return skipValue();
}
