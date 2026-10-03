namespace AgenticPrReview.Runtime.Agent.Loop;

// Both successful results and fixed recovery errors consume the same run budget.
// Admission is transactional: failure never advances the caller's byte count.
internal static class AgentToolResultBudget
{
    internal static bool TryAdd(long current, int resultBytes, out long next)
    {
        next = current;
        if (current < 0 || resultBytes < 0 || resultBytes > AgentLimits.ToolResultBytes)
        {
            return false;
        }

        long candidate;
        try
        {
            candidate = checked(current + resultBytes);
        }
        catch (OverflowException)
        {
            return false;
        }

        if (candidate > AgentLimits.ToolResultsTotalBytes)
        {
            return false;
        }

        next = candidate;
        return true;
    }
}
