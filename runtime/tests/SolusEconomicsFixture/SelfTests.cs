using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgenticPrReview.Runtime.SolusFreshHostProof;
using SolusAgent.Api.Context;
using SolusAgent.Api.Usage;
using SolusAgent.Runtime.Api.Providers;

namespace AgenticPrReview.Runtime.SolusEconomicsFixture;

internal static class SelfTests
{
    internal static readonly List<string> Passed = [];
    internal static async Task Run()
    {
        Contracts(); Passed.Add("contracts");
        NullableUsage(); Passed.Add("nullable");
        Pricing(); Passed.Add("pricing");
        Comparisons(); Passed.Add("comparison");
        await ProviderTests.Wire(); Passed.Add("wire");
        await ProviderTests.Freeze(); Passed.Add("freeze");
        await ProviderTests.Retry(); Passed.Add("retry");
    }
    internal static RunFact Usage(long? input = 11, long? output = 7, long? hit = 3, long? miss = 8,
        DispatchExposure exposure = DispatchExposure.Dispatched, UsageInventoryCoverage coverage = UsageInventoryCoverage.Complete)
    {
        var details = new[] { new ProviderTokenCounter(ProviderTokenCounterKind.CacheRead, hit, TokenCounterRelationship.IncludedInInput),
            new ProviderTokenCounter(ProviderTokenCounterKind.UncachedInput, miss, TokenCounterRelationship.IncludedInInput) };
        var id = Guid.NewGuid();
        return RunFact.From(new(id, coverage, [new(id, Guid.NewGuid(), Guid.NewGuid(), 1, exposure, new(input, output, details))]));
    }
    internal static EconomicsDocument Document()
    {
        var run = Usage(); var attempt = run.Attempts[0];
        SegmentFact Token(string name) => new(Evidence.Hash("unit", name), 8);
        var wire = new WireFact(run.ExecutionId, attempt.LogicalCallId, attempt.PhysicalAttemptId, 1,
            Token("whole"), Token("settings"), [new("system", Token("control"), Evidence.Hash("content", "control"), null, null),
                new("user", Token("context"), Evidence.Hash("content", "context"), null, null),
                new("user", Token("data"), Evidence.Hash("content", "data"), null, null)]);
        return Evidence.Seal(new(Evidence.Format, "controlled", "known", new(new('a', 40), new('b', 40), true, new('c', 64), new('d', 64)),
            new("0.1.0-internal.4", "cr1-solus-internal4-dev", new('e', 64), Guid.NewGuid(), 2, 1, new('f', 64), new('0', 64)),
            Environment.ProcessId, Evidence.Hash("startup", "unit-only"), ContextAdmission.Fresh, ContextCaptureStatus.Delivered,
            Guid.NewGuid(), Guid.NewGuid(), true, run, [], [new(attempt, ProviderOutcome.Succeeded, ProviderError.None)], [wire],
            new(Evidence.Hash("content", "unit-final"), Evidence.Hash("reasoning", "unit-reasoning"), 0),
            true, true, false, false, 0, 0, SyntheticPrice.Create(run), ""));
    }
    private static EconomicsDocument RestoredDocument()
    {
        var report = Document(); var prior = Usage(10, 5, 4, 6);
        prior = RunFact.From(new(prior.ExecutionId, UsageInventoryCoverage.Complete,
            Enumerable.Range(0, 5).Select(_ => (prior.Attempts[0] with {
                LogicalCallId = Guid.NewGuid(), PhysicalAttemptId = Guid.NewGuid() }).ToObservation()).ToArray()));
        var messages = report.Wire[0].Messages.ToList();
        MessageFact Assistant(int index) => new("assistant", new(Evidence.Hash("unit", "assistant-" + index), 8),
            Evidence.Hash("content", "assistant-" + index), Evidence.Hash("reasoning", "assistant-" + index), null);
        for (var index = 0; index < 4; index++)
        {
            messages.Add(Assistant(index));
            messages.Add(new("tool", new(Evidence.Hash("unit", "tool-" + index), 8),
                Evidence.Hash("content", "tool-" + index), null, Evidence.Hash("tool", "tool-" + index)));
        }
        messages.Add(Assistant(4)); messages.Add(report.Wire[0].Messages[1]); messages.Add(report.Wire[0].Messages[2]);
        return Evidence.Seal(report with { Admission = ContextAdmission.Supplied,
            History = [new(Guid.NewGuid(), Guid.NewGuid(), prior)],
            Wire = [report.Wire[0] with { Messages = messages.ToArray() }] });
    }
    private static void Contracts()
    {
        var report = Document(); var bytes = Evidence.Write(report);
        Proof.Require(Evidence.Read(bytes) is not null, "v2_unit_original");
        void Reject(EconomicsDocument changed, string code) => Proof.Require(Evidence.Read(Evidence.Write(Evidence.Seal(changed))) is null, code);
        Reject(report with { ExecutionKind = "live" }, "v2_unit_controlled_provenance");
        Reject(report with { Current = report.Current! with { ContinuedCalls = 1 } }, "v2_unit_continued_not_current");
        Reject(report with { Current = report.Current! with { Attempts = [report.Current!.Attempts[0], report.Current.Attempts[0]] } }, "v2_unit_duplicate_attempt");
        Reject(report with { Current = report.Current! with { Input = new(999, TokenObservationCoverage.Complete) } }, "v2_unit_rebound_wrong_totals");
        Reject(report with { Price = report.Price with { Observed = report.Price.Observed with { Total = 0 } } }, "v2_unit_rebound_price");
        Reject(report with { History = [new(Guid.NewGuid(), Guid.NewGuid(), report.Current!)] }, "v2_unit_history_not_current");
        Reject(report with { Wire = [report.Wire[0] with { PhysicalAttemptId = Guid.NewGuid() }] }, "v2_unit_wire_association");
        Reject(report with { Exchanges = [report.Exchanges[0] with { Observation = report.Current!.Attempts[0] with { LogicalCallId = Guid.NewGuid() } }] }, "v2_unit_settlement_association");
        var text = Encoding.UTF8.GetString(bytes);
        Proof.Require(Evidence.Read(Encoding.UTF8.GetBytes(text.Insert(1, "\"format\":\"duplicate\","))) is null, "v2_unit_duplicate_json");
        Proof.Require(Evidence.Read(Encoding.UTF8.GetBytes(text.Insert(1, "\"unknown\":0,"))) is null, "v2_unit_unknown_json");
        var value = JsonNode.Parse(bytes)!; value.AsObject().Remove("admission");
        Proof.Require(Evidence.Read(Encoding.UTF8.GetBytes(value.ToJsonString())) is null, "v2_unit_required_nullable_field");
        foreach (var canary in new[] { ControlledProvider.Reasoning, ControlledProvider.ProviderRequestId, ControlledProvider.CurrentOnly })
            foreach (var encoding in new[] { canary, JsonSerializer.Serialize(canary).Replace("V", "\\u0056", StringComparison.Ordinal),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(canary)) })
                Throws(() => Privacy.Protect(Encoding.UTF8.GetBytes(encoding)), "v2_unit_private_encodings");
        Privacy.Protect(bytes);
        FailureAndAdmissionContracts();
    }
    private static void FailureAndAdmissionContracts()
    {
        var restored = RestoredDocument();
        bool Admitted(EconomicsDocument value) => Evidence.Read(Evidence.Write(Evidence.Seal(value))) is not null;
        var invalidPair = restored with { ProductAccepted = false, DurableAccepted = false,
            Exchanges = [restored.Exchanges[0] with { Outcome = ProviderOutcome.Failed, Error = ProviderError.InvalidResponse }] };
        var missing = restored with { Admission = null, HistoryReported = false, History = [] };
        var relabeled = restored with { Admission = ContextAdmission.Fresh, History = [] };
        Proof.Require(Admitted(restored), "v2_unit_restored_context_original");
        Proof.Require(!Admitted(invalidPair), "v2_unit_invalid_provider_failure_pair");
        Proof.Require(!Admitted(missing), "v2_unit_success_requires_context_admission");
        Proof.Require(!Admitted(relabeled), "v2_unit_restored_not_fresh");
        Proof.Require(!Admitted(restored with { Wire = [restored.Wire[0] with { Messages = restored.Wire[0].Messages[..3] }] }),
            "v2_unit_supplied_requires_historical_wire");
        var failure = restored with { ProductAccepted = false, DurableAccepted = false };
        foreach (var outcome in Enum.GetValues<ProviderOutcome>())
        {
            var permitted = outcome switch {
                ProviderOutcome.Succeeded => new[] { ProviderError.None },
                ProviderOutcome.Failed => [ProviderError.ProviderFailed],
                ProviderOutcome.Cancelled => [ProviderError.Cancelled],
                _ => Enum.GetValues<ProviderError>().Where(error => error != ProviderError.None).ToArray(),
            };
            foreach (var error in Enum.GetValues<ProviderError>())
                Proof.Require(Admitted(failure with { Exchanges = [failure.Exchanges[0] with { Outcome = outcome, Error = error }] }) ==
                    permitted.Contains(error), "v2_unit_provider_result_contract_matrix");
        }
    }
    private static void NullableUsage()
    {
        var unavailable = RunFact.From(new(Guid.NewGuid(), UsageInventoryCoverage.Unavailable, []));
        var empty = RunFact.From(new(Guid.NewGuid(), UsageInventoryCoverage.Complete, []));
        Proof.Require(unavailable.Input is { ObservedTokens: null, Coverage: TokenObservationCoverage.Unavailable } &&
            empty.Input is { ObservedTokens: 0, Coverage: TokenObservationCoverage.Complete }, "v2_unit_unavailable_not_zero");
        var partial = Usage(coverage: UsageInventoryCoverage.Partial);
        Proof.Require(partial.Input.Coverage == TokenObservationCoverage.Partial && !SyntheticPrice.Create(partial).Observed.InputComplete,
            "v2_unit_partial_not_complete");
        var notSent = Usage(null, null, null, null, DispatchExposure.NotDispatched);
        var unknown = Usage(null, null, null, null, DispatchExposure.Unknown);
        Proof.Require(notSent.Attempts[0].InputTokens is null && notSent.Input.ObservedTokens == 0 &&
            unknown.Input.ObservedTokens is null && SyntheticPrice.Create(unknown).Observed.Total is null, "v2_unit_exposure_not_measurement");
        var noInput = Usage(null, 7, 3, null);
        Proof.Require(noInput.Input.ObservedTokens is null && noInput.Output.ObservedTokens == 7 &&
            SyntheticPrice.Detail(noInput.Attempts[0], ProviderTokenCounterKind.CacheRead) == 3, "v2_unit_independent_parent");
        var id = Guid.NewGuid();
        var overflow = RunFact.From(new(id, UsageInventoryCoverage.Complete,
            [new(id, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched, new(long.MaxValue, 0)),
             new(id, Guid.NewGuid(), Guid.NewGuid(), 1, DispatchExposure.Dispatched, new(long.MaxValue, 0))]));
        Proof.Require(overflow.Input is { ObservedTokens: null, Coverage: TokenObservationCoverage.Overflow } &&
            SyntheticPrice.Create(overflow).Observed.Total is null, "v2_unit_overflow_not_saturated");
        Throws(() => new UsageObservation(10, 5, [new(ProviderTokenCounterKind.CacheRead, 11, TokenCounterRelationship.IncludedInInput)]), "v2_unit_invalid_subset");
    }
    private static void Pricing()
    {
        Proof.Require(SyntheticPrice.Create(Usage()).Observed.Total == .000070m, "v2_unit_reference_actual_values");
        Proof.Require(SyntheticPrice.Create(Usage(10, 5, 4, 6)).Observed.Total == .000053m, "v2_unit_seed_reference_values");
        Proof.Require(SyntheticPrice.Create(Usage(0, 0, 0, 0)).Observed.Total == 0m, "v2_unit_measured_zero_price");
        foreach (var value in new[] { Usage(hit: null), Usage(miss: null), Usage(hit: null, miss: null) })
        {
            var price = SyntheticPrice.Create(value);
            Proof.Require(price.Observed is { Total: null, KnownInput: null, KnownOutput: .000035m } &&
                price.SameTokenAllMissCounterfactual.Total == .000079m, "v2_unit_missing_peer_not_inferred");
        }
        var unavailable = Usage(null, null, null, null);
        Proof.Require(SyntheticPrice.Create(unavailable).Observed.Total is null && SyntheticPrice.Create(null).Observed.Total is null &&
            SyntheticPrice.Create(null, true).Observed.Total == 0, "v2_unit_incomplete_price");
    }
    private static void Comparisons()
    {
        var report = Document(); var baseline = Comparison.Create(report, report);
        Proof.Require(baseline is { PrefixEligibility: "comparable", PrefixStable: true, EconomicsEligibility: "comparable", ReferenceDifference: 0 }, "v2_unit_self_comparison");
        foreach (var changed in new[] { report with { Source = report.Source with { Commit = new('1', 40) } },
            report with { Source = report.Source with { Tree = new('1', 40) } },
            report with { Domain = report.Domain! with { PackageVersion = "0.1.0-internal.5" } },
            report with { Domain = report.Domain! with { Profile = "different-profile" } },
            report with { Domain = report.Domain! with { RuntimeConfigurationSha256 = new('1', 64) } },
            report with { Source = report.Source with { CorpusSha256 = new('1', 64) } } })
            Proof.Require(Comparison.Create(report, Evidence.Seal(changed)).PrefixStable is null, "v2_unit_domain_incomparable");
        var incomplete = report with { Current = report.Current! with { Coverage = UsageInventoryCoverage.Partial } };
        // Construct a coherent partial typed snapshot instead of merely changing its label.
        var partial = RunFact.From(new(report.Current!.ExecutionId, UsageInventoryCoverage.Partial,
            report.Current.Attempts.Select(a => a.ToObservation()).ToArray()));
        incomplete = Evidence.Seal(incomplete with { Current = partial, ProductAccepted = false, DurableAccepted = false,
            Price = SyntheticPrice.Create(partial) });
        var comparison = Comparison.Create(incomplete, incomplete);
        Proof.Require(comparison is { PrefixStable: true, EconomicsEligibility: "incomplete_usage", ReferenceDifference: null }, "v2_unit_separate_prefix_and_usage");
        var wholeChanged = report.Wire[0] with { Whole = report.Wire[0].Whole with { Sha256 = new('9', 64) } };
        Proof.Require(Comparison.Create(report, Evidence.Seal(report with { Wire = [wholeChanged] })).PrefixStable is true,
            "v2_unit_whole_not_prefix");
    }
    internal static void Throws(Action action, string code)
    { try { action(); } catch (Exception error) when (error is ArgumentException or ProofFailure) { Proof.Require(true, code); return; } throw new ProofFailure(code); }
}
