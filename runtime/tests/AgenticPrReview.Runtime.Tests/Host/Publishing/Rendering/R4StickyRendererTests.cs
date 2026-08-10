using System.Collections.Immutable;
using System.Globalization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

public sealed class R4StickyRendererTests
{
    [Fact]
    public void NonEmptyReviewFreezesExactMarkdownAndBodyDigest()
    {
        var rendered = R4PublicationTestData.Render(
            findings: [R4PublicationTestData.Finding()]);
        const string expected =
            "## Agentic PR Review\n\n" +
            "Reviewed head: `cccccccccccccccccccccccccccccccccccccccc`\n\n" +
            "### Summary\n\n" +
            "Review complete\n\n" +
            "### Findings\n\n" +
            "#### HIGH: Title\n\n" +
            "Message\n\n" +
            "- Fingerprint: `43390faa4068833717f38a47230155790bed811f1e00a3d3caeef6d8b682e1fc`\n" +
            "- Evidence:\n" +
            "  - src/app&#x2E;ts (lines 7-9; observation `aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`)";

        Assert.Equal(expected, rendered.Body);
        Assert.Equal(
            "a7b785114c25c4b5103905baa28040d4017d880c3e005f2ead9312ddca2af4a2",
            rendered.Identity.BodySha256);
        Assert.Equal(1, rendered.RenderedFindingCount);
        Assert.Equal(0, rendered.OmittedFindingCount);
        Assert.Single(rendered.OrderedFindings);
    }

    [Fact]
    public void InputPermutationAndCultureDoNotChangeOutput()
    {
        var findings = CreateDistinctFindings(4, 20);
        var first = R4PublicationTestData.Render(findings: findings);
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar-SA");
            var reversed = R4PublicationTestData.Render(
                findings: findings.Reverse().ToImmutableArray());

            Assert.Equal(first.Comment, reversed.Comment);
            Assert.Equal(
                first.OrderedFindings.Select(item => item.FingerprintSha256),
                reversed.OrderedFindings.Select(item => item.FingerprintSha256));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Theory]
    [InlineData("summary")]
    [InlineData("title")]
    [InlineData("message")]
    public void EveryFreeTextRenderContextKeepsTheFullCanaryCorpusInert(
        string field)
    {
        const string canary =
            "line\n@octocat @org/team\n<!--script-->\n::warning::boom\n# heading\n" +
            "[link](url)\n```code```\n\0\t\r\u0085\u202e";
        var summary = field == "summary" ? canary : "Summary";
        var title = field == "title" ? canary : "Title";
        var message = field == "message" ? canary : "Message";

        var rendered = R4PublicationTestData.Render(
            summary,
            [R4PublicationTestData.Finding(title: title, message: message)]);

        AssertInert(rendered);
        Assert.Contains(
            "&lt;&#x21;&#x2D;&#x2D;script&#x2D;&#x2D;&#x3E;",
            rendered.Body);
        Assert.Contains("&#x3A;&#x3A;warning&#x3A;&#x3A;boom", rendered.Body);
        Assert.Contains("U+0000", rendered.Body);
        Assert.Contains("U+0009", rendered.Body);
        Assert.Contains("U+000D", rendered.Body);
        Assert.Contains("U+0085", rendered.Body);
        Assert.Contains("U+202E", rendered.Body);
        Assert.Contains("U+0040octocat U+0040org/team", rendered.Body);
        Assert.DoesNotContain("&#x0;", rendered.Body);
        Assert.DoesNotContain("&#x9;", rendered.Body);
        Assert.DoesNotContain("&#xD;", rendered.Body);
        Assert.DoesNotContain("&#x85;", rendered.Body);
        Assert.DoesNotContain("&#x202E;", rendered.Body);
    }

    [Fact]
    public void EvidencePathsStayInertInRepeatedListPositions()
    {
        const string path = "src/@octocat/`[link](target)!_name.md";
        var finding = R4PublicationTestData.Finding(
            evidence:
            [
                R4PublicationTestData.Evidence('a', path, 1, 1),
                R4PublicationTestData.Evidence('b', path, 2, 3),
            ]);

        var rendered = R4PublicationTestData.Render(findings: [finding]);

        AssertInert(rendered);
        Assert.DoesNotContain(path, rendered.Body, StringComparison.Ordinal);
        Assert.Equal(
            2,
            Count(
                rendered.Body,
                "src/U+0040octocat/&#x60;&#x5B;link&#x5D;"));
    }

    [Fact]
    public void InvalidUnicodeFailsBeforeHashingOrRendering()
    {
        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationTestData.Render(
                summary: "summary " + new string('\ud800', 1)));

        Assert.Equal(R4PublicationFailureCodes.ReviewInvalid, failure.Code);
    }

    [Fact]
    public void ProductionTruncationKeepsCompleteProjectionAndWholeBlocks()
    {
        var findings = CreateDistinctFindings(4, 16_000);

        var rendered = R4PublicationTestData.Render(findings: findings);

        Assert.Equal(4, rendered.OrderedFindings.Length);
        Assert.InRange(rendered.RenderedFindingCount, 1, 3);
        Assert.Equal(
            4 - rendered.RenderedFindingCount,
            rendered.OmittedFindingCount);
        Assert.Contains(
            R4StickyRenderer.TruncationNoticePrefix +
                rendered.OmittedFindingCount.ToString(CultureInfo.InvariantCulture) +
                R4StickyRenderer.TruncationNoticeSuffix,
            rendered.Body);
        for (var index = 0; index < rendered.OrderedFindings.Length; index++)
        {
            var fingerprint = rendered.OrderedFindings[index].FingerprintSha256;
            if (index < rendered.RenderedFindingCount)
            {
                Assert.Contains(fingerprint, rendered.Body);
            }
            else
            {
                Assert.DoesNotContain(fingerprint, rendered.Body);
            }
        }

        AssertValidAndBounded(rendered);
    }

    [Fact]
    public void OmittedCountUsesActualInvariantDigitWidthAcrossNineAndTen()
    {
        var block = new string('x', 100);
        var tenBlocks = Enumerable.Repeat(block, 10).ToArray();
        var elevenBlocks = Enumerable.Repeat(block, 11).ToArray();
        var oneDigitBody = ExpectedPlannedBody("fixed", block, 9);
        var twoDigitBody = ExpectedPlannedBody("fixed", block, 10);

        var oneDigit = R4StickyBodyPlanner.Select(
            "fixed",
            tenBlocks,
            oneDigitBody.Length + R4StickyMarker.MarkerLength + 2,
            oneDigitBody.Length + R4StickyMarker.MarkerLength + 2);
        var twoDigits = R4StickyBodyPlanner.Select(
            "fixed",
            elevenBlocks,
            twoDigitBody.Length + R4StickyMarker.MarkerLength + 2,
            twoDigitBody.Length + R4StickyMarker.MarkerLength + 2);

        Assert.Equal(1, oneDigit.RenderedFindingCount);
        Assert.Equal(9, oneDigit.OmittedFindingCount);
        Assert.Equal(oneDigitBody, oneDigit.Body);
        Assert.Equal(1, twoDigits.RenderedFindingCount);
        Assert.Equal(10, twoDigits.OmittedFindingCount);
        Assert.Equal(twoDigitBody, twoDigits.Body);
    }

    [Fact]
    public void FixedBodyAndRequiredNoticeOverflowFailWithoutPartialText()
    {
        var fixedFailure = Assert.Throws<R4PublicationException>(() =>
            R4StickyBodyPlanner.Select("fixed", [], 243, 243));
        var noticeFailure = Assert.Throws<R4PublicationException>(() =>
            R4StickyBodyPlanner.Select(
                "f",
                [new string('x', 100)],
                240,
                240));

        Assert.Equal(R4PublicationFailureCodes.BodyOverflow, fixedFailure.Code);
        Assert.Equal(R4PublicationFailureCodes.BodyOverflow, noticeFailure.Code);
    }

    [Fact]
    public void ExactFiftyThousandScalarBoundaryIsAccepted()
    {
        var baselineFindings = CreateFindingsWithTotalMessageCharacters(4);
        var baseline = R4PublicationTestData.Render(findings: baselineFindings);
        var targetMessageCharacters =
            4 + R4PublicationBudget.MaximumScalars - baseline.Comment.Length;
        var exact = R4PublicationTestData.Render(
            findings: CreateFindingsWithTotalMessageCharacters(
                targetMessageCharacters));

        Assert.Equal(4, exact.RenderedFindingCount);
        Assert.Equal(0, exact.OmittedFindingCount);
        Assert.True(R4Markdown.TryMeasure(
            exact.Comment,
            out var scalars,
            out var bytes));
        Assert.Equal(R4PublicationBudget.MaximumScalars, scalars);
        Assert.InRange(bytes, 0, R4PublicationBudget.MaximumUtf8Bytes);

        var over = R4PublicationTestData.Render(
            findings: CreateFindingsWithTotalMessageCharacters(
                targetMessageCharacters + 1));
        Assert.True(over.OmittedFindingCount > 0);
        AssertValidAndBounded(over);
    }

    [Fact]
    public void Utf8BudgetCountsFourByteScalarsIndependently()
    {
        Assert.True(R4PublicationBudget.Fits("😀😀", 2, 8));
        Assert.False(R4PublicationBudget.Fits("😀😀", 2, 7));
        Assert.False(R4PublicationBudget.Fits("😀😀", 1, 8));
    }

    [Fact]
    public void TwentyFindingsAreAcceptedAndTwentyOneFailClosed()
    {
        var twenty = CreateDistinctFindings(20, 10);
        var rendered = R4PublicationTestData.Render(findings: twenty);

        Assert.Equal(20, rendered.OrderedFindings.Length);
        AssertValidAndBounded(rendered);

        var failure = Assert.Throws<R4PublicationException>(() =>
            R4PublicationTestData.Render(
                findings: CreateDistinctFindings(21, 10)));
        Assert.Equal(R4PublicationFailureCodes.ReviewInvalid, failure.Code);
    }

    private static ImmutableArray<AgentFinding> CreateDistinctFindings(
        int count,
        int messageLength)
    {
        var builder = ImmutableArray.CreateBuilder<AgentFinding>(count);
        for (var index = 0; index < count; index++)
        {
            builder.Add(R4PublicationTestData.Finding(
                (index % 4) switch
                {
                    0 => "critical",
                    1 => "high",
                    2 => "medium",
                    _ => "low",
                },
                "Finding " + index.ToString(CultureInfo.InvariantCulture),
                new string((char)('a' + index % 20), messageLength),
                [R4PublicationTestData.Evidence(
                    (char)('a' + index % 6),
                    "src/file" + index.ToString(CultureInfo.InvariantCulture) + ".cs",
                    index + 1,
                    index + 2)]));
        }

        return builder.ToImmutable();
    }

    private static ImmutableArray<AgentFinding>
        CreateFindingsWithTotalMessageCharacters(int totalCharacters)
    {
        const int count = 4;
        if (totalCharacters is < count or > count * 16_384)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCharacters));
        }

        var remaining = totalCharacters;
        var builder = ImmutableArray.CreateBuilder<AgentFinding>(count);
        for (var index = 0; index < count; index++)
        {
            var remainingSlots = count - index - 1;
            var length = Math.Min(16_384, remaining - remainingSlots);
            remaining -= length;
            builder.Add(R4PublicationTestData.Finding(
                "high",
                "Sized " + index.ToString(CultureInfo.InvariantCulture),
                new string((char)('m' + index), length),
                [R4PublicationTestData.Evidence(
                    (char)('a' + index),
                    "src/sized" + index.ToString(CultureInfo.InvariantCulture) + ".cs",
                    index + 1,
                    index + 1)]));
        }

        Assert.Equal(0, remaining);
        return builder.ToImmutable();
    }

    private static string ExpectedPlannedBody(
        string fixedBody,
        string block,
        int omitted) =>
        fixedBody + "\n\n" + block + "\n\n" +
        R4StickyRenderer.TruncationNoticePrefix +
        omitted.ToString(CultureInfo.InvariantCulture) +
        R4StickyRenderer.TruncationNoticeSuffix;

    private static void AssertInert(R4RenderedStickyComment rendered)
    {
        Assert.DoesNotContain("<!--script-->", rendered.Body);
        Assert.DoesNotContain("::warning::", rendered.Body);
        Assert.DoesNotContain("# heading", rendered.Body);
        Assert.DoesNotContain("[link](url)", rendered.Body);
        Assert.DoesNotContain("```code```", rendered.Body);
        Assert.DoesNotContain("@octocat", rendered.Body);
        Assert.DoesNotContain("@org/team", rendered.Body);
        Assert.DoesNotContain('\0', rendered.Body);
        Assert.DoesNotContain('\t', rendered.Body);
        Assert.DoesNotContain('\r', rendered.Body);
        Assert.DoesNotContain('\u0085', rendered.Body);
        Assert.DoesNotContain('\u202e', rendered.Body);
        Assert.Equal(1, Count(rendered.Comment, R4StickyMarker.LookingPrefix));
        AssertValidAndBounded(rendered);
    }

    private static void AssertValidAndBounded(R4RenderedStickyComment rendered)
    {
        Assert.DoesNotContain('\r', rendered.Comment);
        Assert.True(R4Markdown.TryMeasure(
            rendered.Comment,
            out var scalars,
            out var bytes));
        Assert.InRange(scalars, 0, R4PublicationBudget.MaximumScalars);
        Assert.InRange(bytes, 0, R4PublicationBudget.MaximumUtf8Bytes);
        Assert.Equal(
            R4StickyInspectionKind.ValidR4,
            R4StickyMarker.Inspect(rendered.Comment).Kind);
    }

    private static int Count(string value, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(
                   needle,
                   offset,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }

        return count;
    }
}
