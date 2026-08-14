using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Tests.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;

public sealed class PublicationRecoveryServiceTests
{
    [Fact]
    public async Task RecoveryReadbackCanInspectAnExactStaleCandidatePayload()
    {
        var (_, request, _) = await StickyPublicationTestData.CreateAsync();
        var staleIdentity = new ReviewedIdentity(
                request.Authorization.PullRequest.RepositoryId.ToString(),
                request.Authorization.PullRequest.Number,
                ActionHostAuthorizationScenario.BaseSha,
                new string('d', 40));
        var stale = R4PublicationTestData.Render(
            identity: staleIdentity,
            scope: request.Scope);

        Assert.False(AuthorizedStickyReadbackRequest.TryCreate(
            request.Authorization,
            request.Scope,
            stale,
            out _));
        Assert.True(AuthorizedStickyReadbackRequest.TryCreateRecovery(
            request.Authorization,
            request.Scope,
            stale,
            out _));
    }

    [Fact]
    public async Task ExactStoredPayloadDiscoveryCompletesAcceptanceWithoutMutation()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await RetainedStateTransactionEndToEndTests
            .CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                context,
                run.Run,
                publication!,
                CancellationToken.None);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                prepared,
                CancellationToken.None);
        Assert.True(persistedResult.Succeeded, persistedResult.Code);
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            prepared.Publication,
            out var rendered));
        var factory = new FakePublisherTransportFactory();
        var comment = StickyPublicationTestData.Comment(
            77,
            rendered!.Comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(comment);
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));

        using var result = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            context,
            CancellationToken.None);

        Assert.Equal(StickyDiscoveryKind.ExactTarget, result.DiscoveryKind);
        Assert.Equal(
            PublicationRecoveryAction.CompleteAcceptance,
            result.Decision.Action);
        Assert.NotNull(result.ExactReadbackReceipt);
        Assert.Equal(2, factory.Transport.Lists);
        Assert.Equal(1, factory.Transport.Reads);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Fact]
    public async Task CompleteAbsenceResumesBeforeIntentWithoutMutation()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await RetainedStateTransactionEndToEndTests
            .CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                context,
                run.Run,
                publication!,
                CancellationToken.None);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                prepared,
                CancellationToken.None);
        Assert.True(persistedResult.Succeeded, persistedResult.Code);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));

        using var result = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            context,
            CancellationToken.None);

        Assert.Equal(
            PublicationRecoveryAction.ResumeBeforeIntent,
            result.Decision.Action);
        Assert.Null(result.ExactReadbackReceipt);
        Assert.Equal(1, factory.Transport.Lists);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Fact]
    public void FreshObservationRequiresEveryImmutableReceiptField()
    {
        var durable = Receipt(StickyPublicationOperation.Create);
        var observed = Receipt(StickyPublicationOperation.Observed);

        Assert.True(PublicationReceiptMatcher.IsFreshObservationOf(
            durable,
            observed));
        Assert.False(PublicationReceiptMatcher.AreDurablyEqual(
            durable,
            observed));
        Assert.True(PublicationReceiptMatcher.AreDurablyEqual(
            durable,
            Receipt(StickyPublicationOperation.Create)));
        Assert.False(PublicationReceiptMatcher.IsFreshObservationOf(
            durable,
            Receipt(StickyPublicationOperation.Update)));

        var mismatches = new[]
        {
            Receipt(StickyPublicationOperation.Observed, repositoryId: 2),
            Receipt(StickyPublicationOperation.Observed, pullRequest: 4),
            Receipt(StickyPublicationOperation.Observed, commentId: 6),
            Receipt(
                StickyPublicationOperation.Observed,
                commentUrl:
                    "https://github.com/other/repo/pull/3" +
                    "#issuecomment-5"),
            Receipt(
                StickyPublicationOperation.Observed,
                scopeSha256: new string('d', 64)),
            Receipt(
                StickyPublicationOperation.Observed,
                bodySha256: new string('e', 64)),
            Receipt(
                StickyPublicationOperation.Observed,
                headSha: new string('f', 40)),
        };
        Assert.All(mismatches, mismatch =>
            Assert.False(PublicationReceiptMatcher.IsFreshObservationOf(
                durable,
                mismatch)));
    }

    private static ValidatedPublicationPayloadV1 Stored(
        AuthorizedStickyPublicationRequest request,
        AgenticPrReview.Runtime.Host.Publishing.Rendering
            .R4RenderedStickyComment rendered)
    {
        Assert.True(ValidatedPublicationPayloadV1.TryCreate(
            rendered.Comment,
            request.Authorization.PullRequest.RepositoryId,
            ActionHostAuthorizationScenario.RepositoryName,
            request.Authorization.PullRequest.Number,
            R4PublicationTestData.PolicySha256,
            new string('a', 64),
            "runtime-payload-v1",
            AcceptedStateFormat.RenderingVersion,
            out var stored));
        return stored!;
    }

    private static StickyCommentPublisher.StickyPublicationReceipt Receipt(
        StickyPublicationOperation operation,
        long repositoryId = 1,
        long pullRequest = 3,
        long commentId = 5,
        string? commentUrl = null,
        string? scopeSha256 = null,
        string? bodySha256 = null,
        string? headSha = null)
    {
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                operation,
                repositoryId,
                pullRequest,
                commentId,
                commentUrl ??
                    $"https://github.com/owner/repo/pull/{pullRequest}" +
                    $"#issuecomment-{commentId}",
                scopeSha256 ?? new string('a', 64),
                bodySha256 ?? new string('b', 64),
                headSha ?? new string('c', 40),
                out var receipt));
        return receipt!;
    }

}
