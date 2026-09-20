using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5LiveHarnessTests
{
    [Theory]
    [InlineData(2, 5, 3)]
    [InlineData(0, 7, 3)]
    [InlineData(0, 0, 0)]
    public async Task CachePartitionReachesSafeSummaryWithBothValidatedModelIdentities(
        long hit, long miss, long output)
    {
        using var plan = new PlanFile();
        var lines = new List<string>();
        var count = 0;
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(lines, run =>
        {
            var replay = new ReplayTransport(run.Script, ReplayFault.None);
            return new FakeTransport(async (request, token) =>
            {
                using var sent = JsonDocument.Parse(request);
                Assert.Equal("deepseek-v4-flash", sent.RootElement.GetProperty("model").GetString());
                var original = await replay.SendAsync(request, token);
                var body = JsonNode.Parse(original.Body.AsSpan())!.AsObject();
                body["model"] = ++count % 2 == 0 ? "deepseek-flash" : "deepseek-v4-flash";
                body["id"] = Canary;
                body["system_fingerprint"] = Canary;
                body["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = hit + miss, ["completion_tokens"] = output,
                    ["total_tokens"] = hit + miss + output,
                    ["prompt_cache_hit_tokens"] = hit, ["prompt_cache_miss_tokens"] = miss,
                    ["prompt_tokens_details"] = new JsonObject { ["cached_tokens"] = 999 },
                    ["completion_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 999 },
                };
                return DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(body.ToJsonString()));
            });
        }), CancellationToken.None);

        Assert.Equal(1, result.Completed);
        Assert.True(count >= 2);
        Assert.Equal(count * (hit + miss), result.Summary.KnownInputTokens);
        Assert.Equal(count * output, result.Summary.KnownOutputTokens);
        Assert.Equal(count * (hit + miss + output), result.Summary.KnownCombinedTokens);
        Assert.Equal(0, result.Summary.UsageUnknownCalls);
        var cache = Assert.IsType<LiveCacheUsageSummary>(result.Summary.CacheUsage);
        Assert.Equal("measured", cache.Status);
        Assert.Equal(count, cache.MeasuredCalls);
        Assert.Equal(0, cache.KnownUsageWithoutCacheCalls);
        Assert.Equal(count * hit, cache.CacheReadInputTokens);
        Assert.Equal(count * miss, cache.UncachedInputTokens);
        Assert.Equal("deepseek", cache.ProviderId);
        Assert.Equal("deepseek-v4-flash", cache.RequestedModel);
        Assert.Equal(new[] { "deepseek-v4-flash", "deepseek-flash" }, cache.ResponseModels);
        Assert.Equal("not_applicable", cache.CacheWriteBillingStatus);
        Assert.Equal("unavailable", cache.BackendSnapshotStatus);
        Assert.DoesNotContain(Canary, string.Join('\n', lines));
        var restored = JsonSerializer.Deserialize(lines[^1], LiveJsonContext.Default.LiveRunSummary)!;
        Assert.Equal(JsonSerializer.Serialize(cache, LiveJsonContext.Default.LiveCacheUsageSummary),
            JsonSerializer.Serialize(restored.CacheUsage, LiveJsonContext.Default.LiveCacheUsageSummary));
    }

    [Theory]
    [InlineData(10, 2)]
    [InlineData(0, 0)]
    public async Task MissingOptionalCacheNeverMakesKnownTotalUsageUnknown(long input, long output)
    {
        var accounting = CacheAccounting();
        var client = new LiveChatObserver(new MinimalChatClient(new NoCacheBackend(input, output)), accounting);
        await client.GetResponseAsync(new([], [], null), CancellationToken.None);
        Assert.Equal(input, accounting.KnownInputTokens);
        Assert.Equal(output, accounting.KnownOutputTokens);
        Assert.Equal(input + output, accounting.KnownCombinedTokens);
        Assert.Equal(0, accounting.UsageUnknownCalls);
        Assert.Equal("unavailable", accounting.CacheUsage.Status);
        Assert.Null(accounting.CacheUsage.CacheReadInputTokens);
        Assert.Null(accounting.CacheUsage.UncachedInputTokens);
        Assert.Equal(1, accounting.CacheUsage.KnownUsageWithoutCacheCalls);
        Assert.Empty(accounting.CacheUsage.ResponseModels);
        AssertCacheRoundTrip(accounting.CacheUsage);

        accounting.RecordUsage(new(7, 3, new("deepseek", "deepseek-v4-flash", "deepseek-flash", 0, 7)));
        Assert.Equal("partial", accounting.CacheUsage.Status);
        Assert.Equal(0, accounting.CacheUsage.CacheReadInputTokens);
        Assert.Equal(7, accounting.CacheUsage.UncachedInputTokens);
        Assert.Equal(input + 7, accounting.KnownInputTokens);
        Assert.Equal(0, accounting.UsageUnknownCalls);
        AssertCacheRoundTrip(accounting.CacheUsage);
    }

    [Fact]
    public void CacheAvailabilityNeverFabricatesZeroAndDropsUntrustedOptionalFields()
    {
        var accounting = CacheAccounting();
        Assert.Equal("unavailable", accounting.CacheUsage.Status);
        Assert.Null(accounting.CacheUsage.CacheReadInputTokens);
        AssertCacheRoundTrip(accounting.CacheUsage);
        foreach (var observation in new ProjectProviderUsage[]
        {
            new(Canary, "deepseek-v4-flash", "deepseek-flash", 2, 5),
            new("deepseek", Canary, "deepseek-flash", 2, 5),
            new("deepseek", "deepseek-v4-flash", Canary, 2, 5),
            new("deepseek", "deepseek-v4-flash", "deepseek-flash", -1, 8),
            new("deepseek", "deepseek-v4-flash", "deepseek-flash", 2, 6),
            new("deepseek", "deepseek-v4-flash", "deepseek-flash", long.MaxValue, 1),
        }) accounting.RecordUsage(new(7, 3, observation));
        Assert.Equal(42, accounting.KnownInputTokens);
        Assert.Equal(0, accounting.UsageUnknownCalls);
        Assert.Equal("unavailable", accounting.CacheUsage.Status);
        Assert.Equal(6, accounting.CacheUsage.KnownUsageWithoutCacheCalls);
        Assert.DoesNotContain(Canary, AssertCacheRoundTrip(accounting.CacheUsage));
        Assert.False(accounting.AccountingViolation);
    }

    [Fact]
    public void CacheSubtotalRemainsPartialAfterUnknownUsageAndOverflowIsUnavailable()
    {
        var accounting = CacheAccounting();
        accounting.RecordUsage(new(7, 3, new("deepseek", "deepseek-v4-flash", "deepseek-flash", 2, 5)));
        accounting.RecordUsageUnknown();
        Assert.Equal("partial", accounting.CacheUsage.Status);
        Assert.Equal(2, accounting.CacheUsage.CacheReadInputTokens);
        Assert.Equal(0, accounting.CacheUsage.KnownUsageWithoutCacheCalls);
        Assert.Equal(1, accounting.UsageUnknownCalls);
        AssertCacheRoundTrip(accounting.CacheUsage);

        var overflow = CacheAccounting();
        for (var i = 0; i < 2; i++)
            overflow.RecordUsage(new(long.MaxValue, 0,
                new("deepseek", "deepseek-v4-flash", "deepseek-flash", long.MaxValue, 0)));
        Assert.Equal("unavailable", overflow.CacheUsage.Status);
        Assert.Null(overflow.CacheUsage.CacheReadInputTokens);
        Assert.Null(overflow.CacheUsage.UncachedInputTokens);
        AssertCacheRoundTrip(overflow.CacheUsage);
    }

    private static LiveAccounting CacheAccounting() => new(
        new(4, 8, 262144, 32768, 294912, 120, 100000, new(65536, 4096, 1000)));

    private static string AssertCacheRoundTrip(LiveCacheUsageSummary cache)
    {
        var json = JsonSerializer.Serialize(cache, LiveJsonContext.Default.LiveCacheUsageSummary);
        var restored = JsonSerializer.Deserialize(json, LiveJsonContext.Default.LiveCacheUsageSummary);
        Assert.Equal(json, JsonSerializer.Serialize(restored, LiveJsonContext.Default.LiveCacheUsageSummary));
        using var document = JsonDocument.Parse(json);
        Assert.Equal("not_applicable", document.RootElement.GetProperty("cache_write_billing_status").GetString());
        Assert.False(document.RootElement.TryGetProperty("cache_write_tokens", out _));
        return json;
    }

    private sealed class NoCacheBackend(long input, long output) : IMinimalChatBackend
    {
        public Task<MinimalChatResponse> GetResponseAsync(MinimalChatRequest request, CancellationToken token) =>
            Task.FromResult(new MinimalChatResponse(new("assistant", []), new(input, output)));
    }

    [Theory]
    [InlineData("sequence", AgentFailureCodes.TerminalSequenceInvalid)]
    [InlineData("arguments", AgentFailureCodes.ToolArgumentsInvalid)]
    [InlineData("unknown", AgentFailureCodes.UnknownTool)]
    [InlineData("grounding", AgentFailureCodes.TerminalInvalid)]
    public async Task FailedAttemptsRetainOnlyTypedDiagnosticsWithoutRetry(string fault, string code)
    {
        using var plan = new PlanFile(document =>
            document["schedule"]![0]!["repeats"] = 2);
        var calls = fault switch
        {
            "sequence" => new ReplayToolCall[]
            {
                new("finish", "finish_review", "{\"summary\":\"" + Canary + "\",\"findings\":[]}"),
                new("read", "read_file", "{\"path\":\"src/Caller.cs\",\"start_line\":1,\"line_count\":20}"),
            },
            "arguments" => [new("read", "read_file", "{\"path\":\"" + Canary + "\",\"start_line\":0}")],
            "unknown" => [new("unknown", Canary, "{}")],
            _ => [new("finish", "finish_review", "{\"summary\":\"" + Canary + "\",\"findings\":[{\"severity\":\"high\",\"title\":\"Test\",\"message\":\"" + Canary + "\",\"evidence\":[{\"observation_id\":\"" + new string('a', 64) + "\",\"path\":\"src/Caller.cs\",\"start_line\":3,\"end_line\":3}]}]}")],
        };
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(lines, _ =>
            new ReplayTransport(new([new([.. calls], Canary)]), ReplayFault.None)), CancellationToken.None);
        Assert.Equal(2, result.Failed);
        Assert.Equal(2, result.Summary.SimulatedAdapterCalls);
        Assert.Equal(0, result.Summary.ActualProviderCalls);
        Assert.Equal(new[] { 0, 1 }, result.Summary.AgentDiagnostics.Select(d => d.ScheduleIndex));
        Assert.All(result.Summary.AgentDiagnostics, d =>
        {
            Assert.Equal(code, d.Code);
            Assert.Equal(1, d.ModelCalls);
            Assert.InRange(d.ToolCalls!.Value, 0, AgentLimits.ToolCalls);
        });
        Assert.DoesNotContain(Canary, string.Join('\n', lines));
        var summary = JsonSerializer.Deserialize(lines[^1], LiveJsonContext.Default.LiveRunSummary)!;
        Assert.Equal(result.Summary.AgentDiagnostics.ToArray(), summary.AgentDiagnostics.ToArray());
    }

    [Fact]
    public void DiagnosticProjectionRejectsUnknownTextAndInvalidCounts()
    {
        var unknown = LiveAgentDiagnostic.Capture(0, new(Canary, 1, 2));
        Assert.Equal("unknown", unknown.Code);
        Assert.Equal(1, unknown.ModelCalls);
        foreach (var diagnostic in new AgentDiagnostic?[]
        {
            null, new(AgentFailureCodes.TerminalInvalid, -1, 0),
            new(AgentFailureCodes.TerminalInvalid, 1, AgentLimits.ToolCalls + 1),
        })
        {
            var safe = LiveAgentDiagnostic.Capture(1, diagnostic);
            Assert.Equal(new LiveAgentDiagnostic(1, "unknown", null, null), safe);
        }
    }

    private sealed class ReviewInput(StringWriter prompts, string action, string? staleField = null, bool empty = false) : TextReader
    {
        private string? command;
        private int position;
        internal string? Root { get; private set; }
        internal string? Packet { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (command is null)
            {
                Root = prompts.ToString().Split('\n')[0].TrimEnd('\r')
                    .Replace("r5_adjudication_private_directory ", "", StringComparison.Ordinal);
                Packet = File.ReadAllText(Path.Combine(Root, "review.json"));
                var path = Path.Combine(Root, "annotation.json");
                var annotation = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
                annotation["findings"] = new JsonArray(new JsonObject
                {
                    ["finding_ordinal"] = 0, ["verdict"] = "confirmed", ["defect_id"] = "defect",
                });
                if (action == "stale") annotation["execution_sha256"] = new string('0', 64);
                if (action == "duplicate") annotation["findings"]!.AsArray().Add(annotation["findings"]![0]!.DeepClone());
                if (empty) annotation["findings"] = new JsonArray();
                if (staleField is not null) annotation[staleField] = new string('0', 64);
                File.WriteAllText(path, action == "malformed" ? "{" : annotation.ToJsonString());
                command = action == "skip" ? "skip\n" : action == "eof" ? "" : action == "ai" ? "ai-adjudicated\n" : "human-confirmed\n";
            }
            if (position == command.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = command[position++];
            return ValueTask.FromResult(1);
        }
    }

    [Theory]
    [InlineData("confirm", "adjudicated", 1)]
    [InlineData("ai", "adjudicated", 1)]
    [InlineData("stale", "input_invalid", 0)]
    [InlineData("duplicate", "input_invalid", 0)]
    [InlineData("malformed", "input_invalid", 0)]
    [InlineData("skip", "pending", 0)]
    [InlineData("eof", "pending", 0)]
    public async Task AdjudicationUsesActualSubjectAndPreservesAccounting(string action, string status, int credit)
    {
        using var plan = new PlanFile(AdjudicationPlan);
        using var prompts = new StringWriter();
        using var input = new ReviewInput(prompts, action);
        var lines = new List<string>();
        var baseline = await LiveRunner.RunAsync(plan.Path, false, Options(), CancellationToken.None);
        Assert.True(baseline.Completed == 1, System.Text.Encoding.UTF8.GetString(EvaluationJson.Write(baseline.Outcomes[0])));
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = lines.Add, Adjudicator = new LiveAdjudicator(input, prompts),
        }, CancellationToken.None);
        Assert.Equal(status, result.Summary.AdjudicationStatus);
        Assert.Equal("cleaned", result.Summary.Cleanup);
        Assert.False(Directory.Exists(input.Root));
        Assert.Equal(baseline.Summary.SimulatedAdapterCalls, result.Summary.SimulatedAdapterCalls);
        Assert.Equal(0, result.Summary.ActualProviderCalls);
        Assert.Equal(baseline.Attempted, result.Attempted);
        var row = Assert.Single(result.Outcomes);
        Assert.Equal(credit, row.AdjudicatedTrue);
        Assert.Equal(action == "ai" ? 0 : credit, result.Summary.HumanConfirmedCases);
        Assert.Equal(action == "ai" ? credit : 0, result.Summary.AiAdjudicatedCases);
        Assert.Equal(credit == 1 ? ModelObservationStatus.Adjudicated : ModelObservationStatus.Unadjudicated, row.ModelStatus);
        Assert.All(lines.Take(1), line => Assert.NotNull(EvaluationJson.ReadOutcome(System.Text.Encoding.UTF8.GetBytes(line))));
        using var packet = JsonDocument.Parse(input.Packet!);
        var finding = packet.RootElement.GetProperty("findings")[0];
        Assert.DoesNotContain(finding.GetProperty("message").GetString()!, string.Join('\n', lines));
        Assert.True(packet.RootElement.GetProperty("source").TryGetProperty("src/Caller.cs", out _));
        Assert.True(packet.RootElement.GetProperty("diffs").GetArrayLength() > 0);
        Assert.DoesNotContain("reasoning_content", input.Packet!);
        Assert.DoesNotContain("continuation", input.Packet!);
    }

    private sealed class CancelledReviewInput : TextReader
    {
        public override async ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            return 0;
        }
    }

    [Theory]
    [InlineData("corpus_sha256")]
    [InlineData("case_sha256")]
    [InlineData("configuration_sha256")]
    [InlineData("execution_sha256")]
    [InlineData(null)]
    public async Task EvidenceFailureCannotBypassAnnotationAdmission(string? staleField)
    {
        using var plan = new PlanFile(document =>
        {
            AdjudicationPlan(document);
            document["schedule"] = new JsonArray(new JsonObject { ["case_id"] = "no-required-tool", ["repeats"] = 1 });
        });
        using var prompts = new StringWriter();
        // Null field tests an out-of-range ordinal against an empty completed review.
        using var input = new ReviewInput(prompts, "ai", staleField, empty: staleField is not null);
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { }, Adjudicator = new LiveAdjudicator(input, prompts),
        }, CancellationToken.None);
        Assert.Equal("input_invalid", result.Summary.AdjudicationStatus);
        Assert.Equal(0, result.Summary.AiAdjudicatedCases);
        Assert.Equal(0, result.Summary.HumanConfirmedCases);
        Assert.Equal("cleaned", result.Summary.Cleanup);
        Assert.False(Directory.Exists(input.Root));
        var row = Assert.Single(result.Outcomes);
        Assert.Equal(EvaluationStatus.Completed, row.ExecutionStatus);
        Assert.Equal(EvaluationCode.RequiredToolMissing, row.Code);
        Assert.Equal(ModelObservationStatus.NotEvaluated, row.ModelStatus);
    }

    [Fact]
    public async Task AdjudicationTimeoutRetainsRunAndCleansPrivatePacket()
    {
        using var plan = new PlanFile(AdjudicationPlan);
        using var prompts = new StringWriter();
        var result = await LiveRunner.RunAsync(plan.Path, false, new LiveOptions
        {
            WriteLine = _ => { },
            Adjudicator = new LiveAdjudicator(new CancelledReviewInput(), prompts) { Timeout = TimeSpan.FromMilliseconds(100) },
        }, CancellationToken.None);
        Assert.Equal("cancelled", result.Summary.AdjudicationStatus);
        Assert.Equal("cleaned", result.Summary.Cleanup);
        Assert.Equal(1, result.Completed);
        Assert.Equal(ModelObservationStatus.Unadjudicated, Assert.Single(result.Outcomes).ModelStatus);
    }

    private static void AdjudicationPlan(JsonObject document)
    {
        document["schedule"] = new JsonArray(new JsonObject { ["case_id"] = "cs-defect", ["repeats"] = 1 });
        document["bounds"]!["per_call"]!["max_input_tokens"] = 8192;
    }

    private const string Canary = "APR251_LIVE_CANARY_VALUE";
    private static string Corpus =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "quality", "bundle");

    private static string CorpusSha() =>
        Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture).CorpusSha256;

    private static JsonObject BasePlan() => new()
    {
        ["format"] = LiveLimits.PlanFormat,
        ["source"] = new JsonObject
        {
            ["commit"] = EvaluationSource.Commit,
            ["tree"] = EvaluationSource.Tree,
            ["clean"] = EvaluationSource.Clean,
        },
        ["corpus"] = new JsonObject { ["path"] = Corpus, ["sha256"] = CorpusSha() },
        ["provider"] = new JsonObject
        {
            ["provider_id"] = DeepSeekAdapterContext.Provider,
            ["model_id"] = DeepSeekAdapterContext.Model,
            ["adapter_id"] = DeepSeekAdapterContext.Adapter,
            ["configuration_sha256"] = LivePlanAdmission.ProviderConfigurationSha256(),
        },
        ["schedule"] = new JsonArray(new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 1 }),
        ["bounds"] = new JsonObject
        {
            ["max_evaluations"] = 4,
            ["max_model_calls"] = 8,
            ["max_input_tokens"] = 262144,
            ["max_output_tokens"] = 32768,
            ["max_combined_tokens"] = 294912,
            ["max_seconds"] = 120,
            ["spend_ceiling_micro_usd"] = 100000,
            ["per_call"] = new JsonObject
            {
                ["max_input_tokens"] = 65536,
                ["max_output_tokens"] = 4096,
                ["max_charge_micro_usd"] = 1000,
            },
        },
    };

    private sealed class PlanFile : IDisposable
    {
        internal string Root { get; } =
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "r5-live-" + Guid.NewGuid().ToString("N"));
        internal string Path { get; }
        internal PlanFile(Action<JsonObject>? edit = null)
        {
            Directory.CreateDirectory(Root);
            var document = BasePlan();
            edit?.Invoke(document);
            Path = System.IO.Path.Combine(Root, "plan.json");
            File.WriteAllText(Path, document.ToJsonString());
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }

    private static LiveOptions Options(
        List<string>? lines = null,
        Func<AdmittedReplayRun, IDeepSeekTransport>? transport = null) => new()
    {
        WriteLine = lines is null ? _ => { } : lines.Add,
        DryRunTransport = transport ?? (run => new ReplayTransport(run.Script, ReplayFault.None)),
    };

    private static LiveAdmissionCode RejectPlan(Action<JsonObject> edit)
    {
        using var plan = new PlanFile(edit);
        try
        {
            LivePlanAdmission.Load(plan.Path, execute: false, CancellationToken.None);
            return LiveAdmissionCode.Admitted;
        }
        catch (LivePlanRejected rejection)
        {
            return rejection.Code;
        }
    }

    private sealed class FakeTransport(
        Func<ReadOnlyMemory<byte>, CancellationToken, Task<DeepSeekTransportResult>> send)
        : IDeepSeekTransport
    {
        internal int Calls { get; private set; }
        public Task<DeepSeekTransportResult> SendAsync(
            ReadOnlyMemory<byte> requestBody, CancellationToken cancellationToken)
        {
            Calls++;
            return send(requestBody, cancellationToken);
        }
        public void Dispose() { }
    }

    private sealed class FakeSecret(string? value) : ILiveSecretSource
    {
        internal int Taken { get; private set; }
        public string? TakeProviderCredential() { Taken++; return value; }
    }

    private sealed class FakeFactory(Func<DeepSeekCredential, IDeepSeekTransport> create) : ILiveTransportFactory
    {
        internal int Calls { get; private set; }
        public IDeepSeekTransport Create(DeepSeekCredential credential) { Calls++; return create(credential); }
    }

    [Fact]
    public void ValidPlanAdmitsWithExpandedScheduleAndBoundDigest()
    {
        using var plan = new PlanFile(document =>
            document["schedule"] = new JsonArray(
                new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 2 },
                new JsonObject { ["case_id"] = "cs-defect", ["repeats"] = 1 }));
        var admitted = LivePlanAdmission.Load(plan.Path, execute: false, CancellationToken.None);
        Assert.True(admitted.Schedule.SequenceEqual(new[] { "cs-safe", "cs-safe", "cs-defect" }));
        Assert.Equal(64, admitted.Digest.Length);
        Assert.Equal(CorpusSha(), admitted.Corpus.Sha256);
        var mutated = LivePlanAdmission.Load(
            new PlanFile(document =>
            {
                document["schedule"] = new JsonArray(
                    new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 2 },
                    new JsonObject { ["case_id"] = "cs-defect", ["repeats"] = 1 });
                document["bounds"]!["max_seconds"] = 121;
            }).Path, execute: false, CancellationToken.None);
        Assert.NotEqual(admitted.Digest, mutated.Digest);
    }

    [Theory]
    [InlineData("format")]
    [InlineData("missing-source")]
    [InlineData("unknown-field")]
    [InlineData("bad-format")]
    [InlineData("bad-commit")]
    [InlineData("bad-tree")]
    [InlineData("wrong-commit")]
    [InlineData("wrong-clean")]
    [InlineData("bad-corpus-hash")]
    [InlineData("bad-provider")]
    [InlineData("bad-model")]
    [InlineData("bad-adapter")]
    [InlineData("bad-config-hash")]
    [InlineData("empty-schedule")]
    [InlineData("bad-case")]
    [InlineData("zero-repeats")]
    [InlineData("huge-repeats")]
    [InlineData("schedule-overflow")]
    [InlineData("zero-evals")]
    [InlineData("zero-calls")]
    [InlineData("zero-seconds")]
    [InlineData("negative-tokens")]
    [InlineData("oversize-evals")]
    [InlineData("oversize-calls")]
    [InlineData("percall-input-over-total")]
    [InlineData("percall-output-over-wire")]
    [InlineData("percall-over-combined")]
    [InlineData("zero-percall-charge")]
    [InlineData("ceiling-below-charge")]
    [InlineData("missing-percall")]
    [InlineData("too-large")]
    public void MalformedOrInconsistentPlansRejectBeforeCorpusOrCredential(string fault)
    {
        if (fault == "too-large")
        {
            var root = Path.Combine(Path.GetTempPath(), "r5-live-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "plan.json");
            File.WriteAllBytes(path, new byte[LiveLimits.PlanBytes + 1]);
            var thrown = Assert.Throws<LivePlanRejected>(() =>
                LivePlanAdmission.Load(path, execute: false, CancellationToken.None));
            Assert.Equal(LiveAdmissionCode.InvalidPlan, thrown.Code);
            Directory.Delete(root, true);
            return;
        }
        var expected = fault switch
        {
            "wrong-commit" or "wrong-clean" => LiveAdmissionCode.InvalidSource,
            "bad-provider" or "bad-model" or "bad-adapter" or "bad-config-hash" =>
                LiveAdmissionCode.UnsupportedConfiguration,
            "ceiling-below-charge" => LiveAdmissionCode.Unpriceable,
            _ => LiveAdmissionCode.InvalidPlan,
        };
        var code = RejectPlan(document =>
        {
            switch (fault)
            {
                case "format": document["format"] = "other"; break;
                case "missing-source": document.Remove("source"); break;
                case "unknown-field": document["extra"] = 1; break;
                case "bad-format": document["format"] = 42; break;
                case "bad-commit": document["source"]!["commit"] = "zzzz"; break;
                case "bad-tree": document["source"]!["tree"] = new string('g', 40); break;
                case "wrong-commit": document["source"]!["commit"] = new string('0', 40); break;
                case "wrong-clean": document["source"]!["clean"] = !EvaluationSource.Clean; break;
                case "bad-corpus-hash": document["corpus"]!["sha256"] = "xyz"; break;
                case "bad-provider": document["provider"]!["provider_id"] = "other"; break;
                case "bad-model": document["provider"]!["model_id"] = "other"; break;
                case "bad-adapter": document["provider"]!["adapter_id"] = new string('0', 64); break;
                case "bad-config-hash": document["provider"]!["configuration_sha256"] = new string('0', 64); break;
                case "empty-schedule": document["schedule"] = new JsonArray(); break;
                case "bad-case": document["schedule"]![0]!["case_id"] = "bad case!"; break;
                case "zero-repeats": document["schedule"]![0]!["repeats"] = 0; break;
                case "huge-repeats": document["schedule"]![0]!["repeats"] = 17; break;
                case "schedule-overflow":
                    document["schedule"] = new JsonArray(Enumerable.Range(0, 17)
                        .Select(i => new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 16 }).ToArray<JsonNode?>());
                    document["bounds"]!["max_evaluations"] = 256;
                    document["bounds"]!["max_model_calls"] = 2048;
                    document["bounds"]!["max_input_tokens"] = 67108864;
                    document["bounds"]!["max_output_tokens"] = 8388608;
                    document["bounds"]!["max_combined_tokens"] = 75497472;
                    document["bounds"]!["spend_ceiling_micro_usd"] = 100000000;
                    break;
                case "zero-evals": document["bounds"]!["max_evaluations"] = 0; break;
                case "zero-calls": document["bounds"]!["max_model_calls"] = 0; break;
                case "zero-seconds": document["bounds"]!["max_seconds"] = 0; break;
                case "negative-tokens": document["bounds"]!["max_input_tokens"] = -1; break;
                case "oversize-evals": document["bounds"]!["max_evaluations"] = 257; break;
                case "oversize-calls": document["bounds"]!["max_model_calls"] = 33; break;
                case "percall-input-over-total": document["bounds"]!["per_call"]!["max_input_tokens"] = 524289; break;
                case "percall-output-over-wire": document["bounds"]!["per_call"]!["max_output_tokens"] = 4097; break;
                case "percall-over-combined": document["bounds"]!["max_combined_tokens"] = 1000; break;
                case "zero-percall-charge": document["bounds"]!["per_call"]!["max_charge_micro_usd"] = 0; break;
                case "ceiling-below-charge": document["bounds"]!["spend_ceiling_micro_usd"] = 500; break;
                case "missing-percall": document["bounds"]!.AsObject().Remove("per_call"); break;
            }
        });
        Assert.Equal(expected, code);
    }

    [Fact]
    public async Task CancelledTokenPropagatesAsCancellationNotAdmissionRejection()
    {
        using var plan = new PlanFile();
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            LiveRunner.RunAsync(plan.Path, false, Options(), cancel.Token));
    }

    [Fact]
    public async Task CorpusMismatchAndUnknownCaseRejectAsInvalidCorpus()
    {
        using var wrongSha = new PlanFile(document => document["corpus"]!["sha256"] = new string('0', 64));
        var thrown = await Assert.ThrowsAsync<LivePlanRejected>(() =>
            LiveRunner.RunAsync(wrongSha.Path, false, Options(), CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidCorpus, thrown.Code);
        using var unknownCase = new PlanFile(document =>
            document["schedule"]![0]!["case_id"] = "no-such-case");
        thrown = await Assert.ThrowsAsync<LivePlanRejected>(() =>
            LiveRunner.RunAsync(unknownCase.Path, false, Options(), CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidCorpus, thrown.Code);
    }

    [Fact]
    public async Task DryRunRunsRealAdapterPathWithCompleteAccounting()
    {
        using var plan = new PlanFile(document =>
        {
            document["schedule"] = new JsonArray(
                new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 2 },
                new JsonObject { ["case_id"] = "cs-defect", ["repeats"] = 1 });
            document["bounds"]!["max_model_calls"] = 24;
            document["bounds"]!["max_input_tokens"] = 786432;
            document["bounds"]!["max_output_tokens"] = 98304;
            document["bounds"]!["max_combined_tokens"] = 884736;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 32768;
        });
        var lines = new List<string>();
        var transports = new List<ReplayTransport>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(lines, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("complete", result.StopReason);
        Assert.Equal(3, result.Attempted);
        Assert.Equal(3, result.Summary.Scheduled);
        Assert.Equal(0, result.Summary.Unattempted);
        Assert.Equal(3, result.Outcomes.Length);
        Assert.Equal(3, result.Outcomes.Select(o => o.AttemptSha256).Distinct().Count());
        Assert.All(result.Outcomes, outcome => Assert.Equal("deterministic", outcome.Mode));
        Assert.Equal(result.Attempted, result.Completed + result.Failed + result.Invalid);
        Assert.True(result.Summary.SimulatedAdapterCalls >= 3);
        Assert.Equal(0, result.Summary.TransportOutcomeCounts.TransportFailure);
        Assert.Equal(0, result.Summary.ActualProviderCalls);
        Assert.Equal(3, transports.Count);
        Assert.All(transports, transport => Assert.True(transport.Requests.Count >= 1));
        Assert.True(result.Summary.ReservedInputTokens >= result.Summary.KnownInputTokens);
        Assert.True(result.Summary.ReservedSpendMicroUsd > 0);
        Assert.False(result.Summary.AccountingViolation);
        // Every output line is bounded public-safe JSON; no caller text or paths leak.
        Assert.Equal(result.Outcomes.Length + 2, lines.Count);
        Assert.All(lines, line =>
        {
            Assert.DoesNotContain(Corpus, line);
            using var document = JsonDocument.Parse(line);
            Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        });
        using var summaryDocument = JsonDocument.Parse(lines[^1]);
        Assert.Equal("r5-live-local-v1", summaryDocument.RootElement.GetProperty("format").GetString());
        Assert.Equal("loopback", summaryDocument.RootElement.GetProperty("execution_kind").GetString());
        Assert.Equal(64, summaryDocument.RootElement.GetProperty("plan_sha256").GetString()!.Length);
    }

    [Fact]
    public async Task SamePlanInvocationsProduceDisjointAttemptIdentities()
    {
        using var plan = new PlanFile(document =>
        {
            document["schedule"]![0]!["repeats"] = 2;
            document["bounds"]!["max_model_calls"] = 16;
            document["bounds"]!["max_input_tokens"] = 524288;
            document["bounds"]!["max_output_tokens"] = 65536;
            document["bounds"]!["max_combined_tokens"] = 589824;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 32768;
        });
        var first = await LiveRunner.RunAsync(plan.Path, false, Options(), CancellationToken.None);
        var second = await LiveRunner.RunAsync(plan.Path, false, Options(), CancellationToken.None);
        Assert.Empty(first.Outcomes.Select(o => o.AttemptSha256)
            .Intersect(second.Outcomes.Select(o => o.AttemptSha256)));
        Assert.Equal(2, first.Outcomes.Length);
    }

    [Fact]
    public async Task ReservationsNeverReleaseUsageBelowBoundDoesNotUnlockAnotherSend()
    {
        // total=100 per-call=60: first send reserves 60, second needs 60 more -> refuse.
        using var plan = new PlanFile(document =>
        {
            document["bounds"]!["max_input_tokens"] = 100;
            document["bounds"]!["max_combined_tokens"] = 4196;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 60;
        });
        var transports = new List<ReplayTransport>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("bound_stop", result.StopReason);
        Assert.Equal(1, transports.Sum(t => t.Requests.Count));
        Assert.True(result.Summary.ReservedInputTokens == 60);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.BudgetRefused);
        // The refused send never reached the provider: no usage existed to be
        // unknown, so it must not inflate usage_unknown_calls.
        Assert.Equal(0, result.Summary.UsageUnknownCalls);
    }

    [Fact]
    public async Task ModelCallAndSpendCeilingsStopBeforeSend()
    {
        using var callsPlan = new PlanFile(document => document["bounds"]!["max_model_calls"] = 1);
        var transports = new List<ReplayTransport>();
        var callsResult = await LiveRunner.RunAsync(callsPlan.Path, false, Options(null, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("bound_stop", callsResult.StopReason);
        Assert.Equal(1, transports.Sum(t => t.Requests.Count));

        using var spendPlan = new PlanFile(document =>
            document["bounds"]!["spend_ceiling_micro_usd"] = 1000);
        transports = [];
        var spendResult = await LiveRunner.RunAsync(spendPlan.Path, false, Options(null, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("bound_stop", spendResult.StopReason);
        Assert.Equal(1, transports.Sum(t => t.Requests.Count));
        Assert.Equal(1000, spendResult.Summary.ReservedSpendMicroUsd);
    }

    [Fact]
    public async Task RateLimitedProviderOutcomeStopsSchedule()
    {
        using var plan = new PlanFile(document =>
        {
            document["schedule"] = new JsonArray(
                new JsonObject { ["case_id"] = "cs-safe", ["repeats"] = 2 },
                new JsonObject { ["case_id"] = "cs-defect", ["repeats"] = 1 });
            document["bounds"]!["max_model_calls"] = 24;
            document["bounds"]!["max_input_tokens"] = 786432;
            document["bounds"]!["max_output_tokens"] = 98304;
            document["bounds"]!["max_combined_tokens"] = 884736;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 32768;
        });
        var sends = 0;
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, run =>
        {
            var inner = new ReplayTransport(run.Script, ReplayFault.None);
            return (IDeepSeekTransport)new FakeTransport((body, token) =>
                ++sends == 1
                    ? Task.FromResult(DeepSeekTransportResult.HttpFailure(
                        DeepSeekHttpStatusClass.TooManyRequests, 0))
                    : inner.SendAsync(body, token));
        }), CancellationToken.None);
        Assert.Equal("rate_limited", result.StopReason);
        Assert.Equal(1, sends);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.Http429);
        Assert.Equal(0, Assert.Single(result.Summary.AgentDiagnostics).ScheduleIndex);
        Assert.Equal(1, result.Attempted);
        Assert.Equal(2, result.Summary.Unattempted);
        Assert.Equal(EvaluationStatus.Failed, result.Outcomes[0].ExecutionStatus);
    }

    [Fact]
    public async Task UsageAboveAuthorizedPerCallBoundIsAccountingViolation()
    {
        // Loopback reports 3 input tokens per call; authorizing only 2 must violate.
        using var plan = new PlanFile(document =>
        {
            document["bounds"]!["per_call"]!["max_input_tokens"] = 2;
            document["bounds"]!["max_combined_tokens"] = 12294;
        });
        var transports = new List<ReplayTransport>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("accounting_violation", result.StopReason);
        Assert.True(result.Summary.AccountingViolation);
        Assert.True(result.Summary.KnownInputTokens >= 3);
        // The falsified reservation basis refuses the next send inside the same
        // evaluation: exactly one provider request escaped.
        Assert.Equal(1, transports.Sum(t => t.Requests.Count));
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.ViolationRefused);
        Assert.Equal(0, result.Summary.UsageUnknownCalls);
    }

    [Fact]
    public async Task SpendReservationArithmeticCannotWrapPastTheCeiling()
    {
        // near-long.MaxValue ceiling + per-call charge: addition would overflow
        // negative on the second reservation, so the gate must compare by
        // subtraction instead.
        using var plan = new PlanFile(document =>
        {
            document["bounds"]!["spend_ceiling_micro_usd"] = long.MaxValue;
            document["bounds"]!["per_call"]!["max_charge_micro_usd"] = long.MaxValue;
        });
        var transports = new List<ReplayTransport>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, run =>
        {
            var transport = new ReplayTransport(run.Script, ReplayFault.None);
            transports.Add(transport);
            return transport;
        }), CancellationToken.None);
        Assert.Equal("bound_stop", result.StopReason);
        Assert.Equal(1, transports.Sum(t => t.Requests.Count));
        Assert.Equal(long.MaxValue, result.Summary.ReservedSpendMicroUsd);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.BudgetRefused);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task ResponseTooLargeCountsOnlyAsUsageUnknown(int oversizedCall)
    {
        using var plan = new PlanFile();
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(lines, run =>
        {
            var inner = new ReplayTransport(run.Script, ReplayFault.None);
            var calls = 0;
            return new FakeTransport(async (body, token) =>
            {
                if (++calls == oversizedCall) return DeepSeekTransportResult.ResponseTooLarge();
                var original = await inner.SendAsync(body, token);
                var response = JsonNode.Parse(original.Body.AsSpan())!.AsObject();
                response["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = 7, ["completion_tokens"] = 3, ["total_tokens"] = 10,
                    ["prompt_cache_hit_tokens"] = 2, ["prompt_cache_miss_tokens"] = 5,
                };
                return DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes(response.ToJsonString()));
            });
        }), CancellationToken.None);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.ResponseTooLarge);
        Assert.Equal(1, result.Summary.UsageUnknownCalls);
        Assert.Equal(AgentFailureCodes.ResponseTooLarge, Assert.Single(result.Summary.AgentDiagnostics).Code);
        Assert.Equal(oversizedCall * 1000, result.Summary.ReservedSpendMicroUsd);
        var measured = oversizedCall - 1;
        Assert.Equal(measured * 7, result.Summary.KnownInputTokens);
        Assert.Equal(measured * 3, result.Summary.KnownOutputTokens);
        Assert.Equal(measured * 10, result.Summary.KnownCombinedTokens);
        var cache = Assert.IsType<LiveCacheUsageSummary>(result.Summary.CacheUsage);
        Assert.Equal(measured == 0 ? "unavailable" : "partial", cache.Status);
        Assert.Equal(measured, cache.MeasuredCalls);
        Assert.Equal(0, cache.KnownUsageWithoutCacheCalls);
        Assert.Equal(measured == 0 ? (long?)null : 2, cache.CacheReadInputTokens);
        Assert.Equal(measured == 0 ? (long?)null : 5, cache.UncachedInputTokens);
        var restored = JsonSerializer.Deserialize(lines[^1], LiveJsonContext.Default.LiveRunSummary)!;
        Assert.Equal(AssertCacheRoundTrip(cache), AssertCacheRoundTrip(restored.CacheUsage!));
    }

    [Fact]
    public void DuplicatePlanFieldsRejectAsMalformed()
    {
        var root = Path.Combine(Path.GetTempPath(), "r5-live-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "plan.json");
        var document = BasePlan().ToJsonString();
        // A duplicated authorization field must reject, not silently last-win.
        File.WriteAllText(path, document.Replace(
            "\"spend_ceiling_micro_usd\":100000",
            "\"spend_ceiling_micro_usd\":1,\"spend_ceiling_micro_usd\":100000"));
        var thrown = Assert.Throws<LivePlanRejected>(() =>
            LivePlanAdmission.Load(path, execute: false, CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidPlan, thrown.Code);
        File.WriteAllText(path, document.Replace(
            "\"max_charge_micro_usd\":1000",
            "\"max_charge_micro_usd\":1,\"max_charge_micro_usd\":1000"));
        thrown = Assert.Throws<LivePlanRejected>(() =>
            LivePlanAdmission.Load(path, execute: false, CancellationToken.None));
        Assert.Equal(LiveAdmissionCode.InvalidPlan, thrown.Code);
        Directory.Delete(root, true);
    }

    [Fact]
    public async Task DeadlineCancelsInFlightSendWithoutAnotherAttempt()
    {
        using var plan = new PlanFile(document =>
        {
            document["bounds"]!["max_seconds"] = 1;
            document["schedule"]![0]!["repeats"] = 3;
            document["bounds"]!["max_model_calls"] = 24;
            document["bounds"]!["max_input_tokens"] = 786432;
            document["bounds"]!["max_output_tokens"] = 98304;
            document["bounds"]!["max_combined_tokens"] = 884736;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 32768;
        });
        var sends = 0;
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, _ =>
            (IDeepSeekTransport)new FakeTransport(async (_, token) =>
            {
                sends++;
                await Task.Delay(Timeout.Infinite, token);
                return DeepSeekTransportResult.TransportFailure();
            })), CancellationToken.None);
        Assert.Equal("deadline", result.StopReason);
        Assert.Equal(1, sends);
        // The token-registration path records the cancellation when the token
        // fires, deterministically before the abandoned continuation races the
        // summary publication.
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.Cancelled);
        Assert.Equal(1, result.Attempted);
        Assert.Equal(2, result.Summary.Unattempted);
    }

    [Fact]
    public async Task CallerCancellationStopsAndIsReported()
    {
        using var plan = new PlanFile(document =>
        {
            document["schedule"]![0]!["repeats"] = 3;
            document["bounds"]!["max_model_calls"] = 24;
            document["bounds"]!["max_input_tokens"] = 786432;
            document["bounds"]!["max_output_tokens"] = 98304;
            document["bounds"]!["max_combined_tokens"] = 884736;
            document["bounds"]!["per_call"]!["max_input_tokens"] = 32768;
        });
        using var cancel = new CancellationTokenSource();
        var sends = 0;
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, _ =>
            (IDeepSeekTransport)new FakeTransport(async (_, token) =>
            {
                if (++sends == 2) cancel.Cancel();
                await Task.Delay(50, token);
                return DeepSeekTransportResult.TransportFailure();
            })), cancel.Token);
        Assert.Equal("caller_cancelled", result.StopReason);
        Assert.True(sends <= 2);
        Assert.True(result.Summary.Unattempted >= 1);
        Assert.Equal(Enumerable.Range(0, result.Attempted), result.Summary.AgentDiagnostics.Select(d => d.ScheduleIndex));
    }

    [Fact]
    public async Task TransportFailureIsNotRetriedAndFailsTheAttempt()
    {
        using var plan = new PlanFile(document => document["schedule"]![0]!["repeats"] = 1);
        var sends = 0;
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(null, _ =>
            (IDeepSeekTransport)new FakeTransport((_, _) =>
            {
                sends++;
                return Task.FromResult(DeepSeekTransportResult.TransportFailure());
            })), CancellationToken.None);
        Assert.Equal(1, sends);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.TransportFailure);
        Assert.Equal(EvaluationStatus.Failed, result.Outcomes[0].ExecutionStatus);
        Assert.Equal(EvaluationFailureSource.Unknown, result.Outcomes[0].FailureSource);
        Assert.Equal(1, result.Summary.TransportOutcomeCounts.BackendExceptions);
        Assert.Equal(1, result.Summary.UsageUnknownCalls);
        Assert.Equal("complete", result.StopReason);
        Assert.Equal(AgentFailureCodes.ChatFailed, Assert.Single(result.Summary.AgentDiagnostics).Code);
    }

    [Fact]
    public async Task ExecuteRejectsMissingOrMalformedCredentialBeforeTransport()
    {
        using var plan = new PlanFile();
        var secret = new FakeSecret(null);
        var factory = new FakeFactory(_ => throw new InvalidOperationException("must not be constructed"));
        var code = await Assert.ThrowsAsync<LivePlanRejected>(() =>
            LiveRunner.RunAsync(plan.Path, true,
                new LiveOptions { SecretSource = secret, TransportFactory = factory, WriteLine = _ => { } },
                CancellationToken.None));
        Assert.True(code.Code is LiveAdmissionCode.SecretInvalid or LiveAdmissionCode.InvalidSource);
        Assert.Equal(0, factory.Calls);
        // When the source gate admits the plan (clean builds), the missing
        // credential is read exactly once and still reaches no transport.
        Assert.True(secret.Taken <= 1);

        var malformed = new FakeSecret("line\nbreak");
        code = await Assert.ThrowsAsync<LivePlanRejected>(() =>
            LiveRunner.RunAsync(plan.Path, true,
                new LiveOptions { SecretSource = malformed, TransportFactory = factory, WriteLine = _ => { } },
                CancellationToken.None));
        Assert.True(code.Code is LiveAdmissionCode.SecretInvalid or LiveAdmissionCode.InvalidSource);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public void EnvironmentSecretSourceReadsAndClearsProviderVariableOnly()
    {
        var variable = LiveEnvironmentSecretSource.ProviderVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, Canary);
            var source = new LiveEnvironmentSecretSource();
            Assert.Equal(Canary, source.TakeProviderCredential());
            Assert.Null(Environment.GetEnvironmentVariable(variable));
            Assert.Null(source.TakeProviderCredential());
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    [Fact]
    public async Task DryRunNeverReadsProviderCredential()
    {
        var variable = LiveEnvironmentSecretSource.ProviderVariable;
        var previous = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, Canary);
            using var plan = new PlanFile();
            var lines = new List<string>();
            var result = await LiveRunner.RunAsync(plan.Path, false, Options(lines), CancellationToken.None);
            Assert.Equal("complete", result.StopReason);
            // The variable is untouched: dry-run performs no credential access.
            Assert.Equal(Canary, Environment.GetEnvironmentVariable(variable));
            Assert.All(lines, line => Assert.DoesNotContain(Canary, line));
        }
        finally { Environment.SetEnvironmentVariable(variable, previous); }
    }

    private static bool CleanBuild() => EvaluationSource.Clean;

    [Fact]
    public async Task ExecuteWithAuthorizedCredentialPassesItToTransportOnly()
    {
        if (!CleanBuild()) return; // execute admission requires a clean-built evaluator
        using var plan = new PlanFile();
        var secret = new FakeSecret(Canary);
        var received = new List<string>();
        var lines = new List<string>();
        var result = await LiveRunner.RunAsync(plan.Path, true, new LiveOptions
        {
            SecretSource = secret,
            TransportFactory = new FakeFactory(credential =>
            {
                received.Add(credential.Value);
                return new ReplayTransport(
                    Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(Corpus).Fixture)
                        .Runs.First(run => run.Input.CaseId == "cs-safe").Script,
                    ReplayFault.None);
            }),
            WriteLine = lines.Add,
        }, CancellationToken.None);
        Assert.Equal(1, secret.Taken);
        Assert.Equal([Canary], received);
        Assert.All(lines, line => Assert.DoesNotContain(Canary, line));
        Assert.All(result.Outcomes, outcome => Assert.Equal("live", outcome.Mode));
        Assert.True(result.Summary.ActualProviderCalls >= 1);
        Assert.Equal(0, result.Summary.SimulatedAdapterCalls);
        Assert.Equal("live", result.Summary.ExecutionKind);
    }
}
