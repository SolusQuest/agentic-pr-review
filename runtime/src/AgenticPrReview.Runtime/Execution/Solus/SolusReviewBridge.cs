using AgenticPrReview.Runtime.Host.State;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
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
    private readonly ReviewTokenBudget selectedBudget;
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
        selectedBudget = budget ?? new(Cr1DevelopmentProfile.InputTokenAllowance,
            Cr1DevelopmentProfile.InputTokenAllowance, Cr1DevelopmentProfile.OutputTokenAllowance);
        Facts = new(executionId, selectedBudget);
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
        return Complete(result, terminal.Refused, cancellationToken, deadline.Token);
    }

    // R5's trusted Host entry. Construction consumes the actual live authority;
    // it never creates native SESSION/state or a publication capability.
    internal async Task<SolusContextReviewOutcome> RunAuthorizedAsync(SolusFreshHostContext context,
        string instructions, IReadOnlyList<AgentInput> data, SolusSnapshotTools tools,
        SolusReviewCandidateHost domain, IRestrictedContextSink? sink,
        TimeSpan hostRemaining, CancellationToken cancellationToken,
        Func<IModelProvider, IModelProvider>? trustedProviderDecorator = null,
        Func<SolusRestrictedStateCompatibility, IRestrictedContextSink?>? sinkFactory = null,
        SolusRestrictedStateCompatibility? expectedCompatibility = null,
        SolusConsumedContext? consumed = null, Action? providerStarted = null)
    {
        if (Interlocked.Exchange(ref used, 1) != 0) throw new InvalidOperationException("APR_SOLUS_REVIEW_ALREADY_USED");
        var limits = context.Policy.LimitAuthority;
        if (acknowledgement != ExposureStrength.Volatile || !context.IsLive || tools.ExecutionId != Facts.ExecutionId ||
            !SolusReviewCapacity.TrySelect(context.Policy.ConfigBytes.AsSpan(), out var capacity) ||
            selectedBudget != capacity!.Tokens ||
            SolusSnapshotTools.ArgumentCorrectionFor(tools.Registrations, context.Snapshot) is null ||
            !AgentLimitAuthority.TryResolve(context.Policy.AdapterId, limits, out var profile) ||
            profile != AgentLimitProfile.Current)
            throw new ArgumentException("APR_SOLUS_REVIEW_AUTHORITY");
        var duration = TimeSpan.FromSeconds(capacity.TimeoutSeconds);
        if (hostRemaining < duration) duration = hostRemaining;
        if (duration <= TimeSpan.Zero)
        {
            if (consumed is not null) return new(null, null);
            var empty = new AgentRunUsage(Facts.ExecutionId, UsageInventoryCoverage.Complete, []);
            return new(new(AgentTerminationReason.ResourceLimit, Facts.Freeze(empty),
                ActionHostTerminationReason.DeadlineExceeded, false, empty), null);
        }
        ValidateText(instructions, data);
        using var deadline = new CancellationTokenSource(duration, clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        // A compiled fixture can fault only capture/export after real provider
        // work; no repository, environment or model field selects this delegate.
        IModelProvider observedProvider = providerStarted is null ? provider : new ExchangeObserver(provider, providerStarted);
        var currentProvider = trustedProviderDecorator?.Invoke(observedProvider) ?? observedProvider;
        using var runtime = SolusRuntimeConstruction.CreateForAuthorizedReview(context,
            _ => new(currentProvider, tools.Registrations, hooks));
        if (expectedCompatibility is not null && runtime.SourceSelection.Compatibility != expectedCompatibility)
            throw new ArgumentException("APR_SOLUS_CONFIGURATION_MISMATCH");
        AgentContextEnvelope? envelope = null;
        ContextRoundGrant? grant = null;
        if (consumed is not null && !runtime.TryArm(consumed, out envelope, out grant))
            throw new ArgumentException("APR_SOLUS_CONTEXT_AUTHORITY");
        var currentSink = sinkFactory is null ? sink : sinkFactory(runtime.SourceSelection.Compatibility);
        var request = Request(instructions, data, duration,
            capacity.ModelCalls, admitToolCalls: true, admittedCapacity: capacity);
        var terminal = new SolusTerminalAccountingGate(Facts, domain);
        var result = await ((IContextCandidateAgent)runtime.Agent).ExecuteCandidatesWithContextAsync(
            new(request, consumed is null ? ContextExecutionIntent.Fresh : ContextExecutionIntent.NewRunFromContext,
                envelope, grant), SolusReviewCandidateHost.Bounds(), terminal, currentSink,
            cancellationToken: linked.Token).ConfigureAwait(false);
        // Supplied admission/import rejection has no current work outcome.
        // It is neither a fresh fallback nor an invented provider failure.
        if (result.Candidates is not { } candidates) return new(null, result);
        // Capture happens after work. Its latency/failure cannot rewrite an
        // actually completed work outcome; Host eligibility separately observes
        // the caller/deadline before any prospective effect.
        var completed = candidates.StopReason == CandidateStopReason.Completed &&
            candidates.Outcome.Reason == AgentTerminationReason.Completed;
        return new(Complete(candidates, terminal.Refused,
            completed ? CancellationToken.None : cancellationToken,
            completed ? CancellationToken.None : deadline.Token), result);
    }

    private SolusReviewOutcome Complete(CandidateExecutionResult result, bool terminalRefused,
        CancellationToken cancellationToken, CancellationToken deadline)
    {
        var accounting = Facts.Freeze(result.Outcome.Usage);
        var reason = cancellationToken.IsCancellationRequested ? ActionHostTerminationReason.Cancelled
            : deadline.IsCancellationRequested || result.StopReason == CandidateStopReason.DurationLimit ? ActionHostTerminationReason.DeadlineExceeded
            : Facts.HostStopped ? ActionHostTerminationReason.HostFailure
            : Facts.Overrun || Facts.Refused || Facts.BudgetStopped ? ActionHostTerminationReason.TokenLimit
            : Facts.MissingCore || accounting is null ? ActionHostTerminationReason.ProviderFailure
            : TokenReservationExhausted(result) ? ActionHostTerminationReason.TokenLimit
            : result.StopReason == CandidateStopReason.RuntimeLimit || result.Outcome.Reason == AgentTerminationReason.ResourceLimit ? ActionHostTerminationReason.ResourceLimit
            : result.Outcome.Reason == AgentTerminationReason.Cancelled ? ActionHostTerminationReason.Cancelled
            : result.Outcome.Reason == AgentTerminationReason.Completed && result.Receipts.Count == 1 &&
                result.Receipts[0].IsAccepted && !terminalRefused ? ActionHostTerminationReason.ReviewCompleted
            : result.StopReason is CandidateStopReason.ProductionStopped or CandidateStopReason.FailedAcknowledgement or
                CandidateStopReason.MissingAcknowledgement or CandidateStopReason.UnknownAcknowledgement or
                CandidateStopReason.MismatchedFeedback or CandidateStopReason.DuplicateFeedback or
                CandidateStopReason.ProgressObserverFailed ? ActionHostTerminationReason.HostFailure
            : result.Outcome.Reason == AgentTerminationReason.Failed ? ActionHostTerminationReason.ProviderFailure
            : ActionHostTerminationReason.InvalidResult;
        return new(result.Outcome.Reason, accounting, reason, reason == ActionHostTerminationReason.ReviewCompleted, result.Outcome.Usage);
    }

    private static bool TokenReservationExhausted(CandidateExecutionResult result)
    {
        // Runtime preflight can refuse before provider hooks observe an attempt.
        // Use its current accounting balance, never historical usage or an
        // inference from the coarse ResourceLimit reason alone.
        if (result.Outcome.Reason != AgentTerminationReason.ResourceLimit ||
            result.Outcome.Usage.Accounting is not { } accounting) return false;
        return accounting.Input.RemainingAllowance is { } input && input < accounting.Policy.Reservation.InputTokens ||
            accounting.Output.RemainingAllowance is { } output && output < accounting.Policy.Reservation.OutputTokens;
    }

    // The unauthenticated R4 control seam retains its original probe policy.
    // Every Host review supplies the admitted capacity here.
    internal AgentRequest Request(string instructions, IReadOnlyList<AgentInput> data, TimeSpan duration,
        int maximumCalls = Cr1DevelopmentProfile.LogicalCalls, bool admitToolCalls = false,
        SolusReviewCapacity? admittedCapacity = null) => new(
        Facts.ExecutionId, instructions, data, new(maximumCalls, duration),
        AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation |
        AgentCapability.UsageReporting | AgentCapability.DispatchLimits | AgentCapability.UsageThresholds |
        AgentCapability.ToolInvocationLimit | AgentCapability.UsageAccounting |
        (admitToolCalls ? AgentCapability.ToolCallAdmissionLimit : AgentCapability.None),
        new(maximumLogicalCalls: maximumCalls, maximumPhysicalDispatches: maximumCalls,
            inputTokenThreshold: admittedCapacity?.InputAllowance ?? Cr1DevelopmentProfile.InputTokenThreshold,
            outputTokenThreshold: admittedCapacity?.Tokens.OutputTokens ?? Cr1DevelopmentProfile.OutputTokenThreshold,
            maximumToolInvocations: Cr1DevelopmentProfile.ToolInvocations,
            maximumToolCallAdmissions: admitToolCalls ? Cr1DevelopmentProfile.ToolInvocations : null,
            accountingPolicy: new(new(Cr1DevelopmentProfile.InputTokenReservation, Cr1DevelopmentProfile.OutputTokenReservation),
                admittedCapacity?.InputAllowance ?? Cr1DevelopmentProfile.InputTokenAllowance,
                admittedCapacity?.Tokens.OutputTokens ?? Cr1DevelopmentProfile.OutputTokenAllowance, UnknownUsagePolicy.Stop),
            retryPolicy: new(Cr1DevelopmentProfile.AttemptsPerLogicalCall)));

    internal static ProviderExchangeBounds Bounds() => new(Cr1DevelopmentProfile.ProviderInputs,
        Cr1DevelopmentProfile.NonterminalDefinitions, Cr1DevelopmentProfile.ToolCallsPerBatch,
        Cr1DevelopmentProfile.RequestPayloadBytes, Cr1DevelopmentProfile.ResponsePayloadBytes, Cr1DevelopmentProfile.ContinuationBytes);
    internal static void ValidateText(string instructions, IReadOnlyList<AgentInput> data)
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

    // Import and request admission do not start a provider exchange. Keep Host
    // accounting untouched until the actual current provider operation begins.
    private sealed class ExchangeObserver(IModelProvider inner, Action started)
        : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
    {
        private int observed;
        public ProviderScope Scope => inner.Scope;
        public ProviderCapabilities Capabilities => inner.Capabilities;
        public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
        {
            if (Interlocked.Exchange(ref observed, 1) == 0) started();
            return inner.ExchangeAsync(request, token);
        }
        public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
    }
}

internal sealed record SolusContextReviewOutcome(SolusReviewOutcome? Work, ContextCandidateExecutionResult? Execution)
{
    public override string ToString() => nameof(SolusContextReviewOutcome);
}

internal sealed record SolusReviewOutcome(AgentTerminationReason RuntimeReason, ProviderAccounting? Accounting,
    ActionHostTerminationReason Termination, bool EligibleForProductValidation, AgentRunUsage? CurrentUsage = null)
{
    public override string ToString() => nameof(SolusReviewOutcome);
}
