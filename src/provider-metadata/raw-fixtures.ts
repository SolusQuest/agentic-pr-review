import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { canonicalJsonBytes } from '../canonical-json/index.js';
import { METADATA_MAX_BYTES, parseProviderRunMetadata } from './index.js';
import type { MetadataError } from './types.js';

const basePath = resolve('protocol/fixtures/provider-run-metadata/v1/valid-standard-resumed.json');

function base(): any {
  return JSON.parse(readFileSync(basePath, 'utf8'));
}

function result(bytes: Uint8Array): MetadataError[] {
  const parsed = parseProviderRunMetadata(bytes);
  if (parsed.valid) throw new Error('raw fixture unexpectedly valid');
  return parsed.errors;
}

export function runRawFixture(name: string): MetadataError[] {
  switch (name) {
    case 'bounds':
      return result(new Uint8Array(METADATA_MAX_BYTES + 1));
    case 'bom':
      return result(new Uint8Array([0xef, 0xbb, 0xbf, 0x7b, 0x7d]));
    case 'utf8':
      return result(new Uint8Array([0xc3, 0x28]));
    case 'json':
      return result(new TextEncoder().encode('['));
    case 'duplicate':
      return result(new TextEncoder().encode('{"a":1,"a":2}'));
    case 'unicode':
      return result(new TextEncoder().encode('{"value":"\\ud800"}'));
    case 'schema':
      return result(new TextEncoder().encode('{"schemaVersion":2}'));
    case 'additional': {
      const value = base();
      value.extra = true;
      return result(canonicalJsonBytes(value));
    }
    case 'unknown-enum': {
      const value = base();
      value.capability.aggregate = 'not-a-capability';
      return result(canonicalJsonBytes(value));
    }
    case 'token-maximum': {
      const value = base();
      value.normalizedUsage.attempts[0].outputTokens = 9007199254740992;
      return result(canonicalJsonBytes(value));
    }
    case 'error-list-truncated': {
      const value = base();
      value.normalizedUsage.attempts = Array.from({ length: 32 }, () => ({
        ...value.normalizedUsage.attempts[0],
        requestOrdinal: 0,
        attemptOrdinal: 0,
        outcome: 'cancelled',
        attemptErrorCodes: [],
      }));
      value.normalizedUsage.requests = [];
      value.normalizedUsage.aggregate = {
        totalInputTokens: null,
        uncachedInputTokens: null,
        cacheWriteInputTokens: null,
        cacheReadInputTokens: null,
        outputTokens: null,
        requestCount: 0,
        attemptCount: 0,
      };
      return result(canonicalJsonBytes(value));
    }
    default:
      throw new Error(`unknown raw fixture: ${name}`);
  }
}
