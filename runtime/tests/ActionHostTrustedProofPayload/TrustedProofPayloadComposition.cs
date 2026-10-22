using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.ActionHostTrustedProofPayload;

internal static class TrustedProofPayloadComposition
{
    internal static ActionHostCompositionDependencies CreateProductionLike(
        TrustedProofGitHubRequestBudget githubBudget,
        TrustedProofPayloadRuntimePorts ports,
        ITrustedProofStaleSignal? staleSignal = null,
        TrustedProofRequestBudgetProfile? requestBudgetProfile = null)
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
        var provider = new CurrentHostAuthoredRunnerFactory(ports, staleSignal);
        var artifactPolicy = ArtifactRestBudgetPolicy.Protected(
            (requestBudgetProfile ?? TrustedProofRequestBudgetProfile.Measurement).MeasurementOnly);
        var stateDependencies = ports.CreateStateDependencies(github);
        if (stateDependencies is AcceptedStateProductionDependencies production)
            stateDependencies = production.WithArtifactPolicy(artifactPolicy);
        return new ActionHostCompositionDependencies(
            new ActionHostExactPathEventReader(),
            github,
            github,
            github,
            stateDependencies,
            publisher,
            provider,
            ports.TimeProvider,
            ports.StagingParentFactory,
            new PostAcceptanceInlinePublisherHook(publisher),
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance,
            reviewedHeadSourceFactory: reviewedHeadSource,
            changedFileSourceFactory:
                TrustedProofChangedFileSourceFactory.Instance,
            stateReconciliationDiagnosticSink:
                StateReconciliationStderrSink.Instance,
            artifactBudgetPolicy: artifactPolicy);
    }
}
