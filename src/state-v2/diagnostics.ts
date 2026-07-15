/**
 * Bundle-loading diagnostic codes returned by `classifyStateBundleV2`.
 *
 * The full `DiagnosticCode` union splits into two mutually exclusive
 * halves per the design contract:
 *   - `UnsupportedLegacyDiagnostic` — only ever appears on the
 *     `kind: 'unsupported_legacy_v1'` branch of `BundleClassification`.
 *   - `InvalidDiagnosticCode` — the closed enum of failure codes on the
 *     `kind: 'invalid'` branch.
 *
 * The stable `DiagnosticCode` alias remains the union of the two halves
 * for API back-compat.
 */

export type UnsupportedLegacyDiagnostic = 'state_unsupported_legacy_v1';

export type InvalidDiagnosticCode =
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

export type DiagnosticCode = UnsupportedLegacyDiagnostic | InvalidDiagnosticCode;

/**
 * Internal cross-field / semantic sub-codes attached to
 * `AggregatorCandidate.subCode` for stable ordering. These are never
 * emitted on the wire; only `<code>:<safe-path>` (with `<code>` in the
 * three-value wire enum) reaches the classifier / validator message.
 * The old `x_*` alias has been retired; new sub-codes follow the
 * `cross_*` / `semantic_*` naming convention.
 */
export type CrossFieldSubCode =
  | 'cross_transaction_ledger_binding'
  | 'cross_metadata_producing_session_epoch'
  | 'cross_metadata_producing_state_generation'
  | 'cross_metadata_producing_ledger_epoch'
  | 'cross_bootstrap_generation_nonzero'
  | 'cross_bootstrap_ordinal_nonzero'
  | 'cross_recovery_root_generation_nonzero'
  | 'cross_recovery_root_ordinal_nonzero'
  | 'cross_continuation_epoch_mismatch'
  | 'cross_continuation_generation_step'
  | 'cross_continuation_ordinal_zero'
  | 'cross_reset_epoch_same'
  | 'cross_reset_generation_step'
  | 'cross_reset_ordinal_nonzero';

export type SemanticSubCode =
  | 'semantic_identity_empty'
  | 'semantic_identity_utf8_over_cap'
  | 'semantic_identity_control_char'
  | 'semantic_floating_alias'
  | 'semantic_produced_at_rfc3339';
