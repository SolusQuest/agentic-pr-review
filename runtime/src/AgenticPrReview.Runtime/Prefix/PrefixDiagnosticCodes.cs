namespace AgenticPrReview.Runtime.Prefix;

/// <summary>Diagnostic codes owned by the prefix contract (#50).</summary>
internal static class PrefixDiagnosticCodes
{
    public const string IdentityInvalid = "prefix_identity_invalid";
    public const string ModelAliasLiteral = "prefix_model_alias_literal";
    public const string DigestInvalid = "prefix_digest_invalid";
    public const string GitShaInvalid = "prefix_git_sha_invalid";
    public const string OrdinalInvalid = "prefix_ordinal_invalid";
    public const string EpochInvalid = "prefix_epoch_invalid";
    public const string EnvelopeInvalid = "prefix_envelope_invalid";
    public const string CanonicalInputRejected = "prefix_canonical_input_rejected";
    public const string EnvelopeTooLarge = "prefix_envelope_too_large";
    public const string CacheContractIdMismatch = "prefix_cache_contract_id_mismatch";
    public const string IdentityMismatch = "prefix_identity_mismatch";
    public const string CurrentContextInvalid = "prefix_current_context_invalid";
    public const string SegmentTooLarge = "prefix_segment_too_large";
    public const string StreamTooLarge = "prefix_stream_too_large";
    public const string LengthOverflow = "prefix_length_overflow";
}
