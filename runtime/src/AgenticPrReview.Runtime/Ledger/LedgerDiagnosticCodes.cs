namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerDiagnosticCodes
{
    // Raw-transport
    public const string RawByteLimitExceeded = "ledger_raw_byte_limit_exceeded";
    public const string InvalidUtf8 = "ledger_invalid_utf8";
    public const string InvalidJson = "ledger_invalid_json";
    public const string DuplicateJsonProperty = "ledger_duplicate_json_property";
    public const string JsonDepthExceeded = "ledger_json_depth_exceeded";
    public const string JsonArrayLengthExceeded = "ledger_json_array_length_exceeded";
    public const string JsonPropertyCountExceeded = "ledger_json_property_count_exceeded";

    // Unicode
    public const string InvalidUnicode = "ledger_invalid_unicode";

    // Version routing
    public const string UnsupportedSchemaVersion = "ledger_unsupported_schema_version";
    public const string UnsupportedPrefixContractVersion = "ledger_unsupported_prefix_contract_version";

    // Schema / shape
    public const string SchemaViolation = "ledger_schema_violation";
    public const string UnknownField = "ledger_unknown_field";
    public const string BootstrapShapeViolation = "ledger_bootstrap_shape_violation";
    public const string ContinuationShapeViolation = "ledger_continuation_shape_violation";
    public const string ResetShapeViolation = "ledger_reset_shape_violation";
    public const string RecoveryRootShapeViolation = "ledger_recovery_root_shape_violation";
    public const string RecordRoleMismatch = "ledger_record_role_mismatch";
    public const string ChangedFileLimitExceeded = "ledger_changed_file_limit_exceeded";
    public const string FindingLimitExceeded = "ledger_finding_limit_exceeded";
    public const string LimitationsLimitExceeded = "ledger_limitations_limit_exceeded";
    public const string OverlongValue = "ledger_overlong_value";
    public const string UnsupportedChangeStatus = "ledger_unsupported_change_status";
    public const string RecordsEmpty = "ledger_records_empty";
    public const string InteractionLimitExceeded = "ledger_interaction_limit_exceeded";
    public const string ResetReasonMissing = "ledger_reset_reason_missing";
    public const string RecoveryRootReasonMissing = "ledger_recovery_root_reason_missing";

    // Structural bounds
    public const string CanonicalByteLimitExceeded = "ledger_canonical_byte_limit_exceeded";
    public const string IdentityByteLengthExceeded = "ledger_identity_byte_length_exceeded";
    public const string ControlCharacterInIdentity = "ledger_control_character_in_identity";

    // Semantic invariants
    public const string RecordsLengthNotEven = "ledger_records_length_not_even";
    public const string PairOrderMismatch = "ledger_pair_order_mismatch";
    public const string DuplicateInteraction = "ledger_duplicate_interaction";
    public const string OrdinalGap = "ledger_ordinal_gap";
    public const string PairInteractionIdMismatch = "ledger_pair_interaction_id_mismatch";
    public const string FindingLocationMismatch = "ledger_finding_location_mismatch";
    public const string FindingLocationMissingPath = "ledger_finding_location_missing_path";
    public const string FindingLineRangeInvalid = "ledger_finding_line_range_invalid";
    public const string DigestMismatch = "ledger_digest_mismatch";
    public const string ModelAliasLiteral = "ledger_model_alias_literal";

    // Canonical form
    public const string NonCanonical = "ledger_non_canonical";

    // Builder / transition
    public const string OverBoundAppend = "ledger_over_bound_append";
    public const string TransitionKindMismatch = "ledger_transition_kind_mismatch";
    public const string IdentityMismatch = "ledger_identity_mismatch";
    public const string SessionEpochMismatch = "ledger_session_epoch_mismatch";
    public const string LedgerEpochMismatch = "ledger_ledger_epoch_mismatch";
    public const string ResetEpochNotFresh = "ledger_reset_epoch_not_fresh";
    public const string StateGenerationMismatch = "ledger_state_generation_mismatch";
    public const string PredecessorHashMismatch = "ledger_predecessor_hash_mismatch";
    public const string PredecessorManifestHashMismatch = "ledger_predecessor_manifest_hash_mismatch";
    public const string PredecessorLedgerEpochMismatch = "ledger_predecessor_ledger_epoch_mismatch";
    public const string PredecessorGenerationMismatch = "ledger_predecessor_generation_mismatch";
    public const string ResetReasonMismatch = "ledger_reset_reason_mismatch";
    public const string RecoveryRootReasonMismatch = "ledger_recovery_root_reason_mismatch";
    public const string ContinuationPrefixMismatch = "ledger_continuation_prefix_mismatch";
    public const string RootRecordsShapeMismatch = "ledger_root_records_shape_mismatch";
    public const string ResetRecordsShapeMismatch = "ledger_reset_records_shape_mismatch";
}
