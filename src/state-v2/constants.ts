/**
 * Fixed constants for the M4 v2 state bundle. See docs/20_architecture/state-manifest-v2.md.
 *
 * These values are frozen for M4. Sibling PRs (#49, #51, #53, #55) consume
 * them either through this module or through the authoritative JSON Schema
 * `protocol/schemas/state-manifest.v2.json`.
 */

export const MANIFEST_MAX_BYTES = 65536 as const;
export const LEDGER_MAX_BYTES = 524288 as const;
export const METADATA_MAX_BYTES = 32768 as const;

export const LEDGER_FILENAME = 'ledger.json' as const;
export const PROVIDER_RUN_METADATA_FILENAME = 'provider-run-metadata.json' as const;
export const MANIFEST_FILENAME = 'manifest.json' as const;

export const LEDGER_SCHEMA_VERSION = 1 as const;
export const PROVIDER_RUN_METADATA_SCHEMA_VERSION = 1 as const;
export const PREFIX_CONTRACT_VERSION = 1 as const;

export const STATE_NAMESPACE = 'm4-ledger-v2' as const;

export const EPOCH_ID_REGEX = /^[A-Za-z0-9_-]{22}$/;
export const SHA256_HEX_REGEX = /^[a-f0-9]{64}$/;
export const GIT_SHA_REGEX = /^([a-f0-9]{40}|[a-f0-9]{64})$/;

export const MAX_DIAGNOSTIC_ERRORS = 8 as const;
export const MAX_DIAGNOSTIC_MESSAGE_CHARS = 256 as const;
export const MAX_DIAGNOSTIC_MESSAGE_UTF8_BYTES = 1024 as const;

export const EXPECTED_BUNDLE_FILENAMES = [
  MANIFEST_FILENAME,
  LEDGER_FILENAME,
  PROVIDER_RUN_METADATA_FILENAME,
] as const;

// Shared frozen numeric bounds (see docs/20_architecture/session-ledger-and-prefix-contract.md
// section "M4 Batch #1 Frozen Vocabulary" -> "Numeric bounds intersection").
export const STATE_GENERATION_MAX = 1_000_000 as const;
export const PREDECESSOR_STATE_GENERATION_MAX = 999_999 as const;
export const INTERACTION_ORDINAL_MAX = 1_000_000 as const;
export const PULL_REQUEST_MAX = 2_147_483_647 as const;
export const PRODUCING_RUN_ATTEMPT_MAX = 2_147_483_647 as const;
export const PRODUCING_RUN_ID_REGEX = /^[1-9][0-9]{0,18}$/;
export const REPOSITORY_REGEX = /^[A-Za-z0-9._-]+\/[A-Za-z0-9._-]+$/;
export const REPOSITORY_MIN_LENGTH = 3 as const;
export const REPOSITORY_MAX_LENGTH = 200 as const;
export const IDENTITY_STRING_MAX_LENGTH = 256 as const;
export const IDENTITY_STRING_MAX_UTF8_BYTES = 256 as const;
