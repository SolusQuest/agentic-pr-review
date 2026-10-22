using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

public sealed partial class ActionHostCompositionTests
{
    // Real SDK/store/Host commit with structural Solus capture. This protects
    // conditional-read budget behavior; original archive qualification is separate.
    [LinuxSdkTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConditionalArtifactRevalidationPreservesAllocationThroughStateCommit(bool conditional)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch);
        await using var fixture = await HybridArtifactFixture.StartAsync(FullLaunch(scenario.Launch), LocatorTestData.Now);
        var launch = fixture.Launch;
        var github = new FullPathGitHubFactory(scenario.Transport.PullRequest);
        var production = new AcceptedStateProductionDependencies(github, () => new ConditionalArtifactHandler(fixture.Handlers(), conditional), artifactClock: fixture.Clock);
        var owner = new BudgetObservedStoreOwner(production);
        var publisher = SuccessfulPublisher(790);
        var nativeProvider = new FullPathProviderFactory();
        var compatibility = new SolusRestrictedStateCompatibility(SolusRestrictedStateFormat.PackageVersion,
            SolusRestrictedStateFormat.Profile, 1, SolusRestrictedStateFormat.Implementation,
            SolusRestrictedStateFormat.ContextFormat, SolusRestrictedStateFormat.ContextCompatibility,
            SolusRestrictedStateFormat.PackageClosureSha256, new string('1', 64), new string('2', 64));
        var executionId = Guid.NewGuid();
        var checkpoint = new SolusRestrictedStateCheckpoint(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), executionId);
        byte[] restricted = [0, 255, 128, 42, 7];
        Assert.True(SolusRestrictedStateRecord.TryCreate(compatibility, checkpoint, restricted, out var captured));
        ActionHostComposition NewHost() => new(new ActionHostCompositionDependencies(
            scenario.EventReader, scenario.Factory, github, github, owner,
            publisher, nativeProvider, new FrozenLocatorTimeProvider(LocatorTestData.Now), StagingPath));
        var host = NewHost();
        var first = await host.RunPreparedFreshAsync(launch, async prepared =>
        {
            Assert.NotNull(prepared.FreshContext);
            var selected = prepared with { RestoreRequest = prepared.RestoreRequest with {
                SolusTransaction = new(prepared.FreshContext!, compatibility, executionId) } };
            return await host.RunStateCommitPreparedAsync(selected, async (state, scope, _) =>
            {
                Assert.True(RestrictedStateService.CanPrepareFreshSolus(state));
                var terminal = new AgentTerminalReview("Review complete", [], new string('d', 64), [1]);
                Assert.True(R4ValidatedPublicationReview.TryCreateProjection(terminal,
                    prepared.Snapshot.Snapshot.Identity, scope, out var review));
                var result = await RestrictedStateService.PrepareRetainedSolusCandidateAsync(state, captured!, review!, default);
                Assert.True(result.Succeeded, result.Code);
                Assert.False(result.Value!.IsRecovered);
                Assert.Null(result.Value.Run);
                return new(null, result);
            });
        }, default);
        var actual = owner.Store!;
        Assert.Null(first.Failure);
        Assert.Equal(conditional ? ActionHostStatus.Reviewed : ActionHostStatus.OutcomeAmbiguous, first.Value!.Status);
        Assert.Single(publisher.Transport.Bodies);
        Assert.Equal("None/Committed", owner.LastUpload);
        Assert.Equal("None", owner.LastReadback);
        if (conditional)
        {
            Assert.Equal(ActionHostStateDisposition.Accepted, first.Value.Summary.StateDisposition);
            Assert.Null(owner.FirstFailedList);
            Assert.Equal("active", actual.Rest.Budget.Disposition);
            Assert.True(actual.Rest.Budget.NotModified > 0);
            Assert.True(actual.Rest.Budget.Primary < actual.Rest.Budget.Requests);
        }
        else
        {
            // A committed upload and matching direct readback do not establish
            // acceptance when the shared budget refuses the complete census.
            Assert.NotNull(owner.FirstFailedList);
            Assert.Equal(OpaqueStoreFailure.Io, owner.FirstFailedList.Failure);
            Assert.Equal("primary_exhausted", owner.FirstFailedList.Disposition);
            Assert.Equal(owner.FirstFailedList.Requests, actual.Rest.Budget.Requests);
            Assert.True(owner.FailedLists > 1);
            Assert.Equal(0, actual.Rest.Budget.NotModified);
        }
    }
    private sealed record ListFailureObservation(OpaqueStoreFailure Failure, int Requests, string Disposition);

    private sealed class BudgetObservedStoreOwner(AcceptedStateProductionDependencies inner) : IAcceptedStateProductionDependencies
    {
        internal GitHubArtifactRestrictedStateStore? Store;
        internal ListFailureObservation? FirstFailedList;
        internal int FailedLists;
        internal string? LastUpload, LastReadback;
        public IRestrictedStateStore CreateArtifactStore(ActionHostLaunchContract launch) {
            Store = (GitHubArtifactRestrictedStateStore)inner.CreateArtifactStore(launch);
            return new BudgetObservedStore(Store, this);
        }
        public IActionHostGitObjectTransport CreateAncestryTransport(ActionHostGitHubToken token) => inner.CreateAncestryTransport(token);
    }
    private sealed class BudgetObservedStore(GitHubArtifactRestrictedStateStore actual, BudgetObservedStoreOwner owner) : IRestrictedStateStore, IDisposable
    {
        public async Task<OpaqueStoreListResult> ListExactAsync(OpaqueStoreListRequest request, CancellationToken token) {
            var result = await actual.ListExactAsync(request, token);
            if (!result.Succeeded) {
                owner.FailedLists++;
                owner.FirstFailedList ??= new(result.Failure, actual.Rest.Budget.Requests, actual.Rest.Budget.Disposition);
            }
            return result;
        }
        public async Task<OpaqueStoreUploadResult> UploadImmutableAsync(OpaqueStoreUploadRequest request, CancellationToken token) {
            var result = await actual.UploadImmutableAsync(request, token);
            owner.LastUpload = result.Failure + "/" + result.MutationState;
            return result;
        }
        public async Task<OpaqueStoreReadBackResult> ReadBackExactAsync(OpaqueStoreReadBackRequest request, CancellationToken token) {
            var result = await actual.ReadBackExactAsync(request, token);
            owner.LastReadback = result.Failure.ToString();
            return result;
        }
        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(OpaqueStoreMetadataRequest request, CancellationToken token) => actual.ReadMetadataAsync(request, token);
        public Task<OpaqueStoreDownloadResult> DownloadAsync(OpaqueStoreDownloadRequest request, CancellationToken token) => actual.DownloadAsync(request, token);
        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(OpaqueStoreDeleteRequest request, CancellationToken token) => actual.DeleteExactAsync(request, token);
        public void Dispose() => actual.Dispose();
    }
    private sealed class ConditionalArtifactHandler(HttpMessageHandler inner, bool conditional) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker client = new(inner);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await client.SendAsync(request, token);
            response.Headers.ETag = null;
            if (request.RequestUri!.AbsolutePath.Contains("/attempts/")) {
                response.Headers.Add("x-ratelimit-limit", "1000");
                response.Headers.Add("x-ratelimit-remaining", "950");
                response.Headers.Add("x-ratelimit-reset", "4102444800");
            }
            if (conditional && request.RequestUri.Host == "api.github.com" && request.Method == HttpMethod.Get && response.StatusCode == System.Net.HttpStatusCode.OK) {
                var bytes = await response.Content.ReadAsByteArrayAsync(token);
                var tag = "\"" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)) + "\"";
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(tag);
                if (request.Headers.IfNoneMatch.Any(x => x.Tag == tag)) {
                    response.StatusCode = System.Net.HttpStatusCode.NotModified;
                    response.Content.Dispose();
                    response.Content = new ByteArrayContent([]);
                }
            }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) client.Dispose(); base.Dispose(disposing); }
    }
}
