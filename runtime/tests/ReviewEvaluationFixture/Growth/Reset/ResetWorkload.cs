using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Reset;

// Reuses S1's admitted tool-heavy workload; the Host owns the actual request and state identity.
internal static class ResetWorkload
{
    internal const string OldFact = "synthetic-old-only-tool-fact";
    internal const string OldReasoning = "synthetic-old-only-continuation";
    internal const string FreshFact = "synthetic-fresh-tool-fact";
    internal const string FreshReasoning = "synthetic-fresh-continuation";

    internal static string Digest(ReplayScript script) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(script, ReplayJsonContext.Default.ReplayScript)));

    internal static ReplayScript Script(AdmittedReplayFixture fixture, int phase, bool fresh)
    {
        var run = GrowthProfiles.Run(fixture, "tools", phase);
        return new(run.Script.Turns.Select(turn => turn with
        {
            ReasoningContent = fresh ? FreshReasoning : OldReasoning,
            ToolCalls = turn.ToolCalls.Select(call => call.Name == "read_file"
                ? call with { ArgumentsJson = "{\"path\":\"file.txt\",\"start_line\":1,\"line_count\":1}" }
                : call).ToImmutableArray(),
        }).ToImmutableArray());
    }
}
