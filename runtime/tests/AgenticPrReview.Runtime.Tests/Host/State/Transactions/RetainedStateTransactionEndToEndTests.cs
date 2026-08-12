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
        var intent = await RestrictedStateService
            .PersistRetainedOpaqueRecordAsync(
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
            Assert.True(storedIntent.TryCopyPayload(
                candidate.Authority,
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
        var evidence = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                context,
                ownershipC,
                sticky!,
                exactHead,
                CancellationToken.None);
        using var acceptanceEvidence = Assert.IsType<
            RetainedStateAcceptanceEvidence>(evidence.Value);
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
        var nextProcess = await RestoreFixtureAsync(fixture);
        using var nextContext = nextProcess.Context;
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
        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                context,
                ownership,
                sticky!,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    preparedCandidate.Publication.ReviewedHeadSha,
                    preparedCandidate.Publication.ReviewedHeadSha),
                CancellationToken.None);
        using var evidence = Assert.IsType<RetainedStateAcceptanceEvidence>(
            evidenceResult.Value);
        var uploadsBefore = fixture.Store.UploadCalls;
        fixture.Store.FailUploadOnUploadCall = uploadsBefore + 2;
        fixture.Store.ScheduledUploadFailure = OpaqueStoreFailure.Io;
        fixture.Store.ScheduledUploadMutationState =
            OpaqueStoreMutationState.NotCommitted;

        var notCommitted = await RestrictedStateService
            .AcceptRetainedStateAsync(
                context,
                evidence,
                CancellationToken.None);

        Assert.False(notCommitted.Succeeded);
        Assert.Equal(uploadsBefore + 2, fixture.Store.UploadCalls);
        fixture.Time.UnixSeconds += 30;
        var acceptedAt = fixture.Time.UnixSeconds;
        var uploadsAfterKnownFailure = fixture.Store.UploadCalls;
        fixture.Store.HideUploadedObjectOnUploadCall =
            uploadsAfterKnownFailure + 3;
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
            uploadsAfterKnownFailure + 3,
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
            uploadsAfterKnownFailure + 3,
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
            uploadsAfterKnownFailure + 3,
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
            commentId: 202);
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
        var cleanup = await RestrictedStateService.CleanupRetainedStateAsync(
            thirdFixture.Context,
            new RetainedStateCleanupRequest(
                third.Acceptance,
                [
                    new RetainedStateCleanupTarget(first.CandidateMetadata),
                    new RetainedStateCleanupTarget(
                        first.Acceptance.ReceiptMetadata),
                ],
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
        TransactionFixture fixture)
    {
        var restored = await RestrictedStateService
            .RestoreAuthorizedArtifactStateAsync(
                new ArtifactStateRestoreRequest(
                    fixture.Launch,
                    fixture.Invocation,
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
            Selected = selected!,
        };
    }

    private static async Task<AcceptedGeneration> AcceptGenerationAsync(
        TransactionFixture fixture,
        long commentId)
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
        var owned = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
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
                commentId,
                $"https://github.com/{preparedCandidate.Publication.RepositoryName}" +
                    $"/pull/{preparedCandidate.Publication.PullRequestNumber}" +
                    $"#issuecomment-{commentId}",
                preparedCandidate.Publication.ScopeSha256,
                preparedCandidate.Publication.BodySha256,
                preparedCandidate.Publication.ReviewedHeadSha,
                out var sticky));
        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                ownership,
                sticky!,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    preparedCandidate.Publication.ReviewedHeadSha,
                    preparedCandidate.Publication.ReviewedHeadSha),
                CancellationToken.None);
        using var evidence = Assert.IsType<RetainedStateAcceptanceEvidence>(
            evidenceResult.Value);
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
        byte currentKeyByte)
    {
        Assert.True(ActionHostStateKey.TryCreate(
            Convert.ToBase64String(
                Enumerable.Repeat(currentKeyByte, 32).ToArray()),
            out var stateKey));
        Assert.True(ActionHostInputs.TryCreate(
            launch.Inputs.GitHubToken,
            launch.Inputs.ProviderApiKey,
            stateKey,
            previousStateKey: null,
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
            launch.RunId,
            launch.RunAttempt,
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
