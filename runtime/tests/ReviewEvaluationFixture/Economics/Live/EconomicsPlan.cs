using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Contracts;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal sealed class EconomicsPlan
{
    private EconomicsPlan(EconomicsPlanInput input, ImmutableArray<EconomicsSlot> slots)
    {
        Input = input; Slots = slots;
        Workload = new(input.Replay.Sha256, input.Growth.Sha256, input.Scenarios, input.ChildSeconds,
            input.SpacingMilliseconds, input.StopRule);
        Selection = new(input.Format, input.Source, input.BuildSha256, Workload, input.Provider,
            input.Thinking, input.TariffSha256, input.Bounds);
        WorkloadSha256 = AgentCanonical.HashDomain("apr.r6.economics.workload",
            JsonSerializer.SerializeToUtf8Bytes(Workload, EconomicsLiveJson.Default.EconomicsWorkloadSelection));
        Sha256 = Digest(Selection);
        Projection = new(LiveLimits.PlanFormat, input.Source, WorkloadSha256, input.Provider,
            slots.Select(slot => slot.CaseId).ToImmutableArray(), input.Bounds);
    }

    internal EconomicsPlanInput Input { get; }
    internal ImmutableArray<EconomicsSlot> Slots { get; }
    internal EconomicsWorkloadSelection Workload { get; }
    internal EconomicsPlanSelection Selection { get; }
    internal string WorkloadSha256 { get; }
    internal string Sha256 { get; }
    internal LivePlanDigestInput Projection { get; }
    internal EconomicsAllocation ChildAllocation => Allocation(Input.Bounds.PerCall, Input.ChildSeconds);
    internal EconomicsAllocation Ceiling => new(Input.Bounds.MaxEvaluations, Input.Bounds.MaxModelCalls,
        Input.Bounds.MaxInputTokens, Input.Bounds.MaxOutputTokens, Input.Bounds.MaxCombinedTokens,
        Input.Bounds.SpendCeilingMicroUsd, checked(Input.Bounds.MaxSeconds * 1000));
    internal LivePlanBounds ChildBounds => new(1, 8, ChildAllocation.InputTokens, ChildAllocation.OutputTokens,
        ChildAllocation.CombinedTokens, Input.ChildSeconds, ChildAllocation.SpendMicroUsd, Input.Bounds.PerCall);

    internal UsageJournalExpectation Expectation(string campaign, string transport) => new(
        new(campaign, Input.Source.Commit, Input.Source.Tree, Input.Source.Clean, "c2-" + Input.BuildSha256[..32],
            WorkloadSha256, Input.Provider.ConfigurationSha256, LivePlanAdmission.Digest(Projection), transport), Projection);

    internal static string Digest(EconomicsPlanSelection selection) => AgentCanonical.HashDomain("apr.r6.economics.plan",
        JsonSerializer.SerializeToUtf8Bytes(selection, EconomicsLiveJson.Default.EconomicsPlanSelection));

    internal static EconomicsPlan Load(string path, bool execute, CancellationToken token)
    {
        var input = ReplayWire.Read(ReadFile(path, EconomicsLiveLimits.PlanBytes, token),
            EconomicsLiveJson.Default.EconomicsPlanInput, EconomicsLiveLimits.PlanBytes);
        return Admit(input, execute);
    }

    internal static EconomicsPlan Admit(EconomicsPlanInput? input, bool execute, bool currentBuild = true)
    {
        if (input is not { Source: not null, Replay: not null, Growth: not null, Provider: not null, Bounds.PerCall: not null } ||
            input.Format != EconomicsLiveLimits.PlanFormat || !EvaluationLimits.Hash(input.BuildSha256) ||
            !EvaluationLimits.Hash(input.Replay.Sha256) || !EvaluationLimits.Hash(input.Growth.Sha256) ||
            !EvaluationLimits.Hash(input.TariffSha256) || input.Thinking != "high" ||
            input.ChildSeconds is < 1 or > 300 || input.SpacingMilliseconds is < 0 or > 60000 ||
            input.StopRule != "stop_remaining_tail" || input.Scenarios.IsDefaultOrEmpty ||
            input.Scenarios.Length > EconomicsLiveLimits.Scenarios || !PathValue(input.Replay.Path) ||
            !PathValue(input.Growth.Path) || !PathValue(input.TariffPath)) Reject("plan_invalid");
        var plan = new EconomicsPlan(input!, Expand(input!.Scenarios));
        if (!LivePlanAdmission.ValidProjection(plan.Projection)) Reject("plan_invalid");
        if (currentBuild && (input.Source.Commit != EvaluationSource.Commit || input.Source.Tree != EvaluationSource.Tree ||
                input.Source.Clean != EvaluationSource.Clean || input.BuildSha256 != EconomicsBuild.Current()) ||
            execute && (!input.Source.Clean || currentBuild && !EvaluationSource.Clean)) Reject("source_invalid");
        var per = input.Bounds.PerCall;
        if (per.MaxInputTokens is < 1 or > AgentLimits.InputTokens / 8 || per.MaxOutputTokens != 4096 ||
            input.Bounds.MaxModelCalls != plan.Slots.Length * 8L) Reject("allocation_invalid");
        EconomicsAllocation required = new(0, 0, 0, 0, 0, 0, checked((plan.Slots.Length - 1L) * input.SpacingMilliseconds));
        for (var index = 0; index < plan.Slots.Length; index++)
            required = EconomicsLedger.Add(required, plan.ChildAllocation) ?? throw new EconomicsRejected("r6_economics_allocation_invalid");
        if (!EconomicsLedger.Within(required, plan.Ceiling)) Reject("allocation_invalid");
        return plan;
    }

    internal AdmittedTariff LoadTariff(CancellationToken token)
    {
        var tariff = AdmittedTariff.Read(ReadFile(Input.TariffPath, PricingLimits.TariffBytes, token), out _);
        if (tariff is null || tariff.Sha256 != Input.TariffSha256) Reject("tariff_invalid");
        var terms = tariff!.Document.Terms;
        if (terms.Rates.Output.Currency != "USD") Reject("tariff_invalid");
        var per = Input.Bounds.PerCall;
        var amount = ((BigInteger)per.MaxInputTokens * Math.Max(terms.Rates.CacheHitInput.Units, terms.Rates.CacheMissInput.Units) +
            (BigInteger)per.MaxOutputTokens * terms.Rates.Output.Units) * 1_000_000;
        var denominator = (BigInteger)terms.TokenUnit * BigInteger.Pow(10, terms.RateDecimalPlaces);
        if (amount > (BigInteger)per.MaxChargeMicroUsd * denominator) Reject("allocation_invalid");
        return tariff;
    }

    internal static ImmutableArray<EconomicsSlot> Expand(ImmutableArray<EconomicsScenario> scenarios)
    {
        if (scenarios.IsDefaultOrEmpty || scenarios.Length > 16 || scenarios[0] is not { Profile: "replay", Phases: >= 2 })
            throw new EconomicsRejected("r6_economics_plan_invalid");
        var slots = ImmutableArray.CreateBuilder<EconomicsSlot>();
        var chain = 0;
        foreach (var scenario in scenarios)
        {
            if (scenario is null || scenario.Repeats is < 1 or > 16 || scenario.Phases < 1 ||
                (scenario.Profile == "replay" ? scenario.Phases > 3 || scenario.ResetAfterCapacity :
                    !GrowthProfiles.Names.Contains(scenario.Profile) || scenario.Phases > GrowthProfiles.Attempts))
                Reject("plan_invalid");
            for (var repeat = 0; repeat < scenario!.Repeats; repeat++)
            {
                var selectedChain = chain++;
                for (var phase = 0; phase < scenario.Phases; phase++)
                    slots.Add(new(slots.Count, "c2-" + scenario.Profile + "-" + phase, scenario.Profile, phase,
                        selectedChain, phase == 0 ? null : slots.Count - 1,
                        scenario.ResetAfterCapacity && phase == scenario.Phases - 1, null));
                if (scenario.ResetAfterCapacity)
                {
                    var resetChain = chain++;
                    for (var phase = 0; phase < 2; phase++)
                        slots.Add(new(slots.Count, "c2-" + scenario.Profile + "-reset-" + phase, scenario.Profile,
                            phase, resetChain, phase == 0 ? null : slots.Count - 1, false,
                            phase == 0 ? selectedChain : null));
                }
                if (slots.Count > EconomicsLiveLimits.Slots) Reject("plan_invalid");
            }
        }
        return slots.ToImmutable();
    }

    internal static EconomicsAllocation Allocation(LivePlanPerCall per, int seconds) =>
        new(1, 8, checked(per.MaxInputTokens * 8), checked(per.MaxOutputTokens * 8),
            checked((per.MaxInputTokens + per.MaxOutputTokens) * 8), checked(per.MaxChargeMicroUsd * 8), checked(seconds * 1000L));
    internal static byte[] ReadFile(string path, int maximum, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 || file.Length > maximum) Reject("input_invalid");
        using var result = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = file.Read(buffer)) > 0)
        {
            token.ThrowIfCancellationRequested();
            if (result.Length + read > maximum) Reject("input_invalid");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }
    private static bool PathValue(string? path) => path is { Length: > 0 and <= 4096 } && !path.Contains('\0');
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    internal static void Reject(string suffix) => throw new EconomicsRejected("r6_economics_" + suffix);
}
