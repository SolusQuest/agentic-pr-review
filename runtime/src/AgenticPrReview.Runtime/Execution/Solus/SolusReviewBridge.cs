using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Execution.Solus;

// Callable, single-review managed boundary; no production Host route selects it.
// Runtime scheduling, candidate production, tools and retries remain Solus-owned.
internal sealed class SolusReviewBridge : IDisposable
{
    private readonly SolusDeepSeekAdapter provider;
    private readonly IRuntimeExposureHooks hooks;
    private readonly ExposureStrength acknowledgement;
    private readonly TimeProvider clock;
    private int used;
    internal SolusReviewFacts Facts { get; }

    internal SolusReviewBridge(Guid executionId, DeepSeekCredential credential, IRuntimeExposureHooks hostHooks,
        ReviewTokenBudget? budget = null, HttpMessageHandler? controlledTransport = null,
        TimeProvider? clock = null, ExposureStrength acknowledgement = ExposureStrength.Volatile)
    {
        ArgumentNullException.ThrowIfNull(hostHooks);
        if (!Enum.IsDefined(acknowledgement)) throw new ArgumentOutOfRangeException(nameof(acknowledgement));
        this.acknowledgement = acknowledgement;
        this.clock = clock ?? TimeProvider.System;
        Facts = new(executionId, budget ?? new(Cr1DevelopmentProfile.InputTokenAllowance,
            Cr1DevelopmentProfile.InputTokenAllowance, Cr1DevelopmentProfile.OutputTokenAllowance));
        provider = new(credential, Facts, controlledTransport);
        hooks = new SolusAccountingHooks(Facts, hostHooks);
    }

    internal async Task<SolusReviewOutcome> RunAsync(string instructions, IReadOnlyList<AgentInput> data,
        IReadOnlyList<RuntimeToolRegistration> tools, ICandidateHost domain,
        TimeSpan? hostRemaining = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("APR_SOLUS_REVIEW_ALREADY_USED");
        var duration = TimeSpan.FromSeconds(Cr1DevelopmentProfile.DurationSeconds);
        if (hostRemaining is { } remaining && remaining < duration) duration = remaining;
        if (duration <= TimeSpan.Zero)
        {
            var empty = new AgentRunUsage(Facts.ExecutionId, UsageInventoryCoverage.Complete, []);
            return new(AgentTerminationReason.ResourceLimit, Facts.Freeze(empty), ActionHostTerminationReason.DeadlineExceeded, false);
        }
        ValidateText(instructions, data);
        // The outer Host headroom starts once, before construction. Its token
        // bounds provider/backoff/tools/terminal work; closure grants no new work.
        using var deadline = new CancellationTokenSource(duration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var configuration = new RuntimeConfiguration(provider, tools, hooks, Bounds(), acknowledgement);
        var agent = (ICandidateAgent)SolusRuntimeConstruction.Create(configuration, Cr1DevelopmentProfile.Id);
        var request = Request(instructions, data, duration);
        var terminal = new SolusTerminalAccountingGate(Facts, domain);
        var result = await agent.ExecuteCandidatesAsync(new(request,
            new(Cr1DevelopmentProfile.CandidateSubmissions, Cr1DevelopmentProfile.CandidateRepairs,
                Cr1DevelopmentProfile.CandidateContinuations)), terminal, cancellationToken: linked.Token).ConfigureAwait(false);
        var accounting = Facts.Freeze(result.Outcome.Usage);
        var reason = cancellationToken.IsCancellationRequested ? ActionHostTerminationReason.Cancelled
            : deadline.IsCancellationRequested || result.StopReason == CandidateStopReason.DurationLimit ? ActionHostTerminationReason.DeadlineExceeded
            : Facts.HostStopped ? ActionHostTerminationReason.HostFailure
            : Facts.Overrun || Facts.Refused || Facts.BudgetStopped ? ActionHostTerminationReason.TokenLimit
            : Facts.MissingCore || accounting is null ? ActionHostTerminationReason.ProviderFailure
            : result.StopReason == CandidateStopReason.RuntimeLimit || result.Outcome.Reason == AgentTerminationReason.ResourceLimit ? ActionHostTerminationReason.ResourceLimit
            : result.Outcome.Reason == AgentTerminationReason.Cancelled ? ActionHostTerminationReason.Cancelled
            : result.Outcome.Reason == AgentTerminationReason.Completed && result.Receipts.Count == 1 &&
                result.Receipts[0].IsAccepted && !terminal.Refused ? ActionHostTerminationReason.ReviewCompleted
            : result.StopReason is CandidateStopReason.ProductionStopped or CandidateStopReason.FailedAcknowledgement or
                CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement or
                CandidateStopReason.MismatchedFeedback or CandidateStopReason.DuplicateFeedback or
                CandidateStopReason.ProgressObserverFailed ? ActionHostTerminationReason.HostFailure
            : result.Outcome.Reason == AgentTerminationReason.Failed ? ActionHostTerminationReason.ProviderFailure
            : ActionHostTerminationReason.InvalidResult;
        return new(result.Outcome.Reason, accounting, reason, reason == ActionHostTerminationReason.ReviewCompleted, result.Outcome.Usage);
    }

    internal AgentRequest Request(string instructions, IReadOnlyList<AgentInput> data, TimeSpan duration) => new(
        Facts.ExecutionId, instructions, data, new(Cr1DevelopmentProfile.WorkUnits, duration),
        AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation |
        AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds |
        AgentCapability.ToolInvocationLimit | AgentCapability.UsageAccounting,
        new(maximumLogicalCalls: Cr1DevelopmentProfile.LogicalCalls, maximumPhysicalDispatches: Cr1DevelopmentProfile.PhysicalAttempts,
            inputTokenThreshold: Cr1DevelopmentProfile.InputTokenThreshold, outputTokenThreshold: Cr1DevelopmentProfile.OutputTokenThreshold,
            maximumToolInvocations: Cr1DevelopmentProfile.ToolInvocations,
            accountingPolicy: new(new(Cr1DevelopmentProfile.InputTokenReservation, Cr1DevelopmentProfile.OutputTokenReservation),
                Cr1DevelopmentProfile.InputTokenAllowance, Cr1DevelopmentProfile.OutputTokenAllowance, UnknownUsagePolicy.Stop),
            retryPolicy: new(Cr1DevelopmentProfile.AttemptsPerLogicalCall)));

    internal static ProviderExchangeBounds Bounds() => new(Cr1DevelopmentProfile.ProviderInputs,
        Cr1DevelopmentProfile.NonterminalDefinitions, Cr1DevelopmentProfile.ToolCallsPerBatch,
        Cr1DevelopmentProfile.RequestPayloadBytes, Cr1DevelopmentProfile.ResponsePayloadBytes, Cr1DevelopmentProfile.ContinuationBytes);
    private static void ValidateText(string instructions, IReadOnlyList<AgentInput> data)
    {
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            if (string.IsNullOrWhiteSpace(instructions) || utf8.GetByteCount(instructions) > Cr1DevelopmentProfile.InstructionsBytes ||
                data.Count >= Cr1DevelopmentProfile.ProviderInputs || data.Any(d => d is null || utf8.GetByteCount(d.Text) > Cr1DevelopmentProfile.DataItemBytes))
                throw new ArgumentException("APR_SOLUS_REVIEW_INPUT");
        }
        catch (EncoderFallbackException) { throw new ArgumentException("APR_SOLUS_REVIEW_INPUT"); }
    }
    public void Dispose() => provider.Dispose();
    public override string ToString() => nameof(SolusReviewBridge);
}

internal sealed record SolusReviewOutcome(AgentTerminationReason RuntimeReason, ProviderAccounting? Accounting,
    ActionHostTerminationReason Termination, bool EligibleForProductValidation, AgentRunUsage? CurrentUsage = null)
{
    public override string ToString() => nameof(SolusReviewOutcome);
}
