using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.Solus;
using SolusAgent.Runtime.Api.Providers;
using SolusAgent.Tools.Api;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

// Observes the actual trusted invocation. Scripts, preflight reads and restored
// ToolResult data cannot populate the current evidence inventory.
internal sealed class SolusWitness(IModelProvider inner, ReviewedIdentity identity) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    private readonly Dictionary<string, ToolResult> current = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> attempts = [];
    internal Guid ExecutionId { get; private set; }
    internal ReviewedIdentity Identity { get; } = identity;
    internal string? ConfigurationFingerprint { get; private set; }
    internal string? Final { get; private set; }
    internal int Exchanges => attempts.Count;
    internal int MaxInputs { get; private set; }
    internal int MaxRequestBytes { get; private set; }
    internal int MaxResponseBytes { get; private set; }
    internal int HistoricalResults { get; private set; }
    internal int CurrentSuccessfulResults => current.Values.Count(r => r.Outcome == ToolOutcome.Succeeded);
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);

    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
    {
        if (ExecutionId == Guid.Empty) ExecutionId = request.Attempt.ExecutionId;
        Proof.Require(ExecutionId == request.Attempt.ExecutionId && attempts.Add(request.Attempt.PhysicalAttemptId),
            "witness_current_attempt_association");
        var configuration = Configuration(request);
        ConfigurationFingerprint ??= configuration;
        Proof.Require(ConfigurationFingerprint == configuration, "witness_stable_configuration");
        MaxInputs = Math.Max(MaxInputs, request.Inputs.Count);
        MaxRequestBytes = Math.Max(MaxRequestBytes, request.PayloadByteCount);
        var currentCalls = request.Inputs.Where(i => i.Model?.Attempt.ExecutionId == ExecutionId)
            .SelectMany(i => i.Model!.Calls).ToDictionary(c => c.CallId, StringComparer.Ordinal);
        HistoricalResults = Math.Max(HistoricalResults, request.Inputs.Count(i => i.ToolResult?.IsHistorical == true));
        foreach (var input in request.Inputs)
        {
            if (input.ToolResult is not { IsHistorical: false } result) continue;
            Proof.Require(currentCalls.TryGetValue(result.Call.CallId, out var call) && call.Matches(result.Call),
                "witness_current_model_tool_association");
            if (current.TryGetValue(result.Call.CallId, out var previous))
                Proof.Require(previous.Call.Matches(result.Call) && previous.Outcome == result.Outcome &&
                    previous.Error == result.Error && previous.InvocationStarted == result.InvocationStarted &&
                    previous.Json == result.Json, "witness_repeated_projection_unchanged");
            else current.Add(result.Call.CallId, result);
        }
        var exchange = await inner.ExchangeAsync(request, token);
        MaxResponseBytes = Math.Max(MaxResponseBytes, exchange.Response?.PayloadByteCount ?? 0);
        if (exchange is { Outcome: ProviderOutcome.Succeeded, Response.Finish: ProviderFinish.Final })
        {
            Proof.Require(Final is null && exchange.Response.Attempt.ExecutionId == ExecutionId,
                "witness_one_current_final");
            Final = exchange.Response.Text;
        }
        return exchange;
    }

    internal bool TryProject(ReviewedIdentity identity, ImmutableArray<AgentObservation> observations,
        out ImmutableArray<EvaluationObservation> projected)
    {
        projected = [];
        if (observations.IsDefault || observations.Any(o => o.Identity != identity) ||
            observations.Length != CurrentSuccessfulResults) return false;
        var remaining = Enumerable.Range(0, observations.Length).ToHashSet();
        var values = new List<(int Ordinal, EvaluationObservation Value)>();
        foreach (var result in current.Values.Where(r => r.Outcome == ToolOutcome.Succeeded))
        {
            if (result.IsHistorical || !result.InvocationStarted || result.Error != ToolError.None || result.Json is null ||
                !SolusToolCorrection.TryParse(result.Call, out var call, out _)) return false;
            using var document = JsonDocument.Parse(result.Json);
            var value = document.RootElement;
            var id = value.GetProperty("observation_id").GetString();
            var index = remaining.FirstOrDefault(i => observations[i].ObservationId == id, -1);
            if (index < 0) return false;
            var observation = observations[index];
            var execution = new AgentToolExecution(true, null, result.Json, Encoding.UTF8.GetBytes(result.Json), observation);
            if (!AgentToolResultAdmission.TryAdmit(call!, identity, execution, out _, out _)) return false;
            var completeDiff = call!.Name == AgentToolRegistry.ReadDiffName &&
                value.GetProperty("status").GetString() == "ok" &&
                !value.GetProperty("source_truncated").GetBoolean() && !value.GetProperty("truncated").GetBoolean() &&
                value.GetProperty("requested_start_hunk").GetInt32() == 1 && value.GetProperty("returned_start_hunk").GetInt32() == 1;
            values.Add((index, new(call.Name, observation, completeDiff)));
            remaining.Remove(index);
        }
        if (remaining.Count != 0) return false;
        projected = values.OrderBy(v => v.Ordinal).Select(v => v.Value).ToImmutableArray();
        return true;
    }

    private static string Configuration(ProviderRequest request)
    {
        var fields = new List<string> { request.Scope.Provider, request.Scope.Model,
            ((int)request.RequiredCapabilities).ToString(System.Globalization.CultureInfo.InvariantCulture), request.Bounds.MaximumInputs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Bounds.MaximumTools.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Bounds.MaximumToolCalls.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Bounds.MaximumRequestBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Bounds.MaximumResponseBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.Bounds.MaximumContinuationBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), request.Bounds.MaximumTextBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cr1DevelopmentProfile.LogicalCalls.ToString(System.Globalization.CultureInfo.InvariantCulture), Cr1DevelopmentProfile.PhysicalAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cr1DevelopmentProfile.ToolInvocations.ToString(System.Globalization.CultureInfo.InvariantCulture), Cr1DevelopmentProfile.RetainedAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cr1DevelopmentProfile.RetainedRecords.ToString(System.Globalization.CultureInfo.InvariantCulture), Cr1DevelopmentProfile.RetainedBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Cr1DevelopmentProfile.ProviderOutputTokens.ToString(System.Globalization.CultureInfo.InvariantCulture), Cr1DevelopmentProfile.ProviderTimeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            request.ToolArgumentCorrection?.Identity ?? "none", request.ToolArgumentCorrection?.SemanticFingerprint ?? "none" };
        fields.AddRange(request.Inputs.Where(i => i.Kind == ProviderInputKind.HostInstruction)
            .Select(i => AgentCanonical.HashRaw(Encoding.UTF8.GetBytes(i.Text!))));
        foreach (var tool in request.Tools)
            fields.AddRange([tool.Name, tool.Description, tool.CapabilityId, tool.InputSchema.NormalizedJson,
                tool.ResultSchema.NormalizedJson, ((int)tool.Effect).ToString(System.Globalization.CultureInfo.InvariantCulture), tool.MaximumArgumentBytes.ToString(System.Globalization.CultureInfo.InvariantCulture), tool.MaximumResultBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        // Attempt IDs, input/history/continuation, source revision and package
        // provenance are intentionally separate from configuration cohorts.
        return EvaluationAttempt.Hash("solus-request-configuration", fields.ToArray());
    }
    public override string ToString() => nameof(SolusWitness);
}
