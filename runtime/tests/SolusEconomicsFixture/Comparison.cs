using System.Text.Json;
using SolusAgent.Api.Context;
using SolusAgent.Api.Usage;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal static class Comparison
{
    internal static ComparisonFact Create(EconomicsDocument left, EconomicsDocument right)
    {
        ComparisonFact Refuse(string reason) => new(reason, null, reason, null, "unknown", "not_evidenced", "inconclusive");
        if (!Evidence.Admit(left) || !Evidence.Admit(right)) return Refuse("invalid_evidence");
        if (!left.Source.Clean || !right.Source.Clean) return Refuse("source_not_clean");
        if (left.Source != right.Source || left.Domain != right.Domain) return Refuse("domain_mismatch");
        if (left.Domain is null) return Refuse("domain_unavailable");
        if (left.Scenario != right.Scenario || left.Admission != right.Admission || !Boundary(left, right)) return Refuse("boundary_mismatch");
        if (left.Wire.Length == 0 || right.Wire.Length == 0) return Refuse("wire_unavailable");
        var count = Evidence.PrefixCount(left);
        if (count is null || count != Evidence.PrefixCount(right)) return Refuse("boundary_unverified");
        var all = left.Wire.Concat(right.Wire).ToArray(); var first = all[0];
        var stable = all.All(w => w.Messages.Length >= count && w.Settings == first.Settings &&
            w.Messages.Take(count.Value).SequenceEqual(first.Messages.Take(count.Value)));
        var complete = Complete(left.Current) && Complete(right.Current) &&
            left.Current!.Attempts.Length == right.Current!.Attempts.Length &&
            left.Price.Reference == right.Price.Reference && left.Price.Observed.Total is not null && right.Price.Observed.Total is not null;
        return new("comparable", stable, complete ? "comparable" : "incomplete_usage", complete
            ? right.Price.Observed.Total - left.Price.Observed.Total : null, "unknown", "not_evidenced", "inconclusive");
    }
    private static bool Complete(RunFact? value) => value is { Coverage: UsageInventoryCoverage.Complete, ContinuedCalls: 0 } &&
        value.Input.Coverage == TokenObservationCoverage.Complete && value.Output.Coverage == TokenObservationCoverage.Complete &&
        value.Attempts.All(a => a.Exposure != DispatchExposure.Unknown);
    private static bool Boundary(EconomicsDocument left, EconomicsDocument right) => left.History.Length == right.History.Length &&
        left.History.Zip(right.History).All(pair => pair.First.LogicalWorkId == pair.Second.LogicalWorkId &&
            pair.First.RoundId == pair.Second.RoundId && JsonSerializer.Serialize(pair.First.Usage, EconomicsJson.Default.RunFact) ==
            JsonSerializer.Serialize(pair.Second.Usage, EconomicsJson.Default.RunFact));
}
