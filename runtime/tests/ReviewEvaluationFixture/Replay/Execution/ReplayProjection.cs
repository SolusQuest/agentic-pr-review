using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

internal static class ReplayProjection
{
    // Normalize only after actual admission. Never restore or persist this comparison artifact.
    internal static string Logical(AgentSessionArtifact artifact, AgentSessionTrustedRequest trusted)
    {
        string? previous = null;
        var runs = ImmutableArray.CreateBuilder<AgentSessionCompletedRun>();
        foreach (var run in artifact.Document.CompletedRuns)
        {
            if (!AgentStableRequestMaterializer.TryMaterialize(trusted, previous, out var stable)) throw new InvalidOperationException();
            runs.Add(run with { StablePlanSha256 = AgentCanonical.StablePlanSha256(stable!.StablePlan) });
            var normalized = artifact.Document with
            {
                SessionId = "normalized-session", Generation = run.RunOrdinal,
                ProducerBaseSha = run.ReviewedIdentity.BaseSha, ProducerHeadSha = run.ReviewedIdentity.HeadSha,
                PredecessorStateSha256 = previous is null ? null : new string('0', 64),
                PriorSessionSha256 = previous, CompletedRuns = runs.ToImmutable(),
            };
            if (!AgentSessionCodec.TryWrite(normalized, out var encoded, out _) || encoded is null) throw new InvalidOperationException();
            previous = encoded.SessionSha256;
        }
        return previous ?? throw new InvalidOperationException();
    }

    internal static string Provider(IEnumerable<byte[]> requests) => EvaluationAttempt.Hash("replay-provider",
        requests.Select(bytes => AgentCanonical.HashRaw(bytes)).ToArray());

    internal static string Steps(ImmutableArray<ReplayStep> steps) => AgentCanonical.HashDomain("apr.r5.replay.steps",
        JsonSerializer.SerializeToUtf8Bytes(steps, ReplayExecutionJson.Default.ImmutableArrayReplayStep));
}
