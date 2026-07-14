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

    // String maxLengths (characters)
    public const int MaxSummaryChars = 4000;
    public const int MaxFindingBodyChars = 4000;
    public const int MaxFindingTitleChars = 240;
    public const int MaxFindingEvidenceChars = 2000;
    public const int MaxFindingSuggestedActionChars = 1600;
    public const int MaxLimitationsItemChars = 1200;
    public const int MaxSafeRelativePathChars = 500;

    public const int MaxIntegerValue = 1_000_000;
}
