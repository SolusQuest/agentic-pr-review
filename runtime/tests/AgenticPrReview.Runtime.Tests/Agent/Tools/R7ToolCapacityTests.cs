using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed partial class AgentToolsTests
{
    [Fact]
    public async Task R7SnapshotReads800LinesAndReachesOmittedContentByRangeOrPath()
    {
        var root = Directory.CreateTempSubdirectory("apr-r7-c5-tools-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "p"));
            var paths = Enumerable.Range(0, 500).Select(index => $"p/{index:D3}.txt").ToArray();
            foreach (var path in paths)
            {
                await File.WriteAllTextAsync(Path.Combine(root.FullName, path), "ordinary\n", new UTF8Encoding(false));
            }
            var largeBytes = Encoding.UTF8.GetBytes(string.Concat(
                Enumerable.Range(1, 1800).Select(line => $"x-{line:D4}-" + new string('a', 42) + "\n")));
            Assert.True(largeBytes.Length > 64 * 1024);
            await File.WriteAllBytesAsync(Path.Combine(root.FullName, paths[0]), largeBytes);
            await File.WriteAllTextAsync(Path.Combine(root.FullName, paths[^1]), "late-marker\n", new UTF8Encoding(false));
            var snapshot = new ReviewedSnapshot(Identity, root.FullName, paths,
                paths.Select(path => new ReviewedChangedFile(path, null, "modified", 0, 0, 0, "unavailable", null, false)), []);
            Assert.Equal(500, snapshot.OrderedChangedFiles.Length);
            var executor = new SnapshotToolExecutor(snapshot, new VerifiedReviewedFileAccess());

            var first = await R7ExecuteReadAsync(executor, "first", paths[0], 1, 800);
            using var firstJson = JsonDocument.Parse(first.CanonicalResult!);
            Assert.Equal(800, firstJson.RootElement.GetProperty("lines").GetArrayLength());
            Assert.Equal("line_count", firstJson.RootElement.GetProperty("truncation_reason").GetString());
            Assert.InRange(first.CanonicalResult!.Length, 32 * 1024 + 1, 64 * 1024);
            Assert.Equal(AgentCanonical.HashRaw(largeBytes), firstJson.RootElement.GetProperty("raw_sha256").GetString());
            Assert.True(first.Observation!.Grounds(new AgentEvidence(first.Observation.ObservationId, paths[0], 1, 800)));
            Assert.False(first.Observation.Grounds(new AgentEvidence(first.Observation.ObservationId, paths[0], 801, 801)));
            var second = await R7ExecuteReadAsync(executor, "second", paths[0], 801, 800);
            using var secondJson = JsonDocument.Parse(second.CanonicalResult!);
            Assert.Equal(801, secondJson.RootElement.GetProperty("returned_start_line").GetInt32());
            Assert.Equal(1600, secondJson.RootElement.GetProperty("returned_end_line").GetInt32());
            Assert.True(second.Observation!.Grounds(new AgentEvidence(second.Observation.ObservationId, paths[0], 801, 1600)));

            var broad = await R7ExecuteSearchAsync(executor, "broad", "late-marker");
            using var broadJson = JsonDocument.Parse(broad.CanonicalResult!);
            Assert.Equal(100, broadJson.RootElement.GetProperty("files_scanned").GetInt32());
            Assert.Equal("files_scanned", broadJson.RootElement.GetProperty("truncation_reason").GetString());
            Assert.Empty(broad.Observation!.ReturnedLines);
            var late = await R7ExecuteSearchAsync(executor, "late", "late-marker", paths[^1]);
            Assert.True(late.Observation!.Grounds(new AgentEvidence(late.Observation.ObservationId, paths[^1], 1, 1)));

            var common = await R7ExecuteSearchAsync(executor, "common", "x-", paths[0]);
            using var commonJson = JsonDocument.Parse(common.CanonicalResult!);
            Assert.Equal(100, commonJson.RootElement.GetProperty("matches").GetArrayLength());
            Assert.Equal("matches", commonJson.RootElement.GetProperty("truncation_reason").GetString());
            Assert.False(common.Observation!.Grounds(new AgentEvidence(common.Observation.ObservationId, paths[0], 1800, 1800)));
            var narrow = await R7ExecuteSearchAsync(executor, "narrow", "x-1800", paths[0]);
            Assert.True(narrow.Observation!.Grounds(new AgentEvidence(narrow.Observation.ObservationId, paths[0], 1800, 1800)));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task R7ReadCanonicalByteBoundaryKeepsOversizedLinesAtomic()
    {
        const string args = "{\"path\":\"exact.txt\",\"line_count\":1}";
        var envelope = await ExecuteReadAsync("exact.txt", "\n"u8.ToArray(), args);
        Assert.True(envelope.Succeeded);
        var textLength = 65_536 - envelope.CanonicalResult!.Length;
        var exact = await ExecuteReadAsync("exact.txt", Encoding.UTF8.GetBytes(new string('x', textLength) + "\n"), args);
        Assert.True(exact.Succeeded);
        Assert.Equal(65_536, exact.CanonicalResult!.Length);
        Assert.True(exact.Observation!.Grounds(new AgentEvidence(exact.Observation.ObservationId, "exact.txt", 1, 1)));
        var plusOne = await ExecuteReadAsync("exact.txt", Encoding.UTF8.GetBytes(new string('x', textLength + 1) + "\n"), args);
        Assert.True(plusOne.Succeeded);
        using var document = JsonDocument.Parse(plusOne.CanonicalResult!);
        Assert.Empty(plusOne.Observation!.ReturnedLines);
        Assert.Equal("result_bytes", document.RootElement.GetProperty("truncation_reason").GetString());
        Assert.Empty(document.RootElement.GetProperty("lines").EnumerateArray());
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\\")]
    [InlineData("\u0001")]
    [InlineData("雪")]
    public async Task R7EscapedHugeLinesHaveNoEvidenceAndLaterLinesRemainReadable(string pattern)
    {
        var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(pattern, 70_000)) + "\nvisible\n");
        Assert.InRange(bytes.Length, 1, 1_048_576);
        var first = await ExecuteReadAsync("escape.txt", bytes, "{\"path\":\"escape.txt\"}");
        Assert.True(first.Succeeded);
        Assert.InRange(first.CanonicalResult!.Length, 1, 65_536);
        Assert.Empty(first.Observation!.ReturnedLines);
        using var firstJson = JsonDocument.Parse(first.CanonicalResult);
        Assert.Equal("result_bytes", firstJson.RootElement.GetProperty("truncation_reason").GetString());
        var later = await ExecuteReadAsync("escape.txt", bytes, "{\"path\":\"escape.txt\",\"start_line\":2}");
        Assert.True(later.Succeeded);
        Assert.True(later.Observation!.Grounds(new AgentEvidence(later.Observation.ObservationId, "escape.txt", 2, 2)));
    }

    [Fact]
    public async Task R7SearchByteTruncationAndRetainedFileCapRemainExplicit()
    {
        var wide = await ExecuteSearchAsync(["wide.txt"], new Dictionary<string, byte[]>
        {
            ["wide.txt"] = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(new string('x', 1000) + "\n", 100))),
        }, "wide.txt");
        Assert.True(wide.Succeeded);
        Assert.InRange(wide.CanonicalResult!.Length, 32 * 1024 + 1, 65_536);
        using var json = JsonDocument.Parse(wide.CanonicalResult);
        Assert.Equal("result_bytes", json.RootElement.GetProperty("truncation_reason").GetString());
        var count = json.RootElement.GetProperty("matches").GetArrayLength();
        Assert.InRange(count, 1, 99);
        Assert.False(wide.Observation!.Grounds(new AgentEvidence(wide.Observation.ObservationId, "wide.txt", count + 1, count + 1)));

        var bytes = Encoding.UTF8.GetBytes(new string('x', 256 * 1024) + "\nvisible\n");
        var access = new FakeFileAccess(new Dictionary<string, byte[]> { ["large.txt"] = bytes });
        var executor = CreateExecutor(["large.txt"], access);
        var read = await R7ExecuteReadAsync(executor, "read", "large.txt", 2, 1);
        Assert.True(read.Succeeded);
        var search = await R7ExecuteSearchAsync(executor, "search", "visible", "large.txt", expectSuccess: false);
        Assert.Equal("tool_file_too_large", search.FailureCode);
        Assert.Equal(1, access.ReadCount);
    }

    [Fact]
    public void R7ReadSchemaDefaultAndBoundaryAgree()
    {
        using var schema = JsonDocument.Parse(AgentToolRegistry.ReadFileSchema);
        Assert.Equal(800, schema.RootElement.GetProperty("properties").GetProperty("line_count").GetProperty("maximum").GetInt32());
        Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"a.txt\"}", out var defaulted));
        Assert.Equal(800, defaulted!.LineCount);
        Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"a.txt\",\"line_count\":800}", out _));
        Assert.False(AgentToolArguments.TryReadFile("{\"path\":\"a.txt\",\"line_count\":801}", out _));
        Assert.True(AgentToolArguments.TryReadFile("{\"path\":\"a.txt\",\"line_count\":400}", out _));
        Assert.False(AgentToolArguments.TrySearchText("{\"query\":\"x\",\"after\":\"a.txt\"}", out _));
    }

    private static async Task<AgentToolExecution> R7ExecuteReadAsync(
        SnapshotToolExecutor executor, string id, string path, int start, int count)
    {
        Assert.True(AgentToolArguments.TryReadFile(JsonSerializer.Serialize(new { path, start_line = start, line_count = count }), out var args));
        var result = await executor.ExecuteAsync(new PreparedReadFileCall(id, args!), CancellationToken.None);
        Assert.True(result.Succeeded, result.FailureCode);
        return result;
    }

    private static async Task<AgentToolExecution> R7ExecuteSearchAsync(
        SnapshotToolExecutor executor, string id, string query, string? path = null, bool expectSuccess = true)
    {
        var json = path is null ? JsonSerializer.Serialize(new { query }) : JsonSerializer.Serialize(new { query, path });
        Assert.True(AgentToolArguments.TrySearchText(json, out var args));
        var result = await executor.ExecuteAsync(new PreparedSearchTextCall(id, args!), CancellationToken.None);
        if (expectSuccess) Assert.True(result.Succeeded, result.FailureCode);
        return result;
    }
}
