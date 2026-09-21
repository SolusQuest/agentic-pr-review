using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Histories;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Prefix;
using AgenticPrReview.Runtime.ReviewEvaluationFixture.Evaluation;

namespace AgenticPrReview.Runtime.ReviewEvaluationFixture.Economics.Comparison;

internal static class ComparisonPrefix
{
    private sealed class Event(PrefixDomain domain, string signature, PrefixComparison comparison, bool verified)
    {
        internal PrefixDomain Domain { get; } = domain;
        internal string Signature { get; } = signature;
        internal PrefixComparison Comparison { get; } = comparison;
        internal bool Verified { get; } = verified;
        internal string? Expectation;
    }

    internal static ComparisonPrefixSummary Build(ComparisonInput left, ComparisonInput right)
    {
        var events = new Dictionary<string, Event>(StringComparer.Ordinal);
        var targets = new Dictionary<(string History, int Phase, int Call, string Observation), string>();
        foreach (var input in new[] { left, right })
        foreach (var history in input.Evidence.Histories)
        {
            var historyHash = ComparisonJson.HistoryHash(history);
            foreach (var row in history.Rows)
            {
                var baseline = row.Capture.Baseline;
                if (row.RestoredMatch && baseline is not null && row.Capture.Calls.FirstOrDefault() is { } first &&
                    row.Comparison != baseline.Compare(first))
                    throw new ComparisonInputException("r6_comparison_history_contradiction");
                for (var i = 0; i < row.Capture.Calls.Length; i++)
                {
                    if (row.Capture.Calls[i] is not { } observation) continue;
                    var key = EventKey(observation.Domain, i + 1);
                    var observedHash = ComparisonJson.ObservationHash(observation);
                    var signature = EvaluationAttempt.Hash("r6-prefix-event-content", observedHash,
                        baseline is null ? "absent" : ComparisonJson.ObservationHash(baseline),
                        row.RestoredMatch.ToString(), row.WireMatch.ToString(), row.Capture.Code);
                    // P2's WireMatch proves a nonempty prefix of captured calls, but exports no
                    // transported-call count. Only ordinal 1 has individual wire coverage from this DTO.
                    var verified = i == 0 && baseline is not null && row.RestoredMatch && row.WireMatch &&
                        row.Capture.Code == "observed";
                    var item = new Event(observation.Domain, signature,
                        baseline?.Compare(observation) ?? new("unavailable", null, null), verified);
                    if (events.TryGetValue(key, out var previous) && previous.Signature != signature)
                        throw new ComparisonInputException("r6_comparison_history_contradiction");
                    events.TryAdd(key, item);
                    targets[(historyHash, row.Phase, i + 1, observedHash)] = key;
                }
            }
        }
        var unbound = 0;
        foreach (var input in new[] { left, right })
        {
            var localHistories = input.Evidence.Histories.Select(ComparisonJson.HistoryHash).ToHashSet(StringComparer.Ordinal);
            foreach (var expectation in input.Evidence.Expectations)
            {
                if (!localHistories.Contains(expectation.HistorySha256) ||
                    !targets.TryGetValue((expectation.HistorySha256, expectation.Phase, expectation.CallOrdinal,
                        expectation.ObservationSha256), out var key))
                { unbound++; continue; }
                var item = events[key];
                if (item.Expectation is not null && item.Expectation != expectation.Expectation)
                    throw new ComparisonInputException("r6_comparison_expectation_conflict");
                item.Expectation = expectation.Expectation;
            }
        }
        var selected = new[] { left.Pricing.Journal.Provenance, right.Pricing.Journal.Provenance };
        var sources = events.Values.GroupBy(e => (e.Domain.SourceCommit, e.Domain.SourceTree, e.Domain.SourceClean))
            .OrderBy(g => g.Key.SourceCommit, StringComparer.Ordinal).ThenBy(g => g.Key.SourceTree, StringComparer.Ordinal)
            .ThenBy(g => g.Key.SourceClean).Select(group =>
            {
                var boundSource = group.Key.SourceClean && selected.Any(s => s.SourceCommit == group.Key.SourceCommit &&
                    s.SourceTree == group.Key.SourceTree && s.SourceClean == group.Key.SourceClean);
                static bool Unstable(Event item) => item.Comparison.Code == "compared" &&
                    (item.Comparison.LogicalStable == false || item.Comparison.ProviderStable == false);
                var unexpected = group.Count(e => boundSource && e.Verified && Unstable(e) && e.Expectation == "stable_continuity");
                return new PrefixSourceSummary(group.Key.SourceCommit, group.Key.SourceTree, group.Key.SourceClean,
                    group.Count(), group.Count(e => e.Comparison.Code != "compared"), group.Count(e => !e.Verified),
                    group.Count(Unstable),
                    group.Count(e => boundSource && e.Verified && e.Expectation == "stable_continuity" &&
                        e.Comparison == new PrefixComparison("compared", true, true)),
                    group.Count(e => e.Expectation == "intentional_fault"),
                    group.Count(e => e.Expectation is null or "unknown"), unexpected,
                    unexpected >= 2 ? "blocked_unexpected_prefix_drift" : "no_promotion_approval");
            }).ToImmutableArray();
        return new(sources, unbound, "native_campaign_link_unproven");
    }

    private static string EventKey(PrefixDomain domain, int ordinal) => EvaluationAttempt.Hash("r6-prefix-native-event",
        AgentCanonical.HashDomain("apr.r6.comparison.prefix-domain",
            JsonSerializer.SerializeToUtf8Bytes(domain, ComparisonJsonContext.Default.PrefixDomain)),
        ordinal.ToString(CultureInfo.InvariantCulture));
}
