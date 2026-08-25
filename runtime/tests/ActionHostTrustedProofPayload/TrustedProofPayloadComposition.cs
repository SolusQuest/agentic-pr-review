using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal static class TrustedProofPayloadComposition
{
    internal static ActionHostCompositionDependencies CreateProductionLike(
        ITrustedProofStaleSignal? staleSignal = null)
    {
        var github = new ActionHostGitHubAuthorizationTransportFactory();
        var publisher = new BoundedGitHubPublisherTransportFactory();
        var provider = new ActionHostDeepSeekProviderRunnerFactory(
            credential => DeepSeekTransport.CreateForTesting(
                credential,
                new TrustedProofDeterministicDeepSeekHandler(
                    credential.Value,
                    staleSignal),
                TimeSpan.FromSeconds(30)));
        return new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            new AcceptedStateProductionDependencies(github),
            publisher,
            provider,
            TimeProvider.System,
            inlineHook: new PostAcceptanceInlinePublisherHook(publisher),
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance);
    }
}
