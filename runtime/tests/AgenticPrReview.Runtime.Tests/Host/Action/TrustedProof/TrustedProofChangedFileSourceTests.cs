using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.ActionHostTrustedProofPayload;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.TrustedProof;

public sealed class TrustedProofChangedFileSourceTests
{
    private static readonly byte[] CanaryBytes =
        "APR178_TOOL_DATA_CANARY\n"u8.ToArray();

    [Fact]
    public async Task HistoricalReportedBaseProducesOnlyTheExactCanary()
    {
        var invocation = await V2InvocationAsync();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await TreeAsync(invocation, parent, Canary());
        var transport = new ScriptedTransport([
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
        ]);
        try
        {
            var result = await new TrustedProofChangedFileSource(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);

            var changed = Assert.IsType<ReviewedChangedFileSet>(result.Value);
            var canary = Assert.Single(changed.Files);
            Assert.Equal(TrustedProofChangedFileSource.CanaryPath, canary.Path);
            Assert.Equal("added", canary.Status);
            Assert.True(changed.RequireAddedBaseAbsence);
            Assert.Equal(2, transport.PullRequestReads);
            Assert.Equal(0, transport.PullRequestFileReads);
            Assert.NotEqual(
                invocation.PullRequest.ReportedBaseSha,
                invocation.PullRequest.BaseSha);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ReportedBaseDriftAndWrongFirstParentFailClosed()
    {
        var invocation = await V2InvocationAsync();
        var drift = H5SnapshotTestSupport.PullRequest(
            invocation.PullRequest,
            baseSha: new string('e', 40));
        await AssertFailureAsync(
            invocation,
            [
                H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
                drift,
            ],
            [invocation.PullRequest.BaseSha, new string('d', 40)],
            Canary());
        await AssertFailureAsync(
            invocation,
            [H5SnapshotTestSupport.PullRequest(invocation.PullRequest)],
            [new string('6', 40), new string('d', 40)],
            Canary());
    }

    [Fact]
    public async Task MissingAlteredAndNonRegularCanariesFailClosed()
    {
        var invocation = await V2InvocationAsync();
        var current = H5SnapshotTestSupport.PullRequest(invocation.PullRequest);
        var parents = new[]
        {
            invocation.PullRequest.BaseSha,
            new string('d', 40),
        };
        await AssertFailureAsync(invocation, [current], parents);
        await AssertFailureAsync(
            invocation,
            [current],
            parents,
            Canary("ALTERED\n"u8.ToArray()));
        await AssertFailureAsync(
            invocation,
            [current],
            parents,
            Canary(CanaryBytes, mode: "100755"));
        await AssertFailureAsync(
            invocation,
            [current],
            parents,
            new H5HeadEntry(
                TrustedProofChangedFileSource.CanaryPath,
                "120000",
                ReviewedTreeEntryKind.Symlink,
                null,
                TrustedProofChangedFileSource.CanaryBlobSha));
        await AssertFailureAsync(
            invocation,
            [current],
            parents,
            new H5HeadEntry(
                TrustedProofChangedFileSource.CanaryPath,
                "160000",
                ReviewedTreeEntryKind.Submodule,
                null,
                TrustedProofChangedFileSource.CanaryBlobSha));
    }

    private static async Task AssertFailureAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        IReadOnlyList<ActionHostGitHubPullRequestFact> pullRequests,
        IReadOnlyList<string> parentShas,
        params H5HeadEntry[] entries)
    {
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var tree = await H5SnapshotTestSupport.TreeWithParentsAsync(
            invocation,
            parent,
            parentShas,
            entries);
        var transport = new ScriptedTransport(pullRequests);
        try
        {
            var result = await new TrustedProofChangedFileSource(
                    new ScriptedFactory(transport))
                .ReadAsync(
                    invocation,
                    H5SnapshotTestSupport.Token(),
                    tree,
                    CancellationToken.None);
            Assert.Null(result.Value);
            Assert.Equal(
                ReviewedSnapshotReadFailure.IdentityMismatch,
                result.Failure);
            Assert.Equal(0, transport.PullRequestFileReads);
        }
        finally
        {
            await tree.DisposeAsync();
            Directory.Delete(parent, recursive: true);
        }
    }

    private static Task<ReviewedTreeSnapshot> TreeAsync(
        ActionHostAuthorizer.AuthorizedInvocation invocation,
        string parent,
        params H5HeadEntry[] entries) =>
        H5SnapshotTestSupport.TreeWithParentsAsync(
            invocation,
            parent,
            [invocation.PullRequest.BaseSha, new string('d', 40)],
            entries);

    private static H5HeadEntry Canary(
        byte[]? bytes = null,
        string mode = TrustedProofChangedFileSource.CanaryMode) => new(
            TrustedProofChangedFileSource.CanaryPath,
            mode,
            ReviewedTreeEntryKind.Regular,
            bytes ?? CanaryBytes);

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        V2InvocationAsync()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var workflow = Encoding.UTF8.GetBytes(
            TrustedProofV2WorkflowAdmission.Render(
                ActionHostAuthorizationScenario.ActionSha,
                ActionHostAuthorizationScenario.PayloadSha));
        var header = Encoding.ASCII.GetBytes($"blob {workflow.Length}\0");
        scenario.Transport.Source = scenario.Transport.Source with
        {
            BlobSha = Convert.ToHexString(SHA1.HashData(
                header.Concat(workflow).ToArray())).ToLowerInvariant(),
            Bytes = workflow,
        };
        var source = TrustedProofPayloadBuildIdentity.SourceCommit;
        scenario.Transport.CurrentRun = scenario.Transport.CurrentRun with
        {
            HeadSha = source,
        };
        Assert.True(ActionHostLaunchContract.TryCreate(
            scenario.Launch.Inputs,
            scenario.Launch.EventJsonPath,
            scenario.Launch.EventJsonSha256,
            scenario.Launch.RepositoryName,
            scenario.Launch.RepositoryId,
            scenario.Launch.RunId,
            scenario.Launch.RunAttempt,
            scenario.Launch.WorkflowPath,
            scenario.Launch.WorkflowRef,
            source,
            source,
            scenario.Launch.PayloadSha256,
            scenario.Launch.BuildDiscriminator,
            scenario.Launch.Cancellation,
            scenario.Launch.ArtifactBridgeEndpoint,
            out var launch));
        var authorizer = new ActionHostAuthorizer(
            scenario.EventReader,
            scenario.Factory,
            ActionHostAuthorizationPolicy.TrustedProof,
            workflowAdmission: TrustedProofV2WorkflowAdmission.Instance);
        var result = await authorizer.AuthorizeAsync(
            launch!,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private sealed class ScriptedFactory(ScriptedTransport transport) :
        IReviewedSnapshotTransportFactory
    {
        public IReviewedSnapshotTransport Create(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedContentBudget budget) => transport;
    }

    private sealed class ScriptedTransport(
        IReadOnlyList<ActionHostGitHubPullRequestFact> pullRequests) :
        IReviewedSnapshotTransport
    {
        private int pullRequestIndex;

        internal int PullRequestReads { get; private set; }
        internal int PullRequestFileReads { get; private set; }

        public Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
            GetCurrentPullRequestAsync(CancellationToken cancellationToken)
        {
            PullRequestReads++;
            var index = Math.Min(
                pullRequestIndex++,
                pullRequests.Count - 1);
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitHubPullRequestFact>.Success(pullRequests[index]));
        }

        public Task<ReviewedSnapshotReadResult<
            ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
                int page,
                CancellationToken cancellationToken)
        {
            PullRequestFileReads++;
            throw new InvalidOperationException(
                "Trusted proof must not read historical PR files.");
        }

        public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
            GetBaseCommitAsync(CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Source must not read base commit.");

        public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
            GetTreeAsync(
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Source must not read base tree.");

        public Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
            StageBaseBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBaseBlobStagingLease staging,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Source must not read base blob.");

        public void Dispose() { }
    }
}
