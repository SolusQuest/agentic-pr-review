using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
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
        Assert.Equal(PublicationRecoveryAction.CompleteAcceptance,
            (await EvaluateAsync(
                RecoveryState.OutcomeUnknownWithExactMarker)).Action);
        Assert.Equal(PublicationRecoveryAction.Conflict,
            (await EvaluateAsync(
                RecoveryState.KnownNotWrittenWithExactMarker)).Action);
        Assert.Equal(PublicationRecoveryAction.CancelledBeforeSend,
            (await EvaluateAsync(RecoveryState.Cancelled)).Action);
        Assert.Equal(
            PublicationRecoveryAction.AuthorizationOrValidationFailure,
            (await EvaluateAsync(RecoveryState.AuthorizationFailure)).Action);
        var expired = await EvaluateAsync(
            RecoveryState.ExpiredOutcomeUnknown);
        Assert.Equal(PublicationRecoveryAction.StickyOutcomeUnknown,
            expired.Action);
        Assert.False(expired.AllowsProvider);
        Assert.True(expired.CandidatePresent);
        Assert.True(expired.IntentPresent);
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            expired.FailureOutcome);
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
        Assert.True(retry.StickyAuthorizationConsumedOnce, retry.ToString());
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

    [Fact]
    public async Task AcceptedOutcomeUnknownConvergesOnExactMarkerAfterRestart()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var accepted = await RetainedStateTransactionEndToEndTests
            .AcceptGenerationAsync(
                fixture,
                commentId: 903,
                persistOutcomeUnknownFailure: true);
        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            accepted.Publication,
            out var rendered));
        var exact = StickyPublicationTestData.Comment(903, rendered!.Comment);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(exact);
        factory.Transport.Enqueue(exact);
        factory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(exact);

        using var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(factory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);

        Assert.Equal(PublicationRecoveryAction.ReturnCommitted,
            recovery.Decision.Action);
        Assert.False(recovery.Decision.AllowsProvider);
        Assert.Equal(BoundedGitHubPublisherOutcome.OutcomeUnknown,
            recovery.Observation!.Failure!.Outcome);
        var receipt = Assert.IsType<
            StickyCommentPublisher.StickyPublicationReceipt>(
                recovery.Observation.Inventory!
                    .CurrentAcceptancePublicationReceipt);
        Assert.Equal(903, receipt.CommentId);
        Assert.Equal(accepted.Publication.ScopeSha256, receipt.ScopeSha256);
        Assert.Equal(accepted.Publication.BodySha256, receipt.BodySha256);
        Assert.Equal(accepted.Publication.ReviewedHeadSha, receipt.HeadSha);
        Assert.Equal(
            StickyPublicationOperation.Observed,
            recovery.ExactReadbackReceipt!.Operation);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public async Task SupersededOutcomeUnknownCleanupIsRestartSafeBeforeProvider(
        int completedTargetCount)
    {
        var reviewedHead = new string('f', 40);
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync(
                route: ActionHostAuthorizationRoute.WorkflowDispatch);
        var acceptedHead = fixture.Invocation.PullRequest.HeadSha;
        _ = await RetainedStateTransactionEndToEndTests
            .AcceptGenerationAsync(
                fixture,
                commentId: 904,
                persistOutcomeUnknownFailure: true);
        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: reviewedHead,
                ancestryPreviousHeadSha: acceptedHead);
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(new FakePublisherTransportFactory()));
        var storeDeleteCount = 0;
        System.Action? afterDelete = null;
        afterDelete = () =>
        {
            storeDeleteCount++;
            if (storeDeleteCount == completedTargetCount * 2)
            {
                if (completedTargetCount < 6)
                {
                    fixture.Store.BeforeUpload = static () =>
                        throw new SimulatedProcessCrashException();
                }

                return;
            }

            fixture.Store.AfterDelete = afterDelete;
        };
        fixture.Store.AfterDelete = afterDelete;

        using (var cleanupOnly = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            fixture.Context,
            CancellationToken.None))
        {
            Assert.NotNull(cleanupOnly.Observation);
            Assert.Equal(
                PublicationRecoveryAction.CleanupSupersededRecovery,
                cleanupOnly.Decision.Action);
            Assert.False(cleanupOnly.Decision.AllowsProvider);
            Assert.True(cleanupOnly.Decision.AllowsSupersededCleanup);
            Assert.Equal(4, cleanupOnly.Observation!.HistoricalRecords.Length);
            Assert.Equal(2, cleanupOnly.Observation.CompletedAnchors.Length);
            if (completedTargetCount < 6)
            {
                await Assert.ThrowsAsync<SimulatedProcessCrashException>(() =>
                    PublicationRecoveryService
                        .CleanupHistoricalRecoveryRecordsAsync(
                            fixture.Invocation,
                            fixture.Context,
                            cleanupOnly,
                            CancellationToken.None));
            }
            else
            {
                var completed = await PublicationRecoveryService
                    .CleanupHistoricalRecoveryRecordsAsync(
                        fixture.Invocation,
                        fixture.Context,
                        cleanupOnly,
                        CancellationToken.None);
                Assert.True(completed.Completed, completed.Code);
            }
        }
        Assert.Equal(completedTargetCount * 2, storeDeleteCount);

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: reviewedHead,
                ancestryPreviousHeadSha: acceptedHead);
        if (completedTargetCount < 6)
        {
            using (var resumed = await service.ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None))
            {
                Assert.Equal(
                    PublicationRecoveryAction.CleanupSupersededRecovery,
                    resumed.Decision.Action);
                Assert.False(resumed.Decision.AllowsProvider);
                var cleanup = await PublicationRecoveryService
                    .CleanupHistoricalRecoveryRecordsAsync(
                        fixture.Invocation,
                        fixture.Context,
                        resumed,
                        CancellationToken.None);
                Assert.True(cleanup.Completed, cleanup.Code);
            }
        }
        else
        {
            using var resumed = await service.ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None);
            Assert.Equal(
                PublicationRecoveryAction.NoPendingWork,
                resumed.Decision.Action);
            Assert.True(resumed.Decision.AllowsProvider);
        }

        using (var cleared = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            fixture.Context,
            CancellationToken.None))
        {
            Assert.Equal(
                PublicationRecoveryAction.NoPendingWork,
                cleared.Decision.Action);
            Assert.True(cleared.Decision.AllowsProvider);
            Assert.False(cleared.Decision.AllowsSupersededCleanup);
            Assert.Empty(cleared.Observation!.HistoricalRecords);
            Assert.Empty(cleared.Observation.CompletedAnchors);
        }

        var (prepared, _) = await PersistCandidateAsync(fixture);
        prepared.Dispose();
        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: reviewedHead,
                ancestryPreviousHeadSha: acceptedHead);
        using var processB = fixture.Context;
        var successorFactory = new FakePublisherTransportFactory();
        successorFactory.Transport.Enqueue();
        using var successor = await new PublicationRecoveryService(
                new StickyCommentPublisher(successorFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.ResumeBeforeIntent,
            successor.Decision.Action);
        Assert.Empty(successor.Observation!.HistoricalRecords);
        Assert.Empty(successor.Observation.CompletedAnchors);
        var intentResult = await PublicationRecoveryPersistence
            .PersistIntentAndAuthorizeAsync(
                processB,
                successor.Observation,
                CancellationToken.None);
        Assert.True(intentResult.Succeeded, intentResult.Code);
        using var intent = Assert.IsType<PublicationIntentPersistenceResult>(
            intentResult.Value);
        Assert.Single(intent.Observation.Records);
        Assert.Equal(0, successorFactory.Transport.Creates);
        Assert.Equal(0, successorFactory.Transport.Updates);
    }

    [Fact]
    public async Task ContinuationWithoutReadbackUsesFreshCandidateReceipt()
    {
        var continuation = await CreateContinuationCandidateAsync(
            acceptedCommentId: 910);
        using var prepared = continuation.Prepared;
        using var context = continuation.Fixture.Context;
        var (factory, _) = MarkerFactory(
            prepared,
            commentId: 911,
            exact: true);

        using var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(factory))
            .ClassifyBeforeProviderAsync(
                continuation.Fixture.Launch.Inputs.GitHubToken!,
                continuation.Fixture.Invocation,
                continuation.Fixture.PublicationScope,
                context,
                CancellationToken.None);

        Assert.Equal(
            PublicationRecoveryAction.CompleteAcceptance,
            recovery.Decision.Action);
        var receipt = Assert.IsType<
            StickyCommentPublisher.StickyPublicationReceipt>(
                recovery.ExactReadbackReceipt);
        Assert.Equal(StickyPublicationOperation.Observed, receipt.Operation);
        Assert.Equal(911, receipt.CommentId);
        Assert.Equal(prepared.Publication.ScopeSha256, receipt.ScopeSha256);
        Assert.Equal(prepared.Publication.BodySha256, receipt.BodySha256);
        Assert.Equal(
            prepared.Publication.ReviewedHeadSha,
            receipt.HeadSha);
        Assert.Equal(
            910,
            recovery.Observation!.Inventory!
                .CurrentAcceptancePublicationReceipt!.CommentId);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Fact]
    public async Task ContinuationOutcomeUnknownConvergesAcrossAcceptanceRestart()
    {
        var continuation = await CreateContinuationCandidateAsync(
            acceptedCommentId: 920);
        using var prepared = continuation.Prepared;
        await PersistOutcomeUnknownAsync(
            continuation.Fixture,
            prepared,
            continuation.Candidate);
        continuation.Fixture.Context.Dispose();
        var fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(continuation.Fixture);
        var (readbackFactory, exactComment) = MarkerFactory(
            prepared,
            commentId: 921,
            exact: true);
        using (var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(readbackFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None))
        {
            Assert.Equal(
                PublicationRecoveryAction.CompleteAcceptance,
                recovery.Decision.Action);
            Assert.Equal(
                StickyPublicationOperation.Observed,
                recovery.ExactReadbackReceipt!.Operation);
            Assert.Equal(921, recovery.ExactReadbackReceipt.CommentId);
            Assert.Equal(
                BoundedGitHubPublisherOutcome.OutcomeUnknown,
                recovery.Observation!.Failure!.Outcome);
            _ = await RetainedStateTransactionEndToEndTests
                .AcceptRecoveredCandidateAsync(fixture, recovery);
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processC = fixture.Context;
        var committedFactory = ExactMarkerFactory(exactComment);
        using var committed = await new PublicationRecoveryService(
                new StickyCommentPublisher(committedFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processC,
                CancellationToken.None);

        Assert.Equal(
            PublicationRecoveryAction.ReturnCommitted,
            committed.Decision.Action);
        Assert.False(committed.Decision.AllowsProvider);
        Assert.Equal(
            StickyPublicationOperation.Observed,
            committed.ExactReadbackReceipt!.Operation);
        Assert.Equal(921, committed.ExactReadbackReceipt.CommentId);
        Assert.Equal(0, readbackFactory.Transport.Creates);
        Assert.Equal(0, readbackFactory.Transport.Updates);
        Assert.Equal(0, committedFactory.Transport.Creates);
        Assert.Equal(0, committedFactory.Transport.Updates);

        var cleanup = await PublicationRecoveryService
            .CleanupHistoricalRecoveryRecordsAsync(
                fixture.Invocation,
                processC,
                committed,
                CancellationToken.None);
        Assert.True(cleanup.Completed, cleanup.Code);
        var cleanedInventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                processC,
                CancellationToken.None);
        using (var cleanedInventory = Assert.IsType<
                RetainedStatePublicationRecoveryInventory>(
            cleanedInventoryResult.Value))
        {
            Assert.Empty(cleanedInventory.Records);
            Assert.Equal(
                921,
                cleanedInventory.CurrentAcceptancePublicationReceipt!
                    .CommentId);
        }
        var afterCleanupFactory = ExactMarkerFactory(exactComment);
        using var afterCleanup = await new PublicationRecoveryService(
                new StickyCommentPublisher(afterCleanupFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processC,
                CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.ReturnCommitted,
            afterCleanup.Decision.Action);
        Assert.Equal(
            StickyPublicationOperation.Observed,
            afterCleanup.ExactReadbackReceipt!.Operation);
        Assert.Equal(0, afterCleanupFactory.Transport.Creates);
        Assert.Equal(0, afterCleanupFactory.Transport.Updates);
    }

    [Fact]
    public async Task ContinuationWithoutReadbackRejectsNonExactMarker()
    {
        var continuation = await CreateContinuationCandidateAsync(
            acceptedCommentId: 930);
        using var prepared = continuation.Prepared;
        using var context = continuation.Fixture.Context;
        var (factory, _) = MarkerFactory(
            prepared,
            commentId: 931,
            exact: false);

        using var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(factory))
            .ClassifyBeforeProviderAsync(
                continuation.Fixture.Launch.Inputs.GitHubToken!,
                continuation.Fixture.Invocation,
                continuation.Fixture.PublicationScope,
                context,
                CancellationToken.None);

        Assert.Equal(
            PublicationRecoveryAction.Conflict,
            recovery.Decision.Action);
        Assert.Null(recovery.ExactReadbackReceipt);
        Assert.False(recovery.Decision.AllowsProvider);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBResumesExactTargetOrFailsClosedAtMissingTarget(
        bool processAWroteTarget)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        using (prepared)
        {
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
                candidate,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            if (processAWroteTarget)
            {
                var recordResult = await RestrictedStateService
                    .PersistPreparedRetainedOpaqueWriteAsync(
                        fixture.Context,
                        attempt,
                        CancellationToken.None);
                using var record = Assert.IsType<RetainedStateOpaqueRecord>(
                    recordResult.Value);
            }
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));
        using (var recovery = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            processB,
            CancellationToken.None))
        {
            if (!processAWroteTarget)
            {
                Assert.Equal(PublicationRecoveryAction.Conflict,
                    recovery.Decision.Action);
                Assert.False(recovery.Decision.AllowsProvider);
                return;
            }

            Assert.Equal(PublicationRecoveryAction.ResumeAnchoredWrite,
                recovery.Decision.Action);
            var resumed = await PublicationRecoveryService
                .ResumeInterruptedWriteAsync(
                    processB,
                    recovery,
                    CancellationToken.None);
            Assert.True(resumed.Succeeded, resumed.Code);
            using var record = Assert.IsType<RetainedStateOpaqueRecord>(
                resumed.Value);
        }

        var afterFactory = new FakePublisherTransportFactory();
        afterFactory.Transport.Enqueue();
        var afterService = new PublicationRecoveryService(
            new StickyCommentPublisher(afterFactory));
        using var after = await afterService.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            processB,
            CancellationToken.None);
        Assert.Equal(PublicationRecoveryAction.StickyOutcomeUnknown,
            after.Decision.Action);
        Assert.Equal(PublicationRecoveryAnchorState.None,
            after.Observation!.Anchors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBResumesCleanupBeforeOrAfterTargetDeletion(
        bool processADeletedTarget)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        using (prepared)
        {
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
                candidate,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            var persistedResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            using var persisted = Assert.IsType<RetainedStateOpaqueRecord>(
                persistedResult.Value);
            if (processADeletedTarget)
            {
                fixture.Store.AfterDelete = static () =>
                    throw new SimulatedProcessCrashException();
            }
            else
            {
                fixture.Store.BeforeDelete = static () =>
                    throw new SimulatedProcessCrashException();
            }

            await Assert.ThrowsAsync<SimulatedProcessCrashException>(() =>
                PublicationRecoveryPersistence
                    .CleanupCompletedWriteAnchorAsync(
                        fixture.Context,
                        attempt,
                        CancellationToken.None));
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        var inventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                processB,
                CancellationToken.None);
        using (var inventory = inventoryResult.Value)
        {
            Assert.True(inventoryResult.Succeeded, inventoryResult.Code);
            var observationResult = await PublicationRecoveryInventoryFactory
                .CreateAsync(
                    processB,
                    inventory!,
                    fixture.Invocation.PullRequest.HeadSha,
                    CancellationToken.None);
            using var observation = observationResult.Value;
            Assert.True(observationResult.Succeeded, observationResult.Code);
            Assert.Single(observation!.CleanupRecords);
        }
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(new FakePublisherTransportFactory()));
        using (var recovery = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            processB,
            CancellationToken.None))
        {
            Assert.Equal(PublicationRecoveryAction.ResumeCleanup,
                recovery.Decision.Action);
            var cleanup = await PublicationRecoveryService
                .ResumeInterruptedCleanupAsync(
                    processB,
                    recovery,
                    CancellationToken.None);
            Assert.True(cleanup.Completed, cleanup.Code);
        }

        var afterFactory = new FakePublisherTransportFactory();
        afterFactory.Transport.Enqueue();
        using var after = await new PublicationRecoveryService(
                new StickyCommentPublisher(afterFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);
        Assert.Equal(PublicationRecoveryAction.StickyOutcomeUnknown,
            after.Decision.Action);
        Assert.True(after.Observation!.CleanupRecords.IsEmpty);
        Assert.Equal(PublicationRecoveryAnchorState.None,
            after.Observation.Anchors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrphanedRecordOrTargetMissingAnchorFailsClosedInProcessB(
        bool targetWasPersisted)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        using (prepared)
        {
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
                candidate,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            if (targetWasPersisted)
            {
                var persistedResult = await RestrictedStateService
                    .PersistPreparedRetainedOpaqueWriteAsync(
                        fixture.Context,
                        attempt,
                        CancellationToken.None);
                using var persisted = Assert.IsType<
                    RetainedStateOpaqueRecord>(persistedResult.Value);
                var cleanup = await PublicationRecoveryPersistence
                    .CleanupCompletedWriteAnchorAsync(
                        fixture.Context,
                        attempt,
                        CancellationToken.None);
                Assert.True(cleanup.Completed, cleanup.Code);
            }

            var removed = await fixture.Store.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(candidate.Metadata),
                CancellationToken.None);
            Assert.True(removed.Succeeded);
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        var factory = new FakePublisherTransportFactory();
        using var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(factory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);
        Assert.Equal(PublicationRecoveryAction.Conflict,
            recovery.Decision.Action);
        Assert.False(recovery.Decision.AllowsProvider);
        Assert.Equal(0, factory.Transport.Lists);
    }

    [Fact]
    public async Task FailureWithoutIntentFailsClosedWithoutPublisherAccess()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        using (prepared)
        {
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
                candidate,
                BoundedGitHubPublisherOutcome.KnownNotWritten,
                StickyPublicationReason.Deadline,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            var persistedResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            using var persisted = Assert.IsType<RetainedStateOpaqueRecord>(
                persistedResult.Value);
            var anchorCleanup = await PublicationRecoveryPersistence
                .CleanupCompletedWriteAnchorAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            Assert.True(anchorCleanup.Completed, anchorCleanup.Code);
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        var factory = new FakePublisherTransportFactory();
        using var recovery = await new PublicationRecoveryService(
                new StickyCommentPublisher(factory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);

        Assert.Equal(PublicationRecoveryAction.Conflict,
            recovery.Decision.Action);
        Assert.False(recovery.Decision.AllowsProvider);
        Assert.Null(recovery.StickyWriteAuthorization);
        Assert.Equal(0, factory.Transport.Lists);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
    }

    [Fact]
    public async Task ProcessBReconcilesStaleAbandonmentBeforeAnchorCleanup()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync(
                route: ActionHostAuthorizationRoute.WorkflowDispatch);
        var (prepared, _) = await PersistCandidateAsync(fixture);
        prepared.Dispose();
        fixture.Context.Dispose();
        var newHead = new string('b', 40);
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: newHead);

        var processAFactory = new FakePublisherTransportFactory();
        processAFactory.Transport.Enqueue();
        var processAService = new PublicationRecoveryService(
            new StickyCommentPublisher(processAFactory));
        using (var stale = await processAService.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            fixture.Context,
            CancellationToken.None))
        {
            Assert.Equal(PublicationRecoveryAction.AbandonStaleCandidate,
                stale.Decision.Action);
            using var abandonmentAuthorization =
                PublicationRecoveryInventoryFactory
                    .CreateStaleAbandonmentAuthorization(
                        stale.Observation!,
                        stale.MarkerAbsenceEvidence!);
            var observedCandidate = stale.Observation!.Candidate!;
            var ownershipResult = await RestrictedStateService
                .AuthorizeRetainedStaleAbandonmentOwnershipAsync(
                    fixture.Context,
                    stale.Observation,
                    observedCandidate,
                    abandonmentAuthorization,
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence
                .TryCreateStaleAbandonmentWrite(
                    stale.Observation,
                    abandonmentAuthorization,
                    stale.Observation.ObservedAtUnixSeconds,
                    out _,
                    out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            var persistedResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            using var persisted = Assert.IsType<RetainedStateOpaqueRecord>(
                persistedResult.Value);
            Assert.Equal(StateObjectClass.Abandonment,
                persisted.ObjectClass);
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: newHead);
        using var processB = fixture.Context;
        var processBService = new PublicationRecoveryService(
            new StickyCommentPublisher(new FakePublisherTransportFactory()));
        using (var recovery = await processBService
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None))
        {
            Assert.Equal(PublicationRecoveryAction.ResumeAnchoredWrite,
                recovery.Decision.Action);
            var resumed = await PublicationRecoveryService
                .ResumeInterruptedWriteAsync(
                    processB,
                    recovery,
                    CancellationToken.None);
            Assert.True(resumed.Succeeded, resumed.Code);
            using var record = Assert.IsType<RetainedStateOpaqueRecord>(
                resumed.Value);
            Assert.Equal(StateObjectClass.Abandonment,
                record.ObjectClass);
        }

        var afterFactory = new FakePublisherTransportFactory();
        afterFactory.Transport.Enqueue();
        using var after = await new PublicationRecoveryService(
                new StickyCommentPublisher(afterFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);
        Assert.Equal(PublicationRecoveryAction.ResumeStaleCleanup,
            after.Decision.Action);
        Assert.Equal(PublicationRecoveryAnchorState.None,
            after.Observation!.Anchors);
        Assert.NotNull(after.Observation.Abandonment);
        Assert.Single(after.Observation.Records);
        Assert.True(after.Observation.CleanupRecords.IsEmpty);
        Assert.Equal(0, afterFactory.Transport.Creates);
        Assert.Equal(0, afterFactory.Transport.Updates);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessBResumesStaleCleanupWithBothOrOneTargetRemaining(
        bool processADeletedCandidate)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync(
                route: ActionHostAuthorizationRoute.WorkflowDispatch);
        var (prepared, _) = await PersistCandidateAsync(fixture);
        prepared.Dispose();
        fixture.Context.Dispose();
        var newHead = new string('b', 40);
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: newHead);

        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Enqueue();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));
        using (var stale = await service.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            fixture.Context,
            CancellationToken.None))
        {
            Assert.Equal(PublicationRecoveryAction.AbandonStaleCandidate,
                stale.Decision.Action);
            var initialDeleteCount = fixture.Store.DeleteCalls;
            System.Action? afterDelete = null;
            afterDelete = () =>
            {
                if (fixture.Store.DeleteCalls == initialDeleteCount + 2)
                {
                    if (processADeletedCandidate)
                    {
                        fixture.Store.AfterDelete = static () =>
                            throw new SimulatedProcessCrashException();
                    }
                    else
                    {
                        fixture.Store.BeforeDelete = static () =>
                            throw new SimulatedProcessCrashException();
                    }
                }
                else
                {
                    fixture.Store.AfterDelete = afterDelete;
                }
            };
            fixture.Store.AfterDelete = afterDelete;

            await Assert.ThrowsAsync<SimulatedProcessCrashException>(() =>
                service.AbandonAndCleanupStaleCandidateAsync(
                    fixture.Launch.Inputs.GitHubToken!,
                    fixture.Invocation,
                    fixture.PublicationScope,
                    fixture.Context,
                    stale,
                    CancellationToken.None));
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(
                fixture,
                newWorkflowRun: true,
                reviewedHeadSha: newHead);
        using var processB = fixture.Context;
        var recoveryService = new PublicationRecoveryService(
            new StickyCommentPublisher(new FakePublisherTransportFactory()));
        using (var recovery = await recoveryService
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None))
        {
            Assert.Equal(PublicationRecoveryAction.ResumeCleanup,
                recovery.Decision.Action);
            var cleanup = await PublicationRecoveryService
                .ResumeInterruptedCleanupAsync(
                    processB,
                    recovery,
                    CancellationToken.None);
            Assert.True(cleanup.Completed, cleanup.Code);
        }

        using var after = await recoveryService.ClassifyBeforeProviderAsync(
            fixture.Launch.Inputs.GitHubToken!,
            fixture.Invocation,
            fixture.PublicationScope,
            processB,
            CancellationToken.None);
        Assert.Equal(PublicationRecoveryAction.NoPendingWork,
            after.Decision.Action);
        Assert.True(after.Decision.AllowsProvider);
    }

    [Fact]
    public async Task DurableStickyReceiptPinsCommentIdAcrossProcessRestart()
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync();
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        string exactBody;
        using (prepared)
        {
            var initialFactory = new FakePublisherTransportFactory();
            initialFactory.Transport.Enqueue();
            var initialService = new PublicationRecoveryService(
                new StickyCommentPublisher(initialFactory));
            using var before = await initialService.ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None);
            var intentResult = await PublicationRecoveryPersistence
                .PersistIntentAndAuthorizeAsync(
                    fixture.Context,
                    before.Observation!,
                    CancellationToken.None);
            using var intent = Assert.IsType<
                PublicationIntentPersistenceResult>(intentResult.Value);
            Assert.True(PublicationRecoveryService.TryRestoreRendered(
                intent.Observation.StoredPublication,
                out var rendered));
            exactBody = rendered!.Comment;
            Assert.True(AuthorizedStickyPublicationRequest.TryCreateRecovery(
                fixture.Invocation,
                fixture.PublicationScope,
                rendered,
                intent.Observation,
                intent.StickyWriteAuthorization,
                out var authorized));
            Assert.NotNull(authorized);

            var originalComment = StickyPublicationTestData.Comment(
                99,
                rendered!.Comment);
            Assert.True(StickyCommentPublisher.StickyPublicationReceipt
                .TryRehydrate(
                    StickyPublicationOperation.Observed,
                    fixture.Invocation.PullRequest.RepositoryId,
                    fixture.Invocation.PullRequest.Number,
                    originalComment.Id,
                    originalComment.HtmlUrl,
                    rendered.Identity.ScopeSha256,
                    rendered.Identity.BodySha256,
                    rendered.Identity.HeadSha,
                    out var receipt));
            Assert.NotNull(receipt);
            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    intent.Observation.Records,
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            Assert.True(PublicationRecoveryPersistence
                .TryCreateStickyReadbackWrite(
                    candidate,
                    receipt!,
                    fixture.Time.UnixSeconds,
                    prepared.Header.LogicalExpiresAtUnixSeconds,
                    out _,
                    out var request));
            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    fixture.Context,
                    ownership,
                    request!,
                    CancellationToken.None);
            using var attempt = Assert.IsType<
                RetainedStateOpaqueWriteAttempt>(attemptResult.Value);
            var persistedResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            using var persisted = Assert.IsType<RetainedStateOpaqueRecord>(
                persistedResult.Value);
            var anchorCleanup = await PublicationRecoveryPersistence
                .CleanupCompletedWriteAnchorAsync(
                    fixture.Context,
                    attempt,
                    CancellationToken.None);
            Assert.True(anchorCleanup.Completed, anchorCleanup.Code);
        }

        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        using var processB = fixture.Context;
        var replacementFactory = new FakePublisherTransportFactory();
        replacementFactory.Transport.Enqueue(
            StickyPublicationTestData.Comment(100,
                exactBody));
        using var replacement = await new PublicationRecoveryService(
                new StickyCommentPublisher(replacementFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                processB,
                CancellationToken.None);
        Assert.Equal(StickyDiscoveryKind.StaleTarget,
            replacement.DiscoveryKind);
        Assert.Equal(PublicationRecoveryAction.Conflict,
            replacement.Decision.Action);
        Assert.Equal(0, replacementFactory.Transport.Reads);
    }

    private static async Task<(
        RetainedStateTransactionEndToEndTests.TransactionFixture Fixture,
        RetainedStatePreparedCandidate Prepared,
        RetainedStatePersistedCandidate Candidate)>
        CreateContinuationCandidateAsync(long acceptedCommentId)
    {
        var fixture = await RetainedStateTransactionEndToEndTests
            .CreateFixtureAsync(
                route: ActionHostAuthorizationRoute.WorkflowDispatch);
        var accepted = await RetainedStateTransactionEndToEndTests
            .AcceptGenerationAsync(fixture, acceptedCommentId);
        fixture.Context.Dispose();
        fixture = await RetainedStateTransactionEndToEndTests
            .RestoreFixtureAsync(fixture);
        var cleanupFactory = new FakePublisherTransportFactory();
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            accepted.Publication,
            out var acceptedRendered));
        var acceptedComment = StickyPublicationTestData.Comment(
            acceptedCommentId,
            acceptedRendered!.Comment);
        cleanupFactory.Transport.Enqueue(acceptedComment);
        cleanupFactory.Transport.Enqueue(acceptedComment);
        cleanupFactory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(acceptedComment);
        using (var terminal = await new PublicationRecoveryService(
                new StickyCommentPublisher(cleanupFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None))
        {
            Assert.Equal(
                PublicationRecoveryAction.ReturnCommitted,
                terminal.Decision.Action);
            Assert.Equal(
                PublicationRecoveryLifecycleState.CurrentTerminalRecovery,
                terminal.Decision.Lifecycle);
            Assert.Equal(2, terminal.Observation!.HistoricalRecords.Length);
            var cleanup = await PublicationRecoveryService
                .CleanupHistoricalRecoveryRecordsAsync(
                    fixture.Invocation,
                    fixture.Context,
                    terminal,
                    CancellationToken.None);
            Assert.True(cleanup.Completed, cleanup.Code);
        }
        Assert.Equal(0, cleanupFactory.Transport.Creates);
        Assert.Equal(0, cleanupFactory.Transport.Updates);
        var inventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                fixture.Context,
                CancellationToken.None);
        using (var inventory = Assert.IsType<
                RetainedStatePublicationRecoveryInventory>(
            inventoryResult.Value))
        {
            Assert.Empty(inventory.Records);
            Assert.Equal(
                acceptedCommentId,
                inventory.CurrentAcceptancePublicationReceipt!.CommentId);
        }
        var (prepared, candidate) = await PersistCandidateAsync(fixture);
        return (fixture, prepared, candidate);
    }

    private static async Task PersistOutcomeUnknownAsync(
        RetainedStateTransactionEndToEndTests.TransactionFixture fixture,
        RetainedStatePreparedCandidate prepared,
        RetainedStatePersistedCandidate candidate)
    {
        var beforeFactory = new FakePublisherTransportFactory();
        beforeFactory.Transport.Enqueue();
        using var before = await new PublicationRecoveryService(
                new StickyCommentPublisher(beforeFactory))
            .ClassifyBeforeProviderAsync(
                fixture.Launch.Inputs.GitHubToken!,
                fixture.Invocation,
                fixture.PublicationScope,
                fixture.Context,
                CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.ResumeBeforeIntent,
            before.Decision.Action);
        var beforeObservation = Assert.IsType<PublicationRecoveryObservation>(
            before.Observation);
        Assert.Empty(beforeObservation.HistoricalRecords);
        Assert.Empty(beforeObservation.Records);
        var intentResult = await PublicationRecoveryPersistence
            .PersistIntentAndAuthorizeAsync(
                fixture.Context,
                beforeObservation,
                CancellationToken.None);
        Assert.True(intentResult.Succeeded, intentResult.Code);
        using var intent = Assert.IsType<PublicationIntentPersistenceResult>(
            intentResult.Value);
        Assert.Single(intent.Observation.Records);
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            intent.Observation.StoredPublication,
            out var rendered));
        Assert.True(AuthorizedStickyPublicationRequest.TryCreateRecovery(
            fixture.Invocation,
            fixture.PublicationScope,
            rendered,
            intent.Observation,
            intent.StickyWriteAuthorization,
            out var authorized));
        Assert.NotNull(authorized);

        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                intent.Observation.Records,
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
            candidate,
            BoundedGitHubPublisherOutcome.OutcomeUnknown,
            StickyPublicationReason.ReconciliationIncomplete,
            fixture.Time.UnixSeconds,
            prepared.Header.LogicalExpiresAtUnixSeconds,
            out _,
            out var request));
        var attemptResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                fixture.Context,
                ownership,
                request!,
                CancellationToken.None);
        using var attempt = Assert.IsType<RetainedStateOpaqueWriteAttempt>(
            attemptResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                fixture.Context,
                attempt,
                CancellationToken.None);
        using var persisted = Assert.IsType<RetainedStateOpaqueRecord>(
            persistedResult.Value);
        var cleanup = await PublicationRecoveryPersistence
            .CleanupCompletedWriteAnchorAsync(
                fixture.Context,
                attempt,
                CancellationToken.None);
        Assert.True(cleanup.Completed, cleanup.Code);
    }

    private static (FakePublisherTransportFactory Factory,
        BoundedGitHubIssueComment Comment) MarkerFactory(
        RetainedStatePreparedCandidate prepared,
        long commentId,
        bool exact)
    {
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            prepared.Publication,
            out var rendered));
        var body = exact
            ? rendered!.Comment
            : "tampered\n" + rendered!.Comment;
        var comment = StickyPublicationTestData.Comment(commentId, body);
        return (ExactMarkerFactory(comment), comment);
    }

    private static FakePublisherTransportFactory ExactMarkerFactory(
        BoundedGitHubIssueComment comment)
    {
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(comment);
        return factory;
    }

    private static async Task<(RetainedStatePreparedCandidate Prepared,
        RetainedStatePersistedCandidate Candidate)> PersistCandidateAsync(
        RetainedStateTransactionEndToEndTests.TransactionFixture fixture)
    {
        var run = await RetainedStateTransactionEndToEndTests
            .CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                run.Run,
                publication!,
                CancellationToken.None);
        var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        return (prepared, candidate);
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
            var advancedScenario = ActionHostAuthorizationScenario.Valid(
                ActionHostAuthorizationRoute.WorkflowDispatch);
            advancedScenario.Transport.PullRequest =
                advancedScenario.Transport.PullRequest with
                {
                    HeadSha = new string('f', 40),
                };
            var advancedAuthorization = await advancedScenario
                .CreateAuthorizer()
                .AuthorizeAsync(
                    advancedScenario.Launch,
                    CancellationToken.None);
            Assert.Equal(
                ActionHostAuthorizationFailure.None,
                advancedAuthorization.Failure);
            var advancedInvocation = Assert.IsType<
                ActionHostAuthorizer.AuthorizedInvocation>(
                    advancedAuthorization.Invocation);
            Assert.False(AuthorizedStickyPublicationRequest.TryCreateRecovery(
                advancedInvocation,
                fixture.PublicationScope,
                persistedRendered,
                persistedIntent.Observation,
                persistedIntent.StickyWriteAuthorization,
                out _));
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
            RecoveryState.KnownNotWrittenWithExactMarker or
            RecoveryState.OutcomeUnknown or
            RecoveryState.OutcomeUnknownWithExactMarker or
            RecoveryState.ExpiredOutcomeUnknown or
            RecoveryState.Cancelled or
            RecoveryState.AuthorizationFailure)
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
            var persistedIntentResult = await PublicationRecoveryPersistence
                .PersistIntentAndAuthorizeAsync(
                    context,
                    before.Observation!,
                    CancellationToken.None);
            using var persistedIntent = Assert.IsType<
                PublicationIntentPersistenceResult>(
                    persistedIntentResult.Value);
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

            var ownershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    context,
                    candidate,
                    prior: null,
                    persistedIntent.Observation.Records,
                    CancellationToken.None);
            using var ownership = Assert.IsType<RetainedStateOwnership>(
                ownershipResult.Value);
            var (outcome, reason) = state switch
            {
                RecoveryState.KnownNotWritten or
                    RecoveryState.KnownNotWrittenWithExactMarker => (
                    BoundedGitHubPublisherOutcome.KnownNotWritten,
                    StickyPublicationReason.Deadline),
                RecoveryState.OutcomeUnknown or
                    RecoveryState.OutcomeUnknownWithExactMarker or
                    RecoveryState.ExpiredOutcomeUnknown => (
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
            var cleanupResult = await PublicationRecoveryPersistence
                .CleanupCompletedWriteAnchorAsync(
                    context,
                    attempt,
                    CancellationToken.None);
            Assert.True(cleanupResult.Completed, cleanupResult.Code);
            if (state == RecoveryState.ExpiredOutcomeUnknown)
            {
                fixture.Time.UnixSeconds =
                    prepared.Header.LogicalExpiresAtUnixSeconds + 1;
                context.Dispose();
                fixture = await RetainedStateTransactionEndToEndTests
                    .RestoreFixtureAsync(fixture);
                var inventoryResult = await RestrictedStateService
                    .ObserveRetainedPublicationRecoveryInventoryAsync(
                        fixture.Context,
                        CancellationToken.None);
                using var inventory = inventoryResult.Value;
                Assert.True(inventoryResult.Succeeded, inventoryResult.Code);
                Assert.NotNull(inventory!.Candidate);
                Assert.Equal(2, inventory.Records.Length);
                Assert.Empty(inventory.Anchors);
                Assert.Empty(inventory.CleanupRecords);
            }
        }

        if (state is RecoveryState.ExactMarker or
            RecoveryState.OutcomeUnknownWithExactMarker or
            RecoveryState.KnownNotWrittenWithExactMarker)
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
        var consumedOnce = false;
        if (evaluation.StickyWriteAuthorization is not null &&
            evaluation.Observation is not null &&
            PublicationRecoveryService.TryRestoreRendered(
                evaluation.Observation.StoredPublication,
                out var rendered) &&
            rendered is not null)
        {
            consumedOnce = AuthorizedStickyPublicationRequest
                .TryCreateRecovery(
                    fixture.Invocation,
                    fixture.PublicationScope,
                    rendered,
                    evaluation.Observation,
                    evaluation.StickyWriteAuthorization,
                    out var authorized) &&
                authorized is not null &&
                !AuthorizedStickyPublicationRequest.TryCreateRecovery(
                    fixture.Invocation,
                    fixture.PublicationScope,
                    rendered,
                    evaluation.Observation,
                    evaluation.StickyWriteAuthorization,
                    out _);
        }
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
            evaluation.Observation?.Failure?.Outcome,
            consumedOnce);
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
        KnownNotWrittenWithExactMarker,
        OutcomeUnknown,
        OutcomeUnknownWithExactMarker,
        ExpiredOutcomeUnknown,
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
        BoundedGitHubPublisherOutcome? FailureOutcome,
        bool StickyAuthorizationConsumedOnce);

    private sealed class SimulatedProcessCrashException : Exception
    {
    }
}
