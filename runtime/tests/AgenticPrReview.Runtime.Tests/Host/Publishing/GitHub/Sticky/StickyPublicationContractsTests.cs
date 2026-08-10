using System.Reflection;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class StickyPublicationContractsTests
{
    [Fact]
    public async Task InvalidOrMismatchedP1ScopeCannotMintBoundRequest()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var invalid = data.Request.Scope with { RepositoryId = 0 };
        var mismatched = data.Request.Scope with
        {
            PullRequestNumber = data.Request.Scope.PullRequestNumber + 1,
        };

        Assert.False(AuthorizedStickyPublicationRequest.TryCreate(
            data.Request.Authorization, invalid, data.Rendered, out _));
        Assert.False(AuthorizedStickyPublicationRequest.TryCreate(
            data.Request.Authorization, mismatched, data.Rendered, out _));

        var oversizedBody = new string('x',
            R4PublicationBudget.MaximumUtf8Bytes + 1);
        var forged = data.Rendered with
        {
            Body = oversizedBody,
            Comment = oversizedBody + "\n\n" +
                R4StickyMarker.Create(data.Rendered.Identity),
        };
        Assert.False(AuthorizedStickyPublicationRequest.TryCreate(
            data.Request.Authorization, data.Request.Scope, forged, out _));
    }

    [Fact]
    public void BoundRequestReceiptAndLiveResultsCannotBeConstructed()
    {
        var requestType = typeof(AuthorizedStickyPublicationRequest);
        Assert.False(requestType.IsPublic);
        Assert.All(requestType.GetConstructors(BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic), constructor =>
            Assert.True(constructor.IsPrivate));
        Assert.All(requestType.GetProperties(BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic), property =>
            Assert.Null(property.SetMethod));
        foreach (var type in new[]
        {
            typeof(StickyCommentPublisher.StickyPublicationReceipt),
            typeof(StickyCommentPublisher.StickyPublicationResult),
            typeof(StickyCommentPublisher.StickyDiscoveryResult),
        })
        {
            Assert.False(type.IsPublic);
            Assert.All(type.GetConstructors(BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic), constructor =>
                Assert.True(constructor.IsPrivate));
        }

        var fake = new FakeIssuerEvidence();
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyPublicationReceipt.FromReadback(
                null!, StickyPublicationOperation.Create, fake));
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyPublicationResult.FromFailure(fake));
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyDiscoveryResult.FromEvidence(fake));
    }

    private sealed class FakeIssuerEvidence :
        IStickyPublicationIssuerEvidence;
}
