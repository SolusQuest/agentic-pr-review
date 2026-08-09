using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4StickyMarkerTests
{
    private const string ExpectedBody =
        "## Agentic PR Review\n\n" +
        "Reviewed head: `cccccccccccccccccccccccccccccccccccccccc`\n\n" +
        "### Summary\n\n" +
        "Review complete\n\n" +
        "### Findings\n\n" +
        "No actionable findings.";

    [Fact]
    public void EmptyReviewFreezesExactBodyDigestMarkerAndPlacement()
    {
        var rendered = R4PublicationTestData.Render();
        const string expectedMarker =
            "<!-- agentic-pr-review:r4:v1 " +
            "scope_sha256=5378fccb420ffa06f6510e5050bb64db7d0751cdab77581d306925c400bcdab4 " +
            "body_sha256=461041e2398648aa84d5c13687bd077b7fa9d2accd0369ca120b8551d39ab333 " +
            "head_sha=cccccccccccccccccccccccccccccccccccccccc -->";

        Assert.Equal(ExpectedBody, rendered.Body);
        Assert.Equal(
            "461041e2398648aa84d5c13687bd077b7fa9d2accd0369ca120b8551d39ab333",
            rendered.Identity.BodySha256);
        Assert.Equal(R4StickyMarker.MarkerLength, expectedMarker.Length);
        Assert.Equal(237, expectedMarker.Length);
        Assert.Equal(ExpectedBody + "\n\n" + expectedMarker, rendered.Comment);
        Assert.False(rendered.Comment.EndsWith('\n'));
    }

    [Fact]
    public void ExactCommentInspectsAndRecomputesBodyDigest()
    {
        var rendered = R4PublicationTestData.Render();

        var inspected = R4StickyMarker.Inspect(rendered.Comment);

        Assert.Equal(R4StickyInspectionKind.ValidR4, inspected.Kind);
        Assert.Equal(rendered.Body, inspected.Body);
        Assert.Equal(rendered.Identity, inspected.Identity);
        Assert.Null(inspected.InvalidReason);
    }

    [Theory]
    [InlineData("ordinary comment")]
    [InlineData("ordinary\r\ncomment")]
    [InlineData("<!-- agentic-pr-review:v1 -->")]
    [InlineData("<!-- agentic-pr-review:m4:v1 -->")]
    public void OrdinaryAndHistoricalCommentsAreNotR4Targets(string comment)
    {
        var inspected = R4StickyMarker.Inspect(comment);

        Assert.Equal(R4StickyInspectionKind.NoR4Marker, inspected.Kind);
    }

    [Fact]
    public void HistoricalMarkerCanRemainInsideOneValidR4Body()
    {
        const string body = "Historical <!-- agentic-pr-review:v1 -->";
        var identity = new R4PublicationIdentityV1(
            new string('a', 64),
            R4PublicationIdentityV1.ComputeBodySha256(body),
            new string('b', 40));
        var comment = body + "\n\n" + R4StickyMarker.Create(identity);

        var inspected = R4StickyMarker.Inspect(comment);

        Assert.Equal(R4StickyInspectionKind.ValidR4, inspected.Kind);
        Assert.Equal(body, inspected.Body);
    }

    [Fact]
    public void DuplicateAndValidPlusMalformedR4MarkersFailClosed()
    {
        var rendered = R4PublicationTestData.Render();
        var marker = rendered.Comment[(rendered.Body.Length + 2)..];

        AssertInvalid(
            rendered.Comment + "\n\n" + marker,
            R4StickyInvalidReason.Duplicate);
        AssertInvalid(
            rendered.Comment + "\n" + R4StickyMarker.LookingPrefix + "v1 -->",
            R4StickyInvalidReason.Duplicate);
    }

    [Fact]
    public void VersionCaseExtraFieldAndMalformedGrammarFailClosed()
    {
        var comment = R4PublicationTestData.Render().Comment;

        AssertInvalid(
            comment.Replace("r4:v1", "r4:v2", StringComparison.Ordinal),
            R4StickyInvalidReason.WrongVersion);
        AssertInvalid(
            comment.Replace("scope_sha256", "Scope_sha256", StringComparison.Ordinal),
            R4StickyInvalidReason.WrongCase);
        AssertInvalid(
            comment.Replace(
                "scope_sha256=5",
                "scope_sha256=F",
                StringComparison.Ordinal),
            R4StickyInvalidReason.WrongCase);
        AssertInvalid(
            comment.Replace(" -->", " extra=x -->", StringComparison.Ordinal),
            R4StickyInvalidReason.ExtraField);
        AssertInvalid(
            "body\n\n<!-- agentic-pr-review:r4:v1 -->",
            R4StickyInvalidReason.Malformed);
    }

    [Fact]
    public void SeparatorTerminalBytesUnicodeLfAndDigestMismatchFailClosed()
    {
        var comment = R4PublicationTestData.Render().Comment;

        AssertInvalid(
            comment.Replace("\n\n<!--", "\n<!--", StringComparison.Ordinal),
            R4StickyInvalidReason.Separator);
        AssertInvalid(comment + "x", R4StickyInvalidReason.TrailingBytes);
        AssertInvalid(
            "body\n\n<!-- agentic-pr-review:r4:v1 scope_sha256=bad -->tail",
            R4StickyInvalidReason.NonTerminal);
        AssertInvalid(
            comment.Replace("Review complete", "Review\rcomplete", StringComparison.Ordinal),
            R4StickyInvalidReason.InvalidLf);
        AssertInvalid(
            comment.Replace(
                "Review complete",
                "Review " + new string('\ud800', 1),
                StringComparison.Ordinal),
            R4StickyInvalidReason.InvalidUnicode);
        AssertInvalid(
            comment.Replace("Review complete", "Review changed", StringComparison.Ordinal),
            R4StickyInvalidReason.BodyDigestMismatch);
    }

    private static void AssertInvalid(
        string comment,
        R4StickyInvalidReason reason)
    {
        var inspected = R4StickyMarker.Inspect(comment);

        Assert.Equal(R4StickyInspectionKind.InvalidR4, inspected.Kind);
        Assert.Equal(reason, inspected.InvalidReason);
        Assert.Null(inspected.Body);
        Assert.Null(inspected.Identity);
    }
}
