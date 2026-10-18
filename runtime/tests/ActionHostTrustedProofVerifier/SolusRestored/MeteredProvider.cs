using SolusAgent.Runtime.Api.Providers;
namespace AgenticPrReview.Runtime.SolusFreshHostProof;
internal sealed class MeteredProvider(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    internal int MaxRequestBytes, MaxResponseBytes, MaxInputs;
    internal Guid? ExecutionId;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken cancellationToken = default)
    {
        ExecutionId ??= request.Attempt.ExecutionId;
        Proof.Require(ExecutionId == request.Attempt.ExecutionId, "provider_execution_witness");
        MaxRequestBytes = Math.Max(MaxRequestBytes, request.PayloadByteCount);
        MaxInputs = Math.Max(MaxInputs, request.Inputs.Count);
        var result = await inner.ExchangeAsync(request, cancellationToken);
        MaxResponseBytes = Math.Max(MaxResponseBytes, result.Response?.PayloadByteCount ?? 0);
        return result;
    }
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}
