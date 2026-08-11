using System.Reflection;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class StickyPublicationContractsTests
{
    [Fact]
    public async Task OnlyValidatedP1CapabilityCanMintBoundRequest()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        var mismatched = data.Request.Scope with
        {
            PullRequestNumber = data.Request.Scope.PullRequestNumber + 1,
        };
        var pullRequest = data.Request.Authorization.PullRequest;
        var mismatchedIdentity = new AgenticPrReview.Runtime.Agent.Core
            .ReviewedIdentity(
                pullRequest.RepositoryId.ToString(
                    System.Globalization.CultureInfo.InvariantCulture),
                checked((long)mismatched.PullRequestNumber),
                pullRequest.BaseSha, pullRequest.HeadSha);
        var mismatchedReview = R4PublicationTestData.Validated(
            identity: mismatchedIdentity, scope: mismatched);

        Assert.False(AuthorizedStickyPublicationRequest.TryCreate(
            data.Request.Authorization, null, out _));
        Assert.False(AuthorizedStickyPublicationRequest.TryCreate(
            data.Request.Authorization, mismatchedReview, out _));

        var forgedBody = "## forged\n\nself-consistent output";
        var forgedIdentity = data.Rendered.Identity with
        {
            BodySha256 = R4PublicationIdentityV1.ComputeBodySha256(
                forgedBody),
        };
        var forged = data.Rendered with
        {
            Body = forgedBody,
            Comment = forgedBody + "\n\n" +
                R4StickyMarker.Create(forgedIdentity),
            Identity = forgedIdentity,
        };
        var oversizedBody = new string('x',
            R4PublicationBudget.MaximumUtf8Bytes + 1);
        var oversizedIdentity = data.Rendered.Identity with
        {
            BodySha256 = R4PublicationIdentityV1.ComputeBodySha256(
                oversizedBody),
        };
        var oversized = data.Rendered with
        {
            Body = oversizedBody,
            Comment = oversizedBody + "\n\n" +
                R4StickyMarker.Create(oversizedIdentity),
            Identity = oversizedIdentity,
        };

        Assert.Equal(R4StickyInspectionKind.ValidR4,
            R4StickyMarker.Inspect(forged.Comment).Kind);
        Assert.Equal(R4StickyInspectionKind.ValidR4,
            R4StickyMarker.Inspect(oversized.Comment).Kind);
        var factory = Assert.Single(typeof(AuthorizedStickyPublicationRequest)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic),
            method => method.Name == "TryCreate");
        Assert.Contains(typeof(R4ValidatedPublicationReview),
            factory.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.DoesNotContain(typeof(R4RenderedStickyComment),
            factory.GetParameters().Select(parameter => parameter.ParameterType));
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
            typeof(AuthorizedStickyReadbackRequest),
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
                (AuthorizedStickyPublicationRequest)null!,
                StickyPublicationOperation.Create, fake));
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyPublicationResult.FromFailure(fake));
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyDiscoveryResult.FromEvidence(fake));
    }

    [Fact]
    public void ReceiptRehydrationIsBoundedAndCannotMintLiveSuccess()
    {
        const string scope =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        const string body =
            "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
        const string head = "0123456789abcdef0123456789abcdef01234567";
        const string url =
            "https://github.com/SolusQuest/agentic-pr-review/pull/42#issuecomment-7";

        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(StickyPublicationOperation.Update, 123, 42, 7,
                url, scope, body, head, out var receipt));
        Assert.Equal(url, receipt!.CommentUrl);
        Assert.False(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(StickyPublicationOperation.Update, 123, 42, 7,
                url + "?unexpected=1", scope, body, head, out _));
        Assert.False(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(StickyPublicationOperation.Update, 123, 42, 7,
                url, scope.ToUpperInvariant(), body, head, out _));
        Assert.Throws<InvalidOperationException>(() =>
            StickyCommentPublisher.StickyPublicationResult.FromReadback(
                receipt, new FakeIssuerEvidence()));
    }

    [Fact]
    public async Task PersistedP1OrReceiptCanAuthorizeReadOnlyDiscovery()
    {
        var data = await StickyPublicationTestData.CreateAsync();
        Assert.True(AuthorizedStickyReadbackRequest.TryCreate(
            data.Request.Authorization, data.Request.Scope, data.Rendered,
            out var persisted));
        var comment = StickyPublicationTestData.Comment(7,
            data.Rendered.Comment);
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(StickyPublicationOperation.Observed,
                data.Request.Authorization.PullRequest.RepositoryId,
                data.Request.Authorization.PullRequest.Number, comment.Id,
                comment.HtmlUrl, data.Rendered.Identity.ScopeSha256,
                data.Rendered.Identity.BodySha256,
                data.Rendered.Identity.HeadSha, out var receipt));
        Assert.True(AuthorizedStickyReadbackRequest.TryCreate(
            data.Request.Authorization, data.Request.Scope, receipt,
            out var rehydrated));

        Assert.Equal(data.Rendered.Comment, persisted!.ExactComment);
        Assert.Null(rehydrated!.ExactComment);
        Assert.Equal(data.Rendered.Identity, rehydrated.ExpectedIdentity);
        var inputs = typeof(AuthorizedStickyReadbackRequest)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "TryCreate")
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.ParameterType)
            .ToArray();
        Assert.DoesNotContain(typeof(R4ValidatedPublicationReview), inputs);
        Assert.Contains(typeof(R4RenderedStickyComment), inputs);
        Assert.Contains(
            typeof(StickyCommentPublisher.StickyPublicationReceipt), inputs);
        using var transport = BoundedGitHubPublisherTransport.CreateReadback(
            data.Token.ExportForPrivateLaunch(), rehydrated);
        Assert.IsAssignableFrom<IStickyGitHubReadbackTransport>(transport);
        Assert.False(transport is IStickyGitHubPublisherTransport);
    }

    private sealed class FakeIssuerEvidence :
        IStickyPublicationIssuerEvidence;
}
