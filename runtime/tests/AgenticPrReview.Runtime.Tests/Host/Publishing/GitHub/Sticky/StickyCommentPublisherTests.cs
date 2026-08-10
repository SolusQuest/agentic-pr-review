using System.Text;
using System.Globalization;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;

public sealed class StickyCommentPublisherTests
{
    [Fact]
    public async Task ZeroCreatesAndExactResponseGetRelistProduceReceipt()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var comment = StickyPublicationTestData.Comment(7, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation = Success(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
            result.Outcome);
        Assert.Equal(1, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
        Assert.Equal(7, result.Receipt!.CommentId);
        Assert.Equal("[PRIVATE]", result.Receipt.ToString());
        Assert.Equal(rendered.Comment,
            JsonBody(factory.Transport.Bodies.Single()));
    }

    [Fact]
    public async Task OneUpdatesEvenWhenExistingBodyIsAlreadyExact()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync(empty: true);
        var comment = StickyPublicationTestData.Comment(8, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(comment);
        factory.Transport.Mutation = Success(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
            result.Outcome);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(1, factory.Transport.Updates);
        Assert.Equal(StickyPublicationOperation.Update,
            result.Receipt!.Operation);
    }

    [Fact]
    public async Task HistoricalAndForeignScopeCommentsAreNotAdopted()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var foreign = rendered.Comment.Replace(
            rendered.Identity.ScopeSha256, new string('a', 64),
            StringComparison.Ordinal);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(
            StickyPublicationTestData.Comment(1,
                "old <!-- agentic-pr-review:v1 -->"),
            StickyPublicationTestData.Comment(2, foreign),
            StickyPublicationTestData.Comment(3,
                "old\n<!-- agentic-pr-review:m4-state/v1 " +
                "{\"bodySha256\":\"" + new string('b', 64) +
                "\",\"markerId\":\"" + new string('c', 64) +
                "\",\"selectorRevision\":\"sha256:" +
                new string('d', 64) + "\"} -->"));
        var created = StickyPublicationTestData.Comment(4, rendered.Comment);
        factory.Transport.Mutation = Success(created);
        factory.Transport.Read = Success(created);
        factory.Transport.Enqueue(
            StickyPublicationTestData.Comment(2, foreign), created);
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
            result.Outcome);
        Assert.Equal(1, factory.Transport.Creates);
    }

    [Fact]
    public async Task MultipleOrMalformedR4TargetsFailBeforeWrite()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        foreach (var comments in new[]
        {
            new[] { StickyPublicationTestData.Comment(1, rendered.Comment),
                StickyPublicationTestData.Comment(2, rendered.Comment) },
            new[] { StickyPublicationTestData.Comment(1,
                rendered.Comment + " trailing") },
        })
        {
            var factory = new FakePublisherTransportFactory();
            factory.Transport.Enqueue(comments);
            var result = await new StickyCommentPublisher(factory).PublishAsync(
                token, request, CancellationToken.None);
            Assert.Equal(
                StickyPublicationOutcome.AuthorizationOrValidationFailure,
                result.Outcome);
            Assert.Null(result.Receipt);
            Assert.Equal(0, factory.Transport.Creates +
                factory.Transport.Updates);
        }
    }

    [Fact]
    public async Task LostCreateResponseReconcilesWithoutSecondWrite()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var comment = StickyPublicationTestData.Comment(9, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
            result.Outcome);
        Assert.Equal(1, factory.Transport.Creates);
        Assert.Equal(1, factory.Transport.Reads);
    }

    [Fact]
    public async Task UnresolvedMutationNeverRetriesAndHasNoReceipt()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Enqueue();
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyPublicationOutcome.OutcomeUnknown, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(1, factory.Transport.Creates);
    }

    [Fact]
    public async Task AmbiguousPostWriteRaceNeverCreatesTwice()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Enqueue(
            StickyPublicationTestData.Comment(11, rendered.Comment),
            StickyPublicationTestData.Comment(12, rendered.Comment));

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(StickyPublicationOutcome.OutcomeUnknown, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(1, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Fact]
    public async Task CallerCancellationAfterSendCannotInterruptExactReadback()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var comment = StickyPublicationTestData.Comment(13, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation = Success(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        using var cancellation = new CancellationTokenSource();
        factory.Transport.OnMutation = cancellation.Cancel;

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
            result.Outcome);
        Assert.All(factory.Transport.ReadCancellationTokens,
            tokenUsed => Assert.False(tokenUsed.CanBeCanceled));
        Assert.False(factory.Transport.ListCancellationTokens.Last()
            .CanBeCanceled);
    }

    [Fact]
    public async Task StaleBodyOrHeadUpdatesTheSingleScopedTarget()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var pullRequest = request.Authorization.PullRequest;
        var currentIdentity = new ReviewedIdentity(
            pullRequest.RepositoryId.ToString(CultureInfo.InvariantCulture),
            pullRequest.Number, pullRequest.BaseSha, pullRequest.HeadSha);
        var staleHeadIdentity = currentIdentity with
        {
            HeadSha = new string('e', 40),
        };
        var staleComments = new[]
        {
            R4PublicationTestData.Render("stale body",
                identity: currentIdentity, scope: request.Scope).Comment,
            R4PublicationTestData.Render(identity: staleHeadIdentity,
                scope: request.Scope).Comment,
        };

        foreach (var stale in staleComments)
        {
            var expected = StickyPublicationTestData.Comment(14,
                rendered.Comment);
            var factory = new FakePublisherTransportFactory();
            factory.Transport.Enqueue(
                StickyPublicationTestData.Comment(14, stale));
            factory.Transport.Mutation = Success(expected);
            factory.Transport.Read = Success(expected);
            factory.Transport.Enqueue(expected);

            var result = await new StickyCommentPublisher(factory).PublishAsync(
                token, request, CancellationToken.None);

            Assert.Equal(StickyPublicationOutcome.WrittenAndReadBack,
                result.Outcome);
            Assert.Equal(0, factory.Transport.Creates);
            Assert.Equal(1, factory.Transport.Updates);
        }
    }

    [Fact]
    public async Task DiscoveryAcceptsExactlyFiftyPagesAndFiveThousandRecords()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        for (var page = 1; page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            var firstId = (page - 1) * BoundedGitHubPublisherPolicy.PerPage + 1;
            factory.Transport.EnqueuePage(
                page == BoundedGitHubPublisherPolicy.MaximumPages
                    ? null
                    : page + 1,
                Enumerable.Range(firstId, BoundedGitHubPublisherPolicy.PerPage)
                    .Select(id => StickyPublicationTestData.Comment(id,
                        "historical comment"))
                    .ToArray());
        }

        var result = await new StickyCommentPublisher(factory).DiscoverAsync(
            token, request, CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.Absent, result.Kind);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumPages,
            factory.Transport.Lists);
    }

    [Fact]
    public async Task PageItemAndCompletenessCapPlusOneFailClosed()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var itemOverflow = new FakePublisherTransportFactory();
        for (var page = 1; page < BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            var firstId = (page - 1) * BoundedGitHubPublisherPolicy.PerPage + 1;
            itemOverflow.Transport.EnqueuePage(page + 1,
                Enumerable.Range(firstId, BoundedGitHubPublisherPolicy.PerPage)
                    .Select(id => StickyPublicationTestData.Comment(id,
                        "historical comment"))
                    .ToArray());
        }
        itemOverflow.Transport.EnqueuePage(null,
            Enumerable.Range(4_901, BoundedGitHubPublisherPolicy.PerPage + 1)
                .Select(id => StickyPublicationTestData.Comment(id,
                    "historical comment"))
                .ToArray());

        var records = await new StickyCommentPublisher(itemOverflow)
            .DiscoverAsync(token, request, CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.InvalidOrIncomplete, records.Kind);

        var pageOverflow = new FakePublisherTransportFactory();
        for (var page = 1; page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            pageOverflow.Transport.EnqueuePage(page + 1);
        }

        var pages = await new StickyCommentPublisher(pageOverflow)
            .DiscoverAsync(token, request, CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.InvalidOrIncomplete, pages.Kind);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumPages,
            pageOverflow.Transport.Lists);
    }

    [Fact]
    public async Task ReadOnlyExactDiscoveryReturnsObservedReceiptWithoutMutation()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var comment = StickyPublicationTestData.Comment(10, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        var result = await new StickyCommentPublisher(factory).DiscoverAsync(
            token, request, CancellationToken.None);
        Assert.Equal(StickyDiscoveryKind.ExactTarget, result.Kind);
        Assert.Equal(StickyPublicationOperation.Observed,
            result.Receipt!.Operation);
        Assert.Equal(0, factory.Transport.Creates + factory.Transport.Updates);
    }

    [Fact]
    public async Task CancellationBeforeDiscoveryCreatesNoTransport()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, cancelled.Token);
        Assert.Equal(StickyPublicationOutcome.CancelledBeforeSend,
            result.Outcome);
        Assert.Equal(0, factory.Creates);
    }

    [Theory]
    [InlineData((int)BoundedGitHubPublisherFailure.Unavailable,
        (int)StickyPublicationOutcome.KnownNotWritten)]
    [InlineData((int)BoundedGitHubPublisherFailure.CancelledBeforeSend,
        (int)StickyPublicationOutcome.CancelledBeforeSend)]
    [InlineData((int)
        BoundedGitHubPublisherFailure.AuthorizationOrValidationFailure,
        (int)StickyPublicationOutcome.AuthorizationOrValidationFailure)]
    public async Task PreSendMutationFailuresStayInTheirClosedOutcomePhase(
        int failureValue, int expectedValue)
    {
        var failure = (BoundedGitHubPublisherFailure)failureValue;
        var expected = (StickyPublicationOutcome)expectedValue;
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation =
            BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Failed(
                failure, failure ==
                    BoundedGitHubPublisherFailure.AuthorizationOrValidationFailure
                        ? BoundedGitHubPublisherReason.ValidationRejected
                        : BoundedGitHubPublisherReason.Deadline);

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(1, factory.Transport.Creates);
    }

    private static BoundedGitHubPublisherResult<BoundedGitHubIssueComment>
        Success(BoundedGitHubIssueComment comment) =>
        BoundedGitHubPublisherResult<BoundedGitHubIssueComment>.Success(comment);

    private static string JsonBody(byte[] bytes)
    {
        using var json = System.Text.Json.JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("body").GetString()!;
    }
}
