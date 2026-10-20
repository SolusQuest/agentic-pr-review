using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class CurrentPathAuthorityTests
{
    [Theory]
    [InlineData("read_file", "{\"path\":\"src/a.cs\"}", false)]
    [InlineData("search_text", "{\"query\":\"line\",\"path\":\"src/a.cs\"}", false)]
    [InlineData("read_diff", "{\"path\":\"src/a.cs\"}", true)]
    [InlineData("list_files", "{\"after\":\"src/a.cs\"}", false)]
    [InlineData("list_changed_files", "{\"after\":\"src/a.cs\"}", false)]
    public async Task AuthoredHistoricalPathCannotGrantCurrentSnapshotAuthority(
        string tool, string arguments, bool remainsTracked)
    {
        var identity = new ReviewedIdentity("repo", 1, new('0', 40), new('2', 40));
        var snapshot = new ReviewedSnapshot(identity, Directory.GetCurrentDirectory(),
            remainsTracked ? ["src/a.cs", "current.txt"] : ["current.txt"]);
        var files = new CurrentFileOnlyAccess();
        var executor = new SnapshotToolExecutor(snapshot, files);
        var rejected = Prepare("historical-name", tool, arguments);
        var code = executor.Preflight(rejected);
        Assert.Equal(tool is "list_files" or "list_changed_files"
            ? AgentFailureCodes.ToolCursorInvalid : AgentFailureCodes.ToolPathNotTracked, code);
        Assert.NotNull(AgentRecoveryFeedback.ForPreflight(rejected, code!));
        Assert.Equal(0, files.Reads);
        var currentList = Prepare("lookup-corrected", "list_files", "{}");
        Assert.Null(executor.Preflight(currentList));
        var listed = await executor.ExecuteAsync(currentList, default);
        Assert.True(AgentToolResultAdmission.TryAdmit(currentList, identity, listed, out _, out var listObservation));
        Assert.Equal(0, files.Reads);
        var currentRead = Prepare("read-corrected", "read_file", "{\"path\":\"current.txt\"}");
        Assert.Null(executor.Preflight(currentRead));
        var read = await executor.ExecuteAsync(currentRead, default);
        Assert.True(AgentToolResultAdmission.TryAdmit(currentRead, identity, read, out _, out var observation));
        Assert.Equal(1, files.Reads);
        var findings = ImmutableArray.Create(new AgentFinding("high", "bug", "fix",
            [new(observation.ObservationId, "current.txt", 1, 1)]));
        Assert.True(AgentToolArguments.TryFinishReviewProvider(
            Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("found", findings)), out var finish));
        Assert.True(TerminalReviewValidator.TryValidate(finish!, identity, [listObservation, observation], out var review));
        Assert.Equal("current.txt", Assert.Single(Assert.Single(review!.Findings).Evidence).Path);
        var historical = new AgentObservation(new('a', 64), identity with { HeadSha = new('1', 40) },
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty.Add("src/a.cs", [1]));
        var oldFinding = new AgentFinding("high", "old", "old", [new(historical.ObservationId, "src/a.cs", 1, 1)]);
        Assert.True(AgentToolArguments.TryFinishReviewProvider(
            Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview("old", [oldFinding])), out var oldFinish));
        Assert.False(TerminalReviewValidator.TryValidate(oldFinish!, identity,
            [historical, observation], out _, out var reason));
        Assert.Equal("evidence_identity_mismatch", reason);
    }

    private static PreparedAgentToolCall Prepare(string id, string name, string json) => name switch
    {
        "read_file" when AgentToolArguments.TryReadFileProvider(json, out var value) => new PreparedReadFileCall(id, value!),
        "read_diff" when AgentToolArguments.TryReadDiffProvider(json, out var value) => new PreparedReadDiffCall(id, value!),
        "search_text" when AgentToolArguments.TrySearchTextProvider(json, out var value) => new PreparedSearchTextCall(id, value!),
        "list_files" when AgentToolArguments.TryListFilesProvider(json, out var value) => new PreparedListFilesCall(id, value!),
        "list_changed_files" when AgentToolArguments.TryListChangedFilesProvider(json, out var value) => new PreparedListChangedFilesCall(id, value!),
        _ => throw new InvalidOperationException("authored_path_case_invalid"),
    };

    private sealed class CurrentFileOnlyAccess : IReviewedFileAccess
    {
        private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("line\n");
        internal int Reads { get; private set; }
        public ReviewedFileMetadata InspectMetadata(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length);
        }
        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
        {
            Assert.Equal("current.txt", path);
            return new(ReviewedFileAccessStatus.Success, Bytes.Length, new(1, 1));
        }
        public ValueTask<ReviewedFileRead> ReadAsync(ReviewedSnapshot snapshot, string path,
            ReviewedFileProbe expected, CancellationToken cancellationToken)
        {
            Assert.Equal("current.txt", path);
            Reads++;
            return ValueTask.FromResult(new ReviewedFileRead(ReviewedFileAccessStatus.Success, Bytes));
        }
    }
}
