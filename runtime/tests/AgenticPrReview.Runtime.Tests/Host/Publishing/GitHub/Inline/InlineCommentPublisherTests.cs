using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Reflection;
using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Inline;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using Xunit;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Inline;

public sealed class InlineCommentPublisherTests
{
    private const string Exact422 =
        "{\"message\":\"Validation Failed\"," +
        "\"documentation_url\":\"https://docs.github.com/rest/pulls/" +
        "reviews#create-a-review-for-a-pull-request\"," +
        "\"errors\":[{\"resource\":\"PullRequestReview\"," +
        "\"field\":\"comments\",\"code\":\"invalid\"}]}";

    [Fact]
    public void PublicationAuthorityTypesHaveNoPublicConstructionSurface()
    {
        Assert.Empty(typeof(ActionHostCoordinator.PostAcceptanceInlineOperation)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.Empty(typeof(AuthorizedInlinePublicationRequest)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        Assert.DoesNotContain(
            typeof(BoundedGitHubPublisherTransportFactory).GetMethods(
                BindingFlags.Instance | BindingFlags.Public),
            static method => method.Name == "Create" &&
                method.GetParameters().Any(static parameter =>
                    parameter.ParameterType == typeof(ActionHostGitHubToken)) &&
                method.GetParameters().Any(static parameter =>
                    parameter.ParameterType ==
                        typeof(AuthorizedInlinePublicationRequest)));
    }

    [Fact]
    public async Task CompleteInitialListSuppressesExactIdentityWithoutWrite()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 7));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ExistingDuplicate);
        Assert.Equal(0, result.BatchAttempts);
        Assert.Equal(0, data.Revalidation.Calls);
        Assert.Equal(0, transport.BatchCalls);
    }

    [Fact]
    public async Task CompleteMultiPageInitialListSuppressesExactIdentity()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport();
        transport.EnqueuePageWithBounds(2, 2, OrdinaryComment(6));
        transport.EnqueuePageWithBounds(null, 2,
            Comment(data.Request, rendered[0], 7));

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ExistingDuplicate);
        Assert.Equal(2, transport.ListCalls);
        Assert.Equal(0, transport.BatchCalls);
    }

    [Fact]
    public async Task LaterRevisionWithNullLinePreservesDuplicateAcrossRuns()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var published = Comment(data.Request, rendered[0], 8);
        var transport = new FakeInlineTransport();
        transport.EnqueuePage();
        transport.EnqueuePage(published);
        transport.EnqueuePage(published with { Line = null });
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var first = await publisher.PublishAsync(
            data.Request, CancellationToken.None);
        var laterRevision = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(first.IsComplete);
        Assert.Equal(1, first.Reasons.ReconciledPublished);
        Assert.True(laterRevision.IsComplete);
        Assert.Equal(1, laterRevision.Reasons.ExistingDuplicate);
        Assert.Equal(1, transport.BatchCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task UnrelatedHistoricalMarkerWithNullLineDoesNotBlockDiscovery()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var historical = Comment(data.Request, rendered[0], 7) with
        {
            Body = InlineCommentMarker.Append(
                "historical", new string('0', 64)),
            Line = null,
        };
        var transport = new FakeInlineTransport();
        transport.EnqueuePage(historical);
        transport.EnqueuePage(Comment(data.Request, rendered[0], 8));

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ReconciledPublished);
        Assert.Equal(1, transport.BatchCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task PublisherRejectsPageAndRecordCapPlusOne()
    {
        var recordData = await CreateAsync();
        var recordTransport = new FakeInlineTransport();
        recordTransport.EnqueuePage(Enumerable.Range(1,
            BoundedGitHubPublisherPolicy.MaximumRecords + 1)
            .Select(index => OrdinaryComment(index)).ToArray());
        var records = await new InlineCommentPublisher(
                new FakeInlineFactory(recordTransport))
            .PublishAsync(recordData.Request, CancellationToken.None);

        Assert.False(records.IsComplete);
        Assert.Equal(1, records.Reasons.ListingIncomplete);
        Assert.Equal(0, recordTransport.BatchCalls);

        var pageData = await CreateAsync();
        var pageTransport = new FakeInlineTransport();
        for (var page = 1;
            page <= BoundedGitHubPublisherPolicy.MaximumPages;
            page++)
        {
            pageTransport.EnqueuePageWithBounds(page + 1, null);
        }

        var pages = await new InlineCommentPublisher(
                new FakeInlineFactory(pageTransport))
            .PublishAsync(pageData.Request, CancellationToken.None);

        Assert.False(pages.IsComplete);
        Assert.Equal(1, pages.Reasons.ListingIncomplete);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumPages,
            pageTransport.ListCalls);
        Assert.Equal(0, pageTransport.BatchCalls);
    }

    [Fact]
    public async Task AmbiguousBatchNeverFallsBackAndCompletesOnlyByReadback()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.TransportFailure),
        };
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 8));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ReconciledPublished);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(1, transport.BatchCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task SuccessfulBatchRequiresCompleteExactRelist()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport();
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 8));

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.ReconciledPublished);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(0, result.IndividualAttempts);
    }

    [Fact]
    public async Task MatchingHistoricalMarkerCannotReconcileBatch()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport();
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 8) with
        {
            Line = null,
        });

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.BatchOutcomeUnknown);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task UnresolvedAmbiguousBatchNeverFallsBack()
    {
        var data = await CreateAsync();
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.OutcomeUnknown,
                    BoundedGitHubPublisherReason.TransportFailure),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.BatchOutcomeUnknown);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task AllNonPositiveBatchOutcomesHaveZeroIndividualFanout()
    {
        var evidence = new BoundedGitHubValidationEvidence(
            403, false, "bounded", null, []);
        var cases = new[]
        {
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.OutcomeUnknown,
                BoundedGitHubPublisherReason.TransportFailure),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.OutcomeUnknown,
                BoundedGitHubPublisherReason.Deadline),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.OutcomeUnknown,
                BoundedGitHubPublisherReason.InvalidResponse),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.CancelledBeforeSend,
                BoundedGitHubPublisherReason.Deadline),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidResponse),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
                BoundedGitHubPublisherReason.AuthorizationDenied, evidence),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
                BoundedGitHubPublisherReason.ValidationRejected, evidence),
            BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Failed(
                BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
                BoundedGitHubPublisherReason.RateLimited,
                new BoundedGitHubValidationEvidence(
                    429, false, "bounded", null, [])),
        };

        foreach (var batch in cases)
        {
            var data = await CreateAsync();
            var transport = new FakeInlineTransport { Batch = batch };
            transport.EnqueuePage();
            transport.EnqueuePage();

            var result = await new InlineCommentPublisher(
                    new FakeInlineFactory(transport))
                .PublishAsync(data.Request, CancellationToken.None);

            Assert.False(result.IsComplete);
            Assert.Equal(0, result.IndividualAttempts);
            Assert.Equal(0, transport.IndividualCalls);
        }
    }

    [Fact]
    public async Task ExactBatchValidationRelistsAndUsesBoundedIndividualPath()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var exact = Comment(data.Request, rendered[0], 9);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        transport.Creates.Enqueue(
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Success(exact));
        transport.Reads.Enqueue(
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Success(exact));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Reasons.IndividualPublished);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(1, result.IndividualAttempts);
        Assert.Equal(2, data.Revalidation.Calls);
        Assert.Equal(2, transport.ListCalls);
        Assert.Equal(1, transport.IndividualCalls);
        Assert.Equal(1, transport.ReadCalls);
    }

    [Fact]
    public async Task PartialSecondListClosesFallbackWithoutAnyIndividualWrite()
    {
        var data = await CreateAsync(candidateCount: 2);
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 10));
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.ConcurrentDuplicate);
        Assert.Equal(1, result.Reasons.ReadbackIncomplete);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task MatchingHistoricalMarkerCannotEnterFallbackFanout()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage(Comment(data.Request, rendered[0], 10) with
        {
            Line = null,
        });

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.ReadbackIncomplete);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task PostSendCancellationCannotEnterFallback()
    {
        var data = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
            OnBatch = cancellation.Cancel,
        };
        transport.EnqueuePage();

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, cancellation.Token);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.Cancelled);
        Assert.Equal(1, transport.ListCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task CancellationDuringFallbackHeadCheckIsOutcomeUnknown()
    {
        var data = await CreateAsync();
        using var cancellation = new CancellationTokenSource();
        data.Revalidation.OnRequest = (call, _) =>
        {
            if (call == 2) cancellation.Cancel();
        };
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, cancellation.Token);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.Cancelled);
        Assert.Equal(2, data.Revalidation.Calls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task CancellationAfterFirstIndividualPreservesSuccessAndStops()
    {
        var data = await CreateAsync(candidateCount: 2);
        var rendered = Render(data.Request);
        using var cancellation = new CancellationTokenSource();
        var first = Comment(data.Request, rendered[0], 12);
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
            OnRead = cancellation.Cancel,
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        transport.Creates.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(first));
        transport.Reads.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(first));

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, cancellation.Token);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            result.Outcome);
        Assert.Equal(1, result.Reasons.IndividualPublished);
        Assert.Equal(1, result.Reasons.Cancelled);
        Assert.Equal(1, result.IndividualAttempts);
        Assert.Equal(1, transport.IndividualCalls);
        Assert.Equal(1, transport.ReadCalls);
    }

    [Fact]
    public async Task IncompleteOrMismatchedSecondListIsOutcomeUnknown()
    {
        var incompleteData = await CreateAsync();
        var incompleteTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        incompleteTransport.EnqueuePage();
        incompleteTransport.EnqueueFailure(
            BoundedGitHubPublisherReason.InvalidPagination);
        var incomplete = await new InlineCommentPublisher(
                new FakeInlineFactory(incompleteTransport))
            .PublishAsync(incompleteData.Request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            incomplete.Outcome);
        Assert.Equal(1, incomplete.Reasons.ReadbackIncomplete);
        Assert.Equal(0, incompleteTransport.IndividualCalls);

        var mismatchData = await CreateAsync();
        var rendered = Render(mismatchData.Request);
        var mismatchTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        mismatchTransport.EnqueuePage();
        mismatchTransport.EnqueuePage(Comment(
            mismatchData.Request, rendered[0], 11) with { Line = 2 });
        var mismatch = await new InlineCommentPublisher(
                new FakeInlineFactory(mismatchTransport))
            .PublishAsync(mismatchData.Request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            mismatch.Outcome);
        Assert.Equal(1, mismatch.Reasons.ReadbackIncomplete);
        Assert.Equal(0, mismatchTransport.IndividualCalls);

        var timeoutData = await CreateAsync();
        var timeoutTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        timeoutTransport.EnqueuePage();
        timeoutTransport.EnqueueFailure(BoundedGitHubPublisherReason.Deadline);
        var timeout = await new InlineCommentPublisher(
                new FakeInlineFactory(timeoutTransport))
            .PublishAsync(timeoutData.Request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            timeout.Outcome);
        Assert.Equal(1, timeout.Reasons.ReadbackIncomplete);
        Assert.Equal(0, timeoutTransport.IndividualCalls);
    }

    [Fact]
    public async Task ChangedHeadAtFirstBarrierClosesBeforeBatch()
    {
        var data = await CreateAsync();
        data.Revalidation.Current = data.Revalidation.Current with
        {
            HeadSha = new string('f', 40),
        };
        var transport = new FakeInlineTransport();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.HeadNotExact);
        Assert.Equal(0, result.BatchAttempts);
        Assert.Equal(1, data.Revalidation.Calls);
        Assert.Equal(0, transport.BatchCalls);
    }

    [Fact]
    public async Task ChangedHeadAtFallbackBarrierClosesBeforeIndividuals()
    {
        var data = await CreateAsync();
        data.Revalidation.OnCall = call => call == 1
            ? data.Revalidation.Current
            : data.Revalidation.Current with
            {
                HeadSha = new string('f', 40),
            };
        var transport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        transport.EnqueuePage();
        transport.EnqueuePage();
        var publisher = new InlineCommentPublisher(
            new FakeInlineFactory(transport));

        var result = await publisher.PublishAsync(
            data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.HeadNotExact);
        Assert.Equal(1, result.BatchAttempts);
        Assert.Equal(0, result.IndividualAttempts);
        Assert.Equal(2, data.Revalidation.Calls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task IneligibleOrUnavailableFirstBarrierHasZeroWrites()
    {
        var ineligibleData = await CreateAsync();
        ineligibleData.Revalidation.Current =
            ineligibleData.Revalidation.Current with { Draft = true };
        var ineligibleTransport = new FakeInlineTransport();
        ineligibleTransport.EnqueuePage();
        var ineligible = await new InlineCommentPublisher(
                new FakeInlineFactory(ineligibleTransport))
            .PublishAsync(ineligibleData.Request, CancellationToken.None);

        Assert.Equal(1, ineligible.Reasons.HeadNotExact);
        Assert.Equal(0, ineligibleTransport.BatchCalls);

        var unavailableData = await CreateAsync();
        unavailableData.Revalidation.Failure =
            ActionHostGitObjectFailure.UpstreamFailure;
        var unavailableTransport = new FakeInlineTransport();
        unavailableTransport.EnqueuePage();
        var unavailable = await new InlineCommentPublisher(
                new FakeInlineFactory(unavailableTransport))
            .PublishAsync(unavailableData.Request, CancellationToken.None);

        Assert.Equal(1, unavailable.Reasons.HeadNotExact);
        Assert.Equal(0, unavailableTransport.BatchCalls);
        Assert.Equal(0, unavailableTransport.IndividualCalls);
    }

    [Fact]
    public async Task FallbackCapsAtFiveAndUnknownOrBadReadbackStopsFanout()
    {
        var fiveData = await CreateAsync(candidateCount: 5);
        var fiveRendered = Render(fiveData.Request);
        var fiveTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        fiveTransport.EnqueuePage();
        fiveTransport.EnqueuePage();
        for (var index = 0; index < fiveRendered.Count; index++)
        {
            var exact = Comment(fiveData.Request, fiveRendered[index],
                20 + index);
            fiveTransport.Creates.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewComment>.Success(exact));
            fiveTransport.Reads.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewComment>.Success(exact));
        }

        var five = await new InlineCommentPublisher(
                new FakeInlineFactory(fiveTransport))
            .PublishAsync(fiveData.Request, CancellationToken.None);

        Assert.True(five.IsComplete);
        Assert.Equal(5, five.IndividualAttempts);
        Assert.Equal(5, five.Reasons.IndividualPublished);

        var unknownData = await CreateAsync(candidateCount: 2);
        var unknownTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        unknownTransport.EnqueuePage();
        unknownTransport.EnqueuePage();
        unknownTransport.Creates.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Failed(
                BoundedGitHubHttpOutcome.OutcomeUnknown,
                BoundedGitHubPublisherReason.TransportFailure));

        var unknown = await new InlineCommentPublisher(
                new FakeInlineFactory(unknownTransport))
            .PublishAsync(unknownData.Request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            unknown.Outcome);
        Assert.Equal(1, unknown.IndividualAttempts);
        Assert.Equal(1, unknownTransport.IndividualCalls);

        var readData = await CreateAsync(candidateCount: 2);
        var readRendered = Render(readData.Request);
        var created = Comment(readData.Request, readRendered[0], 30);
        var readTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        readTransport.EnqueuePage();
        readTransport.EnqueuePage();
        readTransport.Creates.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(created));
        readTransport.Reads.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(created with { Line = 99 }));

        var badRead = await new InlineCommentPublisher(
                new FakeInlineFactory(readTransport))
            .PublishAsync(readData.Request, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            badRead.Outcome);
        Assert.Equal(1, badRead.IndividualAttempts);
        Assert.Equal(1, readTransport.IndividualCalls);
    }

    [Fact]
    public async Task KnownIndividualRejectionsContinueBoundedFanout()
    {
        var evidence = new BoundedGitHubValidationEvidence(
            422, false, "bounded", null, []);
        var knownRejections = new[]
        {
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Failed(
                BoundedGitHubHttpOutcome.KnownNotSent,
                BoundedGitHubPublisherReason.InvalidRequest),
            BoundedGitHubHttpResult<BoundedGitHubReviewComment>.Failed(
                BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
                BoundedGitHubPublisherReason.ValidationRejected,
                evidence),
        };

        foreach (var rejection in knownRejections)
        {
            var data = await CreateAsync(candidateCount: 2);
            var rendered = Render(data.Request);
            var later = Comment(data.Request, rendered[1], 35);
            var transport = new FakeInlineTransport
            {
                Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                    .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                        BoundedGitHubPublisherReason.BatchValidationRejected),
            };
            transport.EnqueuePage();
            transport.EnqueuePage();
            transport.Creates.Enqueue(rejection);
            transport.Creates.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewComment>.Success(later));
            transport.Reads.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewComment>.Success(later));

            var result = await new InlineCommentPublisher(
                    new FakeInlineFactory(transport))
                .PublishAsync(data.Request, CancellationToken.None);

            Assert.False(result.IsComplete);
            Assert.Equal(
                BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure,
                result.Outcome);
            Assert.Equal(1, result.Reasons.IndividualKnownFailure);
            Assert.Equal(1, result.Reasons.IndividualPublished);
            Assert.Equal(2, result.IndividualAttempts);
            Assert.Equal(2, transport.IndividualCalls);
            Assert.Equal(1, transport.ReadCalls);
        }
    }

    [Fact]
    public async Task SuccessfulIndividualReadbackIsSuppressedOnNextRun()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        var exact = Comment(data.Request, rendered[0], 40);
        var firstTransport = new FakeInlineTransport
        {
            Batch = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>
                .Failed(BoundedGitHubHttpOutcome.KnownNotSent,
                    BoundedGitHubPublisherReason.BatchValidationRejected),
        };
        firstTransport.EnqueuePage();
        firstTransport.EnqueuePage();
        firstTransport.Creates.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(exact));
        firstTransport.Reads.Enqueue(BoundedGitHubHttpResult<
            BoundedGitHubReviewComment>.Success(exact));
        var first = await new InlineCommentPublisher(
                new FakeInlineFactory(firstTransport))
            .PublishAsync(data.Request, CancellationToken.None);

        var nextTransport = new FakeInlineTransport();
        nextTransport.EnqueuePage(exact);
        var next = await new InlineCommentPublisher(
                new FakeInlineFactory(nextTransport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.True(first.IsComplete);
        Assert.True(next.IsComplete);
        Assert.Equal(1, next.Reasons.ExistingDuplicate);
        Assert.Equal(0, nextTransport.BatchCalls);
        Assert.Equal(0, nextTransport.IndividualCalls);
    }

    [Fact]
    public async Task CommonTransportUsesExactBatchEndpointAndStrict422Shape()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(request =>
                {
                    Assert.Equal(HttpMethod.Post, request.Method);
                    Assert.Equal(
                        $"https://api.github.com/repos/" +
                        $"{ActionHostAuthorizationScenario.RepositoryName}/" +
                        $"pulls/{ActionHostAuthorizationScenario.PullRequestNumber}/" +
                        "reviews",
                        request.RequestUri!.AbsoluteUri);
                    Assert.Equal(BoundedGitHubPublisherPolicy.ApiVersion,
                        Assert.Single(request.Headers.GetValues(
                            "X-GitHub-Api-Version")));
                    return Json(HttpStatusCode.UnprocessableEntity, Exact422);
                }));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.BatchValidationRejected,
            result.Reason);
        Assert.Null(result.ValidationEvidence);
    }

    [Fact]
    public async Task CommonTransportDoesNotTreatUnknown422AsFallbackSafe()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(
                    HttpStatusCode.UnprocessableEntity,
                    Exact422[..^1] + ",\"extra\":true}")));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Null(result.Value);
        Assert.Equal(
            BoundedGitHubHttpOutcome.AuthorizationOrValidationFailure,
            result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.ValidationRejected,
            result.Reason);
        Assert.NotNull(result.ValidationEvidence);
    }

    [Fact]
    public async Task CommonTransportTreatsMalformed422AsOutcomeUnknown()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(
                    HttpStatusCode.UnprocessableEntity,
                    "{\"message\":")));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            result.Reason);
    }

    [Fact]
    public async Task CommonTransportTreatsServerFailureAsOutcomeUnknown()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(
                    HttpStatusCode.InternalServerError,
                    "{\"message\":\"upstream\"}")));

        var result = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown, result.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidResponse,
            result.Reason);
    }

    [Fact]
    public async Task CommonTransportListsReviewCommentsOnCanonicalEndpoint()
    {
        var data = await CreateAsync();
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    Assert.EndsWith(
                        $"/pulls/{ActionHostAuthorizationScenario.PullRequestNumber}/" +
                        "comments?per_page=100&page=1",
                        request.RequestUri!.AbsoluteUri,
                        StringComparison.Ordinal);
                    return Json(HttpStatusCode.OK, "[]");
                }));

        var result = await transport.ListReviewCommentsAsync(
            1, CancellationToken.None);

        Assert.NotNull(result.Value);
        Assert.Empty(result.Value.Comments);
        Assert.Null(result.Value.NextPage);
    }

    [Fact]
    public async Task OrdinaryFileLevelCommentAllowsNullTargetAndArbitraryBody()
    {
        var data = await CreateAsync();
        const string ordinary = "ordinary\0body with bidi \u202e and markdown <!--";
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(HttpStatusCode.OK,
                    "[" + OrdinaryFileReviewCommentDocument(
                        data.Request, 71, ordinary) + "]")));

        var result = await transport.ListReviewCommentsAsync(
            1, CancellationToken.None);

        var comment = Assert.Single(Assert.IsType<
            BoundedGitHubReviewCommentPage>(result.Value).Comments);
        Assert.Equal(ordinary, comment.Body);
        Assert.Null(comment.ReviewId);
        Assert.Null(comment.Path);
        Assert.Null(comment.Line);
        Assert.Null(comment.Side);
        Assert.Null(comment.CommitId);
    }

    [Fact]
    public async Task HistoricalInlineMarkerAllowsNullLineFromGitHub()
    {
        var data = await CreateAsync();
        var rendered = Render(data.Request);
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(HttpStatusCode.OK,
                    "[" + HistoricalInlineReviewCommentDocument(
                        data.Request, rendered[0], 72) + "]")));

        var result = await transport.ListReviewCommentsAsync(
            1, CancellationToken.None);

        var comment = Assert.Single(Assert.IsType<
            BoundedGitHubReviewCommentPage>(result.Value).Comments);
        Assert.Equal(rendered[0].Body, comment.Body);
        Assert.Equal(rendered[0].Candidate.Path, comment.Path);
        Assert.Null(comment.Line);
        Assert.Equal("RIGHT", comment.Side);
    }

    [Fact]
    public async Task MalformedCurrentMarkerStillClosesBeforeAnyWrite()
    {
        var data = await CreateAsync();
        var transport = new FakeInlineTransport();
        transport.EnqueuePage(new BoundedGitHubReviewComment(
            72,
            null,
            "https://api.github.com/repos/owner/repository/pulls/comments/72",
            "https://api.github.com/repos/owner/repository/pulls/17",
            "https://github.com/owner/repository/pull/17#discussion_r72",
            "ordinary\n\n<!-- agentic-pr-review:r4:inline:v1 malformed -->",
            null,
            null,
            null,
            null));

        var result = await new InlineCommentPublisher(
                new FakeInlineFactory(transport))
            .PublishAsync(data.Request, CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Reasons.ListingIncomplete);
        Assert.Equal(0, transport.BatchCalls);
        Assert.Equal(0, transport.IndividualCalls);
    }

    [Fact]
    public async Task CommonInlineTransportSharesRequestBudgetAcrossOperations()
    {
        var data = await CreateAsync();
        var calls = 0;
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(request =>
                {
                    calls++;
                    if (request.Method == HttpMethod.Post)
                    {
                        return Json(HttpStatusCode.OK,
                            ReviewDocument(data.Request, 77));
                    }

                    return Json(HttpStatusCode.OK, "[]");
                }));
        for (var index = 0;
            index < BoundedGitHubPublisherPolicy.MaximumRequests - 1;
            index++)
        {
            var listed = await transport.ListReviewCommentsAsync(
                1, CancellationToken.None);
            Assert.NotNull(listed.Value);
        }

        var batch = await transport.CreateBatchReviewAsync(
            "{}"u8.ToArray(), CancellationToken.None);
        var over = await transport.GetReviewCommentAsync(
            1, CancellationToken.None);

        Assert.NotNull(batch.Value);
        Assert.Equal(BoundedGitHubPublisherReason.RequestLimit, over.Reason);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumRequests, calls);
    }

    [Fact]
    public async Task CommonInlineTransportRejectsPageAndRecordCapPlusOne()
    {
        var data = await CreateAsync();
        var calls = 0;
        using var transport = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    calls++;
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(
                        Enumerable.Range(0, 101).Select(static _ =>
                            new { })));
                }));

        var page = await transport.ListReviewCommentsAsync(
            BoundedGitHubPublisherPolicy.MaximumPages + 1,
            CancellationToken.None);
        var records = await transport.ListReviewCommentsAsync(
            1, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherReason.InvalidRequest, page.Reason);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidPagination,
            records.Reason);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CommonInlineTransportSharesDeadlineAndResponseBudgets()
    {
        var data = await CreateAsync();
        var deadlineCalls = 0;
        using (var expired = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    deadlineCalls++;
                    return Json(HttpStatusCode.OK, "[]");
                }), overallTimeout: TimeSpan.Zero))
        {
            var deadline = await expired.ListReviewCommentsAsync(
                1, CancellationToken.None);
            Assert.Equal(BoundedGitHubPublisherReason.Deadline,
                deadline.Reason);
            Assert.Equal(0, deadlineCalls);
        }

        using (var oversized = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    var response = Json(HttpStatusCode.OK, "[]");
                    response.Content.Headers.ContentLength =
                        BoundedGitHubPublisherPolicy.MaximumResponseBytes + 1;
                    return response;
                })))
        {
            var response = await oversized.ListReviewCommentsAsync(
                1, CancellationToken.None);
            Assert.Equal(BoundedGitHubPublisherReason.ResponseLimit,
                response.Reason);
        }

        var exactCap = AtResponseCapReviewPageJson(data.Request, 50);
        Assert.Equal(BoundedGitHubPublisherPolicy.MaximumResponseBytes,
            Encoding.UTF8.GetByteCount(exactCap));
        var aggregateCalls = 0;
        using var aggregate = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    aggregateCalls++;
                    return aggregateCalls <= 64
                        ? Json(HttpStatusCode.OK, exactCap)
                        : Json(HttpStatusCode.OK, "[]");
                }));
        for (var index = 0; index < 64; index++)
        {
            var accepted = await aggregate.ListReviewCommentsAsync(
                1, CancellationToken.None);
            Assert.NotNull(accepted.Value);
        }

        var overflow = await aggregate.ListReviewCommentsAsync(
            1, CancellationToken.None);

        Assert.Equal(BoundedGitHubPublisherReason.AggregateResponseLimit,
            overflow.Reason);
        Assert.Equal(65, aggregateCalls);
    }

    [Fact]
    public async Task CommonInlineTransportRechecksDeadlineAfterMapping()
    {
        var data = await CreateAsync();
        using (var listing = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(HttpStatusCode.OK,
                    "[" + ReviewCommentDocument(data.Request, 81) + "]")),
                overallTimeout: TimeSpan.FromSeconds(180),
                operation: new ExpiringReadClock(6)))
        {
            var result = await listing.ListReviewCommentsAsync(
                1, CancellationToken.None);
            Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent,
                result.Outcome);
            Assert.Equal(BoundedGitHubPublisherReason.Deadline,
                result.Reason);
        }

        using (var mutation = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(HttpStatusCode.Created,
                    ReviewCommentDocument(data.Request, 82))),
                overallTimeout: TimeSpan.FromSeconds(180),
                operation: new ExpiringReadClock(5)))
        {
            var result = await mutation.CreateReviewCommentAsync(
                "{}"u8.ToArray(), CancellationToken.None);
            Assert.Equal(BoundedGitHubHttpOutcome.OutcomeUnknown,
                result.Outcome);
            Assert.Equal(BoundedGitHubPublisherReason.Deadline,
                result.Reason);
        }

        using var readback = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ => Json(HttpStatusCode.OK,
                    ReviewCommentDocument(data.Request, 83))),
                overallTimeout: TimeSpan.FromSeconds(180),
                operation: new ExpiringReadClock(5));
        var read = await readback.GetReviewCommentAsync(
            83, CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, read.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.Deadline, read.Reason);
    }

    [Fact]
    public async Task CommonInlineRequestBodiesEnforceExactCaps()
    {
        var data = await CreateAsync();
        var batchCalls = 0;
        using (var exactBatch = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    batchCalls++;
                    return Json(HttpStatusCode.OK,
                        ReviewDocument(data.Request, 91));
                })))
        {
            var result = await exactBatch.CreateBatchReviewAsync(
                new byte[BoundedGitHubPublisherPolicy
                    .MaximumInlineBatchRequestBytes],
                CancellationToken.None);
            Assert.NotNull(result.Value);
            Assert.Equal(1, batchCalls);
        }

        using (var oversizedBatch = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    batchCalls++;
                    return Json(HttpStatusCode.OK,
                        ReviewDocument(data.Request, 92));
                })))
        {
            var result = await oversizedBatch.CreateBatchReviewAsync(
                new byte[BoundedGitHubPublisherPolicy
                    .MaximumInlineBatchRequestBytes + 1],
                CancellationToken.None);
            Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent,
                result.Outcome);
            Assert.Equal(BoundedGitHubPublisherReason.InvalidRequest,
                result.Reason);
            Assert.Equal(1, batchCalls);
        }

        var individualCalls = 0;
        using (var exactIndividual = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    individualCalls++;
                    return Json(HttpStatusCode.Created,
                        ReviewCommentDocument(data.Request, 93));
                })))
        {
            var result = await exactIndividual.CreateReviewCommentAsync(
                new byte[BoundedGitHubPublisherPolicy
                    .MaximumIndividualInlineRequestBytes],
                CancellationToken.None);
            Assert.NotNull(result.Value);
            Assert.Equal(1, individualCalls);
        }

        using var oversizedIndividual = BoundedGitHubPublisherTransport
            .CreateInlineForTesting("token-canary", data.Request,
                new DelegateHandler(_ =>
                {
                    individualCalls++;
                    return Json(HttpStatusCode.Created,
                        ReviewCommentDocument(data.Request, 94));
                }));
        var over = await oversizedIndividual.CreateReviewCommentAsync(
            new byte[BoundedGitHubPublisherPolicy
                .MaximumIndividualInlineRequestBytes + 1],
            CancellationToken.None);

        Assert.Equal(BoundedGitHubHttpOutcome.KnownNotSent, over.Outcome);
        Assert.Equal(BoundedGitHubPublisherReason.InvalidRequest, over.Reason);
        Assert.Equal(1, individualCalls);
    }

    [Fact]
    public async Task SerializerFreezesHeadCoordinatesAndEscapesCanaries()
    {
        var finding = new AgentFinding(
            "high",
            "@ title <!-- fake",
            "message --> [link](https://example.invalid)",
            ImmutableArray.Create(new AgentEvidence(
                "observation", "file.txt", 1, 1)));
        var data = await CreateAsync(finding);
        var rendered = Render(data.Request);

        Assert.Single(rendered);
        Assert.DoesNotContain("@ title", rendered[0].Body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("<!-- fake", rendered[0].Body,
            StringComparison.Ordinal);
        var marker = InlineCommentMarker.Inspect(rendered[0].Body);
        Assert.Equal(InlineMarkerInspectionKind.Valid, marker.Kind);
        Assert.True(InlineCommentSerializer.TryBatch(
            data.Request, rendered, out var batch));
        Assert.NotNull(batch);
        Assert.True(batch.Length <= BoundedGitHubPublisherPolicy
            .MaximumInlineBatchRequestBytes);

        using var json = JsonDocument.Parse(batch);
        var root = json.RootElement;
        Assert.Equal(data.Request.Authorization.PullRequest.HeadSha,
            root.GetProperty("commit_id").GetString());
        Assert.Equal("COMMENT", root.GetProperty("event").GetString());
        var comment = Assert.Single(root.GetProperty("comments")
            .EnumerateArray());
        Assert.Equal("file-0.txt", comment.GetProperty("path").GetString());
        Assert.Equal(1, comment.GetProperty("line").GetInt32());
        Assert.Equal("RIGHT", comment.GetProperty("side").GetString());
        Assert.Equal(rendered[0].Body,
            comment.GetProperty("body").GetString());
    }

    private static IReadOnlyList<RenderedInlineComment> Render(
        AuthorizedInlinePublicationRequest request)
    {
        Assert.True(InlineCommentSerializer.TryRender(request,
            out var rendered));
        return Assert.IsAssignableFrom<IReadOnlyList<RenderedInlineComment>>(
            rendered);
    }

    private static BoundedGitHubReviewComment Comment(
        AuthorizedInlinePublicationRequest request,
        RenderedInlineComment rendered,
        long id) => new(
            id,
            99,
            $"https://api.github.com/repos/owner/repository/pulls/comments/{id}",
            "https://api.github.com/repos/owner/repository/pulls/17",
            "https://github.com/owner/repository/pull/17#discussion-diff-1",
            rendered.Body,
            rendered.Candidate.Path,
            rendered.Candidate.Line,
            "RIGHT",
            request.Authorization.PullRequest.HeadSha);

    private static BoundedGitHubReviewComment OrdinaryComment(long id) => new(
        id,
        99,
        $"https://api.github.com/repos/owner/repository/pulls/comments/{id}",
        "https://api.github.com/repos/owner/repository/pulls/17",
        "https://github.com/owner/repository/pull/17#discussion-diff-1",
        "ordinary review comment",
        "file.txt",
        1,
        "RIGHT",
        new string('a', 40));

    private static string ReviewDocument(
        AuthorizedInlinePublicationRequest request,
        long id) => JsonSerializer.Serialize(new
        {
            id,
            url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}/" +
                $"reviews/{id}",
            pull_request_url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}",
            html_url = $"https://github.com/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pull/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}" +
                $"#pullrequestreview-{id}",
            commit_id = request.Authorization.PullRequest.HeadSha,
        });

    private static string AtResponseCapReviewPageJson(
        AuthorizedInlinePublicationRequest request,
        long id)
    {
        var document = ReviewCommentDocument(request, id);
        var prefix = "[" + document[..^1] + ",\"padding\":\"";
        const string suffix = "\"}]";
        var padding = BoundedGitHubPublisherPolicy.MaximumResponseBytes -
            Encoding.UTF8.GetByteCount(prefix) - suffix.Length;
        Assert.True(padding > 0);
        return prefix + new string('a', padding) + suffix;
    }

    private static string ReviewCommentDocument(
        AuthorizedInlinePublicationRequest request,
        long id) => JsonSerializer.Serialize(new
        {
            id,
            pull_request_review_id = 99,
            url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"comments/{id}",
            pull_request_url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}",
            html_url = $"https://github.com/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pull/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}" +
                $"#discussion_r{id}",
            body = "ordinary",
            path = "file.txt",
            line = 1,
            side = "RIGHT",
            commit_id = request.Authorization.PullRequest.HeadSha,
        });

    private static string OrdinaryFileReviewCommentDocument(
        AuthorizedInlinePublicationRequest request,
        long id,
        string body) => JsonSerializer.Serialize(new
        {
            id,
            pull_request_review_id = (long?)null,
            url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"comments/{id}",
            pull_request_url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}",
            html_url = $"https://github.com/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pull/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}" +
                $"#discussion_r{id}",
            body,
            path = (string?)null,
            line = (int?)null,
            side = (string?)null,
            commit_id = (string?)null,
        });

    private static string HistoricalInlineReviewCommentDocument(
        AuthorizedInlinePublicationRequest request,
        RenderedInlineComment rendered,
        long id) => JsonSerializer.Serialize(new
        {
            id,
            pull_request_review_id = 99,
            url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"comments/{id}",
            pull_request_url = $"https://api.github.com/repos/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pulls/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}",
            html_url = $"https://github.com/" +
                $"{ActionHostAuthorizationScenario.RepositoryName}/pull/" +
                $"{ActionHostAuthorizationScenario.PullRequestNumber}" +
                $"#discussion_r{id}",
            body = rendered.Body,
            path = rendered.Candidate.Path,
            line = (int?)null,
            side = "RIGHT",
            commit_id = request.Authorization.PullRequest.HeadSha,
        });

    private static async Task<TestData> CreateAsync(
        AgentFinding? suppliedFinding = null,
        int candidateCount = 1)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var authorized = await scenario.CreateAuthorizer().AuthorizeAsync(
            scenario.Launch, CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(authorized.Invocation);
        var identity = new AgenticPrReview.Runtime.Agent.Core.ReviewedIdentity(
            invocation.PullRequest.RepositoryId.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            invocation.PullRequest.Number,
            invocation.PullRequest.BaseSha,
            invocation.PullRequest.HeadSha);
        var finding = suppliedFinding ?? R4PublicationTestData.Finding();
        Assert.InRange(candidateCount, 1, 5);
        var candidates = Enumerable.Range(0, candidateCount)
            .Select(index => new InlineCandidate(
                new R4FindingIdentityV1(finding,
                    new string((char)('a' + index), 64)),
                $"file-{index}.txt",
                index + 1,
                new string((char)('f' - index), 64)))
            .ToImmutableArray();
        var map = new InlineCandidateMap(
            identity,
            new string('d', 64),
            new string('e', 64),
            candidates,
            [],
            new(0, 0));
        var revalidation = new FakeRevalidationFactory(
            scenario.Transport.PullRequest);
        var constructor = typeof(ActionHostCoordinator
                .PostAcceptanceInlineOperation)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();
        var operation = Assert.IsType<ActionHostCoordinator
            .PostAcceptanceInlineOperation>(constructor.Invoke([
                scenario.Launch.Inputs.GitHubToken!,
                invocation,
                revalidation,
                map,
            ]));
        Assert.True(AuthorizedInlinePublicationRequest.TryCreate(
            operation, out var request));
        return new(request!, revalidation);
    }

    private sealed record TestData(
        AuthorizedInlinePublicationRequest Request,
        FakeRevalidationFactory Revalidation);

    private sealed class FakeInlineFactory(FakeInlineTransport transport) :
        IInlineGitHubPublisherTransportFactory
    {
        public IInlineGitHubPublisherTransport Create(
            AuthorizedInlinePublicationRequest request) => transport;
    }

    private sealed class FakeInlineTransport : IInlineGitHubPublisherTransport
    {
        private readonly Queue<BoundedGitHubHttpResult<
            BoundedGitHubReviewCommentPage>> _pages = new();

        internal BoundedGitHubHttpResult<BoundedGitHubPullRequestReview> Batch
        {
            get; set;
        } = BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>.Success(
            new(1, "api", "pull", "html#review", new string('a', 40)));

        internal Queue<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            Creates { get; } = new();

        internal Queue<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            Reads { get; } = new();

        internal int ListCalls { get; private set; }
        internal int BatchCalls { get; private set; }
        internal int IndividualCalls { get; private set; }
        internal int ReadCalls { get; private set; }
        internal System.Action? OnBatch { get; set; }
        internal System.Action? OnRead { get; set; }
        internal bool WithinOverallDeadline { get; set; } = true;

        public bool IsWithinOverallDeadline => WithinOverallDeadline;

        internal void EnqueuePage(
            params BoundedGitHubReviewComment[] comments) =>
            _pages.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewCommentPage>.Success(
                    new(comments, null, null)));

        internal void EnqueuePageWithBounds(int? nextPage, int? lastPage,
            params BoundedGitHubReviewComment[] comments) =>
            _pages.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewCommentPage>.Success(
                    new(comments, nextPage, lastPage)));

        internal void EnqueueFailure(BoundedGitHubPublisherReason reason) =>
            _pages.Enqueue(BoundedGitHubHttpResult<
                BoundedGitHubReviewCommentPage>.Failed(
                    BoundedGitHubHttpOutcome.KnownNotSent, reason));

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewCommentPage>>
            ListReviewCommentsAsync(int page,
                CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult(_pages.Dequeue());
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubPullRequestReview>>
            CreateBatchReviewAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            BatchCalls++;
            OnBatch?.Invoke();
            return Task.FromResult(Batch);
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            CreateReviewCommentAsync(ReadOnlyMemory<byte> body,
                CancellationToken cancellationToken)
        {
            IndividualCalls++;
            return Task.FromResult(Creates.Dequeue());
        }

        public Task<BoundedGitHubHttpResult<BoundedGitHubReviewComment>>
            GetReviewCommentAsync(long commentId,
                CancellationToken cancellationToken)
        {
            ReadCalls++;
            OnRead?.Invoke();
            return Task.FromResult(Reads.Dequeue());
        }

        public void Dispose() { }
    }

    private sealed class FakeRevalidationFactory(
        ActionHostGitHubPullRequestFact current) :
        IActionHostReviewedSnapshotTransportFactory
    {
        internal int Calls { get; private set; }
        internal ActionHostGitHubPullRequestFact Current { get; set; } =
            current;
        internal Func<int, ActionHostGitHubPullRequestFact>? OnCall
        {
            get; set;
        }
        internal ActionHostGitObjectFailure? Failure { get; set; }
        internal System.Action<int, CancellationToken>? OnRequest
        {
            get; set;
        }

        public IActionHostReviewedSnapshotTransport
            CreateReviewedSnapshotTransport(ActionHostGitHubToken token) =>
            new Transport(this);

        private sealed class Transport(FakeRevalidationFactory owner) :
            IActionHostReviewedSnapshotTransport
        {
            public Task<ActionHostGitObjectResult<
                ActionHostGitHubPullRequestFact>> GetCurrentPullRequestAsync(
                    string repositoryName,
                    long pullRequestNumber,
                    CancellationToken cancellationToken)
            {
                owner.Calls++;
                owner.OnRequest?.Invoke(owner.Calls, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (owner.Failure is { } failure)
                {
                    return Task.FromResult(ActionHostGitObjectResult<
                        ActionHostGitHubPullRequestFact>.Failed(failure));
                }

                var value = owner.OnCall?.Invoke(owner.Calls) ?? owner.Current;
                return Task.FromResult(ActionHostGitObjectResult<
                    ActionHostGitHubPullRequestFact>.Success(value, 64));
            }

            public Task<ActionHostGitObjectResult<
                ActionHostPullRequestFilePageObject>> GetPullRequestFilesAsync(
                    string repositoryName, long pullRequestNumber, int page,
                    int perPage, CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
                GetCommitObjectAsync(string repositoryName, string commitSha,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
                GetTreeObjectAsync(string repositoryName, string treeSha,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public Task<ActionHostGitObjectResult<
                ActionHostStreamedBlobObject>> CopyBlobObjectAsync(
                    string repositoryName, string blobSha, long declaredSize,
                    Stream destination,
                    CancellationToken cancellationToken) =>
                throw new NotSupportedException();

            public void Dispose() { }
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status,
        string json)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json)),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(
            "application/json") { CharSet = "utf-8" };
        return response;
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, HttpResponseMessage> callback) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(callback(request));
    }

    private sealed class ExpiringReadClock(int allowedReads) :
        IBoundedGitHubOperationClock
    {
        private int _reads;

        public TimeSpan Elapsed => ++_reads <= allowedReads
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(181);
    }
}
