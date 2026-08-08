using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Quality;
using AgenticPrReview.Runtime.Agent.Tools;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R3QualityCorpusTests
{
    [Fact]
    public void CorpusParsesThreeCoherentPublicSafeCases()
    {
        var bytes = ReadCorpus();

        Assert.True(
            R3QualityCorpusParser.TryParse(bytes, out var corpus, out var failure),
            failure?.SourceCode);
        Assert.Null(failure);
        Assert.Equal(3, corpus!.Cases.Length);
        Assert.Equal(
            [
                R3QualityCaseKind.MustFind,
                R3QualityCaseKind.MustNotFind,
                R3QualityCaseKind.Continuation,
            ],
            corpus.Cases.Select(testCase => testCase.Kind));
        Assert.Equal(3, corpus.Cases.Select(testCase => testCase.Id).Distinct().Count());
        Assert.All(corpus.Cases, testCase =>
        {
            Assert.Matches("^[0-9a-f]{64}$", testCase.CaseSha256);
            Assert.Equal(testCase.ChangedFile.Path, testCase.DiffSource.Path);
            Assert.Equal(
                testCase.ChangedFile.PatchSha256,
                testCase.DiffSource.PatchSha256);
            Assert.Equal(
                testCase.ChangedFile.Additions,
                testCase.DiffSource.RepresentedAdditions);
            Assert.Equal(
                testCase.ChangedFile.Deletions,
                testCase.DiffSource.RepresentedDeletions);

            using var repository = new SyntheticRepository(testCase);
            var snapshot = new ReviewedSnapshot(
                testCase.ReviewedIdentity,
                repository.Root,
                testCase.Files.Select(file => file.Path),
                [testCase.ChangedFile],
                [testCase.DiffSource]);
            Assert.True(snapshot.Contains(testCase.ChangedFile.Path));
            Assert.True(snapshot.TryGetDiffSource(
                testCase.ChangedFile.Path,
                out var source));
            Assert.Equal(testCase.DiffSource.PatchSha256, source.PatchSha256);
        });

        var mustFind = Assert.IsType<R3QualityMustFindExpectation>(
            corpus.Cases[0].Expectation);
        Assert.Equal(
            "eb616af246c95c3c2c9e4d0ef278c50979d36ae91d07dcbfdb02df21149a3a7c",
            mustFind.RequiredObservationId);
        var mustFindLines = corpus.Cases[0].DiffSource.Hunks
            .SelectMany(hunk => hunk.Lines)
            .ToArray();
        var markerLine = Assert.Single(mustFindLines, line =>
            line.Text.Contains(
                mustFind.TargetMarker,
                StringComparison.Ordinal));
        Assert.Equal("deletion", markerLine.Kind);
        Assert.Null(markerLine.NewLine);
        var evidenceLine = Assert.Single(
            mustFindLines,
            line => line.NewLine == mustFind.Evidence.StartLine);
        Assert.Equal("addition", evidenceLine.Kind);
        Assert.DoesNotContain(
            mustFind.TargetMarker,
            evidenceLine.Text,
            StringComparison.Ordinal);
        var mustNot = Assert.IsType<R3QualityMustNotFindExpectation>(
            corpus.Cases[1].Expectation);
        Assert.Equal(
            "d2f7244ec7994a88c9949616c8f07506ee3740d51802c89464acb36e5b799877",
            mustNot.RequiredObservationId);
        var continuation = corpus.Cases[2];
        Assert.Equal(
            "674dda34d9ed6b5bf7594a1dc5e15af9931cf0ed1e9c44ff17be88093af4f3ed",
            continuation.CaseSha256);
        Assert.Contains(
            "no findings",
            continuation.InitialContext,
            StringComparison.Ordinal);
        Assert.Contains(
            "no findings",
            continuation.ProcessOneContext,
            StringComparison.Ordinal);

        AssertPublicSafe(bytes);
    }

    [Theory]
    [MemberData(nameof(InvalidCorpusMutations))]
    public void ParserRejectsMalformedOrIncoherentCorpus(
        string name,
        Func<string, string> mutate)
    {
        var original = Encoding.UTF8.GetString(ReadCorpus());
        var mutated = Encoding.UTF8.GetBytes(mutate(original));

        Assert.False(R3QualityCorpusParser.TryParse(
            mutated,
            out var corpus,
            out var failure));
        Assert.Null(corpus);
        Assert.NotNull(failure);
        Assert.Equal("corpus", failure!.CaseId);
        Assert.Matches("^[0-9a-f]{64}$", failure.CaseSha256);
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Fact]
    public void ParseFailureProducesStableEvaluatorOutcome()
    {
        var bytes = "{}"u8.ToArray();
        Assert.False(R3QualityCorpusParser.TryParse(
            bytes,
            out _,
            out var failure));

        var outcome = R3QualityEvaluator.FromParseFailure(failure!);

        Assert.Equal("not_evaluated", outcome.Status);
        Assert.Equal("evaluator", outcome.Classification);
        Assert.Equal(R3QualityCodes.FixtureInvalid, outcome.Code);
        Assert.Equal(0, outcome.FindingCount);
        Assert.Equal(0, outcome.ToolCallCount);
        Assert.Null(outcome.TerminalSha256);
        Assert.DoesNotContain(
            "{}",
            Encoding.UTF8.GetString(outcome.CanonicalBytes),
            StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ValidOutcomeRows))]
    public void OutcomeContractAcceptsEveryLegalRow(
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256)
    {
        Assert.True(R3QualityOutcome.TryCreate(
            "case",
            new string('a', 64),
            status,
            classification,
            code,
            sourceCode,
            findingCount,
            toolCallCount,
            terminalSha256,
            out var outcome));
        Assert.NotNull(outcome);
    }

    [Theory]
    [MemberData(nameof(InvalidOutcomeRows))]
    public void OutcomeContractRejectsIllegalCombinations(
        string status,
        string classification,
        string code,
        string? sourceCode,
        int findingCount,
        int toolCallCount,
        string? terminalSha256)
    {
        Assert.False(R3QualityOutcome.TryCreate(
            "case",
            new string('a', 64),
            status,
            classification,
            code,
            sourceCode,
            findingCount,
            toolCallCount,
            terminalSha256,
            out var outcome));
        Assert.Null(outcome);
    }

    [Fact]
    public void PassedOutcomeIsByteExactAndContainsNoLogicalText()
    {
        Assert.True(R3QualityOutcome.TryCreate(
            "case",
            new string('a', 64),
            "passed",
            "quality",
            R3QualityCodes.Passed,
            sourceCode: null,
            findingCount: 1,
            toolCallCount: 1,
            new string('b', 64),
            out var outcome));

        Assert.Equal(
            "{\"kind\":\"apr-r3-quality-outcome\",\"case_id\":\"case\",\"case_sha256\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"status\":\"passed\",\"classification\":\"quality\",\"code\":\"r3_quality_passed\",\"source_code\":null,\"finding_count\":1,\"tool_call_count\":1,\"terminal_sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}",
            Encoding.UTF8.GetString(outcome!.CanonicalBytes));
        Assert.Equal("r3_quality_outcome", outcome.ToString());
    }

    public static TheoryData<string, Func<string, string>>
        InvalidCorpusMutations() =>
        new()
        {
            {
                "duplicate root property",
                text => text.Replace(
                    "\"kind\": \"apr-r3-quality-corpus\"",
                    "\"kind\": \"apr-r3-quality-corpus\", \"kind\": \"duplicate\"",
                    StringComparison.Ordinal)
            },
            {
                "duplicate case id",
                text => text.Replace(
                    "\"id\": \"must-not-find-safe-line\"",
                    "\"id\": \"must-find-read-diff\"",
                    StringComparison.Ordinal)
            },
            {
                "unsafe path",
                text => text.Replace(
                    "src/CacheGate.cs",
                    "../CacheGate.cs",
                    StringComparison.Ordinal)
            },
            {
                "incoherent observation",
                text => text.Replace(
                    "eb616af246c95c3c2c9e4d0ef278c50979d36ae91d07dcbfdb02df21149a3a7c",
                    new string('0', 64),
                    StringComparison.Ordinal)
            },
            {
                "impossible diff range",
                text => text.Replace(
                    "\"old_count\": 5",
                    "\"old_count\": 6",
                    StringComparison.Ordinal)
            },
            {
                "duplicate fresh input",
                text => text.Replace(
                    "\"reviewed-snapshot.json\"",
                    "\"current-review-context.json\"",
                    StringComparison.Ordinal)
            },
        };

    public static TheoryData<
        string,
        string,
        string,
        string?,
        int,
        int,
        string?> ValidOutcomeRows()
    {
        var terminal = new string('b', 64);
        var data = new TheoryData<
            string,
            string,
            string,
            string?,
            int,
            int,
            string?>
        {
            { "passed", "quality", R3QualityCodes.Passed, null, 1, 1, terminal },
            { "failed", "quality", R3QualityCodes.RequiredToolMissing, null, 0, 0, terminal },
            { "failed", "quality", R3QualityCodes.RequiredToolWrong, null, 0, 1, terminal },
            { "failed", "quality", R3QualityCodes.RequiredObservationMissing, null, 0, 1, terminal },
            { "failed", "quality", R3QualityCodes.ExpectedFindingMissing, null, 0, 1, terminal },
            { "failed", "quality", R3QualityCodes.ProhibitedFinding, null, 1, 1, terminal },
            { "failed", "quality", R3QualityCodes.PriorFactMissing, null, 0, 0, terminal },
            { "not_evaluated", "evaluator", R3QualityCodes.FixtureInvalid, null, 0, 0, null },
            { "not_evaluated", "evaluator", R3QualityCodes.SubjectInvalid, "session_record_invalid", 0, 0, null },
            { "not_evaluated", "evaluator", R3QualityCodes.InitialContextLeak, null, 0, 1, terminal },
            { "not_evaluated", "evaluator", R3QualityCodes.FreshInputInvalid, null, 0, 0, terminal },
            { "not_evaluated", "evaluator", R3QualityCodes.ObservationIsolationInvalid, null, 0, 2, terminal },
            { "not_evaluated", "product", R3QualityCodes.ProductFailed, "agent_terminal_invalid", 0, 0, null },
            { "not_evaluated", "provider", R3QualityCodes.ProviderFailed, "agent_chat_failed", 0, 0, null },
            { "not_evaluated", "tool", R3QualityCodes.ToolFailed, "tool_io_failed", 0, 1, null },
            { "not_evaluated", "state", R3QualityCodes.StateFailed, "session_record_invalid", 0, 1, null },
            { "not_evaluated", "evaluator", R3QualityCodes.EvaluatorFailed, "harness_failed", 0, 0, null },
        };
        return data;
    }

    public static TheoryData<
        string,
        string,
        string,
        string?,
        int,
        int,
        string?> InvalidOutcomeRows()
    {
        var terminal = new string('b', 64);
        return new TheoryData<
            string,
            string,
            string,
            string?,
            int,
            int,
            string?>
        {
            { "passed", "quality", R3QualityCodes.RequiredToolMissing, null, 0, 0, terminal },
            { "failed", "quality", R3QualityCodes.Passed, null, 0, 0, terminal },
            { "failed", "quality", R3QualityCodes.ProhibitedFinding, "source", 1, 1, terminal },
            { "not_evaluated", "evaluator", R3QualityCodes.SubjectInvalid, null, 1, 0, null },
            { "not_evaluated", "evaluator", R3QualityCodes.InitialContextLeak, null, 0, 1, null },
            { "not_evaluated", "provider", R3QualityCodes.ProviderFailed, null, 0, 0, null },
            { "not_evaluated", "quality", R3QualityCodes.ProductFailed, "source", 0, 0, null },
            { "not_evaluated", "tool", R3QualityCodes.ToolFailed, "source", 0, 0, terminal },
            { "failed", "quality", R3QualityCodes.ExpectedFindingMissing, null, 21, 0, terminal },
            { "failed", "quality", R3QualityCodes.ExpectedFindingMissing, null, 0, 25, terminal },
        };
    }

    internal static byte[] ReadCorpus() =>
        File.ReadAllBytes(Path.Join(
            AppContext.BaseDirectory,
            "fixtures",
            "agent",
            "r3-quality",
            "corpus.json"));

    internal static R3QualityCorpus ParseCorpus()
    {
        Assert.True(R3QualityCorpusParser.TryParse(
            ReadCorpus(),
            out var corpus,
            out var failure),
            failure?.SourceCode);
        return corpus!;
    }

    private static void AssertPublicSafe(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        string[] prohibitedText =
        [
            "api_key",
            "authorization:",
            "credential",
            "provider_response",
            "reasoning_payload",
            "continuation_payload",
            "session_plaintext",
            "state_plaintext",
            "github.com/",
            "D:\\",
        ];
        Assert.All(prohibitedText, value =>
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase));

        using var document = JsonDocument.Parse(bytes);
        AssertNoSensitiveProperty(document.RootElement);
    }

    private static void AssertNoSensitiveProperty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                Assert.DoesNotContain(
                    property.Name,
                    new[]
                    {
                        "api_key",
                        "credential",
                        "opaque",
                        "provider_response",
                        "reasoning",
                        "session_bytes",
                        "state_bytes",
                    },
                    StringComparer.OrdinalIgnoreCase);
                AssertNoSensitiveProperty(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                AssertNoSensitiveProperty(item);
            }
        }
    }

    private sealed class SyntheticRepository : IDisposable
    {
        internal SyntheticRepository(R3QualityCase testCase)
        {
            Root = Path.Join(
                Path.GetTempPath(),
                "apr-r3-quality",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            foreach (var file in testCase.Files)
            {
                var fullPath = Path.Join(
                    Root,
                    file.Path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(
                    fullPath,
                    file.Content,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            }
        }

        internal string Root { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
