using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.Tests.Host.Action;
using EvaluatorProgram = AgenticPrReview.Runtime.ReviewEvaluationFixture.Program;

namespace AgenticPrReview.Runtime.Tests.Agent.Quality;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class R6EconomicsRunnerTests
{
    private const string Canary = "APR278_PRIVATE_CANARY";

    [Theory]
    [InlineData(0, 17)]
    [InlineData(1, 18)]
    [InlineData(1000, 18)]
    [InlineData(1001, 19)]
    [InlineData(60000, 77)]
    public void PreparedCampaignTimeIncludesSetupPerSlotSupervisionAndRoundedSpacing(int spacing, long seconds)
    {
        using var files = new Inputs();
        var plan = EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            [new("replay", 2, 1, false)], childSeconds: 1, spacingMilliseconds: spacing);
        Assert.Equal(seconds, plan.Bounds.MaxSeconds);
        Assert.NotNull(EconomicsPlan.Admit(plan, false));
        var rejected = Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(
            plan with { Bounds = plan.Bounds with { MaxSeconds = seconds - 1 } }, false));
        Assert.Equal("r6_economics_allocation_invalid", rejected.Code);
    }

    [Fact]
    public void CandidateEconomicsPlanBindsFullChildAllocationAndTrustedIdentity()
    {
        using var files = new Inputs();
        var original = files.Plan;
        var calls = original.Bounds.MaxModelCalls;
        var per = original.Bounds.PerCall with { MaxOutputTokens = 8192 };
        var input = original with
        {
            Provider = original.Provider with
            {
                AdapterId = DeepSeekAdapterContext.CandidateAdapter,
                ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256(DeepSeekRequestProfile.Output8192),
            },
            Bounds = original.Bounds with
            {
                PerCall = per,
                MaxOutputTokens = calls * per.MaxOutputTokens,
                MaxCombinedTokens = calls * (per.MaxInputTokens + per.MaxOutputTokens),
            },
        };
        var admitted = EconomicsPlan.Admit(input, false);
        Assert.Equal(DeepSeekRequestProfile.Output8192, admitted.Profile);
        Assert.Equal(65_536, admitted.ChildAllocation.OutputTokens);
        Assert.Equal(327_680, admitted.ChildAllocation.CombinedTokens);
        Assert.NotNull(admitted.LoadTariff(default));
        var replay = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(input.Replay.Path).Fixture);
        var trusted = admitted.TrustedRequest(replay.Runs[0]);
        Assert.Equal(DeepSeekAdapterContext.CandidateAdapter, trusted.AdapterId);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        Assert.Equal(AgentCanonical.LimitsSha256(AgentLimitProfile.Output8192), stable!.StablePlan.LimitsSha256);
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Bounds = input.Bounds with { PerCall = per with { MaxOutputTokens = 4096 } },
        }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Bounds = input.Bounds with { PerCall = per with { MaxOutputTokens = 8193 } },
        }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Bounds = input.Bounds with { MaxOutputTokens = input.Bounds.MaxOutputTokens - 1 },
        }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Provider = input.Provider with { ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256() },
        }, false));
    }

    [Fact]
    public void Output65536EconomicsPlanBindsFullChildAllocationAndTariff()
    {
        using var files = new Inputs();
        var original = files.Plan;
        var calls = original.Bounds.MaxModelCalls;
        var per = original.Bounds.PerCall with { MaxOutputTokens = 65_536 };
        var input = original with
        {
            Provider = original.Provider with
            {
                AdapterId = DeepSeekAdapterContext.Output65536Adapter,
                ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256(DeepSeekRequestProfile.Output65536),
            },
            Bounds = original.Bounds with
            {
                PerCall = per,
                MaxOutputTokens = calls * per.MaxOutputTokens,
                MaxCombinedTokens = calls * (per.MaxInputTokens + per.MaxOutputTokens),
            },
        };
        var admitted = EconomicsPlan.Admit(input, false);
        Assert.Equal(DeepSeekRequestProfile.Output65536, admitted.Profile);
        Assert.Equal(262_144, admitted.ChildAllocation.InputTokens);
        Assert.Equal(524_288, admitted.ChildAllocation.OutputTokens);
        Assert.Equal(786_432, admitted.ChildAllocation.CombinedTokens);
        Assert.NotNull(admitted.LoadTariff(default));
        var replay = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(input.Replay.Path).Fixture);
        var trusted = admitted.TrustedRequest(replay.Runs[0]);
        Assert.Equal(DeepSeekAdapterContext.Output65536Adapter, trusted.AdapterId);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        Assert.Equal(AgentCanonical.LimitsSha256(AgentLimitProfile.Output65536),
            stable!.StablePlan.LimitsSha256);
        foreach (var output in new[] { 4096, 8192, 65_535, 65_537 })
        {
            Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
            {
                Bounds = input.Bounds with { PerCall = per with { MaxOutputTokens = output } },
            }, false));
        }
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Bounds = input.Bounds with { MaxOutputTokens = input.Bounds.MaxOutputTokens - 1 },
        }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Provider = input.Provider with { ConfigurationSha256 = LivePlanAdmission.ProviderConfigurationSha256() },
        }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(input with
        {
            Bounds = input.Bounds with
            {
                PerCall = per with { MaxChargeMicroUsd = 1 },
                SpendCeilingMicroUsd = calls,
            },
        }, false).LoadTariff(default));
    }

    [Fact]
    public void CurrentEconomicsPlanBindsTheClosedAdapterEvenForAnotherAdmittedReplayFixture()
    {
        using var files = new Inputs();
        var plan = EconomicsPlan.Admit(files.Plan, false);
        var seed = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5", "replay-seed", "valid");
        var foreign = Assert.IsType<AdmittedReplayFixture>(ReplayAdmission.Load(seed).Fixture).Runs[0];
        var trusted = plan.TrustedRequest(foreign);
        Assert.Equal(DeepSeekAdapterContext.Provider, trusted.ProviderId);
        Assert.Equal(DeepSeekAdapterContext.Model, trusted.ModelId);
        Assert.Equal(DeepSeekAdapterContext.Adapter, trusted.AdapterId);
        Assert.Equal(DeepSeekAdapterContext.LimitAuthorityFor(DeepSeekRequestProfile.Current), trusted.LimitAuthority);
        Assert.True(AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        Assert.Equal(AgentCanonical.LimitsSha256(), stable!.StablePlan.LimitsSha256);
    }

    [Fact]
    public void MaximumScheduleAccountsForEverySupervisorSlotWithoutRaisingGlobalTimeLimit()
    {
        using var files = new Inputs();
        ImmutableArray<EconomicsScenario> scenarios =
            [new("replay", 2, 16, false), new("tools", 7, 16, false), new("continuation", 7, 16, false)];
        var plan = EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            scenarios, childSeconds: 1);
        Assert.Equal(256, EconomicsPlan.Expand(scenarios).Length);
        Assert.Equal(1541, plan.Bounds.MaxSeconds);
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(
            plan with { Bounds = plan.Bounds with { MaxSeconds = 1540 } }, false));
        Assert.Throws<EconomicsRejected>(() => EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path,
            files.Plan.TariffPath, scenarios, childSeconds: 300, spacingMilliseconds: 60000));
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(
            plan with { Bounds = plan.Bounds with { MaxSeconds = 86401 } }, false));
    }

    [Theory]
    [InlineData("attempts")]
    [InlineData("calls")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("combined")]
    [InlineData("spend")]
    [InlineData("time")]
    public void LedgerExactAndPlusOneNeverRefundOrAcceptDuplicateLeases(string dimension)
    {
        var amount = new EconomicsAllocation(1, 8, 80, 80, 160, 800, 1000);
        var ceiling = EconomicsLedger.Add(amount, amount)!;
        var ledger = new EconomicsLedger(ceiling);
        var first = Assert.IsType<EconomicsLease>(ledger.Reserve(0, amount));
        Assert.Null(ledger.Reserve(0, amount));
        Assert.False(ledger.Receipt(first with { Id = new string('a', 32) }));
        Assert.True(ledger.Receipt(first)); Assert.False(ledger.Receipt(first));
        var larger = dimension switch
        {
            "attempts" => amount with { Attempts = amount.Attempts + 1 },
            "calls" => amount with { Calls = amount.Calls + 1 },
            "input" => amount with { InputTokens = amount.InputTokens + 1 },
            "output" => amount with { OutputTokens = amount.OutputTokens + 1 },
            "combined" => amount with { CombinedTokens = amount.CombinedTokens + 1 },
            "spend" => amount with { SpendMicroUsd = amount.SpendMicroUsd + 1 },
            _ => amount with { Milliseconds = amount.Milliseconds + 1 },
        };
        Assert.Null(ledger.Reserve(1, larger)); Assert.Equal(amount, ledger.Total);
        var second = Assert.IsType<EconomicsLease>(ledger.Reserve(1, amount));
        Assert.Equal(ceiling, ledger.Total); Assert.Null(ledger.Reserve(2, amount));
        Assert.Equal(ceiling, ledger.Seal()); Assert.False(ledger.Receipt(second));
        Assert.Null(ledger.Reserve(2, amount)); Assert.Equal(ceiling, ledger.Seal());
        Assert.Null(EconomicsLedger.Add(amount with { Calls = long.MaxValue }, amount));
    }

    private static EconomicsStep LocalFailedStep(EconomicsReceipt receipt) => new(
        receipt.Index, receipt.Evaluation!.CaseId, receipt.Code, "complete", true,
        receipt.Evaluation.AttemptSha256, "failed", receipt.Restored, false, false, false, false,
        receipt.ProcessId, receipt.Startup, 0, 1, null, null, receipt.PredecessorSha256,
        receipt.ToolCalls, EconomicsJournal.Observe(receipt));

    // Alter only a task-private copied replay corpus; archived fixtures stay byte-exact.
    private static void WriteWideReplay(Inputs files, int batches)
    {
        var replay = Path.Combine(files.Root, "wide-replay");
        Directory.CreateDirectory(replay);
        foreach (var path in Directory.GetFiles(files.Plan.Replay.Path))
            File.Copy(path, Path.Combine(replay, Path.GetFileName(path)));
        var scriptPath = Path.Combine(replay, "script-0.json");
        var script = JsonNode.Parse(File.ReadAllText(scriptPath))!;
        var first = script["turns"]![0]!;
        var original = first["tool_calls"]!.AsArray();
        var turns = new JsonArray();
        for (var batch = 0; batch < batches; batch++)
        {
            var turn = first.DeepClone();
            turn["tool_calls"] = new JsonArray(Enumerable.Range(0, 16).Select(index =>
            {
                var call = original[index % original.Count]!.DeepClone();
                if (batch != 0 || index >= original.Count) call["id"] = $"wide-{batch}-{index}";
                return call;
            }).ToArray());
            turns.Add(turn);
        }
        turns.Add(script["turns"]!.AsArray()[^1]!.DeepClone());
        script["turns"] = turns;
        File.WriteAllText(scriptPath, script.ToJsonString());
        var manifestPath = Path.Combine(replay, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var member = manifest["files"]!.AsArray().Single(item => item!["path"]!.GetValue<string>() == "script-0.json")!;
        var scriptBytes = File.ReadAllBytes(scriptPath);
        member["length"] = scriptBytes.Length;
        member["sha256"] = AgentCanonical.HashRaw(scriptBytes);
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        files.Write(EconomicsCommand.Prepare(replay, files.Plan.Growth.Path, files.Plan.TariffPath, files.Plan.Scenarios));
    }

    private static ImmutableArray<EconomicsCall> CorruptNinthObservation(
        ImmutableArray<EconomicsCall> calls, string mutation) => mutation switch
    {
        "dispatched" => calls.SetItem(8, calls[0] with { Ordinal = 9 }),
        "refusal" => calls.SetItem(8, calls[8] with { TransportOutcome = "violation_refused" }),
        "request_rejected" => calls.SetItem(8, calls[8] with { TransportOutcome = "request_rejected" }),
        "chat" => calls.SetItem(8, calls[8] with { ChatOutcome = "returned" }),
        "usage" => calls.SetItem(8, calls[8] with { Usage = calls[0].Usage }),
        "ordinal" => calls.SetItem(8, calls[8] with { Ordinal = 8 }),
        "order" => calls.SetItem(0, calls[1]).SetItem(1, calls[0]),
        "preceding_refusal" => calls.SetItem(0, calls[8] with { Ordinal = 1 }),
        "multiple_refusal" => calls.SetItem(7, calls[8] with { Ordinal = 8 }),
        _ => calls.Add(calls[8] with { Ordinal = 10 }),
    };

    [Fact]
    public void EightCompletedSendsPermitOnlyOneNondispatchableRefusalObserver()
    {
        var observer = new EconomicsCalls();
        for (var ordinal = 1; ordinal <= 8; ordinal++)
        {
            var call = observer.BeginCall()!;
            Assert.True(call.Dispatch());
            call.ScriptFinished("success");
            call.Returned(new ProjectChatUsage(1, 1));
        }
        var refused = observer.BeginCall()!;
        Assert.False(refused.Dispatch());
        refused.Refuse("budget_refused");
        refused.Threw();
        Assert.Throws<InvalidOperationException>(() => observer.BeginCall());
        Assert.Equal(new EconomicsCall(9, false, "budget_refused", "threw", "not_sent", null), observer.Seal(false)[^1]);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("input")]
    [InlineData("spend")]
    public async Task UnsupportedTinyScopeRefusesThirdSendInActualAgentAndCannotBecomeACampaignJournal(string dimension)
    {
        using var files = new Inputs();
        var plan = EconomicsPlan.Load(files.PlanPath, false, default);
        var workload = EconomicsWorkload.Load(plan, default);
        var run = workload.Run(plan.Slots[0], "tiny");
        var trusted = run.CreateTrustedRequest(ReplayFixtureIdentity.Build);
        var session = new string('a', 32);
        Assert.True(AgenticPrReview.Runtime.Agent.Core.AgentStableRequestMaterializer.TryMaterialize(trusted, null, out var stable));
        var request = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable!.StablePlan, session,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var snapshot = run.CreateSnapshot(files.Root);
        var bounds = dimension switch
        {
            "calls" => plan.ChildBounds with { MaxModelCalls = 2 },
            "input" => plan.ChildBounds with { MaxInputTokens = 2 * plan.ChildBounds.PerCall.MaxInputTokens },
            _ => plan.ChildBounds with { SpendCeilingMicroUsd = 2 * plan.ChildBounds.PerCall.MaxChargeMicroUsd },
        };
        var accounting = new LiveAccounting(bounds); var calls = new EconomicsCalls();
        using var client = new LiveMeteredChat(new EconomicsLoopback(run.Script, EconomicsFault.ThreeCalls, 32768,
            new(trusted.ProviderId, trusted.ModelId, trusted.AdapterId, session)), accounting, calls);
        var outcome = await new AgentLoop(new LiveChatObserver(client, accounting, calls),
            new SnapshotToolExecutor(snapshot, run.CreateFileAccess(snapshot))).RunAsync(request, default);
        Assert.False(outcome.Succeeded);
        var observed = calls.Seal(false);
        Assert.Equal(3, observed.Length); Assert.Equal(2, observed.Count(call => call.Dispatched));
        Assert.Equal("budget_refused", observed[^1].TransportOutcome); Assert.Equal(2, accounting.Seal().Sends);
        Assert.All(observed.Take(2), call => Assert.Equal("known", call.UsageStatus));
        var tooSmall = dimension switch
        {
            "calls" => files.Plan.Bounds with { MaxModelCalls = 4 },
            "input" => files.Plan.Bounds with { MaxInputTokens = 4 * files.Plan.Bounds.PerCall.MaxInputTokens },
            _ => files.Plan.Bounds with { SpendCeilingMicroUsd = 4 * files.Plan.Bounds.PerCall.MaxChargeMicroUsd },
        };
        var selected = files.Plan with { Bounds = tooSmall };
        Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Admit(selected, false));
    }

    [Fact]
    public void SealedReceiptsAndAccountingIgnoreLateCompletionWithoutAttributingItToAnotherCall()
    {
        var observer = new EconomicsCalls(); var call = observer.BeginCall()!;
        Assert.True(call.Dispatch()); var frozen = observer.Seal(cancelled: true);
        call.ScriptFinished("success");
        call.Returned(new ProjectChatUsage(999, 999)); call.Threw();
        Assert.Equal(frozen, observer.Seal(false)); Assert.Null(observer.BeginCall());
        Assert.Equal("unknown", frozen[0].UsageStatus); Assert.Equal("cancelled", frozen[0].TransportOutcome);
        var accounting = new LiveAccounting(new(1, 8, 80, 80, 160, 1, 800, new(10, 10, 100)));
        Assert.True(accounting.TryReserve()); var counters = accounting.Seal();
        accounting.RecordUsage(new(9, 9)); accounting.RecordCancelled();
        Assert.False(accounting.TryReserve()); Assert.Equal(counters, accounting.Seal());
    }

    private static string Describe(EconomicsReport report) => report.StopReason + ":" + string.Join(",",
        report.Steps.Select(step => step.Index + "=" + step.Code + "/" + step.ReceiptCoverage));
    private static bool IsCleanBuild() => EvaluationSource.Clean;

    [Theory]
    [InlineData("malformed")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("stale")]
    [InlineData("over_budget")]
    [InlineData("oversized")]
    public void HistoricalPlanReaderRejectsUnadmittedPlansWithoutExecuting(string corruption)
    {
        using var files = new Inputs();
        Assert.NotNull(EconomicsPlan.Load(files.PlanPath, false, default));
        var text = File.ReadAllText(files.PlanPath);
        var node = JsonNode.Parse(text)!.AsObject();
        switch (corruption)
        {
            case "malformed": text = "{"; break;
            case "missing": node.Remove("bounds"); text = node.ToJsonString(); break;
            case "duplicate": text = text.Replace("\"format\":", "\"format\":\"duplicate\",\"format\":", StringComparison.Ordinal); break;
            case "extra": node["credential"] = Canary; text = node.ToJsonString(); break;
            case "stale": node["build_sha256"] = new string('f', 64); text = node.ToJsonString(); break;
            case "over_budget": node["bounds"]!["max_model_calls"] = 2; text = node.ToJsonString(); break;
            case "oversized": text = new string(' ', EconomicsLiveLimits.PlanBytes + 1); break;
        }
        File.WriteAllText(files.PlanPath, text);
        var error = Assert.Throws<EconomicsRejected>(() => EconomicsPlan.Load(files.PlanPath, false, default));
        Assert.DoesNotContain(Canary, error.Code); Assert.DoesNotContain(files.Root, error.Code);
    }

    [Theory]
    [InlineData(48, false)]
    [InlineData(0, false)]
    [InlineData(50, false)]
    [InlineData(48, true)]
    public async Task EarlyTimerWakeCannotShortenSpacingOrAdmitWorkAfterCancellation(int firstWake, bool cancel)
    {
        long elapsed = 100;
        var waits = new List<int>();
        using var cancellation = new CancellationTokenSource();
        var wait = EconomicsRunner.WaitSpacingAsync(50, () => elapsed, (remaining, token) =>
        {
            Assert.Equal(cancellation.Token, token); waits.Add(remaining);
            elapsed += waits.Count == 1 ? firstWake : remaining;
            if (cancel) cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token);
        if (cancel) { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait); Assert.Single(waits); }
        else { await wait; Assert.True(elapsed >= 150); }
        Assert.All(waits, remaining => Assert.InRange(remaining, 1, 50));
    }

    private static void VerifyCredentialTraceMutations(EconomicsChildInput input, EconomicsReceipt receipt, byte[] key)
    {
        var path = Path.Join(input.Root, EconomicsCredentialProbe.TraceName(input.Slot.Index));
        var bytes = EconomicsCredentialProbe.ReadTrace(input.Root, input.Slot.Index);
        Assert.True(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key));
        var proof = receipt.CredentialProof!;
        Assert.True(EconomicsJournal.ValidCredentialProof(proof, 2, receipt.Restored));
        var held = path + ".held";
        // A completely plausible positive child summary cannot substitute for missing actual callback events.
        File.Move(path, held);
        try { Assert.False(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key)); }
        finally { File.Move(held, path); }
        void Reject(byte[] changed, bool bindDigest = true)
        {
            File.WriteAllBytes(path, changed);
            var candidate = bindDigest ? receipt with { CredentialProof = proof with
            { TraceSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(changed)) } } : receipt;
            try { Assert.False(GateEconomicsCases.VerifyCredentialTrace(input, candidate, key)); }
            finally { File.WriteAllBytes(path, bytes); }
        }
        var original = Encoding.UTF8.GetString(bytes).Split('\n');
        byte[] Encode(string[] lines) => Encoding.UTF8.GetBytes(string.Join('\n', lines));
        foreach (var field in new[] { "kind", "observation_domain", "operation", "plan_sha256", "workload_sha256", "source_commit",
            "source_tree", "build_sha256", "startup", "session_sha256" })
        {
            var lines = original.ToArray(); var header = JsonNode.Parse(lines[0])!;
            header[field] = "wrong"; lines[0] = header.ToJsonString(); Reject(Encode(lines));
        }
        foreach (var field in new[] { "index", "phase" })
        {
            var lines = original.ToArray(); var header = JsonNode.Parse(lines[0])!;
            header[field] = header[field]!.GetValue<int>() + 1; lines[0] = header.ToJsonString(); Reject(Encode(lines));
        }
        {
            var lines = original.ToArray(); var header = JsonNode.Parse(lines[0])!;
            header["restored"] = !receipt.Restored; lines[0] = header.ToJsonString(); Reject(Encode(lines));
            header["extra"] = true; lines[0] = header.ToJsonString(); Reject(Encode(lines));
        }
        foreach (var outcome in new[] { "entered", "failed", "cancelled" })
        {
            var lines = original.ToArray(); var end = JsonNode.Parse(lines[2])!;
            end["outcome"] = outcome; lines[2] = end.ToJsonString(); Reject(Encode(lines));
        }
        {
            var lines = original.ToArray(); (lines[1], lines[3]) = (lines[3], lines[1]); Reject(Encode(lines));
            lines = original.ToArray(); lines[3] = lines[1]; Reject(Encode(lines));
            lines = original.ToArray(); var row = JsonNode.Parse(lines[2])!;
            row["request_sha256"] = new string('0', 64); lines[2] = row.ToJsonString(); Reject(Encode(lines));
            row["ordinal"] = 8; lines[2] = row.ToJsonString(); Reject(Encode(lines));
            lines = original.ToArray(); lines[1] = "{\"ordinal\":1,\"ordinal\":1,\"request_sha256\":\"" + new string('a', 64) + "\",\"outcome\":\"entered\"}"; Reject(Encode(lines));
            Reject(Encode(original.Where((_, index) => index != 2).ToArray()));
            Reject(Encode([.. original[..^1], original[1], ""]));
            Reject(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes) + " "), bindDigest: false);
            Reject(new byte[EconomicsCredentialProbe.MaximumTraceBytes + 1]);
            Reject([255, .. bytes]);
            Reject(Encoding.UTF8.GetBytes(EconomicsCredentialProbe.Provider));
        }
        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(path); var rootMode = File.GetUnixFileMode(input.Root);
            try
            {
                File.SetUnixFileMode(path, mode | UnixFileMode.GroupRead);
                Assert.False(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key));
                File.SetUnixFileMode(path, mode);
                File.SetUnixFileMode(input.Root, rootMode | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                Assert.False(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key));
            }
            finally { File.SetUnixFileMode(path, mode); File.SetUnixFileMode(input.Root, rootMode); }
            File.Move(path, held);
            try
            {
                File.CreateSymbolicLink(path, held);
                Assert.False(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key));
            }
            finally { File.Delete(path); File.Move(held, path); }
        }
        Assert.True(GateEconomicsCases.VerifyCredentialTrace(input, receipt, key));
        foreach (var cancelled in new[] { false, true })
        {
            var root = Path.Join(input.Root, (cancelled ? "cancelled-trace-" : "failed-trace-") + receipt.Index);
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(root);
            else Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var bound = input with { Root = root };
            var context = new DeepSeekAdapterContext(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model,
                input.Plan.Provider.AdapterId, input.Session);
            using (var probe = new EconomicsCredentialProbe(new ReplayScript([]), context, bound, receipt.Startup))
            {
                var request = new ProjectChatRequest([new("user", [new ProjectTextContent("fixed failure/cancellation observation")])], [], null);
                if (cancelled)
                    Assert.ThrowsAny<OperationCanceledException>(() => probe.GetResponseAsync(request, new CancellationToken(true)).GetAwaiter().GetResult());
                else Assert.Throws<IOException>(() => probe.GetResponseAsync(request, default).GetAwaiter().GetResult());
            }
            var observed = EconomicsCredentialProbe.ReadTrace(root, input.Slot.Index);
            var events = Encoding.UTF8.GetString(observed).Split('\n');
            Assert.Equal(4, events.Length);
            using var ended = JsonDocument.Parse(events[2]);
            Assert.Equal(cancelled ? "cancelled" : "failed", ended.RootElement.GetProperty("outcome").GetString());
            var expectedRequest = new ProjectChatRequest([new("user", [new ProjectTextContent("fixed failure/cancellation observation")])], [], null);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(AgentRequestWriter.Write(expectedRequest))),
                ended.RootElement.GetProperty("request_sha256").GetString());
            var forgedSuccess = receipt with { CredentialProof = proof with
            { TraceSha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(observed)) } };
            Assert.False(GateEconomicsCases.VerifyCredentialTrace(bound, forgedSuccess, key));
        }
    }

    [Fact]
    public void CredentialProbeRejectsEveryCanaryAndActualKeyEncodingInProtectedSinks()
    {
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        EconomicsCredentialProbe.AssertProtected(Encoding.UTF8.GetBytes("safe session"), key);
        foreach (var value in EconomicsCredentialProbe.Canaries.Values.Append(Convert.ToBase64String(key))
            .Append(Convert.ToHexString(key)).Append(Convert.ToHexString(key).ToLowerInvariant()))
            Assert.Throws<IOException>(() => EconomicsCredentialProbe.AssertProtected(Encoding.UTF8.GetBytes(value), key));
        Assert.Throws<IOException>(() => EconomicsCredentialProbe.AssertProtected(key, key));
        Assert.Throws<IOException>(() => EconomicsCredentialProbe.AssertProtected(
            Convert.FromBase64String(EconomicsCredentialProbe.Canaries["AGENTIC_REVIEW_R3_STATE_KEY_B64"]), key));
    }

    private sealed class LinuxInterruptTheoryAttribute : TheoryAttribute
    {
        public LinuxInterruptTheoryAttribute()
        { if (!OperatingSystem.IsLinux()) Skip = "Actual POSIX SIGINT delivery is exercised by the required Linux runtime CI."; }
    }
    private sealed class ProbeSecrets : ILiveSecretSource
    {
        internal int Reads;
        public string? TakeProviderCredential() { Reads++; return EconomicsCredentialProbe.Provider; }
    }

    private static ComparisonReportDocument Compare(EconomicsReport left, EconomicsReport right)
    {
        var le = new ComparisonEvidence(left.Outcomes, [], [], []);
        var re = new ComparisonEvidence(right.Outcomes, [], [], []);
        var declaration = new ComparisonDeclaration("none", "not_requested", ComparisonJson.Select(left.Pricing!, le),
            ComparisonJson.Select(right.Pricing!, re), null, null);
        ComparisonInput Side(EconomicsReport report, ComparisonEvidence evidence) => Assert.IsType<ComparisonInput>(
            ComparisonJson.ReadInput(ComparisonJson.WriteInput(new(ComparisonLimits.InputFormat, declaration, report.Pricing!, evidence))));
        return ComparisonReport.Create(Side(left, le), Side(right, re));
    }

    private sealed class Secrets : ILiveSecretSource
    {
        internal int Reads;
        public string? TakeProviderCredential() { Reads++; return Canary; }
    }
    private sealed class Inputs : IDisposable
    {
        internal string Root { get; } = ReplayProcess.CreatePrivateRoot();
        internal string PlanPath => Path.Combine(Root, "plan.json");
        internal EconomicsPlanInput Plan { get; private set; }
        internal Inputs(bool full = false, bool bounded = false)
        {
            var tariff = new TariffInput(PricingLimits.TariffFormat, "https://example.com/synthetic-tariff", "2026-09-20",
                new(PricingLimits.Formula, DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, null,
                    "standard", "2026-09-20T12:00:00Z", new("unknown", null, null), 1_000_000, 0,
                    new(new("USD", 1), new("USD", 4), new("USD", 5)),
                    new("half_even", 6, PricingLimits.Aggregation, PricingLimits.Normalization)));
            var tariffPath = Path.Combine(Root, "tariff.json");
            File.WriteAllBytes(tariffPath, JsonSerializer.SerializeToUtf8Bytes(tariff, PricingJsonContext.Default.TariffInput));
            var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "agent", "r5");
            Plan = EconomicsCommand.Prepare(Path.Combine(fixtureRoot, "replay"), Path.Combine(fixtureRoot, "growth"), tariffPath,
                full ? default : bounded ? [new("replay", 3, 1, false), new("tools", 2, 1, false), new("continuation", 2, 1, false)] : [new("replay", 3, 1, false)]);
            Write(Plan);
        }
        internal void Write(EconomicsPlanInput plan)
        { Plan = plan; File.WriteAllBytes(PlanPath, JsonSerializer.SerializeToUtf8Bytes(plan, EconomicsLiveJson.Default.EconomicsPlanInput)); }
        public void Dispose() => Assert.True(ReplayProcess.Cleanup(Root));
    }
    private static async Task<(int Exit, string Output, string Error)> Command(string[] args)
    {
        using var output = new StringWriter(); using var error = new StringWriter();
        var previousOutput = Console.Out; var previousError = Console.Error;
        try
        {
            Console.SetOut(output); Console.SetError(error);
            var exit = await EvaluatorProgram.Main(args);
            return (exit, output.ToString(), error.ToString());
        }
        finally { Console.SetOut(previousOutput); Console.SetError(previousError); }
    }
}
