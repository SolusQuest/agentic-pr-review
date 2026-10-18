using System.Text;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusFreshHostProof;

internal sealed class CapacityMeter(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    internal readonly List<object> Exchanges = [];
    internal readonly List<object> AdmissionChecks = [];
    internal int Imports;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
    {
        Proof.Require(request.Bounds.MaximumResponseBytes == 16384, "full_response_bound_never_shrinks");
        var results = request.Inputs.Where(i => i.ToolResult is not null).Select(i => i.ToolResult!).ToArray();
        var observed = new Dictionary<string, object?> {
            ["inputs"] = request.Inputs.Count, ["logical_request_bytes"] = request.PayloadByteCount,
            ["logical_response_bytes"] = null, ["response_allowance"] = request.Bounds.MaximumResponseBytes,
            ["classifications"] = request.Inputs.GroupBy(i => i.Kind.ToString()).ToDictionary(g => g.Key, g => g.Count()),
            ["tool_results"] = results.Length, ["historical_tool_results"] = results.Count(r => r.IsHistorical),
            ["tool_result_bytes"] = results.Sum(r => Encoding.UTF8.GetByteCount(r.Json ?? "")),
            ["largest_tool_result_bytes"] = results.Length == 0 ? 0 : results.Max(r => Encoding.UTF8.GetByteCount(r.Json ?? "")),
            ["proposed_calls"] = null, ["correction_members"] = null };
        Exchanges.Add(observed);
        var response = await inner.ExchangeAsync(request, token);
        observed["logical_response_bytes"] = response.Response?.PayloadByteCount;
        observed["proposed_calls"] = response.Response?.Calls.Count;
        observed["correction_members"] = response.Response?.Correction?.Members.Count ?? 0;
        return response;
    }
    public ProviderError ValidateRequest(ProviderRequest request)
    {
        var result = ((IProviderRequestValidation)inner).ValidateRequest(request);
        AdmissionChecks.Add(new { inputs = request.Inputs.Count, logical_request_bytes = request.PayloadByteCount,
            response_allowance = request.Bounds.MaximumResponseBytes, result = result.ToString() });
        return result;
    }
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state)
    { Imports++; return ((IProviderContextPersistence)inner).AdmitContext(binding, state); }
}
