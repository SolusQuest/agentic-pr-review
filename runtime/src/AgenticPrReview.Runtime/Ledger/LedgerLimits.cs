using System.Globalization;

namespace AgenticPrReview.Runtime.Ledger;

public static class LedgerLimits
{
    public const int MaxRawBytes = 524_288;         // 512 KiB
    public const int MaxCanonicalBytes = 262_144;   // 256 KiB
    public const int MaxJsonDepth = 32;
    public const int MaxArrayLength = 4096;
    public const int MaxTotalProperties = 65_536;
    public const int MaxInteractionPairs = 32;
    public const int MaxRecords = 64; // 2 * MaxInteractionPairs
    public const int MaxChangedFilesPerContext = 200;
    public const int MaxFindingsPerOutcome = 50;
    public const int MaxLimitationsPerOutcome = 16;
    public const int MaxIdentityUtf8Bytes = 256;
    public const int MaxIdentityChars = 256;

    // String maxLengths (characters, counted as Unicode text elements to
    // match Draft-7 JSON Schema definition and JsonSchema.Net evaluator).
    public const int MaxSummaryChars = 4000;
    public const int MaxFindingBodyChars = 4000;
    public const int MaxFindingTitleChars = 240;
    public const int MaxFindingEvidenceChars = 2000;
    public const int MaxFindingSuggestedActionChars = 1600;
    public const int MaxLimitationsItemChars = 1200;
    public const int MaxSafeRelativePathChars = 500;

    public const int MaxIntegerValue = 1_000_000;

    /// <summary>
    /// Returns the length of <paramref name="value"/> in Unicode text elements,
    /// matching Draft-7 JSON Schema's `maxLength` semantics and the count used
    /// by the authoritative JsonSchema.Net evaluator (StringInfo-based).
    /// Callers must use this helper wherever a schema maxLength / minLength /
    /// pattern length is enforced so builder / projection / mapper classify
    /// identically on supplementary-plane and combining-sequence inputs.
    /// </summary>
    internal static int SchemaStringLength(string value)
        => new StringInfo(value).LengthInTextElements;
}