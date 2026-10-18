using AgenticPrReview.Runtime.SolusFreshHostProof;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

// Transparent on every production-owned authority/persistence/validation seam.
// A closed snapshot cannot change when a late delegated call eventually returns.
internal sealed class ObservedProvider(IModelProvider inner, WireObservation wire, Action? afterExchange = null) : IModelProvider,
    IProviderContextPersistence, IProviderRequestValidation
{
    private readonly object gate = new();
    private readonly List<ExchangeFact> exchanges = [];
    private bool frozen;
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default)
    {
        lock (gate)
        {
            Proof.Require(!frozen && wire.Active is null && exchanges.Count < 6, "v2_observer_single_attempt");
            wire.Active = request.Attempt;
        }
        try
        {
            var result = await inner.ExchangeAsync(request, token);
            var observation = result.Observation;
            Proof.Require(observation.ExecutionId == request.Attempt.ExecutionId && observation.LogicalCallId == request.Attempt.LogicalCallId &&
                observation.PhysicalAttemptId == request.Attempt.PhysicalAttemptId && observation.AttemptNumber == request.Attempt.AttemptNumber,
                "v2_observer_actual_association");
            lock (gate)
            {
                if (!frozen)
                {
                    exchanges.Add(new(AttemptFact.From(observation), result.Outcome, result.Error));
                    if (result.Response is { Finish: ProviderFinish.Final } response)
                        Proof.Require(wire.ReturnedFinal is { } final && response.Text is not null &&
                            Evidence.Hash("content", response.Text) == final.ContentSha256 && response.Continuation is not null &&
                            response.Calls.Count == final.Calls, "v2_normalized_final_association");
                }
            }
            afterExchange?.Invoke();
            return result;
        }
        finally { lock (gate) wire.Active = null; }
    }
    internal ExchangeFact[] Freeze() { lock (gate) { frozen = true; return exchanges.ToArray(); } }
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => ((IProviderContextPersistence)inner).AdmitContext(binding, state);
}

internal sealed class RejectedImport(IModelProvider inner) : IModelProvider, IProviderContextPersistence, IProviderRequestValidation
{
    public ProviderScope Scope => inner.Scope;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request, CancellationToken token = default) => inner.ExchangeAsync(request, token);
    public ProviderError ValidateRequest(ProviderRequest request) => ((IProviderRequestValidation)inner).ValidateRequest(request);
    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => ((IProviderContextPersistence)inner).ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => false;
}
