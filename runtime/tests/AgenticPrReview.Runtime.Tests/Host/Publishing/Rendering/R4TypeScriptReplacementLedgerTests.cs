using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4TypeScriptReplacementLedgerTests
{
    private sealed record Replacement(
        string OldTest,
        string NewOwner,
        string ReplacementTest,
        string DeliberateDifference);

    private static readonly Replacement[] Ledger =
    [
        new(
            "comments.test.ts: uses a terminal M4 marker that is distinct from legacy lineage metadata",
            "P1",
            nameof(R4StickyMarkerTests.EmptyReviewFreezesExactBodyDigestMarkerAndPlacement),
            "R4 has one exact terminal marker; M4 remains historical and non-adoptable."),
        new(
            "comments.test.ts: uses generic markers",
            "P1",
            nameof(R4StickyMarkerTests.OrdinaryAndHistoricalCommentsAreNotR4Targets),
            "Generic lineage markers classify as no R4 marker and are not rendered."),
        new(
            "comments.test.ts: renders findings without path or line in the top-level comment body",
            "Agent/H5 plus P1",
            nameof(PathlessFindingsAreObsoleteForTheGroundedAgentShape),
            "Every current Agent finding has one or more validated evidence locations."),
        new(
            "comments.test.ts: caps the structured review before comment rendering instead of hiding artifact findings / comments.ts: capStructuredReviewForMarkdownLimit",
            "P1",
            nameof(R4StickyRendererTests.ProductionTruncationKeepsCompleteProjectionAndWholeBlocks),
            "Only whole public blocks truncate; P3 retains the complete ordered projection."),
        new(
            "comments.ts: HTML comment delimiter protection",
            "P1",
            nameof(R4StickyRendererTests.EveryFreeTextRenderContextKeepsTheFullCanaryCorpusInert),
            "Every untrusted render context also covers Markdown, workflow, control, and bidi canaries."),
        new(
            "structured.test.ts: validates model JSON and injects trusted action-owned metadata",
            "P1",
            nameof(R4PublicationIdentityTests.FindingFingerprintMatchesIndependentGoldenAndEvidenceOrderMatters),
            "P1 computes a full Host-owned 64-hex framed fingerprint and consumes no model fingerprint."),
        new(
            "structured.test.ts: generates stable action-owned fingerprints for equivalent normalized findings",
            "P1",
            nameof(R4PublicationIdentityTests.UnicodeNormalizationDoesNotParticipateInFindingIdentity),
            "The old 16-hex JSON fingerprint is deliberately incompatible with exact R4 scalar bytes."),
        new(
            "structured.test.ts: normalizes safe repo-relative finding paths / rejects unsafe non-relative finding paths",
            "Agent/H5 plus P1",
            nameof(R4StickyRendererTests.EvidencePathsStayInertInRepeatedListPositions),
            "P1 does not renormalize canonical paths; it owns their Markdown inertness."),
        new(
            "structured.test.ts: caps normalized findings and records truncation before rendering/artifacts",
            "Agent plus P1",
            nameof(R4StickyRendererTests.TwentyFindingsAreAcceptedAndTwentyOneFailClosed),
            "Agent owns admission at 20; P1 independently bounds complete public blocks."),
        new(
            "comments.test.ts: updates an incremental lineage comment / updates the existing M4 sticky comment",
            "P2/P5/P6",
            "deferred to GitHub discovery, recovery, and transaction leaves",
            "P1 is pure and performs no create, update, readback, lineage, or state operation."),
        new(
            "structured.test.ts: drops or keeps findings by current PR file membership",
            "Agent/H5 and P3",
            "retained grounding and later inline mapping tests",
            "P1 consumes validated grounded evidence and does not decide diff membership."),
    ];

    [Fact]
    public void NamedReplacementLedgerIsCompleteUniqueAndDeliberate()
    {
        Assert.Equal(11, Ledger.Length);
        Assert.Equal(
            Ledger.Length,
            Ledger.Select(item => item.OldTest).Distinct(StringComparer.Ordinal).Count());
        Assert.All(
            Ledger,
            item =>
            {
                Assert.False(string.IsNullOrWhiteSpace(item.NewOwner));
                Assert.False(string.IsNullOrWhiteSpace(item.ReplacementTest));
                Assert.False(string.IsNullOrWhiteSpace(item.DeliberateDifference));
            });
    }

    [Fact]
    public void PathlessFindingsAreObsoleteForTheGroundedAgentShape()
    {
        var pathless = R4PublicationTestData.Finding(evidence: []);

        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationTestData.Render(findings: [pathless]));

        Assert.Equal(R4PublicationFailureCodes.ReviewInvalid, failure.Code);
    }
}
