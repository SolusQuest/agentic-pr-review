using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class RetainedStateTransactionEndToEndTests
{
    private const string FinishJson =
        "{\"summary\":\"complete\",\"findings\":[]}";

    [Fact]
    public async Task BootstrapCandidateIntentAndAcceptanceUseOneClosedChain()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        Assert.NotNull(publication);

        var prepared = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                context,
                run.Run,
                publication!,
                CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Code);
        using var preparedCandidate = Assert.IsType<
            RetainedStatePreparedCandidate>(prepared.Value);
        Assert.Equal(RetainedStateTransactionCodes.Prepared, prepared.Code);
        Assert.Equal(0, preparedCandidate.Generation.Generation);
        Assert.Empty(fixture.Store.Objects.Where(item =>
            item.Reference.Name == preparedCandidate.Name));

        var persisted = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                preparedCandidate,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persisted.Value);
        Assert.Equal(
            RetainedStateTransactionCodes.Persisted,
            persisted.Code);
        Assert.Single(fixture.Store.Objects.Where(item =>
            item.Reference.Name == preparedCandidate.Name));

        var ownedA = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        Assert.True(ownedA.Succeeded, ownedA.Code);
        using var ownershipA = Assert.IsType<RetainedStateOwnership>(
            ownedA.Value);
        var intentPayload = ImmutableArray.CreateRange(
            "opaque-p5-publication-intent"u8.ToArray());
        var intent = await PersistOpaqueRecordAsync(
                context,
                ownershipA,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationIntent,
                    intentPayload,
                    preparedCandidate.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    fixture.Time.UnixSeconds +
                        StateRetentionRequirements.LogicalWindowSeconds +
                        StateRetentionRequirements.PreStickyBudgetSeconds),
                CancellationToken.None);
        using var intentRecord = Assert.IsType<RetainedStateOpaqueRecord>(
            intent.Value);
        Assert.False(ownershipA.IsUsable);

        var queried = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using (var records = Assert.IsType<RetainedStateOpaqueRecordSet>(
            queried.Value))
        {
            var storedIntent = Assert.Single(records.Records);
            Assert.True(RestrictedStateService
                .TryCopyRetainedStateOpaquePayload(
                    context,
                    storedIntent,
                    out var roundTrip));
            Assert.True(intentPayload.AsSpan().SequenceEqual(
                roundTrip.AsSpan()));
        }

        var missingP5Evidence = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.Conflict,
            missingP5Evidence.Code);

        var ownedB = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [intentRecord],
                CancellationToken.None);
        using var ownershipB = Assert.IsType<RetainedStateOwnership>(
            ownedB.Value);
        var ownedC = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                ownershipB,
                expectedP5Records: [intentRecord],
                CancellationToken.None);
        using var ownershipC = Assert.IsType<RetainedStateOwnership>(
            ownedC.Value);
        Assert.False(ownershipB.IsUsable);

        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                preparedCandidate.Publication.RepositoryId,
                preparedCandidate.Publication.PullRequestNumber,
                commentId: 99,
                $"https://github.com/{preparedCandidate.Publication.RepositoryName}" +
                    $"/pull/{preparedCandidate.Publication.PullRequestNumber}" +
                    "#issuecomment-99",
                preparedCandidate.Publication.ScopeSha256,
                preparedCandidate.Publication.BodySha256,
                preparedCandidate.Publication.ReviewedHeadSha,
                out var sticky));
        var exactHead = new ExactHeadRevalidationResult(
            ExactHeadRevalidationStatus.Exact,
            preparedCandidate.Publication.ReviewedHeadSha,
            preparedCandidate.Publication.ReviewedHeadSha);
        using var acceptanceEvidence = await CreateFinalEvidenceAsync(
            fixture,
            candidate,
            ownershipC,
            sticky!,
            [intentRecord],
            exactHead);
        Assert.False(ownershipC.IsUsable);

        var accepted = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                acceptanceEvidence,
                CancellationToken.None);
        var verified = Assert.IsType<VerifiedRetainedStateAcceptance>(
            accepted.Value);
        Assert.Equal(RetainedStateTransactionCodes.Accepted, accepted.Code);
        Assert.Equal(
            fixture.Time.UnixSeconds,
            verified.AcceptedAtUnixSeconds);
        Assert.Equal(
            fixture.Time.UnixSeconds +
                StateRetentionRequirements.LogicalWindowSeconds,
            verified.LogicalExpiresAtUnixSeconds);
        Assert.True(
            verified.ReceiptMetadata.ExpiresAtUnixSeconds >=
                fixture.Time.UnixSeconds +
                    2 * StateRetentionRequirements.LogicalWindowSeconds);

        var dependencies = await RestrictedStateService
            .GetRetainedStateKeyDependenciesAsync(
                context,
                CancellationToken.None);
        var report = Assert.IsType<RetainedStateKeyDependencyReport>(
            dependencies.Value);
        Assert.Contains(report.RequiredDependencies, dependency =>
            dependency.Kind == LocatorDependencyKind.RestrictedState);
        Assert.Contains(report.RequiredDependencies, dependency =>
            dependency.Kind == LocatorDependencyKind.Transaction);
    }

    [Fact]
    public async Task CandidatePossibleCommitReconcilesWithoutSecondAppend()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var prepared = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                context,
                run.Run,
                publication!,
                CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Code);
        using var candidate = Assert.IsType<RetainedStatePreparedCandidate>(
            prepared.Value);
        var uploadsBefore = fixture.Store.UploadCalls;
        fixture.Store.NextUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
        fixture.Store.NextUploadMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        fixture.Store.PersistFailedUpload = true;

        var persisted = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                candidate,
                CancellationToken.None);

        Assert.True(persisted.Succeeded, persisted.Code);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        Assert.Single(fixture.Store.Objects.Where(item =>
            item.Reference.Name == candidate.Name));

        var retried = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                candidate,
                CancellationToken.None);
        Assert.True(retried.Succeeded, retried.Code);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        Assert.Equal(persisted.Value!.Metadata, retried.Value!.Metadata);

        context.Dispose();
        var nextProcess = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true);
        using var nextContext = nextProcess.Context;
        Assert.NotEqual(fixture.Launch.RunId, nextProcess.Launch.RunId);
        var uploadsBeforeRecovery = fixture.Store.UploadCalls;
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                nextContext,
                CancellationToken.None);
        var recovered = Assert.IsType<RetainedStatePersistedCandidate>(
            recoveredResult.Value);
        Assert.Equal(candidate.Header, recovered.Prepared.Header);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            candidate.Generation,
            out var originalGeneration));
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            recovered.Prepared.Generation,
            out var recoveredGeneration));
        Assert.True(originalGeneration.AsSpan().SequenceEqual(
            recoveredGeneration));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            candidate.Publication,
            out var originalPublication));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            recovered.Prepared.Publication,
            out var recoveredPublication));
        Assert.True(originalPublication.AsSpan().SequenceEqual(
            recoveredPublication));
        Assert.Equal(persisted.Value.Metadata, recovered.Metadata);
        Assert.Equal(uploadsBeforeRecovery, fixture.Store.UploadCalls);
    }

    [Fact]
    public async Task PreDispatchCancellationDoesNotMutateTheStore()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
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
        var uploadsBefore = fixture.Store.UploadCalls;

        var cancelled = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                prepared,
                new CancellationToken(canceled: true));

        Assert.Equal(RetainedStateTransactionCodes.Cancelled, cancelled.Code);
        Assert.Equal(uploadsBefore, fixture.Store.UploadCalls);
        Assert.DoesNotContain(fixture.Store.Objects, item =>
            item.Reference.Name == prepared.Name);
    }

    [Fact]
    public async Task OpaquePossibleCommitReconcilesExactAttemptAcrossProcess()
    {
        var fixture = await CreateFixtureAsync();
        var run = await CompleteRunAsync(fixture);
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
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        var semanticExpiry = fixture.Time.UnixSeconds +
            StateRetentionRequirements.LogicalWindowSeconds;
        var writeResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                fixture.Context,
                ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationFailure,
                    ImmutableArray.CreateRange("failure"u8.ToArray()),
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    semanticExpiry),
                CancellationToken.None);
        using var write = Assert.IsType<RetainedStateOpaqueWriteAttempt>(
            writeResult.Value);
        Assert.True(write.TryCreateRecoveryHandoff(out var handoff));
        fixture.Store.NextUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
        fixture.Store.NextUploadMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        fixture.Store.PersistFailedUpload = true;
        fixture.Store.HideUploadedObjectOnUploadCall =
            fixture.Store.UploadCalls + 1;
        fixture.Store.HideNextUploadedObjectForNextLists = 10;
        var uncertain = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                fixture.Context,
                write,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            uncertain.Code);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            rotateStateKey: true);
        var recoveredCandidateResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumed.Context,
                CancellationToken.None);
        var recoveredCandidate = Assert.IsType<
            RetainedStatePersistedCandidate>(recoveredCandidateResult.Value);
        var recoveredWriteResult = await RestrictedStateService
            .RecoverRetainedOpaqueWriteAsync(
                resumed.Context,
                recoveredCandidate,
                handoff!.OpaqueInnerPayload,
                CancellationToken.None);
        using var recoveredWrite = Assert.IsType<
            RetainedStateOpaqueWriteAttempt>(recoveredWriteResult.Value);
        Assert.Equal(
            handoff.MinimumSemanticExpiresAtUnixSeconds,
            recoveredWrite.SemanticRequiredExpiresAtUnixSeconds);
        var uploadsBeforeReconcile = resumed.Store.UploadCalls;
        var reconciled = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                resumed.Context,
                recoveredWrite,
                new CancellationToken(canceled: true));
        for (var attempt = 0;
            attempt < 5 &&
            StringComparer.Ordinal.Equals(
                reconciled.Code,
                RetainedStateTransactionCodes.OutcomeUnknown);
            attempt++)
        {
            reconciled = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    resumed.Context,
                    recoveredWrite,
                    new CancellationToken(canceled: true));
        }

        using var record = Assert.IsType<RetainedStateOpaqueRecord>(
            reconciled.Value);
        Assert.Equal(RetainedStateTransactionCodes.Persisted, reconciled.Code);
        Assert.Equal(uploadsBeforeReconcile, resumed.Store.UploadCalls);
        Assert.Equal(StateObjectClass.PublicationFailure, record.ObjectClass);
        resumed.Context.Dispose();
    }

    [Fact]
    public async Task CancellationAtStoreBoundaryReportsCommittedCandidate()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
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
        using var cancellation = new CancellationTokenSource();
        fixture.Store.BeforeUpload = cancellation.Cancel;
        var uploadsBefore = fixture.Store.UploadCalls;

        var persisted = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                prepared,
                cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(persisted.Succeeded, persisted.Code);
        Assert.Equal(RetainedStateTransactionCodes.Persisted, persisted.Code);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        Assert.Single(fixture.Store.Objects.Where(item =>
            item.Reference.Name == prepared.Name));
    }

    [Fact]
    public async Task ForkedOwnershipCannotMutateAfterInventoryChanges()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
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
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var ownedAResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownedA = Assert.IsType<RetainedStateOwnership>(
            ownedAResult.Value);
        var ownedBResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownedB = Assert.IsType<RetainedStateOwnership>(
            ownedBResult.Value);
        var request = new RetainedStateOpaqueWriteRequest(
            StateObjectClass.PublicationIntent,
            ImmutableArray.CreateRange("first-writer"u8.ToArray()),
            prepared.Header.ObjectIdentity,
            SuccessorIdentity: null,
            prepared.Header.LogicalExpiresAtUnixSeconds);
        var first = await PersistOpaqueRecordAsync(
                context,
                ownedB,
                request,
                CancellationToken.None);
        using var firstRecord = Assert.IsType<RetainedStateOpaqueRecord>(
            first.Value);
        var uploadsAfterFirst = fixture.Store.UploadCalls;

        var stale = await PersistOpaqueRecordAsync(
                context,
                ownedA,
                request with
                {
                    Payload = ImmutableArray.CreateRange(
                        "stale-writer"u8.ToArray()),
                },
                CancellationToken.None);

        Assert.Equal(RetainedStateTransactionCodes.Conflict, stale.Code);
        Assert.Equal(uploadsAfterFirst, fixture.Store.UploadCalls);
    }

    [Fact]
    public async Task AcceptancePossibleCommitRetryReconcilesFrozenReceipt()
    {
        var fixture = await CreateFixtureAsync(
            extraRetentionSeconds:
                20 * StateRetentionRequirements.LogicalWindowSeconds);
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var prepared = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                context,
                run.Run,
                publication!,
                CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Code);
        using var preparedCandidate = Assert.IsType<
            RetainedStatePreparedCandidate>(prepared.Value);
        var persisted = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                context,
                preparedCandidate,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persisted.Value);
        var owned = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            owned.Value);
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                preparedCandidate.Publication.RepositoryId,
                preparedCandidate.Publication.PullRequestNumber,
                commentId: 101,
                $"https://github.com/{preparedCandidate.Publication.RepositoryName}" +
                    $"/pull/{preparedCandidate.Publication.PullRequestNumber}" +
                    "#issuecomment-101",
                preparedCandidate.Publication.ScopeSha256,
                preparedCandidate.Publication.BodySha256,
                preparedCandidate.Publication.ReviewedHeadSha,
                out var sticky));
        using var evidence = await CreateFinalEvidenceAsync(
            fixture,
            candidate,
            ownership,
            sticky!);
        var uploadsBefore = fixture.Store.UploadCalls;
        var acceptedAt = fixture.Time.UnixSeconds;
        fixture.Store.FailUploadOnUploadCall = uploadsBefore + 1;
        fixture.Store.ScheduledUploadFailure = OpaqueStoreFailure.Io;
        fixture.Store.ScheduledUploadMutationState =
            OpaqueStoreMutationState.NotCommitted;

        var notCommitted = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                evidence,
                CancellationToken.None);

        Assert.False(notCommitted.Succeeded);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        fixture.Time.UnixSeconds += 30;
        var uploadsAfterKnownFailure = fixture.Store.UploadCalls;
        fixture.Store.HideUploadedObjectOnUploadCall =
            uploadsAfterKnownFailure + 1;
        fixture.Store.HideNextUploadedObjectForNextLists = 3;

        var uncertain = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                evidence,
                CancellationToken.None);

        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            uncertain.Code);
        Assert.Equal(
            uploadsAfterKnownFailure + 1,
            fixture.Store.UploadCalls);
        fixture.Time.UnixSeconds += 30;

        var retried = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                evidence,
                CancellationToken.None);
        var verified = Assert.IsType<VerifiedRetainedStateAcceptance>(
            retried.Value);
        Assert.Equal(RetainedStateTransactionCodes.Accepted, retried.Code);
        Assert.Equal(acceptedAt, verified.AcceptedAtUnixSeconds);
        Assert.Equal(
            uploadsAfterKnownFailure + 1,
            fixture.Store.UploadCalls);
        Assert.Single(fixture.Store.Objects.Where(item =>
            item.Reference.Name == verified.ReceiptMetadata.Reference.Name));

        var replayed = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                evidence,
                new CancellationToken(canceled: true));
        Assert.Same(verified, replayed.Value);
        Assert.Equal(
            uploadsAfterKnownFailure + 1,
            fixture.Store.UploadCalls);
    }

    [Fact]
    public async Task SuccessorsCopyPredecessorsAndCleanupOldExactTargets()
    {
        var fixture = await CreateFixtureAsync(extraRetentionSeconds: 0);
        var first = await AcceptGenerationAsync(fixture, commentId: 201);
        Assert.Equal(0, first.Generation);
        fixture.Context.Dispose();
        fixture.Time.UnixSeconds +=
            2 * 24 * 60 * 60;

        var successorFixture = await RestoreFixtureAsync(fixture);
        Assert.True(successorFixture.Context.TryGetAdmittedValue(
            out var admittedFirst));
        Assert.Equal(0, admittedFirst!.Artifact.Document.Generation);
        var second = await AcceptGenerationAsync(
            successorFixture,
            commentId: 202,
            exercisePredecessorCopyPossibleCommit: true);
        Assert.Equal(1, second.Generation);

        Assert.Equal(
            3,
            fixture.Store.Objects.Count(item =>
                item.Reference.Name == first.CandidateName));
        successorFixture.Context.Dispose();
        fixture.Time.UnixSeconds +=
            2 * 24 * 60 * 60;
        var thirdFixture = await RestoreFixtureAsync(successorFixture);
        var third = await AcceptGenerationAsync(thirdFixture, commentId: 203);
        Assert.Equal(2, third.Generation);
        var cleanupPlan = await RestrictedStateService
            .PlanRetainedStateCleanupAsync(
                thirdFixture.Context,
                third.Acceptance,
                CancellationToken.None);
        using var cleanupAuthorization = Assert.IsType<
            RetainedStateCleanupAuthorization>(cleanupPlan.Value);
        Assert.Contains(cleanupAuthorization.Targets, target =>
            target.Metadata == first.CandidateMetadata);
        Assert.Contains(cleanupAuthorization.Targets, target =>
            target.Metadata == first.Acceptance.ReceiptMetadata);
        fixture.Store.AfterDelete = () =>
        {
            fixture.Store.DeleteFailuresRemaining = 1;
            fixture.Store.NextDeleteFailure = OpaqueStoreFailure.Io;
            fixture.Store.NextDeleteMutationState =
                OpaqueStoreMutationState.NotCommitted;
        };
        var interrupted = await RestrictedStateService
            .CleanupRetainedStateAsync(
            thirdFixture.Context,
            new RetainedStateCleanupRequest(
                third.Acceptance,
                cleanupAuthorization,
                fixture.Time.UnixSeconds +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
            CancellationToken.None);
        Assert.False(interrupted.Completed);
        Assert.True(interrupted.AcceptanceRemainsVerified);
        Assert.DoesNotContain(
            cleanupAuthorization.Targets[0].Metadata,
            fixture.Store.Objects);

        var resumedPlan = await RestrictedStateService
            .PlanRetainedStateCleanupAsync(
                thirdFixture.Context,
                third.Acceptance,
                CancellationToken.None);
        using var resumedAuthorization = Assert.IsType<
            RetainedStateCleanupAuthorization>(resumedPlan.Value);
        var cleanup = await RestrictedStateService.CleanupRetainedStateAsync(
            thirdFixture.Context,
            new RetainedStateCleanupRequest(
                third.Acceptance,
                resumedAuthorization,
                fixture.Time.UnixSeconds +
                    StateRetentionRequirements.ScopedPlatformRequestSeconds),
            CancellationToken.None);
        Assert.True(cleanup.Completed, cleanup.Code);
        Assert.True(cleanup.AcceptanceRemainsVerified);
        Assert.DoesNotContain(first.CandidateMetadata, fixture.Store.Objects);
        Assert.DoesNotContain(
            first.Acceptance.ReceiptMetadata,
            fixture.Store.Objects);

        thirdFixture.Context.Dispose();
        var restored = await RestoreFixtureAsync(thirdFixture);
        using var restoredContext = restored.Context;
        Assert.True(restoredContext.TryGetAdmittedValue(
            out var admittedSecond));
        Assert.Equal(2, admittedSecond!.Artifact.Document.Generation);
        Assert.Equal(3, admittedSecond.Artifact.Document.CompletedRuns.Length);
    }

    [Fact]
    public async Task DurableAcceptanceAttemptAndVerifiedReceiptRecoverAcrossProcesses()
    {
        var fixture = await CreateFixtureAsync();
        var run = await CompleteRunAsync(fixture);
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
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                prepared.Publication.RepositoryId,
                prepared.Publication.PullRequestNumber,
                commentId: 301,
                $"https://github.com/{prepared.Publication.RepositoryName}" +
                    $"/pull/{prepared.Publication.PullRequestNumber}" +
                    "#issuecomment-301",
                prepared.Publication.ScopeSha256,
                prepared.Publication.BodySha256,
                prepared.Publication.ReviewedHeadSha,
                out var sticky));
        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                fixture.Context,
                ownership,
                sticky!,
                CancellationToken.None);
        using var preparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(preparationResult.Value);
        Assert.True(preparation.TryCreateRecoveryHandoff(out var handoff));
        var recoveryBytes = WrapP5RecoveryPayload(
            handoff!.OpaqueInnerPayload);
        var recordResult = await PersistOpaqueRecordAsync(
                fixture.Context,
                preparation.Ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationIntent,
                    recoveryBytes,
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var record = Assert.IsType<RetainedStateOpaqueRecord>(
            recordResult.Value);
        Assert.Equal(
            RetainedStateTransactionCodes.Ready,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    fixture.Context,
                    preparation,
                    CancellationToken.None));
        var finalOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                expectedP5Records: [record],
                CancellationToken.None);
        using var finalOwnership = Assert.IsType<RetainedStateOwnership>(
            finalOwnershipResult.Value);
        var originalEvidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                preparation,
                finalOwnership,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    prepared.Publication.ReviewedHeadSha,
                    prepared.Publication.ReviewedHeadSha),
                CancellationToken.None);
        using var originalEvidence = Assert.IsType<
            RetainedStateAcceptanceEvidence>(originalEvidenceResult.Value);
        fixture.Store.HideUploadedObjectOnUploadCall =
            fixture.Store.UploadCalls + 1;
        fixture.Store.HideNextUploadedObjectForNextLists = 10;
        var uncertainAcceptance = await RestrictedStateService
            .AcceptRetainedStateAsync(
                fixture.Context,
                originalEvidence,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            uncertainAcceptance.Code);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            rotateStateKey: true);
        Assert.NotEqual(fixture.Launch.RunId, resumed.Launch.RunId);
        var recoveredCandidateResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumed.Context,
                CancellationToken.None);
        var recoveredCandidate = Assert.IsType<
            RetainedStatePersistedCandidate>(recoveredCandidateResult.Value);
        var recordsResult = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                resumed.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var records = Assert.IsType<RetainedStateOpaqueRecordSet>(
            recordsResult.Value);
        var recoveredRecord = Assert.Single(records.Records);
        Assert.True(RestrictedStateService.TryCopyRetainedStateOpaquePayload(
            resumed.Context,
            recoveredRecord,
            out var recoveredOuterPayload));
        Assert.True(TryUnwrapP5RecoveryPayload(
            recoveredOuterPayload,
            out var recoveredInnerPayload));
        var recoveredPreparationResult = await RestrictedStateService
            .RecoverRetainedStateAcceptancePreparationAsync(
                resumed.Context,
                recoveredCandidate,
                recoveredInnerPayload,
                CancellationToken.None);
        using var recoveredPreparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(
                recoveredPreparationResult.Value);
        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                resumed.Context,
                recoveredPreparation,
                recoveredPreparation.Ownership,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    prepared.Publication.ReviewedHeadSha,
                    prepared.Publication.ReviewedHeadSha),
                CancellationToken.None);
        using var evidence = Assert.IsType<RetainedStateAcceptanceEvidence>(
            evidenceResult.Value);
        var accepted = await RestrictedStateService.AcceptRetainedStateAsync(
            resumed.Context,
            evidence,
            new CancellationToken(canceled: true));
        for (var attempt = 0;
            attempt < 4 &&
            StringComparer.Ordinal.Equals(
                accepted.Code,
                RetainedStateTransactionCodes.OutcomeUnknown);
            attempt++)
        {
            accepted = await RestrictedStateService.AcceptRetainedStateAsync(
                resumed.Context,
                evidence,
                new CancellationToken(canceled: true));
        }
        var verified = Assert.IsType<VerifiedRetainedStateAcceptance>(
            accepted.Value);

        resumed.Context.Dispose();
        var afterCommit = await RestoreFixtureAsync(resumed);
        using var afterCommitContext = afterCommit.Context;
        var recoveredVerified = await RestrictedStateService
            .RecoverVerifiedRetainedStateAcceptanceAsync(
                afterCommitContext,
                CancellationToken.None);
        var verifiedAfterRestart = Assert.IsType<
            VerifiedRetainedStateAcceptance>(recoveredVerified.Value);
        Assert.Equal(
            verified.AcceptanceReceiptIdentity,
            verifiedAfterRestart.AcceptanceReceiptIdentity);
        Assert.Equal(
            verified.AcceptedAtUnixSeconds,
            verifiedAfterRestart.AcceptedAtUnixSeconds);

        var completedRecordsResult = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                afterCommitContext,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var completedRecords = Assert.IsType<
            RetainedStateOpaqueRecordSet>(completedRecordsResult.Value);
        var completedRecord = Assert.Single(completedRecords.Records);
        var cleanupPlan = await RestrictedStateService
            .PlanRetainedStateCleanupAsync(
                afterCommitContext,
                verifiedAfterRestart,
                CancellationToken.None);
        using var cleanupAuthorization = Assert.IsType<
            RetainedStateCleanupAuthorization>(cleanupPlan.Value);
        Assert.DoesNotContain(cleanupAuthorization.Targets, target =>
            target.Metadata == completedRecord.Metadata);
        Assert.Contains(
            completedRecord.Metadata,
            fixture.Store.Objects);
    }

    [Fact]
    public async Task PredecessorPossibleCommitReconcilesAcrossRotatedProcess()
    {
        var fixture = await CreateFixtureAsync(extraRetentionSeconds: 0);
        _ = await AcceptGenerationAsync(fixture, commentId: 310);
        fixture.Context.Dispose();
        fixture.Time.UnixSeconds += 2 * 24 * 60 * 60;
        var successor = await RestoreFixtureAsync(fixture);
        var run = await CompleteRunAsync(successor, "successor", "finish311");
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            successor.PublicationScope,
            out var publication));
        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                successor.Context,
                run.Run,
                publication!,
                CancellationToken.None);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                successor.Context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var existingResult = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                successor.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var existing = Assert.IsType<RetainedStateOpaqueRecordSet>(
            existingResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                successor.Context,
                candidate,
                prior: null,
                expectedP5Records: existing.Records,
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                prepared.Publication.RepositoryId,
                prepared.Publication.PullRequestNumber,
                commentId: 311,
                $"https://github.com/{prepared.Publication.RepositoryName}" +
                    $"/pull/{prepared.Publication.PullRequestNumber}" +
                    "#issuecomment-311",
                prepared.Publication.ScopeSha256,
                prepared.Publication.BodySha256,
                prepared.Publication.ReviewedHeadSha,
                out var sticky));
        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                successor.Context,
                ownership,
                sticky!,
                CancellationToken.None);
        using var preparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(preparationResult.Value);
        Assert.True(preparation.TryCreateRecoveryHandoff(out var handoff));
        var outer = WrapP5RecoveryPayload(handoff!.OpaqueInnerPayload);
        var p5Result = await PersistOpaqueRecordAsync(
                successor.Context,
                preparation.Ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationIntent,
                    outer,
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var p5 = Assert.IsType<RetainedStateOpaqueRecord>(p5Result.Value);
        successor.Store.FailNextUploadForName = candidate.Prepared.Name;
        successor.Store.ScheduledUploadFailure =
            OpaqueStoreFailure.OutcomeUnknown;
        successor.Store.ScheduledUploadMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        successor.Store.PersistFailedUpload = true;
        successor.Store.HideFailedUploadForNextLists = 12;
        var uncertain = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                successor.Context,
                preparation,
                CancellationToken.None);
        Assert.Equal(RetainedStateTransactionCodes.OutcomeUnknown, uncertain);
        var uploadsAfterPossibleCommit = successor.Store.UploadCalls;
        var physicalAfterPossibleCommit = successor.Store.Objects.Count(item =>
            item.Reference.Name == candidate.Prepared.Name);

        successor.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            successor,
            newWorkflowRun: true,
            rotateStateKey: true);
        var recoveredCandidateResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumed.Context,
                CancellationToken.None);
        var recoveredCandidate = Assert.IsType<
            RetainedStatePersistedCandidate>(recoveredCandidateResult.Value);
        var recoveredRecordsResult = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                resumed.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var recoveredRecords = Assert.IsType<
            RetainedStateOpaqueRecordSet>(recoveredRecordsResult.Value);
        ImmutableArray<byte> recoveredInner = [];
        foreach (var record in recoveredRecords.Records)
        {
            if (RestrictedStateService.TryCopyRetainedStateOpaquePayload(
                    resumed.Context,
                    record,
                    out var recoveredOuter) &&
                TryUnwrapP5RecoveryPayload(
                    recoveredOuter,
                    out var inner) &&
                inner.AsSpan().SequenceEqual(
                    handoff.OpaqueInnerPayload.AsSpan()))
            {
                recoveredInner = inner;
                break;
            }
        }

        Assert.False(recoveredInner.IsDefaultOrEmpty);
        var recoveredPreparationResult = await RestrictedStateService
            .RecoverRetainedStateAcceptancePreparationAsync(
                resumed.Context,
                recoveredCandidate,
                recoveredInner,
                CancellationToken.None);
        using var recoveredPreparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(
                recoveredPreparationResult.Value);
        var uploadsBeforeRecoveryReconcile = resumed.Store.UploadCalls;
        var cancelled = new CancellationToken(canceled: true);
        var reconciled = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                resumed.Context,
                recoveredPreparation,
                cancelled);
        for (var attempt = 0;
            attempt < 6 &&
            StringComparer.Ordinal.Equals(
                reconciled,
                RetainedStateTransactionCodes.OutcomeUnknown);
            attempt++)
        {
            reconciled = await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    resumed.Context,
                    recoveredPreparation,
                    cancelled);
        }

        Assert.Equal(RetainedStateTransactionCodes.Ready, reconciled);
        Assert.True(
            uploadsBeforeRecoveryReconcile >= uploadsAfterPossibleCommit);
        Assert.Equal(
            uploadsBeforeRecoveryReconcile,
            resumed.Store.UploadCalls);
        Assert.Equal(
            physicalAfterPossibleCommit,
            resumed.Store.Objects.Count(item =>
                item.Reference.Name == candidate.Prepared.Name));
        resumed.Context.Dispose();
    }

    [Fact]
    public async Task UndispatchedPreparationCanBeReplacedAfterP5AbandonsIt()
    {
        var fixture = await CreateFixtureAsync();
        _ = await AcceptGenerationAsync(fixture, commentId: 320);
        fixture.Context.Dispose();
        var successor = await RestoreFixtureAsync(fixture);
        var run = await CompleteRunAsync(successor, "successor", "finish321");
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            successor.PublicationScope,
            out var publication));
        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                successor.Context,
                run.Run,
                publication!,
                CancellationToken.None);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                successor.Context,
                prepared,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var existingResult = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                successor.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var existing = Assert.IsType<RetainedStateOpaqueRecordSet>(
            existingResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                successor.Context,
                candidate,
                prior: null,
                expectedP5Records: existing.Records,
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                prepared.Publication.RepositoryId,
                prepared.Publication.PullRequestNumber,
                commentId: 321,
                $"https://github.com/{prepared.Publication.RepositoryName}" +
                    $"/pull/{prepared.Publication.PullRequestNumber}" +
                    "#issuecomment-321",
                prepared.Publication.ScopeSha256,
                prepared.Publication.BodySha256,
                prepared.Publication.ReviewedHeadSha,
                out var sticky));
        var firstResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                successor.Context,
                ownership,
                sticky!,
                CancellationToken.None);
        using var first = Assert.IsType<RetainedStateAcceptancePreparation>(
            firstResult.Value);
        Assert.True(first.TryCreateRecoveryHandoff(out var firstHandoff));
        var uploadsBeforeReplacement = successor.Store.UploadCalls;
        var predecessorPhysicalBeforeReplacement =
            successor.Store.Objects.Count(item =>
                item.Reference.Name == candidate.Prepared.Name);
        successor.Time.UnixSeconds++;

        var secondResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                successor.Context,
                first.Ownership,
                sticky!,
                CancellationToken.None);
        using var second = Assert.IsType<RetainedStateAcceptancePreparation>(
            secondResult.Value);
        Assert.True(second.TryCreateRecoveryHandoff(out var secondHandoff));
        Assert.Equal(
            firstHandoff!.MinimumSemanticExpiresAtUnixSeconds + 1,
            secondHandoff!.MinimumSemanticExpiresAtUnixSeconds);
        Assert.False(firstHandoff.OpaqueInnerPayload.AsSpan().SequenceEqual(
            secondHandoff.OpaqueInnerPayload.AsSpan()));
        Assert.True(
            successor.Store.UploadCalls >= uploadsBeforeReplacement);
        Assert.Equal(
            predecessorPhysicalBeforeReplacement,
            successor.Store.Objects.Count(item =>
                item.Reference.Name == candidate.Prepared.Name));
        successor.Context.Dispose();
    }

    [Fact]
    public async Task RecoveryHandoffUsesAcceptanceHorizonAfterDelayedSticky()
    {
        var fixture = await CreateFixtureAsync();
        using var context = fixture.Context;
        var run = await CompleteRunAsync(fixture);
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
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        fixture.Time.UnixSeconds += 60;
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                prepared.Publication.RepositoryId,
                prepared.Publication.PullRequestNumber,
                commentId: 302,
                $"https://github.com/{prepared.Publication.RepositoryName}" +
                    $"/pull/{prepared.Publication.PullRequestNumber}" +
                    "#issuecomment-302",
                prepared.Publication.ScopeSha256,
                prepared.Publication.BodySha256,
                prepared.Publication.ReviewedHeadSha,
                out var sticky));

        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                context,
                ownership,
                sticky!,
                CancellationToken.None);
        using var preparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(preparationResult.Value);
        Assert.True(preparation.TryCreateRecoveryHandoff(out var handoff));
        Assert.Equal(
            fixture.Time.UnixSeconds +
                StateRetentionRequirements.LogicalWindowSeconds,
            handoff!.MinimumSemanticExpiresAtUnixSeconds);
        Assert.True(
            handoff.MinimumSemanticExpiresAtUnixSeconds >
                prepared.Header.LogicalExpiresAtUnixSeconds);
    }

    [Fact]
    public async Task DisposedContextFailsBeforeTransactionPreparation()
    {
        var fixture = await CreateFixtureAsync();
        var runB = await CompleteRunAsync(fixture, "B");
        Assert.True(R4PreparedPublication.TryCreate(
            runB.Outcome,
            fixture.PublicationScope,
            out var publicationB));

        fixture.Context.Dispose();
        var disposed = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                runB.Run,
                publicationB!,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            disposed.Code);
    }

    private static async Task<TransactionFixture> CreateFixtureAsync(
        long extraRetentionSeconds = 3_600)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            ActionHostAuthorizationRoute.WorkflowRun);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        var invocation = Assert.IsType<
            ActionHostAuthorizer.AuthorizedInvocation>(
                authorization.Invocation);
        Assert.True(ActionHostTrustedPolicyRequest.TryBind(
            launch,
            invocation,
            out var policyRequest,
            out var bindFailure));
        Assert.Equal(ActionHostTrustedPolicyFailure.None, bindFailure);
        var materialized = await ActionHostTrustedPolicy.MaterializeAsync(
            policyRequest!,
            ActionHostTrustedPolicyTests.ScriptedObjectTransport.Valid(
                ActionHostTrustedPolicyTests.Config("sticky", null),
                Encoding.UTF8.GetBytes("trusted transaction policy")),
            CancellationToken.None);
        var policy = Assert.IsType<ActionHostTrustedPolicy>(
            materialized.Policy);
        var time = new MutableLineageTimeProvider(1_700_000_000);
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            UseNumericObjectIds = true,
            ProducingRunIdentity = launch.RunId.ToString(
                CultureInfo.InvariantCulture),
            ProducingRunAttempt = launch.RunAttempt,
            ExtraRetentionSeconds = extraRetentionSeconds,
        };
        var currentReview = User("current review context");
        var request = new ArtifactStateRestoreRequest(
            launch,
            invocation,
            policy,
            currentReview,
            DeepSeekReasoningContinuationCodec.Instance,
            new TestDependencies(store),
            time);
        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                request,
                CancellationToken.None);
        Assert.True(restored.Succeeded, restored.Code);
        Assert.True(restored.IsBootstrap);
        var context = Assert.IsType<AuthorizedAcceptedStateRestoreContext>(
            restored.Context);
        Assert.True(context.TryGetLineageSnapshot(out var selected));
        Assert.NotNull(selected);
        var baseScope = AuthorizedAcceptedStateComposer.BaseScope(
            Assert.IsType<AcceptedStateProductionAuthorization>(
                AcceptedStateProductionAuthorization.TryAuthorize(
                    request,
                    out var production)
                    ? production
                    : null));
        var publicationScope = new R4PublicationScopeV1(
            (ulong)launch.RepositoryId,
            (ulong)launch.RepositoryId,
            invocation.WorkflowPath,
            launch.WorkflowRef,
            (ulong)invocation.PullRequest.Number,
            policy.PolicySha256,
            baseScope.PayloadBuildIdentity);
        return new TransactionFixture(
            scenario,
            store,
            time,
            context,
            launch,
            invocation,
            policy,
            currentReview,
            selected!,
            publicationScope);
    }

    private static async Task<TransactionFixture> RestoreFixtureAsync(
        TransactionFixture fixture,
        bool newWorkflowRun = false,
        bool rotateStateKey = false)
    {
        var launch = fixture.Launch;
        var invocation = fixture.Invocation;
        if (newWorkflowRun)
        {
            launch = StateLaunch(
                fixture.Launch,
                currentKeyByte: rotateStateKey ? (byte)0x43 : (byte)0x42,
                previousKeyByte: rotateStateKey ? (byte)0x42 : null,
                runId: fixture.Launch.RunId + 1,
                runAttempt: 1);
            fixture.Scenario.Transport.CurrentRun =
                fixture.Scenario.Transport.CurrentRun with
                {
                    Id = launch.RunId,
                    Attempt = launch.RunAttempt,
                };
            var authorization = await fixture.Scenario.CreateAuthorizer()
                .AuthorizeAsync(launch, CancellationToken.None);
            invocation = Assert.IsType<
                ActionHostAuthorizer.AuthorizedInvocation>(
                    authorization.Invocation);
            fixture.Store.ProducingRunIdentity = launch.RunId.ToString(
                CultureInfo.InvariantCulture);
            fixture.Store.ProducingRunAttempt = launch.RunAttempt;
        }

        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                new ArtifactStateRestoreRequest(
                    launch,
                    invocation,
                    fixture.Policy,
                    fixture.CurrentReview,
                    DeepSeekReasoningContinuationCodec.Instance,
                    new TestDependencies(fixture.Store),
                    fixture.Time),
                CancellationToken.None);
        Assert.True(restored.Succeeded, restored.Code);
        var context = Assert.IsType<AuthorizedAcceptedStateRestoreContext>(
            restored.Context);
        Assert.True(context.TryGetLineageSnapshot(out var selected));
        Assert.NotNull(selected);
        return fixture with
        {
            Context = context,
            Launch = launch,
            Invocation = invocation,
            Selected = selected!,
        };
    }

    private static async Task<AcceptedGeneration> AcceptGenerationAsync(
        TransactionFixture fixture,
        long commentId,
        bool exercisePredecessorCopyPossibleCommit = false)
    {
        var run = await CompleteRunAsync(
            fixture,
            $"generation {commentId}",
            $"finish{commentId}");
        Assert.True(R4PreparedPublication.TryCreate(
            run.Outcome,
            fixture.PublicationScope,
            out var publication));
        var prepared = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                run.Run,
                publication!,
                CancellationToken.None);
        Assert.True(prepared.Succeeded, prepared.Code);
        using var preparedCandidate = Assert.IsType<
            RetainedStatePreparedCandidate>(prepared.Value);
        var persisted = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                preparedCandidate,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            persisted.Value);
        var existingP5Result = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                fixture.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var existingP5 = Assert.IsType<RetainedStateOpaqueRecordSet>(
            existingP5Result.Value);
        var owned = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                existingP5.Records,
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            owned.Value);
        RetainedStateOwnership? renewedOwnership = null;
        var acceptanceOwnership = ownership;
        Assert.True(StickyCommentPublisher.StickyPublicationReceipt
            .TryRehydrate(
                StickyPublicationOperation.Observed,
                preparedCandidate.Publication.RepositoryId,
                preparedCandidate.Publication.PullRequestNumber,
                commentId,
                $"https://github.com/{preparedCandidate.Publication.RepositoryName}" +
                    $"/pull/{preparedCandidate.Publication.PullRequestNumber}" +
                    $"#issuecomment-{commentId}",
                preparedCandidate.Publication.ScopeSha256,
                preparedCandidate.Publication.BodySha256,
                preparedCandidate.Publication.ReviewedHeadSha,
                out var sticky));
        if (exercisePredecessorCopyPossibleCommit)
        {
            fixture.Store.FailNextUploadForName = candidate.Prepared.Name;
            fixture.Store.ScheduledUploadFailure =
                OpaqueStoreFailure.OutcomeUnknown;
            fixture.Store.ScheduledUploadMutationState =
                OpaqueStoreMutationState.OutcomeUnknown;
            fixture.Store.PersistFailedUpload = true;
            fixture.Store.HideFailedUploadForNextLists = 6;
        }

        using var renewedOwnershipLifetime = renewedOwnership;
        using var evidence = await CreateFinalEvidenceAsync(
            fixture,
            candidate,
            acceptanceOwnership,
            sticky!,
            existingP5.Records);
        var accepted = await RestrictedStateService.AcceptRetainedStateAsync(
            fixture.Context,
            evidence,
            CancellationToken.None);
        Assert.Equal(RetainedStateTransactionCodes.Accepted, accepted.Code);
        var verified = Assert.IsType<VerifiedRetainedStateAcceptance>(
            accepted.Value);
        return new AcceptedGeneration(
            preparedCandidate.Generation.Generation,
            preparedCandidate.Name,
            candidate.Metadata,
            verified);
    }

    private static async Task<RetainedStateAcceptanceEvidence>
        CreateFinalEvidenceAsync(
        TransactionFixture fixture,
        RetainedStatePersistedCandidate candidate,
        RetainedStateOwnership ownership,
        StickyCommentPublisher.StickyPublicationReceipt sticky,
        ImmutableArray<RetainedStateOpaqueRecord> existingP5Records = default,
        ExactHeadRevalidationResult? exactHead = null)
    {
        if (existingP5Records.IsDefault)
        {
            existingP5Records = [];
        }

        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                fixture.Context,
                ownership,
                sticky,
                CancellationToken.None);
        using var preparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(preparationResult.Value);
        Assert.True(preparation.TryCreateRecoveryHandoff(out var handoff));
        var recoveryPayload = WrapP5RecoveryPayload(
            handoff!.OpaqueInnerPayload);
        var recoveredAttemptResult = await PersistOpaqueRecordAsync(
                fixture.Context,
                preparation.Ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationIntent,
                    recoveryPayload,
                    candidate.Prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var recoveryRecord = Assert.IsType<RetainedStateOpaqueRecord>(
            recoveredAttemptResult.Value);
        var predecessorCode = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                fixture.Context,
                preparation,
                CancellationToken.None);
        for (var attempt = 0;
            attempt < 4 &&
            StringComparer.Ordinal.Equals(
                predecessorCode,
                RetainedStateTransactionCodes.OutcomeUnknown);
            attempt++)
        {
            predecessorCode = await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    fixture.Context,
                    preparation,
                    CancellationToken.None);
        }

        Assert.Equal(RetainedStateTransactionCodes.Ready, predecessorCode);
        var allP5 = existingP5Records.Add(recoveryRecord);
        var finalOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                allP5,
                CancellationToken.None);
        using var finalOwnership = Assert.IsType<RetainedStateOwnership>(
            finalOwnershipResult.Value);
        var head = exactHead ?? new ExactHeadRevalidationResult(
            ExactHeadRevalidationStatus.Exact,
            candidate.Prepared.Publication.ReviewedHeadSha,
            candidate.Prepared.Publication.ReviewedHeadSha);
        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                preparation,
                finalOwnership,
                head,
                CancellationToken.None);
        Assert.True(evidenceResult.Succeeded, evidenceResult.Code);
        return Assert.IsType<RetainedStateAcceptanceEvidence>(
            evidenceResult.Value);
    }

    private static async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecord>> PersistOpaqueRecordAsync(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateOwnership ownership,
        RetainedStateOpaqueWriteRequest request,
        CancellationToken cancellationToken)
    {
        var prepared = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                context,
                ownership,
                request,
                cancellationToken);
        using var attempt = prepared.Value;
        return attempt is null
            ? RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(prepared.Code)
            : await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    context,
                    attempt,
                    cancellationToken);
    }

    private static ImmutableArray<byte> WrapP5RecoveryPayload(
        ImmutableArray<byte> inner)
    {
        var prefix = Encoding.UTF8.GetBytes(
            "P5REC01|status=sticky-published|acceptance=");
        return [.. prefix, .. inner];
    }

    private static bool TryUnwrapP5RecoveryPayload(
        ImmutableArray<byte> outer,
        out ImmutableArray<byte> inner)
    {
        var prefix = Encoding.UTF8.GetBytes(
            "P5REC01|status=sticky-published|acceptance=");
        if (outer.IsDefaultOrEmpty ||
            outer.Length <= prefix.Length ||
            !outer.AsSpan(0, prefix.Length).SequenceEqual(prefix))
        {
            inner = [];
            return false;
        }

        inner = outer[prefix.Length..];
        return true;
    }

    private static async Task<CompletedRun> CompleteRunAsync(
        TransactionFixture fixture,
        string summary = "complete",
        string callId = "finish0")
    {
        var trusted = new AgentSessionTrustedRequest(
            fixture.Launch.RepositoryId.ToString(CultureInfo.InvariantCulture),
            fixture.Invocation.PullRequest.Number,
            AuthorizedAcceptedStateComposer.TrustedWorkflowIdentity(
                fixture.Invocation,
                fixture.Policy),
            fixture.Policy.InstructionBytes.ToArray(),
            fixture.Policy.BuildDiscriminator,
            fixture.Policy.ProviderId,
            fixture.Policy.ModelId,
            fixture.Policy.AdapterId);
        AgentRunRequest run;
        if (fixture.Context.TryGetAdmittedValue(out var admitted))
        {
            run = admitted!.RunRequest;
        }
        else
        {
            Assert.True(AgentStableRequestMaterializer.TryMaterialize(
                trusted,
                priorSessionSha256: null,
                out var materialized));
            run = new AgentRunRequest(
                new ReviewedIdentity(
                    fixture.Launch.RepositoryId.ToString(
                        CultureInfo.InvariantCulture),
                    fixture.Invocation.PullRequest.Number,
                    fixture.Invocation.PullRequest.BaseSha,
                    fixture.Invocation.PullRequest.HeadSha),
                materialized!.StablePlan,
                fixture.Selected.SessionId,
                [.. materialized.ControlMessages, fixture.CurrentReview]);
        }
        var outcome = await new AgentLoop(
            new OneResponseChatClient(request =>
                new ProjectChatResponse(
                    new ProjectChatMessage(
                        "assistant",
                        [
                            new ProjectReasoningContent(
                                "state reasoning",
                                string.Empty,
                                DeepSeekReasoningContinuationCodec.FramingName,
                                AssociatedCallId: null,
                                MessagePosition: request.Messages.Length,
                                Position: 0),
                            new ProjectToolCallContent(
                                callId,
                                AgentToolRegistry.FinishReviewName,
                                $"{{\"summary\":\"{summary}\",\"findings\":[]}}"),
                        ]),
                    new ProjectChatUsage(1, 1),
                    CapturedResponseBodyBytes: 1,
                    new ProjectContinuation(
                        fixture.Policy.ProviderId,
                        fixture.Policy.ModelId,
                        fixture.Policy.AdapterId,
                        fixture.Selected.SessionId,
                        [
                            new ProjectContinuationItem(
                                "state reasoning",
                                string.Empty,
                                DeepSeekReasoningContinuationCodec.FramingName,
                                AssociatedCallId: null,
                                MessagePosition: request.Messages.Length,
                                ContentPosition: 0),
                        ]))),
            new NeverToolExecutor()).RunAsync(
                run,
                CancellationToken.None);
        Assert.True(outcome.CompletedSessionEligible);
        return new CompletedRun(run, outcome);
    }

    private static ActionHostLaunchContract StateLaunch(
        ActionHostLaunchContract launch,
        byte currentKeyByte,
        byte? previousKeyByte = null,
        long? runId = null,
        int? runAttempt = null)
    {
        Assert.True(ActionHostStateKey.TryCreate(
            Convert.ToBase64String(
                Enumerable.Repeat(currentKeyByte, 32).ToArray()),
            out var stateKey));
        ActionHostPreviousStateKey? previousStateKey = null;
        if (previousKeyByte is { } previous)
        {
            Assert.True(ActionHostPreviousStateKey.TryCreate(
                Convert.ToBase64String(
                    Enumerable.Repeat(previous, 32).ToArray()),
                out previousStateKey));
        }

        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            launch.Inputs.ProviderApiKey,
            stateKey,
            previousStateKey,
            launch.Inputs.ConfigPath,
            pullRequestNumber: null,
            ActionHostStateMode.Auto,
            out var inputs));
        Assert.True(ActionHostLaunchContract.TryCreate(
            inputs,
            launch.EventJsonPath,
            launch.EventJsonSha256,
            launch.RepositoryName,
            launch.RepositoryId,
            runId ?? launch.RunId,
            runAttempt ?? launch.RunAttempt,
            launch.WorkflowPath,
            launch.WorkflowRef,
            launch.WorkflowSha,
            launch.ActionSourceSha,
            launch.PayloadSha256,
            launch.BuildDiscriminator,
            launch.Cancellation,
            launch.ArtifactBridgeEndpoint,
            out var result));
        return result!;
    }

    private static ProjectChatMessage User(string text) =>
        new("user", [new ProjectTextContent(text)]);

    private sealed record TransactionFixture(
        ActionHostAuthorizationScenario Scenario,
        ScriptedLocatorStore Store,
        MutableLineageTimeProvider Time,
        AuthorizedAcceptedStateRestoreContext Context,
        ActionHostLaunchContract Launch,
        ActionHostAuthorizer.AuthorizedInvocation Invocation,
        ActionHostTrustedPolicy Policy,
        ProjectChatMessage CurrentReview,
        SelectedLineageSnapshot Selected,
        R4PublicationScopeV1 PublicationScope);

    private sealed record CompletedRun(
        AgentRunRequest Run,
        AgentRunOutcome Outcome);

    private sealed record AcceptedGeneration(
        long Generation,
        OpaqueStoreName CandidateName,
        OpaqueStoreObjectMetadata CandidateMetadata,
        VerifiedRetainedStateAcceptance Acceptance);

    private sealed class TestDependencies(IRestrictedStateStore store) :
        IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch) => store;

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token) => new NoCallTransport();
    }

    private sealed class NoCallTransport : IActionHostGitObjectTransport
    {
        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bootstrap must not read Git.");

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bootstrap must not read Git.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Bootstrap must not read Git.");

        public void Dispose() { }
    }

    private sealed class OneResponseChatClient(
        Func<ProjectChatRequest, ProjectChatResponse> response) :
        IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    private sealed class NeverToolExecutor : IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) => null;

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "No non-terminal tool expected.");
    }
}
