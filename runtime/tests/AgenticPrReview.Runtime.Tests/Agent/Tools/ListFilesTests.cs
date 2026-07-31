using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class ListFilesTests
{
    private static readonly ReviewedIdentity Identity = new(
        "repo",
        1,
        new string('0', 40),
        new string('1', 40));

    [Theory]
    [InlineData("{}", "{\"prefix\":null,\"after\":null}")]
    [InlineData("{\"prefix\":\"p\"}", "{\"prefix\":\"p\",\"after\":null}")]
    [InlineData("{\"after\":\"p\"}", "{\"prefix\":null,\"after\":\"p\"}")]
    [InlineData(
        "{\"prefix\":\"p\",\"after\":\"q\"}",
        "{\"prefix\":\"p\",\"after\":\"q\"}")]
    public void ProviderArgumentsAdmitExactlyTheFourCanonicalShapes(
        string input,
        string expectedCanonical)
    {
        Assert.True(AgentToolArguments.TryListFiles(input, out var arguments));
        Assert.Equal(
            expectedCanonical,
            Encoding.UTF8.GetString(arguments!.CanonicalBytes));
        Assert.True(AgentToolArguments.TryListFilesCanonical(
            expectedCanonical,
            out var canonical));
        Assert.Equal(arguments.Prefix, canonical!.Prefix);
        Assert.Equal(arguments.After, canonical.After);
    }

    [Theory]
    [InlineData("{\"prefix\":null}")]
    [InlineData("{\"after\":null}")]
    [InlineData("{\"after\":\"q\",\"prefix\":\"p\"}")]
    [InlineData("{\"prefix\":\"p\",\"prefix\":\"p\"}")]
    [InlineData("{\"prefix\":\"p\",\"unknown\":1}")]
    [InlineData("{ \"prefix\":\"p\"}")]
    [InlineData("{\"prefix\":\"\\u0070\"}")]
    [InlineData("{\"prefix\":\"../p\"}")]
    public void ProviderArgumentsRejectNullOpenOrNonCanonicalShapes(string input)
    {
        Assert.False(AgentToolArguments.TryListFiles(input, out _));
    }

    [Fact]
    public void CanonicalHistoryRequiresBothExplicitNullableProperties()
    {
        Assert.True(AgentToolArguments.TryListFilesCanonical(
            "{\"prefix\":null,\"after\":null}",
            out _));
        Assert.False(AgentToolArguments.TryListFilesCanonical("{}", out _));
        Assert.False(AgentToolArguments.TryListFilesCanonical(
            "{\"prefix\":\"p\",\"after\":null,\"after\":null}",
            out _));
    }

    [Fact]
    public async Task EmptySnapshotProducesCanonicalMetadataOnlyObservation()
    {
        var (call, execution, _) = await ExecuteAsync([], "{}");

        Assert.True(execution.Succeeded);
        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"prefix\":null,\"after\":null,\"paths\":[],\"truncated\":false,\"next_after\":null,\"observation_id\":\"" +
                execution.Observation!.ObservationId +
                "\"}",
            execution.ResultJson);
        Assert.Empty(execution.Observation.ReturnedLines);
        Assert.False(execution.Observation.Grounds(
            new AgentEvidence(
                execution.Observation.ObservationId,
                "a.txt",
                1,
                1)));
        Assert.True(AgentToolResultAdmission.TryAdmit(
            call,
            Identity,
            execution,
            out _,
            out _));
    }

    [Fact]
    public async Task PrefixSelectionKeepsExactAndDescendantsAcrossLookalikes()
    {
        var tracked = new[]
        {
            "a/file.cs",
            "a",
            "a-other",
            "a0",
            "a/file.txt",
            "b",
        };

        var (_, execution, _) = await ExecuteAsync(
            tracked,
            "{\"prefix\":\"a\"}");

        Assert.Equal(
            ["a", "a/file.cs", "a/file.txt"],
            ReadPaths(execution));
    }

    [Fact]
    public async Task ExactPrefixCursorContinuesAtDescendantsPastLookalikes()
    {
        var tracked = new[] { "a", "a-other", "a/file.cs" };

        var (_, execution, executor) = await ExecuteAsync(
            tracked,
            "{\"prefix\":\"a\",\"after\":\"a\"}");

        Assert.Equal(["a/file.cs"], ReadPaths(execution));
        Assert.True(AgentToolArguments.TryListFiles(
            "{\"prefix\":\"a\",\"after\":\"a-other\"}",
            out var invalidArguments));
        Assert.Equal(
            AgentFailureCodes.ToolCursorInvalid,
            executor.Preflight(
                new PreparedListFilesCall("invalid", invalidArguments!)));
    }

    [Fact]
    public async Task RepeatedPagesEnumerateTheAllowlistExactlyOnce()
    {
        var tracked = Enumerable.Range(0, 205)
            .Select(index => $"src/{index:D3}.cs")
            .Reverse()
            .ToArray();
        var actual = new List<string>();
        string? after = null;

        do
        {
            var input = after is null
                ? "{\"prefix\":\"src\"}"
                : "{\"prefix\":\"src\",\"after\":\"" + after + "\"}";
            var (_, execution, _) = await ExecuteAsync(tracked, input);
            using var document = JsonDocument.Parse(execution.CanonicalResult!);
            actual.AddRange(document.RootElement.GetProperty("paths")
                .EnumerateArray()
                .Select(path => path.GetString()!));
            after = document.RootElement.GetProperty("next_after").ValueKind ==
                JsonValueKind.Null
                    ? null
                    : document.RootElement.GetProperty("next_after").GetString();
        }
        while (after is not null);

        Assert.Equal(
            tracked.Order(StringComparer.Ordinal),
            actual);
        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task ResultByteCapIncludesTheFinalObservationId()
    {
        var tracked = Enumerable.Range(0, 100)
            .Select(index => $"p/{index:D3}-" + new string('x', 900))
            .ToArray();

        var (_, execution, _) = await ExecuteAsync(
            tracked,
            "{\"prefix\":\"p\"}");
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        var paths = document.RootElement.GetProperty("paths");

        Assert.InRange(execution.CanonicalResult!.Length, 1, AgentLimits.ToolResultBytes);
        Assert.InRange(paths.GetArrayLength(), 1, 99);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            paths[paths.GetArrayLength() - 1].GetString(),
            document.RootElement.GetProperty("next_after").GetString());
    }

    [Fact]
    public async Task OrderingIsOrdinalForNonAsciiPaths()
    {
        var tracked = new[] { "z", "é", "中", "aa", "ä" };

        var (_, execution, _) = await ExecuteAsync(tracked, "{}");

        Assert.Equal(
            tracked.Order(StringComparer.Ordinal),
            ReadPaths(execution));
    }

    [Fact]
    public async Task InvalidCursorFailsPreflightAndDefensiveExecution()
    {
        Assert.True(AgentToolArguments.TryListFiles(
            "{\"prefix\":\"src\",\"after\":\"src/missing.cs\"}",
            out var arguments));
        var executor = CreateExecutor(["src/a.cs", "src/b.cs"]);
        var call = new PreparedListFilesCall("call", arguments!);

        Assert.Equal("tool_cursor_invalid", executor.Preflight(call));
        var execution = await executor.ExecuteAsync(call, CancellationToken.None);
        Assert.False(execution.Succeeded);
        Assert.Equal("tool_cursor_invalid", execution.FailureCode);
    }

    [Fact]
    public async Task ListingNeverUsesReviewedFileAccess()
    {
        Assert.True(AgentToolArguments.TryListFiles("{}", out var arguments));
        var executor = new SnapshotToolExecutor(
            new ReviewedSnapshot(
                Identity,
                Directory.GetCurrentDirectory(),
                ["a.txt"]),
            new ThrowingFileAccess());

        var execution = await executor.ExecuteAsync(
            new PreparedListFilesCall("call", arguments!),
            CancellationToken.None);

        Assert.True(execution.Succeeded);
        Assert.Equal(["a.txt"], ReadPaths(execution));
    }

    [Fact]
    public async Task CancellationIsObservedDuringPageMaterialization()
    {
        Assert.True(AgentToolArguments.TryListFiles("{}", out var arguments));
        var executor = CreateExecutor(
            Enumerable.Range(0, 200).Select(index => $"{index:D3}.txt"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(
                new PreparedListFilesCall("call", arguments!),
                cancellation.Token));
    }

    [Fact]
    public void SnapshotBoundsChargeRawEntriesBeforeDeduplication()
    {
        var accepted = new ReviewedSnapshot(
            Identity,
            Directory.GetCurrentDirectory(),
            Enumerable.Repeat("a", AgentLimits.TrackedFiles));
        Assert.Single(accepted.OrderedTrackedFiles);

        Assert.Throws<ArgumentException>(() => new ReviewedSnapshot(
            Identity,
            Directory.GetCurrentDirectory(),
            Enumerable.Repeat("a", AgentLimits.TrackedFiles + 1)));
    }

    [Fact]
    public void SnapshotMetadataByteBoundUsesCheckedUtf8Sum()
    {
        var path = new string('a', AgentLimits.PathBytes);
        var exactCount = AgentLimits.TrackedFilesMetadataBytes /
            AgentLimits.PathBytes;
        var accepted = new ReviewedSnapshot(
            Identity,
            Directory.GetCurrentDirectory(),
            Enumerable.Repeat(path, exactCount));
        Assert.Single(accepted.OrderedTrackedFiles);

        Assert.Throws<ArgumentException>(() => new ReviewedSnapshot(
            Identity,
            Directory.GetCurrentDirectory(),
            Enumerable.Repeat(path, exactCount + 1)));
    }

    [Fact]
    public void ResultAdmissionRejectsRecomputedCrossFieldInconsistencies()
    {
        Assert.True(AgentToolArguments.TryListFiles(
            "{\"prefix\":\"a\",\"after\":\"a/0\"}",
            out var arguments));
        var call = new PreparedListFilesCall("call", arguments!);
        var good = new ListFilesResult(
            "ok",
            Identity,
            "a",
            "a/0",
            ["a/1"],
            false,
            null,
            null);
        Assert.True(TryAdmit(call, good));

        Assert.False(TryAdmit(call, good with { Paths = ["b/1"] }));
        Assert.False(TryAdmit(call, good with { Paths = ["a/0"] }));
        Assert.False(TryAdmit(call, good with { Paths = ["a/2", "a/1"] }));
        Assert.False(TryAdmit(call, good with
        {
            Paths = Enumerable.Range(1, AgentLimits.ListFilesEntries + 1)
                .Select(index => $"a/{index:D3}")
                .ToImmutableArray(),
        }));
        Assert.False(TryAdmit(call, good with
        {
            Truncated = true,
            NextAfter = "a/other",
        }));
        Assert.False(TryAdmit(call, good with
        {
            ReviewedIdentity = Identity with { ReviewTarget = 2 },
        }));
    }

    [Fact]
    public void ResultWriterHasTheFrozenPropertyOrderAndNullableFields()
    {
        var result = new ListFilesResult(
            "ok",
            Identity,
            null,
            null,
            ["a"],
            true,
            "a",
            new string('f', 64));

        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"prefix\":null,\"after\":null,\"paths\":[\"a\"],\"truncated\":true,\"next_after\":\"a\",\"observation_id\":\"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"}",
            Encoding.UTF8.GetString(ListFilesResultWriter.Write(result)));
    }

    private static bool TryAdmit(
        PreparedListFilesCall call,
        ListFilesResult result)
    {
        var withoutIdentity = ListFilesResultWriter.Write(
            result with { ObservationId = null },
            includeObservationId: false);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ListFilesObservationDomain,
            withoutIdentity);
        var signed = result with { ObservationId = observationId };
        var canonical = ListFilesResultWriter.Write(signed);
        var observation = new AgentObservation(
            observationId,
            signed.ReviewedIdentity,
            ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal));
        var execution = new AgentToolExecution(
            true,
            null,
            Encoding.UTF8.GetString(canonical),
            canonical,
            observation);
        return AgentToolResultAdmission.TryAdmit(
            call,
            Identity,
            execution,
            out _,
            out _);
    }

    private static async Task<(
        PreparedListFilesCall Call,
        AgentToolExecution Execution,
        SnapshotToolExecutor Executor)> ExecuteAsync(
        IEnumerable<string> tracked,
        string argumentsJson)
    {
        Assert.True(AgentToolArguments.TryListFiles(
            argumentsJson,
            out var arguments));
        var executor = CreateExecutor(tracked);
        var call = new PreparedListFilesCall("call", arguments!);
        var execution = await executor.ExecuteAsync(
            call,
            CancellationToken.None);
        return (call, execution, executor);
    }

    private static string[] ReadPaths(AgentToolExecution execution)
    {
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        return document.RootElement.GetProperty("paths")
            .EnumerateArray()
            .Select(path => path.GetString()!)
            .ToArray();
    }

    private static SnapshotToolExecutor CreateExecutor(
        IEnumerable<string> tracked) =>
        new(
            new ReviewedSnapshot(
                Identity,
                Directory.GetCurrentDirectory(),
                tracked),
            new ThrowingFileAccess());

    private sealed class ThrowingFileAccess : IReviewedFileAccess
    {
        public ReviewedFileMetadata InspectMetadata(
            ReviewedSnapshot snapshot,
            string path) =>
            throw new InvalidOperationException("list_files must not inspect files");

        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path) =>
            throw new InvalidOperationException("list_files must not probe files");

        public ValueTask<ReviewedFileRead> ReadAsync(
            ReviewedSnapshot snapshot,
            string path,
            ReviewedFileProbe expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("list_files must not read files");
    }
}
