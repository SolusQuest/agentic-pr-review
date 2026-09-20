using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
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
    PrefixProjection Logical, PrefixProjection Provider)
{
    // Shape continuity is not cache telemetry.
    internal string CacheObservation => "unknown";

    internal PrefixComparison Compare(PrefixObservation next)
    {
        if (Domain != next.Domain || ControlMessages != next.ControlMessages ||
            HistoricalMessages != next.HistoricalMessages)
            return new("incomparable", null, null);
        return new("compared", Stable(Logical, next.Logical), Stable(Provider, next.Provider));
    }

    private static bool Stable(PrefixProjection left, PrefixProjection right) =>
        left.Control == right.Control && left.History == right.History && left.Settings == right.Settings;
}

internal sealed record PrefixComparison(string Code, bool? LogicalStable, bool? ProviderStable);

internal sealed class PrefixObservationException() : Exception("prefix_observation_invalid")
{
    public override string ToString() => "prefix_observation_invalid";
}

// The caller supplies production admission results, never a favorable prefix length.
// Raw run/SESSION objects are used only while deriving the fixed boundary.
internal sealed class PrefixBoundary
{
    private PrefixBoundary(PrefixDomain domain, int controls, int history)
    { Domain = domain; ControlMessages = controls; HistoricalMessages = history; }

    internal PrefixDomain Domain { get; }
    internal int ControlMessages { get; }
    internal int HistoricalMessages { get; }
    internal int DynamicStart => ControlMessages + HistoricalMessages;

    internal static PrefixBoundary Bootstrap(AgentSessionTrustedRequest trusted, AgentRunRequest run)
    {
        var stable = Stable(trusted, run);
        if (run.StablePlan.PriorSessionSha256 is not null || run.Continuation is not null ||
            run.InitialMessages.Length != stable.ControlMessages.Length + 1)
            throw new PrefixObservationException();
        return Create(run, stable.ControlMessages.Length, -1, null);
    }

    internal static PrefixBoundary Restored(AgentSessionTrustedRequest trusted, AgentSessionRestoreResult restored)
    {
        if (!restored.Succeeded || restored.RunRequest is not { } run || restored.Artifact is not { } artifact ||
            run.StablePlan.PriorSessionSha256 != artifact.SessionSha256)
            throw new PrefixObservationException();
        var stable = Stable(trusted, run);
        return Create(run, stable.ControlMessages.Length, artifact.Document.Generation, artifact.SessionSha256);
    }

    private static AgentSessionMaterializedStableRequest Stable(AgentSessionTrustedRequest trusted, AgentRunRequest run)
    {
        if (!AgentStableRequestMaterializer.TryMaterialize(trusted, run.StablePlan.PriorSessionSha256, out var stable) ||
            stable!.StablePlan != run.StablePlan || !AgentValueDomains.IsIdentifier(run.SessionId) ||
            !run.ReviewedIdentity.IsValid() || !AgentSessionRestorer.TryValidateReconstructedRequest(run) ||
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
