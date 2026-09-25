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
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
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

    [Fact]
    public async Task FreshChildrenRestoreActualCompletedHistoryAndReachUnchangedReaders()
    {
        using var files = new Inputs();
        var secrets = new Secrets();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Secrets = secrets });
        Assert.True(result.StopReason == "complete", Describe(result));
        Assert.Equal("cleaned", result.Cleanup); Assert.Equal("loopback", result.ExecutionKind); Assert.Equal(0, secrets.Reads);
        Assert.Equal(3, result.Attempted); Assert.Equal(3, result.Steps.Select(step => step.Startup).Distinct().Count());
        Assert.All(result.Steps, step => { Assert.True(step.Accepted); Assert.True(step.Readback); });
        Assert.False(result.Steps[0].Restored); Assert.All(result.Steps.Skip(1), step => Assert.True(step.Restored));
        Assert.Equal(result.Steps[0].SessionSha256, result.Steps[1].PredecessorSha256);
        Assert.Equal(24, result.Allocations.Calls);
        Assert.True(result.Journal!.Reservations.Calls < result.Allocations.Calls);
        Assert.NotNull(UsageJournal.Admit(result.Journal));
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        var comparison = Compare(result, result);
        Assert.Equal("inconclusive", comparison.Result.HistoricalR5Quality);
        Assert.Equal("native_history_equivalence_unproven", comparison.Result.HistoryWorkloadEquivalence);
    }

    [Fact]
    public async Task ActualCommandDefaultsToKeylessExecutionAndRejectsConflictingOptions()
    {
        using var files = new Inputs();
        var command = await Command(["economics-live", "--plan", files.PlanPath]);
        Assert.True(command.Exit == 0, command.Error + command.Output);
        var result = Assert.IsType<EconomicsReport>(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(command.Output)));
        Assert.Equal("loopback", result.ExecutionKind); Assert.Equal("complete", result.StopReason);
        var rejected = await Command(["economics-live", "--execute", "--dry-run", "--plan", Canary]);
        Assert.Equal(2, rejected.Exit); Assert.Empty(rejected.Output); Assert.DoesNotContain(Canary, rejected.Error);
    }

    [Theory]
    [InlineData("calls")]
    [InlineData("input")]
    [InlineData("output")]
    [InlineData("combined")]
    [InlineData("spend")]
    [InlineData("attempts")]
    [InlineData("time")]
    public async Task InsufficientFullChildAllocationsRejectBeforeAnyProcessOrCredential(string dimension)
    {
        using var files = new Inputs();
        var bounds = files.Plan.Bounds;
        bounds = dimension switch
        {
            "calls" => bounds with { MaxModelCalls = bounds.MaxModelCalls - 1 },
            "input" => bounds with { MaxInputTokens = bounds.MaxInputTokens - 1 },
            "output" => bounds with { MaxOutputTokens = bounds.MaxOutputTokens - 1 },
            "combined" => bounds with { MaxCombinedTokens = bounds.MaxCombinedTokens - 1 },
            "spend" => bounds with { SpendCeilingMicroUsd = bounds.SpendCeilingMicroUsd - 1 },
            "attempts" => bounds with { MaxEvaluations = bounds.MaxEvaluations - 1 },
            _ => bounds with { MaxSeconds = bounds.MaxSeconds - 1 },
        };
        files.Write(files.Plan with { Bounds = bounds });
        var secrets = new Secrets(); var launched = false;
        await Assert.ThrowsAsync<EconomicsRejected>(() => EconomicsRunner.RunAsync(files.PlanPath, false,
            new() { Secrets = secrets, BeforeChild = _ => launched = true }));
        Assert.False(launched); Assert.Equal(0, secrets.Reads);
    }

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
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreparedCampaignRetainsChildWindowsWithSupervisorWork(bool duringSetup)
    {
        using var files = new Inputs();
        files.Write(EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            [new("replay", 2, 1, false)], childSeconds: 5));
        var accepted = 0;
        var secrets = new Secrets();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Secrets = secrets,
            PrivateRoot = _ => { if (duringSetup) Thread.Sleep(3000); },
            BeforeAccept = () => { if (!duringSetup && accepted++ == 0) Thread.Sleep(3000); },
            Process = async (input, credential, token) =>
            {
                var elapsed = Stopwatch.StartNew();
                var observed = await EconomicsProcess.RunAsync(input, credential, token);
                // Keep each genuine successful child near its allocated five-second window.
                var remaining = TimeSpan.FromMilliseconds(4500) - elapsed.Elapsed;
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token);
                return observed;
            },
        });
        Assert.True(result.StopReason == "complete", Describe(result));
        Assert.Equal(2, result.Attempted); Assert.Equal(0, result.ReceiptMissing);
        Assert.Equal("cleaned", result.Cleanup); Assert.Equal(0, secrets.Reads);
        Assert.True(result.Steps[1].Restored);
        Assert.All(result.Steps, step => { Assert.True(step.Accepted); Assert.True(step.Readback); });
        Assert.Equal(10000, result.Allocations.Milliseconds);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        Assert.NotNull(UsageJournal.Admit(result.Journal!));
        _ = Compare(result, result);
    }

    [Theory]
    [InlineData((int)EconomicsFault.WrongSource)]
    [InlineData((int)EconomicsFault.WrongBuild)]
    [InlineData((int)EconomicsFault.WrongPredecessor)]
    [InlineData((int)EconomicsFault.BeforeReadyCrash)]
    [InlineData((int)EconomicsFault.AfterPrepareCrash)]
    [InlineData((int)EconomicsFault.PartialReply)]
    [InlineData((int)EconomicsFault.OversizedReply)]
    [InlineData((int)EconomicsFault.WrongReply)]
    public async Task MissingOrInvalidRealChildReceiptRetainsAllocationAndWithholdsCampaignExport(int faultValue)
    {
        var fault = (EconomicsFault)faultValue;
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = fault, FaultIndex = 1 });
        Assert.NotEqual("complete", result.StopReason); Assert.Equal(2, result.Attempted);
        Assert.Equal(16, result.Allocations.Calls); Assert.Equal(1, result.ReceiptMissing);
        Assert.False(result.UsageComplete); Assert.False(result.MonetaryComplete);
        Assert.Null(result.Journal); Assert.Null(result.Pricing); Assert.Single(result.Outcomes);
        Assert.Equal("unattempted", result.Steps[2].Code); Assert.Equal("cleaned", result.Cleanup);
        Assert.NotNull(result.Steps[0].Observation);
        Assert.All(result.Steps[0].Observation!.Calls, call => Assert.Equal("known", call.UsageStatus));
        Assert.Equal(2, result.Steps[0].Observation!.Reservations);
        Assert.Null(result.Steps[1].Observation); Assert.Null(result.Steps[2].Observation);
        var json = EconomicsReportJson.Write(result);
        Assert.DoesNotContain(Canary, Encoding.UTF8.GetString(json));
    }

    [Theory]
    [InlineData((int)EconomicsFault.RateLimit, "rate_limited")]
    [InlineData((int)EconomicsFault.UsageViolation, "accounting_violation")]
    [InlineData((int)EconomicsFault.ProviderFailure, "representative_history_insufficient")]
    [InlineData((int)EconomicsFault.CancelAfterUsage, "caller_cancelled")]
    [InlineData((int)EconomicsFault.RejectAccept, "state_failed")]
    [InlineData((int)EconomicsFault.CancelAfterPrepare, "caller_cancelled")]
    public async Task FullyReceiptedFailuresPreserveCostsAndCompletedEvaluationIndependently(int faultValue, string stop)
    {
        var fault = (EconomicsFault)faultValue;
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = fault, FaultIndex = 1 });
        Assert.True(result.StopReason == stop, Describe(result)); Assert.Equal(0, result.ReceiptMissing);
        Assert.Equal(3, result.Journal!.Totals.Scheduled); Assert.Equal(1, result.Journal.Totals.Unattempted);
        Assert.True(result.Journal.Totals.ActualSends > 0); Assert.NotNull(result.Pricing);
        if (fault is EconomicsFault.RejectAccept or EconomicsFault.CancelAfterPrepare)
        { Assert.Equal("completed", result.Steps[1].EvaluationStatus); Assert.False(result.Steps[1].Accepted); }
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        Assert.NotNull(Compare(result, result));
    }

    [Fact]
    public async Task FullEightCallLeaseAllowsThirdSendWithoutPretendingParentAllocationIsTraffic()
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = EconomicsFault.ThreeCalls, FaultIndex = 0 });
        Assert.True(result.StopReason == "complete", Describe(result));
        Assert.Equal(3, result.Journal!.Attempts[0].Sends); Assert.Equal(24, result.Allocations.Calls);
        Assert.Equal(7, result.Journal.Reservations.Calls); Assert.NotNull(Compare(result, result));
    }

    [Fact]
    public async Task ToolsAndContinuationCapacityResetIsExplicitAndRestoresFreshHistory()
    {
        using var files = new Inputs(full: true);
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false);
        Assert.True(result.StopReason == "complete", Describe(result));
        Assert.Equal(2, result.Steps.Count(step => step.Code == "capacity_stop"));
        Assert.All(result.Steps.Where(step => step.Code == "capacity_stop"), step =>
            Assert.True(EconomicsJournal.Capacity(step.Observation!)));
        Assert.Equal(2, result.Steps.Count(step => step.Reset));
        foreach (var reset in result.Steps.Where(step => step.Reset))
        {
            Assert.False(reset.Restored); Assert.True(reset.Accepted);
            Assert.True(result.Steps[reset.Index + 1].Restored);
            Assert.Equal(reset.SessionSha256, result.Steps[reset.Index + 1].PredecessorSha256);
        }
        Assert.Equal(result.Scheduled * 8, result.Allocations.Calls);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
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

    [Theory]
    [InlineData("commit")]
    [InlineData("tree")]
    [InlineData("clean")]
    [InlineData("build")]
    [InlineData("corpus")]
    [InlineData("provider")]
    [InlineData("thinking")]
    [InlineData("tariff")]
    [InlineData("output_basis")]
    public async Task StaleOrUnsupportedSelectionRejectsBeforeKeyAndLaunch(string field)
    {
        using var files = new Inputs();
        var plan = files.Plan;
        plan = field switch
        {
            "commit" => plan with { Source = plan.Source with { Commit = new string('f', 40) } },
            "tree" => plan with { Source = plan.Source with { Tree = new string('f', 40) } },
            "clean" => plan with { Source = plan.Source with { Clean = !plan.Source.Clean } },
            "build" => plan with { BuildSha256 = new string('f', 64) },
            "corpus" => plan with { Replay = plan.Replay with { Sha256 = new string('f', 64) } },
            "provider" => plan with { Provider = plan.Provider with { ModelId = "unsupported" } },
            "thinking" => plan with { Thinking = "low" },
            "tariff" => plan with { TariffSha256 = new string('f', 64) },
            _ => plan with { Bounds = plan.Bounds with { PerCall = plan.Bounds.PerCall with { MaxOutputTokens = 4095 } } },
        };
        files.Write(plan); var secrets = new Secrets(); var launches = 0;
        await Assert.ThrowsAsync<EconomicsRejected>(() => EconomicsRunner.RunAsync(files.PlanPath, false,
            new() { Secrets = secrets, BeforeChild = _ => launches++ }));
        Assert.Equal(0, launches); Assert.Equal(0, secrets.Reads);
    }

    [Fact]
    public async Task ExecuteUsesTheSameFreshProcessPathOnlyFromACleanBuildAndStaysLoopbackWhenInjected()
    {
        using var files = new Inputs(); var secrets = new Secrets();
        var options = new EconomicsOptions { LoopbackExecute = true, Secrets = secrets };
        if (!IsCleanBuild())
        {
            var error = await Assert.ThrowsAsync<EconomicsRejected>(() => EconomicsRunner.RunAsync(files.PlanPath, true, options));
            Assert.Equal("r6_economics_source_invalid", error.Code); Assert.Equal(0, secrets.Reads);
            return;
        }
        var result = await EconomicsRunner.RunAsync(files.PlanPath, true, options);
        Assert.Equal("complete", result.StopReason); Assert.Equal("loopback", result.ExecutionKind);
        Assert.Equal(0, secrets.Reads); Assert.Equal(3, result.Steps.Select(step => step.Startup).Distinct().Count());
    }

    [Fact]
    public async Task EighthNonterminalResponseStopsAtTheProductLimitWithoutNinthSend()
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = EconomicsFault.EightCalls, FaultIndex = 0 });
        Assert.Equal("representative_history_insufficient", result.StopReason);
        Assert.Equal(8, result.Journal!.Totals.ActualSends); Assert.Equal(8, result.Journal.Reservations.Calls);
        Assert.Equal(8, result.Allocations.Calls); Assert.Equal(2, result.Journal.Totals.Unattempted);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
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
        using var state = new ReplayState(run, new string('a', 32), files.Root, new byte[32]);
        Assert.True(AgenticPrReview.Runtime.Agent.Session.AgentStableRequestMaterializer.TryMaterialize(state.Trusted, null, out var stable));
        var request = new AgentRunRequest(run.Input.ReviewedIdentity.Runtime, stable!.StablePlan, state.Session,
            [.. stable.ControlMessages, new("user", [new ProjectTextContent(run.InitialContext)])]);
        var snapshot = run.CreateSnapshot(files.Root);
        var bounds = dimension switch
        {
            "calls" => plan.ChildBounds with { MaxModelCalls = 2 },
            "input" => plan.ChildBounds with { MaxInputTokens = 2 * plan.ChildBounds.PerCall.MaxInputTokens },
            _ => plan.ChildBounds with { SpendCeilingMicroUsd = 2 * plan.ChildBounds.PerCall.MaxChargeMicroUsd },
        };
        var accounting = new LiveAccounting(bounds); var calls = new EconomicsCalls();
        using var transport = new LiveMeteredTransport(new EconomicsLoopback(run.Script, EconomicsFault.ThreeCalls, 32768), accounting, calls);
        var client = DeepSeekChatBackend.CreateClient(new(state.Trusted.ProviderId, state.Trusted.ModelId, state.Trusted.AdapterId, state.Session), transport);
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
        call.TransportFinished(DeepSeekTransportResult.Success(Encoding.UTF8.GetBytes("{}")));
        call.Returned(new ProjectChatUsage(999, 999)); call.Threw();
        Assert.Equal(frozen, observer.Seal(false)); Assert.Null(observer.BeginCall());
        Assert.Equal("unknown", frozen[0].UsageStatus); Assert.Equal("cancelled", frozen[0].TransportOutcome);
        var accounting = new LiveAccounting(new(1, 8, 80, 80, 160, 1, 800, new(10, 10, 100)));
        Assert.True(accounting.TryReserve()); var counters = accounting.Seal();
        accounting.RecordUsage(new(9, 9)); accounting.RecordCancelled();
        Assert.False(accounting.TryReserve()); Assert.Equal(counters, accounting.Seal());
    }

    [Fact]
    public async Task CancellationBeforeAllocationAndAfterFinalCompletionHasSeparateOutcomes()
    {
        using var files = new Inputs(); using var before = new CancellationTokenSource();
        var early = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { PrivateRoot = _ => before.Cancel() }, before.Token);
        Assert.Equal("caller_cancelled", early.StopReason); Assert.Equal(0, early.Attempted); Assert.Equal(0, early.Allocations.Calls);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(early)));
        using var after = new CancellationTokenSource();
        var late = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Finalized = after.Cancel }, after.Token);
        Assert.True(after.IsCancellationRequested); Assert.Equal("complete", late.StopReason);
        Assert.Equal(3, late.Journal!.Totals.Completed); Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(late)));
    }

    [Fact]
    public async Task UnreapedChildStopsSuccessorsAndPreservesTheOwnedRootWithoutCleanup()
    {
        using var files = new Inputs(); string? root = null; var cleanupCalled = false;
        try
        {
            var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
            {
                PrivateRoot = value => root = value, Cleanup = _ => { cleanupCalled = true; return false; },
                Process = (_, _, _) => throw new ReplayProcessUnreaped(),
            });
            Assert.Equal("child_unreaped", result.StopReason); Assert.Equal("cleanup_failed", result.Cleanup);
            Assert.Equal(1, result.Attempted); Assert.Equal(8, result.Allocations.Calls); Assert.False(cleanupCalled);
            Assert.True(Directory.Exists(root)); Assert.Null(result.Journal);
            Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        }
        finally { if (root is not null) Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public async Task RealHungWorkerIsTerminatedReapedAndCleanedWithinTheSelectedDeadline()
    {
        using var files = new Inputs();
        files.Write(files.Plan with { ChildSeconds = 1 });
        int? pid = null;
        var timer = Stopwatch.StartNew();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Fault = EconomicsFault.Hang, FaultIndex = 0,
            Process = async (input, secret, token) =>
            { var process = await EconomicsProcess.RunAsync(input, secret, token); pid = process.Ready?.ProcessId; return process; },
        });
        Assert.Equal("deadline", result.StopReason); Assert.Equal("cleaned", result.Cleanup);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(15)); Assert.Equal(1, result.Attempted);
        if (pid is { } id) Assert.Throws<ArgumentException>(() => Process.GetProcessById(id));
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
    public async Task ActualExecuteCommandRejectsUnadmittedPlansBeforeAnyExecution(string corruption)
    {
        using var files = new Inputs();
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
        var command = await Command(["economics-live", "--execute", "--plan", files.PlanPath]);
        Assert.Equal(2, command.Exit); Assert.Empty(command.Output);
        Assert.DoesNotContain(Canary, command.Error); Assert.DoesNotContain(files.Root, command.Error);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("assembly")]
    [InlineData("runtimeconfig")]
    public async Task ActualLaunchArtifactSubstitutionCannotCrossCredentialIngress(string change)
    {
        using var files = new Inputs(); var reads = 0;
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Process = (input, _, token) => EconomicsProcess.RunWithStartAsync(
                input with { Transport = IsCleanBuild() ? "live" : "loopback" }, () => { reads++; return null; }, token, start =>
                {
                    var copy = Path.Combine(files.Root, "binary"); Directory.CreateDirectory(copy);
                    var assembly = start.ArgumentList[3]; var folder = Path.GetDirectoryName(assembly)!;
                    foreach (var file in Directory.EnumerateFiles(folder, "*.dll")) File.Copy(file, Path.Combine(copy, Path.GetFileName(file)), true);
                    var deps = Path.ChangeExtension(assembly, ".deps.json");
                    if (File.Exists(deps)) File.Copy(deps, Path.Combine(copy, Path.GetFileName(deps)), true);
                    var copiedAssembly = Path.Combine(copy, Path.GetFileName(assembly));
                    var config = Path.ChangeExtension(copiedAssembly, ".runtimeconfig.json");
                    File.Copy(start.ArgumentList[2], config, true);
                    if (change == "assembly") { using var file = new FileStream(copiedAssembly, FileMode.Append); file.WriteByte(0); }
                    if (change == "runtimeconfig") File.AppendAllText(config, "\n");
                    start.ArgumentList[2] = config; start.ArgumentList[3] = copiedAssembly;
                }),
        });
        if (change == "unchanged")
        {
            Assert.Equal(IsCleanBuild() ? "credential_invalid" : "complete", result.StopReason);
            Assert.Equal(IsCleanBuild() ? 1 : 0, reads);
        }
        else { Assert.NotEqual("complete", result.StopReason); Assert.Equal(0, reads); Assert.Equal(1, result.Attempted); }
        Assert.Equal("cleaned", result.Cleanup);
    }

    [Fact]
    public async Task CorruptedAcceptedEnvelopeFailsActualFreshRestoreBeforeCredential()
    {
        using var files = new Inputs(); var secrets = new Secrets();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Secrets = secrets,
            BeforeChild = input =>
            {
                if (input.Slot.Index != 1) return;
                var state = Path.Combine(EconomicsChild.StateRoot(input.Root, input.Slot.Chain), "state");
                var objects = Directory.GetFiles(state, "*", SearchOption.AllDirectories);
                Assert.NotEmpty(objects);
                foreach (var path in objects) File.WriteAllBytes(path, Encoding.UTF8.GetBytes(Canary));
            },
        });
        Assert.Equal("process_failed", result.StopReason); Assert.Equal(0, secrets.Reads);
        Assert.Equal(2, result.Attempted); Assert.Equal(1, result.ReceiptMissing); Assert.Null(result.Journal);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
    }

    [Theory]
    [InlineData("ordinal")]
    [InlineData("duplicate")]
    [InlineData("accounting")]
    [InlineData("usage")]
    [InlineData("lease")]
    [InlineData("session")]
    public async Task ForgedRealReceiptCannotAdvanceAcceptedStateOrExportPartialCampaign(string field)
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Process = async (input, credential, token) =>
            {
                var observed = await EconomicsProcess.RunAsync(input, credential, token);
                if (input.Slot.Index != 1) return observed;
                var receipt = observed.Receipt!;
                receipt = field switch
                {
                    "ordinal" => receipt with { Calls = receipt.Calls.SetItem(0, receipt.Calls[0] with { Ordinal = 0 }) },
                    "duplicate" => receipt with { Calls = receipt.Calls.SetItem(1, receipt.Calls[0]) },
                    "accounting" => receipt with { Accounting = receipt.Accounting with { Sends = receipt.Accounting.Sends + 1 } },
                    "usage" => receipt with { Calls = receipt.Calls.SetItem(0, receipt.Calls[0] with { UsageStatus = "unknown" }) },
                    "lease" => receipt with { LeaseId = new string('a', 32) },
                    _ => receipt with { Session = new string('a', 32) },
                };
                return observed with { Receipt = receipt };
            },
        });
        Assert.Equal("receipt_invalid", result.StopReason); Assert.Equal(16, result.Allocations.Calls);
        Assert.Equal(1, result.ReceiptMissing); Assert.False(result.Steps[1].Accepted); Assert.Null(result.Journal);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
    }

    [Fact]
    public async Task PublicReportStrictlyRejectsTamperedPopulationIdentityConservationAndLifecycle()
    {
        using var files = new Inputs();
        var report = await EconomicsRunner.RunAsync(files.PlanPath, false);
        var original = EconomicsReportJson.Write(report);
        Action<JsonObject>[] mutations =
        [
            node => node["extra"] = true,
            node => node.Remove("receipt_missing"),
            node => node["receipt_missing"] = 1,
            node => node["allocations"]!["calls"] = 0,
            node => node["journal"]!["reservations"]!["calls"] = 0,
            node => node["steps"]![1]!["predecessor_sha256"] = new string('f', 64),
            node => node["steps"]![0]!["accepted"] = false,
            node => node["campaign"] = "forged-campaign",
            node => node["outcomes"]![0]!["attempt_sha256"] = new string('f', 64),
            node => node["steps"]![0]!["observation"]!["reservations"] = 8,
            node => node["steps"]![0]!["observation"]!["calls"]![0]!["usage"]!["input_tokens"] = 123,
            node => node["steps"]![0]!["observation"]!["measurement"]!["calls"] = 0,
        ];
        foreach (var mutate in mutations)
        {
            var node = JsonNode.Parse(original)!.AsObject(); mutate(node);
            Assert.Null(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(node.ToJsonString())));
        }
        var duplicated = Encoding.UTF8.GetString(original).Replace("\"format\":", "\"format\":\"duplicate\",\"format\":", StringComparison.Ordinal);
        Assert.Null(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(duplicated)));
        Assert.Null(EconomicsReportJson.Read(new byte[EconomicsLiveLimits.ReportBytes + 1]));
        var missing = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = EconomicsFault.AfterPrepareCrash, FaultIndex = 1 });
        var altered = JsonNode.Parse(EconomicsReportJson.Write(missing))!; altered["campaign"] = "forged-campaign";
        Assert.Null(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(altered.ToJsonString())));
        altered = JsonNode.Parse(EconomicsReportJson.Write(missing))!;
        altered["steps"]![0]!["observation"]!["calls"]![0]!["ordinal"] = 0;
        Assert.Null(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(altered.ToJsonString())));
    }

    [Fact]
    public async Task EnvironmentAndPrivateStateCanariesNeverReachPublicOutputOrChildEnvironment()
    {
        using var files = new Inputs();
        string[] names = [LiveEnvironmentSecretSource.ProviderVariable, "AGENTIC_REVIEW_R3_STATE_KEY_B64",
            "GITHUB_TOKEN", "ACTIONS_RUNTIME_TOKEN", "APR278_UNRELATED_SECRET"];
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        var privateValues = new List<string> { Canary, files.Root }; var secrets = new Secrets();
        try
        {
            foreach (var name in names) Environment.SetEnvironmentVariable(name, Canary);
            var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
            {
                Secrets = secrets,
                BeforeChild = input =>
                {
                    privateValues.Add(input.Root); privateValues.Add(Convert.ToBase64String(input.StateKey)); privateValues.Add(input.Session);
                    var environment = ReplayProcess.StartInfo(input.Root).Environment;
                    Assert.Equal(ReplayProcess.EnvironmentNames.Order(), environment.Keys.Order());
                    Assert.All(names, name => Assert.False(environment.ContainsKey(name)));
                    Assert.DoesNotContain(Canary, environment.Values);
                },
            });
            Assert.Equal("complete", result.StopReason); Assert.Equal(0, secrets.Reads);
            var output = Encoding.UTF8.GetString(EconomicsReportJson.Write(result));
            Assert.All(privateValues, value => Assert.DoesNotContain(value, output));
            Assert.DoesNotContain("reasoning_content", output); Assert.DoesNotContain("authorization", output, StringComparison.OrdinalIgnoreCase);
            var command = await Command(["economics-live", "--plan", files.PlanPath]);
            Assert.Equal(0, command.Exit); Assert.DoesNotContain(Canary, command.Output + command.Error);
            Assert.All(names, name => Assert.Equal(Canary, Environment.GetEnvironmentVariable(name)));
        }
        finally { foreach (var name in names) Environment.SetEnvironmentVariable(name, previous[name]); }
    }

    [Fact]
    public async Task CancellationBeforeAcceptPreservesKnownUsageAndCleanupFailureIsVisible()
    {
        using var files = new Inputs(); using var cancellation = new CancellationTokenSource(); string? root = null;
        try
        {
            var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
            {
                BeforeAccept = cancellation.Cancel, PrivateRoot = value => root = value, Cleanup = _ => false,
            }, cancellation.Token);
            Assert.Equal("caller_cancelled", result.StopReason); Assert.Equal("cleanup_failed", result.Cleanup);
            Assert.Equal("completed", result.Steps[0].EvaluationStatus); Assert.False(result.Steps[0].Accepted);
            Assert.True(result.Journal!.Totals.ActualSends > 0); Assert.NotNull(result.Pricing);
            Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        }
        finally { if (root is not null) Assert.True(ReplayProcess.Cleanup(root)); }
    }

    [Fact]
    public async Task RepetitionsAndSpacingRemainPreselectedAndUseOneConservingLedger()
    {
        using var files = new Inputs();
        var plan = EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            [new("replay", 2, 2, false)], spacingMilliseconds: 50);
        var original = EconomicsPlan.Admit(files.Plan, false); files.Write(plan);
        Assert.NotEqual(original.WorkloadSha256, EconomicsPlan.Admit(plan, false).WorkloadSha256);
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false);
        Assert.Equal("complete", result.StopReason); Assert.Equal(4, result.Attempted); Assert.Equal(32, result.Allocations.Calls);
        Assert.All(result.Steps.Skip(1), step => Assert.True(step.IntervalMilliseconds >= 50));
        Assert.False(result.Steps[2].Restored); Assert.True(result.Steps[3].Restored);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualComparisonCommandConsumesSuccessfulAndKnownFailedC2Evidence(bool failure)
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new() { Fault = failure ? EconomicsFault.RateLimit : EconomicsFault.None });
        var evidence = new ComparisonEvidence(result.Outcomes, [], [], []);
        var selected = ComparisonJson.Select(result.Pricing!, evidence);
        var declaration = new ComparisonDeclaration("none", "not_requested", selected, selected, null, null);
        var path = Path.Combine(files.Root, "comparison.json");
        File.WriteAllBytes(path, ComparisonJson.WriteInput(new(ComparisonLimits.InputFormat, declaration, result.Pricing!, evidence)));
        var command = await Command(["economics-compare", "--left", path, "--right", path]);
        Assert.True(command.Exit == 0, command.Error); Assert.Contains("inconclusive", command.Output);
        Assert.DoesNotContain(files.Root, command.Output);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FailedPrepareWithRecoveryReceiptPreservesCompletedEvaluationAndKnownTraffic(int index)
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false,
            new() { Fault = EconomicsFault.PrepareWriteFailure, FaultIndex = index });
        Assert.Equal(index + 1, result.Attempted); Assert.Equal(0, result.ReceiptMissing);
        var failed = result.Steps[index];
        Assert.Equal("state_failed", failed.Code); Assert.Equal("completed", failed.EvaluationStatus);
        Assert.False(failed.Prepared); Assert.False(failed.Accepted);
        Assert.All(failed.Observation!.Calls, call => Assert.Equal("known", call.UsageStatus));
        Assert.Equal(2, failed.Observation.Reservations); Assert.NotNull(failed.Observation.CompletedSessionSha256);
        Assert.All(result.Steps.Skip(index + 1), step => Assert.False(step.Allocated));
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        Assert.NotNull(UsageJournal.Admit(result.Journal)); Assert.NotNull(Compare(result, result));
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("not_created")]
    public async Task FinalMissingReceiptCannotClaimSuccessfulCompletionOrAnUncreatedRoot(string mutation)
    {
        using var files = new Inputs();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false,
            new() { Fault = EconomicsFault.AfterPrepareCrash, FaultIndex = 2 });
        Assert.Equal(3, result.Attempted); Assert.Equal(1, result.ReceiptMissing);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
        var changed = mutation == "complete" ? result with { StopReason = "complete" } : result with { Cleanup = "not_created" };
        Assert.Null(EconomicsReportJson.Read(JsonSerializer.SerializeToUtf8Bytes(changed, EconomicsLiveJson.Default.EconomicsReport)));
    }

    [Fact]
    public async Task SyntheticCredentialCrossesPrivateFrameOnlyIntoAuthorizationAndRestoresProtectedHistory()
    {
        using var files = new Inputs();
        var previous = EconomicsCredentialProbe.Canaries.Keys.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Assert.Equal(32, Convert.FromBase64String(EconomicsCredentialProbe.Canaries["AGENTIC_REVIEW_R3_STATE_KEY_B64"]).Length);
        var keys = new List<byte[]>(); var receipts = new List<EconomicsReceipt>();
        var secrets = new ProbeSecrets();
        try
        {
            foreach (var item in EconomicsCredentialProbe.Canaries) Environment.SetEnvironmentVariable(item.Key, item.Value);
            var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
            {
                CredentialProbe = true, Secrets = secrets,
                BeforeChild = input => keys.Add(input.StateKey.ToArray()), ObservedReceipt = receipts.Add,
            });
            Assert.True(result.StopReason == "complete", Describe(result)); Assert.Equal("loopback", result.ExecutionKind);
            Assert.Equal(1, secrets.Reads); Assert.Equal(3, receipts.Count);
            Assert.Equal(3, receipts.Select(receipt => receipt.Startup).Distinct().Count());
            foreach (var receipt in receipts)
            {
                var proof = Assert.IsType<EconomicsCredentialProof>(receipt.CredentialProof);
                Assert.Equal(2, proof.Requests); Assert.True(proof.EnvironmentChecked); Assert.True(proof.CompletedSessionChecked);
                Assert.Equal(receipt.Index > 0, proof.RestoredSessionChecked); Assert.True(proof.StoredObjects > 0);
                EconomicsCredentialProbe.AssertProtected(JsonSerializer.SerializeToUtf8Bytes(receipt,
                    EconomicsLiveJson.Default.EconomicsReceipt), keys[receipt.Index]);
            }
            var output = EconomicsReportJson.Write(result);
            Assert.All(keys, key => EconomicsCredentialProbe.AssertProtected(output, key));
            Assert.DoesNotContain("credential_proof", Encoding.UTF8.GetString(output));
            Assert.NotNull(Compare(result, result));
            Assert.All(EconomicsCredentialProbe.Canaries, item => Assert.Equal(item.Value, Environment.GetEnvironmentVariable(item.Key)));
        }
        finally
        {
            foreach (var item in previous) Environment.SetEnvironmentVariable(item.Key, item.Value);
            foreach (var key in keys) System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
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

    [Fact]
    public async Task CampaignDeadlineDuringChildSupervisionIsNotMisreportedAsOperatorCancellation()
    {
        using var files = new Inputs();
        files.Write(EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            [new("replay", 2, 1, false)], childSeconds: 1));
        var secrets = new Secrets();
        var result = await EconomicsRunner.RunAsync(files.PlanPath, false, new()
        {
            Secrets = secrets,
            Process = async (input, credential, token) =>
            {
                // Delay the selected child until the actual campaign deadline expires.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.Infinite, token));
                return await EconomicsProcess.RunAsync(input, credential, token);
            },
        });
        Assert.Equal("deadline", result.StopReason); Assert.Equal("cleaned", result.Cleanup);
        Assert.Equal(1, result.Attempted); Assert.Equal(1, result.ReceiptMissing); Assert.Equal(0, secrets.Reads);
        Assert.False(result.Steps[1].Allocated);
        Assert.NotNull(EconomicsReportJson.Read(EconomicsReportJson.Write(result)));
    }

    [LinuxInterruptTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualCliInterruptPreservesKnownPrefixReapsWorkerAndCleansPrivateRoot(bool activeChild)
    {
        using var files = new Inputs();
        files.Write(EconomicsCommand.Prepare(files.Plan.Replay.Path, files.Plan.Growth.Path, files.Plan.TariffPath,
            [new("replay", 3, 1, false)], spacingMilliseconds: 2000));
        var start = ReplayProcess.StartInfo(files.Root);
        start.ArgumentList[^1] = "economics-live"; start.ArgumentList.Add("--plan"); start.ArgumentList.Add(files.PlanPath);
        using var command = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = command.StandardOutput.ReadToEndAsync(timeout.Token);
        Process? worker = null;
        try
        {
            var marker = activeChild ? "r6_economics_child_ready 1 " : "r6_economics_step_completed 0";
            while (true)
            {
                var line = await command.StandardError.ReadLineAsync(timeout.Token);
                Assert.NotNull(line);
                if (!line.StartsWith(marker, StringComparison.Ordinal)) continue;
                if (activeChild)
                {
                    worker = Process.GetProcessById(int.Parse(line.Split(' ')[^1], System.Globalization.CultureInfo.InvariantCulture));
                    Assert.False(worker.HasExited);
                }
                break;
            }
            var campaignRoot = Assert.Single(Directory.GetDirectories(Path.Combine(files.Root, "tmp"), "apr-r5-replay-*"));
            var signal = new ProcessStartInfo("/bin/kill") { UseShellExecute = false, CreateNoWindow = true };
            signal.ArgumentList.Add("-s"); signal.ArgumentList.Add("INT");
            signal.ArgumentList.Add(command.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (var interrupt = Process.Start(signal)!)
            { await interrupt.WaitForExitAsync(timeout.Token); Assert.Equal(0, interrupt.ExitCode); }
            var remainingError = command.StandardError.ReadToEndAsync(timeout.Token);
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await command.WaitForExitAsync(reap.Token);
            Assert.Equal(1, command.ExitCode);
            var report = Assert.IsType<EconomicsReport>(EconomicsReportJson.Read(Encoding.UTF8.GetBytes(await output)));
            Assert.Equal("caller_cancelled", report.StopReason); Assert.Equal("cleaned", report.Cleanup);
            Assert.Equal(activeChild ? 2 : 1, report.Attempted);
            Assert.True(report.Steps[0].Accepted); Assert.True(report.Steps[0].Readback);
            Assert.Equal(2, report.Steps[0].Observation!.Reservations);
            Assert.All(report.Steps[0].Observation!.Calls, call => Assert.Equal("known", call.UsageStatus));
            Assert.False(report.Steps[2].Allocated); Assert.False(Directory.Exists(campaignRoot));
            if (worker is not null) Assert.True(worker.HasExited);
            Assert.DoesNotContain(files.Root, await remainingError);
        }
        finally
        {
            if (!command.HasExited) { command.Kill(entireProcessTree: true); await command.WaitForExitAsync(); }
            if (worker is not null)
            {
                if (!worker.HasExited) { worker.Kill(entireProcessTree: true); await worker.WaitForExitAsync(); }
                worker.Dispose();
            }
        }
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
        internal Inputs(bool full = false)
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
                full ? default : [new("replay", 3, 1, false)]);
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
