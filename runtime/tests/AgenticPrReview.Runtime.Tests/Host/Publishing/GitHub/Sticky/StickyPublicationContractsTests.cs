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
    }

    [Fact]
    public void BoundRequestAndReceiptArePrivateCapabilityShapes()
    {
        var requestType = typeof(AuthorizedStickyPublicationRequest);
        Assert.False(requestType.IsPublic);
        Assert.All(requestType.GetConstructors(BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic), constructor =>
            Assert.True(constructor.IsPrivate));
        Assert.All(requestType.GetProperties(BindingFlags.Instance |
            BindingFlags.Public | BindingFlags.NonPublic), property =>
            Assert.Null(property.SetMethod));
        Assert.False(typeof(StickyPublicationReceipt).IsPublic);

        var receipt = new StickyPublicationReceipt(
            StickyPublicationOperation.Create, 1, 2, 3,
            "https://github.com/example/repo/pull/2#issuecomment-3",
            new string('a', 64), new string('b', 64), new string('c', 40));
        var written = StickyPublicationResult.Written(receipt);
        var failed = StickyPublicationResult.Failed(
            StickyPublicationOutcome.OutcomeUnknown,
            StickyPublicationReason.ReconciliationIncomplete);

        Assert.Same(receipt, written.Receipt);
        Assert.Null(failed.Receipt);
        Assert.Equal("[PRIVATE]", receipt.ToString());
    }
}
