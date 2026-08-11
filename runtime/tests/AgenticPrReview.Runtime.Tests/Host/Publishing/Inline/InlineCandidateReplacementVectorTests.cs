using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
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

    private sealed record ReplacementVector(
        string RetainedTypeScriptVector,
        string R4Owner,
        string ReplacementTest,
        string DeliberateDifference);

    private static readonly ReplacementVector[] ReplacementLedger =
    [
        new(
            "inline-comments.test.ts: disabled mode produces no candidates",
            "H3 plus P3",
            nameof(InlineCandidateMapperTests.StickyAndSeverityPoliciesExcludeWithoutNewReasons),
            "R4 consumes trusted sticky mode and creates no disabled reason code."),
        new(
            "inline-comments.test.ts: severity and confidence thresholds",
            "H3 plus P3",
            nameof(InlineCandidateMapperTests.StickyAndSeverityPoliciesExcludeWithoutNewReasons),
            "R4 has only the trusted high/critical severity predicate and no confidence gate."),
        new(
            "inline-comments.test.ts: current-side patch target selection",
            "H5 plus P3",
            nameof(InlineCandidateMapperTests.ExactCurrentPathAdditionAndContextAreTheOnlyTargets),
            "R4 consumes exact H5 coordinates and never parses patch text or relocates."),
        new(
            "inline-comments.test.ts: state/range JSON inline key",
            "P1 plus P3",
            nameof(InlineKeyMatchesIndependentStrictUtf8Golden),
            "R4 frames only the P1 fingerprint, exact path, and selected single line."),
        new(
            "inline-comments.test.ts: configurable cap after candidate selection",
            "H3 plus P3",
            nameof(InlineCandidateMapperTests.MappingPrecedesFixedCapAndReasonCountsAreStable),
            "R4 freezes a product-owned cap of five and maps before cap classification."),
        new(
            "inline-comments.test.ts: refill cap after GitHub duplicate suppression",
            "P3 then P4",
            nameof(InlineCandidateMapperTests.DistinctFingerprintsAtOneLocationRemainDistinctCandidates),
            "P3 never reads GitHub or refills; P4 owns later duplicate handling."),
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
    public void RetainedTypeScriptVectorsHaveExplicitR4Replacements()
    {
        Assert.Equal(6, ReplacementLedger.Length);
        Assert.Equal(
            ReplacementLedger.Length,
            ReplacementLedger.Select(item => item.RetainedTypeScriptVector)
                .Distinct(StringComparer.Ordinal)
                .Count());
        Assert.All(ReplacementLedger, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.R4Owner));
            Assert.False(string.IsNullOrWhiteSpace(item.ReplacementTest));
            Assert.False(string.IsNullOrWhiteSpace(item.DeliberateDifference));
        });
    }
}
