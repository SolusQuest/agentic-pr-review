using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Action;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.GitHub;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot;
using AgenticPrReview.Runtime.Tests.Host.Action.Snapshot.ChangedFiles;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Inline;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Inline;

public sealed class InlineCandidateReplacementVectorTests
{
    private const string GoldenFingerprint =
        "43390faa4068833717f38a47230155790bed811f1e00a3d3caeef6d8b682e1fc";
    private const string GoldenInlineKey =
        "7d874d15e95245854e163b72111c2ccb5c8a18088e7f4ff14590d3f7c0b067f3";
    private const string GoldenPreimageHex =
        "6167656e7469632d70722d7265766965772f72342f696e6c696e652d6b65792f7631" +
        "0000000000000040" +
        "3433333930666161343036383833333731376633386134373233303135353739306265" +
        "6438313166316530306133643363616565663664386236383265316663" +
        "000000000000000a7372632f6170702e7473" +
        "000000000000000137";

    private enum TypeScriptDisposition
    {
        Retained,
        Superseded,
        Obsolete,
    }

    private sealed record ReplacementVector(
        string TypeScriptAssertionGroup,
        TypeScriptDisposition Disposition,
        string R4Owner,
        string ReplacementTest,
        string DeliberateDifference,
        string[] FrameworkScenarioIds);

    private static readonly ReplacementVector[] ReplacementLedger =
    [
        new(
            "target.test.ts: repository identity and pull-request eligibility",
            TypeScriptDisposition.Retained,
            "H2",
            nameof(ActionHostGitHubAuthorizationTransportTests.PullRequestReadRejectsMissingOrNullHeadRepository),
            "R4 admits one frozen non-forgeable invocation instead of returning a mutable ReviewTarget.",
            ["stale-head"]),
        new(
            "target.test.ts: raw GitHub patch hash before prompt truncation",
            TypeScriptDisposition.Superseded,
            "H5",
            nameof(BoundedReviewedSnapshotBuilderTests.CompositionReturnsCompleteImmutableAgentViewAndCleansStaging),
            "R4 constructs the diff from exact base and head bytes; GitHub patch text is never byte authority.",
            ["inline"]),
        new(
            "target.test.ts: open-state propagation into the TypeScript ledger gate",
            TypeScriptDisposition.Superseded,
            "H2 plus P6",
            nameof(ActionHostCompositionTests.PreservesExactH2SkipAndDoesNotMaterializeLaterLayers),
            "R4 closes ineligible pull requests before later capabilities exist and has no mutable ledger gate.",
            ["stale-head"]),
        new(
            "target.test.ts: incremental changed, removed, and unchanged snapshot delta",
            TypeScriptDisposition.Obsolete,
            "H5 plus P3",
            nameof(ReviewedChangedFileReaderTests.PostPaginationTupleDriftFailsClosed),
            "R4 reviews one authoritative current effective diff and does not carry the M4 delta model forward.",
            ["inline"]),
        new(
            "target.test.ts: file-SHA comparison for patch-unavailable entries",
            TypeScriptDisposition.Superseded,
            "H5 plus P3",
            nameof(InlineCandidateMapperTests.AllUnavailableSnapshotRemainsBoundAndMismatchesFailClosed),
            "R4 uses exact Git-object bytes and closed unavailable classifications instead of guessing from patch presence.",
            ["inline"]),
        new(
            "target.ts: synthetic resolution, state-key derivation, and remaining uncalled exports",
            TypeScriptDisposition.Obsolete,
            "H2 H3 H5 P6",
            nameof(ActionHostAuthorizationRouteTests.ApprovedRoutesMintOneFrozenInvocation),
            "The sole C# route owns authorization, policy, snapshot, state scope, and orchestration without a compatibility export.",
            ["inline"]),
        new(
            "inline-comments.test.ts: disabled mode and severity or confidence thresholds",
            TypeScriptDisposition.Superseded,
            "H3 plus P3",
            nameof(InlineCandidateMapperTests.StickyAndSeverityPoliciesExcludeWithoutNewReasons),
            "R4 trusts sticky or sticky-and-inline with high or critical severity and has no confidence gate or disabled reason.",
            ["inline"]),
        new(
            "inline-comments.test.ts: current-side commentable line and deterministic target order",
            TypeScriptDisposition.Retained,
            "H5 plus P3",
            nameof(InlineCandidateMapperTests.ExactCurrentPathAdditionAndContextAreTheOnlyTargets),
            "P3 consumes exact H5 coordinates and never parses raw patch text or relocates a finding.",
            ["inline"]),
        new(
            "inline-comments.test.ts: state and range JSON inline key",
            TypeScriptDisposition.Superseded,
            "P1 plus P3",
            nameof(InlineKeyMatchesIndependentStrictUtf8Golden),
            "R4 frames the P1 fingerprint, exact path, and selected single line without a state key or discarded range.",
            ["inline"]),
        new(
            "inline-comments.test.ts: marker grammar and bounded public-safe body",
            TypeScriptDisposition.Superseded,
            "P3 plus P4",
            nameof(InlineCommentPublisherTests.SerializerFreezesHeadCoordinatesAndEscapesCanaries),
            "R4 uses its closed marker and one selected coordinate rather than adopting the M4 marker or range notice.",
            ["inline"]),
        new(
            "inline-comments.test.ts: configurable cap and post-duplicate refill",
            TypeScriptDisposition.Superseded,
            "P3 then P4",
            nameof(InlineCandidateMapperTests.MappingPrecedesFixedCapAndReasonCountsAreStable),
            "P3 applies the product-owned cap of five before P4 duplicate handling and never refills from an unbounded tail.",
            ["inline"]),
        new(
            "inline-comments.test.ts: complete enumeration and exact duplicate suppression",
            TypeScriptDisposition.Retained,
            "P4",
            nameof(InlineCommentPublisherTests.CompleteMultiPageInitialListSuppressesExactIdentity),
            "Incomplete evidence closes publication and later exact readback makes retries idempotent.",
            ["inline"]),
        new(
            "inline-comments.test.ts: one batch attempt and complete relist",
            TypeScriptDisposition.Retained,
            "P4",
            nameof(InlineCommentPublisherTests.SuccessfulBatchRequiresCompleteExactRelist),
            "R4 never assumes batch success or ambiguity and reconciles only exact current identities.",
            ["inline"]),
        new(
            "inline-comments.test.ts: individual fallback after batch failure",
            TypeScriptDisposition.Superseded,
            "P4",
            nameof(InlineCommentPublisherTests.AllNonPositiveBatchOutcomesHaveZeroIndividualFanout),
            "Only the exact closed 422 known-not-written shape permits fallback; 5xx and every ambiguous outcome have zero fan-out.",
            ["inline-warning"]),
        new(
            "inline-comments.test.ts: individual create readback cancellation and retry",
            TypeScriptDisposition.Retained,
            "P4",
            nameof(InlineCommentPublisherTests.FallbackCapsAtFiveAndUnknownOrBadReadbackStopsFanout),
            "Every successful create needs exact readback and ambiguous evidence stops further writes.",
            ["inline-warning"]),
        new(
            "inline-comments.test.ts: changed-head barriers before batch and fallback",
            TypeScriptDisposition.Retained,
            "H5 plus P4",
            nameof(InlineCommentPublisherTests.ChangedHeadAtFallbackBarrierClosesBeforeIndividuals),
            "Changed, ineligible, or unavailable head evidence authorizes no write.",
            ["stale-head"]),
        new(
            "inline-comments.test.ts: historical 3000-file and 3000-comment limits",
            TypeScriptDisposition.Superseded,
            "H5 plus P4",
            nameof(ReviewedChangedFileReaderTests.CapPlusOneReturnsNoPrefix),
            "R4 uses the 200-file product cap and shared fail-closed request, page, record, time, and byte budgets.",
            ["inline"]),
        new(
            "inline-comments.test.ts: inline failure preserves accepted sticky and state",
            TypeScriptDisposition.Retained,
            "P6 plus E1",
            nameof(ActionHostCompositionTests.ProductionInlineWarningPreservesAcceptedStickyAndState),
            "Optional inline work can only produce a post-acceptance warning and cannot change the committed review.",
            ["inline-warning"]),
        new(
            "inline-comments.ts: metadata reason DTOs and remaining uncalled helpers",
            TypeScriptDisposition.Obsolete,
            "P3 P4 P6",
            nameof(ActionHostCoordinatorTests.HostOutcomeEnumsRemainClosed),
            "Closed C# contracts own bounded reasons and outcomes without a TypeScript tombstone surface.",
            ["inline-warning"]),
    ];

    [Fact]
    public async Task InlineKeyMatchesIndependentStrictUtf8Golden()
    {
        var source = InlineCandidateTestData.AdditionSource("src/app.ts", 7);
        var coordinates = InlineCandidateTestData.Coordinates(source);
        var identity = new R4FindingIdentityV1(
            InlineCandidateTestData.Finding(
                title: "Injected fingerprint must be used directly",
                evidence: [InlineCandidateTestData.Evidence(
                    'a', source.Path, 7, 9)]),
            GoldenFingerprint);
        var sameSelectedTargetWithDifferentRange = identity with
        {
            Finding = identity.Finding with
            {
                Evidence = [InlineCandidateTestData.Evidence(
                    'b', source.Path, 7, 12)],
            },
        };
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(
            policy,
            [identity],
            coordinates);
        var changedRange = InlineCandidateMapper.Map(
            policy,
            [sameSelectedTargetWithDifferentRange],
            coordinates);
        var preimage = R4CanonicalUtf8Framing.BuildPreimage(
            InlineCandidateMapper.InlineKeyDomain,
            [GoldenFingerprint, source.Path, "7"]);

        Assert.Equal(133, preimage.Length);
        Assert.Equal(GoldenPreimageHex, Convert.ToHexStringLower(preimage));
        Assert.Equal(GoldenInlineKey, Assert.Single(mapped.Candidates).InlineKey);
        Assert.Equal(
            GoldenInlineKey,
            Assert.Single(changedRange.Candidates).InlineKey);
        Assert.NotEqual(
            GoldenFingerprint,
            R4PublicationIdentityV1.ComputeFindingFingerprint(identity.Finding));
    }

    [Fact]
    public async Task ExactUnicodePathBytesAreNotNormalized()
    {
        const string composed = "src/caf\u00e9.cs";
        const string decomposed = "src/cafe\u0301.cs";
        var coordinates = InlineCandidateTestData.Coordinates(
            InlineCandidateTestData.AdditionSource(composed, 7),
            InlineCandidateTestData.AdditionSource(decomposed, 7));
        var findings = InlineCandidateTestData.Ordered(
            InlineCandidateTestData.Finding(
                title: "Composed",
                evidence: [InlineCandidateTestData.Evidence(
                    'a', composed, 7, 7)]),
            InlineCandidateTestData.Finding(
                title: "Decomposed",
                evidence: [InlineCandidateTestData.Evidence(
                    'b', decomposed, 7, 7)]));
        var policy = await InlineCandidateTestData.Policy(
            "sticky_and_inline",
            "high");

        var mapped = InlineCandidateMapper.Map(policy, findings, coordinates);

        Assert.Equal(2, mapped.Candidates.Length);
        Assert.Contains(mapped.Candidates, item => item.Path == composed);
        Assert.Contains(mapped.Candidates, item => item.Path == decomposed);
        Assert.Equal(
            2,
            mapped.Candidates.Select(item => item.InlineKey)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [Fact]
    public void TypeScriptAssertionsHaveExplicitR4Dispositions()
    {
        Assert.Equal(19, ReplacementLedger.Length);
        Assert.Equal(
            ReplacementLedger.Length,
            ReplacementLedger.Select(item => item.TypeScriptAssertionGroup)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(ReplacementLedger, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.R4Owner));
            Assert.False(string.IsNullOrWhiteSpace(item.ReplacementTest));
            Assert.False(string.IsNullOrWhiteSpace(item.DeliberateDifference));
            Assert.NotEmpty(item.FrameworkScenarioIds);
        });
        Assert.Contains(TypeScriptDisposition.Retained,
            ReplacementLedger.Select(item => item.Disposition));
        Assert.Contains(TypeScriptDisposition.Superseded,
            ReplacementLedger.Select(item => item.Disposition));
        Assert.Contains(TypeScriptDisposition.Obsolete,
            ReplacementLedger.Select(item => item.Disposition));
        Assert.Equal(new[] { "inline", "inline-warning", "stale-head" },
            ReplacementLedger.SelectMany(item => item.FrameworkScenarioIds)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }
}
