using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal static class EconomicsCommand
{
    internal static int Invoke(string[] args)
    {
        try
        {
            if (args is ["economics-plan", "--replay", var replay, "--growth", var growth, "--tariff", var tariff, "--out", var output])
            {
                var prepared = Prepare(replay, growth, tariff);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(prepared, EconomicsLiveJson.Default.EconomicsPlanInput);
                using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                file.Write(bytes);
                Console.WriteLine("r6_economics_preparation_only");
                return 0;
            }
            throw new EconomicsRejected("r6_economics_input_invalid");
        }
        catch (OperationCanceledException) { Console.Error.WriteLine("r6_economics_cancelled"); return 1; }
        catch (EconomicsRejected rejected) { Console.Error.WriteLine(rejected.Code); return 2; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or OverflowException or ArgumentException)
        { Console.Error.WriteLine("r6_economics_input_invalid"); return 2; }
        catch { Console.Error.WriteLine("r6_economics_infrastructure_failed"); return 1; }
    }

    internal static EconomicsPlanInput Prepare(string replay, string growth, string tariff,
        ImmutableArray<EconomicsScenario> scenarios = default, int childSeconds = 30, int spacingMilliseconds = 0)
    {
        if (scenarios.IsDefault) scenarios = [new("replay", 3, 1, false), new("tools", 6, 1, true), new("continuation", 7, 1, true)];
        var replayFixture = ReplayAdmission.Load(replay).Fixture ?? throw new EconomicsRejected("r6_economics_corpus_invalid");
        var growthFixture = ReplayAdmission.Load(growth).Fixture ?? throw new EconomicsRejected("r6_economics_corpus_invalid");
        var price = AdmittedTariff.Read(EconomicsPlan.ReadFile(tariff, PricingLimits.TariffBytes, default), out _) ??
            throw new EconomicsRejected("r6_economics_tariff_invalid");
        var count = EconomicsPlan.Expand(scenarios).Length;
        // This is a conservative preparation ceiling, not a current billing quote or execution grant.
        var per = new LivePlanPerCall(32768, 4096, 1_000_000);
        var calls = count * 8L;
        var milliseconds = checked(count * (long)childSeconds * 1000 +
            EconomicsPlan.CampaignOverheadMilliseconds(count, spacingMilliseconds));
        var plan = new EconomicsPlanInput(EconomicsLiveLimits.PlanFormat,
            new(EvaluationSource.Commit, EvaluationSource.Tree, EvaluationSource.Clean), EconomicsBuild.Current(),
            new(Path.GetFullPath(replay), replayFixture.CorpusSha256), new(Path.GetFullPath(growth), growthFixture.CorpusSha256),
            new(DeepSeekAdapterContext.Provider, DeepSeekAdapterContext.Model, DeepSeekAdapterContext.Adapter,
                LivePlanAdmission.ProviderConfigurationSha256()), "high", Path.GetFullPath(tariff), price.Sha256,
            scenarios, childSeconds, spacingMilliseconds, "stop_remaining_tail",
            new(count, calls, calls * per.MaxInputTokens, calls * per.MaxOutputTokens,
                calls * (per.MaxInputTokens + per.MaxOutputTokens), checked((milliseconds + 999) / 1000),
                calls * per.MaxChargeMicroUsd, per));
        _ = EconomicsPlan.Admit(plan, false).LoadTariff(default);
        return plan;
    }
}
