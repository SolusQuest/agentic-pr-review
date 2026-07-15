import { describe, expect, it } from 'vitest';
import {
  METADATA_MAX_BYTES as SHARED_METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION as SHARED_VERSION,
} from '../state-v2/constants.js';
import {
  METADATA_MAX_BYTES as PROVIDER_METADATA_MAX_BYTES,
  PROVIDER_RUN_METADATA_SCHEMA_VERSION as PROVIDER_METADATA_VERSION,
} from './types.js';

describe('provider-metadata constants mirror #48 shared vocabulary', () => {
  it('METADATA_MAX_BYTES is the same binding value as the shared source', () => {
    expect(PROVIDER_METADATA_MAX_BYTES).toBe(SHARED_METADATA_MAX_BYTES);
    expect(PROVIDER_METADATA_MAX_BYTES).toBe(32768);
  });

  it('PROVIDER_RUN_METADATA_SCHEMA_VERSION is the same binding value as the shared source', () => {
    expect(PROVIDER_METADATA_VERSION).toBe(SHARED_VERSION);
    expect(PROVIDER_METADATA_VERSION).toBe(1);
  });
});
