using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Api.Capabilities;
using SolusAgent.Api.Candidates;
using SolusAgent.Api.Context;
using SolusAgent.Api.Execution;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Configuration;
using SolusAgent.Runtime.Api.Exposure;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.SolusToolAdapterFixture;

internal static class Proof
{
    internal static string Stage = "startup";
    internal static int Checks;
    internal static int MaxRequest, MaxProspectiveRequest, MaxWireRequest, MaxContext, MaxResult, MaxSchema;
    internal const string RejectedCanary = "REJECTED_ARGUMENT_CANARY_399";
    internal const string InstructionCanary = "UNTRUSTED_REPO_INSTRUCTION_399";
    internal const string EmptyFinal = "{\"summary\":\"No supported findings.\",\"findings\":[]}";
    internal static void Check(bool value) { Checks++; if (!value) throw new InvalidOperationException(); }
    internal static void Reject(Action action)
    {
        try { action(); } catch (ArgumentException) { Checks++; return; }
        throw new InvalidOperationException();
    }
    internal static string Json(string text) => JsonSerializer.Serialize(text);
    internal static void ObserveRequest(ProviderRequest request)
    {
        MaxRequest = Math.Max(MaxRequest, request.PayloadByteCount);
        if (request.Inputs.Any(i => i.Kind == ProviderInputKind.ToolCorrectionFeedbackData))
            MaxProspectiveRequest = Math.Max(MaxProspectiveRequest, request.PayloadByteCount);
    }
    internal static string Grounded(string id, string path = "changed.cs", int first = 1, int last = 1) =>
        "{\"summary\":\"A supported defect.\",\"findings\":[{\"severity\":\"high\",\"title\":\"Missing guard\",\"message\":\"Validate before use.\",\"evidence\":[{\"observation_id\":" +
        Json(id) + ",\"path\":" + Json(path) + ",\"start_line\":" + first + ",\"end_line\":" + last + "}]}]}";
    internal static RuntimeConfiguration Configuration(SolusSnapshotTools tools, IModelProvider provider,
        IRuntimeContextAuthority? authority = null, ToolArgumentCorrectionPolicy? policy = null,
        IReadOnlyList<RuntimeToolRegistration>? registrations = null) =>
        new(provider, registrations ?? tools.Registrations, new Hooks(),
            new(Cr1DevelopmentProfile.ProviderInputs, Cr1DevelopmentProfile.NonterminalDefinitions,
                Cr1DevelopmentProfile.ToolCallsPerBatch, Cr1DevelopmentProfile.RequestPayloadBytes,
                Cr1DevelopmentProfile.ResponsePayloadBytes, Cr1DevelopmentProfile.ContinuationBytes),
            contextAuthority: authority, toolArgumentCorrection: policy ?? SolusToolCorrection.Policy);
    internal static IContextCandidateAgent Agent(RuntimeConfiguration configuration) =>
        (IContextCandidateAgent)SolusRuntimeConstruction.Create(configuration, Cr1DevelopmentProfile.Id);
    internal static AgentRequest Request(Guid execution, int calls = 6, int admissions = 6) =>
        new(execution, "Inspect classified snapshot data and return one structured Final.", [],
            new(calls, TimeSpan.FromSeconds(Cr1DevelopmentProfile.DurationSeconds)),
            AgentCapability.WorkUnitLimit | AgentCapability.DurationLimit | AgentCapability.Cancellation |
            AgentCapability.DispatchLimits | AgentCapability.ToolInvocationLimit | AgentCapability.ToolCallAdmissionLimit,
            new(maximumLogicalCalls: calls, maximumPhysicalDispatches: calls, maximumToolInvocations: 6,
                maximumToolCallAdmissions: admissions));
    internal static ProviderResponse Response(ProviderRequest request, ToolCall[] calls, string? text = null) =>
        new(request.Scope, request.Attempt, calls.Length == 0 ? ProviderFinish.Final : ProviderFinish.ToolCalls,
            text ?? (calls.Length == 0 ? EmptyFinal : null), calls,
            new(request.Scope, request.Attempt, "synthetic-replay"u8.ToArray()));
}

internal sealed class SnapshotFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "apr-tools-" + Guid.NewGuid().ToString("N"));
    internal ReviewedSnapshot Snapshot { get; }
    internal SnapshotFixture()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(Path.Combine(Root, "changed.cs"), "guard missing\n" + Proof.InstructionCanary + "\nlast line\n");
        File.WriteAllText(Path.Combine(Root, "unchanged.cs"), "safe\n");
        File.WriteAllText(Path.Combine(Root, "large.cs"), new string('x', 5000) + "\n");
        File.WriteAllBytes(Path.Combine(Root, "invalid.cs"), [0xff, 0xfe]);
        File.WriteAllBytes(Path.Combine(Root, "binary.cs"), [0, 1]);
        File.WriteAllText(Path.Combine(Root, "lone-cr.cs"), "one\rtwo");
        File.WriteAllText(Path.Combine(Root, "oversize.cs"), new string('x', AgentLimits.ReadFileRawBytes + 1));
        File.CreateSymbolicLink(Path.Combine(Root, "link.cs"), "/etc/passwd");
        var identity = new ReviewedIdentity("synthetic-repo", 399, new string('a', 40), new string('b', 40));
        var source = new ReviewedDiffSource(identity, "changed.cs", null, "modified", false,
            [new(1, 1, 1, 2, [new("context", 1, 1, "guard missing"), new("addition", null, 2, "new line")])]);
        Snapshot = new(identity, Root, ["changed.cs", "unchanged.cs", "large.cs", "invalid.cs", "binary.cs",
            "lone-cr.cs", "oversize.cs", "link.cs"],
            [new("changed.cs", null, "modified", 1, 0, 1, "available", source.PatchSha256, false)], [source]);
    }
    internal (SolusSnapshotTools Tools, CountingExecutor Executor) Tools(Guid execution, Func<AgentToolExecution, AgentToolExecution>? fault = null)
    {
        var executor = new CountingExecutor(new SnapshotToolExecutor(Snapshot, new VerifiedReviewedFileAccess()), fault);
        return (new(execution, Snapshot, executor), executor);
    }
    public void Dispose() => Directory.Delete(Root, true);
}

internal sealed class CountingExecutor(IAgentToolExecutor inner, Func<AgentToolExecution, AgentToolExecution>? fault = null) : IAgentToolExecutor
{
    internal int Invocations;
    internal Func<PreparedAgentToolCall, string?>? PreflightFault;
    public string? Preflight(PreparedAgentToolCall call) => PreflightFault?.Invoke(call) ?? inner.Preflight(call);
    public async ValueTask<AgentToolExecution> ExecuteAsync(PreparedAgentToolCall call, CancellationToken token)
    { Interlocked.Increment(ref Invocations); var result = await inner.ExecuteAsync(call, token); return fault?.Invoke(result) ?? result; }
}

internal sealed class Hooks : IRuntimeExposureHooks
{
    public ValueTask<ExposureAcknowledgement?> BeforeDispatchAsync(RuntimeExposure exposure, CancellationToken token) =>
        ValueTask.FromResult<ExposureAcknowledgement?>(new(exposure, RuntimeHookStatus.Acknowledged, ExposureDecision.Permit, exposure.RequiredAcknowledgement));
    public ValueTask<SettlementAcknowledgement?> AfterAttemptAsync(RuntimeSettlement settlement, CancellationToken token) =>
        ValueTask.FromResult<SettlementAcknowledgement?>(new(settlement.Exposure, RuntimeHookStatus.Acknowledged, RuntimeContinuation.Continue, settlement.Accounting));
}

internal sealed class Provider(Func<ProviderRequest, int, ProviderResponse> reply) : ModelProvider(new("r3-controlled", "synthetic"),
    ProviderCapabilities.Continuation | ProviderCapabilities.UsageReporting | ProviderCapabilities.ToolCalls | ProviderCapabilities.ToolArgumentCorrection), IProviderContextPersistence
{
    internal int Calls;
    internal int Validations;
    internal int Imports;
    internal bool RefuseCorrection;
    internal readonly List<ProviderRequest> Requests = [];
    protected override ProviderError ValidateRequestCore(ProviderRequest request)
    { Validations++; Proof.ObserveRequest(request); return RefuseCorrection && request.Inputs.Any(x => x.Kind == ProviderInputKind.ToolCorrectionFeedbackData) ? ProviderError.LimitExceeded : ProviderError.None; }
    protected override ValueTask<ProviderResponse> ExchangeCoreAsync(ProviderRequest request, ProviderObservation observation, CancellationToken token)
    { token.ThrowIfCancellationRequested(); Requests.Add(request); observation.ObserveDispatch(DispatchExposure.Dispatched); return ValueTask.FromResult(reply(request, ++Calls)); }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => new(binding.Scope, binding.Origin, 1, [1, 9, 3]);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
    { Imports++; return state.FormatVersion == 1 && state.CopyRestrictedPayload().AsSpan().SequenceEqual(new byte[] { 1, 9, 3 }); }
}

internal sealed class Sink : IRestrictedContextSink
{
    internal AgentContextEnvelope? Context;
    public void Capture(AgentContextEnvelope context)
    { Proof.Check(Context is null); Context = context; Proof.MaxContext = Math.Max(Proof.MaxContext, context.PayloadByteCount); }
}

// Synthetic exact source/target authority only, not the product S2 claim service.
internal sealed class Authority(AgentContextEnvelope original, ContextRoundGrant expected, bool scopeMatches = true) : IRuntimeContextAuthority
{
    internal int Claims;
    public bool TryClaim(AgentContextEnvelope context, ContextRoundGrant grant)
    {
        Claims++;
        return scopeMatches && Claims == 1 && ReferenceEquals(grant, expected) &&
            context.ImplementationId == original.ImplementationId && context.FormatVersion == original.FormatVersion &&
            context.CompatibilityVersion == original.CompatibilityVersion &&
            context.CopyRestrictedPayload().AsSpan().SequenceEqual(original.CopyRestrictedPayload());
    }
}
