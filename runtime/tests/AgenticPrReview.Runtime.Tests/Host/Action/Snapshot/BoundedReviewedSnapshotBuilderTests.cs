using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Diff;
using AgenticPrReview.Runtime.ActionHost.Snapshot.Materialization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;

public sealed class BoundedReviewedSnapshotBuilderTests
{
    [Fact]
    public async Task ProofAddedFileRequirementRejectsAPathPresentInEffectiveBase()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var canary = "APR178_TOOL_DATA_CANARY\n"u8.ToArray();
        const string canaryPath = "apr178-path-canary.txt";
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                canaryPath,
                "100644",
                ReviewedTreeEntryKind.Regular,
                canary));
        var file = new ActionHostPullRequestFileObject(
            H5SnapshotTestSupport.BlobSha(canary),
            canaryPath,
            null,
            "added",
            1,
            0,
            1,
            null);
        var script = new Script(
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
            file,
            invocation.PullRequest.BaseSha,
            "already-present\n"u8.ToArray());

        var result = await new BoundedReviewedSnapshotBuilder(
                new ScriptedFactory(script),
                new RequiredAbsentSourceFactory(file))
            .BuildAsync(
                invocation,
                H5SnapshotTestSupport.Token(),
                tree,
                parent,
                CancellationToken.None);

        Assert.Null(result.Lease);
        Assert.Equal(
            ReviewedSnapshotReadFailure.IdentityMismatch,
            result.Failure);
        Assert.Equal(1, script.BaseBlobCalls);
        Directory.Delete(parent, recursive: true);
    }

    [Fact]
    public async Task CompositionReturnsCompleteImmutableAgentViewAndCleansStaging()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var before = "before\n"u8.ToArray();
        var after = "after\n"u8.ToArray();
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                after),
            new H5HeadEntry(
                "link",
                "120000",
                ReviewedTreeEntryKind.Symlink,
                null,
                new string('7', 40)));
        var script = new Script(
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
            new ActionHostPullRequestFileObject(
                H5SnapshotTestSupport.BlobSha(after),
                "file.txt",
                null,
                "modified",
                1,
                1,
                2,
                null),
            invocation.PullRequest.BaseSha,
            before);

        var result = await new BoundedReviewedSnapshotBuilder(
                new ScriptedFactory(script))
            .BuildAsync(
                invocation,
                H5SnapshotTestSupport.Token(),
                tree,
                parent,
                CancellationToken.None);

        var lease = Assert.IsType<BoundedReviewedSnapshotLease>(result.Lease);
        var root = lease.Snapshot.AbsoluteRoot;
        try
        {
            Assert.Equal(ReviewedSnapshotReadFailure.None, result.Failure);
            Assert.Equal<string>(
                ["file.txt"],
                lease.Snapshot.OrderedTrackedFiles);
            Assert.Equal<string>(
                ["file.txt", "link"],
                lease.Snapshot.OrderedReviewedHeadPaths);
            Assert.True(lease.Snapshot.Contains("file.txt"));
            Assert.False(lease.Snapshot.Contains("link"));
            Assert.True(lease.Snapshot.TryGetDiffSource(
                "file.txt",
                out var source));
            Assert.Equal(1, source.RepresentedAdditions);
            Assert.Equal(1, source.RepresentedDeletions);
            Assert.Equal("after\n", await File.ReadAllTextAsync(
                Path.Join(root, "file.txt")));
            Assert.Equal(invocation.PullRequest.RepositoryId,
                lease.Identities.RepositoryId);
            Assert.Equal(invocation.PullRequest.BaseSha,
                lease.Identities.BaseSha);
            Assert.Equal(invocation.PullRequest.HeadSha,
                lease.Identities.HeadSha);
            Assert.All(
                new[]
                {
                    lease.Identities.ReviewedTreeSha256,
                    lease.Identities.ChangedFilesSha256,
                    lease.Identities.DiffSha256,
                    lease.Identities.MaterializationSha256,
                },
                static digest => Assert.Equal(64, digest.Length));
            Assert.Equal(2, script.CurrentPullRequestCalls);
            Assert.Equal(1, script.PullRequestFileCalls);
            Assert.Equal(1, script.BaseBlobCalls);

            var executor = new SnapshotToolExecutor(
                lease.Snapshot,
                new VerifiedReviewedFileAccess());
            Assert.True(AgentToolArguments.TryListFiles(
                "{}",
                out var listFiles));
            Assert.True(AgentToolArguments.TryListChangedFiles(
                "{}",
                out var listChangedFiles));
            Assert.True(AgentToolArguments.TryReadDiff(
                "{\"path\":\"file.txt\"}",
                out var readDiff));
            Assert.True(AgentToolArguments.TryReadFile(
                "{\"path\":\"file.txt\"}",
                out var readFile));
            Assert.True(AgentToolArguments.TrySearchText(
                "{\"query\":\"after\",\"path\":\"file.txt\"}",
                out var searchText));
            Assert.True(AgentToolArguments.TryFinishReview(
                "{\"summary\":\"done\",\"findings\":[]}",
                out var finishReview));

            PreparedAgentToolCall[] calls =
            [
                new PreparedListFilesCall("list-files", listFiles!),
                new PreparedListChangedFilesCall(
                    "list-changed-files",
                    listChangedFiles!),
                new PreparedReadDiffCall("read-diff", readDiff!),
                new PreparedReadFileCall("read-file", readFile!),
                new PreparedSearchTextCall("search-text", searchText!),
            ];
            foreach (var call in calls)
            {
                Assert.Null(executor.Preflight(call));
                var execution = await executor.ExecuteAsync(
                    call,
                    CancellationToken.None);
                Assert.True(execution.Succeeded, execution.FailureCode);
                Assert.NotNull(execution.Observation);
                Assert.Contains("file.txt", execution.ResultJson);
            }

            var terminal = new PreparedFinishReviewCall(
                "finish-review",
                finishReview!);
            Assert.Equal(
                AgentFailureCodes.UnknownTool,
                executor.Preflight(terminal));
            var terminalExecution = await executor.ExecuteAsync(
                terminal,
                CancellationToken.None);
            Assert.False(terminalExecution.Succeeded);
            Assert.Equal(
                AgentFailureCodes.UnknownTool,
                terminalExecution.FailureCode);
        }
        finally
        {
            await lease.DisposeAsync();
            Assert.False(Directory.Exists(root));
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ChangedMetadataOverflowFailsBeforeRootCreation()
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var before = "before\n"u8.ToArray();
        var after = "after\n"u8.ToArray();
        var rootCreateCalled = false;
        var tree = await H5SnapshotTestSupport.TreeAsync(
            invocation,
            parent,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                after));
        var script = new Script(
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
            new ActionHostPullRequestFileObject(
                H5SnapshotTestSupport.BlobSha(after),
                "file.txt",
                null,
                "modified",
                1,
                1,
                2,
                null),
            invocation.PullRequest.BaseSha,
            before);

        var result = await new BoundedReviewedSnapshotBuilder(
                new ScriptedFactory(script))
            .BuildAsync(
                invocation,
                H5SnapshotTestSupport.Token(),
                tree,
                parent,
                CancellationToken.None,
                new ReviewedRootMaterializationHooks
                {
                    BeforeRootCreate = _ => rootCreateCalled = true,
                },
                maximumChangedMetadataBytes: 1);

        Assert.Null(result.Lease);
        Assert.Equal(
            ReviewedSnapshotReadFailure.UnsupportedSize,
            result.Failure);
        Assert.False(rootCreateCalled);
        Assert.Empty(Directory.EnumerateDirectories(
            parent,
            "apr-tool-root-*"));
        Directory.Delete(parent, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalAdmissionRejectsExpiredOrCancelledSnapshots(
        bool cancel)
    {
        var invocation = await H5SnapshotTestSupport.AuthorizedInvocation();
        var parent = H5SnapshotTestSupport.TemporaryDirectory();
        var before = "before\n"u8.ToArray();
        var after = "after\n"u8.ToArray();
        var time = new H5ManualTimeProvider();
        var budget = ReviewedSnapshotTestAccess.Budget(
            ReviewedContentLimits.GitObjectRequests,
            ReviewedContentLimits.GitObjectResponseBytes,
            ReviewedContentLimits.AggregateResponseBytes,
            ReviewedContentLimits.AcquisitionAndMaterializationTimeout,
            time);
        var tree = await H5SnapshotTestSupport.TreeWithBudgetAsync(
            invocation,
            parent,
            budget,
            new H5HeadEntry(
                "file.txt",
                "100644",
                ReviewedTreeEntryKind.Regular,
                after));
        var script = new Script(
            H5SnapshotTestSupport.PullRequest(invocation.PullRequest),
            new ActionHostPullRequestFileObject(
                H5SnapshotTestSupport.BlobSha(after),
                "file.txt",
                null,
                "modified",
                1,
                1,
                2,
                null),
            invocation.PullRequest.BaseSha,
            before);
        using var cancellation = new CancellationTokenSource();
        System.Action beforeFinalAdmission = cancel
            ? cancellation.Cancel
            : () => time.Advance(
                ReviewedContentLimits.AcquisitionAndMaterializationTimeout);

        var result = await new BoundedReviewedSnapshotBuilder(
                new ScriptedFactory(script))
            .BuildAsync(
                invocation,
                H5SnapshotTestSupport.Token(),
                tree,
                parent,
                cancellation.Token,
                beforeFinalAdmission: beforeFinalAdmission);

        Assert.Null(result.Lease);
        Assert.Equal(
            cancel
                ? ReviewedSnapshotReadFailure.Cancelled
                : ReviewedSnapshotReadFailure.UnsupportedSize,
            result.Failure);
        Assert.Empty(Directory.EnumerateDirectories(
            parent,
            "apr-tool-root-*"));
        Directory.Delete(parent, recursive: true);
    }

    private sealed class ScriptedFactory : IReviewedSnapshotTransportFactory
    {
        private readonly Script _script;

        internal ScriptedFactory(Script script)
        {
            _script = script;
        }

        public IReviewedSnapshotTransport Create(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedContentBudget budget) => new ScriptedTransport(_script);
    }

    private sealed class Script
    {
        internal Script(
            ActionHostGitHubPullRequestFact pullRequest,
            ActionHostPullRequestFileObject file,
            string baseSha,
            byte[] baseBytes)
        {
            PullRequest = pullRequest;
            File = file;
            BaseSha = baseSha;
            BaseBytes = baseBytes;
            BaseBlobSha = H5SnapshotTestSupport.BlobSha(baseBytes);
            BaseRootTreeSha = new string('c', 40);
        }

        internal ActionHostGitHubPullRequestFact PullRequest { get; }
        internal ActionHostPullRequestFileObject File { get; }
        internal string BaseSha { get; }
        internal byte[] BaseBytes { get; }
        internal string BaseBlobSha { get; }
        internal string BaseRootTreeSha { get; }
        internal int CurrentPullRequestCalls { get; set; }
        internal int PullRequestFileCalls { get; set; }
        internal int BaseBlobCalls { get; set; }
    }

    private sealed class ScriptedTransport : IReviewedSnapshotTransport
    {
        private readonly Script _script;

        internal ScriptedTransport(Script script)
        {
            _script = script;
        }

        public Task<ReviewedSnapshotReadResult<ActionHostGitHubPullRequestFact>>
            GetCurrentPullRequestAsync(CancellationToken cancellationToken)
        {
            _script.CurrentPullRequestCalls++;
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitHubPullRequestFact>.Success(
                    _script.PullRequest));
        }

        public Task<ReviewedSnapshotReadResult<
            ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
            int page,
            CancellationToken cancellationToken)
        {
            _script.PullRequestFileCalls++;
            return Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostPullRequestFilePageObject>.Success(
                    new([_script.File], true)));
        }

        public Task<ReviewedSnapshotReadResult<ActionHostGitCommitObject>>
            GetBaseCommitAsync(CancellationToken cancellationToken) =>
            Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitCommitObject>.Success(
                    new(_script.BaseSha, _script.BaseRootTreeSha)));

        public Task<ReviewedSnapshotReadResult<ActionHostGitTreeObject>>
            GetTreeAsync(
                string treeSha,
                CancellationToken cancellationToken) =>
            Task.FromResult(ReviewedSnapshotReadResult<
                ActionHostGitTreeObject>.Success(
                    new(
                        treeSha,
                        [
                            new(
                                _script.File.Path,
                                "100644",
                                "blob",
                                _script.BaseBlobSha,
                                _script.BaseBytes.LongLength),
                        ])));

        public async Task<ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>>
            StageBaseBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBaseBlobStagingLease staging,
                CancellationToken cancellationToken)
        {
            _script.BaseBlobCalls++;
            await using var writer = staging.TryCreateWriter(
                blobSha,
                declaredSize);
            Assert.NotNull(writer);
            await writer!.WriteAsync(_script.BaseBytes, cancellationToken);
            var blob = await writer.CompleteAsync(cancellationToken);
            return ReviewedSnapshotReadResult<ReviewedBaseStagedBlob>.Success(
                Assert.IsType<ReviewedBaseStagedBlob>(blob));
        }

        public void Dispose()
        {
        }
    }

    private sealed class RequiredAbsentSourceFactory(
        ActionHostPullRequestFileObject file) :
        IReviewedChangedFileSourceFactory
    {
        public IReviewedChangedFileSource Create(
            IReviewedSnapshotTransportFactory transportFactory) =>
            new RequiredAbsentSource(file);
    }

    private sealed class RequiredAbsentSource(
        ActionHostPullRequestFileObject file) : IReviewedChangedFileSource
    {
        public Task<ReviewedSnapshotReadResult<ReviewedChangedFileSet>> ReadAsync(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedTreeSnapshot tree,
            CancellationToken cancellationToken)
        {
            var fact = new ReviewedPullRequestFileFact(
                file.Sha,
                file.Path,
                file.PreviousPath,
                file.Status,
                file.Additions,
                file.Deletions,
                file.Changes,
                file.Patch,
                false);
            var facts = System.Collections.Immutable.ImmutableArray.Create(fact);
            return Task.FromResult(
                ReviewedSnapshotReadResult<ReviewedChangedFileSet>.Success(
                    new(
                        facts,
                        ReviewedChangedFileIdentityWriter.Write(facts),
                        requireAddedBaseAbsence: true)));
        }
    }
}
