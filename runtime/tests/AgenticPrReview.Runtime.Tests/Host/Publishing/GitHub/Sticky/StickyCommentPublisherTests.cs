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
        Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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
        Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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
        Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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
                BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
                result.Outcome);
            Assert.Null(result.Receipt);
            Assert.Equal(0, factory.Transport.Creates +
                factory.Transport.Updates);
        }
    }

    [Fact]
    public async Task OversizedR4LookingCommentIsTotalAndFailsBeforeWrite()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var oversizedBody = new string('x',
            R4PublicationBudget.MaximumUtf8Bytes + 1);
        var oversized = oversizedBody + "\n\n" +
            R4StickyMarker.Create(rendered.Identity);
        Assert.InRange(Encoding.UTF8.GetByteCount(oversized),
            R4PublicationBudget.MaximumUtf8Bytes + 1,
            BoundedGitHubPublisherPolicy.MaximumResponseBytes);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(
            StickyPublicationTestData.Comment(15, oversized));

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
            result.Outcome);
        Assert.Equal(StickyPublicationReason.TargetConflict, result.Reason);
        Assert.Equal(0, factory.Transport.Creates +
            factory.Transport.Updates);
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
        Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
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

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
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
        Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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

            Assert.Equal(BoundedGitHubPublisherOutcome.WrittenAndReadBack,
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
            token, StickyPublicationTestData.Readback(request),
            CancellationToken.None);

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
            .DiscoverAsync(token, StickyPublicationTestData.Readback(request),
                CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.InvalidOrIncomplete, records.Kind);

        var pageOverflow = new FakePublisherTransportFactory();
        for (var page = 1; page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            pageOverflow.Transport.EnqueuePage(page + 1);
        }

        var pages = await new StickyCommentPublisher(pageOverflow)
            .DiscoverAsync(token, StickyPublicationTestData.Readback(request),
                CancellationToken.None);

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
            token, StickyPublicationTestData.Readback(request),
            CancellationToken.None);
        Assert.Equal(StickyDiscoveryKind.ExactTarget, result.Kind);
        Assert.Equal(StickyPublicationOperation.Observed,
            result.Receipt!.Operation);
        Assert.Equal(0, factory.Transport.Creates + factory.Transport.Updates);
    }

    [Fact]
    public async Task RehydratedReceiptClassifiesEveryReadOnlyPublicState()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var exact = StickyPublicationTestData.Comment(20, rendered.Comment);
        var stale = StickyPublicationTestData.Comment(20,
            R4PublicationTestData.Render("stale",
                identity: new ReviewedIdentity(
                    request.Authorization.PullRequest.RepositoryId.ToString(
                        CultureInfo.InvariantCulture),
                    request.Authorization.PullRequest.Number,
                    request.Authorization.PullRequest.BaseSha,
                    request.Authorization.PullRequest.HeadSha),
                scope: request.Scope).Comment);
        foreach (var scenario in new[]
        {
            ("exact", StickyDiscoveryKind.ExactTarget),
            ("stale", StickyDiscoveryKind.StaleTarget),
            ("absent", StickyDiscoveryKind.Absent),
            ("malformed", StickyDiscoveryKind.InvalidOrIncomplete),
            ("incomplete", StickyDiscoveryKind.InvalidOrIncomplete),
        })
        {
            var factory = new FakePublisherTransportFactory();
            switch (scenario.Item1)
            {
                case "exact":
                    factory.Transport.Enqueue(exact);
                    factory.Transport.Read = Success(exact);
                    factory.Transport.Enqueue(exact);
                    break;
                case "stale":
                    factory.Transport.Enqueue(stale);
                    break;
                case "absent":
                    factory.Transport.Enqueue();
                    break;
                case "malformed":
                    factory.Transport.Enqueue(
                        StickyPublicationTestData.Comment(20,
                            rendered.Comment + " trailing"));
                    break;
            }

            var result = await new StickyCommentPublisher(factory)
                .DiscoverAsync(token,
                    StickyPublicationTestData.ReadbackFromReceipt(request, 20),
                    CancellationToken.None);

            Assert.Equal(scenario.Item2, result.Kind);
            Assert.Equal(0,
                factory.Transport.Creates + factory.Transport.Updates);
        }
    }

    [Fact]
    public async Task ReadOnlyDiscoveryHonorsCancellationAtEveryReadPhase()
    {
        foreach (var phase in new[]
        {
            "after-exact-list",
            "during-get",
            "after-absent-list",
            "during-relist",
        })
        {
            var (token, request, rendered) =
                await StickyPublicationTestData.CreateAsync();
            var comment = StickyPublicationTestData.Comment(19,
                rendered.Comment);
            var factory = new FakePublisherTransportFactory();
            using var cancellation = new CancellationTokenSource();
            if (phase == "after-absent-list") factory.Transport.Enqueue();
            else
            {
                factory.Transport.Enqueue(comment);
                factory.Transport.Read = Success(comment);
                factory.Transport.Enqueue(comment);
            }
            factory.Transport.OnList = () =>
            {
                if (phase is "after-exact-list" or "after-absent-list" &&
                        factory.Transport.Lists == 1 ||
                    phase == "during-relist" &&
                        factory.Transport.Lists == 2)
                    cancellation.Cancel();
            };
            if (phase == "during-get")
                factory.Transport.OnRead = cancellation.Cancel;

            var result = await new StickyCommentPublisher(factory)
                .DiscoverAsync(token,
                    StickyPublicationTestData.Readback(request),
                    cancellation.Token);

            Assert.Equal(StickyDiscoveryKind.Cancelled, result.Kind);
            Assert.Equal(StickyPublicationReason.Cancelled, result.Reason);
            Assert.Null(result.Receipt);
            Assert.Equal(0,
                factory.Transport.Creates + factory.Transport.Updates);
            if (factory.Transport.Reads > 0)
                Assert.All(factory.Transport.ReadCancellationTokens,
                    used => Assert.Equal(cancellation.Token, used));
        }
    }

    [Fact]
    public async Task CrossPageLastEvidenceIsRequiredForCompleteDiscovery()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.EnqueuePageWithLast(2, 50);
        factory.Transport.EnqueuePageWithLast(null, null);

        var result = await new StickyCommentPublisher(factory).DiscoverAsync(
            token, StickyPublicationTestData.Readback(request),
            CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.InvalidOrIncomplete, result.Kind);
        Assert.Equal(StickyPublicationReason.DiscoveryIncomplete,
            result.Reason);
    }

    [Fact]
    public async Task DeadlineDuringDiscoveryIsAuthorizationFailure()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var within = true;
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.DeadlineProbe = () => within;
        factory.Transport.OnList = () => within = false;

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
            result.Outcome);
        Assert.Equal(StickyPublicationReason.DiscoveryIncomplete,
            result.Reason);
        Assert.Equal(0, factory.Transport.Creates + factory.Transport.Updates);
    }

    [Fact]
    public async Task DeadlineAfterCompleteDiscoveryIsKnownNotWritten()
    {
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var probes = 0;
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.DeadlineProbe = () => ++probes <= 2;

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.KnownNotWritten,
            result.Outcome);
        Assert.Equal(StickyPublicationReason.Deadline, result.Reason);
        Assert.Equal(0, factory.Transport.Creates + factory.Transport.Updates);
    }

    [Fact]
    public async Task DeadlineAfterMutationIsOutcomeUnknownWithoutReceipt()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var within = true;
        var comment = StickyPublicationTestData.Comment(17, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation = Success(comment);
        factory.Transport.DeadlineProbe = () => within;
        factory.Transport.OnMutation = () => within = false;

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(StickyPublicationReason.Deadline, result.Reason);
        Assert.Null(result.Receipt);
        Assert.Equal(1, factory.Transport.Creates);
    }

    [Fact]
    public async Task LiveReceiptCanBeRehydratedWithoutMintingAnotherResult()
    {
        var (token, request, rendered) =
            await StickyPublicationTestData.CreateAsync();
        var comment = StickyPublicationTestData.Comment(18, rendered.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation = Success(comment);
        factory.Transport.Read = Success(comment);
        factory.Transport.Enqueue(comment);
        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);
        var live = Assert.IsType<StickyCommentPublisher
            .StickyPublicationReceipt>(result.Receipt);

        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(live.Operation, live.RepositoryId,
                live.PullRequestNumber, live.CommentId, live.CommentUrl,
                live.ScopeSha256, live.BodySha256, live.HeadSha,
                out var rehydrated));
        Assert.Equal(live.CommentUrl, rehydrated!.CommentUrl);
        Assert.Equal(live.BodySha256, rehydrated.BodySha256);
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
        Assert.Equal(BoundedGitHubPublisherOutcome.CancelledBeforeSend,
            result.Outcome);
        Assert.Equal(0, factory.Creates);
    }

    [Theory]
    [InlineData((int)BoundedGitHubHttpOutcome.KnownNotSent,
        (int)BoundedGitHubPublisherOutcome.KnownNotWritten)]
    [InlineData((int)BoundedGitHubHttpOutcome.CancelledBeforeSend,
        (int)BoundedGitHubPublisherOutcome.CancelledBeforeSend)]
    [InlineData((int)
        BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
        (int)BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure)]
    public async Task PreSendMutationFailuresStayInTheirClosedOutcomePhase(
        int failureValue, int expectedValue)
    {
        var failure = (BoundedGitHubHttpOutcome)failureValue;
        var expected = (BoundedGitHubPublisherOutcome)expectedValue;
        var (token, request, _) = await StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Mutation =
            BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Failed(
                failure, failure ==
                    BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure
                        ? BoundedGitHubPublisherReason.ValidationRejected
                        : BoundedGitHubPublisherReason.Deadline,
                failure == BoundedGitHubHttpOutcome
                    .AuthorizationOrValidationFailure
                        ? new BoundedGitHubValidationEvidence(422, false,
                            "validation failed", null, [])
                        : null);

        var result = await new StickyCommentPublisher(factory).PublishAsync(
            token, request, CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Receipt);
        Assert.Equal(1, factory.Transport.Creates);
    }

    private static BoundedGitHubHttpResult<BoundedGitHubIssueComment>
        Success(BoundedGitHubIssueComment comment) =>
        BoundedGitHubHttpResult<BoundedGitHubIssueComment>.Success(comment);

    private static string JsonBody(byte[] bytes)
    {
        using var json = System.Text.Json.JsonDocument.Parse(bytes);
        return json.RootElement.GetProperty("body").GetString()!;
    }
}
