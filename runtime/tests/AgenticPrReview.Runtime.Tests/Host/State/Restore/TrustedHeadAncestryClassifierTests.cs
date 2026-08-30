using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class TrustedHeadAncestryClassifierTests
{
    private static readonly string ProducerBase = new('a', 40);
    private static readonly string ProducerHead = new('b', 40);
    private static readonly string CurrentBase = new('c', 40);
    private static readonly string CurrentHead = new('d', 40);
    private static readonly string MergeParent = new('e', 40);
    private static readonly string Tree = new('f', 40);

    [Fact]
    public async Task ExactBaseAndHeadAreSameWithoutTransport()
    {
        using var transport = new FakeGitObjectTransport();
        var result = await new TrustedHeadAncestryClassifier(transport)
            .ClassifyAsync(
                "owner/repository",
                ProducerBase,
                ProducerHead,
                ProducerBase,
                ProducerHead,
                CancellationToken.None);

        Assert.Equal(AgentSessionHeadTransition.SameHead, result);
        Assert.Equal(0, transport.CommitCalls);
    }

    [Fact]
    public async Task SameHeadWithChangedBaseIsDivergedWithoutTransport()
    {
        using var transport = new FakeGitObjectTransport();
        var result = await new TrustedHeadAncestryClassifier(transport)
            .ClassifyAsync(
                "owner/repository",
                ProducerBase,
                ProducerHead,
                CurrentBase,
                ProducerHead,
                CancellationToken.None);

        Assert.Equal(AgentSessionHeadTransition.Diverged, result);
        Assert.Equal(0, transport.CommitCalls);
    }

    [Fact]
    public async Task MergeGraphProvesBothHeadAndBaseAncestry()
    {
        using var transport = new FakeGitObjectTransport();
        transport.Add(CurrentHead, CurrentBase, MergeParent);
        transport.Add(CurrentBase, ProducerHead);
        transport.Add(ProducerHead, ProducerBase);
        transport.Add(MergeParent);

        var result = await new TrustedHeadAncestryClassifier(transport)
            .ClassifyAsync(
                "owner/repository",
                ProducerBase,
                ProducerHead,
                CurrentBase,
                CurrentHead,
                CancellationToken.None);

        Assert.Equal(AgentSessionHeadTransition.VerifiedAhead, result);
        Assert.InRange(transport.CommitCalls, 2, 4);
    }

    [Fact]
    public async Task CompleteUnrelatedGraphIsDiverged()
    {
        using var transport = new FakeGitObjectTransport();
        transport.Add(CurrentHead, CurrentBase);
        transport.Add(CurrentBase);

        var result = await new TrustedHeadAncestryClassifier(transport)
            .ClassifyAsync(
                "owner/repository",
                ProducerBase,
                ProducerHead,
                CurrentBase,
                CurrentHead,
                CancellationToken.None);

        Assert.Equal(AgentSessionHeadTransition.Diverged, result);
    }

    [Theory]
    [InlineData(FailureKind.MissingParents)]
    [InlineData(FailureKind.DuplicateParent)]
    [InlineData(FailureKind.Cycle)]
    [InlineData(FailureKind.OversizedResponse)]
    [InlineData(FailureKind.TransportFailure)]
    public async Task IncompleteOrInvalidGraphIsUnknown(FailureKind kind)
    {
        using var transport = new FakeGitObjectTransport();
        transport.AddFailure(CurrentHead, kind);

        var result = await new TrustedHeadAncestryClassifier(transport)
            .ClassifyAsync(
                "owner/repository",
                ProducerBase,
                ProducerHead,
                CurrentBase,
                CurrentHead,
                CancellationToken.None);

        Assert.Equal(AgentSessionHeadTransition.Unknown, result);
    }

    public enum FailureKind
    {
        MissingParents,
        DuplicateParent,
        Cycle,
        OversizedResponse,
        TransportFailure,
    }

    private sealed class FakeGitObjectTransport : IActionHostGitObjectTransport
    {
        private readonly Dictionary<string,
            ActionHostGitObjectResult<ActionHostGitCommitObject>> commits =
            new(StringComparer.Ordinal);

        internal int CommitCalls { get; private set; }

        internal void Add(string sha, params string[] parents) =>
            commits.Add(
                sha,
                ActionHostGitObjectResult<ActionHostGitCommitObject>.Success(
                    new ActionHostGitCommitObject(sha, Tree, parents),
                    512));

        internal void AddFailure(string sha, FailureKind kind)
        {
            commits.Add(
                sha,
                kind switch
                {
                    FailureKind.MissingParents =>
                        ActionHostGitObjectResult<ActionHostGitCommitObject>
                            .Success(
                                new ActionHostGitCommitObject(
                                    sha,
                                    Tree,
                                    ParentShas: null),
                                512),
                    FailureKind.DuplicateParent =>
                        ActionHostGitObjectResult<ActionHostGitCommitObject>
                            .Success(
                                new ActionHostGitCommitObject(
                                    sha,
                                    Tree,
                                    [CurrentBase, CurrentBase]),
                                512),
                    FailureKind.Cycle =>
                        ActionHostGitObjectResult<ActionHostGitCommitObject>
                            .Success(
                                new ActionHostGitCommitObject(
                                    sha,
                                    Tree,
                                    [sha]),
                                512),
                    FailureKind.OversizedResponse =>
                        ActionHostGitObjectResult<ActionHostGitCommitObject>
                            .Success(
                                new ActionHostGitCommitObject(
                                    sha,
                                    Tree,
                                    [CurrentBase]),
                                TrustedHeadAncestryClassifier
                                    .MaximumCommitResponseBytes + 1),
                    FailureKind.TransportFailure =>
                        ActionHostGitObjectResult<ActionHostGitCommitObject>
                            .Failed(ActionHostGitObjectFailure.TransportFailure),
                    _ => throw new ArgumentOutOfRangeException(nameof(kind)),
                });
        }

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            CommitCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(commits.TryGetValue(commitSha, out var value)
                ? value
                : ActionHostGitObjectResult<ActionHostGitCommitObject>.Failed(
                    ActionHostGitObjectFailure.NotFound));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tree transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Blob transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Archive transport was called.");

        public void Dispose() { }
    }
}
