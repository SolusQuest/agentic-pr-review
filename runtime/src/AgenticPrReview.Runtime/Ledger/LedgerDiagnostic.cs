namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Deterministic diagnostic emitted by any ledger pipeline stage. Messages are
/// fixed templates and must not echo untrusted content, absolute paths, tokens,
/// or environment values. <see cref="CauseCode"/> is set only for composite
/// <c>ledger_over_bound_append</c> failures.
/// </summary>
public sealed record LedgerDiagnostic(string Code, string Message, string? CauseCode = null);

public static class LedgerDiagnosticCodes
{
    // Raw transport stage
    public const string RawByteLimitExceeded = "ledger_raw_byte_limit_exceeded";
    public const string InvalidUtf8 = "ledger_invalid_utf8";
    public const string InvalidJson = "ledger_invalid_json";
    public const string DuplicateJsonProperty = "ledger_duplicate_json_property";
    public const string JsonDepthExceeded = "ledger_json_depth_exceeded";
    public const string JsonArrayLengthExceeded = "ledger_json_array_length_exceeded";
    public const string JsonPropertyCountExceeded = "ledger_json_property_count_exceeded";
    public const string InvalidUnicode = "ledger_invalid_unicode";

    // Version routing
    public const string UnsupportedSchemaVersion = "ledger_unsupported_schema_version";
    public const string UnsupportedPrefixContractVersion = "ledger_unsupported_prefix_contract_version";

    // Schema (via mapper)
    public const string SchemaViolation = "ledger_schema_violation";
    public const string UnknownField = "ledger_unknown_field";
    public const string OverlongValue = "ledger_overlong_value";
    public const string RecordsEmpty = "ledger_records_empty";
    public const string InteractionLimitExceeded = "ledger_interaction_limit_exceeded";
    public const string ChangedFileLimitExceeded = "ledger_changed_file_limit_exceeded";
    public const string FindingLimitExceeded = "ledger_finding_limit_exceeded";
    public const string LimitationsLimitExceeded = "ledger_limitations_limit_exceeded";
    public const string RecordRoleMismatch = "ledger_record_role_mismatch";
    public const string UnsupportedChangeStatus = "ledger_unsupported_change_status";
    public const string ResetReasonMissing = "ledger_reset_reason_missing";
    public const string RecoveryReasonMissing = "ledger_recovery_reason_missing";
    public const string BootstrapShapeViolation = "ledger_bootstrap_shape_violation";
    public const string RecoveryShapeViolation = "ledger_recovery_shape_violation";
    public const string ResetShapeViolation = "ledger_reset_shape_violation";
    public const string ContinuationShapeViolation = "ledger_continuation_shape_violation";

    // Structural bounds (semantic)
    public const string CanonicalByteLimitExceeded = "ledger_canonical_byte_limit_exceeded";
    public const string IdentityByteLengthExceeded = "ledger_identity_byte_length_exceeded";
    public const string ControlCharacterInIdentity = "ledger_control_character_in_identity";

    // Semantic invariants
    public const string PairOrderMismatch = "ledger_pair_order_mismatch";
    public const string OrdinalGap = "ledger_ordinal_gap";
    public const string DuplicateInteraction = "ledger_duplicate_interaction";
    public const string DigestMismatch = "ledger_digest_mismatch";
    public const string RecordsLengthNotEven = "ledger_records_length_not_even";
    public const string FindingLocationMismatch = "ledger_finding_location_mismatch";
    public const string FindingLocationMissingPath = "ledger_finding_location_missing_path";
    public const string FindingLineRangeInvalid = "ledger_finding_line_range_invalid";

    // Canonical form
    public const string NonCanonical = "ledger_non_canonical";

    // Expected-transition
    public const string IdentityMismatch = "ledger_identity_mismatch";
    public const string StateGenerationMismatch = "ledger_state_generation_mismatch";
    public const string LedgerEpochMismatch = "ledger_ledger_epoch_mismatch";
    public const string PredecessorHashMismatch = "ledger_predecessor_hash_mismatch";
    public const string PredecessorGenerationMismatch = "ledger_predecessor_generation_mismatch";
    public const string PredecessorManifestHashMismatch = "ledger_predecessor_manifest_hash_mismatch";
    public const string ResetReasonMismatch = "ledger_reset_reason_mismatch";
    public const string RecoveryReasonMismatch = "ledger_recovery_reason_mismatch";
    public const string ResetEpochNotFresh = "ledger_reset_epoch_not_fresh";
    public const string TransitionKindMismatch = "ledger_transition_kind_mismatch";

    // Transition structure
    public const string ContinuationPrefixMismatch = "ledger_continuation_prefix_mismatch";
    public const string ResetRecordsShapeMismatch = "ledger_reset_records_shape_mismatch";

    // Build candidate
    public const string OverBoundAppend = "ledger_over_bound_append";
}

public static class LedgerDiagnosticMessages
{
    // Fixed templates. Each message is short and content-free.
    public static LedgerDiagnostic Of(string code) => new(code, DefaultMessage(code));
    public static LedgerDiagnostic Of(string code, string cause) => new(code, DefaultMessage(code), cause);

    private static string DefaultMessage(string code) => code switch
    {
        LedgerDiagnosticCodes.RawByteLimitExceeded => "Ledger raw byte length exceeds the 512 KiB pre-parse cap.",
        LedgerDiagnosticCodes.InvalidUtf8 => "Ledger bytes are not valid UTF-8.",
        LedgerDiagnosticCodes.InvalidJson => "Ledger bytes are not valid JSON.",
        LedgerDiagnosticCodes.DuplicateJsonProperty => "Ledger JSON contains a duplicate property name.",
        LedgerDiagnosticCodes.JsonDepthExceeded => "Ledger JSON exceeds the maximum nesting depth of 32.",
        LedgerDiagnosticCodes.JsonArrayLengthExceeded => "Ledger JSON contains an array longer than 4096 elements.",
        LedgerDiagnosticCodes.JsonPropertyCountExceeded => "Ledger JSON exceeds the maximum property count of 65_536.",
        LedgerDiagnosticCodes.InvalidUnicode => "Ledger contains an invalid Unicode code point (NUL or lone surrogate).",
        LedgerDiagnosticCodes.UnsupportedSchemaVersion => "Ledger schemaVersion is not supported by this runtime.",
        LedgerDiagnosticCodes.UnsupportedPrefixContractVersion => "Ledger prefixContractVersion is not supported by this runtime.",
        LedgerDiagnosticCodes.SchemaViolation => "Ledger does not satisfy the ProviderSessionLedgerV1 schema.",
        LedgerDiagnosticCodes.UnknownField => "Ledger contains an unknown field.",
        LedgerDiagnosticCodes.OverlongValue => "Ledger contains a string value exceeding its maximum length.",
        LedgerDiagnosticCodes.RecordsEmpty => "Ledger records array is empty.",
        LedgerDiagnosticCodes.InteractionLimitExceeded => "Ledger interaction pair count exceeds 32.",
        LedgerDiagnosticCodes.ChangedFileLimitExceeded => "Ledger review_context changedFiles length exceeds 200.",
        LedgerDiagnosticCodes.FindingLimitExceeded => "Ledger review_outcome findings length exceeds 50.",
        LedgerDiagnosticCodes.LimitationsLimitExceeded => "Ledger review_outcome limitations length exceeds 16.",
        LedgerDiagnosticCodes.RecordRoleMismatch => "Ledger record role does not match its declared position.",
        LedgerDiagnosticCodes.UnsupportedChangeStatus => "Ledger changedFile status is not a supported value.",
        LedgerDiagnosticCodes.ResetReasonMissing => "Ledger reset header is missing resetReason.",
        LedgerDiagnosticCodes.RecoveryReasonMissing => "Ledger recovery header is missing recoveryReason.",
        LedgerDiagnosticCodes.BootstrapShapeViolation => "Ledger bootstrap header shape violation.",
        LedgerDiagnosticCodes.RecoveryShapeViolation => "Ledger recovery header shape violation.",
        LedgerDiagnosticCodes.ResetShapeViolation => "Ledger reset header shape violation.",
        LedgerDiagnosticCodes.ContinuationShapeViolation => "Ledger continuation header shape violation.",
        LedgerDiagnosticCodes.CanonicalByteLimitExceeded => "Ledger canonical byte length exceeds 256 KiB.",
        LedgerDiagnosticCodes.IdentityByteLengthExceeded => "Ledger identity string UTF-8 byte length exceeds 256.",
        LedgerDiagnosticCodes.ControlCharacterInIdentity => "Ledger identity string contains a control character.",
        LedgerDiagnosticCodes.PairOrderMismatch => "Ledger records are not paired as review_context followed by review_outcome.",
        LedgerDiagnosticCodes.OrdinalGap => "Ledger interactionOrdinal sequence has a gap or unexpected value.",
        LedgerDiagnosticCodes.DuplicateInteraction => "Ledger contains a duplicate interactionId.",
        LedgerDiagnosticCodes.DigestMismatch => "Ledger record digest does not match its recomputed value.",
        LedgerDiagnosticCodes.RecordsLengthNotEven => "Ledger records array length is not even.",
        LedgerDiagnosticCodes.FindingLocationMismatch => "Ledger finding has only one of startLine or endLine present.",
        LedgerDiagnosticCodes.FindingLocationMissingPath => "Ledger finding has a line range but no path.",
        LedgerDiagnosticCodes.FindingLineRangeInvalid => "Ledger finding startLine is greater than endLine.",
        LedgerDiagnosticCodes.NonCanonical => "Ledger bytes are not RFC 8785 canonical.",
        LedgerDiagnosticCodes.IdentityMismatch => "Ledger identity fields do not match the expected transition.",
        LedgerDiagnosticCodes.StateGenerationMismatch => "Ledger stateGeneration does not match the expected transition.",
        LedgerDiagnosticCodes.LedgerEpochMismatch => "Ledger ledgerEpoch does not match the expected transition.",
        LedgerDiagnosticCodes.PredecessorHashMismatch => "Ledger predecessorLedgerSha256 does not match the predecessor.",
        LedgerDiagnosticCodes.PredecessorGenerationMismatch => "Ledger predecessorStateGeneration does not match the predecessor.",
        LedgerDiagnosticCodes.PredecessorManifestHashMismatch => "Ledger predecessorManifestSha256 does not match the expected value.",
        LedgerDiagnosticCodes.ResetReasonMismatch => "Ledger resetReason does not match the expected transition.",
        LedgerDiagnosticCodes.RecoveryReasonMismatch => "Ledger recoveryReason does not match the expected transition.",
        LedgerDiagnosticCodes.ResetEpochNotFresh => "Ledger reset candidate must have a fresh ledgerEpoch.",
        LedgerDiagnosticCodes.TransitionKindMismatch => "Ledger candidate header kind does not match the validator entry point.",
        LedgerDiagnosticCodes.ContinuationPrefixMismatch => "Ledger continuation candidate does not preserve predecessor records.",
        LedgerDiagnosticCodes.ResetRecordsShapeMismatch => "Ledger reset candidate records do not have the required shape.",
        LedgerDiagnosticCodes.OverBoundAppend => "Ledger append would exceed a structural bound.",
        _ => code,
    };
}
