using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.ActionHost;

// Production review is supplied by the selected Solus candidate preparer. A
// missing preparer must fail closed, never select the retired APR-owned loop.
internal sealed class ActionHostUnavailableProviderRunnerFactory : IActionHostProviderRunnerFactory
{
    internal static ActionHostUnavailableProviderRunnerFactory Instance { get; } = new();

    private ActionHostUnavailableProviderRunnerFactory() { }

    public IActionHostProviderRunner Create(ActionHostProviderPolicy policy,
        ActionHostProviderApiKey key, ReviewedSnapshot snapshot, TimeProvider timeProvider) =>
        throw new InvalidOperationException("APR_SOLUS_COMPOSITION_REQUIRED");
}
