using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;

// Test-report admission only. Historical observations retain the limits of
// aea38e3f8f14eeae3e2dc915598aade031b3c6e7; only current production admission executes.
internal sealed record MeasurementLimits(int RequestBytes, int Messages, int PartsTotal,
    int SessionRecords, int ContinuationTotalBytes, int SessionPlaintextBytes,
    int StateEnvelopeBytes, int StateScopeTotalBytes,
    int ModelCalls, int ToolCalls, int ToolCallsPerResponse)
{
    internal static MeasurementLimits Historical { get; } = new(1_048_576, 64, 256,
        256, 262_144, 1_048_576, 2_097_152, 6_291_456, 8, 24, 8);
    internal static MeasurementLimits Current { get; } = new(AgentLimits.RequestBytes,
        AgentLimits.Messages, AgentLimits.PartsTotal, AgentLimits.SessionRecords,
        AgentLimits.ContinuationTotalBytes, AgentLimits.SessionPlaintextBytes,
        AgentLimits.StateEnvelopeBytes, AgentLimits.StateScopeTotalBytes,
        AgentLimits.ModelCalls, AgentLimits.ToolCalls, AgentLimits.ToolCallsPerResponse);

    internal static string HistoricalGrowthCorpus(string root)
    {
        var manifest = ReplayDirectory.Capture(root, default).Manifest;
        if (manifest.SourceKind == "authored-synthetic" && manifest.Configuration.ProviderId == "deepseek" &&
            manifest.Configuration.ModelId == "deepseek-v4-flash" &&
            manifest.Configuration.AdapterId == "968abd371badaa785056ee783553d71763b8a8a6d0d07031f47acc3cfa24d502")
            manifest = manifest with { Configuration = manifest.Configuration with
            { ModelId = "deepseek-flash", AdapterId = "393c2f6cff466c0b8a6ec9aaf29c016959386a4c90ec48d883c6be5fea8b05f6" } };
        return AgentCanonical.HashDomain("apr.r5.replay.corpus", ReplayJson.Write(manifest));
    }
}
