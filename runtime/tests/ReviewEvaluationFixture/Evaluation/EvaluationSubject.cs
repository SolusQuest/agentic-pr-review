using System.Collections.Immutable;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

internal enum EvaluationFailureSource { None, Provider, Agent, Tool, HostState, Evaluator, Unknown }
internal enum EvaluationFailureKind { None, ProviderCall, MalformedOutput, ToolOperation, StateAdmission, InvalidInput, Unknown }

// Factories consume outcomes at their actual boundary; arbitrary error text is never classified or emitted.
internal sealed class EvaluationFailure
{
    private EvaluationFailure(EvaluationFailureSource source, EvaluationFailureKind kind)
    {
        Source = source;
        Kind = kind;
    }
    internal EvaluationFailureSource Source { get; }
    internal EvaluationFailureKind Kind { get; }
    internal static EvaluationFailure Invalid { get; } = new(EvaluationFailureSource.Evaluator, EvaluationFailureKind.InvalidInput);
    internal static EvaluationFailure Unknown { get; } = new(EvaluationFailureSource.Unknown, EvaluationFailureKind.Unknown);

    internal static EvaluationFailure FromAgentOutcome(AgentRunOutcome? outcome)
    {
        if (outcome is null || outcome.Succeeded || outcome.Review is not null || outcome.Diagnostic is null ||
            outcome.Diagnostic.ModelCalls is < 0 or > AgentLimits.ModelCalls ||
            outcome.Diagnostic.ToolCalls is < 0 or > AgentLimits.ToolCalls) return Invalid;
        var code = outcome.Diagnostic.Code;
        if (AgentToolResultAdmission.IsFrozenFailureCode(code))
            return new(EvaluationFailureSource.Tool, EvaluationFailureKind.ToolOperation);
        return code switch
        {
            AgentFailureCodes.TerminalInvalid or AgentFailureCodes.TerminalSequenceInvalid or
                AgentFailureCodes.ToolArgumentsInvalid or AgentFailureCodes.UnknownTool =>
                new(EvaluationFailureSource.Agent, EvaluationFailureKind.MalformedOutput),
            // response_invalid can originate at initial admission or backend normalization.
            AgentFailureCodes.ResponseInvalid or AgentFailureCodes.MissingTool =>
                new(EvaluationFailureSource.Unknown, EvaluationFailureKind.MalformedOutput),
            _ => Unknown,
        };
    }

    // Call only with an exception observed at the provider transport invocation boundary.
    internal static EvaluationFailure FromProviderTransport(HttpRequestException _) =>
        new(EvaluationFailureSource.Provider, EvaluationFailureKind.ProviderCall);

    internal static EvaluationFailure FromToolExecution(AgentToolExecution? result) =>
        result is { Succeeded: false } && AgentToolResultAdmission.IsFrozenFailureCode(result.FailureCode)
            ? new(EvaluationFailureSource.Tool, EvaluationFailureKind.ToolOperation) : Unknown;

    internal static EvaluationFailure FromSessionBuild(AgentSessionBuildResult? result) =>
        result is { Succeeded: false, Artifact: null } && result.FailureCode is
            AgentSessionCodes.ScopeMismatch or AgentSessionCodes.RecordInvalid or AgentSessionCodes.ConstructionLimit or
            AgentSessionCodes.ClassificationInvalid or AgentSessionCodes.AssociationInvalid or
            AgentSessionCodes.ContinuationInvalid or AgentSessionCodes.TransitionRejected
            ? new(EvaluationFailureSource.HostState, EvaluationFailureKind.StateAdmission) : Unknown;

    public override string ToString() => "evaluation_failure";
}

// Safe planned-attempt identity exists before completion, without admitting any result.
internal sealed class EvaluationAttempt
{
    private EvaluationAttempt(EvaluationRunInput run, string configuration)
    {
        Run = run;
        ConfigurationSha256 = configuration;
        AttemptSha256 = Hash("attempt", configuration, AgentCanonical.HashRaw(EvaluationJson.Write(run)));
    }

    internal EvaluationRunInput Run { get; }
    internal string ConfigurationSha256 { get; }
    internal string AttemptSha256 { get; }

    internal static EvaluationAttempt? Admit(AgentSessionTrustedRequest? trusted, EvaluationRunInput? run)
    {
        if (trusted is null || run is not { Valid: true } ||
            !AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable)) return null;
        // Case, repository, session, history and source revision are not configuration settings.
        var configuration = ConfigurationIdentity(stable!.StablePlan, run);
        return new(run, configuration);
    }

    // Pure projection of already materialized settings; it does not admit an attempt or subject.
    internal static string ConfigurationIdentity(StableAgentPlan plan, EvaluationRunInput run) =>
        Hash("configuration", plan.ProviderId, plan.ModelId, plan.AdapterId, plan.PolicySha256,
            plan.ToolsetSha256, plan.LimitsSha256, run.ProviderConfigurationSha256, run.Mode);

    internal static string Hash(string domain, params string[] values)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            foreach (var value in values) writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
        return AgentCanonical.HashDomain("apr.r5.evaluation." + domain, stream.ToArray());
    }

    public override string ToString() => "evaluation_attempt";
}

internal sealed class EvaluationSubject
{
    private EvaluationSubject(EvaluationAttempt attempt, R3QualitySubject admitted, string execution)
    {
        Attempt = attempt;
        ReviewedIdentity = admitted.ReviewedIdentity!;
        Findings = admitted.Review!.Findings;
        Observations = admitted.ToolObservations.Select(o => new RequiredObservation(o.Name, o.Observation.ObservationId)).ToImmutableArray();
        ExecutionSha256 = execution;
    }

    internal EvaluationAttempt Attempt { get; }
    internal EvaluationRunInput Run => Attempt.Run;
    internal ReviewedIdentity ReviewedIdentity { get; }
    internal ImmutableArray<AgentFinding> Findings { get; }
    internal ImmutableArray<RequiredObservation> Observations { get; }
    internal string ConfigurationSha256 => Attempt.ConfigurationSha256;
    internal string ExecutionSha256 { get; }

    internal static EvaluationSubject? Admit(AgentSessionBuildInput? input, EvaluationRunInput? run)
    {
        if (input?.Run?.ReviewedIdentity is null || input.Run.StablePlan is null ||
            input.Outcome is null || input.TrustedRequest is null || input.Run.InitialMessages is null ||
            input.Outcome.Events.IsDefault || run is not { Valid: true }) return null;
        var attempt = EvaluationAttempt.Admit(input.TrustedRequest, run);
        if (attempt is null) return null;
        var creation = R3QualitySubject.TryCreateCompleted(input, R3QualityFreshProcessTwoInputSet.Capture([]));
        if (!creation.Succeeded || creation.Subject is null) return null;
        var admitted = creation.Subject;
        // Actual request/history and the full plan remain bound to the completed execution.
        var execution = EvaluationAttempt.Hash("execution", attempt.AttemptSha256,
            AgentCanonical.HashRaw(admitted.InitialRequest.AsSpan()),
            AgentCanonical.StablePlanSha256(input.Run.StablePlan), input.Run.SessionId, admitted.Review!.TerminalSha256,
            string.Join(',', admitted.ToolObservations.Select(o => o.Observation.ObservationId)));
        return new(attempt, admitted, execution);
    }

    public override string ToString() => "evaluation_subject";
}
