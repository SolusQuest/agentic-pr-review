using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.ActionHost.Snapshot.GitObjects;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.GitObjects;

public sealed class ReviewedTreeReaderTests
{
    [Fact]
    public async Task MaterializesCompleteOrderedTreeWithSharedBlobModes()
    {
        var bytes = "shared-content"u8.ToArray();
        var nestedBytes = "nested"u8.ToArray();
        var sharedSha = GitBlobSha(bytes);
        var nestedSha = GitBlobSha(nestedBytes);
        var rootSha = new string('1', 40);
        var childSha = new string('2', 40);
        var submoduleSha = new string('3', 40);
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("module", "160000", "commit", submoduleSha, null),
                    Entry("link", "120000", "blob", sharedSha, bytes.Length),
                    Entry("plain", "100644", "blob", sharedSha, bytes.Length),
                    Entry("exec", "100755", "blob", sharedSha, bytes.Length),
                    Entry("dir", "040000", "tree", childSha, null),
                ],
                [childSha] =
                [
                    Entry("nested.txt", "100644", "blob", nestedSha,
                        nestedBytes.Length),
                ],
            },
            new Dictionary<string, byte[]>
            {
                [sharedSha] = bytes,
                [nestedSha] = nestedBytes,
            });
        var factory = new ScriptedFactory(transport);
        var reader = ReviewedSnapshotTestAccess.Reader(
            factory,
            TimeProvider.System);
        var parent = CreateTemporaryDirectory();
        try
        {
            var result = await reader.MaterializeAsync(
                await AuthorizedInvocation(),
                Token(),
                parent,
                CancellationToken.None);

            var snapshot = Assert.IsType<ReviewedTreeSnapshot>(result.Snapshot);
            Assert.Equal(ReviewedTreeFailure.None, result.Failure);
            Assert.Equal(
                ["dir/nested.txt", "exec", "link", "module", "plain"],
                snapshot.Records.Select(static record => record.Path));
            Assert.Equal(
                [
                    ReviewedTreeEntryKind.Regular,
                    ReviewedTreeEntryKind.Regular,
                    ReviewedTreeEntryKind.Symlink,
                    ReviewedTreeEntryKind.Submodule,
                    ReviewedTreeEntryKind.Regular,
                ],
                snapshot.Records.Select(static record => record.Kind));
            Assert.Equal(2, transport.StageCalls.Count);
            Assert.Equal(1, transport.StageCalls.Count(call =>
                StringComparer.Ordinal.Equals(call, sharedSha)));
            Assert.Null(snapshot.Records.Single(record =>
                record.Kind == ReviewedTreeEntryKind.Symlink).StagedBlob);
            Assert.True(snapshot.Budget.TryGetRemaining(out var remaining));
            Assert.True(remaining!.Requests <
                ReviewedContentLimits.GitObjectRequests);
            Assert.Equal(64, snapshot.Identity.Sha256.Length);

            await snapshot.DisposeAsync();
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task ReusedTreeShaAtSiblingPrefixesRemainsAValidLogicalTree()
    {
        var bytes = "shared-leaf"u8.ToArray();
        var blobSha = GitBlobSha(bytes);
        var rootSha = new string('a', 40);
        var reusedTreeSha = new string('b', 40);
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("first", "040000", "tree", reusedTreeSha, null),
                    Entry("second", "040000", "tree", reusedTreeSha, null),
                ],
                [reusedTreeSha] =
                [
                    Entry("leaf.txt", "100644", "blob", blobSha,
                        bytes.Length),
                ],
            },
            new Dictionary<string, byte[]> { [blobSha] = bytes });
        var result = await Read(transport);
        try
        {
            var snapshot = Assert.IsType<ReviewedTreeSnapshot>(
                result.Result.Snapshot);
            Assert.Equal(ReviewedTreeFailure.None, result.Result.Failure);
            Assert.Equal(["first/leaf.txt", "second/leaf.txt"],
                snapshot.Records.Select(static record => record.Path));
            Assert.Equal(2, transport.TreeCalls);
            Assert.Single(transport.StageCalls);

            await snapshot.DisposeAsync();
        }
        finally
        {
            Directory.Delete(result.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task ApiEntryOrderCannotChangeReviewedTreeIdentity()
    {
        var bytes = "same"u8.ToArray();
        var blobSha = GitBlobSha(bytes);
        var rootSha = new string('4', 40);
        var entries = new[]
        {
            Entry("z.txt", "100644", "blob", blobSha, bytes.Length),
            Entry("a.txt", "100755", "blob", blobSha, bytes.Length),
        };
        var forward = await Materialize(
            rootSha,
            entries,
            blobSha,
            bytes);
        var reverse = await Materialize(
            rootSha,
            entries.Reverse().ToArray(),
            blobSha,
            bytes);
        try
        {
            Assert.Equal(forward.Snapshot.Identity.Sha256,
                reverse.Snapshot.Identity.Sha256);
            Assert.True(forward.Snapshot.Identity.CanonicalPreimage.AsSpan()
                .SequenceEqual(
                    reverse.Snapshot.Identity.CanonicalPreimage.AsSpan()));
        }
        finally
        {
            await forward.Snapshot.DisposeAsync();
            await reverse.Snapshot.DisposeAsync();
            Directory.Delete(forward.Parent, recursive: true);
            Directory.Delete(reverse.Parent, recursive: true);
        }
    }

    [Fact]
    public async Task ConflictingBlobSizesFailWithoutStagingOrPrefix()
    {
        var bytes = "content"u8.ToArray();
        var sha = GitBlobSha(bytes);
        var rootSha = new string('5', 40);
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("regular", "100644", "blob", sha, bytes.Length),
                    Entry("link", "120000", "blob", sha, bytes.Length + 1),
                ],
            },
            new Dictionary<string, byte[]> { [sha] = bytes });

        var result = await Read(transport);

        Assert.Null(result.Result.Snapshot);
        Assert.Equal(ReviewedTreeFailure.InvalidGraph,
            result.Result.Failure);
        Assert.Empty(transport.StageCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(result.Parent));
        Directory.Delete(result.Parent, recursive: true);
    }

    [Fact]
    public async Task InvalidPathsModesAndDuplicateLeavesNeverBecomeReviewedBytes()
    {
        var blobSha = GitBlobSha([]);
        var invalidEntries = new[]
        {
            Entry("../escape", "100644", "blob", blobSha, 0),
            Entry(".git", "040000", "tree", new string('d', 40), null),
            Entry("special", "100664", "blob", blobSha, 0),
            Entry("tree-as-blob", "040000", "blob", blobSha, 0),
            Entry("uppercase-sha", "100644", "blob",
                new string('A', 40), 0),
        };
        foreach (var invalidEntry in invalidEntries)
        {
            var rootSha = new string('e', 40);
            var transport = new ScriptedTransport(
                ActionHostAuthorizationScenario.HeadSha,
                rootSha,
                new Dictionary<
                    string,
                    IReadOnlyList<ReviewedGitTreeEntryFact>>
                {
                    [rootSha] = [invalidEntry],
                },
                new Dictionary<string, byte[]> { [blobSha] = [] });
            var invalid = await Read(transport);
            Assert.Equal(ReviewedTreeFailure.InvalidGraph,
                invalid.Result.Failure);
            Assert.Null(invalid.Result.Snapshot);
            Assert.Empty(transport.StageCalls);
            Assert.Empty(Directory.EnumerateFileSystemEntries(invalid.Parent));
            Directory.Delete(invalid.Parent, recursive: true);
        }

        var duplicateRoot = new string('f', 40);
        var duplicateTransport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            duplicateRoot,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [duplicateRoot] =
                [
                    Entry("same", "100644", "blob", blobSha, 0),
                    Entry("same", "100755", "blob", blobSha, 0),
                ],
            },
            new Dictionary<string, byte[]> { [blobSha] = [] });
        var duplicate = await Read(duplicateTransport);
        Assert.Equal(ReviewedTreeFailure.InvalidGraph,
            duplicate.Result.Failure);
        Assert.Null(duplicate.Result.Snapshot);
        Assert.Empty(duplicateTransport.StageCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(duplicate.Parent));
        Directory.Delete(duplicate.Parent, recursive: true);
    }

    [Fact]
    public async Task CyclesFileDirectoryConflictsAndObjectKindConflictsFail()
    {
        var rootSha = new string('2', 40);
        var childSha = new string('3', 40);
        var cycle = await Read(new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("child", "040000", "tree", childSha, null),
                ],
                [childSha] =
                [
                    Entry("back", "040000", "tree", rootSha, null),
                ],
            },
            new Dictionary<string, byte[]>()));
        Assert.Equal(ReviewedTreeFailure.InvalidGraph, cycle.Result.Failure);
        Assert.Null(cycle.Result.Snapshot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(cycle.Parent));
        Directory.Delete(cycle.Parent, recursive: true);

        var blobSha = GitBlobSha([]);
        var conflict = await Read(new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("same", "040000", "tree", childSha, null),
                    Entry("same", "100644", "blob", blobSha, 0),
                ],
            },
            new Dictionary<string, byte[]> { [blobSha] = [] }));
        Assert.Equal(ReviewedTreeFailure.InvalidGraph,
            conflict.Result.Failure);
        Assert.Null(conflict.Result.Snapshot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(conflict.Parent));
        Directory.Delete(conflict.Parent, recursive: true);

        var objectKind = await Read(new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] =
                [
                    Entry("directory", "040000", "tree", childSha, null),
                    Entry("file", "100644", "blob", childSha, 0),
                ],
            },
            new Dictionary<string, byte[]>()));
        Assert.Equal(ReviewedTreeFailure.InvalidGraph,
            objectKind.Result.Failure);
        Assert.Null(objectKind.Result.Snapshot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(objectKind.Parent));
        Directory.Delete(objectKind.Parent, recursive: true);
    }

    [Fact]
    public async Task MissingAndMismatchedBlobsReturnNoPrefixAndCleanStaging()
    {
        var expected = "expected"u8.ToArray();
        var expectedSha = GitBlobSha(expected);
        var rootSha = new string('1', 40);
        var trees = new Dictionary<
            string,
            IReadOnlyList<ReviewedGitTreeEntryFact>>
        {
            [rootSha] =
            [
                Entry("file", "100644", "blob", expectedSha,
                    expected.Length),
            ],
        };

        var missing = await Read(new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            trees,
            new Dictionary<string, byte[]>()));
        Assert.Equal(ReviewedTreeFailure.MissingObject,
            missing.Result.Failure);
        Assert.Null(missing.Result.Snapshot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(missing.Parent));
        Directory.Delete(missing.Parent, recursive: true);

        var mismatched = await Read(new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            trees,
            new Dictionary<string, byte[]>
            {
                [expectedSha] = "tampered"u8.ToArray(),
            }));
        Assert.Equal(ReviewedTreeFailure.IdentityMismatch,
            mismatched.Result.Failure);
        Assert.Null(mismatched.Result.Snapshot);
        Assert.Empty(Directory.EnumerateFileSystemEntries(mismatched.Parent));
        Directory.Delete(mismatched.Parent, recursive: true);
    }

    [Fact]
    public async Task OverDepthAndPerBlobCapsUseOnlyUnsupportedSizeCode()
    {
        var maximumBytes = new byte[ReviewedContentLimits.HeadBlobBytes];
        var maximumBlobSha = GitBlobSha(maximumBytes);
        var maximumRoot = new string('c', 40);
        var maximumTransport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            maximumRoot,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [maximumRoot] =
                [
                    Entry("maximum", "100644", "blob", maximumBlobSha,
                        maximumBytes.Length),
                ],
            },
            new Dictionary<string, byte[]> { [maximumBlobSha] = maximumBytes });
        var maximum = await Read(maximumTransport);
        var maximumSnapshot = Assert.IsType<ReviewedTreeSnapshot>(
            maximum.Result.Snapshot);
        await maximumSnapshot.DisposeAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(maximum.Parent));
        Directory.Delete(maximum.Parent, recursive: true);

        var oversizedRoot = new string('6', 40);
        var oversizedTransport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            oversizedRoot,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [oversizedRoot] =
                [
                    Entry("large", "100644", "blob", new string('7', 40),
                        ReviewedContentLimits.HeadBlobBytes + 1),
                ],
            },
            new Dictionary<string, byte[]>());
        var oversized = await Read(oversizedTransport);
        Assert.Equal(ReviewedTreeFailureCodes.UnsupportedSize,
            oversized.Result.FailureCode);
        Assert.Null(oversized.Result.Snapshot);
        Directory.Delete(oversized.Parent, recursive: true);

        var atDepth = await Read(DepthTransport(
            ReviewedContentLimits.TreeDepth));
        var atDepthSnapshot = Assert.IsType<ReviewedTreeSnapshot>(
            atDepth.Result.Snapshot);
        await atDepthSnapshot.DisposeAsync();
        Assert.Empty(Directory.EnumerateFileSystemEntries(atDepth.Parent));
        Directory.Delete(atDepth.Parent, recursive: true);

        var depthTransport = DepthTransport(
            ReviewedContentLimits.TreeDepth + 1);
        var depth = await Read(depthTransport);
        Assert.Equal(ReviewedTreeFailureCodes.UnsupportedSize,
            depth.Result.FailureCode);
        Assert.Null(depth.Result.Snapshot);
        Directory.Delete(depth.Parent, recursive: true);
    }

    [Fact]
    public async Task RepeatedDirectoryFanoutConsumesMetadataBeforeQueueGrowth()
    {
        var rootSha = new string('8', 40);
        var repeatedSha = new string('9', 40);
        var emptySha = new string('a', 40);
        var rootEntries = Enumerable.Range(0, 200)
            .Select(index => Entry(
                "root-" + index.ToString("D3", CultureInfo.InvariantCulture) +
                    new string('r', 180),
                "040000",
                "tree",
                repeatedSha,
                null))
            .ToArray();
        var repeatedEntries = Enumerable.Range(0, 200)
            .Select(index => Entry(
                "child-" + index.ToString("D3", CultureInfo.InvariantCulture) +
                    new string('c', 180),
                "040000",
                "tree",
                emptySha,
                null))
            .ToArray();
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] = rootEntries,
                [repeatedSha] = repeatedEntries,
                [emptySha] = [],
            },
            new Dictionary<string, byte[]>());

        var result = await Read(transport);

        Assert.Equal(ReviewedTreeFailureCodes.UnsupportedSize,
            result.Result.FailureCode);
        Assert.Null(result.Result.Snapshot);
        Assert.Empty(transport.StageCalls);
        Directory.Delete(result.Parent, recursive: true);
    }

    [Fact]
    public async Task ShortNameRepeatedFanoutFailsWithOnlyThreeCachedTrees()
    {
        var rootSha = new string('a', 40);
        var repeatedSha = new string('b', 40);
        var emptySha = new string('c', 40);
        var rootEntries = Enumerable.Range(0, 1_000)
            .Select(index => Entry(
                "r" + index.ToString("D4", CultureInfo.InvariantCulture),
                "040000",
                "tree",
                repeatedSha,
                null))
            .ToArray();
        var repeatedEntries = Enumerable.Range(0, 1_000)
            .Select(index => Entry(
                "s" + index.ToString("D4", CultureInfo.InvariantCulture),
                "040000",
                "tree",
                emptySha,
                null))
            .ToArray();
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] = rootEntries,
                [repeatedSha] = repeatedEntries,
                [emptySha] = [],
            },
            new Dictionary<string, byte[]>());

        var result = await Read(transport);

        Assert.Equal(ReviewedTreeFailure.UnsupportedSize,
            result.Result.Failure);
        Assert.Null(result.Result.Snapshot);
        Assert.Empty(transport.StageCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(result.Parent));
        Directory.Delete(result.Parent, recursive: true);
    }

    [Fact]
    public async Task CachedTraversalCannotOutrunSharedDeadline()
    {
        var rootSha = new string('d', 40);
        var emptySha = new string('f', 40);
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] = Enumerable.Range(0, 1_000)
                    .Select(index => Entry(
                        "d" + index.ToString(
                            "D4",
                            CultureInfo.InvariantCulture),
                        "040000",
                        "tree",
                        emptySha,
                        null))
                    .ToArray(),
                [emptySha] = [],
            },
            new Dictionary<string, byte[]>());

        var result = await Read(
            transport,
            new AdvancingTimeProvider(TimeSpan.FromSeconds(1)));

        Assert.Equal(ReviewedTreeFailure.UnsupportedSize,
            result.Result.Failure);
        Assert.Null(result.Result.Snapshot);
        Assert.Empty(transport.StageCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(result.Parent));
        Directory.Delete(result.Parent, recursive: true);
    }

    [Fact]
    public async Task AlreadyCancelledRunReturnsValueFreeCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            new string('b', 40),
            new Dictionary<
                string,
                IReadOnlyList<ReviewedGitTreeEntryFact>>(),
            new Dictionary<string, byte[]>());
        var parent = CreateTemporaryDirectory();
        try
        {
            var reader = ReviewedSnapshotTestAccess.Reader(
                new ScriptedFactory(transport),
                TimeProvider.System);
            var result = await reader.MaterializeAsync(
                await AuthorizedInvocation(),
                Token(),
                parent,
                cancellation.Token);

            Assert.Equal(ReviewedTreeFailure.Cancelled, result.Failure);
            Assert.Null(result.Snapshot);
            Assert.Equal(0, transport.CommitCalls);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public async Task RelativeStagingParentFailsBeforeTransportCreation()
    {
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            new string('b', 40),
            new Dictionary<
                string,
                IReadOnlyList<ReviewedGitTreeEntryFact>>(),
            new Dictionary<string, byte[]>());
        var factory = new ScriptedFactory(transport);
        var reader = ReviewedSnapshotTestAccess.Reader(
            factory,
            TimeProvider.System);

        var result = await reader.MaterializeAsync(
            await AuthorizedInvocation(),
            Token(),
            "relative-staging-parent",
            CancellationToken.None);

        Assert.Equal(ReviewedTreeFailure.InternalFailure, result.Failure);
        Assert.Null(result.Snapshot);
        Assert.Equal(0, factory.CreateCalls);
        Assert.Equal(0, transport.CommitCalls);
    }

    private static async Task<(ReviewedTreeSnapshot Snapshot, string Parent)>
        Materialize(
            string rootSha,
            IReadOnlyList<ReviewedGitTreeEntryFact> entries,
            string blobSha,
            byte[] bytes)
    {
        var transport = new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            rootSha,
            new Dictionary<string, IReadOnlyList<ReviewedGitTreeEntryFact>>
            {
                [rootSha] = entries,
            },
            new Dictionary<string, byte[]> { [blobSha] = bytes });
        var read = await Read(transport);
        return (Assert.IsType<ReviewedTreeSnapshot>(read.Result.Snapshot),
            read.Parent);
    }

    private static async Task<(
        ReviewedTreeMaterializationResult Result,
        string Parent)> Read(
            ScriptedTransport transport,
            TimeProvider? timeProvider = null)
    {
        var parent = CreateTemporaryDirectory();
        var reader = ReviewedSnapshotTestAccess.Reader(
            new ScriptedFactory(transport),
            timeProvider ?? TimeProvider.System);
        var result = await reader.MaterializeAsync(
            await AuthorizedInvocation(),
            Token(),
            parent,
            CancellationToken.None);
        return (result, parent);
    }

    private static ReviewedGitTreeEntryFact Entry(
        string path,
        string mode,
        string type,
        string sha,
        long? size) => new(path, mode, type, sha, size);

    private static ScriptedTransport DepthTransport(int edges)
    {
        var trees = new Dictionary<
            string,
            IReadOnlyList<ReviewedGitTreeEntryFact>>();
        var shas = Enumerable.Range(0, edges + 1)
            .Select(index => index.ToString("x40", CultureInfo.InvariantCulture))
            .ToArray();
        for (var index = 0; index < edges; index++)
        {
            trees[shas[index]] =
            [
                Entry("d", "040000", "tree", shas[index + 1], null),
            ];
        }

        trees[shas[^1]] = [];
        return new ScriptedTransport(
            ActionHostAuthorizationScenario.HeadSha,
            shas[0],
            trees,
            new Dictionary<string, byte[]>());
    }

    private static async Task<ActionHostAuthorizer.AuthorizedInvocation>
        AuthorizedInvocation()
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowDispatch);
        var result = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch,
            CancellationToken.None);
        return Assert.IsType<ActionHostAuthorizer.AuthorizedInvocation>(
            result.Invocation);
    }

    private static ActionHostGitHubToken Token()
    {
        Assert.True(ActionHostGitHubToken.TryCreate(
            "token-canary",
            out var token));
        return token!;
    }

    private static string GitBlobSha(byte[] bytes)
    {
        var header = Encoding.ASCII.GetBytes(
            "blob " + bytes.Length.ToString(CultureInfo.InvariantCulture) +
            "\0");
        return Convert.ToHexString(SHA1.HashData([.. header, .. bytes]))
            .ToLowerInvariant();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Join(
            Path.GetTempPath(),
            "apr-h4-reader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        private readonly long _step;
        private long _timestamp;

        internal AdvancingTimeProvider(TimeSpan step)
        {
            _step = step.Ticks;
        }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            var current = _timestamp;
            _timestamp = checked(_timestamp + _step);
            return current;
        }
    }

    private sealed class ScriptedFactory : IReviewedGitObjectTransportFactory
    {
        private readonly ScriptedTransport _transport;

        internal ScriptedFactory(ScriptedTransport transport)
        {
            _transport = transport;
        }

        internal int CreateCalls { get; private set; }

        public IReviewedGitObjectTransport Create(
            ActionHostAuthorizer.AuthorizedInvocation invocation,
            ActionHostGitHubToken token,
            ReviewedContentBudget budget)
        {
            CreateCalls++;
            Assert.Equal(ActionHostAuthorizationScenario.HeadSha,
                invocation.PullRequest.HeadSha);
            Assert.Equal("[REDACTED]", token.ToString());
            _transport.Budget = budget;
            return _transport;
        }
    }

    private sealed class ScriptedTransport : IReviewedGitObjectTransport
    {
        private readonly string _headSha;
        private readonly string _rootSha;
        private readonly IReadOnlyDictionary<
            string,
            IReadOnlyList<ReviewedGitTreeEntryFact>> _trees;
        private readonly IReadOnlyDictionary<string, byte[]> _blobs;

        internal ScriptedTransport(
            string headSha,
            string rootSha,
            IReadOnlyDictionary<
                string,
                IReadOnlyList<ReviewedGitTreeEntryFact>> trees,
            IReadOnlyDictionary<string, byte[]> blobs)
        {
            _headSha = headSha;
            _rootSha = rootSha;
            _trees = trees;
            _blobs = blobs;
        }

        internal ReviewedContentBudget? Budget { get; set; }

        internal int CommitCalls { get; private set; }

        internal int TreeCalls { get; private set; }

        internal List<string> StageCalls { get; } = [];

        public Task<ReviewedGitObjectResult<ReviewedGitCommitFact>>
            GetCommitAsync(CancellationToken cancellationToken)
        {
            CommitCalls++;
            if (!Budget!.TryReserveRequest(cancellationToken))
            {
                return Task.FromResult(
                    ReviewedGitObjectResult<ReviewedGitCommitFact>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize));
            }

            return Task.FromResult(
                ReviewedGitObjectResult<ReviewedGitCommitFact>.Success(
                    new ReviewedGitCommitFact(_headSha, _rootSha)));
        }

        public Task<ReviewedGitObjectResult<ReviewedGitTreeFact>> GetTreeAsync(
            string treeSha,
            CancellationToken cancellationToken)
        {
            TreeCalls++;
            if (!Budget!.TryReserveRequest(cancellationToken))
            {
                return Task.FromResult(
                    ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                        ReviewedGitObjectFailure.UnsupportedSize));
            }

            return Task.FromResult(_trees.TryGetValue(treeSha, out var entries)
                ? ReviewedGitObjectResult<ReviewedGitTreeFact>.Success(
                    new ReviewedGitTreeFact(treeSha, entries))
                : ReviewedGitObjectResult<ReviewedGitTreeFact>.Failed(
                    ReviewedGitObjectFailure.NotFound));
        }

        public async Task<ReviewedGitObjectResult<ReviewedStagedBlob>>
            StageBlobAsync(
                string blobSha,
                long declaredSize,
                ReviewedBlobStagingLease staging,
                CancellationToken cancellationToken)
        {
            StageCalls.Add(blobSha);
            if (!Budget!.TryReserveRequest(cancellationToken))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }

            if (!_blobs.TryGetValue(blobSha, out var bytes))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.NotFound);
            }

            long responseBytes = 0;
            if (!Budget.TryConsumeResponseBytes(
                    ref responseBytes,
                    bytes.Length,
                    cancellationToken))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.UnsupportedSize);
            }

            await using var writer = staging.TryCreateWriter(
                blobSha,
                declaredSize);
            if (writer is null || !await writer.WriteAsync(
                    bytes,
                    cancellationToken))
            {
                return ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.StagingFailure);
            }

            var staged = await writer.CompleteAsync(cancellationToken);
            return staged is null
                ? ReviewedGitObjectResult<ReviewedStagedBlob>.Failed(
                    ReviewedGitObjectFailure.IdentityMismatch)
                : ReviewedGitObjectResult<ReviewedStagedBlob>.Success(staged);
        }

        public async Task<ReviewedGitObjectResult<ReviewedHeadArchiveBatch>>
            StageHeadRegularBlobsAsync(
                IReadOnlyList<ReviewedHeadArchiveEntry> entries,
                ReviewedBlobStagingLease staging,
                CancellationToken cancellationToken)
        {
            var staged = new Dictionary<string, ReviewedStagedBlob>(
                StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                if (staged.ContainsKey(entry.Sha))
                {
                    continue;
                }

                var result = await StageBlobAsync(entry.Sha, entry.Size,
                    staging, cancellationToken);
                if (result.Value is null)
                {
                    return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Failed(
                        result.Failure);
                }

                staged.Add(entry.Sha, result.Value);
            }

            return ReviewedGitObjectResult<ReviewedHeadArchiveBatch>.Success(
                new ReviewedHeadArchiveBatch(staged));
        }

        public void Dispose()
        {
        }
    }
}
