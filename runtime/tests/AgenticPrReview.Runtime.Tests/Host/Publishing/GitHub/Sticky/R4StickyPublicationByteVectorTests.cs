using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class R4StickyPublicationByteVectorTests
{
    [Theory]
    [MemberData(nameof(R4StickyPublicationByteVectors.Names),
        MemberType = typeof(R4StickyPublicationByteVectors))]
    public void P1RenderToP2RequestBytesAreFrozenAndRoundTripExactly(
        string name)
    {
        var (summary, findings) = R4StickyPublicationByteVectors.Get(name);
        var rendered = R4PublicationTestData.Render(summary, findings);

        Assert.True(StickyCommentSerializer.TrySerialize(rendered.Comment,
            out var first));
        Assert.True(StickyCommentSerializer.TrySerialize(rendered.Comment,
            out var second));
        var independent = JsonSerializer.SerializeToUtf8Bytes(new
        {
            body = rendered.Comment,
        });

        Assert.Equal(independent, first);
        Assert.Equal(first, second);
        using var document = JsonDocument.Parse(first!);
        Assert.Equal(rendered.Comment,
            document.RootElement.GetProperty("body").GetString());
        Assert.False(string.IsNullOrWhiteSpace(name));
    }

    [Theory]
    [MemberData(nameof(R4StickyPublicationByteVectors.AtLimitNames),
        MemberType = typeof(R4StickyPublicationByteVectors))]
    public void EveryP1AtLimitScalarFamilyIsExactlySendable(string name)
    {
        var body = R4StickyPublicationByteVectors.GetAtLimitBody(name);

        Assert.Equal(R4PublicationBudget.MaximumScalars,
            body.EnumerateRunes().Count());
        Assert.InRange(Encoding.UTF8.GetByteCount(body), 1,
            R4PublicationBudget.MaximumUtf8Bytes);
        Assert.True(StickyCommentSerializer.TrySerialize(body, out var bytes));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(new { body }), bytes);
        Assert.InRange(bytes!.Length, 1,
            AgenticPrReview.Runtime.Host.Publishing.GitHub.Common
                .BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes);
    }
}

internal static class R4StickyPublicationByteVectors
{
    public static TheoryData<string> Names => new(
        "ascii",
        "quotes-backslashes-controls",
        "bmp-and-non-bmp",
        "bidi-and-invisible",
        "near-p1-summary-bound");

    public static TheoryData<string> AtLimitNames => new(
        "at-limit-ascii",
        "at-limit-quote-backslash",
        "at-limit-html-sensitive",
        "at-limit-bmp",
        "at-limit-supplementary");

    internal static (string Summary, ImmutableArray<AgentFinding> Findings) Get(
        string name) => name switch
    {
        "ascii" => ("plain ASCII summary",
            [R4PublicationTestData.Finding()]),
        "quotes-backslashes-controls" =>
            ("quote=\" slash=\\ line\nnull=\0 tab=\t return=\r",
                [R4PublicationTestData.Finding(
                    message: "markdown `code` [link](target)")]),
        "bmp-and-non-bmp" => ("Latin é; Han 漢字; emoji 😀; music 𝄞",
            [R4PublicationTestData.Finding(title: "Unicode ✓")]),
        "bidi-and-invisible" =>
            ("bidi=\u202e isolate=\u2066 invisible=\u200b word=review",
                [R4PublicationTestData.Finding(message: "mentions @octocat")]),
        "near-p1-summary-bound" =>
            (new string('z', AgentLimits.SummaryBytes),
                [R4PublicationTestData.Finding(message:
                    new string('m', AgentLimits.FindingMessageBytes))]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    internal static string GetAtLimitBody(string name) => name switch
    {
        "at-limit-ascii" => new('a', R4PublicationBudget.MaximumScalars),
        "at-limit-quote-backslash" => string.Concat(
            Enumerable.Repeat("\"\\", R4PublicationBudget.MaximumScalars / 2)),
        "at-limit-html-sensitive" => string.Concat(
            Enumerable.Repeat("<>&", R4PublicationBudget.MaximumScalars / 3)) +
            "<>",
        "at-limit-bmp" => new('é', R4PublicationBudget.MaximumScalars),
        "at-limit-supplementary" => string.Concat(Enumerable.Repeat("😀",
            R4PublicationBudget.MaximumScalars)),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
