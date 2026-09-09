using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Quality;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using Xunit;
using FixtureProgram = AgenticPrReview.Runtime.ReviewEvaluationFixture.Program;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R5QualityCorpusTests
{
    private static string CheckedRoot => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "quality");

    [Fact]
    public async Task CompleteCheckedCorpusExecutesEveryCategoryAndActualToolFamily()
    {
        var result = await QualityRunner.RunAsync(CheckedRoot);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(13, result.Summary.ExecutedCases);
        Assert.Equal(13, result.Summary.VerifiedCases);
        Assert.Equal("05b669903439aea13818e507b48c0c3742a979672d124f7d68113c63dfdf4e38", result.Summary.CorpusSha256);
        Assert.Equal("c8714bfdcd2474a7beaf13495b2250a361f8fda1b504a98ed17854e84bf07311", result.Executions[0].Outcome.ConfigurationSha256);
        Assert.Equal("946632f4a0373e5dabc7ef06594dd823d51cb5ca21e350d4eb08f7590f5d185f", result.Executions[0].Outcome.CaseSha256);
        Assert.Equal(QualityCoverage.Cases.Select(spec => spec.Id), result.Executions.Select(item => item.Spec.Id));
        Assert.All(result.Summary.Cases, row => Assert.True(row.Verified, row.CaseId));
        Assert.Equal("deterministic", result.Summary.Mode);
        foreach (var execution in result.Executions)
        {
            Assert.True(execution.FirstRequestSeen);
            Assert.True(execution.ContextWithheld);
            Assert.Null(execution.Request.Continuation);
            Assert.Equal(execution.Input.Script.Turns.Length, execution.ConsumedTurns);
            Assert.Equal(execution.Input.ConfigurationSha256, execution.Outcome.ConfigurationSha256);
            Assert.Equal(execution.Outcome, EvaluationJson.ReadOutcome(EvaluationJson.Write(execution.Outcome)));
        }
        var positive = result.Executions.Where(item => item.Spec.ExpectedCode == EvaluationCode.Scored).ToArray();
        Assert.Equal(new[] { "list_changed_files", "list_files", "read_diff", "read_file", "search_text" },
            positive.SelectMany(item => item.Subject!.Observations).Select(item => item.Tool).Distinct().Order(StringComparer.Ordinal));
        Assert.True(QualityRunner.HasNoInlineLocation(result.Executions.Single(item => item.Spec.Sticky)));
        Assert.All(positive.Where(item => !item.Spec.Safe), item => Assert.Equal(ModelObservationStatus.Unadjudicated, item.Outcome.ModelStatus));
        Assert.All(result.Executions.Where(item => item.Spec.AgentFailure is not null), item =>
        {
            Assert.Equal(EvaluationStatus.Failed, item.Outcome.ExecutionStatus);
            Assert.Equal(EvaluationFailureSource.Agent, item.Outcome.FailureSource);
            Assert.Null(item.Subject);
        });
        Assert.Equal(1, result.Executions.Single(item => item.Spec.Id == "duplicate-proposal").Outcome.DuplicateObservations);
        Assert.Equal(1, result.Executions.Single(item => item.Spec.Id == "safe-invention").Outcome.ProhibitedObservations);
    }

    [Theory]
    [InlineData("cs-defect", "list_changed_files")]
    [InlineData("cs-defect", "read_diff")]
    [InlineData("ts-defect", "search_text")]
    [InlineData("repository-rule", "list_files")]
    public async Task RemovingRequiredOperationCannotBorrowPassingAuditObservations(string caseId, string tool)
    {
        using var corpus = new Corpus();
        corpus.ChangeScript(caseId, script => script with
        { Turns = script.Turns.Where(turn => turn.ToolCalls.All(call => call.Name != tool)).ToImmutableArray() });
        var admitted = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(corpus.Bundle).Fixture);
        var spec = QualityCoverage.Cases.Single(item => item.Id == caseId);
        var input = admitted.Runs.Single(item => item.Input.Id == caseId);
        Assert.True(QualityAudit.Matches(input, spec, await QualityAudit.ObserveAsync(input, spec)));
        var result = await QualityRunner.RunAsync(corpus.Root);
        Assert.Equal(1, result.ExitCode);
        var actual = result.Executions.Single(item => item.Spec.Id == caseId);
        Assert.True(actual.AgentOutcome.Succeeded);
        Assert.Equal(EvaluationCode.RequiredToolMissing, actual.Outcome.Code);
        Assert.DoesNotContain(actual.Subject!.Observations, item => item.Tool == tool);
        Assert.False(result.Summary.Cases.Single(item => item.CaseId == caseId).Verified);
    }

    [Theory]
    [InlineData("cs-defect", "read_diff")]
    [InlineData("ts-defect", "search_text")]
    [InlineData("repository-rule", "list_files")]
    public async Task ReplacingRequiredFamilyWithAnotherReadDoesNotMeetCoverage(string caseId, string tool)
    {
        using var corpus = new Corpus();
        corpus.ChangeScript(caseId, script => script with { Turns = script.Turns.Select(turn => turn with
        { ToolCalls = turn.ToolCalls.Select(call => call.Name == tool ? call with
            { Name = "read_file", ArgumentsJson = QualityCoverage.Read("docs/notes.txt").Arguments } : call).ToImmutableArray() }).ToImmutableArray() });
        var result = await QualityRunner.RunAsync(corpus.Root);
        var actual = result.Executions.Single(item => item.Spec.Id == caseId);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal(EvaluationCode.RequiredToolMissing, actual.Outcome.Code);
        Assert.True(result.Summary.Cases.Single(item => item.CaseId == caseId).AuditPassed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InitialContextOrTrustedPolicyLeakFailsIndependentlyOfScriptedAnswer(bool policy)
    {
        using var corpus = new Corpus();
        var descriptor = corpus.Manifest.Runs[0];
        var originalScript = File.ReadAllBytes(corpus.Member(descriptor.Script));
        var member = policy ? descriptor.Policy : descriptor.Context;
        corpus.Replace(member, Encoding.UTF8.GetBytes(File.ReadAllText(corpus.Member(member)) + QualityCoverage.Cases[0].Facts[0].Text));
        Assert.NotNull(ReplayAdmission.Load(corpus.Bundle).Fixture);
        var result = await QualityRunner.RunAsync(corpus.Root);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.Summary.Cases[0].ContextWithheld);
        Assert.True(result.Summary.Cases[0].AuditPassed);
        Assert.Equal(EvaluationCode.Scored, result.Executions[0].Outcome.Code);
        Assert.Equal(originalScript, File.ReadAllBytes(corpus.Member(descriptor.Script)));
    }

    [Theory]
    [InlineData("src/Lookup.cs", "=> null;", "=> string.Empty;")]
    [InlineData("src/config.ts", "timeoutMs: 0", "timeoutMs: 5000")]
    [InlineData("rules/review.md", "Never log", "Always log")]
    public async Task ChangedDecisiveSourceWithValidHashesFailsTheIndependentOracle(string path, string before, string after)
    {
        using var corpus = new Corpus();
        var member = corpus.Manifest.Runs[0].Repository.Single(item => item.Path == path).File;
        corpus.Replace(member, Encoding.UTF8.GetBytes(File.ReadAllText(corpus.Member(member)).Replace(before, after, StringComparison.Ordinal)));
        var admitted = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(corpus.Bundle).Fixture);
        var index = path.EndsWith("Lookup.cs", StringComparison.Ordinal) ? 0 : path.EndsWith("config.ts", StringComparison.Ordinal) ? 2 : 4;
        Assert.False((await QualityAudit.ObserveAsync(admitted.Runs[index], QualityCoverage.Cases[index])).FactsMatch);
        Assert.Equal(2, (await QualityRunner.RunAsync(corpus.Root)).ExitCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("renamed")]
    [InlineData("reordered")]
    [InlineData("extra")]
    public async Task CorpusInventoryCannotBeSilentlyReducedOrRelabeled(string mutation)
    {
        using var corpus = new Corpus();
        var runs = corpus.Manifest.Runs;
        if (mutation == "missing")
        {
            var removed = runs[^1];
            runs = runs.RemoveAt(runs.Length - 1);
            var dropped = new[] { removed.Context, removed.Script, removed.Assertions };
            corpus.Manifest = corpus.Manifest with { Files = corpus.Manifest.Files.Where(file => !dropped.Contains(file.Path)).ToImmutableArray() };
            Directory.Delete(Path.GetDirectoryName(corpus.Member(removed.Context))!, true);
        }
        if (mutation == "duplicate") runs = runs.SetItem(runs.Length - 1, runs[runs.Length - 1] with { Id = runs[0].Id });
        if (mutation == "renamed") runs = runs.SetItem(runs.Length - 1, runs[runs.Length - 1] with { Id = "unlisted", CaseId = "unlisted" });
        if (mutation == "reordered")
        {
            runs = runs.SetItem(0, runs[1]).SetItem(1, runs[0]);
            runs = runs.Select((run, index) => run with { Transition = index == 0 ? "initial" : "same_head", PreviousRunId = index == 0 ? null : runs[index - 1].Id }).ToImmutableArray();
        }
        if (mutation == "extra") runs = runs.Add(runs[^1] with { Id = "extra", CaseId = "extra", PreviousRunId = runs[^1].Id });
        corpus.Manifest = corpus.Manifest with { Runs = runs };
        if (mutation != "duplicate") Assert.NotNull(ReplayAdmission.Load(corpus.Bundle).Fixture);
        var result = await QualityRunner.RunAsync(corpus.Root);
        Assert.Equal(2, result.ExitCode);
        Assert.Empty(result.Executions);
    }

    [Theory]
    [InlineData("suffix")]
    [InlineData("exhausted")]
    [InlineData("reasoning")]
    [InlineData("exact-duplicate")]
    [InlineData("invalid-tool")]
    public async Task UnconsumedOrInvalidScriptsNeverCountAsExpectedSuccess(string mutation)
    {
        using var corpus = new Corpus();
        corpus.ChangeScript("cs-defect", script =>
        {
            if (mutation == "suffix") return script with { Turns = script.Turns.Add(new([new("unused", "read_file", QualityCoverage.Read("docs/notes.txt").Arguments)], "")) };
            if (mutation == "exhausted") return script with { Turns = script.Turns.RemoveAt(script.Turns.Length - 1) };
            if (mutation == "reasoning") return script with { Turns = script.Turns.SetItem(0, script.Turns[0] with { ReasoningContent = "APR242_REASONING_CANARY" }) };
            if (mutation == "invalid-tool") return script with { Turns = script.Turns.SetItem(0, new([new("invalid", "shell", "{}")], "")) };
            Assert.True(AgentToolArguments.TryFinishReview(script.Turns[^1].ToolCalls[0].ArgumentsJson, out var terminal));
            var duplicate = Encoding.UTF8.GetString(AgentToolArguments.WriteFinishReview(terminal!.Summary, [terminal.Findings[0], terminal.Findings[0]]));
            return script with { Turns = script.Turns.SetItem(script.Turns.Length - 1, new([new("finish", "finish_review", duplicate)], "")) };
        });
        var result = await QualityRunner.RunAsync(corpus.Root);
        Assert.NotEqual(0, result.ExitCode);
        if (mutation == "exact-duplicate") Assert.Equal(AgentFailureCodes.TerminalInvalid, result.Executions[0].AgentOutcome.Diagnostic!.Code);
        if (mutation == "exhausted") Assert.True(result.Executions[0].ScriptExhausted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingExpectedResultOrObservationCannotBlessFaultyExecution(bool observation)
    {
        using var corpus = new Corpus();
        var run = corpus.Manifest.Runs.Single(item => item.Id == "wrong-location");
        var expected = ReplayJson.Read(File.ReadAllBytes(corpus.Member(run.Assertions)), ReplayJsonContext.Default.ReplayAssertions)!;
        var changed = observation ? expected with { RequiredObservations = expected.RequiredObservations.SetItem(0,
            expected.RequiredObservations[0] with { ObservationId = new string('f', 64) }) } : expected with { ExpectedCode = "Scored" };
        corpus.Replace(run.Assertions, JsonSerializer.SerializeToUtf8Bytes(changed, ReplayJsonContext.Default.ReplayAssertions));
        Assert.NotNull(ReplayAdmission.Load(corpus.Bundle).Fixture);
        Assert.Equal(2, (await QualityRunner.RunAsync(corpus.Root)).ExitCode);
    }

    [Fact]
    public async Task IrrelevantOrOmittedReadCannotUsePreflightAsAgentEvidence()
    {
        using var corpus = new Corpus();
        corpus.ChangeScript("cs-defect", script => script with { Turns =
            [new([new("read", "read_file", QualityCoverage.Read("docs/notes.txt").Arguments)], ""),
             new([new("finish", "finish_review", "{\"summary\":\"Synthetic empty completion\",\"findings\":[]}")], "")] });
        var result = await QualityRunner.RunAsync(corpus.Root);
        Assert.Equal(1, result.ExitCode);
        Assert.True(result.Summary.Cases[0].AuditPassed);
        Assert.Single(result.Executions[0].Subject!.Observations);
        Assert.Equal(EvaluationCode.RequiredToolMissing, result.Executions[0].Outcome.Code);
    }

    [Fact]
    public async Task CommandPreservesEvaluateAndEmitsOnlyBoundedDeterministicResults()
    {
        var priorOut = Console.Out;
        var priorError = Console.Error;
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            Assert.Equal(0, await FixtureProgram.Main(["quality", "--corpus", CheckedRoot]));
            var lines = stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(14, lines.Length);
            Assert.All(lines.Take(13), line => Assert.NotNull(EvaluationJson.ReadOutcome(Encoding.UTF8.GetBytes(line))));
            using var summary = JsonDocument.Parse(lines[^1]);
            Assert.Equal("deterministic", summary.RootElement.GetProperty("mode").GetString());
            Assert.Equal("verified", summary.RootElement.GetProperty("code").GetString());
            Assert.DoesNotContain("CANARY", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Lookup.Find", stdout.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("accuracy", stdout.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Empty(stderr.ToString());
            stdout.GetStringBuilder().Clear();
            Assert.Equal(0, await FixtureProgram.Main(["evaluate", "--fixture", "self-test"]));
            Assert.Equal(19, stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
            stdout.GetStringBuilder().Clear();
            Assert.Equal(2, await FixtureProgram.Main(["quality", "--corpus", Path.Combine(CheckedRoot, "missing")]));
            Assert.DoesNotContain(CheckedRoot, stdout.ToString(), StringComparison.Ordinal);
            Assert.Equal(2, await FixtureProgram.Main(["quality"]));
        }
        finally { Console.SetOut(priorOut); Console.SetError(priorError); }
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(true, 13)]
    [InlineData(false, 0)]
    [InlineData(false, 18)]
    public async Task CommandOutputFailuresReturnOnlySafeInfrastructureDiagnostic(bool quality, int successfulLines)
    {
        var priorOut = Console.Out;
        var priorError = Console.Error;
        using var stdout = new FailingWriter(successfulLines);
        using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout); Console.SetError(stderr);
            var arguments = quality ? new[] { "quality", "--corpus", CheckedRoot } : ["evaluate", "--fixture", "self-test"];
            Assert.Equal(1, await FixtureProgram.Main(arguments));
            Assert.Equal("r5_evaluation_infrastructure_failed" + Environment.NewLine, stderr.ToString());
            Assert.Equal(successfulLines, stdout.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Length);
            Assert.DoesNotContain("APR242_OUTPUT_FAILURE_CANARY", stdout.ToString() + stderr, StringComparison.Ordinal);
        }
        finally { Console.SetOut(priorOut); Console.SetError(priorError); }
    }

    [Theory]
    [InlineData("wrong-evidence", "all")]
    [InlineData("pathless-proposal", "all")]
    [InlineData("wrong-evidence", "read-arguments")]
    [InlineData("pathless-proposal", "read-arguments")]
    [InlineData("wrong-evidence", "duplicate-read")]
    [InlineData("pathless-proposal", "duplicate-read")]
    public async Task MalformedTerminalMustFollowTheRequiredActualObservations(string caseId, string mutation)
    {
        using var corpus = new Corpus();
        corpus.ChangeScript(caseId, script => script with { Turns = script.Turns.Select(turn => turn with
        { ToolCalls = turn.ToolCalls.Select(call => call.Name != "finish_review" &&
            (mutation == "all" || call.ArgumentsJson.Contains("src/Lookup.cs", StringComparison.Ordinal)) ? call with
            { Name = "read_file", ArgumentsJson = QualityCoverage.Read(mutation == "duplicate-read" ? "src/Caller.cs" : "docs/notes.txt").Arguments } : call).ToImmutableArray() }).ToImmutableArray() });
        var result = await QualityRunner.RunAsync(corpus.Root);
        var actual = result.Executions.Single(item => item.Spec.Id == caseId);
        Assert.Equal(AgentFailureCodes.TerminalInvalid, actual.AgentOutcome.Diagnostic!.Code);
        Assert.Equal(actual.Spec.Operations.Length + 1, actual.AgentOutcome.Diagnostic.ToolCalls);
        Assert.Equal(actual.Input.Script.Turns.Length, actual.ConsumedTurns);
        Assert.Equal(EvaluationCode.ExecutionFailed, actual.Outcome.Code);
        Assert.Null(actual.Subject);
        var row = result.Summary.Cases.Single(item => item.CaseId == caseId);
        Assert.True(row.AuditPassed);
        Assert.False(row.Verified);
        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData("IoFailure", "infrastructure_failed", 1)]
    [InlineData("Cancelled", "infrastructure_failed", 1)]
    [InlineData("InvalidManifest", "input_invalid", 2)]
    [InlineData("UnsafeEntry", "input_invalid", 2)]
    [InlineData("ContentMismatch", "input_invalid", 2)]
    [InlineData("InvalidContent", "input_invalid", 2)]
    [InlineData("InvalidReference", "input_invalid", 2)]
    public async Task AdmissionFailureCategoryIsPreserved(string admissionCode, string expectedCode, int exitCode)
    {
        var result = await QualityRunner.RunAsync(new ReplayAdmissionResult(Enum.Parse<ReplayAdmissionCode>(admissionCode), null));
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(expectedCode, result.Summary.Code);
        Assert.Equal("deterministic", result.Summary.Mode);
        Assert.Null(result.Summary.CorpusSha256);
        Assert.Empty(result.Executions);
        Assert.Empty(result.Summary.Cases);
        Assert.Equal(0, result.Summary.ExecutedCases);
        Assert.Equal(0, result.Summary.VerifiedCases);
    }

    [Fact]
    public async Task CancelledRealAdmissionIsNotMalformedCorpus()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Equal(ReplayAdmissionCode.Cancelled, ReplayAdmission.Load(Path.Combine(CheckedRoot, "bundle"), cancellation.Token).Code);
        var result = await QualityRunner.RunAsync(CheckedRoot, cancellation.Token);
        Assert.Equal(1, result.ExitCode);
        Assert.Equal("infrastructure_failed", result.Summary.Code);
    }

    private sealed class FailingWriter(int successfulLines) : StringWriter
    {
        private int _written;
        public override void WriteLine(string? value)
        {
            if (_written++ == successfulLines) throw new IOException("APR242_OUTPUT_FAILURE_CANARY");
            base.WriteLine(value);
        }
    }

    private sealed class Corpus : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "apr-r5-q2-" + Guid.NewGuid().ToString("N"));
        internal string Bundle => Path.Combine(Root, "bundle");
        internal string Member(string relative) => Path.Combine(Bundle, relative);
        internal ReplayManifest Manifest
        {
            get => ReplayJson.Read(File.ReadAllBytes(Member("manifest.json")), ReplayJsonContext.Default.ReplayManifest)!;
            set => File.WriteAllBytes(Member("manifest.json"), ReplayJson.Write(value));
        }
        internal Corpus()
        {
            var source = Path.Combine(CheckedRoot, "bundle");
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var destination = Member(Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination);
            }
        }
        internal void Replace(string member, byte[] bytes)
        {
            var manifest = Manifest;
            File.WriteAllBytes(Member(member), bytes);
            Manifest = manifest with { Files = manifest.Files.Select(file => file.Path == member ? file with
                { Length = bytes.Length, Sha256 = AgentCanonical.HashRaw(bytes) } : file).ToImmutableArray() };
        }
        internal void ChangeScript(string caseId, Func<ReplayScript, ReplayScript> change)
        {
            var member = Manifest.Runs.Single(run => run.Id == caseId).Script;
            var script = ReplayJson.Read(File.ReadAllBytes(Member(member)), ReplayJsonContext.Default.ReplayScript)!;
            Replace(member, JsonSerializer.SerializeToUtf8Bytes(change(script), ReplayJsonContext.Default.ReplayScript));
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
