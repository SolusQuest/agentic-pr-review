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
        TrustedProofGitHubRequestBudget githubBudget,
        TrustedProofPayloadRuntimePorts ports,
        ITrustedProofStaleSignal? staleSignal = null)
    {
        ArgumentNullException.ThrowIfNull(githubBudget);
        ArgumentNullException.ThrowIfNull(ports);
        var github = new ActionHostGitHubAuthorizationTransportFactory(
            githubBudget.CreateOtherGitHubHandler);
        // This capability is passed only to ReviewedTreeReader.  It labels
        // the authenticated commit/tree/tarball acquisition of the frozen
        // reviewed head; policy, base-diff, revalidation, and snapshot outer
        // routes remain on the ordinary GitHub capability above.
        var reviewedHeadSource = new ActionHostGitHubAuthorizationTransportFactory(
            githubBudget.CreateHeadSourceHandler);
        var publisher = new BoundedGitHubPublisherTransportFactory(
            githubBudget.CreateHandler);
        var provider = new ActionHostDeepSeekProviderRunnerFactory(
            credential =>
            {
                HttpMessageHandler deterministic =
                    new TrustedProofDeterministicDeepSeekHandler(
                        credential.Value,
                        staleSignal);
                return DeepSeekTransport.CreateForTesting(
                    credential,
                    ports.WrapProviderHandler(deterministic),
                    TimeSpan.FromSeconds(30));
            });
        return new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            ports.CreateStateDependencies(github),
            publisher,
            provider,
            ports.TimeProvider,
            ports.StagingParentFactory,
            new PostAcceptanceInlinePublisherHook(publisher),
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance,
            reviewedHeadSourceFactory: reviewedHeadSource,
            changedFileSourceFactory:
                TrustedProofChangedFileSourceFactory.Instance);
    }
}
