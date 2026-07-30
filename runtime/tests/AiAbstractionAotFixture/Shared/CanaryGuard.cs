using System.Text;

namespace AgenticPrReview.Runtime.AiAbstractionFixture;

internal static class CanaryGuard
{
    internal static void EnsureAbsent(
        IEnumerable<string> canaries,
        params byte[][] surfaces)
    {
        foreach (var canary in canaries)
        {
            if (string.IsNullOrWhiteSpace(canary))
            {
                throw new FixtureFailure("APR_AI_CANARY_CONFIG");
            }
            var needle = Encoding.UTF8.GetBytes(canary);
            foreach (var surface in surfaces)
            {
                if (surface.AsSpan().IndexOf(needle) >= 0)
                {
                    throw new FixtureFailure("APR_AI_CANARY_LEAK");
                }
            }
        }
    }
}
