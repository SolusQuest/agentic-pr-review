using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using SolusAgent.Providers.DeepSeek;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.Execution.Solus;

// No Host/environment configuration reaches the actual provider. The optional
// terminal handler is a trusted test capability, never repository/model input.
internal sealed class SolusDeepSeekAdapter : IModelProvider, IProviderContextPersistence, IProviderRequestValidation, IDisposable
{
    private readonly DeepSeekProvider provider;
    private readonly SolusReviewFacts facts;

    internal SolusDeepSeekAdapter(DeepSeekCredential credential, SolusReviewFacts facts,
        HttpMessageHandler? controlledTransport = null)
    {
        this.facts = facts;
        var options = new DeepSeekOptions(credential.Value, Cr1DevelopmentProfile.ProviderOutputTokens,
            endpoint: new(DeepSeekOptions.OfficialEndpoint),
            maximumRequestBodyBytes: Cr1DevelopmentProfile.WireRequestBytes,
            maximumResponseBodyBytes: Cr1DevelopmentProfile.WireResponseBytes,
            timeout: TimeSpan.FromSeconds(Cr1DevelopmentProfile.ProviderTimeoutSeconds));
        provider = controlledTransport is null ? new(options) : new(options, controlledTransport);
    }

    public ProviderScope Scope => provider.Scope;
    public ProviderCapabilities Capabilities => provider.Capabilities;

    public async ValueTask<ProviderExchangeResult> ExchangeAsync(ProviderRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await provider.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        result.ValidateFor(request);
        facts.ObserveFinish(result.Observation, result.Outcome == ProviderOutcome.Succeeded
            ? result.Response?.Finish : null);
        return result;
    }

    public ProviderError ValidateRequest(ProviderRequest request) => provider.ValidateRequest(request);

    public ProviderSavedState? ExportContext(ProviderContextBinding binding) => provider.ExportContext(binding);
    public bool AdmitContext(ProviderContextBinding binding, ProviderSavedState state) => provider.AdmitContext(binding, state);
    public void Dispose() => provider.Dispose();
    public override string ToString() => nameof(SolusDeepSeekAdapter);
}
