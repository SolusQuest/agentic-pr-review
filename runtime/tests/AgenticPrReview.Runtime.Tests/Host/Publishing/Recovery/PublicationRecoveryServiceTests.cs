using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.Publishing.Rendering;

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
        var (token, request, rendered) = await
            StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        var comment = StickyPublicationTestData.Comment(
            77,
            rendered.Comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(comment);
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));

        var stored = Stored(request, rendered);
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            stored,
            out var restored));
        Assert.True(AuthorizedStickyReadbackRequest.TryCreateRecovery(
            request.Authorization,
            request.Scope,
            restored,
            out _));
        var result = await service.ClassifyBeforeProviderAsync(
            token,
            request.Authorization,
            request.Scope,
            stored,
            Current(),
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
        var (token, request, rendered) = await
            StickyPublicationTestData.CreateAsync();
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));

        var result = await service.ClassifyBeforeProviderAsync(
            token,
            request.Authorization,
            request.Scope,
            Stored(request, rendered),
            Current(),
            CancellationToken.None);

        Assert.Equal(
            PublicationRecoveryAction.ResumeBeforeIntent,
            result.Decision.Action);
        Assert.Null(result.ExactReadbackReceipt);
        Assert.Equal(1, factory.Transport.Lists);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
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

    private static PublicationRecoveryInventory Current() => new(
        EnumerationComplete: true,
        OwnershipRetained: true,
        CandidateCount: 1,
        CandidateMatchesCurrentHead: true,
        HasStoredValidatedPublication: true,
        IntentCount: 0,
        StickyReadbackCount: 0,
        FailureCount: 0,
        AbandonmentCount: 0,
        AcceptanceCount: 0,
        RecoveryCount: 0,
        RecordsMatchCandidate: true,
        AcceptanceMatchesRecovery: false,
        HasExactKnownNotWrittenFailure: false,
        HasOutcomeUnknownFailure: false,
        Marker: PublicationMarkerObservation.Incomplete,
        Anchors: PublicationRecoveryAnchorState.None);
}
