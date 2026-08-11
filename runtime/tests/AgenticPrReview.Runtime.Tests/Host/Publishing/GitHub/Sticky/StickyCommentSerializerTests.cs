using System.Text;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class StickyCommentSerializerTests
{
    [Fact]
    public void SourceGeneratedSerializerFreezesDefaultEncoderBytes()
    {
        Assert.True(StickyCommentSerializer.TrySerialize(
            "quote=\" slash=\\ html=<>& bmp=é scalar=😀", out var bytes));
        Assert.Equal(
            "{\"body\":\"quote=\\u0022 slash=\\\\ html=\\u003C\\u003E\\u0026 " +
            "bmp=\\u00E9 scalar=\\uD83D\\uDE00\"}",
            Encoding.UTF8.GetString(bytes!));
    }

    [Fact]
    public void SerializedRequestAcceptsExactCapAndRejectsCapPlusOne()
    {
        const int framing = 11;
        Assert.True(StickyCommentSerializer.TrySerialize(
            new string('a',
                BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes - framing),
            out var exact));
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes,
            exact!.Length);
        Assert.False(StickyCommentSerializer.TrySerialize(
            new string('a',
                BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes -
                framing + 1), out _));
    }

    [Fact]
    public void SharedRequestCapsAreFrozenForP2AndP4()
    {
        Assert.Equal(1_048_576,
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes);
        Assert.Equal(512 * 1024,
            BoundedGitHubPublisherPolicy.MaximumInlineBatchRequestBytes);
        Assert.Equal(64 * 1024,
            BoundedGitHubPublisherPolicy.MaximumIndividualInlineRequestBytes);
        Assert.True(Enum.IsDefined(
            BoundedGitHubPublisherReason.BatchValidationRejected));
    }
}
