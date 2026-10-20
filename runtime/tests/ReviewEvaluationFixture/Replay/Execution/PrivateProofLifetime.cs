namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

// Process ownership only. This does not admit a state record or SDK context.
internal static class PrivateProofLifetime
{
    internal static bool TryCleanup(bool childReaped, string root, Func<string, bool> cleanup)
    {
        try { return childReaped && cleanup(root); }
        catch { return false; }
    }

    internal static async Task<bool> VerifyPreservationAsync(bool childReaped, Func<Task<bool>> readBack)
    {
        if (!childReaped) return false;
        try { return await readBack(); }
        catch { return false; }
    }
}
