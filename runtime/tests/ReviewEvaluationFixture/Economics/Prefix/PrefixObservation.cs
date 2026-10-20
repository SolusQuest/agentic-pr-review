using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;

// In-memory evidence only. No request content or caller-controlled diagnostic strings.
internal sealed record PrefixSegment(string Sha256, int Bytes, int Count);

internal sealed record PrefixDomain(
    string SourceCommit, string SourceTree, bool SourceClean,
    string StablePlanSha256, string SessionSha256, long Generation,
    string? AcceptedSessionSha256);

internal sealed record PrefixProjection(
    PrefixSegment Control, PrefixSegment History, PrefixSegment Dynamic,
    PrefixSegment Settings, PrefixSegment Whole);

internal sealed record PrefixObservation(
    PrefixDomain Domain, int ControlMessages, int HistoricalMessages,
    PrefixProjection Logical, string ObservationDomain)
{
    internal const string LogicalDomain = "apr-historical-logical-prefix-v1";

    // Shape continuity is not cache telemetry.
    internal string CacheObservation => "unknown";

    internal PrefixComparison Compare(PrefixObservation next)
    {
        if (ObservationDomain != LogicalDomain || next.ObservationDomain != LogicalDomain || Domain != next.Domain || ControlMessages != next.ControlMessages ||
            HistoricalMessages != next.HistoricalMessages)
            return new("incomparable", null);
        return new("compared", Stable(Logical, next.Logical));
    }

    private static bool Stable(PrefixProjection left, PrefixProjection right) =>
        left.Control == right.Control && left.History == right.History && left.Settings == right.Settings;
}

internal sealed record PrefixComparison(string Code, bool? LogicalStable);

internal sealed class PrefixObservationException() : Exception("prefix_observation_invalid")
{
    public override string ToString() => "prefix_observation_invalid";
}

// Logical tracing only: Restored consumes its existing admission result; the
// finite authored fixture grants no state authority. No factory accepts a prefix length.
internal sealed class PrefixBoundary
{
    private PrefixBoundary(PrefixDomain domain, int controls, int history)
    { Domain = domain; ControlMessages = controls; HistoricalMessages = history; }

    internal PrefixDomain Domain { get; }
    internal int ControlMessages { get; }
    internal int HistoricalMessages { get; }
    internal int DynamicStart => ControlMessages + HistoricalMessages;

    internal static PrefixBoundary Bootstrap(AgentTrustedRequest trusted, AgentRunRequest run)
    {
        var stable = Stable(trusted, run);
        if (run.StablePlan.PriorSessionSha256 is not null || run.Continuation is not null ||
            run.InitialMessages.Length != stable.ControlMessages.Length + 1)
            throw new PrefixObservationException();
        return Create(run, stable.ControlMessages.Length, -1, null);
    }

    // One authored typed fixture only. This is not state admission or SDK restore.
    internal static PrefixBoundary AuthoredThreeMessageHistoryForTests(AgentTrustedRequest trusted, AgentRunRequest run)
    {
        var stable = Stable(trusted, run);
        var controls = stable.ControlMessages.Length;
        if (run.StablePlan.PriorSessionSha256 is null || run.Continuation is null ||
            run.Continuation.Items.Length != 1 || run.InitialMessages.Length != controls + 4 ||
            run.InitialMessages[controls].Role != "user" ||
            run.InitialMessages[controls + 1].Role != "assistant" ||
            run.InitialMessages[controls + 2].Role != "tool")
            throw new PrefixObservationException();
        return Create(run, controls, 0, run.StablePlan.PriorSessionSha256);
    }

    private static AgentMaterializedStableRequest Stable(AgentTrustedRequest trusted, AgentRunRequest run)
    {
        if (!AgentStableRequestMaterializer.TryMaterialize(trusted, run.StablePlan.PriorSessionSha256, out var stable) ||
            stable!.StablePlan != run.StablePlan || !AgentValueDomains.IsIdentifier(run.SessionId) ||
            !run.ReviewedIdentity.IsValid() || !AgentRequestBounds.TryValidateReconstructedRequest(run) ||
            run.InitialMessages.Length <= stable.ControlMessages.Length ||
            run.InitialMessages[^1].Role != "user")
            throw new PrefixObservationException();
        return stable;
    }

    private static PrefixBoundary Create(AgentRunRequest run, int controls, long generation, string? accepted) => new(
        new(EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean,
            AgentCanonical.StablePlanSha256(run.StablePlan),
            AgentCanonical.HashDomain("apr.r6.prefix.session", System.Text.Encoding.UTF8.GetBytes(run.SessionId)),
            generation, accepted), controls, run.InitialMessages.Length - controls - 1);

    public override string ToString() => "prefix_boundary";
}
