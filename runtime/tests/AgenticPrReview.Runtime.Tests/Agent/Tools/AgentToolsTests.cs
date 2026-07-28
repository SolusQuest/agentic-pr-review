using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Tools;

public sealed class AgentToolsTests
{
    private static readonly ReviewedIdentity Identity = new(
        "repo",
        1,
        new string('0', 40),
        new string('1', 40));

    [Fact]
    public async Task ReadFileReproducesSuccessGolden()
    {
        var execution = await ExecuteReadAsync(
            "a.txt",
            "a\n"u8.ToArray(),
            "{\"path\":\"a.txt\"}");

        Assert.True(execution.Succeeded);
        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"raw_sha256\":\"87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7\",\"requested_start_line\":1,\"requested_line_count\":400,\"returned_start_line\":1,\"returned_end_line\":1,\"lines\":[{\"line\":1,\"text\":\"a\"}],\"truncated\":false,\"truncation_reason\":null,\"observation_id\":\"0976fa91a184d9a972b55b5ae937666030179d1204f131d66ec8146766714c19\"}",
            execution.ResultJson);
    }

    [Fact]
    public async Task ReadFileReproducesLineCountAndEmptyGoldens()
    {
        var truncated = await ExecuteReadAsync(
            "a.txt",
            "a\nb\n"u8.ToArray(),
            "{\"path\":\"a.txt\",\"start_line\":1,\"line_count\":1}");
        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"a.txt\",\"raw_sha256\":\"911169ddaaf146aff539f58c26c489af3b892dff0fe283c1c264c65ae5aa59a2\",\"requested_start_line\":1,\"requested_line_count\":1,\"returned_start_line\":1,\"returned_end_line\":1,\"lines\":[{\"line\":1,\"text\":\"a\"}],\"truncated\":true,\"truncation_reason\":\"line_count\",\"observation_id\":\"024511eb529cd04cc922235cb47d02a69ab1ff252cb384eee09ea5772a7ba8a2\"}",
            truncated.ResultJson);

        var empty = await ExecuteReadAsync(
            "empty.txt",
            [],
            "{\"path\":\"empty.txt\"}");
        Assert.Equal(
            "{\"status\":\"start_after_eof\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"path\":\"empty.txt\",\"raw_sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"requested_start_line\":1,\"requested_line_count\":400,\"returned_start_line\":null,\"returned_end_line\":null,\"lines\":[],\"truncated\":false,\"truncation_reason\":null,\"observation_id\":\"518e2251e8ff850e74ecb983b206824d7f2d3bc0c87585fff2e93f86f9e0ac99\"}",
            empty.ResultJson);
    }

    [Fact]
    public async Task ReadFileReturnsAValidEmptyObservationWhenFirstLineCannotFit()
    {
        var execution = await ExecuteReadAsync(
            "wide.txt",
            Encoding.UTF8.GetBytes(new string('x', 40_000)),
            "{\"path\":\"wide.txt\"}");

        Assert.True(execution.Succeeded);
        Assert.Contains(
            "\"returned_start_line\":null,\"returned_end_line\":null,\"lines\":[],\"truncated\":true,\"truncation_reason\":\"result_bytes\"",
            execution.ResultJson,
            StringComparison.Ordinal);
        Assert.NotNull(execution.Observation);
    }

    [Fact]
    public async Task ReadFileUsesSharedBomCrLfAndFinalLineSemantics()
    {
        var bytes = new byte[]
        {
            0xEF, 0xBB, 0xBF,
            (byte)'a', (byte)'\r', (byte)'\n',
            (byte)'b',
        };
        var execution = await ExecuteReadAsync(
            "bom.txt",
            bytes,
            "{\"path\":\"bom.txt\"}");

        Assert.True(execution.Succeeded);
        Assert.Contains(
            "\"lines\":[{\"line\":1,\"text\":\"a\"},{\"line\":2,\"text\":\"b\"}]",
            execution.ResultJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"raw_sha256\":\"" + AgentCanonical.HashRaw(bytes) + "\"",
            execution.ResultJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFileRejectsOversizeFromMetadataBeforeReading()
    {
        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"big.txt\"}",
            out var arguments));
        var access = new FakeFileAccess(
            new Dictionary<string, byte[]>
            {
                ["big.txt"] = new byte[AgentLimits.ReadFileRawBytes + 1],
            });
        var executor = CreateExecutor(["big.txt"], access);

        var result = await executor.ExecuteAsync(
            new PreparedReadFileCall("call", arguments!),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("tool_file_too_large", result.FailureCode);
        Assert.Equal(0, access.ReadCount);
    }

    [Fact]
    public async Task SearchTextReproducesEmptyAndOneMatchGoldens()
    {
        Assert.True(AgentToolArguments.TrySearchText(
            "{\"query\":\"x\"}",
            out var emptyArguments));
        var emptyExecutor = CreateExecutor(
            [],
            new FakeFileAccess(new Dictionary<string, byte[]>()));
        var empty = await emptyExecutor.ExecuteAsync(
            new PreparedSearchTextCall("call", emptyArguments!),
            CancellationToken.None);
        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"query_sha256\":\"d21ec26c10a02b1ece6c4f719d0c9ff6a1f5a5b0c013fede7becebc43e5594d7\",\"path\":null,\"files_scanned\":0,\"raw_bytes_scanned\":0,\"skipped_invalid_utf8\":0,\"skipped_binary\":0,\"skipped_lone_cr\":0,\"skipped_oversized\":0,\"matches\":[],\"truncated\":false,\"truncation_reason\":null,\"observation_id\":\"410ed0d3a219acd3aae2c67ab0486c8ec76820fa18bd451fe15a7f93b7183b21\"}",
            empty.ResultJson);

        Assert.True(AgentToolArguments.TrySearchText(
            "{\"query\":\"x\",\"path\":\"one.txt\"}",
            out var oneArguments));
        var oneExecutor = CreateExecutor(
            ["one.txt"],
            new FakeFileAccess(new Dictionary<string, byte[]>
            {
                ["one.txt"] = "x\n"u8.ToArray(),
            }));
        var one = await oneExecutor.ExecuteAsync(
            new PreparedSearchTextCall("call", oneArguments!),
            CancellationToken.None);
        Assert.Equal(
            "{\"status\":\"ok\",\"reviewed_identity\":{\"repository_id\":\"repo\",\"review_target\":1,\"base_sha\":\"0000000000000000000000000000000000000000\",\"head_sha\":\"1111111111111111111111111111111111111111\"},\"query_sha256\":\"d21ec26c10a02b1ece6c4f719d0c9ff6a1f5a5b0c013fede7becebc43e5594d7\",\"path\":\"one.txt\",\"files_scanned\":1,\"raw_bytes_scanned\":2,\"skipped_invalid_utf8\":0,\"skipped_binary\":0,\"skipped_lone_cr\":0,\"skipped_oversized\":0,\"matches\":[{\"path\":\"one.txt\",\"raw_sha256\":\"73cb3858a687a8494ca3323053016282f3dad39d42cf62ca4e79dda2aac7d9ac\",\"line\":1,\"text\":\"x\"}],\"truncated\":false,\"truncation_reason\":null,\"observation_id\":\"ac76d356538981f2b7b0d7eef6d5ec87241dfca6f0fe525c42b606318216d6ed\"}",
            one.ResultJson);
    }

    [Fact]
    public async Task SearchTextReproducesGeneratedTruncationGoldens()
    {
        var manyBytes = Encoding.UTF8.GetBytes(
            string.Concat(Enumerable.Repeat("x\n", 101)));
        var many = await ExecuteSearchAsync(
            ["many.txt"],
            new Dictionary<string, byte[]>
            {
                ["many.txt"] = manyBytes,
            },
            "many.txt");
        Assert.Equal(12_649, many.CanonicalResult!.Length);
        Assert.Contains(
            "\"truncation_reason\":\"matches\"",
            many.ResultJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"observation_id\":\"ad8dee1b98a68a9774ccd254cded6ae44c1156948a652f80a33132213d967251\"",
            many.ResultJson,
            StringComparison.Ordinal);
        Assert.Equal(
            "90db21ea0021a6c8d7df0d2684c36808d5da6630829838ce80c30c546d824396",
            AgentCanonical.HashRaw(many.CanonicalResult));

        var wideLine = new string('x', 500) + "\n";
        var wide = await ExecuteSearchAsync(
            ["wide.txt"],
            new Dictionary<string, byte[]>
            {
                ["wide.txt"] = Encoding.UTF8.GetBytes(
                    string.Concat(Enumerable.Repeat(wideLine, 100))),
            },
            "wide.txt");
        Assert.Equal(32_175, wide.CanonicalResult!.Length);
        Assert.Contains(
            "\"truncation_reason\":\"result_bytes\"",
            wide.ResultJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"observation_id\":\"16e6ad0bfe490a65817cdec1933bed30e12552e25fe05de9118432a1d9b0fde3\"",
            wide.ResultJson,
            StringComparison.Ordinal);
        Assert.Equal(
            "4e58cf6e2f5f7a87708105a09e332b1e9cf3ba517ef8ab9593a8e572f04f66df",
            AgentCanonical.HashRaw(wide.CanonicalResult));
    }

    [Fact]
    public async Task SearchTextReproducesSkipPrecedenceGolden()
    {
        var files = new Dictionary<string, byte[]>
        {
            ["a-big"] = new byte[AgentLimits.SearchFileBytes + 1],
            ["b-bin"] = [0],
            ["c-invalid"] = [0xFF],
            ["d-cr"] = [(byte)'x', (byte)'\r'],
            ["e-ok.txt"] = [(byte)'x', (byte)'\n'],
        };

        var result = await ExecuteSearchAsync(files.Keys, files);

        Assert.Equal(665, result.CanonicalResult!.Length);
        Assert.Contains(
            "\"files_scanned\":5,\"raw_bytes_scanned\":6,\"skipped_invalid_utf8\":1,\"skipped_binary\":1,\"skipped_lone_cr\":1,\"skipped_oversized\":1",
            result.ResultJson,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"observation_id\":\"21601beea265a40f896460e8964b73ab70d88a7c8710c8c527f8f8d28b184421\"",
            result.ResultJson,
            StringComparison.Ordinal);
        Assert.Equal(
            "dae6e8e576e612c17d1ea0dc10e54aec0b38d453a8ffc65656cfccfe1d3c0d96",
            AgentCanonical.HashRaw(result.CanonicalResult));
    }

    [Theory]
    [InlineData("{\"path\":\"a.txt\",\"start_line\":1,\"line_count\":400}", true)]
    [InlineData("{\"path\":\"a.txt\"}", true)]
    [InlineData("{\"path\":\"a.txt\",\"line_count\":2}", true)]
    [InlineData("{ \"path\":\"a.txt\"}", false)]
    [InlineData("{\"start_line\":1,\"path\":\"a.txt\"}", false)]
    [InlineData("{\"path\":\"a.txt\",\"path\":\"a.txt\"}", false)]
    [InlineData("{\"path\":\"a\\u002etxt\"}", false)]
    [InlineData("{\"path\":\"a.txt\",\"unknown\":1}", false)]
    [InlineData("{\"path\":\"a.txt\",\"start_line\":0}", false)]
    [InlineData("{\"path\":\"a.txt\",\"line_count\":401}", false)]
    public void ReadArgumentsAreClosedAndCanonical(string json, bool accepted)
    {
        Assert.Equal(
            accepted,
            AgentToolArguments.TryReadFile(json, out _));
    }

    [Theory]
    [InlineData("{\"query\":\"x\"}", true)]
    [InlineData("{\"query\":\"x\",\"path\":\"a.txt\"}", true)]
    [InlineData("{\"query\":\"x\",\"path\":null}", false)]
    [InlineData("{\"path\":\"a.txt\",\"query\":\"x\"}", false)]
    [InlineData("{\"query\":\"x\",\"query\":\"x\"}", false)]
    [InlineData("{\"query\":\"\\u0078\"}", false)]
    [InlineData("{\"query\":\" \\t\"}", false)]
    [InlineData("{\"query\":\"x\\n\"}", false)]
    public void SearchArgumentsAreClosedAndCanonical(string json, bool accepted)
    {
        Assert.Equal(
            accepted,
            AgentToolArguments.TrySearchText(json, out _));
    }

    [Theory]
    [InlineData("{\"summary\":\"done\",\"findings\":[]}", true)]
    [InlineData("{ \"summary\":\"done\",\"findings\":[]}", false)]
    [InlineData("{\"findings\":[],\"summary\":\"done\"}", false)]
    [InlineData("{\"summary\":\"d\\u006fne\",\"findings\":[]}", false)]
    [InlineData("{\"summary\":\"done\",\"findings\":[],\"extra\":0}", false)]
    [InlineData("{\"summary\":\"done\",\"summary\":\"done\",\"findings\":[]}", false)]
    [InlineData("{\"summary\":null,\"findings\":[]}", false)]
    public void TerminalArgumentsAreClosedAndCanonical(
        string json,
        bool accepted)
    {
        Assert.Equal(
            accepted,
            AgentToolArguments.TryFinishReview(json, out _));
    }

    [Theory]
    [InlineData("a.txt", true)]
    [InlineData("dir/@file", true)]
    [InlineData("", false)]
    [InlineData("/a", false)]
    [InlineData("C:/a", false)]
    [InlineData("https://example", false)]
    [InlineData("a\\b", false)]
    [InlineData("a//b", false)]
    [InlineData("a/./b", false)]
    [InlineData("a/../b", false)]
    [InlineData("a./b", false)]
    [InlineData("a /b", false)]
    [InlineData("a#b", false)]
    public void RepositoryPathsUseTheFrozenLexicalDomain(
        string path,
        bool accepted)
    {
        Assert.Equal(accepted, RepositoryPath.IsValid(path));
    }

    [Theory]
    [InlineData(new byte[] { 0 }, "tool_file_binary")]
    [InlineData(new byte[] { 0xFF }, "tool_file_invalid_utf8")]
    [InlineData(new byte[] { (byte)'x', (byte)'\r' }, "tool_file_lone_cr")]
    public async Task ExplicitSearchUsesStableFileClassification(
        byte[] bytes,
        string expectedCode)
    {
        Assert.True(AgentToolArguments.TrySearchText(
            "{\"query\":\"x\",\"path\":\"a.txt\"}",
            out var arguments));
        var executor = CreateExecutor(
            ["a.txt"],
            new FakeFileAccess(new Dictionary<string, byte[]>
            {
                ["a.txt"] = bytes,
            }));

        var result = await executor.ExecuteAsync(
            new PreparedSearchTextCall("call", arguments!),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, result.FailureCode);
        Assert.Null(result.Observation);
    }

    [Fact]
    public async Task UnavailableIdentityProofFailsClosedWithoutObservation()
    {
        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"a.txt\"}",
            out var arguments));
        var access = new FakeFileAccess(
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = "secret"u8.ToArray(),
            })
        {
            ProbeStatus = ReviewedFileAccessStatus.Unsafe,
        };
        var executor = CreateExecutor(["a.txt"], access);

        var result = await executor.ExecuteAsync(
            new PreparedReadFileCall("call", arguments!),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("tool_path_unsafe", result.FailureCode);
        Assert.Null(result.ResultJson);
        Assert.Null(result.Observation);
        Assert.Equal(0, access.ReadCount);
    }

    [Fact]
    public async Task OpenedObjectIdentityMismatchFailsClosedAfterProbe()
    {
        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"a.txt\"}",
            out var arguments));
        var access = new FakeFileAccess(
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = "secret"u8.ToArray(),
            })
        {
            ReadStatus = ReviewedFileAccessStatus.Unsafe,
        };
        var executor = CreateExecutor(["a.txt"], access);

        var result = await executor.ExecuteAsync(
            new PreparedReadFileCall("call", arguments!),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("tool_path_unsafe", result.FailureCode);
        Assert.Null(result.ResultJson);
        Assert.Null(result.Observation);
        Assert.Equal(1, access.ReadCount);
    }

    [Fact]
    public async Task MetadataToOpenTargetSwapFailsBeforeReadingBytes()
    {
        Assert.True(AgentToolArguments.TryReadFile(
            "{\"path\":\"a.txt\"}",
            out var arguments));
        var access = new FakeFileAccess(
            new Dictionary<string, byte[]>
            {
                ["a.txt"] = "secret"u8.ToArray(),
            })
        {
            ProbeLengthDelta = 1,
        };
        var executor = CreateExecutor(["a.txt"], access);

        var result = await executor.ExecuteAsync(
            new PreparedReadFileCall("call", arguments!),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("tool_path_unsafe", result.FailureCode);
        Assert.Null(result.Observation);
        Assert.Equal(0, access.ReadCount);
    }

    [Fact]
    public async Task ProductionAccessReadsAStableRegularFile()
    {
        var root = Directory.CreateTempSubdirectory("apr86-safe-file-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "a.txt"),
                "a\n",
                new UTF8Encoding(false));
            Assert.True(AgentToolArguments.TryReadFile(
                "{\"path\":\"a.txt\"}",
                out var arguments));
            var executor = new SnapshotToolExecutor(
                new ReviewedSnapshot(Identity, root.FullName, ["a.txt"]),
                new VerifiedReviewedFileAccess());

            var result = await executor.ExecuteAsync(
                new PreparedReadFileCall("call", arguments!),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Contains("\"text\":\"a\"", result.ResultJson, StringComparison.Ordinal);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ProductionAccessRejectsFinalSymlinkWhenPlatformPermitsCreation()
    {
        var root = Directory.CreateTempSubdirectory("apr86-symlink-");
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root.FullName, "target.txt"),
                "secret",
                new UTF8Encoding(false));
            try
            {
                File.CreateSymbolicLink(
                    Path.Combine(root.FullName, "link.txt"),
                    Path.Combine(root.FullName, "target.txt"));
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
            catch (PlatformNotSupportedException)
            {
                return;
            }
            catch (IOException)
            {
                return;
            }

            Assert.True(AgentToolArguments.TryReadFile(
                "{\"path\":\"link.txt\"}",
                out var arguments));
            var executor = new SnapshotToolExecutor(
                new ReviewedSnapshot(Identity, root.FullName, ["link.txt"]),
                new VerifiedReviewedFileAccess());

            var result = await executor.ExecuteAsync(
                new PreparedReadFileCall("call", arguments!),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal("tool_path_unsafe", result.FailureCode);
            Assert.Null(result.Observation);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void TerminalRequiresCurrentGroundedUniqueEvidence()
    {
        var returned = ImmutableDictionary<string, ImmutableHashSet<int>>.Empty
            .WithComparers(StringComparer.Ordinal)
            .Add("a.txt", [1, 2]);
        var observation = new AgentObservation(
            new string('a', 64),
            Identity,
            returned);
        var json =
            "{\"summary\":\"done\",\"findings\":[{\"severity\":\"high\",\"title\":\"bug\",\"message\":\"fix it\",\"evidence\":[{\"observation_id\":\"" +
            new string('a', 64) +
            "\",\"path\":\"a.txt\",\"start_line\":1,\"end_line\":2}]}]}";
        Assert.True(AgentToolArguments.TryFinishReview(json, out var arguments));

        Assert.True(TerminalReviewValidator.TryValidate(
            arguments!,
            Identity,
            [observation],
            out var review));
        Assert.NotNull(review);

        var forged = arguments! with
        {
            Findings =
            [
                arguments.Findings[0] with
                {
                    Evidence =
                    [
                        arguments.Findings[0].Evidence[0] with
                        {
                            EndLine = 3,
                        },
                    ],
                },
            ],
        };
        Assert.False(TerminalReviewValidator.TryValidate(
            forged,
            Identity,
            [observation],
            out _));
    }

    private static async Task<AgentToolExecution> ExecuteReadAsync(
        string path,
        byte[] bytes,
        string argumentsJson)
    {
        Assert.True(AgentToolArguments.TryReadFile(argumentsJson, out var arguments));
        var executor = CreateExecutor(
            [path],
            new FakeFileAccess(new Dictionary<string, byte[]>
            {
                [path] = bytes,
            }));
        return await executor.ExecuteAsync(
            new PreparedReadFileCall("call", arguments!),
            CancellationToken.None);
    }

    private static async Task<AgentToolExecution> ExecuteSearchAsync(
        IEnumerable<string> tracked,
        IReadOnlyDictionary<string, byte[]> files,
        string? path = null)
    {
        Assert.True(AgentToolArguments.TrySearchText(
            path is null
                ? "{\"query\":\"x\"}"
                : "{\"query\":\"x\",\"path\":\"" + path + "\"}",
            out var arguments));
        var executor = CreateExecutor(tracked, new FakeFileAccess(files));
        return await executor.ExecuteAsync(
            new PreparedSearchTextCall("call", arguments!),
            CancellationToken.None);
    }

    private static SnapshotToolExecutor CreateExecutor(
        IEnumerable<string> tracked,
        IReviewedFileAccess access) =>
        new(
            new ReviewedSnapshot(Identity, Directory.GetCurrentDirectory(), tracked),
            access);

    private sealed class FakeFileAccess(
        IReadOnlyDictionary<string, byte[]> files) : IReviewedFileAccess
    {
        internal ReviewedFileAccessStatus ProbeStatus { get; init; } =
            ReviewedFileAccessStatus.Success;

        internal ReviewedFileAccessStatus ReadStatus { get; init; } =
            ReviewedFileAccessStatus.Success;

        internal long ProbeLengthDelta { get; init; }

        internal int ReadCount { get; private set; }

        public ReviewedFileMetadata InspectMetadata(
            ReviewedSnapshot snapshot,
            string path)
        {
            if (ProbeStatus != ReviewedFileAccessStatus.Success)
            {
                return ProbeStatus == ReviewedFileAccessStatus.Unsafe
                    ? ReviewedFileMetadata.Unsafe()
                    : ReviewedFileMetadata.IoFailure();
            }

            return new ReviewedFileMetadata(
                ReviewedFileAccessStatus.Success,
                files[path].Length);
        }

        public ReviewedFileProbe Probe(ReviewedSnapshot snapshot, string path)
        {
            if (ProbeStatus != ReviewedFileAccessStatus.Success)
            {
                return ProbeStatus == ReviewedFileAccessStatus.Unsafe
                    ? ReviewedFileProbe.Unsafe()
                    : ReviewedFileProbe.IoFailure();
            }

            return new ReviewedFileProbe(
                ReviewedFileAccessStatus.Success,
                files[path].Length + ProbeLengthDelta,
                new ReviewedFileIdentity(1, 1));
        }

        public ValueTask<ReviewedFileRead> ReadAsync(
            ReviewedSnapshot snapshot,
            string path,
            ReviewedFileProbe expected,
            CancellationToken cancellationToken)
        {
            ReadCount++;
            if (ReadStatus != ReviewedFileAccessStatus.Success)
            {
                return ValueTask.FromResult(
                    ReadStatus == ReviewedFileAccessStatus.Unsafe
                        ? ReviewedFileRead.Unsafe()
                        : ReviewedFileRead.IoFailure());
            }

            return ValueTask.FromResult(new ReviewedFileRead(
                ReviewedFileAccessStatus.Success,
                files[path]));
        }
    }
}
