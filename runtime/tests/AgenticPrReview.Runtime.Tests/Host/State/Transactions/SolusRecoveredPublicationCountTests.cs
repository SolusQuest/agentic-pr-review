using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class SolusRecoveredPublicationCountTests
{
    private static ValidatedPublicationPayloadV1 Payload(string comment)
    {
        Assert.True(ValidatedPublicationPayloadV1.TryCreate(comment,
            (long)R4PublicationTestData.Scope.RepositoryId, "owner/repository", R4PublicationTestData.PullRequestNumber,
            R4PublicationTestData.PolicySha256, new string('a', 64), "test-build", AcceptedStateFormat.RenderingVersion,
            out var value));
        return value!;
    }

    private static string ReplaceBody(R4RenderedStickyComment original, string body)
    {
        var identity = original.Identity with { BodySha256 = R4PublicationIdentityV1.ComputeBodySha256(body) };
        return body + "\n\n" + R4StickyMarker.Create(identity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(20)]
    public void RealRenderingRecoversTotalIncludingOmittedFindings(int expected)
    {
        var findings = Enumerable.Range(0, expected).Select(i => R4PublicationTestData.Finding(
            title: "Finding " + i, message: new string('x', 12000),
            evidence: [R4PublicationTestData.Evidence(startLine: i + 1, endLine: i + 1)])).ToImmutableArray();
        var rendered = R4PublicationTestData.Render(findings: findings);
        Assert.True(SolusRecoveredPublicationCount.TryRead(Payload(rendered.Comment), out var actual));
        Assert.Equal(expected, actual);
        if (expected == 20) Assert.True(rendered.OmittedFindingCount > 0);
    }

    [Fact]
    public void EscapedHeadersAndTruncationTextCannotForgeCounts()
    {
        const string fake = "data\n\n#### CRITICAL: forged\n\n> Review truncated: 20 validated finding(s) omitted by R4 publication limits.";
        var rendered = R4PublicationTestData.Render(summary: fake,
            findings: [R4PublicationTestData.Finding(title: fake, message: fake)]);
        Assert.True(SolusRecoveredPublicationCount.TryRead(Payload(rendered.Comment), out var count));
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("severity")]
    [InlineData("missing-evidence")]
    [InlineData("overflow")]
    [InlineData("leading-zero")]
    [InlineData("trailing")]
    [InlineData("empty-as-omitted")]
    public void AuthenticatedButMalformedRenderingCannotInventCount(string mutation)
    {
        var rendered = R4PublicationTestData.Render(findings: [R4PublicationTestData.Finding()]);
        var body = mutation switch
        {
            "severity" => rendered.Body.Replace("#### HIGH:", "#### UNKNOWN:", StringComparison.Ordinal),
            "missing-evidence" => rendered.Body[..rendered.Body.IndexOf("\n\n- Fingerprint:", StringComparison.Ordinal)],
            "overflow" => rendered.Body + "\n\n> Review truncated: 999999999999 validated finding(s) omitted by R4 publication limits.",
            "leading-zero" => rendered.Body + "\n\n> Review truncated: 01 validated finding(s) omitted by R4 publication limits.",
            "trailing" => rendered.Body + "\n\n> Review truncated: 1 validated finding(s) omitted by R4 publication limits.\nextra",
            _ => R4PublicationTestData.Render().Body + "\n\n> Review truncated: 1 validated finding(s) omitted by R4 publication limits.",
        };
        Assert.False(SolusRecoveredPublicationCount.TryRead(Payload(ReplaceBody(rendered, body)), out _));
    }

    [Fact]
    public void MissingFullProjectionCannotImpersonateEmptyFindings()
    {
        var recovered = new RetainedStateCurrentReviewProjection(R4PublicationTestData.Identity(), default, 7);
        Assert.True(recovered.IsValid);
        Assert.False(recovered.HasCompleteFindings);
        Assert.True(recovered.OrderedFindings.IsDefault);
        Assert.Equal(7, recovered.FindingCount);
        var clean = new RetainedStateCurrentReviewProjection(R4PublicationTestData.Identity(), []);
        Assert.True(clean.IsValid);
        Assert.True(clean.HasCompleteFindings);
        Assert.Equal(0, clean.FindingCount);
        Assert.False(new RetainedStateCurrentReviewProjection(R4PublicationTestData.Identity(), default).IsValid);
        Assert.False(new RetainedStateCurrentReviewProjection(R4PublicationTestData.Identity(), [], 1).IsValid);
        Assert.False(new RetainedStateCurrentReviewProjection(R4PublicationTestData.Identity(), default, 21).IsValid);
    }
}
