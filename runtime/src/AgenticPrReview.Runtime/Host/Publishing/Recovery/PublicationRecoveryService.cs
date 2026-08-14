using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal sealed class PublicationRecoveryService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly StickyCommentPublisher publisher;

    internal PublicationRecoveryService(StickyCommentPublisher publisher) =>
        this.publisher = publisher ??
            throw new ArgumentNullException(nameof(publisher));

    internal async Task<PublicationRecoveryEvaluation> ClassifyBeforeProviderAsync(
        ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        AuthorizedAcceptedStateRestoreContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);

        var inventoryResult = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                context,
                cancellationToken)
            .ConfigureAwait(false);
        var inventory = inventoryResult.Value;
        if (!inventoryResult.Succeeded || inventory is null)
        {
            return Evaluation(
                PublicationRecoveryClassifier.Classify(
                    null,
                    PublicationMarkerObservation.Incomplete),
                null,
                StickyDiscoveryKind.InvalidOrIncomplete,
                StickyPublicationReason.AdmissionInvalid,
                null);
        }

        var observationResult = await PublicationRecoveryInventoryFactory
            .CreateAsync(
                context,
                inventory,
                authorization.PullRequest.HeadSha,
                cancellationToken)
            .ConfigureAwait(false);
        var observation = observationResult.Value;
        if (!observationResult.Succeeded || observation is null)
        {
            return Evaluation(
                PublicationRecoveryClassifier.Classify(
                    null,
                    PublicationMarkerObservation.Incomplete),
                null,
                StickyDiscoveryKind.InvalidOrIncomplete,
                StickyPublicationReason.AdmissionInvalid,
                null);
        }

        var durableOnly = PublicationRecoveryClassifier.Classify(
            observation,
            PublicationMarkerObservation.Absent);
        if (durableOnly.Action is
            PublicationRecoveryAction.ResumeAnchoredWrite or
            PublicationRecoveryAction.ResumeCleanup)
        {
            return Evaluation(
                durableOnly,
                null,
                StickyDiscoveryKind.Absent,
                StickyPublicationReason.None,
                observation);
        }

        if (observation.Candidate is null &&
            !observation.CurrentAcceptedHeadMatchesReviewedHead)
        {
            return Evaluation(
                PublicationRecoveryClassifier.Classify(
                    observation,
                    PublicationMarkerObservation.Absent),
                null,
                StickyDiscoveryKind.Absent,
                StickyPublicationReason.None,
                observation);
        }

        AuthorizedStickyReadbackRequest? request;
        if (observation.Candidate is null)
        {
            if (!AuthorizedStickyReadbackRequest.TryCreate(
                    authorization,
                    scope,
                    observation.Inventory?
                        .CurrentAcceptancePublicationReceipt,
                    out request) ||
                request is null)
            {
                observation.Dispose();
                return Evaluation(
                    PublicationRecoveryClassifier.Classify(
                        null,
                        PublicationMarkerObservation.Incomplete),
                    null,
                    StickyDiscoveryKind.InvalidOrIncomplete,
                    StickyPublicationReason.AdmissionInvalid,
                    null);
            }
        }
        else if (!TryRestoreRendered(
                    observation.StoredPublication,
                    out var rendered) ||
            rendered is null ||
            (observation.StickyReadback is { } storedReadback
                ? !storedReadback.TryRehydrate(out var storedReceipt) ||
                    storedReceipt is null ||
                    !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                        authorization,
                        scope,
                        rendered,
                        storedReceipt,
                        out request)
                : !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                    authorization,
                    scope,
                    rendered,
                    out request)) ||
            request is null)
        {
            observation.Dispose();
            return Evaluation(
                PublicationRecoveryClassifier.Classify(
                    null,
                    PublicationMarkerObservation.Incomplete),
                null,
                StickyDiscoveryKind.InvalidOrIncomplete,
                StickyPublicationReason.AdmissionInvalid,
                null);
        }

        var discovered = await publisher.DiscoverAsync(
                token,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var marker = discovered.Kind switch
        {
            StickyDiscoveryKind.Absent => PublicationMarkerObservation.Absent,
            StickyDiscoveryKind.ExactTarget
                when discovered.Receipt is not null =>
                PublicationMarkerObservation.Exact,
            StickyDiscoveryKind.StaleTarget =>
                PublicationMarkerObservation.Ambiguous,
            _ => PublicationMarkerObservation.Incomplete,
        };
        StickyCommentPublisher.StickyPublicationReceipt?
            exactReadbackReceipt = null;
        if (marker == PublicationMarkerObservation.Exact &&
            (discovered.Receipt is null ||
                !TryResolveExactReadbackReceipt(
                    observation,
                    discovered.Receipt,
                    out exactReadbackReceipt)))
        {
            marker = PublicationMarkerObservation.Ambiguous;
        }
        else if (marker == PublicationMarkerObservation.Ambiguous &&
            discovered.Kind == StickyDiscoveryKind.StaleTarget &&
            await IsPreviousAcceptedTargetAsync(
                token,
                authorization,
                scope,
                observation,
                cancellationToken).ConfigureAwait(false))
        {
            marker = PublicationMarkerObservation.PreviousAcceptedTarget;
        }
        var classified = PublicationRecoveryClassifier.Classify(
            observation,
            marker);
        PublicationStickyWriteAuthorization? stickyAuthorization = null;
        PublicationMarkerAbsenceEvidence? absenceEvidence = null;
        if (classified.Action ==
                PublicationRecoveryAction.ResumeKnownNotWritten &&
            observation.Failure is { } failure)
        {
            if (!PublicationRecoveryInventoryFactory
                .TryCreateStickyWriteAuthorization(
                    observation,
                    failure.RecordIdentity,
                    PublicationStickyWriteTransition
                        .KnownNotWrittenRetry,
                    out stickyAuthorization) ||
                stickyAuthorization is null)
            {
                classified = PublicationRecoveryClassifier.Classify(
                    null,
                    PublicationMarkerObservation.Incomplete);
            }
        }
        else if (classified.Action is
            PublicationRecoveryAction.AbandonStaleCandidate or
            PublicationRecoveryAction.ResumeStaleCleanup)
        {
            absenceEvidence = PublicationRecoveryInventoryFactory
                .CreateMarkerAbsenceEvidence(
                    observation,
                    discovered.Kind,
                    discovered.Reason);
        }
        return Evaluation(
            classified,
            marker == PublicationMarkerObservation.Exact
                ? exactReadbackReceipt
                : null,
            discovered.Kind,
            discovered.Reason,
            observation,
            stickyAuthorization,
            absenceEvidence);
    }

    internal async Task<RetainedStateCleanupResult>
        AbandonAndCleanupStaleCandidateAsync(
        ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluation);

        var observation = evaluation.Observation;
        var initialAbsence = evaluation.MarkerAbsenceEvidence;
        var candidate = observation?.Candidate;
        var creatingAbandonment = evaluation.Decision.Action ==
            PublicationRecoveryAction.AbandonStaleCandidate;
        var resumingAbandonment = evaluation.Decision.Action ==
            PublicationRecoveryAction.ResumeStaleCleanup;
        if ((!creatingAbandonment && !resumingAbandonment) ||
            observation is null ||
            initialAbsence is null ||
            candidate is null ||
            candidate.MatchesCurrentReviewedHead)
        {
            return CleanupFailure(RetainedStateTransactionCodes.AccessDenied);
        }

        AbandonmentV1? abandonment = observation.Abandonment;
        OpaqueStoreObjectMetadata? persistedMetadata = null;
        StateControlHeaderV1? persistedHeader = null;
        if (creatingAbandonment)
        {
            using var abandonmentAuthorization =
                PublicationRecoveryInventoryFactory
                    .CreateStaleAbandonmentAuthorization(
                        observation,
                        initialAbsence);
            var ownershipResult = await RestrictedStateService
                .AuthorizeRetainedStaleAbandonmentOwnershipAsync(
                    context,
                    observation,
                    candidate,
                    abandonmentAuthorization,
                    cancellationToken)
                .ConfigureAwait(false);
            using var ownership = ownershipResult.Value;
            if (!ownershipResult.Succeeded ||
                ownership is null ||
                !PublicationRecoveryPersistence
                    .TryCreateStaleAbandonmentWrite(
                        observation,
                        abandonmentAuthorization,
                        observation.ObservedAtUnixSeconds,
                        out abandonment,
                        out var request) ||
                abandonment is null ||
                request is null)
            {
                return CleanupFailure(RetainedStateTransactionCodes.Conflict);
            }

            var attemptResult = await RestrictedStateService
                .PrepareRetainedOpaqueWriteAsync(
                    context,
                    ownership,
                    request,
                    cancellationToken)
                .ConfigureAwait(false);
            using var attempt = attemptResult.Value;
            if (!attemptResult.Succeeded || attempt is null)
            {
                return CleanupFailure(attemptResult.Code);
            }

            var persistedResult = await RestrictedStateService
                .PersistPreparedRetainedOpaqueWriteAsync(
                    context,
                    attempt,
                    cancellationToken)
                .ConfigureAwait(false);
            using var persisted = persistedResult.Value;
            if (!persistedResult.Succeeded || persisted is null)
            {
                return CleanupFailure(persistedResult.Code);
            }

            var anchorCleanup = await PublicationRecoveryPersistence
                .CleanupCompletedWriteAnchorAsync(
                    context,
                    attempt,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!anchorCleanup.Completed)
            {
                return CleanupFailure(anchorCleanup.Code);
            }

            persistedMetadata = persisted.Metadata;
            persistedHeader = persisted.Header;
        }

        if (abandonment is null)
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
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
            return CleanupFailure(freshInventoryResult.Code);
        }

        var freshObservationResult = await PublicationRecoveryInventoryFactory
            .CreateAsync(
                context,
                freshInventory,
                authorization.PullRequest.HeadSha,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var freshObservation = freshObservationResult.Value;
        if (!freshObservationResult.Succeeded ||
            freshObservation is null ||
            freshObservation.CandidateObjectIdentity !=
                candidate.Header.ObjectIdentity ||
            freshObservation.Abandonment != abandonment)
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
        }

        var abandonmentRecord = freshObservation.Records.SingleOrDefault(
            record =>
                (persistedMetadata is null ||
                    record.Metadata == persistedMetadata) &&
                (persistedHeader is null ||
                    record.Header == persistedHeader) &&
                record.ObjectClass == StateObjectClass.Abandonment &&
                StringComparer.Ordinal.Equals(
                    record.Header.PredecessorIdentity,
                    candidate.Header.ObjectIdentity));
        if (abandonmentRecord is null ||
            !TryRestoreRendered(
                freshObservation.StoredPublication,
                out var rendered) ||
            rendered is null ||
            !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                authorization,
                scope,
                rendered,
                out var readbackRequest) ||
            readbackRequest is null)
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
        }

        var discovered = await publisher.DiscoverAsync(
                token,
                readbackRequest,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (discovered.Kind != StickyDiscoveryKind.Absent ||
            discovered.Reason != StickyPublicationReason.None)
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
        }

        using var freshAbsence = PublicationRecoveryInventoryFactory
            .CreateMarkerAbsenceEvidence(
                freshObservation,
                discovered.Kind,
                discovered.Reason);
        using var staleCleanupAuthorization =
            PublicationRecoveryInventoryFactory
                .CreateStaleCleanupAuthorization(
                    freshObservation,
                    abandonment,
                    freshAbsence);

        var pendingResult = await RestrictedStateService
            .InspectRetainedPendingCandidateAsync(
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
        var pending = pendingResult.Value;
        if (!pendingResult.Succeeded ||
            pending is null ||
            !StringComparer.Ordinal.Equals(
                pending.InventoryDigest,
                freshObservation.InventoryDigest))
        {
            return CleanupFailure(pendingResult.Code);
        }

        var authorizationResult = await RestrictedStateService
            .AuthorizeRetainedStaleP5CleanupAsync(
                context,
                freshObservation,
                abandonment,
                staleCleanupAuthorization,
                pending,
                abandonmentRecord,
                CancellationToken.None)
            .ConfigureAwait(false);
        using var cleanupAuthorization = authorizationResult.Value;
        if (!authorizationResult.Succeeded ||
            cleanupAuthorization is null ||
            !PublicationRecoveryRetention.TryCompute(
                freshObservation.ObservedAtUnixSeconds,
                candidate.Header.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _))
        {
            return CleanupFailure(authorizationResult.Code);
        }

        return await RestrictedStateService
            .CleanupRetainedP5AuthorizedAsync(
                context,
                new RetainedStateP5CleanupRequest(
                    cleanupAuthorization,
                    semanticExpiry),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    internal static async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecord>> ResumeInterruptedWriteAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluation);

        var observation = evaluation.Observation;
        var inventory = observation?.Inventory;
        var candidate = observation?.Candidate;
        if (evaluation.Decision.Action !=
                PublicationRecoveryAction.ResumeAnchoredWrite ||
            observation is null ||
            inventory is null ||
            candidate is null ||
            inventory.Anchors.Length != 1 ||
            !StringComparer.Ordinal.Equals(
                inventory.Anchors[0].CandidateObjectIdentity,
                candidate.Header.ObjectIdentity))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    RetainedStateTransactionCodes.AccessDenied);
        }

        if (!candidate.MatchesCurrentReviewedHead)
        {
            return await ResumeStaleAbandonmentAnchorAsync(
                    context,
                    observation,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var recoveredResult = await RestrictedStateService
            .RecoverRetainedCandidateAsync(context, cancellationToken)
            .ConfigureAwait(false);
        var recovered = recoveredResult.Value;
        if (!recoveredResult.Succeeded ||
            recovered is null ||
            recovered.Metadata != candidate.Metadata ||
            recovered.Prepared.Header != candidate.Header)
        {
            recovered?.Prepared.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(recoveredResult.Code);
        }

        using var prepared = recovered.Prepared;
        var attemptsResult = await RestrictedStateService
            .RecoverAnchoredRetainedOpaqueWritesAsync(
                context,
                recovered,
                cancellationToken)
            .ConfigureAwait(false);
        using var attempts = attemptsResult.Value;
        var anchor = inventory.Anchors[0];
        if (!attemptsResult.Succeeded ||
            attempts is null ||
            attempts.Attempts.Length != 1 ||
            attempts.Attempts[0].AnchorMetadata != anchor.AnchorMetadata ||
            attempts.Attempts[0].Header.ObjectIdentity !=
                anchor.TargetObjectIdentity ||
            attempts.Attempts[0].ObjectClass != anchor.ObjectClass)
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    attemptsResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : attemptsResult.Code);
        }

        var attempt = attempts.Attempts[0];
        var persistedResult = await RestrictedStateService
            .PersistPreparedRetainedOpaqueWriteAsync(
                context,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!persistedResult.Succeeded || persistedResult.Value is null)
        {
            return persistedResult;
        }

        var anchorCleanup = await PublicationRecoveryPersistence
            .CleanupCompletedWriteAnchorAsync(
                context,
                attempt,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!anchorCleanup.Completed)
        {
            persistedResult.Value.Dispose();
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(anchorCleanup.Code);
        }

        return persistedResult;
    }

    private static async Task<RetainedStateTransactionResult<
        RetainedStateOpaqueRecord>> ResumeStaleAbandonmentAnchorAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryObservation observation,
        CancellationToken cancellationToken)
    {
        if (!PublicationRecoveryInventoryFactory
                .TryGetStaleAbandonmentAnchor(
                    observation,
                    out var anchor,
                    out var abandonmentRecord) ||
            anchor is null ||
            abandonmentRecord is null ||
            observation.Inventory is not { } inventory ||
            !PublicationRecoveryRetention.TryCompute(
                observation.ObservedAtUnixSeconds,
                anchor.AnchorHeader.LogicalExpiresAtUnixSeconds,
                out var semanticExpiry,
                out _))
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(
                    RetainedStateTransactionCodes.Conflict);
        }

        var classificationIdentity = PublicationRecoveryInventoryFactory
            .StaleAbandonmentAnchorCleanupClassificationIdentity(
                observation,
                anchor,
                abandonmentRecord);
        var authorizationResult = await RestrictedStateService
            .AuthorizeRetainedP5CleanupAsync(
                context,
                new RetainedStateP5CleanupDecision(
                    RetainedStateP5CleanupClassification
                        .CompletedOpaqueWriteAnchor,
                    classificationIdentity,
                    MarkerEvidenceIdentity: null),
                pendingCandidate: null,
                opaqueRecord: null,
                opaqueWrite: null,
                inventory,
                anchor,
                cancellationToken)
            .ConfigureAwait(false);
        using var cleanupAuthorization = authorizationResult.Value;
        if (!authorizationResult.Succeeded || cleanupAuthorization is null)
        {
            return RetainedStateTransactionResult<
                RetainedStateOpaqueRecord>.Fail(authorizationResult.Code);
        }

        var cleanup = await RestrictedStateService
            .CleanupRetainedP5AuthorizedAsync(
                context,
                new RetainedStateP5CleanupRequest(
                    cleanupAuthorization,
                    semanticExpiry),
                CancellationToken.None)
            .ConfigureAwait(false);
        return cleanup.Completed
            ? RetainedStateTransactionResult<RetainedStateOpaqueRecord>
                .Success(
                    RetainedStateTransactionCodes.Ready,
                    abandonmentRecord)
            : RetainedStateTransactionResult<RetainedStateOpaqueRecord>
                .Fail(cleanup.Code);
    }

    internal static Task<RetainedStateCleanupResult>
        ResumeInterruptedCleanupAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluation);

        var observation = evaluation.Observation;
        var inventory = observation?.Inventory;
        if (evaluation.Decision.Action !=
                PublicationRecoveryAction.ResumeCleanup ||
            inventory is null ||
            observation!.CleanupRecords.Length != 1)
        {
            return Task.FromResult(CleanupFailure(
                RetainedStateTransactionCodes.AccessDenied));
        }

        return RestrictedStateService.ResumeRetainedP5CleanupAsync(
            context,
            inventory,
            observation.CleanupRecords[0],
            cancellationToken);
    }

    internal static async Task<RetainedStateCleanupResult>
        CleanupHistoricalRecoveryRecordsAsync(
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(evaluation);

        var initial = evaluation.Observation;
        var currentTerminal = evaluation.Decision.Action ==
                PublicationRecoveryAction.ReturnCommitted &&
            initial?.CurrentAcceptedHeadMatchesReviewedHead == true;
        var supersededTerminal = evaluation.Decision.Action ==
                PublicationRecoveryAction.CleanupSupersededRecovery &&
            evaluation.Decision.Lifecycle ==
                PublicationRecoveryLifecycleState
                    .SupersededTerminalRecovery &&
            initial?.HistoricalTerminalRecovery == true;
        if ((!currentTerminal && !supersededTerminal) ||
            initial is null ||
            !initial.IsLive ||
            initial.Candidate is not null)
        {
            return CleanupFailure(RetainedStateTransactionCodes.AccessDenied);
        }

        if (supersededTerminal)
        {
            return await CleanupSupersededRecoveryRecordsAsync(
                    context,
                    initial,
                    authorization.PullRequest.HeadSha,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        PublicationRecoveryObservation? ownedObservation = null;
        var current = initial;
        try
        {
            while (!current.HistoricalRecords.IsEmpty ||
                !current.CompletedAnchors.IsEmpty)
            {
                var target = current.HistoricalRecords.IsEmpty
                    ? null
                    : current.HistoricalRecords[0];
                var anchor = target is null
                    ? current.CompletedAnchors[0]
                    : null;
                var classification = target is not null
                    ? RetainedStateP5CleanupClassification
                        .CompletedOpaqueRecord
                    : RetainedStateP5CleanupClassification
                        .CompletedOpaqueWriteAnchor;
                var classificationIdentity = target is not null
                    ? PublicationRecoveryInventoryFactory
                        .HistoricalCleanupClassificationIdentity(
                            current,
                            target)
                    : PublicationRecoveryInventoryFactory
                        .CompletedAnchorCleanupClassificationIdentity(
                            current,
                            anchor!);
                var authorizationResult = await RestrictedStateService
                    .AuthorizeRetainedP5CleanupAsync(
                        context,
                        new RetainedStateP5CleanupDecision(
                            classification,
                            classificationIdentity,
                            MarkerEvidenceIdentity: null),
                        pendingCandidate: null,
                        target,
                        opaqueWrite: null,
                        target is null ? current.Inventory : null,
                        anchor,
                        cancellationToken)
                    .ConfigureAwait(false);
                using var cleanupAuthorization = authorizationResult.Value;
                if (!authorizationResult.Succeeded ||
                    cleanupAuthorization is null ||
                    !PublicationRecoveryRetention.TryCompute(
                        current.ObservedAtUnixSeconds,
                        target?.Header.LogicalExpiresAtUnixSeconds ??
                            anchor!.AnchorHeader
                                .LogicalExpiresAtUnixSeconds,
                        out var semanticExpiry,
                        out _))
                {
                    return CleanupFailure(authorizationResult.Code);
                }

                var cleanup = await RestrictedStateService
                    .CleanupRetainedP5AuthorizedAsync(
                        context,
                        new RetainedStateP5CleanupRequest(
                            cleanupAuthorization,
                            semanticExpiry),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!cleanup.Completed)
                {
                    return cleanup;
                }

                ownedObservation?.Dispose();
                ownedObservation = null;
                var inventoryResult = await RestrictedStateService
                    .ObserveRetainedPublicationRecoveryInventoryAsync(
                        context,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                var inventory = inventoryResult.Value;
                if (!inventoryResult.Succeeded || inventory is null)
                {
                    inventory?.Dispose();
                    return CleanupFailure(inventoryResult.Code);
                }

                var observationResult =
                    await PublicationRecoveryInventoryFactory.CreateAsync(
                        context,
                        inventory,
                        authorization.PullRequest.HeadSha,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ownedObservation = observationResult.Value;
                if (!observationResult.Succeeded ||
                    ownedObservation is null ||
                    ownedObservation.Candidate is not null ||
                    !ownedObservation
                        .CurrentAcceptedHeadMatchesReviewedHead)
                {
                    return CleanupFailure(observationResult.Code);
                }

                current = ownedObservation;
            }

            return new RetainedStateCleanupResult(
                null,
                Completed: true,
                RetainedStateTransactionCodes.Ready);
        }
        finally
        {
            ownedObservation?.Dispose();
        }
    }

    private static async Task<RetainedStateCleanupResult>
        CleanupSupersededRecoveryRecordsAsync(
        AuthorizedAcceptedStateRestoreContext context,
        PublicationRecoveryObservation initial,
        string reviewedHeadSha,
        CancellationToken cancellationToken)
    {
        var initialInventory = initial.Inventory;
        var acceptedCandidateIdentity = initialInventory?
            .CurrentAcceptanceCandidateObjectIdentity;
        var acceptedPublication = initialInventory?.CurrentAcceptedPublication;
        if (initialInventory is null ||
            acceptedCandidateIdentity is null ||
            acceptedPublication is null ||
            initialInventory.CurrentAcceptance is null)
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
        }

        var remaining = initial.HistoricalRecords
            .Select(record => (record.Metadata, record.Header))
            .ToList();
        var remainingAnchors = initial.CompletedAnchors.ToList();
        PublicationRecoveryObservation? ownedObservation = null;
        var current = initial;
        try
        {
            while (remaining.Count > 0)
            {
                var planned = remaining[0];
                var target = current.HistoricalRecords.SingleOrDefault(record =>
                    record.Metadata == planned.Metadata &&
                    record.Header == planned.Header);
                var inventory = current.Inventory;
                if (target is null || inventory is null)
                {
                    return CleanupFailure(
                        RetainedStateTransactionCodes.Conflict);
                }

                var classificationIdentity =
                    PublicationRecoveryInventoryFactory
                        .SupersededCleanupClassificationIdentity(
                            inventory,
                            target,
                            acceptedCandidateIdentity);
                var authorizationResult = await RestrictedStateService
                    .AuthorizeRetainedP5CleanupAsync(
                        context,
                        new RetainedStateP5CleanupDecision(
                            RetainedStateP5CleanupClassification
                                .CompletedOpaqueRecord,
                            classificationIdentity,
                            MarkerEvidenceIdentity: null),
                        pendingCandidate: null,
                        target,
                        opaqueWrite: null,
                        recoveryInventory: null,
                        recoveryAnchor: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                using var cleanupAuthorization = authorizationResult.Value;
                if (!authorizationResult.Succeeded ||
                    cleanupAuthorization is null ||
                    !PublicationRecoveryRetention.TryCompute(
                        current.ObservedAtUnixSeconds,
                        target.Header.LogicalExpiresAtUnixSeconds,
                        out var semanticExpiry,
                        out _))
                {
                    return CleanupFailure(authorizationResult.Code);
                }

                var cleanup = await RestrictedStateService
                    .CleanupRetainedP5AuthorizedAsync(
                        context,
                        new RetainedStateP5CleanupRequest(
                            cleanupAuthorization,
                            semanticExpiry),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!cleanup.Completed)
                {
                    return cleanup;
                }

                remaining.RemoveAt(0);
                var refreshed = await RefreshSupersededInventoryAsync(
                        context,
                        reviewedHeadSha,
                        acceptedCandidateIdentity,
                        acceptedPublication,
                        remaining,
                        remainingAnchors)
                    .ConfigureAwait(false);
                if (!refreshed.Succeeded || refreshed.Value is null)
                {
                    refreshed.Value?.Dispose();
                    return CleanupFailure(refreshed.Code);
                }

                ownedObservation?.Dispose();
                ownedObservation = refreshed.Value;
                current = ownedObservation;
            }

            while (remainingAnchors.Count > 0)
            {
                var planned = remainingAnchors[0];
                var anchor = current.CompletedAnchors.SingleOrDefault(
                    candidate => SameSupersededAnchor(candidate, planned));
                var inventory = current.Inventory;
                if (anchor is null || inventory is null)
                {
                    return CleanupFailure(
                        RetainedStateTransactionCodes.Conflict);
                }
                var classificationIdentity =
                    PublicationRecoveryInventoryFactory
                        .SupersededAnchorCleanupClassificationIdentity(
                            inventory,
                            anchor,
                            acceptedCandidateIdentity);
                var authorizationResult = await RestrictedStateService
                    .AuthorizeRetainedP5CleanupAsync(
                        context,
                        new RetainedStateP5CleanupDecision(
                            RetainedStateP5CleanupClassification
                                .CompletedOpaqueWriteAnchor,
                            classificationIdentity,
                            MarkerEvidenceIdentity: null),
                        pendingCandidate: null,
                        opaqueRecord: null,
                        opaqueWrite: null,
                        inventory,
                        anchor,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                using var cleanupAuthorization = authorizationResult.Value;
                if (!authorizationResult.Succeeded ||
                    cleanupAuthorization is null ||
                    !PublicationRecoveryRetention.TryCompute(
                        current.ObservedAtUnixSeconds,
                        anchor.AnchorHeader.LogicalExpiresAtUnixSeconds,
                        out var semanticExpiry,
                        out _))
                {
                    return CleanupFailure(authorizationResult.Code);
                }

                var cleanup = await RestrictedStateService
                    .CleanupRetainedP5AuthorizedAsync(
                        context,
                        new RetainedStateP5CleanupRequest(
                            cleanupAuthorization,
                            semanticExpiry),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!cleanup.Completed)
                {
                    return cleanup;
                }

                remainingAnchors.RemoveAt(0);
                var refreshed = await RefreshSupersededInventoryAsync(
                        context,
                        reviewedHeadSha,
                        acceptedCandidateIdentity,
                        acceptedPublication,
                        remaining,
                        remainingAnchors)
                    .ConfigureAwait(false);
                if (!refreshed.Succeeded || refreshed.Value is null)
                {
                    refreshed.Value?.Dispose();
                    return CleanupFailure(refreshed.Code);
                }

                ownedObservation?.Dispose();
                ownedObservation = refreshed.Value;
                current = ownedObservation;
            }

            return new RetainedStateCleanupResult(
                null,
                Completed: true,
                RetainedStateTransactionCodes.Ready);
        }
        finally
        {
            ownedObservation?.Dispose();
        }
    }

    private static async Task<RetainedStateTransactionResult<
        PublicationRecoveryObservation>>
        RefreshSupersededInventoryAsync(
        AuthorizedAcceptedStateRestoreContext context,
        string reviewedHeadSha,
        string acceptedCandidateIdentity,
        ValidatedPublicationPayloadV1 acceptedPublication,
        IReadOnlyCollection<(OpaqueStoreObjectMetadata Metadata,
            StateControlHeaderV1 Header)> remaining,
        IReadOnlyCollection<RetainedStatePublicationRecoveryAnchorEvidence>
            remainingAnchors)
    {
        var result = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
        var inventory = result.Value;
        if (!result.Succeeded || inventory is null)
        {
            inventory?.Dispose();
            return RetainedStateTransactionResult<
                PublicationRecoveryObservation>.Fail(result.Code);
        }

        var observationResult = await PublicationRecoveryInventoryFactory
            .CreateAsync(
                context,
                inventory,
                reviewedHeadSha,
                CancellationToken.None)
            .ConfigureAwait(false);
        var observation = observationResult.Value;
        var refreshedInventory = observation?.Inventory;
        if (!observationResult.Succeeded ||
            observation is null ||
            refreshedInventory is null ||
            observation.Candidate is not null ||
            observation.CurrentAcceptedHeadMatchesReviewedHead ||
            !observation.HistoricalTerminalRecovery ||
            refreshedInventory.CurrentAcceptance is null ||
            !StringComparer.Ordinal.Equals(
                refreshedInventory.CurrentAcceptanceCandidateObjectIdentity,
                acceptedCandidateIdentity) ||
            refreshedInventory.CurrentAcceptedPublication is not
                { } publication ||
            !StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                acceptedPublication.ReviewedHeadSha) ||
            !StringComparer.Ordinal.Equals(
                publication.ScopeSha256,
                acceptedPublication.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(
                publication.BodySha256,
                acceptedPublication.BodySha256) ||
            observation.HistoricalRecords.Length != remaining.Count ||
            remaining.Any(planned => observation.HistoricalRecords.Count(
                record =>
                record.Metadata == planned.Metadata &&
                record.Header == planned.Header) != 1) ||
            observation.CompletedAnchors.Length != remainingAnchors.Count ||
            remainingAnchors.Any(planned =>
                observation.CompletedAnchors.Count(anchor =>
                    SameSupersededAnchor(anchor, planned)) != 1) ||
            !observation.CleanupRecords.IsEmpty)
        {
            observation?.Dispose();
            return RetainedStateTransactionResult<
                PublicationRecoveryObservation>.Fail(
                    observationResult.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : observationResult.Code);
        }

        return RetainedStateTransactionResult<
            PublicationRecoveryObservation>.Success(
                RetainedStateTransactionCodes.Ready,
                observation);
    }

    private static bool SameSupersededAnchor(
        RetainedStatePublicationRecoveryAnchorEvidence left,
        RetainedStatePublicationRecoveryAnchorEvidence right) =>
        left.AnchorMetadata == right.AnchorMetadata &&
        left.AnchorHeader == right.AnchorHeader &&
        StringComparer.Ordinal.Equals(
            left.CandidateObjectIdentity,
            right.CandidateObjectIdentity) &&
        StringComparer.Ordinal.Equals(
            left.OperationIdentity,
            right.OperationIdentity) &&
        left.ObjectClass == right.ObjectClass &&
        left.TargetName == right.TargetName &&
        StringComparer.Ordinal.Equals(
            left.TargetObjectIdentity,
            right.TargetObjectIdentity) &&
        StringComparer.Ordinal.Equals(
            left.TargetPayloadSha256,
            right.TargetPayloadSha256);

    internal static bool TryRestoreRendered(
        ValidatedPublicationPayloadV1? stored,
        out R4RenderedStickyComment? rendered)
    {
        rendered = null;
        if (stored is null || stored.FinalizedCommentUtf8.IsDefaultOrEmpty)
        {
            return false;
        }

        try
        {
            var comment = StrictUtf8.GetString(
                stored.FinalizedCommentUtf8.AsSpan());
            var inspected = R4StickyMarker.Inspect(comment);
            if (inspected.Kind != R4StickyInspectionKind.ValidR4 ||
                inspected.Body is null ||
                inspected.Identity is null ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.ScopeSha256,
                    stored.ScopeSha256) ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.BodySha256,
                    stored.BodySha256) ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.HeadSha,
                    stored.ReviewedHeadSha))
            {
                return false;
            }

            rendered = new R4RenderedStickyComment(
                comment,
                inspected.Body,
                inspected.Identity,
                ImmutableArray<R4FindingIdentityV1>.Empty,
                RenderedFindingCount: 0,
                OmittedFindingCount: 0);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private async Task<bool> IsPreviousAcceptedTargetAsync(
        ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        PublicationRecoveryObservation observation,
        CancellationToken cancellationToken)
    {
        var inventory = observation.Inventory;
        if (observation.Candidate is null ||
            !observation.CandidateMatchesCurrentHead ||
            observation.StickyReadback is not null ||
            observation.Recovery is not null ||
            inventory?.CurrentAcceptedPublication is not { } accepted ||
            inventory.CurrentAcceptancePublicationReceipt is not
                { } durableReceipt ||
            !TryRestoreRendered(accepted, out var rendered) ||
            rendered is null ||
            !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                authorization,
                scope,
                rendered,
                durableReceipt,
                out var request) ||
            request is null)
        {
            return false;
        }

        var discovered = await publisher.DiscoverAsync(
                token,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        return discovered.Kind == StickyDiscoveryKind.ExactTarget &&
            discovered.Receipt is { } freshReceipt &&
            PublicationReceiptMatcher.IsFreshObservationOf(
                durableReceipt,
                freshReceipt);
    }

    private static PublicationRecoveryEvaluation Evaluation(
        PublicationRecoveryDecision decision,
        StickyCommentPublisher.StickyPublicationReceipt? receipt,
        StickyDiscoveryKind kind,
        StickyPublicationReason reason,
        PublicationRecoveryObservation? observation,
        PublicationStickyWriteAuthorization? stickyAuthorization = null,
        PublicationMarkerAbsenceEvidence? absenceEvidence = null) =>
        new(
            decision,
            receipt,
            kind,
            reason,
            observation,
            stickyAuthorization,
            absenceEvidence);

    private static RetainedStateCleanupResult CleanupFailure(string code) =>
        new(null, Completed: false, code);

    private static bool TryResolveExactReadbackReceipt(
        PublicationRecoveryObservation observation,
        StickyCommentPublisher.StickyPublicationReceipt fresh,
        out StickyCommentPublisher.StickyPublicationReceipt? exact)
    {
        exact = null;
        StickyCommentPublisher.StickyPublicationReceipt? durable = null;
        if (observation.StickyReadback is { } readback)
        {
            if (!readback.TryRehydrate(out durable) || durable is null)
            {
                return false;
            }
        }
        else if (observation.Candidate is null)
        {
            durable = observation.Inventory?
                .CurrentAcceptancePublicationReceipt;
        }

        if (durable is null)
        {
            if (fresh.Operation != StickyPublicationOperation.Observed)
            {
                return false;
            }

            exact = fresh;
            return true;
        }

        if (!PublicationReceiptMatcher.IsFreshObservationOf(durable, fresh))
        {
            return false;
        }

        exact = durable;
        return true;
    }
}
