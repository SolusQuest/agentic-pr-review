namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class StateRetentionRequirements
{
    internal const long LogicalWindowSeconds = 7 * 24 * 60 * 60;
    internal const long PreStickyBudgetSeconds = 15 * 60;
    // R7 composition anchors these once. The older constant also governs
    // retained proofs and previous-key evidence freshness and stays unchanged.
    internal const long CurrentPreStickySeconds = 24 * 60;
    internal const long CurrentFinalizationSeconds = 4 * 60;
    internal const long CurrentHostSeconds = CurrentPreStickySeconds + CurrentFinalizationSeconds;
    internal const long ScopedPlatformRequestSeconds = 8 * 24 * 60 * 60;
    internal const long SentinelRequestSeconds = 10 * 24 * 60 * 60;
    internal const long SentinelDependentMarginSeconds = 24 * 60 * 60;

    internal static bool TryGetRequiredSentinelExpiry(
        long nowUnixSeconds,
        long dependentExpiresAtUnixSeconds,
        out long requiredExpiresAtUnixSeconds)
    {
        requiredExpiresAtUnixSeconds = 0;
        if (nowUnixSeconds is < 0 or > RestrictedStateFormat.MaximumUnixSeconds ||
            dependentExpiresAtUnixSeconds is < 0 or >
                RestrictedStateFormat.MaximumUnixSeconds)
        {
            return false;
        }

        try
        {
            var requested = checked(nowUnixSeconds + SentinelRequestSeconds);
            var dependent = checked(
                dependentExpiresAtUnixSeconds +
                SentinelDependentMarginSeconds);
            requiredExpiresAtUnixSeconds = Math.Max(requested, dependent);
            return requiredExpiresAtUnixSeconds <=
                RestrictedStateFormat.MaximumUnixSeconds;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
