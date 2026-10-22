using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed class LinuxArtifactHostFactAttribute : FactAttribute
{
    public LinuxArtifactHostFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Actual Host invocation exclusion requires Linux; native REST conformance is separate.";
    }
}

public sealed partial class ActionHostCompositionTests
{
    [LinuxArtifactHostFact]
    public async Task HybridArtifactHostAllowsAcceptanceReceiptAfterVerifiedStickyBusinessCancellation()
    {
        var authorization = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        await using var fixture = await HybridArtifactFixture.StartAsync(FullLaunch(authorization.Launch), LocatorTestData.Now);
        var launch = fixture.Launch;
        var github = new FullPathGitHubFactory(authorization.Transport.PullRequest);
        using var cancellation = new CancellationTokenSource();
        var publisher = SuccessfulPublisher(44901);
        var provider = new FullPathProviderFactory();
        var readbackSeen = false;
        var sdkAtCancellation = -1;
        var httpAtCancellation = -1;
        publisher.Transport.OnRead = () => { if (publisher.Transport.Bodies.Count == 1) readbackSeen = true; };
        var production = new AcceptedStateProductionDependencies(github, fixture.Handlers, artifactClock: fixture.Clock);
        var dependencies = new HybridCancellationDependencies(production, token =>
        {
            if (!readbackSeen || cancellation.IsCancellationRequested) return;
            // The next state write belongs to CompleteAcceptanceAsync, which
            // requires evaluation.ExactReadbackReceipt before admitting it.
            Assert.True(publisher.Transport.Reads > 0);
            Assert.Single(publisher.Transport.Bodies);
            sdkAtCancellation = fixture.Count("sdk");
            httpAtCancellation = fixture.Requests.Count;
            cancellation.Cancel();
            Assert.False(token.IsCancellationRequested); // independent reconciliation token
        });
        var staging = StagingPath();
        var completion = await new CurrentHostTestComposition(new ActionHostCompositionDependencies(
            authorization.EventReader, authorization.Factory, github, github, dependencies, publisher, provider,
            new FrozenLocatorTimeProvider(LocatorTestData.Now), () => staging)).RunAsync(launch, cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested, $"status={completion.Status}");
        Assert.Equal(ActionHostStateDisposition.Accepted, completion.Summary.StateDisposition);
        Assert.Equal(1, provider.Runs);
        Assert.Equal(1, provider.Creates);
        Assert.Single(publisher.Transport.Bodies);
        Assert.True(sdkAtCancellation >= 0 && fixture.Count("sdk") > sdkAtCancellation);
        Assert.Contains(fixture.Requests.Skip(httpAtCancellation), request => request.Host.EndsWith(".blob.core.windows.net", StringComparison.Ordinal));
        Assert.Equal(0, fixture.Count("node_non_upload"));
        Assert.Equal(1, dependencies.Creates);
        Assert.False(Directory.Exists(staging));
    }

    private sealed class HybridCancellationDependencies(AcceptedStateProductionDependencies inner, System.Action<CancellationToken> beforeUpload)
        : IAcceptedStateProductionDependencies
    {
        internal int Creates;
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch)
        {
            Creates++;
            var actual = inner.CreateArtifactStore(launch);
            Assert.IsType<GitHubArtifactRestrictedStateStore>(actual);
            return new HybridCancellationStore(actual, beforeUpload);
        }
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => inner.CreateAncestryTransport(token);
    }

    private sealed class HybridCancellationStore(IRestrictedStateStore inner, System.Action<CancellationToken> beforeUpload)
        : IRestrictedStateStore, IDisposable
    {
        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token)
        { beforeUpload(token); return inner.UploadImmutableAsync(request, token); }
        public Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token) => inner.ListExactAsync(request, token);
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) => inner.ReadMetadataAsync(request, token);
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token) => inner.DownloadAsync(request, token);
        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) => inner.ReadBackExactAsync(request, token);
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => inner.DeleteExactAsync(request, token);
        public void Dispose() => ((IDisposable)inner).Dispose();
    }
}
