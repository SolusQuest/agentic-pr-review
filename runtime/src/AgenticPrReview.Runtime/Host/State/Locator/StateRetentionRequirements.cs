namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class StateRetentionRequirements
{
    internal const long LogicalWindowSeconds = 7 * 24 * 60 * 60;
    internal const long PreStickyBudgetSeconds = 15 * 60;
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
