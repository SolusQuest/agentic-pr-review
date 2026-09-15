using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

namespace AgenticPrReview.Runtime.Tests.Host.Action;

internal sealed class FullPathGitHubFactory :
    IActionHostGitObjectTransportFactory,
    IActionHostReviewedSnapshotTransportFactory
{
    private static readonly string WorkflowRoot = new('1', 40);
    private static readonly string GitHubTree = new('2', 40);
    private static readonly string InstructionsTree = new('3', 40);
    private static readonly string ConfigBlob = new('4', 40);
    private static readonly string InstructionsBlob = new('5', 40);
    private static readonly string ReviewedRoot = new('6', 40);
    private readonly string FileBlob;
    private static readonly string BaseRoot = new('8', 40);
    private readonly byte[] FileBytes;
    private readonly string? previousHead;
    private readonly string patch;
    private static readonly byte[] Instructions =
        Encoding.UTF8.GetBytes("Review the exact snapshot.");
    private readonly ActionHostGitHubPullRequestFact pullRequest;
    private readonly bool withInlineFile;
    private readonly byte[] config;
    private readonly string workflowSha;

    internal int CurrentPullRequestCalls { get; private set; }
    internal int GitCommitCalls { get; private set; }
    internal int GitTreeCalls { get; private set; }
    internal int GitBlobCalls { get; private set; }
    internal System.Action? OnCurrentPullRequest { get; set; }
    internal Func<int, ActionHostGitHubPullRequestFact>?
        CurrentPullRequestFact
    { get; set; }

    internal FullPathGitHubFactory(
        ActionHostGitHubPullRequestFact pullRequest,
        bool withInlineFile = false,
        string? workflowSha = null,
        byte[]? fileBytes = null,
        string? previousHead = null)
    {
        this.pullRequest = pullRequest;
        this.withInlineFile = withInlineFile;
        FileBytes = fileBytes?.ToArray() ?? Encoding.UTF8.GetBytes("changed line\n");
        FileBlob = Convert.ToHexStringLower(SHA1.HashData(
            Encoding.ASCII.GetBytes($"blob {FileBytes.Length}\0").Concat(FileBytes).ToArray()));
        this.previousHead = previousHead;
        var lines = Encoding.UTF8.GetString(FileBytes).TrimEnd('\n').Split('\n');
        patch = fileBytes is null ? "@@ -0,0 +1 @@\n+changed line"
            : $"@@ -0,0 +1,{lines.Length} @@\n" + string.Join('\n', lines.Select(line => "+" + line));
        this.workflowSha = workflowSha ??
            ActionHostAuthorizationScenario.WorkflowSha;
        config = Encoding.UTF8.GetBytes(
            "{\"schema\":\"agentic-pr-review.config.v1\"," +
            "\"instructionsPath\":\".github/agentic-pr-review/" +
            "instructions.md\",\"publication\":{\"mode\":\"" +
            (withInlineFile ? "sticky_and_inline" : "sticky") +
            "\"" + (withInlineFile
                ? ",\"inlineMinSeverity\":\"high\""
                : string.Empty) + "}}");
    }

    public IActionHostGitObjectTransport CreateExactObjectTransport(
        ActionHostGitHubToken token) => new GitObjectTransport(this);

    public IActionHostReviewedSnapshotTransport
        CreateReviewedSnapshotTransport(ActionHostGitHubToken token) =>
        new SnapshotTransport(this);

    private sealed class GitObjectTransport(FullPathGitHubFactory owner) :
        IActionHostGitObjectTransport
    {
        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.GitCommitCalls++;
            var value = commitSha == owner.workflowSha
                ? new ActionHostGitCommitObject(commitSha, WorkflowRoot)
                : new ActionHostGitCommitObject(
                    commitSha,
                    commitSha == owner.pullRequest.BaseSha
                        ? BaseRoot
                        : ReviewedRoot,
                    commitSha == owner.pullRequest.HeadSha && owner.previousHead is not null ? [owner.previousHead] : []);
            return Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitCommitObject>.Success(value, 64));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.GitTreeCalls++;
            var value = treeSha switch
            {
                var sha when sha == WorkflowRoot => new(
                    sha,
                    [new(".github", "040000", "tree", GitHubTree)]),
                var sha when sha == GitHubTree => new(
                    sha,
                    [
                        new(
                            "agentic-pr-review.json",
                            "100644",
                            "blob",
                            ConfigBlob),
                        new(
                            "agentic-pr-review",
                            "040000",
                            "tree",
                            InstructionsTree),
                    ]),
                var sha when sha == InstructionsTree => new(
                    sha,
                    [new(
                        "instructions.md",
                        "100644",
                        "blob",
                        InstructionsBlob)]),
                var sha when sha == BaseRoot => new(
                    sha,
                    []),
                _ => new ActionHostGitTreeObject(
                    ReviewedRoot,
                    owner.withInlineFile
                        ? [new(
                            "file.txt",
                            "100644",
                            "blob",
                            owner.FileBlob,
                            owner.FileBytes.LongLength)]
                        : []),
            };
            return Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitTreeObject>.Success(value, 64));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner.GitBlobCalls++;
            var value = new ActionHostGitBlobObject(
                blobSha,
                blobSha == ConfigBlob
                    ? (byte[])owner.config.Clone()
                    : blobSha == owner.FileBlob
                        ? (byte[])owner.FileBytes.Clone()
                        : (byte[])Instructions.Clone());
            return Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitBlobObject>.Success(value, value.Bytes.Length));
        }

        public Task<ActionHostGitObjectResult<ActionHostGitArchiveReader>>
            GetHeadArchiveAsync(
                string repositoryName,
                string headSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!owner.withInlineFile || headSha != owner.pullRequest.HeadSha)
            {
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitArchiveReader>.Failed(
                        ActionHostGitObjectFailure.NotFound));
            }

            return Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitArchiveReader>.Success(
                new CompositionArchiveReader(
                    "agentic-pr-review-fixture/file.txt", owner.FileBytes),
                owner.FileBytes.Length));
        }

        public void Dispose() { }
    }

    private sealed class CompositionArchiveReader(
        string name,
        byte[] bytes) : ActionHostGitArchiveReader
    {
        private readonly MemoryStream stream = new(bytes, writable: false);
        private int returned;
        private bool disposed;

        internal override int CapturedResponseBytes => bytes.Length;

        internal override Task<ActionHostGitArchiveEntry?> GetNextEntryAsync(
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(CompositionArchiveReader));
            }

            if (returned == 2)
            {
                return Task.FromResult<ActionHostGitArchiveEntry?>(null);
            }

            if (returned++ == 0)
            {
                return Task.FromResult<ActionHostGitArchiveEntry?>(new(
                    name[..(name.LastIndexOf('/') + 1)],
                    ActionHostGitArchiveEntryType.Directory,
                    0,
                    0,
                    null,
                    null));
            }

            return Task.FromResult<ActionHostGitArchiveEntry?>(new(
                name,
                ActionHostGitArchiveEntryType.RegularFile,
                0x1b4,
                bytes.Length,
                null,
                stream));
        }

        public override void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stream.Dispose();
        }
    }

    private sealed class SnapshotTransport(
        FullPathGitHubFactory owner) :
        IActionHostReviewedSnapshotTransport
    {
        public Task<ActionHostGitObjectResult<
            ActionHostGitHubPullRequestFact>> GetCurrentPullRequestAsync(
                string repositoryName,
                long pullRequestNumber,
                CancellationToken cancellationToken)
        {
            owner.CurrentPullRequestCalls++;
            owner.OnCurrentPullRequest?.Invoke();
            return Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>.Success(
                    owner.CurrentPullRequestFact?.Invoke(
                        owner.CurrentPullRequestCalls) ??
                        owner.pullRequest,
                    64));
        }

        public Task<ActionHostGitObjectResult<
            ActionHostPullRequestFilePageObject>>
            GetPullRequestFilesAsync(
                string repositoryName,
                long pullRequestNumber,
                int page,
                int perPage,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>.Success(
                    new(
                        owner.withInlineFile
                            ? [new ActionHostPullRequestFileObject(
                                owner.FileBlob,
                                "file.txt",
                                null,
                                "added",
                                Encoding.UTF8.GetString(owner.FileBytes).TrimEnd('\n').Split('\n').Length,
                                0,
                                Encoding.UTF8.GetString(owner.FileBytes).TrimEnd('\n').Split('\n').Length,
                                owner.patch)]
                            : [],
                        IsComplete: true),
                    64));

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitCommitObject>.Success(
                    new(
                        commitSha,
                        commitSha == owner.pullRequest.BaseSha
                            ? BaseRoot
                            : ReviewedRoot),
                    64));

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
            CancellationToken cancellationToken) =>
            Task.FromResult(ActionHostGitObjectResult<
                ActionHostGitTreeObject>.Success(
                    new(
                        treeSha,
                        owner.withInlineFile && treeSha != BaseRoot
                            ? [new(
                                "file.txt",
                                "100644",
                                "blob",
                                owner.FileBlob,
                                owner.FileBytes.LongLength)]
                            : []),
                    64));

        public async Task<ActionHostGitObjectResult<
            ActionHostStreamedBlobObject>> CopyBlobObjectAsync(
                string repositoryName,
                string blobSha,
                long declaredSize,
                Stream destination,
                CancellationToken cancellationToken)
        {
            if (!owner.withInlineFile || blobSha != owner.FileBlob ||
                declaredSize != owner.FileBytes.LongLength)
            {
                return ActionHostGitObjectResult<
                    ActionHostStreamedBlobObject>.Failed(
                        ActionHostGitObjectFailure.InvalidRequest);
            }

            await destination.WriteAsync(owner.FileBytes, cancellationToken);
            return ActionHostGitObjectResult<
                ActionHostStreamedBlobObject>.Success(
                    new(owner.FileBlob, owner.FileBytes.LongLength),
                    checked((int)owner.FileBytes.LongLength));
        }

        public void Dispose() { }
    }
}
