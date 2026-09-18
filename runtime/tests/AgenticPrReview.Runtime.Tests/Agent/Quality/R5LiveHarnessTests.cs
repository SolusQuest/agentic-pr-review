using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

public sealed class R5LiveHarnessTests
{
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
        var result = await LiveRunner.RunAsync(plan.Path, false, Options(), CancellationToken.None);
        Assert.Equal("accounting_violation", result.StopReason);
        Assert.True(result.Summary.AccountingViolation);
        Assert.True(result.Summary.KnownInputTokens >= 3);
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
        Assert.True(result.Summary.TransportOutcomeCounts.Cancelled >= 1);
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
