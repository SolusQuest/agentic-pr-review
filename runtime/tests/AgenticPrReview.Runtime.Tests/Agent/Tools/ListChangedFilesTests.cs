using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class ListChangedFilesTests
{
    private static readonly ReviewedIdentity Identity = new(
        "repo",
        1,
        new string('0', 40),
        new string('1', 40));

    public static TheoryData<string, string> LifecyclePatchCases
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var status in new[]
                {
                    "added",
                    "removed",
                    "modified",
                    "renamed",
                    "copied",
                    "changed",
                })
            {
                foreach (var patch in new[] { "available", "unavailable", "binary" })
                {
                    data.Add(status, patch);
                }
            }

            return data;
        }
    }

    [Theory]
    [InlineData("{}", "{\"after\":null}")]
    [InlineData("{\"after\":\"a.txt\"}", "{\"after\":\"a.txt\"}")]
    public void ArgumentsHaveTwoProviderShapesAndOneDurableShape(
        string input,
        string expectedCanonical)
    {
        Assert.True(AgentToolArguments.TryListChangedFiles(input, out var arguments));
        Assert.Equal(expectedCanonical, Encoding.UTF8.GetString(arguments!.CanonicalBytes));
        Assert.True(AgentToolArguments.TryListChangedFilesCanonical(
            expectedCanonical,
            out var canonical));
        Assert.Equal(arguments.After, canonical!.After);
    }

    [Theory]
    [InlineData("{\"after\":null}")]
    [InlineData("{\"after\":\"a\",\"after\":\"a\"}")]
    [InlineData("{\"unknown\":1}")]
    [InlineData("{ \"after\":\"a\"}")]
    [InlineData("{\"after\":\"\\u0061\"}")]
    [InlineData("{\"after\":\"../a\"}")]
    [InlineData("{} ")]
    public void ProviderArgumentsRejectNullOpenAndNoncanonicalForms(string input)
    {
        Assert.False(AgentToolArguments.TryListChangedFiles(input, out _));
    }

    [Fact]
    public void CanonicalHistoryRequiresTheExplicitNullableProperty()
    {
        Assert.True(AgentToolArguments.TryListChangedFilesCanonical(
            "{\"after\":null}",
            out _));
        Assert.False(AgentToolArguments.TryListChangedFilesCanonical("{}", out _));
        Assert.False(AgentToolArguments.TryListChangedFilesCanonical(
            "{\"after\":null,\"after\":null}",
            out _));
        Assert.False(AgentToolArguments.TryListChangedFiles(
            "{\"after\":\"\ud800\"}",
            out _));
        Assert.False(AgentToolArguments.TryListChangedFiles(
            "{\"after\":\"" +
                new string('x', AgentLimits.ToolArgumentsBytes) +
                "\"}",
            out _));
    }

    [Theory]
    [MemberData(nameof(LifecyclePatchCases))]
    public void EveryLifecycleStatusAcceptsEveryPatchStatus(
        string status,
        string patchStatus)
    {
        var path = status == "removed" ? "old.txt" : "new.txt";
        var previous = status is "renamed" or "copied" ? "old.txt" : null;
        var source = patchStatus == "available"
            ? new ReviewedDiffSource(
                Identity,
                path,
                previous,
                status,
                false,
                [])
            : null;
        var change = new ReviewedChangedFile(
            path,
            previous,
            status,
            0,
            0,
            0,
            patchStatus,
            source?.PatchSha256,
            false);
        var tracked = status == "removed" ? Array.Empty<string>() : [path];

        var snapshot = Snapshot(
            tracked,
            [change],
            source is null ? [] : [source]);

        Assert.Equal(change, Assert.Single(snapshot.OrderedChangedFiles));
    }

    [Fact]
    public void RenameSwapsAndCopiesFromADeletedSourceAreValidWithoutAuthority()
    {
        var changes = new[]
        {
            Unavailable("a.txt", "b.txt", "renamed"),
            Unavailable("b.txt", "a.txt", "renamed"),
            Unavailable("source.txt", null, "removed"),
            Unavailable("copy-1.txt", "source.txt", "copied"),
            Unavailable("copy-2.txt", "source.txt", "copied"),
        };
        var snapshot = Snapshot(
            ["a.txt", "b.txt", "copy-1.txt", "copy-2.txt"],
            changes,
            []);

        Assert.Equal(
            changes.Select(change => change.Path).Order(StringComparer.Ordinal),
            snapshot.OrderedChangedFiles.Select(change => change.Path));
        Assert.False(snapshot.Contains("source.txt"));
        Assert.False(snapshot.Contains("old-only.txt"));

        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"source.txt\"}",
            out var read));
        var executor = Executor(snapshot);
        Assert.Equal(
            AgentFailureCodes.ToolPathNotTracked,
            executor.Preflight(new PreparedReadFileCall("read", read!)));
    }

    [Fact]
    public void SnapshotRejectsLifecycleCountAndPatchIncoherence()
    {
        var valid = Unavailable("a.txt", null, "modified");
        _ = Snapshot(
            ["a.txt"],
            [valid with { Additions = 1_000_000, Changes = 1_000_000 }],
            []);
        _ = Snapshot(
            ["a.txt"],
            [valid with { Additions = 500_000, Deletions = 500_000, Changes = 1_000_000 }],
            []);
        Assert.Throws<ArgumentException>(() => Snapshot([], [valid], []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { Status = "removed" }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { Status = "unchanged" }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { Additions = -1 }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { Additions = 500_001, Deletions = 500_000, Changes = 1_000_001 }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { Additions = 1, Changes = 0 }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { PatchStatus = "available", PatchSha256 = new string('A', 64) }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [valid with { PatchStatus = "binary", SourceTruncated = true }],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [Unavailable("a.txt", "a.txt", "renamed")],
            []));
    }

    [Fact]
    public void SnapshotEnforcesOneToOneSourceIdentityAndHashCoherence()
    {
        var source = SourceWithCounts("a.txt", false, 1, 1);
        var change = Available(source, 1, 1);
        _ = Snapshot(["a.txt"], [change], [source]);

        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change with { PatchSha256 = new string('f', 64) }],
            [source]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change],
            []));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [Unavailable("a.txt", null, "modified")],
            [source]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change],
            [source, source]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change],
            [new ReviewedDiffSource(
                Identity with { ReviewTarget = 2 },
                "a.txt",
                null,
                "modified",
                false,
                source.Hunks)]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change],
            [new ReviewedDiffSource(
                Identity,
                "a.txt",
                null,
                "changed",
                false,
                source.Hunks)]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [change with { SourceTruncated = true }],
            [source]));
        var renamedSource = new ReviewedDiffSource(
            Identity,
            "a.txt",
            "other.txt",
            "renamed",
            false,
            []);
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [new ReviewedChangedFile(
                "a.txt",
                "old.txt",
                "renamed",
                0,
                0,
                0,
                "available",
                renamedSource.PatchSha256,
                false)],
            [renamedSource]));
        var orphan = new ReviewedDiffSource(
            Identity,
            "orphan.txt",
            null,
            "modified",
            false,
            []);
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt", "orphan.txt"],
            [change],
            [source, orphan]));
    }

    [Fact]
    public void CompleteAndTruncatedSourceCountsUseExactAndLowerBoundRules()
    {
        var complete = SourceWithCounts("a.txt", false, 1, 1);
        _ = Snapshot(["a.txt"], [Available(complete, 1, 1)], [complete]);
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [Available(complete, 2, 1)],
            [complete]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [Available(complete, 0, 1)],
            [complete]));

        var truncated = SourceWithCounts("b.txt", true, 1, 1);
        _ = Snapshot(["b.txt"], [Available(truncated, 1, 1)], [truncated]);
        _ = Snapshot(["b.txt"], [Available(truncated, 2, 3)], [truncated]);
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["b.txt"],
            [Available(truncated, 0, 1)],
            [truncated]));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["b.txt"],
            [Available(truncated, 1, 0)],
            [truncated]));
    }

    [Fact]
    public void ChangedCountAndMetadataCapsAreExactAndInputsAreCopied()
    {
        var twoHundred = Enumerable.Range(0, AgentLimits.ChangedFiles)
            .Select(index => Unavailable($"f/{index:D3}.txt", null, "modified"))
            .ToList();
        var tracked = twoHundred.Select(change => change.Path).ToArray();
        var accepted = Snapshot(tracked, twoHundred, []);
        twoHundred.Clear();
        Assert.Equal(AgentLimits.ChangedFiles, accepted.OrderedChangedFiles.Length);

        Assert.Throws<ArgumentException>(() => Snapshot(
            Enumerable.Range(0, AgentLimits.ChangedFiles + 1)
                .Select(index => $"f/{index:D3}.txt"),
            Enumerable.Range(0, AgentLimits.ChangedFiles + 1)
                .Select(index => Unavailable($"f/{index:D3}.txt", null, "modified")),
            []));

        var exact = SizedMetadata(AgentLimits.ChangedFilesMetadataBytes);
        Assert.Equal(
            AgentLimits.ChangedFilesMetadataBytes,
            exact.Sum(change => ReviewedChangedFileWriter.Write(change).Length));
        _ = Snapshot(exact.Select(change => change.Path), exact, []);

        var over = SizedMetadata(AgentLimits.ChangedFilesMetadataBytes + 1);
        Assert.Throws<ArgumentException>(() => Snapshot(
            over.Select(change => change.Path),
            over,
            []));
    }

    [Fact]
    public void SourceCountDuplicateAndNullInputsFailBeforeAdmission()
    {
        var sources = Enumerable.Range(0, AgentLimits.ChangedFiles)
            .Select(index => new ReviewedDiffSource(
                Identity,
                $"f/{index:D3}.txt",
                null,
                "modified",
                false,
                []))
            .ToArray();
        var changes = sources.Select(source => Available(source, 0, 0)).ToArray();
        _ = Snapshot(sources.Select(source => source.Path), changes, sources);

        Assert.Throws<ArgumentException>(() => Snapshot(
            sources.Select(source => source.Path),
            changes,
            PoisonAfter(sources.Append(sources[0]))));
        Assert.Throws<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [
                Unavailable("a.txt", null, "modified"),
                Unavailable("a.txt", null, "modified"),
            ],
            []));
        Assert.ThrowsAny<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [null!],
            []));
        Assert.ThrowsAny<ArgumentException>(() => Snapshot(
            ["a.txt"],
            [Unavailable("a.txt", null, "modified")],
            [null!]));
    }

    [Fact]
    public void DiffSourcePerFileAndAggregateByteCapsAreExact()
    {
        var perFile = SizedSource("single.txt", AgentLimits.DiffSourceBytesPerFile);
        Assert.Equal(AgentLimits.DiffSourceBytesPerFile, perFile.CanonicalBytes.Length);
        _ = Snapshot(
            [perFile.Path],
            [Available(perFile, 128, 0)],
            [perFile]);
        Assert.Throws<ArgumentException>(() =>
            SizedSource("single.txt", AgentLimits.DiffSourceBytesPerFile + 1));

        var exactSources = SizedSources(AgentLimits.DiffSnapshotBytes);
        _ = Snapshot(
            exactSources.Select(source => source.Path),
            exactSources.Select(source => Available(source, 128, 0)),
            exactSources);
        var overSources = SizedSources(AgentLimits.DiffSnapshotBytes + 1);
        Assert.Throws<ArgumentException>(() => Snapshot(
            overSources.Select(source => source.Path),
            overSources.Select(source => Available(source, 128, 0)),
            overSources));
    }

    [Fact]
    public void EmptyResultAndCompleteResultHaveIndependentCanonicalOracles()
    {
        var change = Unavailable("a.txt", null, "modified");
        var preimage = new ListChangedFilesResult(
            "ok",
            Identity,
            null,
            [change],
            false,
            null,
            null);
        const string expectedPreimage =
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"after\":null,\"changes\":[{\"path\":\"a.txt\",\"previous_path\":null,\"status\":\"modified\",\"additions\":0,\"deletions\":0,\"changes\":0,\"patch_status\":\"unavailable\",\"patch_sha256\":null,\"source_truncated\":false}],\"truncated\":false,\"next_after\":null}";
        const string observationId =
            "6a4f3664b643f671fddf54c9b103b3677ad294071a73b595df711dea1c71c144";
        var expectedFinal =
            expectedPreimage[..^1] +
            ",\"observation_id\":\"" + observationId + "\"}";

        Assert.Equal(
            expectedPreimage,
            Encoding.UTF8.GetString(ListChangedFilesResultWriter.Write(
                preimage,
                includeObservationId: false)));
        Assert.Equal(
            expectedFinal,
            Encoding.UTF8.GetString(ListChangedFilesResultWriter.Write(
                preimage with { ObservationId = observationId })));
    }

    [Fact]
    public async Task RepeatedPagesEnumerateTwoHundredRecordsExactlyOnce()
    {
        var changes = Enumerable.Range(0, AgentLimits.ChangedFiles)
            .Select(index => Unavailable($"f/{index:D3}.txt", null, "modified"))
            .Reverse()
            .ToArray();
        var snapshot = Snapshot(changes.Select(change => change.Path), changes, []);
        var actual = new List<string>();
        string? after = null;
        do
        {
            var input = after is null ? "{}" : "{\"after\":\"" + after + "\"}";
            var execution = await ExecuteAsync(snapshot, input);
            using var document = JsonDocument.Parse(execution.CanonicalResult!);
            actual.AddRange(document.RootElement.GetProperty("changes")
                .EnumerateArray()
                .Select(change => change.GetProperty("path").GetString()!));
            after = document.RootElement.GetProperty("next_after").ValueKind ==
                JsonValueKind.Null
                    ? null
                    : document.RootElement.GetProperty("next_after").GetString();
        }
        while (after is not null);

        Assert.Equal(
            changes.Select(change => change.Path).Order(StringComparer.Ordinal),
            actual);
        Assert.Equal(actual.Count, actual.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task EmptyEofAndUnicodeOrderingAreDeterministic()
    {
        var empty = await ExecuteAsync(Snapshot([], [], []), "{}");
        Assert.Empty(ReadPaths(empty));

        var paths = new[] { "z", "é", "中", "aa", "ä" };
        var changes = paths
            .Select(path => Unavailable(path, null, "modified"))
            .ToArray();
        var snapshot = Snapshot(paths, changes, []);
        var page = await ExecuteAsync(snapshot, "{}");
        Assert.Equal(paths.Order(StringComparer.Ordinal), ReadPaths(page));

        var final = paths.Order(StringComparer.Ordinal).Last();
        var eof = await ExecuteAsync(
            snapshot,
            "{\"after\":\"" + final + "\"}");
        Assert.Empty(ReadPaths(eof));
        using var document = JsonDocument.Parse(eof.CanonicalResult!);
        Assert.False(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            JsonValueKind.Null,
            document.RootElement.GetProperty("next_after").ValueKind);
    }

    [Fact]
    public async Task RemovedCurrentPathIsAValidCursorButOtherPathsAreNot()
    {
        var changes = new[]
        {
            Unavailable("a-removed.txt", null, "removed"),
            Unavailable("b-new.txt", "historical.txt", "renamed"),
        };
        var snapshot = Snapshot(["b-new.txt", "tracked-unchanged.txt"], changes, []);
        var executor = Executor(snapshot);

        var first = await ExecuteAsync(snapshot, "{}");
        Assert.Contains("a-removed.txt", ReadPaths(first));
        var afterRemoved = await ExecuteAsync(
            snapshot,
            "{\"after\":\"a-removed.txt\"}");
        Assert.Equal(["b-new.txt"], ReadPaths(afterRemoved));

        foreach (var invalid in new[] { "tracked-unchanged.txt", "historical.txt" })
        {
            Assert.True(AgentToolArguments.TryListChangedFiles(
                "{\"after\":\"" + invalid + "\"}",
                out var arguments));
            var call = new PreparedListChangedFilesCall("invalid", arguments!);
            Assert.Equal(AgentFailureCodes.ToolCursorInvalid, executor.Preflight(call));
            var execution = await executor.ExecuteAsync(call, CancellationToken.None);
            Assert.Equal(AgentFailureCodes.ToolCursorInvalid, execution.FailureCode);
        }
    }

    [Fact]
    public async Task ByteAndCountCapsUseWholeRecordsAndTheFinalIdShape()
    {
        var longChanges = Enumerable.Range(0, 100)
            .Select(index => Unavailable(
                $"p/{index:D3}-" + new string('x', 900),
                null,
                "modified"))
            .ToArray();
        var execution = await ExecuteAsync(
            Snapshot(longChanges.Select(change => change.Path), longChanges, []),
            "{}");
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        var returned = document.RootElement.GetProperty("changes");
        Assert.InRange(execution.CanonicalResult!.Length, 1, AgentLimits.ToolResultBytes);
        Assert.InRange(returned.GetArrayLength(), 1, 99);
        Assert.True(document.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(
            returned[returned.GetArrayLength() - 1].GetProperty("path").GetString(),
            document.RootElement.GetProperty("next_after").GetString());

        var shortChanges = Enumerable.Range(0, 101)
            .Select(index => Unavailable($"s/{index:D3}", null, "modified"))
            .ToArray();
        var countLimited = await ExecuteAsync(
            Snapshot(shortChanges.Select(change => change.Path), shortChanges, []),
            "{}");
        Assert.Equal(AgentLimits.ListChangedFilesEntries, ReadPaths(countLimited).Length);
    }

    [Fact]
    public void DefensiveFixedEnvelopeAndFirstRecordOverflowMapToResultLimit()
    {
        var change = Unavailable("a.txt", null, "modified");
        var snapshot = Snapshot([change.Path], [change], []);
        var executor = Executor(snapshot);
        Assert.True(AgentToolArguments.TryListChangedFiles("{}", out var arguments));
        var call = new PreparedListChangedFilesCall("call", arguments!);
        var empty = new ListChangedFilesResult(
            "ok",
            Identity,
            null,
            [],
            false,
            null,
            new string('0', 64));
        var emptyLength = ListChangedFilesResultWriter.Write(empty).Length;

        Assert.Equal(
            AgentFailureCodes.ToolResultLimit,
            executor.ExecuteListChangedFilesWithLimit(
                call,
                CancellationToken.None,
                emptyLength - 1).FailureCode);
        Assert.Equal(
            AgentFailureCodes.ToolResultLimit,
            executor.ExecuteListChangedFilesWithLimit(
                call,
                CancellationToken.None,
                emptyLength).FailureCode);
    }

    [Fact]
    public async Task ListingUsesOnlyImmutableMetadataAndObservationsGroundNoLines()
    {
        var change = Unavailable("a.txt", null, "modified");
        var snapshot = Snapshot([change.Path], [change], []);
        var execution = await ExecuteAsync(snapshot, "{}");

        Assert.True(execution.Succeeded);
        Assert.Empty(execution.Observation!.ReturnedLines);
        Assert.False(execution.Observation.Grounds(new AgentEvidence(
            execution.Observation.ObservationId,
            "a.txt",
            1,
            1)));
    }

    [Fact]
    public async Task CancellationIsObservedBeforePageMaterialization()
    {
        var change = Unavailable("a.txt", null, "modified");
        var snapshot = Snapshot([change.Path], [change], []);
        Assert.True(AgentToolArguments.TryListChangedFiles("{}", out var arguments));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await Executor(snapshot).ExecuteAsync(
                new PreparedListChangedFilesCall("call", arguments!),
                cancellation.Token));
    }

    [Fact]
    public void ResultAdmissionRejectsRecomputedSemanticMutations()
    {
        Assert.True(AgentToolArguments.TryListChangedFiles(
            "{\"after\":\"a.txt\"}",
            out var arguments));
        var call = new PreparedListChangedFilesCall("call", arguments!);
        var good = new ListChangedFilesResult(
            "ok",
            Identity,
            "a.txt",
            [Unavailable("b.txt", null, "modified")],
            false,
            null,
            null);
        Assert.True(TryAdmit(call, good));

        Assert.False(TryAdmit(call, good with { Status = "error" }));
        Assert.False(TryAdmit(call, good with { After = null }));
        Assert.False(TryAdmit(call, good with
        {
            ReviewedIdentity = Identity with { ReviewTarget = 2 },
        }));
        Assert.False(TryAdmit(call, good with
        {
            Changes = [Unavailable("a.txt", null, "modified")],
        }));
        Assert.False(TryAdmit(call, good with
        {
            Changes = [
                Unavailable("c.txt", null, "modified"),
                Unavailable("b.txt", null, "modified"),
            ],
        }));
        Assert.False(TryAdmit(call, good with
        {
            Changes = [good.Changes[0] with { Changes = 1 }],
        }));
        Assert.False(TryAdmit(call, good with
        {
            Changes = [good.Changes[0] with { PatchStatus = "binary", SourceTruncated = true }],
        }));
        Assert.False(TryAdmit(call, good with
        {
            Truncated = true,
            NextAfter = "other.txt",
        }));
        Assert.False(TryAdmit(call, good with
        {
            Changes = Enumerable.Range(0, AgentLimits.ListChangedFilesEntries + 1)
                .Select(index => Unavailable($"b/{index:D3}", null, "modified"))
                .ToImmutableArray(),
        }));
    }

    [Fact]
    public void ResultAdmissionRejectsObservationLineAuthority()
    {
        Assert.True(AgentToolArguments.TryListChangedFiles("{}", out var arguments));
        var call = new PreparedListChangedFilesCall("call", arguments!);
        var result = new ListChangedFilesResult(
            "ok",
            Identity,
            null,
            [],
            false,
            null,
            null);
        var preimage = ListChangedFilesResultWriter.Write(
            result,
            includeObservationId: false);
        var id = AgentCanonical.HashDomain(
            AgentCanonical.ListChangedFilesObservationDomain,
            preimage);
        var signed = result with { ObservationId = id };
        var canonical = ListChangedFilesResultWriter.Write(signed);
        var returned = ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add("a.txt", ImmutableHashSet.Create(1));
        var execution = new AgentToolExecution(
            true,
            null,
            Encoding.UTF8.GetString(canonical),
            canonical,
            new AgentObservation(id, Identity, returned));

        Assert.False(AgentToolResultAdmission.TryAdmit(
            call,
            Identity,
            execution,
            out _,
            out _));
    }

    private static bool TryAdmit(
        PreparedListChangedFilesCall call,
        ListChangedFilesResult result)
    {
        var preimage = ListChangedFilesResultWriter.Write(
            result with { ObservationId = null },
            includeObservationId: false);
        var id = AgentCanonical.HashDomain(
            AgentCanonical.ListChangedFilesObservationDomain,
            preimage);
        var signed = result with { ObservationId = id };
        var canonical = ListChangedFilesResultWriter.Write(signed);
        var execution = new AgentToolExecution(
            true,
            null,
            Encoding.UTF8.GetString(canonical),
            canonical,
            new AgentObservation(
                id,
                signed.ReviewedIdentity,
                ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
                    .WithComparers(StringComparer.Ordinal)));
        return AgentToolResultAdmission.TryAdmit(
            call,
            Identity,
            execution,
            out _,
            out _);
    }

    private static async Task<AgentToolExecution> ExecuteAsync(
        ReviewedSnapshot snapshot,
        string input)
    {
        Assert.True(AgentToolArguments.TryListChangedFiles(input, out var arguments));
        var call = new PreparedListChangedFilesCall("call", arguments!);
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

    private static string[] ReadPaths(AgentToolExecution execution)
    {
        using var document = JsonDocument.Parse(execution.CanonicalResult!);
        return document.RootElement.GetProperty("changes")
            .EnumerateArray()
            .Select(change => change.GetProperty("path").GetString()!)
            .ToArray();
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

    private static ReviewedChangedFile Unavailable(
        string path,
        string? previous,
        string status) =>
        new(path, previous, status, 0, 0, 0, "unavailable", null, false);

    private static ReviewedChangedFile Available(
        ReviewedDiffSource source,
        int additions,
        int deletions) =>
        new(
            source.Path,
            source.PreviousPath,
            source.Status,
            additions,
            deletions,
            additions + deletions,
            "available",
            source.PatchSha256,
            source.SourceTruncated);

    private static ReviewedDiffSource SourceWithCounts(
        string path,
        bool truncated,
        int additions,
        int deletions)
    {
        var lines = new List<ReviewedDiffLine>();
        for (var index = 0; index < deletions; index++)
        {
            lines.Add(new("deletion", index + 1, null, "d"));
        }

        for (var index = 0; index < additions; index++)
        {
            lines.Add(new("addition", null, index + 1, "a"));
        }

        var hunks = lines.Count == 0
            ? Array.Empty<ReviewedDiffHunk>()
            : [new ReviewedDiffHunk(1, deletions, 1, additions, lines)];
        return new ReviewedDiffSource(
            Identity,
            path,
            null,
            "modified",
            truncated,
            hunks);
    }

    private static List<ReviewedChangedFile> SizedMetadata(int target)
    {
        var changes = Enumerable.Range(0, 130)
            .Select(index => Unavailable(
                $"c/{index:D3}",
                $"p/{index:D3}",
                "copied"))
            .ToList();
        var remaining = target - changes.Sum(change =>
            ReviewedChangedFileWriter.Write(change).Length);
        Assert.True(remaining >= 0);
        for (var index = 0; index < changes.Count && remaining > 0; index++)
        {
            var change = changes[index];
            var pathRoom = AgentLimits.PathBytes - Encoding.UTF8.GetByteCount(change.Path);
            var pathPadding = Math.Min(pathRoom, remaining);
            change = change with { Path = change.Path + new string('x', pathPadding) };
            remaining -= pathPadding;
            var previousRoom = AgentLimits.PathBytes -
                Encoding.UTF8.GetByteCount(change.PreviousPath!);
            var previousPadding = Math.Min(previousRoom, remaining);
            change = change with
            {
                PreviousPath = change.PreviousPath + new string('x', previousPadding),
            };
            remaining -= previousPadding;
            changes[index] = change;
        }

        Assert.Equal(0, remaining);
        return changes;
    }

    private static ReviewedDiffSource SizedSource(string path, int target)
    {
        var emptyLines = Enumerable.Range(1, 128)
            .Select(index => new ReviewedDiffLine("addition", null, index, string.Empty))
            .ToArray();
        var baseline = new ReviewedDiffSource(
            Identity,
            path,
            null,
            "modified",
            false,
            [new ReviewedDiffHunk(0, 0, 1, 128, emptyLines)]);
        var remaining = target - baseline.CanonicalBytes.Length;
        Assert.True(remaining >= 0);
        var lines = new ReviewedDiffLine[emptyLines.Length];
        for (var index = 0; index < lines.Length; index++)
        {
            var padding = Math.Min(AgentLimits.DiffLineTextBytes, remaining);
            lines[index] = new ReviewedDiffLine(
                "addition",
                null,
                index + 1,
                new string('x', padding));
            remaining -= padding;
        }

        Assert.Equal(0, remaining);
        return new ReviewedDiffSource(
            Identity,
            path,
            null,
            "modified",
            false,
            [new ReviewedDiffHunk(0, 0, 1, 128, lines)]);
    }

    private static ReviewedDiffSource[] SizedSources(int target)
    {
        const int count = 17;
        var each = target / count;
        var remainder = target % count;
        return Enumerable.Range(0, count)
            .Select(index => SizedSource(
                $"large/{index:D2}.txt",
                each + (index < remainder ? 1 : 0)))
            .ToArray();
    }

    private static IEnumerable<T> PoisonAfter<T>(IEnumerable<T> values)
    {
        foreach (var value in values)
        {
            yield return value;
        }

        throw new InvalidOperationException("poison enumeration requested");
    }

    private sealed class ThrowingFileAccess : IReviewedFileAccess
    {
        public ReviewedFileMetadata InspectMetadata(
            ReviewedSnapshot snapshot,
            string path) =>
            throw new InvalidOperationException(
                "list_changed_files must not inspect files");

        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path) =>
            throw new InvalidOperationException(
                "list_changed_files must not probe files");

        public ValueTask<ReviewedFileRead> ReadAsync(
            ReviewedSnapshot snapshot,
            string path,
            ReviewedFileProbe expected,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "list_changed_files must not read files");
    }
}
