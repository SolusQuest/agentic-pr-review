using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.LiveAgentVerifierFixture;

internal sealed record VerifierNegativeCase(
    string Id,
    string Phase,
    string StateExpectation,
    string StableCode,
    bool RequiresPriorState);

internal static class VerifierScenarioDomain
{
    internal static bool IsNegative(VerifierScenario scenario) =>
        scenario is >= VerifierScenario.OuterAuthorizationDenied and
            <= VerifierScenario.PublicResultCanary;

    internal static bool IsContinuing(VerifierScenario scenario) =>
        scenario is VerifierScenario.ContinuationRestore or
            VerifierScenario.TransitionFromHeadInvalid or
            VerifierScenario.LineageTampered;

    internal static VerifierScenario ProviderBehavior(
        VerifierScenario scenario) => scenario switch
    {
        VerifierScenario.ProviderHttpFailure or
        VerifierScenario.ProviderMalformedResponse or
        VerifierScenario.ToolArgumentsInvalid or
        VerifierScenario.TerminalUngrounded or
        VerifierScenario.QualityFailedAfterCommit => VerifierScenario.MustFind,
        VerifierScenario.PublicResultCanary => VerifierScenario.MustNotFind,
        _ => scenario,
    };

    internal static VerifierNegativeCase Negative(
        VerifierScenario scenario) => scenario switch
    {
        VerifierScenario.OuterAuthorizationDenied => new(
            "outer-authorization-denied",
            "pre_activation",
            "no_advance",
            RestrictedStateCodes.AccessDenied,
            false),
        VerifierScenario.InnerAuthorizationDenied => new(
            "inner-authorization-denied",
            "pre_transport",
            "no_advance",
            RestrictedStateCodes.AccessDenied,
            false),
        VerifierScenario.ProviderHttpFailure => new(
            "provider-http-failure",
            "pre_commit",
            "no_advance",
            AgentFailureCodes.ChatFailed,
            false),
        VerifierScenario.ProviderMalformedResponse => new(
            "provider-malformed-response",
            "pre_commit",
            "no_advance",
            AgentFailureCodes.ResponseInvalid,
            false),
        VerifierScenario.ToolArgumentsInvalid => new(
            "tool-arguments-invalid",
            "pre_commit",
            "no_advance",
            AgentFailureCodes.ToolArgumentsInvalid,
            false),
        VerifierScenario.TerminalUngrounded => new(
            "terminal-ungrounded",
            "pre_commit",
            "no_advance",
            AgentFailureCodes.TerminalInvalid,
            false),
        VerifierScenario.TransitionFromHeadInvalid => new(
            "transition-from-head-invalid",
            "pre_activation",
            "prior_unchanged",
            LiveAgentFreshProcessCodes.TransitionRejected,
            true),
        VerifierScenario.LineageTampered => new(
            "lineage-authority-tampered",
            "pre_activation",
            "prior_unchanged",
            LiveAgentFreshProcessCodes.LineageInvalid,
            true),
        VerifierScenario.QualityFailedAfterCommit => new(
            "quality-failed-after-commit",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.TransportProofFailed,
            false),
        VerifierScenario.PublicResultCanary => new(
            "public-result-canary",
            "post_commit",
            "accepted_preserved",
            LiveAgentFreshProcessCodes.TransportProofFailed,
            false),
        _ => throw new InvalidOperationException(
            "The verifier scenario is not negative."),
    };
}

internal static class VerifierCanaries
{
    internal const string Provider = "APR111_PROVIDER_SECRET_CANARY";
    internal const string State = "APR111_STATE_KEY_CANARY_12345678";
    internal const string StateBase64 =
        "QVBSMTExX1NUQVRFX0tFWV9DQU5BUllfMTIzNDU2Nzg=";
    internal const string GitHub = "APR111_GITHUB_CANARY";
    internal const string Actions = "APR111_ACTIONS_CANARY";
    internal const string Workflow = "APR111_UNRELATED_WORKFLOW_CANARY";
    internal const string Repository = "APR111_REPOSITORY_CANARY";
    internal const string Path = "APR111_PATH_CANARY";
    internal const string Prompt = "APR111_PROMPT_INJECTION_CANARY";
    internal const string PublicResult = "APR111_PUBLIC_RESULT_CANARY";
}
