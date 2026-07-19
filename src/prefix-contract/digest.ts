import { createHash } from 'node:crypto';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import {
  validateAdapterEnvelope,
  validateCacheConfigEnvelope,
  validatePolicyEnvelope,
  validateTemplateEnvelope,
  validateToolsEnvelope,
} from './envelopes.js';
import type { PrefixResult } from './result.js';

/**
 * Host-authoritative cache-contract digest producers (issue #50, D4/D9).
 * digestId(tag, envelope) per the design contract's Prefix Contract section;
 * each tag is the ASCII bytes followed by exactly one NUL octet.
 */

const TAGS = {
  template: 'agentic-pr-review/cache-contract/template/v1',
  policy: 'agentic-pr-review/cache-contract/policy/v1',
  tools: 'agentic-pr-review/cache-contract/tools/v1',
  config: 'agentic-pr-review/cache-contract/config/v1',
  adapter: 'agentic-pr-review/cache-contract/adapter/v1',
} as const;

function digestId(tag: string, canonicalBytes: Uint8Array): string {
  const tagBytes = new TextEncoder().encode(tag);
  const preimage = new Uint8Array(tagBytes.byteLength + 1 + canonicalBytes.byteLength);
  preimage.set(tagBytes);
  preimage[tagBytes.byteLength] = 0;
  preimage.set(canonicalBytes, tagBytes.byteLength + 1);
  return createHash('sha256').update(preimage).digest('hex');
}

/**
 * Cross-language M4 digest for the review-context subject subtree.
 * The domain-separated preimage is frozen by the shared session-ledger contract.
 */
export function computeSubjectDigest(subject: unknown): PrefixResult<string> {
  try {
    const canonical = canonicalJsonBytes(subject);
    return {
      ok: true,
      value: digestId('agentic-pr-review/review-subject/v1', canonical),
    };
  } catch {
    return { ok: false, errors: [{ code: 'prefix-subject-invalid', path: '/subject' }] };
  }
}

/**
 * Cross-language M4 digest for the seven cache-contract identity fields stored
 * on each review_context record. This intentionally has no domain tag: the
 * exact untagged RFC 8785 preimage is the existing C# ledger contract.
 */
export function computeCacheContractDigest(input: {
  readonly adapterId: string;
  readonly cacheConfigId: string;
  readonly modelId: string;
  readonly policyId: string;
  readonly providerId: string;
  readonly templateId: string;
  readonly toolDefinitionId: string;
}): PrefixResult<string> {
  try {
    const canonical = canonicalJsonBytes({
      adapterId: input.adapterId,
      cacheConfigId: input.cacheConfigId,
      modelId: input.modelId,
      policyId: input.policyId,
      providerId: input.providerId,
      templateId: input.templateId,
      toolDefinitionId: input.toolDefinitionId,
    });
    return { ok: true, value: createHash('sha256').update(canonical).digest('hex') };
  } catch {
    return { ok: false, errors: [{ code: 'prefix-cache-contract-invalid', path: '' }] };
  }
}

export function computeTemplateId(envelope: unknown): PrefixResult<string> {
  const validated = validateTemplateEnvelope(envelope);
  return validated.ok
    ? { ok: true, value: digestId(TAGS.template, validated.value.canonicalBytes) }
    : validated;
}

export function computePolicyId(envelope: unknown): PrefixResult<string> {
  const validated = validatePolicyEnvelope(envelope);
  return validated.ok
    ? { ok: true, value: digestId(TAGS.policy, validated.value.canonicalBytes) }
    : validated;
}

export function computeToolDefinitionId(envelope: unknown): PrefixResult<string> {
  const validated = validateToolsEnvelope(envelope);
  return validated.ok
    ? { ok: true, value: digestId(TAGS.tools, validated.value.canonicalBytes) }
    : validated;
}

export function computeCacheConfigId(envelope: unknown): PrefixResult<string> {
  const validated = validateCacheConfigEnvelope(envelope);
  return validated.ok
    ? { ok: true, value: digestId(TAGS.config, validated.value.canonicalBytes) }
    : validated;
}

export function computeAdapterId(envelope: unknown): PrefixResult<string> {
  const validated = validateAdapterEnvelope(envelope);
  return validated.ok
    ? { ok: true, value: digestId(TAGS.adapter, validated.value.canonicalBytes) }
    : validated;
}
