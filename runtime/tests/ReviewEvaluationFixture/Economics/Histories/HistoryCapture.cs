using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;

// Safe sidecar on private IPC. The raw reply remains private; this is not acceptance authority.
internal sealed record HistoryCapture(string Code, PrefixObservation? Baseline,
    ImmutableArray<PrefixObservation?> Calls)
{
    internal static HistoryCapture Unavailable => new("unavailable", null, []);

    internal static bool Valid(ReplayChildInput input, HistoryCapture? value)
    {
        if (!Safe(value)) return false;
        return (value!.Baseline is null || ValidObservation(value.Baseline, input)) &&
            value.Calls.All(c => c is null || ValidObservation(c, input));
    }

    internal static bool Safe(HistoryCapture? value)
    {
        if (value is null || value.Calls.IsDefault || value.Calls.Length > AgentLimits.ModelCalls) return false;
        if (value.Code == "unavailable") return value.Baseline is null && value.Calls.IsEmpty;
        if (value.Code is not ("observed" or "unmeasurable")) return false;
        if (value.Code == "observed" && (value.Baseline is null || value.Calls.Any(c => c is null))) return false;
        if (value.Code == "unmeasurable" && value.Baseline is not null && value.Calls.All(c => c is not null)) return false;
        return (value.Baseline is null || SafeObservation(value.Baseline)) && value.Calls.All(c => c is null || SafeObservation(c));
    }

    private static bool ValidObservation(PrefixObservation observation, ReplayChildInput input)
    {
        var domain = observation.Domain;
        if (domain is null || domain.SourceCommit != EvaluationSource.Commit || domain.SourceTree != EvaluationSource.Tree ||
            domain.SourceClean != EvaluationSource.Clean || !EvaluationLimits.Hash(domain.StablePlanSha256) ||
            domain.SessionSha256 != SessionHash(input.Session) || domain.Generation != (input.Predecessor?.Generation ?? -1) ||
            domain.AcceptedSessionSha256 != input.Predecessor?.SessionSha256) return false;
        return SafeObservation(observation);
    }

    private static bool SafeObservation(PrefixObservation observation)
    {
        var domain = observation.Domain;
        if (domain is null || !Hex40(domain.SourceCommit) || !Hex40(domain.SourceTree) ||
            !EvaluationLimits.Hash(domain.StablePlanSha256) || !EvaluationLimits.Hash(domain.SessionSha256) ||
            domain.Generation is < -1 or >= AgentSessionFormat.MaximumCompletedRuns ||
            (domain.Generation == -1 ? domain.AcceptedSessionSha256 is not null : !EvaluationLimits.Hash(domain.AcceptedSessionSha256)) ||
            observation.ControlMessages is < 1 or > AgentLimits.Messages || observation.HistoricalMessages is < 0 or >= AgentLimits.Messages ||
            observation.ControlMessages + observation.HistoricalMessages >= AgentLimits.Messages) return false;
        return ValidProjection(observation.Logical) && ValidProjection(observation.Provider);
    }

    internal static bool Hex40(string? value) => value is { Length: 40 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool ValidProjection(PrefixProjection? projection) => projection is not null &&
        new[] { projection.Control, projection.History, projection.Dynamic, projection.Settings, projection.Whole }
            .All(s => s is not null && EvaluationLimits.Hash(s.Sha256) && s.Bytes is >= 0 and <= AgentLimits.RequestBytes &&
                s.Count is >= 0 and <= AgentLimits.PartsTotal * 8);

    internal static string SessionHash(string session) => AgentCanonical.HashDomain("apr.r6.prefix.session", Encoding.UTF8.GetBytes(session));
    internal static ProjectChatRequest Request(AgentRunRequest run) =>
        new(run.InitialMessages, AgentToolRegistry.Definitions.ToArray(), run.Continuation, ThinkingRequired: true);
}

// Observation cannot change an R5 Agent outcome. Invalid projection is explicit missing evidence.
internal sealed class HistoryChatClient(PrefixBoundary boundary, IProjectChatClient inner,
    PrefixObservation? baseline) : IProjectChatClient
{
    private ImmutableArray<PrefixObservation?> calls = [];
    internal HistoryCapture Capture => new(baseline is null || calls.Any(c => c is null) ? "unmeasurable" : "observed", baseline, calls);

    internal static PrefixObservation? Measure(PrefixBoundary boundary, ProjectChatRequest request)
    {
        try { return PrefixMeasurement.Observe(boundary, request); }
        catch (PrefixObservationException) { return null; }
    }

    public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
    {
        if (calls.Length < AgentLimits.ModelCalls) calls = calls.Add(Measure(boundary, request));
        return inner.GetResponseAsync(request, token);
    }
}
