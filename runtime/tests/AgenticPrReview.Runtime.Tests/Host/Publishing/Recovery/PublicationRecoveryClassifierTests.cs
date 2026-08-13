using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Tests.Host.Publishing.Recovery;

public sealed class PublicationRecoveryClassifierTests
{
    [Fact]
    public async Task D10MatrixMapsRealS6AndP2EvidenceToClosedActions()
    {
        Assert.Equal(PublicationRecoveryAction.NoPendingWork,
            (await EvaluateAsync(RecoveryState.Empty)).Action);
        var terminal = await EvaluateAsync(RecoveryState.Terminal);
        Assert.True(terminal.Action ==
            PublicationRecoveryAction.ReturnCommitted, terminal.ToString());
        Assert.Equal(PublicationRecoveryAction.CompleteAcceptance,
            (await EvaluateAsync(RecoveryState.ExactMarker)).Action);
        Assert.Equal(PublicationRecoveryAction.ResumeBeforeIntent,
            (await EvaluateAsync(RecoveryState.Candidate)).Action);
        Assert.Equal(PublicationRecoveryAction.StickyOutcomeUnknown,
            (await EvaluateAsync(RecoveryState.Intent)).Action);
        Assert.Equal(PublicationRecoveryAction.ResumeKnownNotWritten,
            (await EvaluateAsync(RecoveryState.KnownNotWritten)).Action);
        Assert.Equal(PublicationRecoveryAction.StickyOutcomeUnknown,
            (await EvaluateAsync(RecoveryState.OutcomeUnknown)).Action);
        Assert.Equal(PublicationRecoveryAction.CancelledBeforeSend,
            (await EvaluateAsync(RecoveryState.Cancelled)).Action);
        Assert.Equal(
            PublicationRecoveryAction.AuthorizationOrValidationFailure,
            (await EvaluateAsync(RecoveryState.AuthorizationFailure)).Action);
    }

    [Fact]
    public async Task ExternalCrashCutsAreReobservedThroughTheRealStores()
    {
        var candidate = await EvaluateAsync(RecoveryState.Candidate);
        var intent = await EvaluateAsync(RecoveryState.Intent);
        var sticky = await EvaluateAsync(RecoveryState.ExactMarker);
        var accepted = await EvaluateAsync(RecoveryState.Terminal);
        var cleaned = await EvaluateAsync(RecoveryState.Empty);

        Assert.Equal(PublicationRecoveryAction.ResumeBeforeIntent,
            candidate.Action);
        Assert.False(candidate.HasStickyAuthorization);
        Assert.True(intent.Action ==
            PublicationRecoveryAction.StickyOutcomeUnknown,
            intent.ToString());
        Assert.Equal(PublicationRecoveryAction.CompleteAcceptance,
            sticky.Action);
        Assert.Equal(PublicationRecoveryAction.ReturnCommitted,
            accepted.Action);
        Assert.Equal(PublicationRecoveryAction.NoPendingWork,
            cleaned.Action);
    }

    [Fact]
    public void MissingIncompleteOrForgedEvidenceAlwaysFailsClosed()
    {
        foreach (var marker in new[]
        {
            PublicationMarkerObservation.Incomplete,
            PublicationMarkerObservation.Ambiguous,
            PublicationMarkerObservation.Absent,
            PublicationMarkerObservation.Exact,
        })
        {
            Assert.Equal(
                PublicationRecoveryAction.Conflict,
                PublicationRecoveryClassifier.Classify(null, marker).Action);
        }

        var constructor = typeof(PublicationRecoveryObservation)
            .GetConstructors(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic)
            .Single();
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.IsValueType
                ? Activator.CreateInstance(parameter.ParameterType)
                : null)
            .ToArray();
        arguments[0] = new object();
        var thrown = Assert.Throws<
            System.Reflection.TargetInvocationException>(
                () => constructor.Invoke(arguments));
        Assert.IsType<ArgumentException>(thrown.InnerException);
    }

    [Fact]
    public async Task OnlyNoPendingWorkAllowsProviderAndOnlyExactFailureAuthorizesRetry()
    {
        var none = await EvaluateAsync(RecoveryState.Empty);
        var candidate = await EvaluateAsync(RecoveryState.Candidate);
        var retry = await EvaluateAsync(RecoveryState.KnownNotWritten);

        Assert.True(none.AllowsProvider);
        Assert.False(none.HasStickyAuthorization);
        Assert.False(candidate.AllowsProvider);
        Assert.False(candidate.HasStickyAuthorization);
        Assert.False(retry.AllowsProvider);
        Assert.True(retry.HasStickyAuthorization, retry.ToString());
    }

    [Fact]
    public async Task TerminalRecoveryRequiresExactDurableS6AndP2Proof()
    {
        var terminal = await EvaluateAsync(RecoveryState.Terminal);
        Assert.True(terminal.Action ==
            PublicationRecoveryAction.ReturnCommitted, terminal.ToString());
        Assert.Equal(PublicationRecoveryLifecycleState.CurrentTerminalRecovery,
            terminal.Lifecycle);
        Assert.False(terminal.AllowsProvider);

        var noPending = await EvaluateAsync(RecoveryState.Empty);
        Assert.Equal(PublicationRecoveryLifecycleState.None,
            noPending.Lifecycle);
        Assert.True(noPending.AllowsProvider);
    }

    private static async Task<RecoveryProbe> EvaluateAsync(
        RecoveryState state)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        using var context = fixture.Context;
        var factory = new FakePublisherTransportFactory();

        if (state == RecoveryState.Empty)
        {
            return await ClassifyAsync(fixture, factory);
        }

        if (state == RecoveryState.Terminal)
        {
            var accepted = await RetainedStateTransactionEndToEndTests
                .AcceptGenerationAsync(fixture, commentId: 901);
            context.Dispose();
            fixture = await RetainedStateTransactionEndToEndTests
                .RestoreFixtureAsync(fixture);
            using var recoveredContext = fixture.Context;
            Assert.True(PublicationRecoveryService.TryRestoreRendered(
                accepted.Publication,
                out var terminalRendered));
            var comment = StickyPublicationTestData.Comment(
                901,
                terminalRendered!.Comment);
            factory.Transport.Enqueue(comment);
            factory.Transport.Enqueue(comment);
            factory.Transport.Read = BoundedGitHubHttpResult<
                BoundedGitHubIssueComment>.Success(comment);
            var inventoryResult = await RestrictedStateService
                .ObserveRetainedPublicationRecoveryInventoryAsync(
                    recoveredContext,
                    CancellationToken.None);
            Assert.True(inventoryResult.Succeeded, inventoryResult.Code);
            var inventorySummary = RecoveryInventorySummary(
                recoveredContext,
                inventoryResult.Value!);
            var recoveryRecord = inventoryResult.Value!.Records.Single(record =>
            {
                Assert.True(RestrictedStateService
                    .TryCopyRetainedStateOpaquePayload(
                        recoveredContext,
                        record,
                        out var payload));
                return RecoveryRecordV1Codec.TryDecode(
                    payload.AsSpan(), out _, out _, out _);
            });
            using var extraction = PublicationRecoveryPersistence
                .CreateAcceptanceRecoveryExtraction(
                    recoveredContext,
                    recoveryRecord).Value;
            Assert.NotNull(extraction);
            var directMatch = await RestrictedStateService
                .MatchRecoveredRetainedStateAcceptanceAsync(
                    recoveredContext,
                    inventoryResult.Value
                        .CurrentAcceptanceCandidateObjectIdentity!,
                    recoveryRecord,
                    extraction!,
                    inventoryResult.Value.CurrentAcceptance!,
                    CancellationToken.None);
            Assert.True(directMatch.Succeeded, directMatch.Code);
            var observationResult = await PublicationRecoveryInventoryFactory
                .CreateAsync(
                    recoveredContext,
                    inventoryResult.Value!,
                    fixture.Invocation.PullRequest.HeadSha,
                    CancellationToken.None);
            Assert.True(observationResult.Succeeded,
                observationResult.Code + "; " + inventorySummary);
            observationResult.Value!.Dispose();
            return await ClassifyAsync(fixture, factory);
        }

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
        var candidateResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            candidateResult.Value);

        if (state == RecoveryState.Intent)
        {
            var beforeFactory = new FakePublisherTransportFactory();
            beforeFactory.Transport.Enqueue();
            var beforeService = new PublicationRecoveryService(
                new StickyCommentPublisher(beforeFactory));
            using var before = await beforeService.ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                context,
                CancellationToken.None);
            Assert.Equal(PublicationRecoveryAction.ResumeBeforeIntent,
                before.Decision.Action);
            Assert.Null(before.StickyWriteAuthorization);
            var persistedIntentResult = await PublicationRecoveryPersistence
                .PersistIntentAndAuthorizeAsync(
                    context,
                    before.Observation!,
                    CancellationToken.None);
            using var persistedIntent = Assert.IsType<
                PublicationIntentPersistenceResult>(
                    persistedIntentResult.Value);
            Assert.NotNull(persistedIntent.StickyWriteAuthorization);
            Assert.True(PublicationRecoveryService.TryRestoreRendered(
                persistedIntent.Observation.StoredPublication,
                out var persistedRendered));
            Assert.True(AuthorizedStickyPublicationRequest.TryCreateRecovery(
                fixture.Invocation,
                fixture.PublicationScope,
                persistedRendered,
                persistedIntent.Observation,
                persistedIntent.StickyWriteAuthorization,
                out var authorizedRequest));
            Assert.NotNull(authorizedRequest);
            Assert.False(AuthorizedStickyPublicationRequest.TryCreateRecovery(
                fixture.Invocation,
                fixture.PublicationScope,
                persistedRendered,
                persistedIntent.Observation,
                persistedIntent.StickyWriteAuthorization,
                out _));
        }
        else if (state is RecoveryState.KnownNotWritten or
            RecoveryState.OutcomeUnknown or
            RecoveryState.Cancelled or
            RecoveryState.AuthorizationFailure)
        {
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            var (outcome, reason) = state switch
            {
                RecoveryState.KnownNotWritten => (
                    BoundedGitHubPublisherOutcome.KnownNotWritten,
                    StickyPublicationReason.Deadline),
                RecoveryState.OutcomeUnknown => (
                    BoundedGitHubPublisherOutcome.OutcomeUnknown,
                    StickyPublicationReason.ReconciliationIncomplete),
                RecoveryState.Cancelled => (
                    BoundedGitHubPublisherOutcome.CancelledBeforeSend,
                    StickyPublicationReason.Cancelled),
                _ => (
                    BoundedGitHubPublisherOutcome
                        .AuthorizationOrValidationFailure,
                    StickyPublicationReason.AdmissionInvalid),
            };
            Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
                candidate,
                outcome,
                reason,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<RetainedStateOpaqueWriteAttempt>(
                attemptResult.Value);
            var recordResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    context,
                    attempt,
                    CancellationToken.None);
            using var record = Assert.IsType<RetainedStateOpaqueRecord>(
                recordResult.Value);
        }

        if (state == RecoveryState.ExactMarker)
        {
            Assert.True(PublicationRecoveryService.TryRestoreRendered(
                prepared.Publication,
                out var rendered));
            var comment = StickyPublicationTestData.Comment(
                902,
                rendered!.Comment);
            factory.Transport.Enqueue(comment);
            factory.Transport.Enqueue(comment);
            factory.Transport.Read = BoundedGitHubHttpResult<
                BoundedGitHubIssueComment>.Success(comment);
        }
        else
        {
            factory.Transport.Enqueue();
        }

        return await ClassifyAsync(fixture, factory);
    }

    private static async Task<RecoveryProbe> ClassifyAsync(
        RetainedStateTransactionEndToEndTests.TransactionFixture fixture,
        FakePublisherTransportFactory factory)
    {
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));
        using var evaluation = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            fixture.Context,
            CancellationToken.None);
        return new(
            evaluation.Decision.Action,
            evaluation.Decision.Lifecycle,
            evaluation.Decision.AllowsProvider,
            evaluation.StickyWriteAuthorization is not null,
            evaluation.DiscoveryKind,
            evaluation.DiscoveryReason,
            evaluation.Observation?.Anchors,
            evaluation.Observation?.Candidate is not null,
            evaluation.Observation?.Inventory?
                .CurrentAcceptancePublicationReceipt is not null,
            evaluation.Observation?.Intent is not null,
            evaluation.Observation?.Failure?.Outcome);
    }

    private static string RecoveryInventorySummary(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStatePublicationRecoveryInventory inventory)
    {
        var records = inventory.Records.Select(record =>
        {
            Assert.True(RestrictedStateService
                .TryCopyRetainedStateOpaquePayload(
                    context,
                    record,
                    out var payload));
            var kinds = new List<string>();
            if (StickyReadbackRecordV1Codec.TryDecode(
                payload.AsSpan(), out _)) kinds.Add("readback");
            if (RecoveryRecordV1Codec.TryDecode(
                payload.AsSpan(), out _, out _, out _)) kinds.Add("recovery");
            if (PublicationIntentV1Codec.TryDecode(
                payload.AsSpan(), out _)) kinds.Add("intent");
            return record.ObjectClass + ":" +
                record.Header.PredecessorIdentity + ":" +
                string.Join(',', kinds);
        });
        return "accepted=" +
            inventory.CurrentAcceptanceCandidateObjectIdentity +
            "; receipt=" +
            (inventory.CurrentAcceptancePublicationReceipt is not null) +
            "; records=" + string.Join('|', records);
    }

    private enum RecoveryState
    {
        Empty,
        Candidate,
        Intent,
        KnownNotWritten,
        OutcomeUnknown,
        Cancelled,
        AuthorizationFailure,
        ExactMarker,
        Terminal,
    }

    private sealed record RecoveryProbe(
        PublicationRecoveryAction Action,
        PublicationRecoveryLifecycleState Lifecycle,
        bool AllowsProvider,
        bool HasStickyAuthorization,
        StickyDiscoveryKind DiscoveryKind,
        StickyPublicationReason DiscoveryReason,
        PublicationRecoveryAnchorState? Anchors,
        bool CandidatePresent,
        bool AcceptanceReceiptPresent,
        bool IntentPresent,
        BoundedGitHubPublisherOutcome? FailureOutcome);
}
