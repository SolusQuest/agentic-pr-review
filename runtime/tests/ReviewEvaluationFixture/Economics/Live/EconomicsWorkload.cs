using System.Collections.Immutable;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Growth.Profiles;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Live;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Admission;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Replay.Execution;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Live;

internal sealed class EconomicsWorkload(EconomicsPlan plan, AdmittedReplayFixture replay, AdmittedReplayFixture growth)
{
    internal static EconomicsWorkload Load(EconomicsPlan plan, CancellationToken token)
    {
        var replay = ReplayAdmission.Load(plan.Input.Replay.Path, token).Fixture;
        var growth = ReplayAdmission.Load(plan.Input.Growth.Path, token).Fixture;
        if (replay is null || growth is null || replay.CorpusSha256 != plan.Input.Replay.Sha256 ||
            growth.CorpusSha256 != plan.Input.Growth.Sha256 || replay.Runs.Length != 3 ||
            !replay.Runs.Select(run => run.Input.Id).SequenceEqual(ReplayCoverage.Cases) || !GrowthProfiles.Matches(growth))
            EconomicsPlan.Reject("corpus_invalid");
        return new(plan, replay!, growth!);
    }

    internal AdmittedReplayRun Run(EconomicsSlot slot, string campaign)
    {
        var seed = slot.Profile == "replay" ? replay.Runs[slot.Phase] :
            GrowthProfiles.Run(growth, slot.Profile, slot.Phase);
        return seed.Derive(seed.Input with
        {
            Id = campaign + "-" + (slot.Index + 1), CaseId = slot.CaseId,
            PreviousRunId = slot.Previous is { } previous ? campaign + "-" + (previous + 1) : null,
        }, seed.Script, plan.WorkloadSha256);
    }

    internal static EconomicsPlan Capture(EconomicsPlan selected, string root, CancellationToken token)
    {
        LivePlanCorpus Copy(LivePlanCorpus source, string name)
        {
            var captured = ReplayDirectory.Capture(source.Path, token);
            var destination = Path.Combine(root, name);
            Directory.CreateDirectory(destination);
            foreach (var member in captured.Files)
            {
                var path = Path.Combine(destination, member.Key);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, member.Value.ToArray());
            }
            File.WriteAllBytes(Path.Combine(destination, ReplayLimits.ManifestName), ReplayJson.Write(captured.Manifest));
            return source with { Path = destination };
        }
        var tariff = Path.Combine(root, "tariff.json");
        File.WriteAllBytes(tariff, EconomicsPlan.ReadFile(selected.Input.TariffPath, PricingLimits.TariffBytes, token));
        return EconomicsPlan.Admit(selected.Input with
        {
            Replay = Copy(selected.Input.Replay, "replay"), Growth = Copy(selected.Input.Growth, "growth"),
            TariffPath = tariff,
        }, false);
    }
}
