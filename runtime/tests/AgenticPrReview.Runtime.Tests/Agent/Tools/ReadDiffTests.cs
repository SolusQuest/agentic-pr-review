using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class ReadDiffTests
{
    private static readonly ReviewedIdentity Identity = new(
        "repo",
        1,
        new string('0', 40),
        new string('1', 40));

    [Theory]
    [InlineData(
        "{\"path\":\"a.txt\"}",
        "{\"path\":\"a.txt\",\"start_hunk\":1,\"hunk_count\":20}")]
    [InlineData(
        "{\"path\":\"a.txt\",\"start_hunk\":2}",
        "{\"path\":\"a.txt\",\"start_hunk\":2,\"hunk_count\":20}")]
    [InlineData(
        "{\"path\":\"a.txt\",\"hunk_count\":3}",
        "{\"path\":\"a.txt\",\"start_hunk\":1,\"hunk_count\":3}")]
    [InlineData(
        "{\"path\":\"a.txt\",\"start_hunk\":2147483647,\"hunk_count\":20}",
        "{\"path\":\"a.txt\",\"start_hunk\":2147483647,\"hunk_count\":20}")]
    public void ArgumentsHaveFourProviderShapesAndOneDurableShape(
        string input,
        string expectedCanonical)
    {
        Assert.True(AgentToolArguments.TryReadDiff(input, out var arguments));
        Assert.Equal(
            expectedCanonical,
            Encoding.UTF8.GetString(arguments!.CanonicalBytes));
        Assert.True(AgentToolArguments.TryReadDiff(
            expectedCanonical,
            out var canonical));
        Assert.Equal(arguments.Path, canonical!.Path);
        Assert.Equal(arguments.StartHunk, canonical.StartHunk);
        Assert.Equal(arguments.HunkCount, canonical.HunkCount);
        Assert.Equal(arguments.CanonicalBytes, canonical.CanonicalBytes);
    }

    [Theory]
    [InlineData("{\"path\":\"a.txt\",\"start_hunk\":null}")]
    [InlineData("{\"path\":\"a.txt\",\"hunk_count\":null}")]
    [InlineData("{\"path\":\"a.txt\",\"path\":\"a.txt\"}")]
    [InlineData("{\"path\":\"a.txt\",\"unknown\":1}")]
    [InlineData("{ \"path\":\"a.txt\"}")]
    [InlineData("{\"path\":\"\\u0061.txt\"}")]
    [InlineData("{\"start_hunk\":1,\"path\":\"a.txt\"}")]
    [InlineData("{\"path\":\"a.txt\",\"hunk_count\":1,\"start_hunk\":1}")]
    [InlineData("{\"path\":\"../a.txt\"}")]
    [InlineData("{\"path\":\"a.txt\",\"start_hunk\":0}")]
    [InlineData("{\"path\":\"a.txt\",\"start_hunk\":1.0}")]
    [InlineData("{\"path\":\"a.txt\",\"hunk_count\":0}")]
    [InlineData("{\"path\":\"a.txt\",\"hunk_count\":21}")]
    [InlineData("{\"path\":\"a.txt\",\"hunk_count\":\"1\"}")]
    public void ArgumentsRejectOpenNullReorderedAndNoncanonicalForms(string input)
    {
        Assert.False(AgentToolArguments.TryReadDiff(input, out _));
    }

    [Fact]
    public void ArgumentAdmissionUsesStrictUtf8AndTheOrdinaryByteCap()
    {
        Assert.False(AgentToolArguments.TryReadDiff(
            "{\"path\":\"\ud800\"}",
            out _));
        Assert.False(AgentToolArguments.TryReadDiff(
            "{\"path\":\"" +
                new string('x', AgentLimits.ToolArgumentsBytes) +
                "\"}",
            out _));
    }

    [Fact]
    public void MixedResultHasIndependentCanonicalAndObservationOracles()
    {
        var hunk = new ReviewedDiffHunk(
            1,
            2,
            1,
            2,
            [
                new("context", 1, 1, "same"),
                new("deletion", 2, null, "gone"),
                new("addition", null, 2, "new"),
                new("no_newline", null, null, ""),
            ]);
        var result = new ReadDiffResult(
            "ok",
            Identity,
            "new.txt",
            new string('a', 64),
            true,
            1,
            20,
            1,
            1,
            [hunk],
            false,
            null,
            null);
        const string expectedPreimage =
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"new.txt\",\"patch_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"source_truncated\":true,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":1,\"returned_end_hunk\":1,\"hunks\":[{\"old_start\":1,\"old_count\":2,\"new_start\":1,\"new_count\":2,\"lines\":[{\"kind\":\"context\",\"old_line\":1,\"new_line\":1,\"text\":\"same\"},{\"kind\":\"deletion\",\"old_line\":2,\"new_line\":null,\"text\":\"gone\"},{\"kind\":\"addition\",\"old_line\":null,\"new_line\":2,\"text\":\"new\"},{\"kind\":\"no_newline\",\"old_line\":null,\"new_line\":null,\"text\":\"\"}]}],\"truncated\":false,\"next_start_hunk\":null}";
        const string expectedObservationId =
            "152ce9e3cbcdb5eac293ce5536131b0918c0cdac2773675913463525820e8e39";
        const string expectedCanonical =
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"new.txt\",\"patch_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"source_truncated\":true,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":1,\"returned_end_hunk\":1,\"hunks\":[{\"old_start\":1,\"old_count\":2,\"new_start\":1,\"new_count\":2,\"lines\":[{\"kind\":\"context\",\"old_line\":1,\"new_line\":1,\"text\":\"same\"},{\"kind\":\"deletion\",\"old_line\":2,\"new_line\":null,\"text\":\"gone\"},{\"kind\":\"addition\",\"old_line\":null,\"new_line\":2,\"text\":\"new\"},{\"kind\":\"no_newline\",\"old_line\":null,\"new_line\":null,\"text\":\"\"}]}],\"truncated\":false,\"next_start_hunk\":null,\"observation_id\":\"152ce9e3cbcdb5eac293ce5536131b0918c0cdac2773675913463525820e8e39\"}";

        Assert.Equal(
            expectedPreimage,
            Encoding.UTF8.GetString(ReadDiffResultWriter.Write(
                result,
                includeObservationId: false)));
        Assert.Equal(
            expectedObservationId,
            AgentCanonical.HashDomain(
                AgentCanonical.ReadDiffObservationDomain,
                Encoding.UTF8.GetBytes(expectedPreimage)));
        Assert.Equal(
            expectedCanonical,
            Encoding.UTF8.GetString(ReadDiffResultWriter.Write(
                result with { ObservationId = expectedObservationId })));
    }

    [Fact]
    public void EmptyPageStatusesHaveIndependentCanonicalOracles()
    {
        AssertEmptyPageOracle(
            "empty",
            new string('a', 64),
            sourceTruncated: true,
            "{\"status\":\"empty\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"patch_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"source_truncated\":true,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":null,\"returned_end_hunk\":null,\"hunks\":[],\"truncated\":false,\"next_start_hunk\":null}",
            "ee23a8944a1b1f0968f4d530ed6ed796bdca3b246b621bf2d79f0b70f66c865b");
        AssertEmptyPageOracle(
            "eof",
            new string('a', 64),
            sourceTruncated: false,
            "{\"status\":\"eof\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"patch_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"source_truncated\":false,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":null,\"returned_end_hunk\":null,\"hunks\":[],\"truncated\":false,\"next_start_hunk\":null}",
            "e528611626fa177d4ee7881db4cb99b67711d9728f62352de01270a139430367");
        AssertEmptyPageOracle(
            "unavailable",
            null,
            sourceTruncated: false,
            "{\"status\":\"unavailable\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"patch_sha256\":null,\"source_truncated\":false,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":null,\"returned_end_hunk\":null,\"hunks\":[],\"truncated\":false,\"next_start_hunk\":null}",
            "5b622a3b7d7ee8a7616d5fbb53bdf9c8a26922f1f3da09ad30c35423ada2b1c9");
        AssertEmptyPageOracle(
            "binary",
            null,
            sourceTruncated: false,
            "{\"status\":\"binary\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"patch_sha256\":null,\"source_truncated\":false,\"requested_start_hunk\":1,\"requested_hunk_count\":20,\"returned_start_hunk\":null,\"returned_end_hunk\":null,\"hunks\":[],\"truncated\":false,\"next_start_hunk\":null}",
            "1d45c7109a7ef6e92021e511484df718b1a89925ed6b9081e445ee1450994b44");
    }

    [Fact]
    public async Task ChangedPathAuthorityIsIndependentOfTrackedPostimageAuthority()
    {
        var removedSource = new ReviewedDiffSource(
            Identity,
            "removed.txt",
            null,
            "removed",
            false,
            [new ReviewedDiffHunk(
                1,
                1,
                0,
                0,
                [new("deletion", 1, null, "gone")])]);
        var snapshot = Snapshot(
            ["unchanged.txt", "new.txt"],
            [
                Available(removedSource),
                Unavailable("new.txt", "old.txt", "renamed"),
            ],
            [removedSource]);
        var executor = Executor(snapshot);

        var removed = await ExecuteAsync(snapshot, "{\"path\":\"removed.txt\"}");
        Assert.Equal("ok", Status(removed));
        var renamed = await ExecuteAsync(snapshot, "{\"path\":\"new.txt\"}");
        Assert.Equal("unavailable", Status(renamed));

        foreach (var call in new[] { "unchanged.txt", "old.txt" }
            .Select(path => Call("{\"path\":\"" + path + "\"}")))
        {
            Assert.Equal(
                AgentFailureCodes.ToolPathNotTracked,
                executor.Preflight(call));
            var failure = await executor.ExecuteAsync(call, CancellationToken.None);
            Assert.Equal(AgentFailureCodes.ToolPathNotTracked, failure.FailureCode);
        }
    }

    [Fact]
    public async Task StatusPrecedenceCoversEmptyEofUnavailableAndBinary()
    {
        var emptySource = new ReviewedDiffSource(
            Identity,
            "empty.txt",
            null,
            "modified",
            true,
            []);
        var snapshot = Snapshot(
            ["empty.txt", "unavailable.txt", "binary.txt"],
            [
                Available(emptySource, additions: 1),
                Unavailable("unavailable.txt", null, "modified"),
                new ReviewedChangedFile(
                    "binary.txt",
                    null,
                    "modified",
                    0,
                    0,
                    0,
                    "binary",
                    null,
                    false),
            ],
            [emptySource]);

        var emptyAtFirst = await ExecuteAsync(
            snapshot,
            "{\"path\":\"empty.txt\",\"start_hunk\":1,\"hunk_count\":1}");
        AssertResultShape(
            emptyAtFirst,
            "empty",
            patchPresent: true,
            sourceTruncated: true);

        var emptyAtMaximum = await ExecuteAsync(
            snapshot,
            "{\"path\":\"empty.txt\",\"start_hunk\":2147483647,\"hunk_count\":1}");
        AssertResultShape(
            emptyAtMaximum,
            "empty",
            patchPresent: true,
            sourceTruncated: true);

        var unavailable = await ExecuteAsync(
            snapshot,
            "{\"path\":\"unavailable.txt\",\"start_hunk\":2147483647,\"hunk_count\":1}");
        AssertResultShape(
            unavailable,
            "unavailable",
            patchPresent: false,
            sourceTruncated: false);

        var binary = await ExecuteAsync(
            snapshot,
            "{\"path\":\"binary.txt\",\"start_hunk\":9,\"hunk_count\":1}");
        AssertResultShape(binary, "binary", patchPresent: false, sourceTruncated: false);

        var nonemptySource = Source(
            "nonempty.txt",
            false,
            [ContextHunk(1)]);
        var eofSnapshot = Snapshot(
            ["nonempty.txt"],
            [Available(nonemptySource)],
            [nonemptySource]);
        var eof = await ExecuteAsync(
            eofSnapshot,
            "{\"path\":\"nonempty.txt\",\"start_hunk\":2147483647,\"hunk_count\":20}");
        AssertResultShape(eof, "eof", patchPresent: true, sourceTruncated: false);
    }

    [Fact]
    public async Task PaginationEnumeratesAllTwoHundredHunksWithoutGaps()
    {
        var source = Source(
            "a.txt",
            false,
            Enumerable.Range(1, AgentLimits.DiffHunksPerFile)
                .Select(ContextHunk));
        var snapshot = Snapshot(["a.txt"], [Available(source)], [source]);
        var starts = new List<int>();
        var next = 1;
        do
        {
            var execution = await ExecuteAsync(
                snapshot,
                "{\"path\":\"a.txt\",\"start_hunk\":" + next + "}");
            using var document = JsonDocument.Parse(execution.CanonicalResult!);
            var root = document.RootElement;
            Assert.Equal("ok", root.GetProperty("status").GetString());
            var start = root.GetProperty("returned_start_hunk").GetInt32();
            var end = root.GetProperty("returned_end_hunk").GetInt32();
            starts.AddRange(Enumerable.Range(start, end - start + 1));
            next = root.GetProperty("next_start_hunk").ValueKind == JsonValueKind.Null
                ? 0
                : root.GetProperty("next_start_hunk").GetInt32();
        }
        while (next != 0);

        Assert.Equal(Enumerable.Range(1, AgentLimits.DiffHunksPerFile), starts);
        Assert.Equal(starts.Count, starts.Distinct().Count());
    }

    [Fact]
    public async Task RequestedCountOneProducesRepeatedSingleHunkPages()
    {
        var source = Source(
            "a.txt",
            false,
            Enumerable.Range(1, 3).Select(ContextHunk));
        var snapshot = Snapshot(["a.txt"], [Available(source)], [source]);

        for (var start = 1; start <= 3; start++)
        {
            var execution = await ExecuteAsync(
                snapshot,
                "{\"path\":\"a.txt\",\"start_hunk\":" + start +
                    ",\"hunk_count\":1}");
            using var document = JsonDocument.Parse(execution.CanonicalResult!);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("hunks").GetArrayLength());
            Assert.Equal(start, root.GetProperty("returned_start_hunk").GetInt32());
            Assert.Equal(start, root.GetProperty("returned_end_hunk").GetInt32());
            Assert.Equal(start < 3, root.GetProperty("truncated").GetBoolean());
            int? expectedNext = start < 3 ? start + 1 : null;
            int? actualNext =
                root.GetProperty("next_start_hunk").ValueKind == JsonValueKind.Null
                    ? null
                    : root.GetProperty("next_start_hunk").GetInt32();
            Assert.Equal(expectedNext, actualNext);
        }
    }

    [Fact]
    public async Task CountAndBytePaginationKeepHunksAtomic()
    {
        var small = ContextHunk(1);
        var large = new ReviewedDiffHunk(
            2,
            0,
            2,
            8,
            Enumerable.Range(2, 8)
                .Select(line => new ReviewedDiffLine(
                    "addition",
                    null,
                    line,
                    new string('x', AgentLimits.DiffLineTextBytes))));
        var source = Source("a.txt", false, [small, large]);
        var snapshot = Snapshot(
            ["a.txt"],
            [Available(source)],
            [source]);
        var execution = await ExecuteAsync(snapshot, "{\"path\":\"a.txt\"}");
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("hunks").GetArrayLength());
        Assert.True(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, root.GetProperty("next_start_hunk").GetInt32());
        Assert.True(execution.CanonicalResult!.Length <= AgentLimits.ToolResultBytes);

        var countLimited = await ExecuteAsync(
            Snapshot(
                ["many.txt"],
                [Available(Source(
                    "many.txt",
                    false,
                    Enumerable.Range(1, 21).Select(ContextHunk)))],
                [Source(
                    "many.txt",
                    false,
                    Enumerable.Range(1, 21).Select(ContextHunk))]),
            "{\"path\":\"many.txt\",\"hunk_count\":20}");
        using var countDocument = JsonDocument.Parse(countLimited.CanonicalResult!);
        Assert.Equal(20, countDocument.RootElement.GetProperty("hunks").GetArrayLength());
        Assert.Equal(21, countDocument.RootElement.GetProperty("next_start_hunk").GetInt32());
    }

    [Fact]
    public void FirstOversizedHunkAndFixedEnvelopeProduceNoObservation()
    {
        var large = new ReviewedDiffHunk(
            0,
            0,
            1,
            9,
            Enumerable.Range(1, 9)
                .Select(line => new ReviewedDiffLine(
                    "addition",
                    null,
                    line,
                    new string('x', AgentLimits.DiffLineTextBytes))));
        var source = Source("a.txt", false, [large]);
        var executor = Executor(Snapshot(
            ["a.txt"],
            [Available(source)],
            [source]));
        var call = Call("{\"path\":\"a.txt\"}");

        var oversized = executor.ExecuteReadDiffWithLimit(
            call,
            CancellationToken.None,
            AgentLimits.ToolResultBytes);
        Assert.Equal(AgentFailureCodes.ToolResultLimit, oversized.FailureCode);
        Assert.Null(oversized.ResultJson);
        Assert.Null(oversized.Observation);

        var fixedEnvelope = executor.ExecuteReadDiffWithLimit(
            call,
            CancellationToken.None,
            1);
        Assert.Equal(AgentFailureCodes.ToolResultLimit, fixedEnvelope.FailureCode);
        Assert.Null(fixedEnvelope.Observation);
    }

    [Fact]
    public async Task ObservationContainsOnlyReturnedContextAndAdditionLines()
    {
        var source = Source(
            "a.txt",
            true,
            [
                new ReviewedDiffHunk(
                    1,
                    2,
                    1,
                    2,
                    [
                        new("context", 1, 1, "context"),
                        new("deletion", 2, null, "deleted"),
                        new("addition", null, 2, "added"),
                        new("no_newline", null, null, ""),
                    ]),
                ContextHunk(3),
            ]);
        var execution = await ExecuteAsync(
            Snapshot(["a.txt"], [Available(source)], [source]),
            "{\"path\":\"a.txt\",\"hunk_count\":1}");
        var observation = execution.Observation!;
        using var document = JsonDocument.Parse(execution.CanonicalResult!);

        Assert.True(
            document.RootElement.GetProperty("source_truncated").GetBoolean());
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.True(observation.Grounds(Evidence(observation, 1, 2)));
        Assert.False(observation.Grounds(Evidence(observation, 3, 3)));
        Assert.False(observation.Grounds(new AgentEvidence(
            observation.ObservationId,
            "other.txt",
            1,
            1)));

        var deletionOnly = Source(
            "deleted.txt",
            false,
            [new ReviewedDiffHunk(
                1,
                1,
                0,
                0,
                [new("deletion", 1, null, "gone")])]);
        var deletedExecution = await ExecuteAsync(
            Snapshot(["deleted.txt"], [Available(deletionOnly)], [deletionOnly]),
            "{\"path\":\"deleted.txt\"}");
        Assert.Empty(deletedExecution.Observation!.ReturnedLines);
    }

    [Fact]
    public async Task ListAndReadDiffExposeTheSameSourceIdentity()
    {
        var source = Source("a.txt", true, [ContextHunk(1), ContextHunk(2)]);
        var snapshot = Snapshot(["a.txt"], [Available(source)], [source]);
        Assert.True(AgentToolArguments.TryListChangedFiles(
            "{}",
            out var listArguments));
        var listCall = new PreparedListChangedFilesCall("list", listArguments!);
        var list = await Executor(snapshot).ExecuteAsync(
            listCall,
            CancellationToken.None);
        var diff = await ExecuteAsync(
            snapshot,
            "{\"path\":\"a.txt\",\"hunk_count\":1}");
        using var listDocument = JsonDocument.Parse(list.CanonicalResult!);
        using var diffDocument = JsonDocument.Parse(diff.CanonicalResult!);
        var change = listDocument.RootElement.GetProperty("changes")[0];
        var result = diffDocument.RootElement;

        Assert.Equal(
            change.GetProperty("path").GetString(),
            result.GetProperty("path").GetString());
        Assert.Equal(
            change.GetProperty("patch_sha256").GetString(),
            result.GetProperty("patch_sha256").GetString());
        Assert.Equal(
            change.GetProperty("source_truncated").GetBoolean(),
            result.GetProperty("source_truncated").GetBoolean());
        Assert.Equal(
            listDocument.RootElement.GetProperty("reviewed_identity").GetRawText(),
            result.GetProperty("reviewed_identity").GetRawText());
        var returnedPageSource = Source("a.txt", true, [ContextHunk(1)]);
        Assert.NotEqual(
            returnedPageSource.PatchSha256,
            result.GetProperty("patch_sha256").GetString());
    }

    [Fact]
    public void ResultAdmissionRejectsSemanticAndObservationMutations()
    {
        var hunk = ContextHunk(1);
        var arguments = Call("{\"path\":\"a.txt\",\"hunk_count\":1}").Arguments;
        var good = new ReadDiffResult(
            "ok",
            Identity,
            "a.txt",
            new string('a', 64),
            false,
            1,
            1,
            1,
            1,
            [hunk],
            false,
            null,
            null);
        Assert.True(TryAdmit(arguments, good, [1]));
        Assert.False(TryAdmit(arguments, good with { Status = "empty" }, []));
        Assert.False(TryAdmit(arguments, good with { PatchSha256 = null }, [1]));
        Assert.False(TryAdmit(arguments, good with { ReturnedStartHunk = 2 }, [1]));
        Assert.False(TryAdmit(arguments, good with
        {
            Truncated = true,
            NextStartHunk = 3,
        }, [1]));
        Assert.False(TryAdmit(arguments, good, [1, 2]));

        var preimage = Encoding.UTF8.GetString(ReadDiffResultWriter.Write(
            good,
            includeObservationId: false));
        var malformed = preimage.Replace(
            "\"new_line\":1",
            "\"new_line\":2",
            StringComparison.Ordinal);
        Assert.False(TryAdmitRaw(arguments, malformed));
    }

    [Fact]
    public async Task CancellationPrecedesPageMaterialization()
    {
        var source = Source("a.txt", false, [ContextHunk(1)]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Executor(Snapshot(["a.txt"], [Available(source)], [source]))
                .ExecuteAsync(Call("{\"path\":\"a.txt\"}"), cancellation.Token));
    }

    private static AgentEvidence Evidence(
        AgentObservation observation,
        int start,
        int end) =>
        new(observation.ObservationId, "a.txt", start, end);

    private static bool TryAdmit(
        ReadDiffArguments arguments,
        ReadDiffResult result,
        IEnumerable<int> returnedLines)
    {
        var preimage = ReadDiffResultWriter.Write(
            result with { ObservationId = null },
            includeObservationId: false);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            preimage);
        var signed = result with { ObservationId = observationId };
        var canonical = ReadDiffResultWriter.Write(signed);
        var lineSet = returnedLines.ToImmutableHashSet();
        var returned = lineSet.Count == 0
            ? ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
            : ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add(result.Path, lineSet);
        var execution = new AgentToolExecution(
            true,
            null,
            Encoding.UTF8.GetString(canonical),
            canonical,
            new AgentObservation(observationId, result.ReviewedIdentity, returned));
        return AgentToolResultAdmission.TryAdmit(
            new PreparedReadDiffCall("call", arguments),
            Identity,
            execution,
            out _,
            out _);
    }

    private static bool TryAdmitRaw(
        ReadDiffArguments arguments,
        string preimage)
    {
        var preimageBytes = Encoding.UTF8.GetBytes(preimage);
        var observationId = AgentCanonical.HashDomain(
            AgentCanonical.ReadDiffObservationDomain,
            preimageBytes);
        var canonical = Encoding.UTF8.GetBytes(
            preimage[..^1] +
            ",\"observation_id\":\"" + observationId + "\"}");
        var execution = new AgentToolExecution(
            true,
            null,
            Encoding.UTF8.GetString(canonical),
            canonical,
            new AgentObservation(
                observationId,
                Identity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal)));
        return AgentToolResultAdmission.TryAdmit(
            new PreparedReadDiffCall("call", arguments),
            Identity,
            execution,
            out _,
            out _);
    }

    private static void AssertResultShape(
        AgentToolExecution execution,
        string expectedStatus,
        bool patchPresent,
        bool sourceTruncated)
    {
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        var root = document.RootElement;
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(
            patchPresent ? JsonValueKind.String : JsonValueKind.Null,
            root.GetProperty("patch_sha256").ValueKind);
        Assert.Equal(
            sourceTruncated,
            root.GetProperty("source_truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("returned_start_hunk").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("returned_end_hunk").ValueKind);
        Assert.Empty(root.GetProperty("hunks").EnumerateArray());
        Assert.False(root.GetProperty("truncated").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("next_start_hunk").ValueKind);
        Assert.Empty(execution.Observation!.ReturnedLines);
    }

    private static void AssertEmptyPageOracle(
        string status,
        string? patchSha256,
        bool sourceTruncated,
        string expectedPreimage,
        string expectedObservationId)
    {
        var result = new ReadDiffResult(
            status,
            Identity,
            "a.txt",
            patchSha256,
            sourceTruncated,
            1,
            20,
            null,
            null,
            [],
            false,
            null,
            null);
        Assert.Equal(
            expectedPreimage,
            Encoding.UTF8.GetString(ReadDiffResultWriter.Write(
                result,
                includeObservationId: false)));
        Assert.Equal(
            expectedObservationId,
            AgentCanonical.HashDomain(
                AgentCanonical.ReadDiffObservationDomain,
                Encoding.UTF8.GetBytes(expectedPreimage)));
        Assert.Equal(
            expectedPreimage[..^1] +
                ",\"observation_id\":\"" + expectedObservationId + "\"}",
            Encoding.UTF8.GetString(ReadDiffResultWriter.Write(
                result with { ObservationId = expectedObservationId })));
    }

    private static string Status(AgentToolExecution execution)
    {
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        return document.RootElement.GetProperty("status").GetString()!;
    }

    private static async Task<AgentToolExecution> ExecuteAsync(
        ReviewedSnapshot snapshot,
        string input)
    {
        var call = Call(input);
        var execution = await Executor(snapshot).ExecuteAsync(
            call,
            CancellationToken.None);
        Assert.True(execution.Succeeded, execution.FailureCode);
        Assert.True(AgentToolResultAdmission.TryAdmit(
            call,
            Identity,
            execution,
            out _,
            out _));
        return execution;
    }

    private static PreparedReadDiffCall Call(string input)
    {
        Assert.True(AgentToolArguments.TryReadDiff(input, out var arguments));
        return new PreparedReadDiffCall("call", arguments!);
    }

    private static ReviewedSnapshot Snapshot(
        IEnumerable<string> tracked,
        IEnumerable<ReviewedChangedFile> changes,
        IEnumerable<ReviewedDiffSource> sources) =>
        new(
            Identity,
            Directory.GetCurrentDirectory(),
            tracked,
            changes,
            sources);

    private static SnapshotToolExecutor Executor(ReviewedSnapshot snapshot) =>
        new(snapshot, new ThrowingFileAccess());

    private static ReviewedDiffSource Source(
        string path,
        bool truncated,
        IEnumerable<ReviewedDiffHunk> hunks) =>
        new(Identity, path, null, "modified", truncated, hunks);

    private static ReviewedDiffHunk ContextHunk(int line) =>
        new(
            line,
            1,
            line,
            1,
            [new ReviewedDiffLine("context", line, line, string.Empty)]);

    private static ReviewedChangedFile Available(
        ReviewedDiffSource source,
        int? additions = null,
        int? deletions = null)
    {
        var added = additions ?? source.RepresentedAdditions;
        var deleted = deletions ?? source.RepresentedDeletions;
        return new ReviewedChangedFile(
            source.Path,
            source.PreviousPath,
            source.Status,
            added,
            deleted,
            added + deleted,
            "available",
            source.PatchSha256,
            source.SourceTruncated);
    }

    private static ReviewedChangedFile Unavailable(
        string path,
        string? previous,
        string status) =>
        new(path, previous, status, 0, 0, 0, "unavailable", null, false);

    private sealed class ThrowingFileAccess : IReviewedFileAccess
    {
        public ReviewedFileMetadata InspectMetadata(
            ReviewedSnapshot snapshot,
            string path) =>
            throw new InvalidOperationException("read_diff must not inspect files");

        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path) =>
            throw new InvalidOperationException("read_diff must not probe files");

        public ValueTask<ReviewedFileRead> ReadAsync(
            ReviewedSnapshot snapshot,
            string path,
            ReviewedFileProbe expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("read_diff must not read files");
    }
}
