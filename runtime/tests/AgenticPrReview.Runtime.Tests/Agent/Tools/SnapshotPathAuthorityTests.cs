using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed partial class AgentToolsTests
{
    [Fact]
    public async Task AdvancedSnapshotDoesNotInheritHistoricalReadOrSearchPathAuthority()
    {
        var files = new FakeFileAccess(new Dictionary<string, byte[]>
        {
            ["removed.txt"] = "x old\n"u8.ToArray(),
            ["retained.txt"] = "x current\n"u8.ToArray(),
        });
        var previous = new SnapshotToolExecutor(
            new ReviewedSnapshot(
                Identity, Directory.GetCurrentDirectory(), ["removed.txt", "retained.txt"]),
            files);
        var currentIdentity = Identity with { HeadSha = new string('2', 40) };
        var current = new SnapshotToolExecutor(
            new ReviewedSnapshot(
                currentIdentity, Directory.GetCurrentDirectory(), ["retained.txt"]),
            files);
        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"removed.txt\"}", out var readRemoved));
        Assert.True(AgentToolArguments.TrySearchText(
            "{\"query\":\"x\",\"path\":\"removed.txt\"}", out var searchRemoved));
        PreparedAgentToolCall[] historicalCalls =
        [
            new PreparedReadFileCall("read-old", readRemoved!),
            new PreparedSearchTextCall("search-old", searchRemoved!),
        ];
        foreach (var call in historicalCalls)
        {
            Assert.Null(previous.Preflight(call));
            var observed = await previous.ExecuteAsync(call, CancellationToken.None);
            Assert.True(observed.Succeeded);
            Assert.Equal(Identity, observed.Observation!.Identity);
            var readsBeforeRejection = files.ReadCount;

            Assert.Equal(AgentFailureCodes.ToolPathNotTracked, current.Preflight(call));
            var rejected = await current.ExecuteAsync(call, CancellationToken.None);
            Assert.Equal(AgentFailureCodes.ToolPathNotTracked, rejected.FailureCode);
            Assert.Null(rejected.Observation);
            Assert.Equal(readsBeforeRejection, files.ReadCount);
        }

        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"retained.txt\"}", out var readRetained));
        var retainedCall = new PreparedReadFileCall("read-current", readRetained!);
        Assert.Null(current.Preflight(retainedCall));
        var retained = await current.ExecuteAsync(retainedCall, CancellationToken.None);
        Assert.True(retained.Succeeded);
        Assert.Equal(currentIdentity, retained.Observation!.Identity);

        Assert.True(AgentToolArguments.TrySearchText("{\"query\":\"x\"}", out var searchAll));
        var searchCall = new PreparedSearchTextCall("search-current", searchAll!);
        Assert.Null(current.Preflight(searchCall));
        var searched = await current.ExecuteAsync(searchCall, CancellationToken.None);
        Assert.True(searched.Succeeded);
        Assert.Equal(currentIdentity, searched.Observation!.Identity);
        Assert.Equal(["retained.txt"], searched.Observation.ReturnedLines.Keys);
        Assert.DoesNotContain("removed.txt", searched.ResultJson);
    }
}
