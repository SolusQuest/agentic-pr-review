/**
 * Bundle-loading diagnostic codes returned by `classifyStateBundleV2`.
 * The list is stable and test-observable; new codes must be documented in
 * docs/20_architecture/state-manifest-v2.md before use.
 */
export type DiagnosticCode =
  | 'state_unsupported_legacy_v1'
  | 'bundle_path_unsafe'
  | 'bundle_extra_entry'
  | 'bundle_listing_mismatch'
  | 'manifest_missing'
  | 'manifest_byte_limit_exceeded'
  | 'manifest_invalid_json'
  | 'manifest_unknown_version'
  | 'manifest_unknown_field'
  | 'manifest_shape_invalid'
  | 'ledger_missing'
  | 'ledger_byte_limit_exceeded'
  | 'ledger_bytes_mismatch'
  | 'ledger_hash_mismatch'
  | 'provider_run_metadata_missing'
  | 'provider_run_metadata_byte_limit_exceeded'
  | 'provider_run_metadata_bytes_mismatch'
  | 'provider_run_metadata_hash_mismatch';

/** Cross-field validation failure `message` codes. Always paired with `manifest_shape_invalid`. */
export type CrossFieldMessageCode =
  | 'x_state_namespace_mismatch'
  | 'x_transaction_ledger_binding'
  | 'x_metadata_producing_session_epoch'
  | 'x_metadata_producing_state_generation'
  | 'x_metadata_producing_ledger_epoch'
  | 'x_bootstrap_generation_nonzero'
  | 'x_bootstrap_ordinal_nonzero'
  | 'x_recovery_root_generation_nonzero'
  | 'x_recovery_root_ordinal_nonzero'
  | 'x_continuation_epoch_mismatch'
  | 'x_continuation_generation_step'
  | 'x_continuation_ordinal_zero'
  | 'x_reset_epoch_same'
  | 'x_reset_generation_step'
  | 'x_reset_ordinal_nonzero'
  | 'x_identity_empty'
  | 'x_identity_too_long'
  | 'x_identity_control_chars'
  | 'x_repository_syntax'
  | 'x_producedAt_invalid_rfc3339';
