using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Application.Solus;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Execution.Solus;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Runtime.Startup;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

// Public installed-runtime probes isolate enforcement hidden by earlier product
// bounds. Their configurations are diagnostics, never selectable APR profiles.
internal static class CapacityProbes
{
    internal static readonly List<object> Results = [];
    internal static async Task Run()
    {
        var rejected = new SolusFreshReviewResult(Guid.NewGuid(), SolusFreshReviewFailure.InputProfileRejected);
        Proof.Require(CapacityHostRun.ObservationCount(rejected) is null &&
            CapacityHostRun.ObservationCount(null) is null, "unavailable_observation_inventory");
        Proof.Require(CapacityHostRun.ObservationCount(rejected with { Observations = [] }) == 0,
            "known_empty_observation_inventory");
        var omitted = SolusReviewBridge.BoundBudget(null);
        Proof.Require(omitted == new ReviewTokenBudget(65536, 65536, 8192), "retained_default_budget_seam");
        var explicitBudget = new ReviewTokenBudget(4096, 4096, 1024);
        Proof.Require(ReferenceEquals(explicitBudget, SolusReviewBridge.BoundBudget(explicitBudget)), "budget_seam_keeps_explicit_selection");
        try
        {
            SolusReviewBridge.BoundBudget(new(65537, 65536, 8192));
            throw new ProofFailure("budget_seam_must_not_clamp");
        }
        catch (ArgumentException) { Proof.Require(true, "budget_seam_rejects_unsupported_selection"); }
        Proof.Stage = "portable_selected_request";
        foreach (var json in new[] { "{}", "{\"review\":{\"maxModelCalls\":2,\"timeoutSeconds\":3," +
            "\"maxUncachedInputTokens\":4096,\"maxCachedInputTokens\":4096,\"maxOutputTokens\":1024}}",
            "{\"review\":{\"maxUncachedInputTokens\":1,\"maxCachedInputTokens\":1,\"maxOutputTokens\":1}}" })
        {
            Proof.Require(SolusReviewCapacity.TrySelect(Encoding.UTF8.GetBytes(json), out var selected), "selected_configuration");
            using var bridge = new SolusReviewBridge(Guid.NewGuid(), DeepSeekCredential.Create("synthetic-v3-key"),
                new Hooks(), selected!.Tokens, new NoNetwork());
            var request = bridge.Request("trusted", [], TimeSpan.FromSeconds(selected.TimeoutSeconds),
                selected.ModelCalls, true, selected);
            Proof.Require(request.Bounds.MaximumWorkUnits == selected.ModelCalls &&
                request.Bounds.MaximumDuration.TotalSeconds == selected.TimeoutSeconds &&
                request.UsageLimits!.MaximumLogicalCalls == selected.ModelCalls &&
                request.UsageLimits.MaximumPhysicalDispatches == selected.ModelCalls &&
                request.UsageLimits.AccountingPolicy!.InputAllowance == selected.InputAllowance &&
                request.UsageLimits.AccountingPolicy.OutputAllowance == selected.Tokens.OutputTokens &&
                request.UsageLimits.InputTokenThreshold == selected.InputAllowance &&
                request.UsageLimits.OutputTokenThreshold == selected.Tokens.OutputTokens, "actual_request_honors_selection");
            Results.Add(new { kind = "selected-request", calls = selected.ModelCalls, seconds = selected.TimeoutSeconds,
                input_allowance = selected.InputAllowance, output_allowance = selected.Tokens.OutputTokens });
        }
        Proof.Stage = "portable_cumulative_attempts";
        var source = await Execute(196608, 64, 3, 1);
        Proof.Require(source.Completed && source.Sink.Envelope is not null, "first_round_completed");
        for (var round = 2; round <= 3; round++)
        {
            var next = await Execute(196608, 64, 3, 1, source);
            Proof.Require(next.Completed && next.Execution.Context.History.Count == round - 1 &&
                next.Execution.Context.History.Sum(h => h.Usage.Attempts.Count) == round - 1 &&
                next.Execution.Context.Outcome?.Usage?.Attempts.Count == 1 && next.Authority?.Claims == 1,
                "retained_attempts_current_allowance_renewed");
            source = next;
        }
        var denied = await Execute(196608, 64, 3, 1, source);
        Proof.Require(denied.Execution.Context.Admission == ContextAdmission.Rejected &&
            denied.Execution.Candidates is null && denied.Provider.Calls == 0 && denied.Provider.Imports == 0 &&
            denied.Authority?.Claims == 0, "attempt_one_over_rejected_before_claim_and_import");
        Results.Add(new { kind = "cumulative-attempts-diagnostic", retained_attempt_capacity = 3,
            completed_rounds = 3, next_sends = denied.Provider.Calls, next_claims = denied.Authority?.Claims });

        Proof.Stage = "portable_candidate_record_boundary";
        var recordsExact = await Execute(2 * 1024 * 1024, 62, 64, 20);
        var recordsOver = await Execute(2 * 1024 * 1024, 61, 64, 20);
        Proof.Require(recordsExact.Completed && recordsExact.Host.Calls == 20 && !recordsOver.Completed &&
            recordsOver.Host.Calls == 19 && recordsOver.Provider.Calls == 20, "real_candidate_record_reservation_boundary");
        Results.Add(new { kind = "candidate-records-diagnostic", required_capacity = 62,
            captured_inventory = 61, refused_capacity = 61, accepted_callbacks = recordsExact.Host.Calls,
            refused_callbacks = recordsOver.Host.Calls });

        Proof.Stage = "portable_serializer_reservation_boundary";
        var low = 1; var high = 196608;
        Proof.Require((await Execute(high, 64, 64, 1)).Completed, "byte_probe_upper_admitted");
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if ((await Execute(middle, 64, 64, 1)).Completed) high = middle;
            else low = middle + 1;
        }
        var bytesExact = await Execute(high, 64, 64, 1);
        var bytesOver = await Execute(high - 1, 64, 64, 1);
        Proof.Require(bytesExact.Completed && !bytesOver.Completed && bytesOver.Host.Calls == 0,
            "actual_serializer_and_pre_callback_reservation_boundary");
        Proof.Require(bytesExact.Sink.Envelope!.PayloadByteCount < high && high <= 196608,
            "encoded_size_different_from_required_retention");
        Results.Add(new { kind = "retained-bytes-diagnostic", required_capacity = high,
            actual_encoded_bytes = bytesExact.Sink.Envelope.PayloadByteCount, refused_capacity = high - 1,
            refused_callbacks = bytesOver.Host.Calls, response_bound = bytesExact.Provider.ResponseBound });
    }

    private sealed record ProbeRun(ContextCandidateExecutionResult Execution, ProbeProvider Provider, Domain Host,
        Sink Sink, Authority? Authority)
    {
        internal bool Completed => Execution.Candidates?.StopReason == CandidateStopReason.Completed &&
            Execution.Context.Outcome?.Reason == AgentTerminationReason.Completed && Sink.Envelope is not null;
    }

    private static async Task<ProbeRun> Execute(int bytes, int records, int attempts, int calls, ProbeRun? source = null)
    {
        var provider = new ProbeProvider(); var domain = new Domain(calls); var sink = new Sink();
        var id = Guid.NewGuid();
        var grant = source is null ? null : new ContextRoundGrant(Guid.NewGuid(), source.Execution.Context.Checkpoint!, id, Guid.NewGuid());
        var authority = source is null ? null : new Authority(source.Sink.Envelope!, grant!);
        var configuration = new RuntimeConfiguration(provider, [], new Hooks(), SolusReviewBridge.Bounds(),
            contextAuthority: authority, toolArgumentCorrection: SolusToolCorrection.Policy);
        var agent = (IContextCandidateAgent)RuntimeAgentFactory.Create(configuration, new(timeProvider: new StillClock(),
            maximumAttempts: attempts, maximumRecords: records, maximumRetainedBytes: bytes, requireContinuation: true));
        var request = new AgentRequest(id, "trusted diagnostic", [], new(calls, TimeSpan.FromSeconds(30)),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation |
            AgentCapability.DispatchLimits | AgentCapability.UsageAccounting | AgentCapability.UsageReporting,
            new(maximumLogicalCalls: calls, maximumPhysicalDispatches: calls,
                accountingPolicy: new(new(8192, 1024), 65536, 8192, UnknownUsagePolicy.Stop)));
        var result = await agent.ExecuteCandidatesWithContextAsync(
            new(request, source is null ? ContextExecutionIntent.Fresh : ContextExecutionIntent.NewRunFromContext,
                source?.Sink.Envelope, grant), new(calls, 0, calls - 1), domain, sink);
        return new(result, provider, domain, sink, authority);
    }

    private sealed class ProbeProvider() : ModelProvider(new("v3-probe", "synthetic"), ProviderCapabilities.ToolCalls |
        ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolArgumentCorrection), IProviderContextPersistence
    {
        internal int Calls, Imports, ResponseBound;
        protected override ProviderError ValidateRequestCore(ProviderRequest request) => ProviderError.None;
        protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
        {
            Calls++; ResponseBound = request.Bounds.MaximumResponseBytes;
            Proof.Require(ResponseBound == 16384, "diagnostic_full_response_reservation");
            observation.ObserveDispatch(DispatchExposure.Dispatched); observation.CaptureUsage(new(10, 5));
            return ValueTask.FromResult(new ProviderResponse(request.Scope, request.Attempt, ProviderFinish.Final,
                "synthetic diagnostic", [], new(request.Scope, request.Attempt, [7, 3, 1])));
        }
        public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1, [7, 3, 1]);
        public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
        { Imports++; return state.FormatVersion == 1 && state.CopyRestrictedPayload().AsSpan().SequenceEqual(new byte[] { 7, 3, 1 }); }
    }
    private sealed class Domain(int desired) : ICandidateHost
    {
        internal int Calls;
        public ValueTask<CandidateFeedback?> SubmitAsync(CandidateSubmission submission, CancellationToken token = default) =>
            ValueTask.FromResult<CandidateFeedback?>(new(submission.ExecutionId, submission.SubmissionId,
                CandidateAcknowledgement.Acknowledged, CandidateDecision.Accept,
                ++Calls == desired ? CandidateContinuation.End : CandidateContinuation.Continue));
    }
    private sealed class Sink : IRestrictedContextSink
    {
        internal AgentContextEnvelope? Envelope;
        public void Capture(AgentContextEnvelope envelope) => Envelope = envelope;
    }
    private sealed class Authority(AgentContextEnvelope source, ContextRoundGrant expected) : IRuntimeContextAuthority
    {
        internal int Claims;
        public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
        {
            Claims++;
            return Claims == 1 && ReferenceEquals(grant, expected) && context.ImplementationId == source.ImplementationId &&
                context.CopyRestrictedPayload().AsSpan().SequenceEqual(source.CopyRestrictedPayload());
        }
    }
    private sealed class StillClock : TimeProvider { public override long GetTimestamp() => 0; }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new ProofFailure("unexpected_network");
    }
    private sealed class Hooks : IRuntimeExposureHooks
    {
        public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token) =>
            ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
        public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
            ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
    }
}
