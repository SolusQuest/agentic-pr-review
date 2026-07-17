import { createHash } from 'node:crypto';
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
