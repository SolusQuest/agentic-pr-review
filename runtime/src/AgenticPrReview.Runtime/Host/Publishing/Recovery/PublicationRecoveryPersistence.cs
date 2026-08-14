using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryPersistence
{
    internal static async Task<RetainedStateTransactionResult<
        PublicationIntentPersistenceResult>> PersistIntentAndAuthorizeAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryObservation observation,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            observation is null ||
            !observation.IsLive ||
            observation.Candidate is not { } observedCandidate ||
            observation.Intent is not null ||
            observation.StickyReadback is not null ||
            observation.Failure is not null ||
            observation.Abandonment is not null ||
            observation.Recovery is not null ||
            observation.Anchors != PublicationRecoveryAnchorState.None ||
            !observation.HistoricalRecords.IsEmpty ||
            !observation.CompletedAnchors.IsEmpty ||
            !observation.CleanupRecords.IsEmpty ||
            !observedCandidate.MatchesCurrentReviewedHead ||
            !PublicationRecoveryRetention.TryCompute(
                observation.ObservedAtUnixSeconds,
                observedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out var requestedPlatformExpiry))
        {
            return IntentFailure();
        }

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded ||
            candidate is null ||
            candidate.Metadata != observedCandidate.Metadata ||
            candidate.Prepared.Header != observedCandidate.Header ||
            !StringComparer.Ordinal.Equals(
                candidate.Prepared.LogicalGenerationIdentity,
                observedCandidate.LogicalGenerationIdentity))
        {
            candidate?.Prepared.Dispose();
            return IntentFailure();
        }

        using var preparedLifetime = candidate.Prepared;
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                observation.Records,
                cancellationToken)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        if (!ownershipResult.Succeeded ||
            ownership is null ||
            !StringComparer.Ordinal.Equals(
                ownership.InventoryDigest,
                observation.InventoryDigest) ||
            !TryCreateIntentWrite(
                candidate,
                observation.ObservedAtUnixSeconds,
                observedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out var intent,
                out var request) ||
            intent is null ||
            request is null ||
            request.SemanticRequiredExpiresAtUnixSeconds != semanticExpiry)
        {
            return IntentFailure();
        }

        var attemptResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                context,
                ownership,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        using var attempt = attemptResult.Value;
        if (!attemptResult.Succeeded ||
            attempt is null ||
            attempt.Header.LogicalExpiresAtUnixSeconds != semanticExpiry ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.PredecessorIdentity,
                observedCandidate.Header.ObjectIdentity))
        {
            return IntentFailure();
        }

        var persistedResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                context,
                attempt,
                cancellationToken)
            .ConfigureAwait(false);
        using var persisted = persistedResult.Value;
        if (!persistedResult.Succeeded ||
            persisted is null ||
            persisted.ObjectClass != StateObjectClass.PublicationIntent ||
            persisted.Header != attempt.Header ||
            persisted.Metadata.ExpiresAtUnixSeconds <
                requestedPlatformExpiry ||
            !StringComparer.Ordinal.Equals(
                persisted.Header.PredecessorIdentity,
                observedCandidate.Header.ObjectIdentity))
        {
            return IntentFailure();
        }

        var anchorCleanup = await CleanupCompletedWriteAnchorAsync(
                context,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!anchorCleanup.Completed)
        {
            return IntentFailure();
        }

        var freshInventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
        var freshInventory = freshInventoryResult.Value;
        if (!freshInventoryResult.Succeeded || freshInventory is null)
        {
            freshInventory?.Dispose();
            return IntentFailure();
        }

        var freshObservationResult = await PublicationRecoveryInventoryFactory
            .CreateAsync(
                context,
                freshInventory,
                observedCandidate.Publication.ReviewedHeadSha,
                CancellationToken.None)
            .ConfigureAwait(false);
        var freshObservation = freshObservationResult.Value;
        if (!freshObservationResult.Succeeded ||
            freshObservation is null ||
            freshObservation.Intent != intent ||
            freshObservation.StickyReadback is not null ||
            freshObservation.Failure is not null ||
            freshObservation.Abandonment is not null ||
            freshObservation.Recovery is not null ||
            freshObservation.Anchors != PublicationRecoveryAnchorState.None ||
            !freshObservation.HistoricalRecords.IsEmpty ||
            !freshObservation.CompletedAnchors.IsEmpty ||
            !freshObservation.CleanupRecords.IsEmpty ||
            freshObservation.Records.Length != 1 ||
            freshObservation.CandidateObjectIdentity !=
                observedCandidate.Header.ObjectIdentity ||
            freshObservation.Records.Count(record =>
                record.Metadata == persisted.Metadata &&
                record.Header == persisted.Header) != 1)
        {
            freshObservation?.Dispose();
            return IntentFailure();
        }

        var stickyAuthorization = PublicationRecoveryInventoryFactory
            .CreateStickyWriteAuthorization(
                freshObservation,
                intent.RecordIdentity,
                PublicationStickyWriteTransition.InitialIntent);
        return RetainedStateTransactionResult<
            PublicationIntentPersistenceResult>.Success(
                RetainedStateTransactionCodes.Ready,
                new(
                    intent,
                    freshObservation.InventoryDigest,
                    freshObservation,
                    stickyAuthorization));
    }

    internal static async Task<RetainedStateCleanupResult>
        CleanupCompletedWriteAnchorAsync(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateOpaqueWriteAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            attempt is null ||
            !PublicationRecoveryRetention.TryCompute(
                attempt.Header.CreatedAtUnixSeconds,
                attempt.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _))
        {
            return new(
                null,
                Completed: false,
                RetainedStateTransactionCodes.Invalid);
        }

        var authorizationResult = await RestrictedStateService
            .AuthorizeRetainedP5CleanupAsync(
                context,
                new RetainedStateP5CleanupDecision(
                    RetainedStateP5CleanupClassification
                        .CompletedOpaqueWriteAnchor,
                    attempt.OperationIdentity,
                    MarkerEvidenceIdentity: null),
                pendingCandidate: null,
                opaqueRecord: null,
                attempt,
                recoveryInventory: null,
                recoveryAnchor: null,
                cancellationToken)
            .ConfigureAwait(false);
        using var authorization = authorizationResult.Value;
        if (!authorizationResult.Succeeded || authorization is null)
        {
            return new(
                null,
                Completed: false,
                authorizationResult.Code);
        }

        return await RestrictedStateService
            .CleanupRetainedP5AuthorizedAsync(
                context,
                new RetainedStateP5CleanupRequest(
                    authorization,
                    semanticExpiry),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static async Task<RetainedStateTransactionResult<
        PublicationRetryIntentPersistenceResult>>
        PersistRetryIntentAndAuthorizeAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryObservation observation,
        PublicationRetryTransitionAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            observation is null ||
            authorization is null ||
            !observation.IsLive ||
            observation.Candidate is not { } observedCandidate ||
            observation.Intent is not { } initialIntent ||
            observation.Failure is not
            {
                Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
            } initialFailure ||
            observation.RetryIntent is not null ||
            observation.RetryFailure is not null ||
            observation.StickyReadback is not null ||
            observation.Abandonment is not null ||
            observation.Recovery is not null ||
            observation.Anchors != PublicationRecoveryAnchorState.None ||
            !observation.HistoricalRecords.IsEmpty ||
            !observation.CompletedAnchors.IsEmpty ||
            !observation.CleanupRecords.IsEmpty ||
            !observedCandidate.MatchesCurrentReviewedHead ||
            !StringComparer.Ordinal.Equals(
                initialFailure.AttemptIntentRecordIdentity,
                initialIntent.RecordIdentity) ||
            !PublicationRecoveryRetention.TryCompute(
                observation.ObservedAtUnixSeconds,
                observedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out var requestedPlatformExpiry))
        {
            return RetryIntentFailure();
        }

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var candidate = recoveredResult.Value;
        if (!recoveredResult.Succeeded ||
            candidate is null ||
            candidate.Metadata != observedCandidate.Metadata ||
            candidate.Prepared.Header != observedCandidate.Header ||
            !StringComparer.Ordinal.Equals(
                candidate.Prepared.LogicalGenerationIdentity,
                observedCandidate.LogicalGenerationIdentity))
        {
            candidate?.Prepared.Dispose();
            return RetryIntentFailure();
        }

        using var preparedLifetime = candidate.Prepared;
        var ownershipResult = await RestrictedStateService
            .RenewRetainedStateOwnershipAsync(
                context,
                candidate,
                prior: null,
                observation.Records,
                cancellationToken)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        if (!ownershipResult.Succeeded ||
            ownership is null ||
            !StringComparer.Ordinal.Equals(
                ownership.InventoryDigest,
                observation.InventoryDigest) ||
            !PublicationRecoveryInventoryFactory
                .TryConsumeRetryTransitionAuthorization(
                    observation,
                    authorization) ||
            !TryCreateRetryIntentWrite(
                candidate,
                initialIntent,
                initialFailure,
                observation.ObservedAtUnixSeconds,
                observedCandidate.Header.LogicalExpiresAtUnixSeconds,
                out var retryIntent,
                out var request) ||
            retryIntent is null ||
            request is null ||
            request.SemanticRequiredExpiresAtUnixSeconds != semanticExpiry)
        {
            return RetryIntentFailure();
        }

        var attemptResult = await RestrictedStateService
            .PrepareRetainedOpaqueWriteAsync(
                context,
                ownership,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        using var attempt = attemptResult.Value;
        if (!attemptResult.Succeeded ||
            attempt is null ||
            attempt.Header.LogicalExpiresAtUnixSeconds != semanticExpiry ||
            !StringComparer.Ordinal.Equals(
                attempt.Header.PredecessorIdentity,
                observedCandidate.Header.ObjectIdentity))
        {
            return RetryIntentFailure();
        }

        var persistedResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                context,
                attempt,
                cancellationToken)
            .ConfigureAwait(false);
        using var persisted = persistedResult.Value;
        if (!persistedResult.Succeeded ||
            persisted is null ||
            persisted.ObjectClass != StateObjectClass.PublicationIntent ||
            persisted.Header != attempt.Header ||
            persisted.Metadata.ExpiresAtUnixSeconds <
                requestedPlatformExpiry ||
            !StringComparer.Ordinal.Equals(
                persisted.Header.PredecessorIdentity,
                observedCandidate.Header.ObjectIdentity))
        {
            return RetryIntentFailure();
        }

        var anchorCleanup = await CleanupCompletedWriteAnchorAsync(
                context,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!anchorCleanup.Completed)
        {
            return RetryIntentFailure();
        }

        var freshInventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
        var freshInventory = freshInventoryResult.Value;
        if (!freshInventoryResult.Succeeded || freshInventory is null)
        {
            freshInventory?.Dispose();
            return RetryIntentFailure();
        }

        var freshObservationResult = await PublicationRecoveryInventoryFactory
            .CreateAsync(
                context,
                freshInventory,
                observedCandidate.Publication.ReviewedHeadSha,
                CancellationToken.None)
            .ConfigureAwait(false);
        var freshObservation = freshObservationResult.Value;
        if (!freshObservationResult.Succeeded ||
            freshObservation is null ||
            freshObservation.Intent != initialIntent ||
            freshObservation.Failure != initialFailure ||
            freshObservation.RetryIntent != retryIntent ||
            freshObservation.RetryFailure is not null ||
            freshObservation.StickyReadback is not null ||
            freshObservation.Abandonment is not null ||
            freshObservation.Recovery is not null ||
            freshObservation.Anchors != PublicationRecoveryAnchorState.None ||
            !freshObservation.HistoricalRecords.IsEmpty ||
            !freshObservation.CompletedAnchors.IsEmpty ||
            !freshObservation.CleanupRecords.IsEmpty ||
            freshObservation.Records.Length != 3 ||
            freshObservation.CandidateObjectIdentity !=
                observedCandidate.Header.ObjectIdentity ||
            freshObservation.Records.Count(record =>
                record.Metadata == persisted.Metadata &&
                record.Header == persisted.Header) != 1)
        {
            freshObservation?.Dispose();
            return RetryIntentFailure();
        }

        var stickyAuthorization = PublicationRecoveryInventoryFactory
            .CreateStickyWriteAuthorization(
                freshObservation,
                retryIntent.RecordIdentity,
                PublicationStickyWriteTransition.RetryIntent);
        return RetainedStateTransactionResult<
            PublicationRetryIntentPersistenceResult>.Success(
                RetainedStateTransactionCodes.Ready,
                new(
                    retryIntent,
                    freshObservation.InventoryDigest,
                    freshObservation,
                    stickyAuthorization));
    }

    internal static bool TryCreateIntentWrite(
        RetainedStatePersistedCandidate candidate,
        long trustedNowUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationIntentV1? intent,
        out RetainedStateOpaqueWriteRequest? request)
    {
        intent = null;
        request = null;
        if (!TryPublication(candidate, out var publication) ||
            !PublicationRecoveryRetention.TryCompute(
                trustedNowUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationIntentV1Codec.TryCreate(
                publication!,
                trustedNowUnixSeconds,
                out intent) ||
            !PublicationIntentV1Codec.TryEncode(intent, out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            candidate.Prepared.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateStickyReadbackWrite(
        RetainedStatePersistedCandidate candidate,
        string attemptIntentRecordIdentity,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out StickyReadbackRecordV1? readback,
        out RetainedStateOpaqueWriteRequest? request)
    {
        readback = null;
        request = null;
        if (!TryPublication(candidate, out var publication) ||
            !PublicationRecoveryRetention.TryCompute(
                observedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !StickyReadbackRecordV1Codec.TryCreate(
                publication!,
                attemptIntentRecordIdentity,
                receipt,
                observedAtUnixSeconds,
                out readback) ||
            !StickyReadbackRecordV1Codec.TryEncode(
                readback,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            candidate.Prepared.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateFailureWrite(
        RetainedStatePersistedCandidate candidate,
        string attemptIntentRecordIdentity,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationFailureV1? failure,
        out RetainedStateOpaqueWriteRequest? request)
    {
        failure = null;
        request = null;
        if (!TryPublication(candidate, out var publication) ||
            !PublicationRecoveryRetention.TryCompute(
                failedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationFailureV1Codec.TryCreate(
                publication!,
                attemptIntentRecordIdentity,
                outcome,
                reason,
                failedAtUnixSeconds,
                out failure) ||
            !PublicationFailureV1Codec.TryEncode(failure, out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationFailure,
            bytes,
            candidate.Prepared.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateRetryIntentWrite(
        RetainedStatePersistedCandidate candidate,
        PublicationIntentV1 initialIntent,
        PublicationFailureV1 initialFailure,
        long trustedNowUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationRetryIntentV1? retryIntent,
        out RetainedStateOpaqueWriteRequest? request)
    {
        retryIntent = null;
        request = null;
        if (!TryPublication(candidate, out var publication) ||
            initialIntent is null ||
            initialFailure is not
            {
                Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
            } ||
            initialIntent.Publication != publication ||
            initialFailure.Publication != publication ||
            !StringComparer.Ordinal.Equals(
                initialFailure.AttemptIntentRecordIdentity,
                initialIntent.RecordIdentity) ||
            !PublicationRecoveryRetention.TryCompute(
                trustedNowUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationRetryIntentV1Codec.TryCreate(
                publication!,
                initialIntent.RecordIdentity,
                initialFailure.RecordIdentity,
                trustedNowUnixSeconds,
                out retryIntent) ||
            !PublicationRetryIntentV1Codec.TryEncode(
                retryIntent,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            candidate.Prepared.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateRetryFailureWrite(
        RetainedStatePersistedCandidate candidate,
        PublicationRetryIntentV1 retryIntent,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAtUnixSeconds,
        long candidateLogicalExpiresAtUnixSeconds,
        out PublicationRetryFailureV1? retryFailure,
        out RetainedStateOpaqueWriteRequest? request)
    {
        retryFailure = null;
        request = null;
        if (!TryPublication(candidate, out var publication) ||
            retryIntent is null ||
            retryIntent.Publication != publication ||
            !PublicationRecoveryRetention.TryCompute(
                failedAtUnixSeconds,
                candidateLogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !PublicationRetryFailureV1Codec.TryCreate(
                publication!,
                retryIntent.RecordIdentity,
                outcome,
                reason,
                failedAtUnixSeconds,
                out retryFailure) ||
            !PublicationRetryFailureV1Codec.TryEncode(
                retryFailure,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationFailure,
            bytes,
            candidate.Prepared.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateStaleAbandonmentWrite(
        PublicationRecoveryObservation observation,
        PublicationStaleAbandonmentAuthorization authorization,
        long abandonedAtUnixSeconds,
        out AbandonmentV1? abandonment,
        out RetainedStateOpaqueWriteRequest? request)
    {
        abandonment = null;
        request = null;
        var candidate = observation?.Candidate;
        if (candidate is null ||
            candidate.MatchesCurrentReviewedHead ||
            authorization is null ||
            !PublicationRecoveryInventoryFactory
                .TryConsumeStaleAbandonmentWriteAuthorization(
                    observation!,
                    authorization) ||
            !TryPublication(candidate, out var publication) ||
            !PublicationRecoveryRetention.TryCompute(
                abandonedAtUnixSeconds,
                candidate.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _) ||
            !AbandonmentV1Codec.TryCreate(
                publication!,
                authorization.EvidenceIdentity,
                abandonedAtUnixSeconds,
                out abandonment) ||
            !AbandonmentV1Codec.TryEncode(
                abandonment,
                out var bytes))
        {
            return false;
        }

        request = Request(
            StateObjectClass.Abandonment,
            bytes,
            candidate.Header.ObjectIdentity,
            semanticExpiry);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static bool TryCreateAcceptanceRecoveryWrite(
        RetainedStateAcceptancePreparation preparation,
        StickyReadbackRecordV1 readback,
        out RecoveryRecordV1? recovery,
        out RetainedStateOpaqueWriteRequest? request)
    {
        recovery = null;
        request = null;
        if (preparation is null ||
            readback is null ||
            !preparation.TryCreateRecoveryHandoff(out var handoff) ||
            handoff is null ||
            !TryPublication(preparation.Candidate, out var publication) ||
            publication != readback.Publication ||
            !RecoveryRecordV1Codec.TryCreate(
                publication!,
                readback,
                handoff.OpaqueInnerPayload,
                handoff.MinimumSemanticExpiresAtUnixSeconds,
                out recovery) ||
            !RecoveryRecordV1Codec.TryEncode(
                recovery,
                out var bytes,
                out _,
                out _))
        {
            return false;
        }

        request = Request(
            StateObjectClass.PublicationIntent,
            bytes,
            handoff.CandidateObjectIdentity,
            handoff.MinimumSemanticExpiresAtUnixSeconds);
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    internal static RetainedStateTransactionResult<
        RetainedStateOpaquePayloadExtraction>
        CreateAcceptanceRecoveryExtraction(
            AuthorizedAcceptedStateRestoreContext context,
            RetainedStateOpaqueRecord record)
    {
        if (context is null ||
            record is null ||
            !RestrictedStateService.TryCopyRetainedStateOpaquePayload(
                context,
                record,
                out var payload))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaquePayloadExtraction>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        try
        {
            if (!RecoveryRecordV1Codec.TryDecode(
                    payload.AsSpan(),
                    out _,
                    out var offset,
                    out var length))
            {
                return RetainedStateTransactionResult<
                    RetainedStateOpaquePayloadExtraction>.Fail(
                        RetainedStateTransactionCodes.Conflict);
            }

            return RestrictedStateService
                .CreateRetainedStateOpaquePayloadExtraction(
                    context,
                    record,
                    offset,
                    length);
        }
        finally
        {
            var array = System.Runtime.InteropServices
                .ImmutableCollectionsMarshal.AsArray(payload);
            if (array is not null)
            {
                CryptographicOperations.ZeroMemory(array);
            }
        }
    }

    internal static bool TryPublication(
        RetainedStatePersistedCandidate candidate,
        out PublicationRecoveryPublicationV1? publication)
    {
        publication = null;
        var prepared = candidate?.Prepared;
        var header = prepared?.Header;
        var stored = prepared?.Publication;
        if (prepared is null || header is null || stored is null ||
            header.ObjectClass != StateObjectClass.Candidate ||
            !StringComparer.Ordinal.Equals(
                header.ObjectIdentity,
                prepared.Header.ObjectIdentity) ||
            !StringComparer.Ordinal.Equals(
                stored.ReviewedHeadSha,
                prepared.Generation.ProducerHeadSha))
        {
            return false;
        }

        publication = new PublicationRecoveryPublicationV1(
            stored.ReviewedHeadSha,
            stored.ScopeSha256,
            stored.BodySha256);
        return true;
    }

    internal static bool TryPublication(
        RetainedStateObservedCandidate candidate,
        out PublicationRecoveryPublicationV1? publication)
    {
        publication = null;
        if (candidate is null ||
            candidate.Header.ObjectClass != StateObjectClass.Candidate ||
            !StringComparer.Ordinal.Equals(
                candidate.Publication.ReviewedHeadSha,
                candidate.Generation.ProducerHeadSha))
        {
            return false;
        }

        publication = new PublicationRecoveryPublicationV1(
            candidate.Publication.ReviewedHeadSha,
            candidate.Publication.ScopeSha256,
            candidate.Publication.BodySha256);
        return true;
    }

    private static RetainedStateOpaqueWriteRequest Request(
        StateObjectClass objectClass,
        byte[] bytes,
        string candidateIdentity,
        long semanticExpiry) => new(
            objectClass,
            ImmutableArray.CreateRange(bytes),
            candidateIdentity,
            SuccessorIdentity: null,
            semanticExpiry);

    private static RetainedStateTransactionResult<
        PublicationIntentPersistenceResult> IntentFailure() =>
        RetainedStateTransactionResult<
            PublicationIntentPersistenceResult>.Fail(
                RetainedStateTransactionCodes.Conflict);

    private static RetainedStateTransactionResult<
        PublicationRetryIntentPersistenceResult> RetryIntentFailure() =>
        RetainedStateTransactionResult<
            PublicationRetryIntentPersistenceResult>.Fail(
                RetainedStateTransactionCodes.Conflict);
}
