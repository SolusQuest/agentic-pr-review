using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.ActionHost.Policy;
using AgenticPrReview.Runtime.ActionHost.Snapshot;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Chat;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Agent.Loop;
using AgenticPrReview.Runtime.Agent.Session;
using AgenticPrReview.Runtime.Agent.Tools;
using AgenticPrReview.Runtime.Canonical;
using AgenticPrReview.Runtime.Execution.DeepSeek;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;
using AgenticPrReview.Runtime.Tests.Host.Action.Policy;
using AgenticPrReview.Runtime.Tests.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Transactions;

public sealed class RetainedStateTransactionEndToEndTests
{
    private const string FinishJson =
        "{\"summary\":\"complete\",\"findings\":[]}";
    private static readonly byte[] P5RecoveryPayloadPrefix =
        Encoding.UTF8.GetBytes(
            "P5REC01|status=sticky-published|acceptance=");

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
        Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
            candidate,
            fixture.Time.UnixSeconds,
            preparedCandidate.Header.LogicalExpiresAtUnixSeconds,
            out var intentBody,
            out var intentRequest));
        var intent = await PersistOpaqueRecordAsync(
                context,
                ownershipA,
                intentRequest!,
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
            Assert.True(intentRequest!.Payload.AsSpan().SequenceEqual(
                roundTrip.AsSpan()));
            Assert.True(PublicationIntentV1Codec.TryDecode(
                roundTrip.AsSpan(),
                out var decodedIntent));
            Assert.Equal(intentBody, decodedIntent);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredPublicationFamilyAllowsSuccessorPossibleCommit(
        bool includeP5Descendants)
    {
        var fixture = await CreateFixtureAsync();
        var firstRun = await CompleteRunAsync(fixture, "expired family");
        Assert.True(R4PreparedPublication.TryCreate(
            firstRun.Outcome,
            fixture.PublicationScope,
            out var firstPublication));
        var firstPreparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                firstRun.Run,
                firstPublication!,
                CancellationToken.None);
        using var firstPrepared = Assert.IsType<RetainedStatePreparedCandidate>(
            firstPreparedResult.Value);
        var firstPersistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                firstPrepared,
                CancellationToken.None);
        var firstCandidate = Assert.IsType<RetainedStatePersistedCandidate>(
            firstPersistedResult.Value);
        OpaqueStoreObjectMetadata? intentMetadata = null;
        OpaqueStoreObjectMetadata? failureMetadata = null;
        if (includeP5Descendants)
        {
            var intentOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    firstCandidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var intentOwnership = Assert.IsType<RetainedStateOwnership>(
                intentOwnershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
                firstCandidate,
                fixture.Time.UnixSeconds,
                firstPrepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var intentRequest));
            using var intent = await PersistP5AndCleanupAnchorAsync(
                fixture,
                intentOwnership,
                intentRequest!);
            intentMetadata = intent.Metadata;

            var failureOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    firstCandidate,
                    prior: null,
                    expectedP5Records: [intent],
                    CancellationToken.None);
            using var failureOwnership = Assert.IsType<RetainedStateOwnership>(
                failureOwnershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
                firstCandidate,
                BoundedGitHubPublisherOutcome.OutcomeUnknown,
                StickyPublicationReason.ReconciliationIncomplete,
                fixture.Time.UnixSeconds,
                firstPrepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var failureRequest));
            using var failure = await PersistP5AndCleanupAnchorAsync(
                fixture,
                failureOwnership,
                failureRequest!);
            failureMetadata = failure.Metadata;
        }

        fixture.Time.UnixSeconds =
            firstPrepared.Header.LogicalExpiresAtUnixSeconds +
            (includeP5Descendants
                ? PublicationRecoveryRetention.PreStickyBudgetSeconds + 1
                : 1);
        fixture.Context.Dispose();
        fixture = await RestoreFixtureAsync(fixture, newWorkflowRun: true);
        using var processB = fixture.Context;
        var secondRun = await CompleteRunAsync(fixture, "successor");
        Assert.True(R4PreparedPublication.TryCreate(
            secondRun.Outcome,
            fixture.PublicationScope,
            out var secondPublication));
        var secondPreparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                processB,
                secondRun.Run,
                secondPublication!,
                CancellationToken.None);
        using var secondPrepared = Assert.IsType<RetainedStatePreparedCandidate>(
            secondPreparedResult.Value);
        var uploadsBefore = fixture.Store.UploadCalls;
        fixture.Store.NextUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
        fixture.Store.NextUploadMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        fixture.Store.PersistFailedUpload = true;

        var secondPersistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                processB,
                secondPrepared,
                CancellationToken.None);
        var secondCandidate = Assert.IsType<RetainedStatePersistedCandidate>(
            secondPersistedResult.Value);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        Assert.Equal(2, fixture.Store.Objects.Count(item =>
            item.Reference.Name == firstPrepared.Name));
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                processB,
                CancellationToken.None);
        Assert.True(recoveredResult.Succeeded, recoveredResult.Code);
        using var recoveredPrepared = recoveredResult.Value!.Prepared;
        Assert.Equal(secondPrepared.Header, recoveredPrepared.Header);
        var inventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                processB,
                CancellationToken.None);
        Assert.True(inventoryResult.Succeeded, inventoryResult.Code);
        using (var inventory = Assert.IsType<
            RetainedStatePublicationRecoveryInventory>(inventoryResult.Value))
        {
            Assert.Equal(secondPrepared.Header, inventory.Candidate!.Header);
        }
        var retried = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                processB,
                secondPrepared,
                CancellationToken.None);
        Assert.True(retried.Succeeded, retried.Code);
        Assert.Equal(uploadsBefore + 1, fixture.Store.UploadCalls);
        Assert.Equal(secondCandidate.Metadata, retried.Value!.Metadata);
        var owned = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                processB,
                secondCandidate,
                prior: null,
                expectedP5Records: [],
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            owned.Value);
        Assert.Equal(2, fixture.Store.Objects.Count(item =>
            item.Reference.Name == firstPrepared.Name));
        if (includeP5Descendants)
        {
            Assert.Contains(intentMetadata!, fixture.Store.Objects);
            Assert.Contains(failureMetadata!, fixture.Store.Objects);
        }
    }

    [Theory]
    [MemberData(nameof(R4StickyPublicationByteVectors.Names),
        MemberType = typeof(R4StickyPublicationByteVectors))]
    public async Task FrozenP1VectorPersistsAndRecoversByteExactlyThroughS6(
        string name)
    {
        var fixture = await CreateFixtureAsync();
        var template = await CompleteRunAsync(fixture);
        var terminal = R4StickyPublicationByteVectors.TerminalReview(
            name,
            template.Run.ReviewedIdentity,
            fixture.PublicationScope);
        var outcome = await CompleteGroundedTerminalReviewAsync(
            template.Run,
            terminal);
        Assert.True(R4PreparedPublication.TryCreate(
            outcome,
            fixture.PublicationScope,
            out var publication));
        Assert.True(publication!.TryProject(
            out _,
            out var rendered,
            out _));
        Assert.NotNull(rendered);
        var expectedComment = rendered!.Comment;
        Assert.True(
            expectedComment.EnumerateRunes().Count() >=
                R4PublicationBudget.MaximumScalars - 1_024 ||
            Encoding.UTF8.GetByteCount(expectedComment) >=
                R4PublicationBudget.MaximumUtf8Bytes - 1_024);

        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                template.Run,
                publication,
                CancellationToken.None);
        Assert.True(preparedResult.Succeeded, preparedResult.Code);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            prepared.Publication,
            out var originalPublication));
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            prepared.Generation,
            out var originalGeneration));
        Assert.InRange(
            originalGeneration.Length,
            1,
            AcceptedStateFormat.MaximumGenerationPayloadBytes);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                prepared,
                CancellationToken.None);
        Assert.True(persistedResult.Succeeded, persistedResult.Code);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true);
        using var resumedContext = resumed.Context;
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumedContext,
                CancellationToken.None);
        Assert.True(recoveredResult.Succeeded, recoveredResult.Code);
        var recovered = Assert.IsType<RetainedStatePersistedCandidate>(
            recoveredResult.Value);
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            recovered.Prepared.Publication,
            out var recoveredPublication));
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            recovered.Prepared.Generation,
            out var recoveredGeneration));
        Assert.True(originalPublication.AsSpan().SequenceEqual(
            recoveredPublication));
        Assert.True(originalGeneration.AsSpan().SequenceEqual(
            recoveredGeneration));
        Assert.Equal(
            Encoding.UTF8.GetBytes(expectedComment),
            recovered.Prepared.Publication.FinalizedCommentUtf8);
        Assert.True(StickyCommentSerializer.TrySerialize(
            Encoding.UTF8.GetString(
                recovered.Prepared.Publication.FinalizedCommentUtf8.AsSpan()),
            out var requestBytes));
        Assert.InRange(
            requestBytes!.Length,
            1,
            BoundedGitHubPublisherPolicy.MaximumStickyRequestBytes);
    }

    [Fact]
    public async Task MaximumSessionCompositePersistsAndRecoversThroughS6()
    {
        var fixture = await CreateFixtureAsync();
        var initial = await CompleteRunAsync(fixture);
        var template = initial with
        {
            Outcome = await CompleteGroundedTerminalReviewAsync(
                initial.Run,
                new AgentTerminalReview(
                    "complete",
                    [
                        new AgentFinding(
                            "high",
                            "grounded",
                            "grounded message",
                            [
                                new AgentEvidence(
                                    new string('a', 64),
                                    "src/max.cs",
                                    1,
                                    1),
                            ]),
                    ],
                    new string('b', 64),
                    [1])),
        };
        var messageCount = template.Outcome.Events
            .OfType<AgentMessageEvent>()
            .Count(message => message.Contents.Any(part =>
                part is AgentToolCallReferencePart));
        Assert.Equal(2, messageCount);
        var paddingParts = checked(
            messageCount * (AgentLimits.PartsPerMessage - 2));
        var minimumPadding = paddingParts;
        var minimumOutcome = WithFixedTextPadding(
            template.Outcome,
            minimumPadding,
            paddingParts);
        var minimumBuild = BuildSession(
            fixture,
            template.Run,
            minimumOutcome);
        Assert.True(minimumBuild.Succeeded, minimumBuild.FailureCode);
        var minimumArtifact = Assert.IsType<AgentSessionArtifact>(
            minimumBuild.Artifact);
        var low = minimumPadding;
        var high = Math.Min(
            paddingParts * AgentLimits.ContentBytes,
            minimumPadding + AgentLimits.SessionPlaintextBytes -
                minimumArtifact.Plaintext.Length);
        CryptographicOperations.ZeroMemory(minimumArtifact.Plaintext);
        var exactPadding = minimumPadding;
        while (low <= high)
        {
            var middle = low + (high - low) / 2;
            var candidateOutcome = WithFixedTextPadding(
                template.Outcome,
                middle,
                paddingParts);
            var candidateBuild = BuildSession(
                fixture,
                template.Run,
                candidateOutcome);
            if (candidateBuild.Succeeded)
            {
                CryptographicOperations.ZeroMemory(
                    candidateBuild.Artifact!.Plaintext);
                exactPadding = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        var outcome = WithFixedTextPadding(
            template.Outcome,
            exactPadding,
            paddingParts);
        var exactBuild = BuildSession(fixture, template.Run, outcome);
        Assert.True(exactBuild.Succeeded, exactBuild.FailureCode);
        var exactArtifact = Assert.IsType<AgentSessionArtifact>(
            exactBuild.Artifact);
        Assert.InRange(
            exactArtifact.Plaintext.Length,
            1,
            AgentLimits.SessionPlaintextBytes);
        if (exactPadding < paddingParts * AgentLimits.ContentBytes)
        {
            var over = BuildSession(
                fixture,
                template.Run,
                WithFixedTextPadding(
                    template.Outcome,
                    exactPadding + 1,
                    paddingParts));
            Assert.False(over.Succeeded);
        }
        Assert.True(R4PreparedPublication.TryCreate(
            outcome,
            fixture.PublicationScope,
            out var publication));

        var preparedResult = await RestrictedStateService
            .PrepareRetainedCandidateAsync(
                fixture.Context,
                template.Run,
                publication!,
                CancellationToken.None);
        Assert.True(preparedResult.Succeeded, preparedResult.Code);
        using var prepared = Assert.IsType<RetainedStatePreparedCandidate>(
            preparedResult.Value);
        Assert.Equal(
            exactArtifact.SessionSha256,
            prepared.Generation.SessionSha256);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            prepared.Generation,
            out var originalGeneration));
        Assert.InRange(
            originalGeneration.Length,
            1,
            AcceptedStateFormat.MaximumGenerationPayloadBytes);
        var persistedResult = await RestrictedStateService
            .PersistRetainedCandidateAsync(
                fixture.Context,
                prepared,
                CancellationToken.None);
        Assert.True(persistedResult.Succeeded, persistedResult.Code);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture with { CurrentReview = User("x") },
            newWorkflowRun: true);
        using var resumedContext = resumed.Context;
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumedContext,
                CancellationToken.None);
        Assert.True(recoveredResult.Succeeded, recoveredResult.Code);
        var recovered = Assert.IsType<RetainedStatePersistedCandidate>(
            recoveredResult.Value);
        Assert.Equal(
            exactArtifact.SessionSha256,
            recovered.Prepared.Generation.SessionSha256);
        Assert.True(AcceptedStateGenerationRecordCodec.TryEncode(
            recovered.Prepared.Generation,
            out var recoveredGeneration));
        Assert.True(originalGeneration.AsSpan().SequenceEqual(
            recoveredGeneration));
        CryptographicOperations.ZeroMemory(exactArtifact.Plaintext);
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
        var semanticHorizon = handoff!.MinimumSemanticExpiresAtUnixSeconds;
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
        handoff = null;

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
            .RecoverAnchoredRetainedOpaqueWritesAsync(
                resumed.Context,
                recoveredCandidate,
                CancellationToken.None);
        using var recoveredWrites = Assert.IsType<
            RetainedStateOpaqueWriteAttemptSet>(recoveredWriteResult.Value);
        var recoveredWrite = Assert.Single(recoveredWrites.Attempts);
        Assert.Equal(
            semanticHorizon,
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
    public async Task DurableOpaqueAnchorBlocksEverySiblingAfterPreDispatchCrash()
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
        var request = new RetainedStateOpaqueWriteRequest(
            StateObjectClass.PublicationIntent,
            ImmutableArray.CreateRange("intent-before-crash"u8.ToArray()),
            prepared.Header.ObjectIdentity,
            SuccessorIdentity: null,
            fixture.Time.UnixSeconds +
                StateRetentionRequirements.LogicalWindowSeconds);
        var preparedWriteResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                fixture.Context,
                ownership,
                request,
                CancellationToken.None);
        using var preparedWrite = Assert.IsType<
            RetainedStateOpaqueWriteAttempt>(preparedWriteResult.Value);
        Assert.False(preparedWrite.ReconcileOnly);
        Assert.Contains(preparedWrite.AnchorMetadata, fixture.Store.Objects);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            rotateStateKey: true);
        using var resumedContext = resumed.Context;
        var recoveredCandidateResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumedContext,
                CancellationToken.None);
        var recoveredCandidate = Assert.IsType<
            RetainedStatePersistedCandidate>(recoveredCandidateResult.Value);
        var recoveredWritesResult = await RestrictedStateService
            .RecoverAnchoredRetainedOpaqueWritesAsync(
                resumedContext,
                recoveredCandidate,
                CancellationToken.None);
        using var recoveredWrites = Assert.IsType<
            RetainedStateOpaqueWriteAttemptSet>(recoveredWritesResult.Value);
        var recoveredWrite = Assert.Single(recoveredWrites.Attempts);
        Assert.True(recoveredWrite.ReconcileOnly);
        Assert.Equal(preparedWrite.OperationIdentity,
            recoveredWrite.OperationIdentity);
        var uploadsBeforeReconcile = fixture.Store.UploadCalls;

        var reconcileOnly = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                resumedContext,
                recoveredWrite,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            reconcileOnly.Code);
        Assert.Equal(uploadsBeforeReconcile, fixture.Store.UploadCalls);

        var siblingRequests = new[]
        {
            request,
            request with
            {
                Payload = ImmutableArray.CreateRange(
                    "different-payload"u8.ToArray()),
            },
            request with
            {
                ObjectClass = StateObjectClass.PublicationFailure,
            },
        };
        foreach (var siblingRequest in siblingRequests)
        {
            var freshOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    resumedContext,
                    recoveredCandidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var freshOwnership = Assert.IsType<RetainedStateOwnership>(
                freshOwnershipResult.Value);
            var sibling = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    resumedContext,
                    freshOwnership,
                    siblingRequest,
                    CancellationToken.None);
            Assert.Equal(RetainedStateTransactionCodes.Conflict, sibling.Code);
            Assert.Null(sibling.Value);
            Assert.Equal(uploadsBeforeReconcile, fixture.Store.UploadCalls);
        }
    }

    [Fact]
    public async Task PossibleCommitOpaqueAnchorRecoversAndBlocksEverySibling()
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
        var request = new RetainedStateOpaqueWriteRequest(
            StateObjectClass.PublicationFailure,
            ImmutableArray.CreateRange("anchor-possible-commit"u8.ToArray()),
            prepared.Header.ObjectIdentity,
            SuccessorIdentity: null,
            fixture.Time.UnixSeconds +
                StateRetentionRequirements.LogicalWindowSeconds);
        var uploadsBeforeAnchor = fixture.Store.UploadCalls;
        fixture.Store.NextUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
        fixture.Store.NextUploadMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        fixture.Store.PersistFailedUpload = true;
        fixture.Store.HideUploadedObjectOnUploadCall =
            uploadsBeforeAnchor + 1;
        fixture.Store.HideNextUploadedObjectForNextLists = 3;

        var uncertainAnchor = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                fixture.Context,
                ownership,
                request,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            uncertainAnchor.Code);
        Assert.Null(uncertainAnchor.Value);
        Assert.Equal(uploadsBeforeAnchor + 1, fixture.Store.UploadCalls);

        fixture.Context.Dispose();
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            rotateStateKey: true);
        using var resumedContext = resumed.Context;
        var recoveredCandidateResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                resumedContext,
                CancellationToken.None);
        var recoveredCandidate = Assert.IsType<
            RetainedStatePersistedCandidate>(recoveredCandidateResult.Value);
        var recoveredWritesResult = await RestrictedStateService
            .RecoverAnchoredRetainedOpaqueWritesAsync(
                resumedContext,
                recoveredCandidate,
                CancellationToken.None);
        using var recoveredWrites = Assert.IsType<
            RetainedStateOpaqueWriteAttemptSet>(recoveredWritesResult.Value);
        var recoveredWrite = Assert.Single(recoveredWrites.Attempts);
        Assert.True(recoveredWrite.ReconcileOnly);
        Assert.Equal(StateObjectClass.PublicationFailure,
            recoveredWrite.ObjectClass);
        var uploadsBeforeReconcile = fixture.Store.UploadCalls;

        var unresolved = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                resumedContext,
                recoveredWrite,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.OutcomeUnknown,
            unresolved.Code);
        Assert.Equal(uploadsBeforeReconcile, fixture.Store.UploadCalls);

        var siblingRequests = new[]
        {
            request with
            {
                Payload = ImmutableArray.CreateRange(
                    "different-possible-payload"u8.ToArray()),
            },
            request with
            {
                ObjectClass = StateObjectClass.Abandonment,
            },
            request,
        };
        foreach (var siblingRequest in siblingRequests)
        {
            var freshOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    resumedContext,
                    recoveredCandidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var freshOwnership = Assert.IsType<RetainedStateOwnership>(
                freshOwnershipResult.Value);
            var sibling = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    resumedContext,
                    freshOwnership,
                    siblingRequest,
                    CancellationToken.None);
            Assert.Equal(RetainedStateTransactionCodes.Conflict, sibling.Code);
            Assert.Null(sibling.Value);
            Assert.Equal(uploadsBeforeReconcile, fixture.Store.UploadCalls);
        }
    }

    [Fact]
    public async Task P5AuthorizedCleanupDeletesOpaqueRecordAndWriteAnchor()
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
        Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
            candidate,
            BoundedGitHubPublisherOutcome.KnownNotWritten,
            StickyPublicationReason.Deadline,
            fixture.Time.UnixSeconds,
            prepared.Header.LogicalExpiresAtUnixSeconds,
            out var failure,
            out var failureRequest));
        var semanticExpiry =
            failureRequest!.SemanticRequiredExpiresAtUnixSeconds;
        var writeResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                context,
                ownership,
                failureRequest,
                CancellationToken.None);
        using var write = Assert.IsType<RetainedStateOpaqueWriteAttempt>(
            writeResult.Value);
        var persistedRecordResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                context,
                write,
                CancellationToken.None);
        using var record = Assert.IsType<RetainedStateOpaqueRecord>(
            persistedRecordResult.Value);
        Assert.True(RestrictedStateService.TryCopyRetainedStateOpaquePayload(
            context,
            record,
            out var failurePayload));
        Assert.True(PublicationFailureV1Codec.TryDecode(
            failurePayload.AsSpan(),
            out var decodedFailure));
        Assert.Equal(failure, decodedFailure);

        var recordAuthorizationResult = await RestrictedStateService
            .AuthorizeRetainedP5CleanupAsync(
                context,
                new RetainedStateP5CleanupDecision(
                    RetainedStateP5CleanupClassification
                        .CompletedOpaqueRecord,
                    OpaqueStoreHash.Sha256("p5-record-complete"u8),
                    MarkerEvidenceIdentity: null),
                pendingCandidate: null,
                record,
                opaqueWrite: null,
                recoveryInventory: null,
                recoveryAnchor: null,
                CancellationToken.None);
        using var recordAuthorization = Assert.IsType<
            RetainedStateP5CleanupAuthorization>(
                recordAuthorizationResult.Value);
        var recordCleanup = await RestrictedStateService
            .CleanupRetainedP5AuthorizedAsync(
                context,
                new RetainedStateP5CleanupRequest(
                    recordAuthorization,
                    semanticExpiry),
                CancellationToken.None);
        Assert.True(recordCleanup.Completed, recordCleanup.Code);
        Assert.DoesNotContain(record.Metadata, fixture.Store.Objects);

        var anchorAuthorizationResult = await RestrictedStateService
            .AuthorizeRetainedP5CleanupAsync(
                context,
                new RetainedStateP5CleanupDecision(
                    RetainedStateP5CleanupClassification
                        .CompletedOpaqueWriteAnchor,
                    OpaqueStoreHash.Sha256("p5-anchor-complete"u8),
                    MarkerEvidenceIdentity: null),
                pendingCandidate: null,
                opaqueRecord: null,
                write,
                recoveryInventory: null,
                recoveryAnchor: null,
                CancellationToken.None);
        using var anchorAuthorization = Assert.IsType<
            RetainedStateP5CleanupAuthorization>(
                anchorAuthorizationResult.Value);
        var anchorCleanup = await RestrictedStateService
            .CleanupRetainedP5AuthorizedAsync(
                context,
                new RetainedStateP5CleanupRequest(
                    anchorAuthorization,
                    semanticExpiry),
                CancellationToken.None);
        Assert.True(anchorCleanup.Completed, anchorCleanup.Code);
        Assert.DoesNotContain(write.AnchorMetadata, fixture.Store.Objects);
        Assert.Contains(candidate.Metadata, fixture.Store.Objects);
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
    public async Task SupersededTerminalRecoveryCleansP5BeforeProvider()
    {
        var fixture = await CreateFixtureAsync(
            extraRetentionSeconds: 0,
            route: ActionHostAuthorizationRoute.WorkflowDispatch);
        var accepted = await AcceptGenerationAsync(fixture, commentId: 700);
        Assert.Equal(0, accepted.Generation);
        fixture.Context.Dispose();

        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            reviewedHeadSha: new string('f', 40));
        var factory = new FakePublisherTransportFactory();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));
        using var evaluation = await service.ClassifyBeforeProviderAsync(
            resumed.Launch.Inputs.GitHubToken!,
            resumed.Invocation,
            resumed.PublicationScope,
            resumed.Context,
            CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.CleanupSupersededRecovery,
            evaluation.Decision.Action);
        Assert.Equal(
            PublicationRecoveryLifecycleState.SupersededTerminalRecovery,
            evaluation.Decision.Lifecycle);
        Assert.False(evaluation.Decision.AllowsProvider);
        Assert.True(evaluation.Decision.AllowsSupersededCleanup);

        var cleanup = await PublicationRecoveryService
            .CleanupHistoricalRecoveryRecordsAsync(
                resumed.Invocation,
                resumed.Context,
                evaluation,
                CancellationToken.None);
        Assert.True(cleanup.Completed, cleanup.Code);
        var inventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                resumed.Context,
                CancellationToken.None);
        using var inventory = Assert.IsType<
            RetainedStatePublicationRecoveryInventory>(inventoryResult.Value);
        Assert.Empty(inventory.Records);
        Assert.Empty(inventory.Anchors);
        Assert.Null(inventory.Candidate);
        Assert.NotNull(inventory.CurrentAcceptance);
        Assert.Equal(0, factory.Transport.Lists);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
        using var after = await service.ClassifyBeforeProviderAsync(
            resumed.Launch.Inputs.GitHubToken!,
            resumed.Invocation,
            resumed.PublicationScope,
            resumed.Context,
            CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.NoPendingWork,
            after.Decision.Action);
        Assert.Equal(
            PublicationRecoveryLifecycleState.SupersededTerminalRecovery,
            after.Decision.Lifecycle);
        Assert.True(after.Decision.AllowsProvider);
        Assert.False(after.Decision.AllowsSupersededCleanup);
        resumed.Context.Dispose();
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
        var uploadsBeforeDurability = fixture.Store.UploadCalls;
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    fixture.Context,
                    preparation,
                    durability: null!,
                    CancellationToken.None));
        var evidenceWithoutDurability = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                preparation,
                durability: null!,
                preparation.Ownership,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    prepared.Publication.ReviewedHeadSha,
                    prepared.Publication.ReviewedHeadSha),
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            evidenceWithoutDurability.Code);
        Assert.Equal(uploadsBeforeDurability, fixture.Store.UploadCalls);
        var unrelatedRecoveryBytes = WrapP5RecoveryPayload(
            ImmutableArray.CreateRange(
                new byte[handoff!.OpaqueInnerPayload.Length]));
        var unrelatedRecordResult = await PersistOpaqueRecordAsync(
                fixture.Context,
                preparation.Ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationFailure,
                    unrelatedRecoveryBytes,
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var unrelatedRecord = Assert.IsType<RetainedStateOpaqueRecord>(
            unrelatedRecordResult.Value);
        using var unrelatedExtraction = CreateAcceptanceRecoveryExtraction(
            fixture.Context,
            unrelatedRecord,
            handoff.OpaqueInnerPayload.Length);
        var wrongDurability = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                fixture.Context,
                preparation,
                unrelatedExtraction,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            wrongDurability.Code);
        Assert.Null(wrongDurability.Value);
        var uploadsAfterUnrelatedRecord = fixture.Store.UploadCalls;
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    fixture.Context,
                    preparation,
                    wrongDurability.Value!,
                    CancellationToken.None));
        var evidenceWithUnrelatedRecord = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                preparation,
                wrongDurability.Value!,
                preparation.Ownership,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    prepared.Publication.ReviewedHeadSha,
                    prepared.Publication.ReviewedHeadSha),
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            evidenceWithUnrelatedRecord.Code);
        Assert.Null(evidenceWithUnrelatedRecord.Value);
        Assert.Equal(
            uploadsAfterUnrelatedRecord,
            fixture.Store.UploadCalls);

        var ownershipAfterUnrelatedResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                expectedP5Records: [unrelatedRecord],
                CancellationToken.None);
        using var ownershipAfterUnrelated = Assert.IsType<
            RetainedStateOwnership>(ownershipAfterUnrelatedResult.Value);
        var recoveryBytes = WrapP5RecoveryPayload(
            handoff.OpaqueInnerPayload);
        var recordResult = await PersistOpaqueRecordAsync(
                fixture.Context,
                ownershipAfterUnrelated,
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
            RetainedStateTransactionCodes.AccessDenied,
            RestrictedStateService
                .CreateRetainedStateOpaquePayloadExtraction(
                    fixture.Context,
                    record,
                    payloadOffset: -1,
                    payloadLength: handoff.OpaqueInnerPayload.Length)
                .Code);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            RestrictedStateService
                .CreateRetainedStateOpaquePayloadExtraction(
                    fixture.Context,
                    record,
                    P5RecoveryPayloadPrefix.Length,
                    handoff.OpaqueInnerPayload.Length + 1)
                .Code);
        var shiftedExtractionResult = RestrictedStateService
            .CreateRetainedStateOpaquePayloadExtraction(
                fixture.Context,
                record,
                P5RecoveryPayloadPrefix.Length - 1,
                handoff!.OpaqueInnerPayload.Length);
        using var shiftedExtraction = Assert.IsType<
            RetainedStateOpaquePayloadExtraction>(
                shiftedExtractionResult.Value);
        var shiftedDurability = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                fixture.Context,
                preparation,
                shiftedExtraction,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            shiftedDurability.Code);
        var disposedExtraction = CreateAcceptanceRecoveryExtraction(
            fixture.Context,
            record,
            handoff.OpaqueInnerPayload.Length);
        disposedExtraction.Dispose();
        var disposedDurability = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                fixture.Context,
                preparation,
                disposedExtraction,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            disposedDurability.Code);
        using var extraction = CreateAcceptanceRecoveryExtraction(
            fixture.Context,
            record,
            handoff.OpaqueInnerPayload.Length);
        using var durability = await BindAcceptanceRecoveryAsync(
            fixture.Context,
            preparation,
            extraction);
        var replayedExtraction = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                fixture.Context,
                preparation,
                extraction,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            replayedExtraction.Code);
        Assert.Null(replayedExtraction.Value);
        Assert.Equal(
            RetainedStateTransactionCodes.Ready,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    fixture.Context,
                    preparation,
                    durability,
                    CancellationToken.None));
        var finalOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                expectedP5Records: [unrelatedRecord, record],
                CancellationToken.None);
        using var finalOwnership = Assert.IsType<RetainedStateOwnership>(
            finalOwnershipResult.Value);
        var originalEvidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                fixture.Context,
                preparation,
                durability,
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
        using var recoveredExtraction =
            CreateAcceptanceRecoveryExtraction(
                resumed.Context,
                recoveredRecord,
                recoveredInnerPayload.Length);
        var recoveredPreparationResult = await RestrictedStateService
            .RecoverRetainedStateAcceptancePreparationAsync(
                resumed.Context,
                recoveredCandidate,
                recoveredExtraction,
                CancellationToken.None);
        using var recoveredPreparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(
                recoveredPreparationResult.Value);
        using var recoveredDurability = await BindAcceptanceRecoveryAsync(
            resumed.Context,
            recoveredPreparation,
            recoveredExtraction);
        Assert.Equal(
            RetainedStateTransactionCodes.Ready,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    resumed.Context,
                    recoveredPreparation,
                    recoveredDurability,
                    CancellationToken.None));
        var evidenceResult = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                resumed.Context,
                recoveredPreparation,
                recoveredDurability,
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
        using var completedExtraction =
            CreateAcceptanceRecoveryExtraction(
                afterCommitContext,
                completedRecord,
                handoff.OpaqueInnerPayload.Length);
        var matchedResult = await RestrictedStateService
            .MatchRecoveredRetainedStateAcceptanceAsync(
                afterCommitContext,
                prepared.Header.ObjectIdentity,
                completedRecord,
                completedExtraction,
                verifiedAfterRestart,
                CancellationToken.None);
        var matched = Assert.IsType<
            MatchedRetainedStateRecoveryAcceptance>(matchedResult.Value);
        Assert.Equal(
            verifiedAfterRestart.AcceptanceReceiptIdentity,
            matched.AcceptanceReceiptIdentity);
        Assert.Equal(sticky!.CommentId, matched.Receipt.CommentId);
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
        var unrelatedOuter = WrapP5RecoveryPayload(
            ImmutableArray.CreateRange(
                new byte[handoff!.OpaqueInnerPayload.Length]));
        var unrelatedResult = await PersistOpaqueRecordAsync(
                successor.Context,
                preparation.Ownership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationFailure,
                    unrelatedOuter,
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var unrelatedP5 = Assert.IsType<RetainedStateOpaqueRecord>(
            unrelatedResult.Value);
        using var unrelatedExtraction = CreateAcceptanceRecoveryExtraction(
            successor.Context,
            unrelatedP5,
            handoff.OpaqueInnerPayload.Length);
        var unrelatedDurability = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                successor.Context,
                preparation,
                unrelatedExtraction,
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            unrelatedDurability.Code);
        var uploadsBeforeRejectedCopy = successor.Store.UploadCalls;
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    successor.Context,
                    preparation,
                    unrelatedDurability.Value!,
                    CancellationToken.None));
        var rejectedEvidence = await RestrictedStateService
            .CreateRetainedStateAcceptanceEvidenceAsync(
                successor.Context,
                preparation,
                unrelatedDurability.Value!,
                preparation.Ownership,
                new ExactHeadRevalidationResult(
                    ExactHeadRevalidationStatus.Exact,
                    prepared.Publication.ReviewedHeadSha,
                    prepared.Publication.ReviewedHeadSha),
                CancellationToken.None);
        Assert.Equal(
            RetainedStateTransactionCodes.AccessDenied,
            rejectedEvidence.Code);
        Assert.Equal(
            uploadsBeforeRejectedCopy,
            successor.Store.UploadCalls);

        var ownershipAfterUnrelatedResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                successor.Context,
                candidate,
                prior: null,
                expectedP5Records: [.. existing.Records, unrelatedP5],
                CancellationToken.None);
        using var ownershipAfterUnrelated = Assert.IsType<
            RetainedStateOwnership>(ownershipAfterUnrelatedResult.Value);
        var outer = WrapP5RecoveryPayload(handoff!.OpaqueInnerPayload);
        var p5Result = await PersistOpaqueRecordAsync(
                successor.Context,
                ownershipAfterUnrelated,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.PublicationIntent,
                    outer,
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var p5 = Assert.IsType<RetainedStateOpaqueRecord>(p5Result.Value);
        using var p5Extraction = CreateAcceptanceRecoveryExtraction(
            successor.Context,
            p5,
            handoff.OpaqueInnerPayload.Length);
        using var staleDurability = await BindAcceptanceRecoveryAsync(
            successor.Context,
            preparation,
            p5Extraction);
        var driftOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                successor.Context,
                candidate,
                prior: null,
                expectedP5Records:
                    [.. existing.Records, unrelatedP5, p5],
                CancellationToken.None);
        using var driftOwnership = Assert.IsType<RetainedStateOwnership>(
            driftOwnershipResult.Value);
        var driftRecordResult = await PersistOpaqueRecordAsync(
                successor.Context,
                driftOwnership,
                new RetainedStateOpaqueWriteRequest(
                    StateObjectClass.Abandonment,
                    ImmutableArray.CreateRange(
                        "classified-after-binding"u8.ToArray()),
                    prepared.Header.ObjectIdentity,
                    SuccessorIdentity: null,
                    handoff.MinimumSemanticExpiresAtUnixSeconds),
                CancellationToken.None);
        using var driftRecord = Assert.IsType<RetainedStateOpaqueRecord>(
            driftRecordResult.Value);
        var uploadsAfterInventoryDrift = successor.Store.UploadCalls;
        Assert.Equal(
            RetainedStateTransactionCodes.Conflict,
            await RestrictedStateService
                .ReconcileRetainedStateAcceptancePredecessorAsync(
                    successor.Context,
                    preparation,
                    staleDurability,
                    CancellationToken.None));
        Assert.Equal(
            uploadsAfterInventoryDrift,
            successor.Store.UploadCalls);
        var refreshedP5Result = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                successor.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var refreshedP5Records = Assert.IsType<
            RetainedStateOpaqueRecordSet>(refreshedP5Result.Value);
        var refreshedP5 = Assert.Single(refreshedP5Records.Records.Where(
            record => record.Metadata == p5.Metadata));
        using var freshExtraction = CreateAcceptanceRecoveryExtraction(
            successor.Context,
            refreshedP5,
            handoff.OpaqueInnerPayload.Length);
        using var durability = await BindAcceptanceRecoveryAsync(
            successor.Context,
            preparation,
            freshExtraction);
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
                durability,
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
        RetainedStateOpaqueRecord? recoveredP5 = null;
        RetainedStateOpaquePayloadExtraction? matchingExtraction = null;
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
                recoveredP5 = record;
                matchingExtraction = CreateAcceptanceRecoveryExtraction(
                    resumed.Context,
                    record,
                    inner.Length);
                break;
            }
        }

        Assert.NotNull(recoveredP5);
        using var recoveredExtraction = Assert.IsType<
            RetainedStateOpaquePayloadExtraction>(matchingExtraction);
        var recoveredPreparationResult = await RestrictedStateService
            .RecoverRetainedStateAcceptancePreparationAsync(
                resumed.Context,
                recoveredCandidate,
                recoveredExtraction,
                CancellationToken.None);
        using var recoveredPreparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(
                recoveredPreparationResult.Value);
        using var recoveredDurability = await BindAcceptanceRecoveryAsync(
            resumed.Context,
            recoveredPreparation,
            recoveredExtraction);
        var uploadsBeforeRecoveryReconcile = resumed.Store.UploadCalls;
        var cancelled = new CancellationToken(canceled: true);
        var reconciled = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                resumed.Context,
                recoveredPreparation,
                recoveredDurability,
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
                    recoveredDurability,
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

    [Theory]
    [InlineData((int)StickyPublicationOperation.Create)]
    [InlineData((int)StickyPublicationOperation.Update)]
    public async Task RealMutationReceiptSurvivesReadbackAndAcceptanceRestarts(
        int operationValue)
    {
        var operation = (StickyPublicationOperation)operationValue;
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
        var intentResult = await PublicationRecoveryPersistence
            .PersistIntentAndAuthorizeAsync(
                fixture.Context,
                before.Observation!,
                CancellationToken.None);
        using var intent = Assert.IsType<PublicationIntentPersistenceResult>(
            intentResult.Value);
        Assert.True(PublicationRecoveryService.TryRestoreRendered(
            intent.Observation.StoredPublication,
            out var rendered));
        Assert.True(AuthorizedStickyPublicationRequest.TryCreateRecovery(
            fixture.Invocation,
            fixture.PublicationScope,
            rendered,
            intent.Observation,
            intent.StickyWriteAuthorization,
            out var request));

        var commentId = operation == StickyPublicationOperation.Create
            ? 904
            : 905;
        var exactComment = StickyPublicationTestData.Comment(
            commentId,
            rendered!.Comment);
        var writeFactory = new FakePublisherTransportFactory();
        if (operation == StickyPublicationOperation.Create)
        {
            writeFactory.Transport.Enqueue();
        }
        else
        {
            writeFactory.Transport.Enqueue(exactComment);
        }

        writeFactory.Transport.Mutation = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(exactComment);
        writeFactory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(exactComment);
        writeFactory.Transport.Enqueue(exactComment);
        var published = await new StickyCommentPublisher(writeFactory)
            .PublishAsync(
                fixture.Launch.Inputs.GitHubToken!,
                request!,
                CancellationToken.None);
        Assert.Equal(
            BoundedGitHubPublisherOutcome.WrittenAndReadBack,
            published.Outcome);
        var durableReceipt = Assert.IsType<
            StickyCommentPublisher.StickyPublicationReceipt>(
                published.Receipt);
        Assert.Equal(operation, durableReceipt.Operation);
        Assert.Equal(
            operation == StickyPublicationOperation.Create ? 1 : 0,
            writeFactory.Transport.Creates);
        Assert.Equal(
            operation == StickyPublicationOperation.Update ? 1 : 0,
            writeFactory.Transport.Updates);

        var readbackOwnershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                intent.Observation.Records,
                CancellationToken.None);
        using var readbackOwnership = Assert.IsType<RetainedStateOwnership>(
            readbackOwnershipResult.Value);
        Assert.True(PublicationRecoveryPersistence
            .TryCreateStickyReadbackWrite(
                candidate,
                durableReceipt,
                fixture.Time.UnixSeconds,
                prepared.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var readbackRequest));
        using var readbackRecord = await PersistP5AndCleanupAnchorAsync(
            fixture,
            readbackOwnership,
            readbackRequest!);

        fixture.Context.Dispose();
        fixture = await RestoreFixtureAsync(fixture);
        var readbackFactory = ExactReadbackFactory(exactComment);
        using (var readback = await new PublicationRecoveryService(
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
                readback.Decision.Action);
            var canonicalReceipt = Assert.IsType<
                StickyCommentPublisher.StickyPublicationReceipt>(
                    readback.ExactReadbackReceipt);
            Assert.Equal(operation, canonicalReceipt.Operation);
            Assert.Equal(0, readbackFactory.Transport.Creates);
            Assert.Equal(0, readbackFactory.Transport.Updates);

            var recoveredResult = await RestrictedStateService
                .RecoverRetainedCandidateAsync(
                    fixture.Context,
                    CancellationToken.None);
            var recovered = Assert.IsType<RetainedStatePersistedCandidate>(
                recoveredResult.Value);
            var acceptanceOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    recovered,
                    prior: null,
                    readback.Observation!.Records,
                    CancellationToken.None);
            using var acceptanceOwnership = Assert.IsType<
                RetainedStateOwnership>(acceptanceOwnershipResult.Value);
            using var evidence = await CreateFinalEvidenceAsync(
                fixture,
                recovered,
                acceptanceOwnership,
                canonicalReceipt,
                readback.Observation.Records,
                existingStickyReadback:
                    readback.Observation.StickyReadback);
            var acceptedResult = await RestrictedStateService
                .AcceptRetainedStateAsync(
                    fixture.Context,
                    evidence,
                    CancellationToken.None);
            Assert.Equal(
                RetainedStateTransactionCodes.Accepted,
                acceptedResult.Code);
        }

        fixture.Context.Dispose();
        fixture = await RestoreFixtureAsync(fixture);
        using var processC = fixture.Context;
        var committedFactory = ExactReadbackFactory(exactComment);
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
        var committedReceipt = Assert.IsType<
            StickyCommentPublisher.StickyPublicationReceipt>(
                committed.ExactReadbackReceipt);
        Assert.Equal(operation, committedReceipt.Operation);
        Assert.Equal(commentId, committedReceipt.CommentId);
        Assert.Equal(0, committedFactory.Transport.Creates);
        Assert.Equal(0, committedFactory.Transport.Updates);
    }

    [Fact]
    public async Task StaleBootstrapCandidateCanBeInspectedAndP5AuthorizedAway()
    {
        var fixture = await CreateFixtureAsync(
            route: ActionHostAuthorizationRoute.WorkflowDispatch);
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
        var persisted = Assert.IsType<RetainedStatePersistedCandidate>(
            persistedResult.Value);
        var target = persisted.Metadata;
        fixture.Context.Dispose();

        var nextHead = new string('f', 40);
        var resumed = await RestoreFixtureAsync(
            fixture,
            newWorkflowRun: true,
            reviewedHeadSha: nextHead);
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue();
        factory.Transport.Enqueue();
        var service = new PublicationRecoveryService(
            new StickyCommentPublisher(factory));
        using var evaluation = await service.ClassifyBeforeProviderAsync(
            resumed.Launch.Inputs.GitHubToken!,
            resumed.Invocation,
            resumed.PublicationScope,
            resumed.Context,
            CancellationToken.None);
        Assert.Equal(
            PublicationRecoveryAction.AbandonStaleCandidate,
            evaluation.Decision.Action);
        Assert.NotNull(evaluation.MarkerAbsenceEvidence);

        var cleaned = await service.AbandonAndCleanupStaleCandidateAsync(
            resumed.Launch.Inputs.GitHubToken!,
            resumed.Invocation,
            resumed.PublicationScope,
            resumed.Context,
            evaluation,
            CancellationToken.None);

        Assert.True(cleaned.Completed, cleaned.Code);
        Assert.DoesNotContain(target, resumed.Store.Objects);
        Assert.Equal(2, factory.Transport.Lists);
        Assert.Equal(0, factory.Transport.Creates);
        Assert.Equal(0, factory.Transport.Updates);
        var after = await RestrictedStateService
            .InspectRetainedPendingCandidateAsync(
                resumed.Context,
                CancellationToken.None);
        Assert.Equal(RetainedStateTransactionCodes.Conflict, after.Code);
        resumed.Context.Dispose();
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

    internal static async Task<TransactionFixture> CreateFixtureAsync(
        long extraRetentionSeconds = 3_600,
        ActionHostAuthorizationRoute route =
            ActionHostAuthorizationRoute.WorkflowRun)
    {
        var scenario = ActionHostAuthorizationScenario.Valid(
            route);
        var launch = StateLaunch(scenario.Launch, currentKeyByte: 0x42);
        var authorization = await scenario.CreateAuthorizer().AuthorizeAsync(
            launch,
            CancellationToken.None);
        Assert.Equal(
            ActionHostAuthorizationFailure.None,
            authorization.Failure);
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

    internal static async Task<TransactionFixture> RestoreFixtureAsync(
        TransactionFixture fixture,
        bool newWorkflowRun = false,
        bool rotateStateKey = false,
        string? reviewedHeadSha = null,
        string? ancestryPreviousHeadSha = null)
    {
        var launch = fixture.Launch;
        var invocation = fixture.Invocation;
        if (newWorkflowRun)
        {
            if (reviewedHeadSha is not null)
            {
                var pullRequest = fixture.Scenario.Transport.PullRequest with
                {
                    HeadSha = reviewedHeadSha,
                };
                fixture.Scenario.Transport.PullRequest = pullRequest;
                fixture.Scenario.Transport.AssociatedPages =
                [
                    new ActionHostGitHubPullRequestPageFact(
                        [pullRequest],
                        true),
                ];
            }

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
            Assert.Equal(
                ActionHostAuthorizationFailure.None,
                authorization.Failure);
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
                    new TestDependencies(
                        fixture.Store,
                        reviewedHeadSha,
                        ancestryPreviousHeadSha ??
                            fixture.Invocation.PullRequest.HeadSha),
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

    internal static async Task<AcceptedGeneration> AcceptGenerationAsync(
        TransactionFixture fixture,
        long commentId,
        bool exercisePredecessorCopyPossibleCommit = false,
        bool persistOutcomeUnknownFailure = false)
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
        if (persistOutcomeUnknownFailure)
        {
            var intentOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [],
                    CancellationToken.None);
            using var intentOwnership = Assert.IsType<RetainedStateOwnership>(
                intentOwnershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateIntentWrite(
                candidate,
                fixture.Time.UnixSeconds,
                preparedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var intentRequest));
            using var intentRecord = await PersistP5AndCleanupAnchorAsync(
                fixture,
                intentOwnership,
                intentRequest!);

            var failureOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    expectedP5Records: [intentRecord],
                    CancellationToken.None);
            using var failureOwnership = Assert.IsType<RetainedStateOwnership>(
                failureOwnershipResult.Value);
            Assert.True(PublicationRecoveryPersistence.TryCreateFailureWrite(
                candidate,
                BoundedGitHubPublisherOutcome.OutcomeUnknown,
                StickyPublicationReason.ReconciliationIncomplete,
                fixture.Time.UnixSeconds,
                preparedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out _,
                out var failureRequest));
            using var failureRecord = await PersistP5AndCleanupAnchorAsync(
                fixture,
                failureOwnership,
                failureRequest!);
        }

        var existingP5Result = await RestrictedStateService
            .QueryRetainedOpaqueRecordsAsync(
                fixture.Context,
                StateObjectClass.PublicationIntent,
                CancellationToken.None);
        using var existingP5 = Assert.IsType<RetainedStateOpaqueRecordSet>(
            existingP5Result.Value);
        RetainedStateOpaqueRecordSet? existingFailures = null;
        if (persistOutcomeUnknownFailure)
        {
            var failureResult = await RestrictedStateService
                .QueryRetainedOpaqueRecordsAsync(
                    fixture.Context,
                    StateObjectClass.PublicationFailure,
                    CancellationToken.None);
            existingFailures = Assert.IsType<RetainedStateOpaqueRecordSet>(
                failureResult.Value);
        }
        using var existingFailureLifetime = existingFailures;
        var existingP5Records = existingFailures is null
            ? existingP5.Records
            : existingP5.Records.AddRange(existingFailures.Records);
        var owned = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                existingP5Records,
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
            existingP5Records);
        var accepted = await RestrictedStateService.AcceptRetainedStateAsync(
            fixture.Context,
            evidence,
            CancellationToken.None);
        Assert.Equal(RetainedStateTransactionCodes.Accepted, accepted.Code);
        var verified = Assert.IsType<VerifiedRetainedStateAcceptance>(
            accepted.Value);
        Assert.True(AcceptedStatePublicationPayloadCodec.TryEncode(
            preparedCandidate.Publication,
            out var publicationBytes));
        Assert.True(AcceptedStatePublicationPayloadCodec.TryDecode(
            publicationBytes,
            out var retainedPublication));
        CryptographicOperations.ZeroMemory(publicationBytes);
        return new AcceptedGeneration(
            preparedCandidate.Generation.Generation,
            preparedCandidate.Name,
            candidate.Metadata,
            verified,
            retainedPublication!);
    }

    internal static async Task<VerifiedRetainedStateAcceptance>
        AcceptRecoveredCandidateAsync(
        TransactionFixture fixture,
        PublicationRecoveryEvaluation recovery)
    {
        Assert.Equal(
            PublicationRecoveryAction.CompleteAcceptance,
            recovery.Decision.Action);
        var observation = Assert.IsType<PublicationRecoveryObservation>(
            recovery.Observation);
        var receipt = Assert.IsType<
            StickyCommentPublisher.StickyPublicationReceipt>(
                recovery.ExactReadbackReceipt);
        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(
                fixture.Context,
                CancellationToken.None);
        var candidate = Assert.IsType<RetainedStatePersistedCandidate>(
            recoveredResult.Value);
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                fixture.Context,
                candidate,
                prior: null,
                observation.Records,
                CancellationToken.None);
        using var ownership = Assert.IsType<RetainedStateOwnership>(
            ownershipResult.Value);
        using var evidence = await CreateFinalEvidenceAsync(
            fixture,
            candidate,
            ownership,
            receipt,
            observation.Records,
            existingStickyReadback: observation.StickyReadback);
        var accepted = await RestrictedStateService.AcceptRetainedStateAsync(
            fixture.Context,
            evidence,
            CancellationToken.None);
        Assert.Equal(RetainedStateTransactionCodes.Accepted, accepted.Code);
        return Assert.IsType<VerifiedRetainedStateAcceptance>(accepted.Value);
    }

    private static async Task<RetainedStateAcceptanceEvidence>
        CreateFinalEvidenceAsync(
        TransactionFixture fixture,
        RetainedStatePersistedCandidate candidate,
        RetainedStateOwnership ownership,
        StickyCommentPublisher.StickyPublicationReceipt sticky,
        ImmutableArray<RetainedStateOpaqueRecord> existingP5Records = default,
        ExactHeadRevalidationResult? exactHead = null,
        StickyReadbackRecordV1? existingStickyReadback = null)
    {
        if (existingP5Records.IsDefault)
        {
            existingP5Records = [];
        }

        StickyReadbackRecordV1 stickyBody;
        ImmutableArray<RetainedStateOpaqueRecord> p5WithReadback;
        RetainedStateOpaqueRecord? persistedStickyRecord = null;
        RetainedStateOwnership? renewedOwnership = null;
        var acceptanceOwnership = ownership;
        if (existingStickyReadback is null)
        {
            Assert.True(PublicationRecoveryPersistence
                .TryCreateStickyReadbackWrite(
                    candidate,
                    sticky,
                    fixture.Time.UnixSeconds,
                    candidate.Prepared.Header.LogicalExpiresAtUnixSeconds,
                    out var createdStickyBody,
                    out var stickyRequest));
            stickyBody = createdStickyBody!;
            var stickyPersistedResult = await PersistOpaqueRecordAsync(
                    fixture.Context,
                    ownership,
                    stickyRequest!,
                    CancellationToken.None);
            persistedStickyRecord = Assert.IsType<RetainedStateOpaqueRecord>(
                stickyPersistedResult.Value);
            p5WithReadback = existingP5Records.Add(persistedStickyRecord);
            var acceptanceOwnershipResult = await RestrictedStateService
                .RenewRetainedStateOwnershipAsync(
                    fixture.Context,
                    candidate,
                    prior: null,
                    p5WithReadback,
                    CancellationToken.None);
            renewedOwnership = Assert.IsType<RetainedStateOwnership>(
                acceptanceOwnershipResult.Value);
            acceptanceOwnership = renewedOwnership;
        }
        else
        {
            stickyBody = existingStickyReadback;
            p5WithReadback = existingP5Records;
        }

        using var persistedStickyRecordLifetime = persistedStickyRecord;
        using var renewedOwnershipLifetime = renewedOwnership;
        var preparationResult = await RestrictedStateService
            .PrepareRetainedStateAcceptanceAsync(
                fixture.Context,
                acceptanceOwnership,
                sticky,
                CancellationToken.None);
        Assert.True(preparationResult.Succeeded, preparationResult.Code);
        using var preparation = Assert.IsType<
            RetainedStateAcceptancePreparation>(preparationResult.Value);
        Assert.True(preparation.TryCreateRecoveryHandoff(out var handoff));
        Assert.True(PublicationRecoveryPersistence.TryPublication(
            candidate,
            out var recoveryPublication));
        Assert.True(RecoveryRecordV1Codec.TryCreate(
            recoveryPublication!,
            stickyBody!,
            handoff!.OpaqueInnerPayload,
            handoff.MinimumSemanticExpiresAtUnixSeconds,
            out var recovery));
        Assert.True(RecoveryRecordV1Codec.TryEncode(
            recovery,
            out var encodedRecovery,
            out _,
            out _));
        var recoveryPayload = ImmutableArray.CreateRange(encodedRecovery);
        CryptographicOperations.ZeroMemory(encodedRecovery);
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
        using var extraction = CreateAcceptanceRecoveryExtraction(
            fixture.Context,
            recoveryRecord,
            handoff.OpaqueInnerPayload.Length);
        using var durability = await BindAcceptanceRecoveryAsync(
            fixture.Context,
            preparation,
            extraction);
        var predecessorCode = await RestrictedStateService
            .ReconcileRetainedStateAcceptancePredecessorAsync(
                fixture.Context,
                preparation,
                durability,
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
                    durability,
                    CancellationToken.None);
        }

        Assert.Equal(RetainedStateTransactionCodes.Ready, predecessorCode);
        var allP5 = p5WithReadback.Add(recoveryRecord);
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
                durability,
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

    private static FakePublisherTransportFactory ExactReadbackFactory(
        BoundedGitHubIssueComment comment)
    {
        var factory = new FakePublisherTransportFactory();
        factory.Transport.Enqueue(comment);
        factory.Transport.Enqueue(comment);
        factory.Transport.Read = BoundedGitHubHttpResult<
            BoundedGitHubIssueComment>.Success(comment);
        return factory;
    }

    private static async Task<RetainedStateOpaqueRecord>
        PersistP5AndCleanupAnchorAsync(
        TransactionFixture fixture,
        RetainedStateOwnership ownership,
        RetainedStateOpaqueWriteRequest request)
    {
        var prepared = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                fixture.Context,
                ownership,
                request,
                CancellationToken.None);
        using var attempt = Assert.IsType<RetainedStateOpaqueWriteAttempt>(
            prepared.Value);
        var persisted = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                fixture.Context,
                attempt,
                CancellationToken.None);
        var record = Assert.IsType<RetainedStateOpaqueRecord>(persisted.Value);
        var cleanup = await PublicationRecoveryPersistence
            .CleanupCompletedWriteAnchorAsync(
                fixture.Context,
                attempt,
                CancellationToken.None);
        if (!cleanup.Completed)
        {
            record.Dispose();
        }
        Assert.True(cleanup.Completed, cleanup.Code);
        return record;
    }

    private static async Task<RetainedStateAcceptanceRecoveryDurability>
        BindAcceptanceRecoveryAsync(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateAcceptancePreparation preparation,
        RetainedStateOpaquePayloadExtraction extraction)
    {
        var bound = await RestrictedStateService
            .BindRetainedStateAcceptanceRecoveryAsync(
                context,
                preparation,
                extraction,
                CancellationToken.None);
        Assert.True(bound.Succeeded, bound.Code);
        return Assert.IsType<RetainedStateAcceptanceRecoveryDurability>(
            bound.Value);
    }

    private static RetainedStateOpaquePayloadExtraction
        CreateAcceptanceRecoveryExtraction(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateOpaqueRecord record,
        int payloadLength)
    {
        var offset = P5RecoveryPayloadPrefix.Length;
        Assert.True(RestrictedStateService.TryCopyRetainedStateOpaquePayload(
            context,
            record,
            out var outerPayload));
        if (RecoveryRecordV1Codec.TryDecode(
                outerPayload.AsSpan(),
                out _,
                out var recoveryOffset,
                out var recoveryLength))
        {
            offset = recoveryOffset;
            payloadLength = recoveryLength;
        }

        var extracted = RestrictedStateService
            .CreateRetainedStateOpaquePayloadExtraction(
                context,
                record,
                offset,
                payloadLength);
        Assert.True(extracted.Succeeded, extracted.Code);
        return Assert.IsType<RetainedStateOpaquePayloadExtraction>(
            extracted.Value);
    }

    private static ImmutableArray<byte> WrapP5RecoveryPayload(
        ImmutableArray<byte> inner)
    {
        return [.. P5RecoveryPayloadPrefix, .. inner];
    }

    private static bool TryUnwrapP5RecoveryPayload(
        ImmutableArray<byte> outer,
        out ImmutableArray<byte> inner)
    {
        if (outer.IsDefaultOrEmpty ||
            outer.Length <= P5RecoveryPayloadPrefix.Length ||
            !outer.AsSpan(0, P5RecoveryPayloadPrefix.Length)
                .SequenceEqual(P5RecoveryPayloadPrefix))
        {
            inner = [];
            return false;
        }

        inner = outer[P5RecoveryPayloadPrefix.Length..];
        return true;
    }

    private static async Task<AgentRunOutcome>
        CompleteGroundedTerminalReviewAsync(
        AgentRunRequest run,
        AgentTerminalReview terminal)
    {
        var evidence = terminal.Findings
            .SelectMany(finding => finding.Evidence)
            .DistinctBy(item => (
                item.Path,
                item.StartLine,
                item.EndLine))
            .ToArray();
        var ids = new Dictionary<(string Path, int Start, int End), string>();
        var calls = new List<(ProjectToolCallContent Call,
            AgentToolExecution Execution)>();
        for (var index = 0; index < evidence.Length; index++)
        {
            var item = evidence[index];
            var count = item.EndLine - item.StartLine + 1;
            var argumentsJson = string.Concat(
                "{\"path\":\"",
                item.Path,
                "\",\"start_line\":",
                item.StartLine.ToString(CultureInfo.InvariantCulture),
                ",\"line_count\":",
                count.ToString(CultureInfo.InvariantCulture),
                "}");
            Assert.True(AgentToolArguments.TryReadFile(
                argumentsJson,
                out var arguments));
            var lines = Enumerable.Range(item.StartLine, count)
                .Select(line => new ReadFileLine(line, "line"))
                .ToImmutableArray();
            var withoutId = new ReadFileResult(
                "ok",
                run.ReviewedIdentity,
                item.Path,
                new string('a', 64),
                item.StartLine,
                count,
                item.StartLine,
                item.EndLine,
                lines,
                Truncated: false,
                TruncationReason: null,
                ObservationId: null);
            var observationId = AgentCanonical.HashDomain(
                AgentCanonical.ReadObservationDomain,
                ReadFileResultWriter.Write(
                    withoutId,
                    includeObservationId: false));
            var result = withoutId with
            {
                ObservationId = observationId,
            };
            var resultBytes = ReadFileResultWriter.Write(result);
            ids.Add(
                (item.Path, item.StartLine, item.EndLine),
                observationId);
            var callId = $"p1_read_{index}";
            calls.Add((
                new ProjectToolCallContent(
                    callId,
                    AgentToolRegistry.ReadFileName,
                    Encoding.UTF8.GetString(arguments!.CanonicalBytes)),
                new AgentToolExecution(
                    true,
                    FailureCode: null,
                    Encoding.UTF8.GetString(resultBytes),
                    resultBytes,
                    new AgentObservation(
                        observationId,
                        run.ReviewedIdentity,
                        ImmutableDictionary<
                                string,
                                ImmutableHashSet<int>>.Empty
                            .WithComparers(StringComparer.Ordinal)
                            .Add(
                                item.Path,
                                Enumerable.Range(item.StartLine, count)
                                    .ToImmutableHashSet())))));
        }

        var findings = terminal.Findings.Select(finding => finding with
        {
            Evidence = finding.Evidence.Select(item => item with
            {
                ObservationId = ids[
                            (item.Path, item.StartLine, item.EndLine)],
            })
                    .ToImmutableArray(),
        })
            .ToImmutableArray();
        var terminalBytes = AgentToolArguments.WriteFinishReview(
            terminal.Summary,
            findings);
        Assert.InRange(
            terminalBytes.Length,
            1,
            AgentLimits.TerminalBytes);
        Assert.True(AgentToolArguments.TryFinishReview(
            Encoding.UTF8.GetString(terminalBytes),
            out var parsedTerminal));
        Assert.True(TerminalReviewValidator.TryValidate(
            parsedTerminal!,
            run.ReviewedIdentity,
            calls.Select(item => item.Execution.Observation!)
                .ToArray(),
            out _));
        var desiredTerminal = new AgentTerminalReview(
            terminal.Summary,
            findings,
            AgentCanonical.HashDomain(
                AgentCanonical.TerminalDomain,
                terminalBytes),
            terminalBytes);
        var providerTerminalBytes = terminalBytes;
        if (!AgentToolArguments.TryFinishReviewProvider(
                Encoding.UTF8.GetString(terminalBytes),
                out _))
        {
            providerTerminalBytes = AgentToolArguments.WriteFinishReview(
                "grounded publication",
                findings.Select((finding, index) => finding with
                {
                    Title = $"Grounded {index}",
                    Message = "Grounded publication evidence.",
                }));
        }
        var responses = new Queue<ProjectChatResponse>();
        var responseOrdinal = 0;
        foreach (var group in calls.Chunk(AgentLimits.ToolCallsPerResponse))
        {
            responses.Enqueue(new ProjectChatResponse(
                new ProjectChatMessage(
                    "assistant",
                    [
                        new ProjectReasoningContent(
                            $"p1 grounding {responseOrdinal}",
                            string.Empty,
                            DeepSeekReasoningContinuationCodec.FramingName,
                            AssociatedCallId: null,
                            MessagePosition: 0,
                            Position: 0),
                        .. group.Select(item =>
                            (ProjectChatContent)item.Call),
                    ]),
                new ProjectChatUsage(1, 1),
                CapturedResponseBodyBytes: 1));
            responseOrdinal++;
        }

        responses.Enqueue(new ProjectChatResponse(
            new ProjectChatMessage(
                "assistant",
                [
                    new ProjectReasoningContent(
                        "p1 terminal",
                        string.Empty,
                        DeepSeekReasoningContinuationCodec.FramingName,
                        AssociatedCallId: null,
                        MessagePosition: 0,
                        Position: 0),
                    new ProjectToolCallContent(
                        "p1_finish",
                        AgentToolRegistry.FinishReviewName,
                        Encoding.UTF8.GetString(providerTerminalBytes)),
                ]),
            new ProjectChatUsage(1, 1),
            CapturedResponseBodyBytes: 1));
        var executions = calls.ToDictionary(
            item => item.Call.CallId,
            item => item.Execution,
            StringComparer.Ordinal);
        var outcome = await new AgentLoop(
            new QueueResponseChatClient(responses, run),
            new ScriptedToolExecutor(executions)).RunAsync(
                run,
                CancellationToken.None);
        Assert.True(
            outcome.CompletedSessionEligible,
            outcome.Diagnostic?.Code);
        return providerTerminalBytes.AsSpan().SequenceEqual(terminalBytes)
            ? outcome
            : RetargetTerminalReview(outcome, desiredTerminal);
    }

    private static AgentRunOutcome RetargetTerminalReview(
        AgentRunOutcome outcome,
        AgentTerminalReview terminal)
    {
        var canonical = ImmutableArray.CreateRange(terminal.CanonicalBytes);
        return outcome with
        {
            Review = terminal,
            Events = outcome.Events
                .Select<AgentLogicalEvent, AgentLogicalEvent>(logical =>
                    logical switch
                    {
                        AgentMessageEvent message => message with
                        {
                            Contents = message.Contents.Select(part =>
                                    part is AgentToolCallReferencePart call &&
                                    StringComparer.Ordinal.Equals(
                                        call.Name,
                                        AgentToolRegistry.FinishReviewName)
                                        ? call with
                                        {
                                            ArgumentsSha256 =
                                                terminal.TerminalSha256,
                                        }
                                        : part)
                                .ToImmutableArray(),
                        },
                        AgentToolCallEvent call when
                            StringComparer.Ordinal.Equals(
                                call.Name,
                                AgentToolRegistry.FinishReviewName) => call with
                                {
                                    ArgumentsSha256 = terminal.TerminalSha256,
                                    CanonicalArguments = canonical,
                                },
                        AgentTerminalEvent => new AgentTerminalEvent(
                            terminal.TerminalSha256),
                        _ => logical,
                    })
                .ToImmutableArray(),
        };
    }

    private static AgentRunOutcome WithFixedTextPadding(
        AgentRunOutcome template,
        int totalCharacters,
        int partCount)
    {
        var messageCount = template.Events
            .OfType<AgentMessageEvent>()
            .Count(message => message.Contents.Any(part =>
                part is AgentToolCallReferencePart));
        if (partCount <= 0 ||
            messageCount <= 0 ||
            partCount % messageCount != 0 ||
            totalCharacters < partCount ||
            totalCharacters > partCount * AgentLimits.ContentBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(totalCharacters));
        }

        var partsPerMessage = partCount / messageCount;
        var paddingByMessage = ImmutableArray.CreateBuilder<
            ImmutableArray<AgentMessagePart>>(messageCount);
        var remainingTotal = totalCharacters;
        for (var messageIndex = 0;
            messageIndex < messageCount;
            messageIndex++)
        {
            var remainingMessages = messageCount - messageIndex;
            var messageCharacters = remainingTotal / remainingMessages;
            remainingTotal -= messageCharacters;
            var padding = ImmutableArray.CreateBuilder<AgentMessagePart>(
                partsPerMessage);
            var remaining = messageCharacters;
            for (var partIndex = 0;
                partIndex < partsPerMessage;
                partIndex++)
            {
                var remainingParts = partsPerMessage - partIndex - 1;
                var length = Math.Min(
                    AgentLimits.ContentBytes,
                    remaining - remainingParts);
                padding.Add(new AgentTextPart(new string('x', length)));
                remaining -= length;
            }

            paddingByMessage.Add(padding.MoveToImmutable());
        }

        var nextMessage = 0;
        var events = template.Events
            .Select<AgentLogicalEvent, AgentLogicalEvent>(logical =>
                logical switch
                {
                    AgentMessageEvent message when message.Contents.Any(
                        part => part is AgentToolCallReferencePart) =>
                        message with
                        {
                            Contents = message.Contents[..1]
                            .AddRange(paddingByMessage[nextMessage++])
                            .AddRange(message.Contents[1..]),
                        },
                    _ => logical,
                })
            .ToImmutableArray();
        return template with
        {
            Events = events,
        };
    }

    private static AgentSessionBuildResult BuildSession(
        TransactionFixture fixture,
        AgentRunRequest run,
        AgentRunOutcome outcome) =>
        AgentSessionBuilder.Build(new AgentSessionBuildInput(
            run,
            outcome,
            TrustedRequest(fixture),
            run.InitialMessages.Length - 1,
            DeepSeekReasoningContinuationCodec.Instance,
            Predecessor: null,
            AgentSessionHeadTransition.SameHead));

    private static AgentSessionTrustedRequest TrustedRequest(
        TransactionFixture fixture) =>
        new(
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

    internal static async Task<CompletedRun> CompleteRunAsync(
        TransactionFixture fixture,
        string summary = "complete",
        string callId = "finish0")
    {
        var trusted = TrustedRequest(fixture);
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
            launch.Inputs.PullRequestNumber,
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

    internal sealed record TransactionFixture(
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

    internal sealed record CompletedRun(
        AgentRunRequest Run,
        AgentRunOutcome Outcome);

    internal sealed record AcceptedGeneration(
        long Generation,
        OpaqueStoreName CandidateName,
        OpaqueStoreObjectMetadata CandidateMetadata,
        VerifiedRetainedStateAcceptance Acceptance,
        ValidatedPublicationPayloadV1 Publication);

    private sealed class TestDependencies(
        IRestrictedStateStore store,
        string? currentHead = null,
        string? previousHead = null) :
        IAcceptedStateProductionDependencies
    {
        public IRestrictedStateStore CreateArtifactStore(
            ActionHostLaunchContract launch) => store;

        public IActionHostGitObjectTransport CreateAncestryTransport(
            ActionHostGitHubToken token) =>
            currentHead is not null && previousHead is not null
                ? new LinearAheadTransport(currentHead, previousHead)
                : new NoCallTransport();
    }

    private sealed class LinearAheadTransport(
        string currentHead,
        string previousHead) : IActionHostGitObjectTransport
    {
        private static readonly string Tree = new('e', 40);

        public Task<ActionHostGitObjectResult<ActionHostGitCommitObject>>
            GetCommitObjectAsync(
                string repositoryName,
                string commitSha,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = StringComparer.Ordinal.Equals(commitSha, currentHead)
                ? ActionHostGitObjectResult<ActionHostGitCommitObject>.Success(
                    new ActionHostGitCommitObject(
                        currentHead,
                        Tree,
                        [previousHead]),
                    512)
                : ActionHostGitObjectResult<ActionHostGitCommitObject>.Failed(
                    ActionHostGitObjectFailure.NotFound);
            return Task.FromResult(result);
        }

        public Task<ActionHostGitObjectResult<ActionHostGitTreeObject>>
            GetTreeObjectAsync(
                string repositoryName,
                string treeSha,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Tree transport was called.");

        public Task<ActionHostGitObjectResult<ActionHostGitBlobObject>>
            GetBlobObjectAsync(
                string repositoryName,
                string blobSha,
                ActionHostGitBlobReadBudget budget,
                CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Blob transport was called.");

        public void Dispose() { }
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

    private sealed class QueueResponseChatClient(
        Queue<ProjectChatResponse> responses,
        AgentRunRequest run) : IProjectChatClient
    {
        public Task<ProjectChatResponse> GetResponseAsync(
            ProjectChatRequest request,
            CancellationToken cancellationToken)
        {
            var response = responses.Dequeue();
            var message = response.Message with
            {
                Contents = response.Message.Contents
                    .Select((content, index) =>
                        content is ProjectReasoningContent reasoning
                            ? reasoning with
                            {
                                MessagePosition = request.Messages.Length,
                                Position = index,
                            }
                            : content)
                    .ToArray(),
            };
            var continuation = new ProjectContinuation(
                run.StablePlan.ProviderId,
                run.StablePlan.ModelId,
                run.StablePlan.AdapterId,
                run.SessionId,
                message.Contents
                    .OfType<ProjectReasoningContent>()
                    .Select(reasoning => new ProjectContinuationItem(
                        reasoning.Text,
                        reasoning.Opaque,
                        reasoning.Framing,
                        reasoning.AssociatedCallId,
                        reasoning.MessagePosition,
                        reasoning.Position))
                    .ToArray());
            return Task.FromResult(response with
            {
                Message = message,
                Continuation = continuation,
            });
        }
    }

    private sealed class ScriptedToolExecutor(
        IReadOnlyDictionary<string, AgentToolExecution> executions) :
        IAgentToolExecutor
    {
        public string? Preflight(PreparedAgentToolCall call) =>
            executions.ContainsKey(call.CallId)
                ? null
                : "unexpected_tool_call";

        public ValueTask<AgentToolExecution> ExecuteAsync(
            PreparedAgentToolCall call,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(executions[call.CallId]);
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
