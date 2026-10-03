using System.Security.Cryptography;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R5CompletedReplayTests
{
    private static string Bundle => Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay");

    [Theory]
    [InlineData(256, 16)]
    [InlineData(257, 16)]
    [InlineData(272, 16)]
    [InlineData(63, 1)]
    public async Task CurrentReplyAdmitsRealWideReplayAndRetainsIndependentBounds(int tools, int batchSize)
    {
        using var bundle = new MutableBundle();
        WriteCountReplay(bundle, tools, batchSize);
        Assert.True(new FileInfo(Path.Combine(bundle.Root, "script-0.json")).Length <= ReplayLimits.FileBytes);
        Assert.NotNull(ReplayAdmission.Load(bundle.Root).Fixture);
        ReplayChildReply? observed = null;
        bool admitted = false;
        var rejected = new List<bool>();
        string? privateRoot = null;
        var result = await ReplayRunner.RunAsync(bundle.Root, new()
        {
            BeforeCleanup = root => privateRoot = root,
            ObserveReply = (input, run, reply) =>
            {
                observed = reply;
                admitted = ReplayRunner.AdmitReply(input, run, reply, out _);
                foreach (var invalid in new[]
                {
                    reply with { ToolCalls = -1 }, reply with { ToolCalls = 513 },
                    reply with { ModelCalls = -1 }, reply with { ModelCalls = 65 },
                    reply with { Requests = [.. reply.Requests, .. Enumerable.Repeat(reply.Requests[^1], 65 - reply.Requests.Length)] },
                    reply with { Requests = reply.Requests.SetItem(0, new byte[1_048_577]) },
                    reply with { EnvironmentBytes = new byte[ReplayWire.InputLimit + 1] },
                    reply with { EnvironmentKeys = [] }, reply with { PrefixHistory = null },
                    reply with { Plaintext = "malformed session"u8.ToArray() },
                    reply with { Prepared = reply.Prepared! with { Generation = 1 } },
                    reply with { Operation = Guid.NewGuid().ToString("N") }, reply with { Code = "unknown" },
                }) rejected.Add(!ReplayRunner.AdmitReply(input, run, invalid, out _));
            },
            Verify = (fixture, phase, reply) => VerifyCountReplay(fixture, phase, reply, tools, batchSize),
        });
        Assert.NotNull(observed);
        Assert.True(observed.Code == "prepared", $"{observed.Code}; tools={observed.ToolCalls}; models={observed.ModelCalls}; requestBytes={observed.Requests.Sum(bytes => (long)bytes.Length)}");
        Assert.Equal(tools, observed.ToolCalls); // Execution diagnostics exclude the terminal's budget charge.
        Assert.Equal((tools + batchSize - 1) / batchSize + 1, observed.ModelCalls);
        Assert.Equal(observed.ModelCalls, observed.Requests.Length);
        Assert.True(observed.Requests.Sum(bytes => (long)bytes.Length) <= ReplayWire.EvidenceLimit);
        Assert.Equal("verified", result.Code);
        Assert.True(admitted);
        Assert.Equal(13, rejected.Count);
        Assert.All(rejected, Assert.True);
        var step = Assert.Single(result.Steps);
        Assert.True(step.Accepted);
        Assert.Equal(tools, step.ToolCalls);
        Assert.Equal(0L, step.Generation);
        Assert.Single(result.Observations);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.False(Directory.Exists(privateRoot));
    }

    [Theory]
    [InlineData(512, "unknown_failed")]
    [InlineData(513, "result_invalid")]
    public async Task ProductionExhaustionReplyAdmits512AndRejectsAdjacentForgery(int reportedTools, string expected)
    {
        using var bundle = new MutableBundle();
        WriteCountReplay(bundle, 512, 16);
        ReplayChildReply? actual = null;
        string? privateRoot = null;
        var result = await ReplayRunner.RunAsync(bundle.Root, new()
        {
            BeforeCleanup = root => privateRoot = root,
            // The real transport's retained 4 MiB cumulative request trace stops this large
            // corpus before 512. Isolate the diagnostic count boundary with a real production
            // loop and snapshot tools, measured history and scored failure at the IPC seam.
            // The positive wide-replay test above separately exercises fresh child IPC/SESSION.
            RunProcess = async (input, _, token) =>
            {
                actual = await ProductionExhaustionReply(input, token);
                return new("completed", actual with { ToolCalls = reportedTools });
            },
        });
        Assert.NotNull(actual);
        Assert.Equal("unknown_failed", actual.Code); // Preserve the evaluator's ToolLimit failure classification.
        Assert.Equal(512, actual.ToolCalls);
        Assert.Equal(33, actual.ModelCalls);
        Assert.Empty(actual.Requests); // This isolated seam observes ProjectChat requests, not transport bytes.
        Assert.Equal(33, actual.PrefixHistory!.Calls.Length);
        Assert.Null(actual.Prepared); // 512 executed tools leave no budget for finish_review.
        Assert.Null(actual.Plaintext);
        Assert.Equal(expected, result.Code);
        var step = Assert.Single(result.Steps);
        Assert.False(step.Accepted);
        Assert.True(step.PredecessorPreserved);
        Assert.Equal(reportedTools == 512 ? 512 : 0, step.ToolCalls);
        Assert.Equal(reportedTools == 512 ? 1 : 0, result.Observations.Length);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.False(Directory.Exists(privateRoot));
    }

    [Theory]
    [InlineData(17, 17)]
    [InlineData(64, 1)]
    public async Task CurrentReplayStillRejects17MemberResponsesAnd65TurnScripts(int tools, int batchSize)
    {
        using var bundle = new MutableBundle();
        WriteCountReplay(bundle, tools, batchSize);
        Assert.Equal(ReplayAdmissionCode.InvalidContent, ReplayAdmission.Load(bundle.Root).Code);
        var result = await ReplayRunner.RunAsync(bundle.Root);
        Assert.Equal("input_invalid", result.Code);
        Assert.Empty(result.Steps);
        Assert.Empty(result.Observations);
        Assert.Equal("cleaned", result.Cleanup);
    }

    [Fact]
    public async Task BootstrapPreparesRealStateWithoutAcceptingIt()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bundle"));
            foreach (var file in Directory.GetFiles(Bundle)) File.Copy(file, Path.Combine(root, "bundle", Path.GetFileName(file)));
            var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Path.Combine(root, "bundle")).Fixture);
            var input = new ReplayChildInput(Guid.NewGuid().ToString("N"), root, fixture.CorpusSha256, 0,
                Guid.NewGuid().ToString("N"), RandomNumberGenerator.GetBytes(32), null, ReplayFault.None);
            var reply = await ReplayChild.ExecuteAsync(input, CancellationToken.None);
            Assert.Equal("prepared", reply.Code);
            Assert.NotNull(reply.Prepared);
            Assert.True(ReplayCoverage.Verify(fixture, 0, reply));
            Assert.True(ReplayRunner.AdmitIdentity(input, reply));
            foreach (var stale in new[]
            {
                reply with { Operation = Guid.NewGuid().ToString("N") }, reply with { Corpus = new string('f', 64) },
                reply with { Session = Guid.NewGuid().ToString("N") }, reply with { Phase = 1 },
                reply with { Commit = new string('f', 40) }, reply with { Tree = new string('f', 40) },
                reply with { SourceClean = !reply.SourceClean }, reply with { ProcessId = 0 }, reply with { Startup = "invalid" },
            }) Assert.False(ReplayRunner.AdmitIdentity(input, stale));
            using var state = new ReplayState(fixture.Runs[0], input.Session, root, input.Key);
            var inventory = await state.Service.EnumerateAsync(state.Access, CancellationToken.None);
            Assert.Equal(StateAction.Enumerated, inventory.Result.Action);
            Assert.Empty(inventory.Candidates);
            Assert.True(AgentSessionCodec.TryParse(reply.Plaintext!, out var artifact, out _));
            var original = artifact!.Document;
            var normalized = ReplayProjection.Logical(artifact, state.Trusted);
            Assert.Equal(normalized, ReplayProjection.Logical(artifact with { Document = original with { SessionId = "another-session" } }, state.Trusted));
            var run = original.CompletedRuns[0];
            var tool = run.Records.OfType<AgentSessionToolResultRecord>().First();
            var changedTool = run with { Records = run.Records.SetItem(run.Records.IndexOf(tool), tool with { ObservationId = new string('f', 64) }) };
            Assert.NotEqual(normalized, ReplayProjection.Logical(artifact with { Document = original with { CompletedRuns = [changedTool] } }, state.Trusted));
            var item = run.Continuation.Items[0];
            foreach (var changed in new[] { item with { ContentPosition = 1 }, item with { Payload = "changed", PayloadBytes = "changed"u8.ToArray() } })
            {
                var changedRun = run with { Continuation = run.Continuation with { Items = run.Continuation.Items.SetItem(0, changed) } };
                Assert.NotEqual(normalized, ReplayProjection.Logical(artifact with { Document = original with { CompletedRuns = [changedRun] } }, state.Trusted));
            }
            Assert.NotEqual(reply.ProviderSha256, ReplayProjection.Provider(reply.Requests.Reverse()));
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public async Task ThreeFreshPhasesAcceptAndRestoreCompletedHistory()
    {
        var result = await ReplayRunner.RunAsync(Bundle);
        Assert.Equal("verified", result.Code);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.Equal(3, result.Steps.Length);
        Assert.All(result.Steps, step => Assert.True(step.Accepted));
        Assert.Equal(new long?[] { 0, 1, 2 }, result.Steps.Select(step => step.Generation));
        Assert.Equal(new[] { "initial", "same_head", "verified_ahead" }, result.Steps.Select(step => step.Transition));
        Assert.Equal(3, result.Observations.Select(item => item.Startup).Distinct().Count());
        Assert.DoesNotContain(ReplayCoverage.Fact, Encoding.UTF8.GetString(ReplayWire.Write(result)));
    }

    [Fact]
    public async Task TwoFreshSupervisorExecutablesProduceEqualNormalizedEvidence()
    {
        var first = await CommandAsync();
        var second = await CommandAsync();
        Assert.True(first.ExitCode == 0, Encoding.UTF8.GetString(ReplayWire.Write(first)));
        Assert.Equal(0, second.ExitCode);
        Assert.Equal(first.NormalizedSha256, second.NormalizedSha256);
        Assert.Equal(first.Steps.ToArray(), second.Steps.ToArray());
        Assert.NotEqual(first.Operation, second.Operation);
        Assert.NotEqual(first.Observations[0].Session, second.Observations[0].Session);
        Assert.NotEqual(first.Observations[0].EnvelopeSha256, second.Observations[0].EnvelopeSha256);
        Assert.Empty(first.Observations.Select(item => item.Startup).Intersect(second.Observations.Select(item => item.Startup)));
    }

    [Theory]
    [InlineData("Provider", "provider_failed")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("Incomplete", "script_exhausted")]
    [InlineData("MalformedTerminal", "agent_failed")]
    [InlineData("WrongScope", "state_failed")]
    [InlineData("WrongHead", "state_failed")]
    [InlineData("NonCompleted", "state_failed")]
    [InlineData("StartFailure", "process_failed")]
    [InlineData("AfterPrepareCrash", "process_failed")]
    [InlineData("AfterPrepareHang", "process_timeout")]
    [InlineData("AfterPrepareOverflow", "process_failed")]
    [InlineData("PartialReply", "result_invalid")]
    [InlineData("WrongReply", "result_invalid")]
    [InlineData("MissingHistory", "history_failed")]
    [InlineData("ChangedContinuation", "session_failed")]
    // Q1 leaves response-admission provenance unknown; the replay report must not invent Agent attribution.
    [InlineData("MissingContinuation", "unknown_failed")]
    [InlineData("WrongContinuationPosition", "unknown_failed")]
    public async Task FailedPhaseNeverAdvancesAcceptedPredecessor(string fault, string expected)
    {
        string? privateRoot = null;
        string diagnostic = "";
        var result = await ReplayRunner.RunAsync(Bundle, new()
        {
            Fault = Enum.Parse<ReplayFault>(fault),
            BeforeCleanup = root => privateRoot = root,
            ObserveReply = (input, run, reply) => diagnostic = $"phase={input.Phase}; identity={ReplayRunner.AdmitIdentity(input, reply)}; code={reply.Code}; requestsDefault={reply.Requests.IsDefault}; requests={reply.Requests.Length}; env={string.Join(',', reply.EnvironmentKeys)}; evalLength={reply.Evaluation?.Length}; prepared={reply.Prepared is not null}; admitted={ReplayRunner.AdmitReply(input, run, reply, out _)}",
        });
        Assert.True(expected == result.Code, $"expected={expected}; actual={result.Code}; {diagnostic}");
        Assert.Equal("cleaned", result.Cleanup);
        Assert.False(Directory.Exists(privateRoot));
        Assert.Equal(2, result.Steps.Length);
        Assert.True(result.Steps[0].Accepted);
        Assert.False(result.Steps[1].Accepted);
        Assert.True(result.Steps[1].PredecessorPreserved);
        Assert.Null(result.Steps[1].QualityCode);
        Assert.Null(result.Steps[1].Generation);
        Assert.All(result.Observations.Skip(1), observation => Assert.Null(observation.EnvelopeSha256));
        if (fault is "WrongScope" or "WrongHead") Assert.Equal(0, result.Steps[1].ModelCalls);
        if (fault == "Provider") Assert.Equal(1, result.Steps[1].ModelCalls);
        if (fault == "Incomplete") Assert.Equal(2, result.Steps[1].ModelCalls);
    }

    [Theory]
    [InlineData("reasoning")]
    [InlineData("observation")]
    [InlineData("missing_observation")]
    [InlineData("literal_answer")]
    public async Task RehashedScriptAndHistoricalInputMutationsCannotBlessReplay(string kind)
    {
        using var bundle = new MutableBundle();
        if (kind == "reasoning") bundle.Edit("script-0.json", text => text.Replace("seed reasoning", "seed reAsoning", StringComparison.Ordinal));
        if (kind == "observation") bundle.Edit("fact.txt", text => text.Replace("ORCHID", "VIOLET", StringComparison.Ordinal));
        if (kind == "missing_observation") bundle.EditJson("script-0.json", json =>
            json["turns"]![0]!["tool_calls"]!.AsArray().RemoveAt(0));
        if (kind == "literal_answer")
        {
            bundle.EditJson("script-0.json", json => json["turns"]![0]!["tool_calls"]!.AsArray().RemoveAt(0));
            bundle.Edit("script-1.json", text => text.Replace("$history:seed_read:1", "Restored fact: " + ReplayCoverage.Fact, StringComparison.Ordinal));
        }
        Assert.NotNull(ReplayAdmission.Load(bundle.Root).Fixture);
        var result = await ReplayRunner.RunAsync(bundle.Root);
        Assert.Equal("assertion_failed", result.Code);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.DoesNotContain(result.Steps, step => step.Accepted);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task LiteralAnswerCannotReplaceHistoryDerivationWithValidPredecessor(int phase)
    {
        using var bundle = new MutableBundle();
        bundle.Edit($"script-{phase}.json", text => text.Replace("$history:seed_read:1",
            "Restored fact: " + ReplayCoverage.Fact, StringComparison.Ordinal));
        Assert.NotNull(ReplayAdmission.Load(bundle.Root).Fixture);
        var result = await ReplayRunner.RunAsync(bundle.Root);
        Assert.Equal("assertion_failed", result.Code);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.Equal(phase + 1, result.Steps.Length);
        Assert.All(result.Steps.Take(phase), step => Assert.True(step.Accepted));
        Assert.False(result.Steps[phase].Accepted);
        Assert.True(result.Steps[phase].PredecessorPreserved);
        Assert.Null(result.Steps[phase].Generation);
        Assert.Null(result.Steps[phase].QualityCode);
    }

    [Theory]
    [InlineData(1, false, "assertion_failed")]
    [InlineData(2, false, "assertion_failed")]
    [InlineData(1, true, "assertion_failed")]
    public async Task RecoveryWithoutReplacementEvidenceRetainsFailedReplayAndPredecessor(int phase, bool invalidArguments, string expected)
    {
        using var bundle = new MutableBundle();
        bundle.EditJson($"script-{phase}.json", script =>
        {
            var call = script["turns"]![0]!["tool_calls"]![0]!;
            var arguments = JsonNode.Parse(call["arguments_json"]!.GetValue<string>())!;
            if (invalidArguments) arguments["start_line"] = 0;
            else arguments["path"] = "src/missing.txt";
            call["arguments_json"] = arguments.ToJsonString();
        });
        Assert.NotNull(ReplayAdmission.Load(bundle.Root).Fixture);
        var result = await ReplayRunner.RunAsync(bundle.Root);
        // Both parser and membership denials now recover. This unchanged script
        // finishes without obtaining the denied observation, so the replay's
        // required evidence still fails; recovery must not manufacture it.
        Assert.Equal(expected, result.Code);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.Equal(phase + 1, result.Steps.Length);
        Assert.All(result.Steps.Take(phase), step => Assert.True(step.Accepted));
        var failed = result.Steps[phase];
        Assert.Equal(expected, failed.Code);
        Assert.False(failed.Accepted);
        Assert.True(failed.PredecessorPreserved);
        Assert.Null(failed.Generation);
        Assert.Null(failed.QualityCode);
    }

    [Fact]
    public async Task RestoredExplicitNullToolArgumentRejectsEntireBatchAndPreservesPredecessor()
    {
        using var bundle = new MutableBundle();
        bundle.EditJson("script-1.json", script =>
        {
            script["turns"]![0]!["tool_calls"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "null_optional",
                ["name"] = "search_text",
                ["arguments_json"] = "{\"query\":\"needle\",\"path\":null}",
            });
        });
        Assert.NotNull(ReplayAdmission.Load(bundle.Root).Fixture);
        ReplayChildReply? rejected = null;
        var result = await ReplayRunner.RunAsync(bundle.Root, new()
        {
            ObserveReply = (input, _, reply) =>
            {
                if (input.Phase == 1) rejected = reply;
            },
        });

        Assert.Equal("assertion_failed", result.Code);
        Assert.Equal("cleaned", result.Cleanup);
        Assert.Equal(2, result.Steps.Length);
        Assert.True(result.Steps[0].Accepted);
        var failed = result.Steps[1];
        Assert.Equal("assertion_failed", failed.Code);
        Assert.False(failed.Accepted);
        Assert.True(failed.PredecessorPreserved);
        Assert.Equal(0, failed.ToolCalls);
        Assert.Null(failed.Generation);
        Assert.Null(failed.QualityCode);
        Assert.Null(result.Observations[1].EnvelopeSha256);
        Assert.NotNull(rejected);
        Assert.Equal("prepared", rejected.Code);
        Assert.Equal(0, rejected.ToolCalls);
        Assert.NotNull(rejected.Prepared);
        Assert.Equal(2, rejected.Requests.Length);
        using var retry = JsonDocument.Parse(rejected.Requests[1]);
        var messages = retry.RootElement.GetProperty("messages").EnumerateArray().ToArray();
        var rejectedCall = messages[^3].GetProperty("tool_calls").EnumerateArray()
            .Single(call => call.GetProperty("id").GetString() == "null_optional");
        Assert.Equal("{\"_apr_rejected\":true}", rejectedCall.GetProperty("function")
            .GetProperty("arguments").GetString());
        Assert.Equal("{\"status\":\"error\",\"code\":\"arguments_invalid\",\"retryable\":true}",
            messages[^1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task ClosedChildEnvironmentAndAllTransientChannelsExcludeAmbientCredentials()
    {
        var names = new[] { "GITHUB_TOKEN", "ACTIONS_RUNTIME_TOKEN", "AGENTIC_REVIEW_DEEPSEEK_API_KEY", "ARBITRARY_CANARY_CREDENTIAL", "AWS_SECRET_ACCESS_KEY", "NPM_TOKEN" };
        var prior = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var values = names.Select(_ => "APR246_SECRET_" + Guid.NewGuid().ToString("N")).ToArray();
        var inspected = false;
        try
        {
            for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], values[i]);
            var result = await ReplayRunner.RunAsync(Bundle, new()
            {
                Verify = (fixture, phase, reply) =>
                {
                    Assert.DoesNotContain(reply.EnvironmentKeys, names.Contains);
                    foreach (var value in values)
                    {
                        Assert.DoesNotContain(value, Encoding.UTF8.GetString(reply.EnvironmentBytes));
                        Assert.DoesNotContain(value, Encoding.UTF8.GetString(reply.Plaintext!));
                        Assert.All(reply.Requests, request => Assert.DoesNotContain(value, Encoding.UTF8.GetString(request)));
                    }
                    return ReplayCoverage.Verify(fixture, phase, reply);
                },
                BeforeCleanup = root =>
                {
                    inspected = true;
                    foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
                    {
                        var text = Encoding.UTF8.GetString(File.ReadAllBytes(file));
                        Assert.DoesNotContain("APRSES01", text);
                        foreach (var value in values) Assert.DoesNotContain(value, text);
                    }
                },
            });
            Assert.Equal("verified", result.Code);
            Assert.True(inspected);
            foreach (var value in values) Assert.DoesNotContain(value, Encoding.UTF8.GetString(ReplayWire.Write(result)));
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, prior[name]); }
    }

    [Fact]
    public async Task IndependentConcurrentInvocationsHaveDisjointPrivateState()
    {
        var roots = new System.Collections.Concurrent.ConcurrentBag<string>();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ReplayRunner.RunAsync(Bundle,
            new() { BeforeCleanup = root => roots.Add(root) })));
        Assert.All(results, result => Assert.Equal("verified", result.Code));
        Assert.Equal(2, roots.Distinct().Count());
        Assert.All(roots, root => Assert.False(Directory.Exists(root)));
        Assert.Equal(results[0].NormalizedSha256, results[1].NormalizedSha256);
    }

    [Fact]
    public async Task CleanupFailureIsExplicitAndCannotReturnSuccess()
    {
        string? retained = null;
        try
        {
            var result = await ReplayRunner.RunAsync(Bundle, new() { Cleanup = root => { retained = root; return false; } });
            Assert.Equal("cleanup_failed", result.Code);
            Assert.Equal("cleanup_failed", result.Cleanup);
            Assert.Equal(1, result.ExitCode);
            Assert.All(result.Steps, step => Assert.True(step.Accepted));
        }
        finally { if (retained is not null) Assert.True(ReplayProcess.Cleanup(retained)); }
    }

    [Fact]
    public async Task CancelledSupervisorAndInvalidBundleCleanPrivateArtifacts()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        string? created = null;
        var cancelled = await ReplayRunner.RunAsync(Bundle, new() { BeforeCleanup = root => created = root }, cancel.Token);
        Assert.Equal("cancelled", cancelled.Code);
        Assert.Equal("cleaned", cancelled.Cleanup);
        Assert.False(Directory.Exists(created));
        using var invalid = new MutableBundle();
        File.WriteAllText(Path.Combine(invalid.Root, "manifest.json"), "{}");
        var rejected = await ReplayRunner.RunAsync(invalid.Root);
        Assert.Equal("input_invalid", rejected.Code);
        Assert.Equal("cleaned", rejected.Cleanup);
        Assert.Empty(rejected.Steps);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"operation\":\"x\",\"operation\":\"y\"}")]
    [InlineData("{\"unknown\":true}")]
    [InlineData("[]")]
    public void ChildProtocolRejectsMalformedOrIncompleteDocuments(string json) =>
        Assert.Null(ReplayWire.Read(Encoding.UTF8.GetBytes(json), ReplayExecutionJson.Default.ReplayChildInput, ReplayWire.InputLimit));

    [Fact]
    public async Task PrivateProtocolBoundsUtf8AndStreams()
    {
        Assert.Null(ReplayWire.Read(new byte[] { 0xff }, ReplayExecutionJson.Default.ReplayChildReply, ReplayWire.ReplyLimit));
        Assert.Null(ReplayWire.Read(new byte[ReplayWire.InputLimit + 1], ReplayExecutionJson.Default.ReplayChildInput, ReplayWire.InputLimit));
        await Assert.ThrowsAsync<IOException>(() => ReplayWire.ReadBoundedAsync(new MemoryStream(new byte[100]), 99, CancellationToken.None));
    }

    private static async Task<ReplayReport> CommandAsync()
    {
        var root = ReplayProcess.CreatePrivateRoot();
        try
        {
            var start = ReplayProcess.StartInfo(root);
            start.Environment["TMP"] = Path.GetTempPath();
            start.Environment["TEMP"] = Path.GetTempPath();
            if (!OperatingSystem.IsWindows()) start.Environment["TMPDIR"] = Path.GetTempPath();
            start.ArgumentList.RemoveAt(start.ArgumentList.Count - 1);
            start.ArgumentList.Add("replay"); start.ArgumentList.Add("--bundle"); start.ArgumentList.Add(Bundle);
            using var process = Process.Start(start)!;
            process.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var output = ReplayWire.ReadBoundedAsync(process.StandardOutput.BaseStream, ReplayWire.ReplyLimit, deadline.Token);
            var error = ReplayWire.ReadBoundedAsync(process.StandardError.BaseStream, 4096, deadline.Token);
            try { await process.WaitForExitAsync(deadline.Token); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            Assert.Empty(await error);
            var result = ReplayWire.Read(await output, ReplayExecutionJson.Default.ReplayReport, ReplayWire.ReplyLimit);
            Assert.NotNull(result);
            Assert.Equal(result.ExitCode, process.ExitCode);
            return result;
        }
        finally { Assert.True(ReplayProcess.Cleanup(root)); }
    }

    private static async Task<ReplayChildReply> ProductionExhaustionReply(ReplayChildInput input, CancellationToken token)
    {
        var fixture = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Path.Combine(input.Root, "bundle")).Fixture);
        var run = Assert.Single(fixture.Runs);
        using var state = new ReplayState(run, input.Session, input.Root, input.Key);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable));
        var request = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable!.StablePlan, input.Session,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var boundary = PrefixBoundary.Bootstrap(state.Trusted, request);
        var history = new HistoryChatClient(boundary, new CountReplayChatClient(run.Script),
            HistoryChatClient.Measure(boundary, HistoryCapture.Request(request)));
        var reviewed = Path.Combine(input.Root, "reviewed");
        Directory.CreateDirectory(reviewed);
        var snapshot = run.CreateSnapshot(reviewed);
        var outcome = await new AgentLoop(history, new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot)))
            .RunAsync(request, token);
        Assert.False(outcome.Succeeded);
        Assert.Equal(AgentFailureCodes.ToolLimit, outcome.Diagnostic!.Code);
        Assert.Equal(512, outcome.Diagnostic.ToolCalls);
        Assert.Equal(33, outcome.Accounting!.ModelCalls);
        Assert.Equal(512, outcome.Events.OfType<AgentToolResultEvent>().Count());
        var attempt = EvaluationAttempt.Admit(state.Trusted, new(run.Input.Id, "deterministic", EvaluationSource.Commit,
            EvaluationSource.Tree, EvaluationSource.Clean, run.ProviderConfigurationSha256));
        var scored = EvaluationScorer.Failure(run.Expected, EvaluationFailure.FromAgentOutcome(outcome), attempt);
        return new(input.Operation, input.Corpus, input.Phase, input.Session, EvaluationSource.Commit, EvaluationSource.Tree,
            EvaluationSource.Clean, Environment.ProcessId, Guid.NewGuid().ToString("N"), "unknown_failed", null,
            EvaluationJson.Write(scored), null, null, outcome.Accounting!.ModelCalls,
            outcome.Events.OfType<AgentToolResultEvent>().Count(), null, [], [.. ReplayProcess.EnvironmentNames], [],
            PrefixHistory: history.Capture);
    }

    private sealed class CountReplayChatClient(ReplayScript script) : IProjectChatClient
    {
        private int ordinal;
        public Task<ProjectChatResponse> GetResponseAsync(ProjectChatRequest request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var turn = script.Turns[ordinal++];
            return Task.FromResult(new ProjectChatResponse(new("assistant", turn.ToolCalls.Select(call =>
                (ProjectChatContent)new ProjectToolCallContent(call.Id, call.Name, call.ArgumentsJson)).ToArray()), new(1, 1), 1));
        }
    }

    // Expand only a private copy. Keep the real provider, history, SESSION and state pipeline.
    private static void WriteCountReplay(MutableBundle bundle, int tools, int batchSize)
    {
        bundle.EditJson("script-0.json", script =>
        {
            var original = script["turns"]!.AsArray();
            var first = original[0]!;
            var calls = first["tool_calls"]!.AsArray();
            var turns = new JsonArray();
            for (var offset = 0; offset < tools; offset += batchSize)
            {
                var turn = first.DeepClone();
                turn["tool_calls"] = new JsonArray(Enumerable.Range(offset, Math.Min(batchSize, tools - offset)).Select(index =>
                {
                    return index < 2 ? calls[index]!.DeepClone() : new JsonObject
                    {
                        ["id"] = "count-" + index, ["name"] = "list_changed_files", ["arguments_json"] = "{}",
                    };
                }).ToArray());
                turns.Add(turn);
            }
            turns.Add(original[^1]!.DeepClone());
            script["turns"] = turns;
        });
        var unused = new[] { "context-1.txt", "context-2.txt", "script-1.json", "script-2.json" };
        bundle.EditJson("manifest.json", manifest =>
        {
            manifest["runs"] = new JsonArray(manifest["runs"]![0]!.DeepClone());
            manifest["files"] = new JsonArray(manifest["files"]!.AsArray()
                .Where(file => !unused.Contains(file!["path"]!.GetValue<string>())).Select(file => file!.DeepClone()).ToArray());
        });
        foreach (var file in unused) File.Delete(Path.Combine(bundle.Root, file));
    }

    private static bool VerifyCountReplay(AdmittedReplayFixture fixture, int phase, ReplayChildReply reply, int tools, int batchSize)
    {
        if (phase != 0 || fixture.Runs.Length != 1 || reply.ModelCalls != (tools + batchSize - 1) / batchSize + 1 ||
            reply.ToolCalls != tools || reply.Requests.Length != reply.ModelCalls || reply.Plaintext is null ||
            !AgentSessionCodec.TryParse(reply.Plaintext, out var artifact, out _) || artifact is null) return false;
        var run = artifact.Document.CompletedRuns.Single();
        var results = run.Records.OfType<AgentSessionToolResultRecord>().ToArray();
        var calls = fixture.Runs[0].Script.Turns.SelectMany(turn => turn.ToolCalls).Where(call => call.Name != "finish_review");
        if (!results.Select(result => result.CallId).SequenceEqual(calls.Select(call => call.Id)) || results.Length != tools ||
            run.Continuation.Items.Length != reply.ModelCalls || artifact.Document.Generation != 0) return false;
        foreach (var result in results)
        {
            using var parsed = JsonDocument.Parse(result.ResultJson);
            if (parsed.RootElement.GetProperty("status").GetString() != "ok") return false;
            if (result.Name == "read_file")
            {
                if (parsed.RootElement.GetProperty("lines")[0].GetProperty("text").GetString() !=
                    (result.CallId == "seed_read" ? ReplayCoverage.Fact : ReplayCoverage.Current)) return false;
            }
            else if (result.Name != "list_changed_files" || parsed.RootElement.GetProperty("changes").GetArrayLength() != 0) return false;
        }
        var outcome = run.Records.OfType<AgentSessionReviewOutcomeRecord>().Single();
        return outcome.Summary == "Seed complete." && outcome.FindingsJson == "[]";
    }

    private sealed class MutableBundle : IDisposable
    {
        private readonly string owner = ReplayProcess.CreatePrivateRoot();
        internal MutableBundle()
        {
            Root = Path.Combine(owner, "bundle"); Directory.CreateDirectory(Root);
            foreach (var file in Directory.GetFiles(Bundle)) File.Copy(file, Path.Combine(Root, Path.GetFileName(file)));
        }
        internal string Root { get; }
        internal void Edit(string file, Func<string, string> edit)
        { File.WriteAllText(Path.Combine(Root, file), edit(File.ReadAllText(Path.Combine(Root, file))), new UTF8Encoding(false)); Rehash(); }
        internal void EditJson(string file, Action<JsonNode> edit)
        { var json = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, file)))!; edit(json); File.WriteAllText(Path.Combine(Root, file), json.ToJsonString()); Rehash(); }
        private void Rehash()
        {
            var path = Path.Combine(Root, "manifest.json");
            var json = JsonNode.Parse(File.ReadAllText(path))!;
            foreach (var file in json["files"]!.AsArray())
            {
                var bytes = File.ReadAllBytes(Path.Combine(Root, file!["path"]!.GetValue<string>()));
                file["length"] = bytes.Length; file["sha256"] = AgentCanonical.HashRaw(bytes);
            }
            File.WriteAllText(path, json.ToJsonString());
        }
        public void Dispose() { Assert.True(ReplayProcess.Cleanup(owner)); }
    }
}
