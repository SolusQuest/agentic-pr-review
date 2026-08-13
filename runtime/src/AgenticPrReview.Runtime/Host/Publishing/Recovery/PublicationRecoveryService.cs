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
            !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                authorization,
                scope,
                rendered,
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
        var classified = PublicationRecoveryClassifier.Classify(
            observation,
            marker);
        PublicationStickyWriteAuthorization? stickyAuthorization = null;
        PublicationMarkerAbsenceEvidence? absenceEvidence = null;
        if (classified.Action ==
                PublicationRecoveryAction.ResumeKnownNotWritten &&
            observation.Failure is { } failure)
        {
            stickyAuthorization = PublicationRecoveryInventoryFactory
                .CreateStickyWriteAuthorization(
                    observation,
                    failure.RecordIdentity);
        }
        else if (classified.Action ==
            PublicationRecoveryAction.AbandonStaleCandidate)
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
                ? discovered.Receipt
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
        if (evaluation.Decision.Action !=
                PublicationRecoveryAction.AbandonStaleCandidate ||
            observation is null ||
            initialAbsence is null ||
            candidate is null ||
            candidate.MatchesCurrentReviewedHead)
        {
            return CleanupFailure(RetainedStateTransactionCodes.AccessDenied);
        }

        var ownershipResult = await RestrictedStateService
            .AuthorizeRetainedStaleAbandonmentOwnershipAsync(
                context,
                candidate,
                cancellationToken)
            .ConfigureAwait(false);
        using var ownership = ownershipResult.Value;
        if (!ownershipResult.Succeeded ||
            ownership is null ||
            !PublicationRecoveryPersistence
                .TryCreateStaleAbandonmentWrite(
                    observation,
                    initialAbsence,
                    observation.ObservedAtUnixSeconds,
                    out var abandonment,
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
                record.Metadata == persisted.Metadata &&
                record.Header == persisted.Header);
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
        var classificationIdentity =
            PublicationRecoveryInventoryFactory
                .StaleCleanupClassificationIdentity(
                    freshObservation,
                    abandonment,
                    freshAbsence);
        if (!PublicationRecoveryInventoryFactory
            .TryConsumeMarkerAbsenceEvidence(
                freshObservation,
                freshAbsence))
        {
            return CleanupFailure(RetainedStateTransactionCodes.Conflict);
        }

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

        var decision = new RetainedStateP5CleanupDecision(
            RetainedStateP5CleanupClassification
                .StaleCandidateAbandonment,
            classificationIdentity,
            freshAbsence.EvidenceIdentity);
        var authorizationResult = await RestrictedStateService
            .AuthorizeRetainedP5CleanupAsync(
                context,
                decision,
                pending,
                abandonmentRecord,
                opaqueWrite: null,
                recoveryInventory: null,
                recoveryAnchor: null,
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
                PublicationRecoveryAction.NoPendingWork &&
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
        RetainedStatePublicationRecoveryInventory? ownedInventory = null;
        var current = initialInventory;
        try
        {
            while (remaining.Count > 0)
            {
                var planned = remaining[0];
                var target = current.Records.SingleOrDefault(record =>
                    record.Metadata == planned.Metadata &&
                    record.Header == planned.Header);
                if (target is null)
                {
                    return CleanupFailure(
                        RetainedStateTransactionCodes.Conflict);
                }

                var classificationIdentity =
                    PublicationRecoveryInventoryFactory
                        .SupersededCleanupClassificationIdentity(
                            current,
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
                        acceptedCandidateIdentity,
                        acceptedPublication,
                        remaining)
                    .ConfigureAwait(false);
                if (!refreshed.Succeeded || refreshed.Value is null)
                {
                    refreshed.Value?.Dispose();
                    return CleanupFailure(refreshed.Code);
                }

                ownedInventory?.Dispose();
                ownedInventory = refreshed.Value;
                current = ownedInventory;
            }

            while (current.Anchors.Any(anchor => !anchor.TargetIsPresent))
            {
                var anchor = current.Anchors.First(item =>
                    !item.TargetIsPresent);
                var classificationIdentity =
                    PublicationRecoveryInventoryFactory
                        .SupersededAnchorCleanupClassificationIdentity(
                            current,
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
                        current,
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

                var refreshed = await RefreshSupersededInventoryAsync(
                        context,
                        acceptedCandidateIdentity,
                        acceptedPublication,
                        remaining)
                    .ConfigureAwait(false);
                if (!refreshed.Succeeded || refreshed.Value is null)
                {
                    refreshed.Value?.Dispose();
                    return CleanupFailure(refreshed.Code);
                }

                ownedInventory?.Dispose();
                ownedInventory = refreshed.Value;
                current = ownedInventory;
            }

            return new RetainedStateCleanupResult(
                null,
                Completed: true,
                RetainedStateTransactionCodes.Ready);
        }
        finally
        {
            ownedInventory?.Dispose();
        }
    }

    private static async Task<RetainedStateTransactionResult<
        RetainedStatePublicationRecoveryInventory>>
        RefreshSupersededInventoryAsync(
        AuthorizedAcceptedStateRestoreContext context,
        string acceptedCandidateIdentity,
        ValidatedPublicationPayloadV1 acceptedPublication,
        IReadOnlyCollection<(OpaqueStoreObjectMetadata Metadata,
            StateControlHeaderV1 Header)> remaining)
    {
        var result = await RestrictedStateService
            .ObserveRetainedPublicationRecoveryInventoryAsync(
                context,
                CancellationToken.None)
            .ConfigureAwait(false);
        var inventory = result.Value;
        if (!result.Succeeded ||
            inventory is null ||
            inventory.Candidate is not null ||
            inventory.CurrentAcceptance is null ||
            !StringComparer.Ordinal.Equals(
                inventory.CurrentAcceptanceCandidateObjectIdentity,
                acceptedCandidateIdentity) ||
            inventory.CurrentAcceptedPublication is not { } publication ||
            !StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                acceptedPublication.ReviewedHeadSha) ||
            !StringComparer.Ordinal.Equals(
                publication.ScopeSha256,
                acceptedPublication.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(
                publication.BodySha256,
                acceptedPublication.BodySha256) ||
            inventory.Records.Length != remaining.Count ||
            remaining.Any(planned => inventory.Records.Count(record =>
                record.Metadata == planned.Metadata &&
                record.Header == planned.Header) != 1))
        {
            inventory?.Dispose();
            return RetainedStateTransactionResult<
                RetainedStatePublicationRecoveryInventory>.Fail(
                    result.Succeeded
                        ? RetainedStateTransactionCodes.Conflict
                        : result.Code);
        }

        return result;
    }

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
}
