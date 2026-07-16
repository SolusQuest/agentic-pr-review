namespace AgenticPrReview.Runtime.Ledger;

/// <summary>
/// Deterministic diagnostic emitted by any ledger pipeline stage. Messages
/// are fixed templates and MUST NOT echo untrusted content, absolute paths,
/// tokens, or environment values. <see cref="CauseCode"/> is set only for the
/// composite <c>ledger_over_bound_append</c> failure emitted by
/// <see cref="LedgerBuilder"/>.
/// </summary>
public sealed class LedgerDiagnostic
{
    public required string Code { get; init; }
    public required string Message { get; init; }
    public string? CauseCode { get; init; }
}

/// <summary>
/// Canonical string identifiers for every ledger diagnostic. Ownership of each
/// code by pipeline stage is documented in the design contract (section 9 of
/// the M4 Batch #1 frozen vocabulary and the Issue #49 spec).
/// </summary>
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

    // Unicode-safety
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
    public const string RecoveryRootReasonMissing = "ledger_recovery_root_reason_missing";
    public const string BootstrapShapeViolation = "ledger_bootstrap_shape_violation";
    public const string RecoveryRootShapeViolation = "ledger_recovery_root_shape_violation";
    public const string ResetShapeViolation = "ledger_reset_shape_violation";
    public const string ContinuationShapeViolation = "ledger_continuation_shape_violation";

    // Structural bounds (semantic)
    public const string CanonicalByteLimitExceeded = "ledger_canonical_byte_limit_exceeded";
    public const string IdentityByteLengthExceeded = "ledger_identity_byte_length_exceeded";
    public const string ControlCharacterInIdentity = "ledger_control_character_in_identity";

    // Semantic invariants
    public const string RecordsLengthNotEven = "ledger_records_length_not_even";
    public const string PairOrderMismatch = "ledger_pair_order_mismatch";
    public const string PairInteractionIdMismatch = "ledger_pair_interaction_id_mismatch";
    public const string OrdinalGap = "ledger_ordinal_gap";
    public const string DuplicateInteraction = "ledger_duplicate_interaction";
    public const string FindingLocationMismatch = "ledger_finding_location_mismatch";
    public const string FindingLocationMissingPath = "ledger_finding_location_missing_path";
    public const string FindingLineRangeInvalid = "ledger_finding_line_range_invalid";
    public const string DigestMismatch = "ledger_digest_mismatch";
    public const string ModelAliasLiteral = "ledger_model_alias_literal";

    // Canonical form
    public const string NonCanonical = "ledger_non_canonical";

    // Expected-transition
    public const string IdentityMismatch = "ledger_identity_mismatch";
    public const string SessionEpochMismatch = "ledger_session_epoch_mismatch";
    public const string StateGenerationMismatch = "ledger_state_generation_mismatch";
    public const string LedgerEpochMismatch = "ledger_ledger_epoch_mismatch";
    public const string PredecessorHashMismatch = "ledger_predecessor_hash_mismatch";
    public const string PredecessorGenerationMismatch = "ledger_predecessor_generation_mismatch";
    public const string PredecessorLedgerEpochMismatch = "ledger_predecessor_ledger_epoch_mismatch";
    public const string PredecessorManifestHashMismatch = "ledger_predecessor_manifest_hash_mismatch";
    public const string ResetReasonMismatch = "ledger_reset_reason_mismatch";
    public const string RecoveryRootReasonMismatch = "ledger_recovery_root_reason_mismatch";
    public const string ResetEpochNotFresh = "ledger_reset_epoch_not_fresh";
    public const string TransitionKindMismatch = "ledger_transition_kind_mismatch";

    // Transition structure
    public const string ContinuationPrefixMismatch = "ledger_continuation_prefix_mismatch";
    public const string ResetRecordsShapeMismatch = "ledger_reset_records_shape_mismatch";
    public const string RootRecordsShapeMismatch = "ledger_root_records_shape_mismatch";

    // Build candidate (composite; CauseCode carries the underlying candidate limit)
    public const string OverBoundAppend = "ledger_over_bound_append";
}

/// <summary>
/// Message templates for ledger diagnostics. Templates are fixed and MUST NOT
/// interpolate untrusted content; a sanitized <c>safePath</c> segment produced
/// by the shared safe-path machinery is the only variable component and is
/// pre-sanitized and pre-capped before it reaches this helper.
/// </summary>
public static class LedgerDiagnosticMessages
{
    /// <summary>
    /// Compose a diagnostic with a fixed template and no safe-path suffix.
    /// </summary>
    public static LedgerDiagnostic Of(string code, string? causeCode = null) => new()
    {
        Code = code,
        Message = TemplateFor(code),
        CauseCode = causeCode,
    };

    /// <summary>
    /// Compose a diagnostic with a fixed template plus a pre-sanitized safe
    /// path (owned by the shared safe-path machinery). Caller MUST ensure the
    /// path already satisfies the dual caps.
    /// </summary>
    public static LedgerDiagnostic Of(string code, string safePath, string? causeCode = null) => new()
    {
        Code = code,
        Message = TemplateFor(code) + " " + safePath,
        CauseCode = causeCode,
    };

    private static string TemplateFor(string code) => code switch
    {
        LedgerDiagnosticCodes.RawByteLimitExceeded => "Ledger raw bytes exceed the raw-transport cap.",
        LedgerDiagnosticCodes.InvalidUtf8 => "Ledger bytes are not valid UTF-8.",
        LedgerDiagnosticCodes.InvalidJson => "Ledger bytes are not valid JSON.",
        LedgerDiagnosticCodes.DuplicateJsonProperty => "Ledger JSON contains a duplicate property.",
        LedgerDiagnosticCodes.JsonDepthExceeded => "Ledger JSON nesting depth exceeds the structural cap.",
        LedgerDiagnosticCodes.JsonArrayLengthExceeded => "Ledger JSON array length exceeds the structural cap.",
        LedgerDiagnosticCodes.JsonPropertyCountExceeded => "Ledger JSON property count exceeds the structural cap.",
        LedgerDiagnosticCodes.InvalidUnicode => "Ledger contains an invalid Unicode code point at",
        LedgerDiagnosticCodes.UnsupportedSchemaVersion => "Ledger schemaVersion is not supported by this runtime.",
        LedgerDiagnosticCodes.UnsupportedPrefixContractVersion => "Ledger prefixContractVersion is not supported by this runtime.",
        LedgerDiagnosticCodes.SchemaViolation => "Ledger does not satisfy the ProviderSessionLedgerV1 schema at",
        LedgerDiagnosticCodes.UnknownField => "Ledger contains an unknown field at",
        LedgerDiagnosticCodes.OverlongValue => "Ledger contains a string value exceeding its maximum length at",
        LedgerDiagnosticCodes.RecordsEmpty => "Ledger records array is empty.",
        LedgerDiagnosticCodes.InteractionLimitExceeded => "Ledger interaction pair count exceeds the structural cap.",
        LedgerDiagnosticCodes.ChangedFileLimitExceeded => "Ledger review_context changedFiles length exceeds the per-record cap at",
        LedgerDiagnosticCodes.FindingLimitExceeded => "Ledger review_outcome findings length exceeds the per-record cap at",
        LedgerDiagnosticCodes.LimitationsLimitExceeded => "Ledger review_outcome limitations length exceeds the per-record cap at",
        LedgerDiagnosticCodes.RecordRoleMismatch => "Ledger record role does not match its declared position at",
        LedgerDiagnosticCodes.UnsupportedChangeStatus => "Ledger changedFile status is not a supported value at",
        LedgerDiagnosticCodes.ResetReasonMissing => "Ledger reset header is missing resetReason.",
        LedgerDiagnosticCodes.RecoveryRootReasonMissing => "Ledger recovery_root header is missing recoveryReason.",
        LedgerDiagnosticCodes.BootstrapShapeViolation => "Ledger bootstrap header shape violation at",
        LedgerDiagnosticCodes.RecoveryRootShapeViolation => "Ledger recovery_root header shape violation at",
        LedgerDiagnosticCodes.ResetShapeViolation => "Ledger reset header shape violation at",
        LedgerDiagnosticCodes.ContinuationShapeViolation => "Ledger continuation header shape violation at",
        LedgerDiagnosticCodes.CanonicalByteLimitExceeded => "Ledger canonical byte length exceeds the shared byte cap.",
        LedgerDiagnosticCodes.IdentityByteLengthExceeded => "Ledger identity string UTF-8 byte length exceeds the shared cap at",
        LedgerDiagnosticCodes.ControlCharacterInIdentity => "Ledger identity string contains a control character at",
        LedgerDiagnosticCodes.RecordsLengthNotEven => "Ledger records array length is not even.",
        LedgerDiagnosticCodes.PairOrderMismatch => "Ledger records are not paired as review_context followed by review_outcome at",
        LedgerDiagnosticCodes.PairInteractionIdMismatch => "Ledger interaction pair carries mismatched interactionId at",
        LedgerDiagnosticCodes.OrdinalGap => "Ledger interactionOrdinal sequence has a gap or unexpected value at",
        LedgerDiagnosticCodes.DuplicateInteraction => "Ledger contains a duplicate interactionId at",
        LedgerDiagnosticCodes.FindingLocationMismatch => "Ledger finding has only one of startLine or endLine present at",
        LedgerDiagnosticCodes.FindingLocationMissingPath => "Ledger finding has a line range but no path at",
        LedgerDiagnosticCodes.FindingLineRangeInvalid => "Ledger finding startLine is greater than endLine at",
        LedgerDiagnosticCodes.DigestMismatch => "Ledger record digest does not match its recomputed value.",
        LedgerDiagnosticCodes.ModelAliasLiteral => "Ledger header modelId is a model-alias literal, not a resolved model identifier.",
        LedgerDiagnosticCodes.NonCanonical => "Ledger bytes are not RFC 8785 canonical.",
        LedgerDiagnosticCodes.IdentityMismatch => "Ledger identity field does not match the expected transition at",
        LedgerDiagnosticCodes.SessionEpochMismatch => "Ledger sessionEpoch does not match the expected transition.",
        LedgerDiagnosticCodes.StateGenerationMismatch => "Ledger stateGeneration does not match the expected transition.",
        LedgerDiagnosticCodes.LedgerEpochMismatch => "Ledger ledgerEpoch does not match the expected transition.",
        LedgerDiagnosticCodes.PredecessorHashMismatch => "Ledger predecessorLedgerSha256 does not match the predecessor.",
        LedgerDiagnosticCodes.PredecessorGenerationMismatch => "Ledger predecessorStateGeneration does not match the predecessor.",
        LedgerDiagnosticCodes.PredecessorLedgerEpochMismatch => "Ledger predecessorLedgerEpoch does not match the predecessor.",
        LedgerDiagnosticCodes.PredecessorManifestHashMismatch => "Ledger predecessorManifestSha256 does not match the expected value.",
        LedgerDiagnosticCodes.ResetReasonMismatch => "Ledger resetReason does not match the expected transition.",
        LedgerDiagnosticCodes.RecoveryRootReasonMismatch => "Ledger recoveryReason does not match the expected transition.",
        LedgerDiagnosticCodes.ResetEpochNotFresh => "Ledger reset candidate must advance to a fresh ledgerEpoch.",
        LedgerDiagnosticCodes.TransitionKindMismatch => "Ledger candidate header kind does not match the validator entry point.",
        LedgerDiagnosticCodes.ContinuationPrefixMismatch => "Ledger continuation candidate does not preserve predecessor records at",
        LedgerDiagnosticCodes.ResetRecordsShapeMismatch => "Ledger reset candidate records do not have the required shape.",
        LedgerDiagnosticCodes.RootRecordsShapeMismatch => "Ledger root (bootstrap / recovery_root) records do not have the required shape.",
        LedgerDiagnosticCodes.OverBoundAppend => "Ledger append would exceed a candidate-level structural bound.",
        _ => code,
    };
}
