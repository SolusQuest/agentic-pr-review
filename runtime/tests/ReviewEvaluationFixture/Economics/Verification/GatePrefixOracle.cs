using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Pricing;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using static AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification.GateContracts;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Verification;

internal static class GatePrefixOracle
{
    internal static JsonElement Verify(GateCase item, GateSelection selection)
    {
        if (item.Id == "p1-invalid-position")
        {
            var rejected = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GateScalar, 4096, 4);
            Require(rejected is not null && rejected.Code == "positions_rejected" &&
                rejected.Values.SequenceEqual(new decimal?[] { 4, 0 }) && rejected.Facts.IsEmpty);
            return item.Evidence;
        }
        var value = PricingJson.ReadValue(Bytes(item.Evidence), GateJson.Default.GatePrefix, 128 * 1024, 16)!;
        Require(value is not null && value.Observations.Length == (item.Id == "p1-dynamic-suffix" ? 3 : 2) &&
            value.Comparisons.Length == value.Observations.Length - 1 && value.Counts.IsEmpty);
        foreach (var observation in value!.Observations)
        {
            // Shape admission does not validate digest values or the
            // bootstrap/generation/accepted-session relationship.
            Require(HistoryCapture.Safe(new("observed", observation, [])));
            Require(observation.Domain.SourceCommit == selection.SourceCommit && observation.Domain.SourceTree == selection.SourceTree &&
                observation.Domain.SourceClean == selection.SourceClean && observation.ControlMessages == 1 &&
                observation.HistoricalMessages is 0 or 3);
            foreach (var projection in new[] { observation.Logical, observation.Provider })
                foreach (var segment in new[] { projection.Control, projection.History, projection.Dynamic, projection.Settings, projection.Whole })
                    Require(segment.Sha256.Length == 64 && segment.Sha256.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                        segment.Bytes is >= 0 and <= AgentLimits.RequestBytes && segment.Count is >= 0 and <= AgentLimits.PartsTotal + AgentLimits.Messages);
        }
        var first = value.Observations[0];
        var next = value.Observations[1];
        for (var index = 1; index < value.Observations.Length; index++)
            Require(value.Comparisons[index - 1] == first.Compare(value.Observations[index]));
        if (item.Id == "p1-bootstrap-restored")
            Require(first.HistoricalMessages == 0 && first.Domain.Generation == -1 && first.Domain.AcceptedSessionSha256 is null &&
                first.Logical.History.Count == 0 && first.Provider.History.Bytes == 0 &&
                next.HistoricalMessages == 3 && next.Domain.Generation == 0 && next.Domain.AcceptedSessionSha256 is not null &&
                next.Logical.History.Count == 4 && next.Provider.History.Count == 3 && next.Provider.Dynamic.Count == 1 &&
                value.Comparisons[0] == new PrefixComparison("incomparable", null, null));
        else if (item.Id == "p1-domain")
            Require(next == first with { Domain = first.Domain with { SessionSha256 = Hash('c') } } &&
                first.Domain.SessionSha256 != next.Domain.SessionSha256 && value.Comparisons[0].Code == "incomparable");
        else if (item.Id == "p1-logical-only")
            Require(first.Provider == next.Provider && first.Logical.Whole != next.Logical.Whole);
        else if (item.Id == "p1-dynamic-suffix")
        {
            foreach (var suffix in value.Observations.Skip(1))
                Require(first.Compare(suffix) == new PrefixComparison("compared", true, true) &&
                    first.Provider.Dynamic != suffix.Provider.Dynamic && first.Logical.Dynamic != suffix.Logical.Dynamic);
            Require(next.Provider.Dynamic != value.Observations[2].Provider.Dynamic);
        }
        else
        {
            Require(value.Comparisons[0] == new PrefixComparison("compared", false, false));
            Require(item.Id switch
            {
                "p1-control" => first.Logical.Control != next.Logical.Control && first.Provider.Control != next.Provider.Control && first.Logical.History == next.Logical.History,
                "p1-settings" => first.Logical.Settings != next.Logical.Settings && first.Provider.Settings != next.Provider.Settings && first.Logical.History == next.Logical.History,
                "p1-history" or "p1-continuation-position" => first.Logical.History != next.Logical.History && first.Provider.History != next.Provider.History && first.Logical.Control == next.Logical.Control,
                _ => false,
            });
        }
        // This fixture uses fixed session/call identities. Every segment hash
        // and byte count is stable and stays in cross-mode parity.
        return item.Evidence;
    }
}
