using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationRecoveryInventoryFactory
{
    private static readonly object CapabilityIssuer = new();

    internal static bool IsIssuer(object? value) =>
        ReferenceEquals(value, CapabilityIssuer);

    internal static void RequireIssuer(object? value)
    {
        if (!IsIssuer(value))
        {
            throw new ArgumentException(
                "The publication-recovery issuer is not authorized.",
                nameof(value));
        }
    }

    internal static async Task<RetainedStateTransactionResult<
        PublicationRecoveryObservation>> CreateAsync(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStatePublicationRecoveryInventory inventory,
        string reviewedHeadSha,
        CancellationToken cancellationToken)
    {
        PublicationRecoveryObservation? observation = null;
        try
        {
            if (context is null ||
                inventory is null ||
                !LineageValidation.IsGitSha(reviewedHeadSha) ||
                inventory.Records.IsDefault ||
                !LineageValidation.IsSha256(inventory.InventoryDigest) ||
                inventory.Records.Any(record =>
                    record is null ||
                    !StringComparer.Ordinal.Equals(
                        record.InventoryDigest,
                        inventory.InventoryDigest)) ||
                inventory.CleanupRecords.IsDefault ||
                inventory.CleanupRecords.Length > 1)
            {
                return Fail();
            }

            if (!context.TryGetProducingRun(out var currentProducingRun) ||
                currentProducingRun is null)
            {
                return Fail();
            }

            var pending = inventory.Candidate;
            if (pending is not null &&
                (!StringComparer.Ordinal.Equals(
                    pending.InventoryDigest,
                    inventory.InventoryDigest) ||
                !PublicationRecoveryPersistence.TryPublication(
                    pending,
                    out var pendingPublication) ||
                pendingPublication is null))
            {
                return Fail();
            }

            var pendingIdentity = pending?.Header.ObjectIdentity;
            var acceptedIdentity =
                inventory.CurrentAcceptanceCandidateObjectIdentity;
            var acceptedPublication = inventory.CurrentAcceptedPublication;
            var pendingSet = new ParsedRecordSet();
            var acceptedSet = new ParsedRecordSet();
            var acceptedRecords = ImmutableArray.CreateBuilder<
                RetainedStateOpaqueRecord>();
            var historicalRecords = ImmutableArray.CreateBuilder<
                RetainedStateOpaqueRecord>();
            var hasHistoricalCleanupDebt = false;
            foreach (var record in inventory.Records)
            {
                if (!TryDecode(context, record, out var decoded) ||
                    decoded is null)
                {
                    return Fail();
                }

                var predecessor = record.Header.PredecessorIdentity;
                if (pendingIdentity is not null &&
                    StringComparer.Ordinal.Equals(
                        predecessor,
                        pendingIdentity))
                {
                    if (!decoded.Matches(pending!.Publication) ||
                        !pendingSet.TryAdd(decoded))
                    {
                        return Fail();
                    }
                }
                else if (acceptedIdentity is not null &&
                    StringComparer.Ordinal.Equals(
                        predecessor,
                        acceptedIdentity))
                {
                    if (acceptedPublication is null ||
                        !decoded.Matches(acceptedPublication) ||
                        !acceptedSet.TryAdd(decoded))
                    {
                        return Fail();
                    }
                    acceptedRecords.Add(record);
                }
                else if (inventory.CleanupRecords.Any(cleanup =>
                    cleanup.Cleanup.Targets.Contains(record.Metadata)))
                {
                    // An authenticated cleanup record owns this exact orphan.
                    // The closed decision surface resumes that cleanup before
                    // interpreting any remaining publication graph.
                }
                else
                {
                    return Fail();
                }
            }

            var pendingReadback = pendingSet.StickyReadback ??
                pendingSet.Recovery?.StickyReadback;
            if (pendingSet.Recovery is not null &&
                (pendingReadback is null ||
                    pendingSet.StickyReadback is not null &&
                    pendingSet.StickyReadback !=
                        pendingSet.Recovery.StickyReadback))
            {
                return Fail();
            }
            if (!ValidAttemptGraph(pendingSet))
            {
                return Fail();
            }

            var acceptedRecordCount = acceptedSet.Count;
            MatchedRetainedStateRecoveryAcceptance? matched = null;
            if (acceptedRecordCount > 0)
            {
                var activeFailure = acceptedSet.RetryFailure is { } retry
                    ? (retry.Outcome, HasIntent: acceptedSet.RetryIntent is not null)
                    : acceptedSet.Failure is { } initial
                        ? (initial.Outcome, HasIntent: acceptedSet.Intent is not null)
                        : ((BoundedGitHubPublisherOutcome?)null, HasIntent: true);
                var failureIsResolvedOutcomeUnknown =
                    activeFailure.Item1 is null ||
                    activeFailure.Item1 ==
                        BoundedGitHubPublisherOutcome.OutcomeUnknown &&
                    activeFailure.HasIntent;
                if (inventory.CurrentAcceptance is null ||
                    acceptedIdentity is null ||
                    acceptedSet.Recovery is null ||
                    !ValidAttemptGraph(
                        acceptedSet,
                        allowTerminalCleanupSubgraph: true) ||
                    !failureIsResolvedOutcomeUnknown ||
                    acceptedSet.Abandonment is not null ||
                    acceptedSet.StickyReadback is not null &&
                        acceptedSet.StickyReadback !=
                            acceptedSet.Recovery.StickyReadback)
                {
                    return Fail();
                }

                var recoveryRecord = inventory.Records.SingleOrDefault(
                    record =>
                        record.Metadata == acceptedSet.RecoveryMetadata);
                if (recoveryRecord is null)
                {
                    return Fail();
                }

                using var extraction = PublicationRecoveryPersistence
                    .CreateAcceptanceRecoveryExtraction(
                        context,
                        recoveryRecord).Value;
                if (extraction is null)
                {
                    return Fail();
                }

                var matchedResult = await RestrictedStateService
                    .MatchRecoveredRetainedStateAcceptanceAsync(
                        context,
                        acceptedIdentity,
                        recoveryRecord,
                        extraction,
                        inventory.CurrentAcceptance,
                        cancellationToken)
                    .ConfigureAwait(false);
                matched = matchedResult.Value;
                if (!matchedResult.Succeeded ||
                    matched is null ||
                    !StringComparer.Ordinal.Equals(
                        matched.InventoryDigest,
                        inventory.InventoryDigest) ||
                    matched.RecoveryRecordMetadata !=
                        recoveryRecord.Metadata ||
                    matched.RecoveryRecordHeader != recoveryRecord.Header ||
                    !acceptedSet.Recovery.StickyReadback.TryRehydrate(
                        out var readbackReceipt) ||
                    readbackReceipt is null ||
                    !PublicationReceiptMatcher.AreDurablyEqual(
                        readbackReceipt,
                        matched.Receipt))
                {
                    return Fail();
                }
            }

            var currentAcceptanceMatchesExecution =
                acceptedPublication is not null &&
                StringComparer.Ordinal.Equals(
                    acceptedPublication.ReviewedHeadSha,
                    reviewedHeadSha) &&
                inventory.CurrentAcceptance is { } currentAcceptance &&
                // A higher attempt of the same GitHub run is recovery. A
                // distinct run on the same head is a new continuation.
                StringComparer.Ordinal.Equals(
                    currentAcceptance.ReceiptMetadata.ProducingRun.Identity,
                    currentProducingRun.Identity);
            if (!currentAcceptanceMatchesExecution)
            {
                if (!TryAddAcceptedCleanupRecords(
                        acceptedSet,
                        acceptedRecords,
                        historicalRecords))
                {
                    return Fail();
                }
                hasHistoricalCleanupDebt |= acceptedRecords.Count > 0;
            }
            else if (pending is null && acceptedRecords.Count > 0)
            {
                if (!TryAddAcceptedCleanupRecords(
                        acceptedSet,
                        acceptedRecords,
                        historicalRecords))
                {
                    return Fail();
                }
                hasHistoricalCleanupDebt = true;
            }
            var terminalProven = acceptedRecordCount == 0
                ? inventory.CurrentAcceptance is not null &&
                    inventory.CurrentAcceptancePublicationReceipt is not null
                : matched is not null;
            if (currentAcceptanceMatchesExecution && !terminalProven)
            {
                return Fail();
            }

            var selected = pending is not null
                ? pendingSet
                : currentAcceptanceMatchesExecution
                    ? acceptedSet
                    : new ParsedRecordSet();
            var selectedPublication = pending?.Publication ??
                (currentAcceptanceMatchesExecution ? acceptedPublication : null);
            var selectedIdentity = pendingIdentity ??
                (currentAcceptanceMatchesExecution ? acceptedIdentity : null);
            var selectedMatched = pending is null &&
                currentAcceptanceMatchesExecution
                ? matched
                : null;
            if (inventory.Anchors.Any(anchor =>
                    !StringComparer.Ordinal.Equals(
                        anchor.CandidateObjectIdentity,
                        pendingIdentity) &&
                    !StringComparer.Ordinal.Equals(
                        anchor.CandidateObjectIdentity,
                        acceptedIdentity)))
            {
                return Fail();
            }

            var selectedAnchors = inventory.Anchors.Where(anchor =>
                    selectedIdentity is not null &&
                    StringComparer.Ordinal.Equals(
                        anchor.CandidateObjectIdentity,
                        selectedIdentity))
                .ToArray();
            var acceptedAnchors = inventory.Anchors.Where(anchor =>
                    acceptedIdentity is not null &&
                    StringComparer.Ordinal.Equals(
                        anchor.CandidateObjectIdentity,
                        acceptedIdentity))
                .ToArray();
            if (acceptedAnchors.Length > 0 && !terminalProven)
            {
                return Fail();
            }

            var anchors = pending is not null && selectedAnchors.Length > 1
                ? PublicationRecoveryAnchorState.Ambiguous
                : pending is not null && selectedAnchors.Length == 1
                    ? selectedAnchors[0].TargetIsPresent
                        ? PublicationRecoveryAnchorState.RecoverableWrite
                        : PublicationRecoveryAnchorState.Unresolved
                : inventory.Anchors.IsEmpty
                    ? PublicationRecoveryAnchorState.None
                    : PublicationRecoveryAnchorState.CleanupDebt;
            hasHistoricalCleanupDebt |= inventory.Anchors.Any(anchor =>
                selectedIdentity is null ||
                !StringComparer.Ordinal.Equals(
                    anchor.CandidateObjectIdentity,
                    selectedIdentity));

            observation = new PublicationRecoveryObservation(
                CapabilityIssuer,
                inventory,
                selectedIdentity,
                selectedPublication,
                selected.Intent,
                selected.RetryIntent,
                selected.StickyReadback ?? selected.Recovery?.StickyReadback,
                selected.Failure,
                selected.RetryFailure,
                selected.Abandonment,
                selected.Recovery,
                selectedMatched,
                historicalRecords.ToImmutable(),
                acceptedAnchors.ToImmutableArray(),
                inventory.CleanupRecords,
                pending?.MatchesCurrentReviewedHead ?? false,
                currentAcceptanceMatchesExecution,
                terminalProven && !currentAcceptanceMatchesExecution,
                hasHistoricalCleanupDebt,
                anchors);
            return RetainedStateTransactionResult<
                PublicationRecoveryObservation>.Success(
                    RetainedStateTransactionCodes.Ready,
                    observation);
        }
        finally
        {
            if (observation is null)
            {
                inventory?.Dispose();
            }
        }
    }

    internal static PublicationStickyWriteAuthorization
        CreateStickyWriteAuthorization(
        PublicationRecoveryObservation observation,
        string evidenceRecordIdentity,
        PublicationStickyWriteTransition transition)
    {
        if (!TryCreateStickyWriteAuthorization(
                observation,
                evidenceRecordIdentity,
                transition,
                out var authorization) ||
            authorization is null)
        {
            throw new ArgumentException(
                "A live exact recovery observation is required.",
                nameof(observation));
        }

        return authorization;
    }

    internal static bool TryCreateStickyWriteAuthorization(
        PublicationRecoveryObservation? observation,
        string evidenceRecordIdentity,
        PublicationStickyWriteTransition transition,
        out PublicationStickyWriteAuthorization? authorization)
    {
        authorization = null;
        if (observation is null ||
            !observation.IsLive ||
            observation.CandidateObjectIdentity is not { } candidate ||
            !LineageValidation.IsSha256(evidenceRecordIdentity) ||
            !CanAuthorizeSticky(observation, evidenceRecordIdentity, transition))
        {
            return false;
        }

        authorization = new PublicationStickyWriteAuthorization(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest,
            evidenceRecordIdentity,
            transition);
        return true;
    }

    internal static bool TryConsumeStickyWriteAuthorization(
        PublicationRecoveryObservation observation,
        PublicationStickyWriteAuthorization authorization)
    {
        var evidence = authorization?.Transition switch
        {
            PublicationStickyWriteTransition.InitialIntent =>
                observation?.Intent?.RecordIdentity,
            PublicationStickyWriteTransition.RetryIntent =>
                observation?.RetryIntent?.RecordIdentity,
            _ => null,
        };
        return observation is not null &&
            authorization is not null &&
            observation.IsLive &&
            observation.CandidateObjectIdentity is { } candidate &&
            evidence is not null &&
            StringComparer.Ordinal.Equals(
                authorization.CandidateObjectIdentity,
                candidate) &&
            StringComparer.Ordinal.Equals(
                authorization.InventoryDigest,
                observation.InventoryDigest) &&
            StringComparer.Ordinal.Equals(
                authorization.EvidenceRecordIdentity,
                evidence) &&
            CanAuthorizeSticky(
                observation,
                evidence,
                authorization.Transition) &&
            authorization.TryConsume(CapabilityIssuer);
    }

    internal static bool TryCreateRetryTransitionAuthorization(
        PublicationRecoveryObservation? observation,
        out PublicationRetryTransitionAuthorization? authorization)
    {
        authorization = null;
        if (observation is null ||
            !observation.IsLive ||
            observation.CandidateObjectIdentity is not { } candidate ||
            observation.Intent is not { } intent ||
            observation.Failure is not
            {
                Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
            } failure ||
            observation.RetryIntent is not null ||
            observation.RetryFailure is not null ||
            observation.StickyReadback is not null ||
            observation.Abandonment is not null ||
            observation.Recovery is not null ||
            observation.Anchors != PublicationRecoveryAnchorState.None ||
            observation.Records.Length != 2 ||
            !StringComparer.Ordinal.Equals(
                failure.AttemptIntentRecordIdentity,
                intent.RecordIdentity))
        {
            return false;
        }

        authorization = new PublicationRetryTransitionAuthorization(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest,
            intent.RecordIdentity,
            failure.RecordIdentity);
        return true;
    }

    internal static bool TryConsumeRetryTransitionAuthorization(
        PublicationRecoveryObservation observation,
        PublicationRetryTransitionAuthorization authorization) =>
        observation is not null &&
        authorization is not null &&
        observation.IsLive &&
        observation.CandidateObjectIdentity is { } candidate &&
        observation.Intent is { } intent &&
        observation.Failure is
        {
            Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
        } failure &&
        observation.RetryIntent is null &&
        observation.RetryFailure is null &&
        observation.StickyReadback is null &&
        observation.Abandonment is null &&
        observation.Recovery is null &&
        observation.Anchors == PublicationRecoveryAnchorState.None &&
        observation.Records.Length == 2 &&
        StringComparer.Ordinal.Equals(
            failure.AttemptIntentRecordIdentity,
            intent.RecordIdentity) &&
        authorization.TryConsume(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest,
            intent.RecordIdentity,
            failure.RecordIdentity);

    internal static PublicationStaleAbandonmentAuthorization
        CreateStaleAbandonmentAuthorization(
        PublicationRecoveryObservation observation,
        PublicationMarkerAbsenceEvidence absenceEvidence)
    {
        if (observation is null ||
            absenceEvidence is null ||
            observation.Candidate is not { MatchesCurrentReviewedHead: false } ||
            observation.Intent is not null ||
            observation.RetryIntent is not null ||
            observation.StickyReadback is not null ||
            observation.Failure is not null ||
            observation.RetryFailure is not null ||
            observation.Abandonment is not null ||
            observation.Recovery is not null ||
            observation.Anchors != PublicationRecoveryAnchorState.None ||
            !observation.HistoricalRecords.IsEmpty ||
            !observation.CleanupRecords.IsEmpty ||
            !TryConsumeMarkerAbsenceEvidence(
                observation,
                absenceEvidence))
        {
            throw new ArgumentException(
                "Exact stale-abandonment evidence is required.");
        }

        return new PublicationStaleAbandonmentAuthorization(
            CapabilityIssuer,
            observation.CandidateObjectIdentity!,
            observation.InventoryDigest,
            absenceEvidence.EvidenceIdentity);
    }

    internal static bool TryAuthorizeStaleAbandonmentOwnership(
        PublicationRecoveryObservation observation,
        PublicationStaleAbandonmentAuthorization authorization) =>
        observation is not null &&
        authorization is not null &&
        observation.CandidateObjectIdentity is { } candidate &&
        authorization.TryAuthorizeOwnership(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest);

    internal static bool TryConsumeStaleAbandonmentWriteAuthorization(
        PublicationRecoveryObservation observation,
        PublicationStaleAbandonmentAuthorization authorization) =>
        observation is not null &&
        authorization is not null &&
        observation.CandidateObjectIdentity is { } candidate &&
        authorization.TryCreateWrite(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest);

    internal static PublicationStaleCleanupAuthorization
        CreateStaleCleanupAuthorization(
        PublicationRecoveryObservation observation,
        AbandonmentV1 abandonment,
        PublicationMarkerAbsenceEvidence freshAbsence)
    {
        var classificationIdentity = StaleCleanupClassificationIdentity(
            observation,
            abandonment,
            freshAbsence);
        if (observation.Abandonment != abandonment ||
            !TryConsumeMarkerAbsenceEvidence(observation, freshAbsence))
        {
            throw new ArgumentException(
                "Exact fresh stale-cleanup evidence is required.");
        }

        return new PublicationStaleCleanupAuthorization(
            CapabilityIssuer,
            observation.CandidateObjectIdentity!,
            observation.InventoryDigest,
            abandonment.RecordIdentity,
            classificationIdentity,
            freshAbsence.EvidenceIdentity);
    }

    internal static bool TryConsumeStaleCleanupAuthorization(
        PublicationRecoveryObservation observation,
        AbandonmentV1 abandonment,
        PublicationStaleCleanupAuthorization authorization) =>
        observation is not null &&
        abandonment is not null &&
        authorization is not null &&
        observation.CandidateObjectIdentity is { } candidate &&
        authorization.TryConsume(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest,
            abandonment.RecordIdentity);

    internal static bool TryGetStaleAbandonmentAnchor(
        PublicationRecoveryObservation? observation,
        out RetainedStatePublicationRecoveryAnchorEvidence? anchor,
        out RetainedStateOpaqueRecord? abandonmentRecord)
    {
        anchor = null;
        abandonmentRecord = null;
        var candidate = observation?.Candidate;
        var inventory = observation?.Inventory;
        if (observation is null ||
            !observation.IsLive ||
            candidate is null ||
            candidate.MatchesCurrentReviewedHead ||
            observation.Abandonment is null ||
            observation.Intent is not null ||
            observation.StickyReadback is not null ||
            observation.Failure is not null ||
            observation.Recovery is not null ||
            observation.Anchors !=
                PublicationRecoveryAnchorState.RecoverableWrite ||
            !observation.HistoricalRecords.IsEmpty ||
            !observation.CompletedAnchors.IsEmpty ||
            !observation.CleanupRecords.IsEmpty ||
            inventory is null ||
            inventory.Anchors.Length != 1 ||
            observation.Records.Length != 1)
        {
            return false;
        }

        var selectedAnchor = inventory.Anchors[0];
        var selectedRecord = observation.Records[0];
        if (!selectedAnchor.TargetIsPresent ||
            selectedAnchor.ObjectClass != StateObjectClass.Abandonment ||
            selectedRecord.ObjectClass != StateObjectClass.Abandonment ||
            !StringComparer.Ordinal.Equals(
                selectedAnchor.CandidateObjectIdentity,
                candidate.Header.ObjectIdentity) ||
            !StringComparer.Ordinal.Equals(
                selectedAnchor.TargetObjectIdentity,
                selectedRecord.Header.ObjectIdentity) ||
            !StringComparer.Ordinal.Equals(
                selectedRecord.Header.PredecessorIdentity,
                candidate.Header.ObjectIdentity))
        {
            return false;
        }

        anchor = selectedAnchor;
        abandonmentRecord = selectedRecord;
        return true;
    }

    internal static string StaleAbandonmentAnchorCleanupClassificationIdentity(
        PublicationRecoveryObservation observation,
        RetainedStatePublicationRecoveryAnchorEvidence anchor,
        RetainedStateOpaqueRecord abandonmentRecord)
    {
        if (!TryGetStaleAbandonmentAnchor(
                observation,
                out var expectedAnchor,
                out var expectedRecord) ||
            expectedAnchor != anchor ||
            expectedRecord != abandonmentRecord)
        {
            throw new ArgumentException(
                "An exact stale abandonment anchor is required.",
                nameof(anchor));
        }

        return CleanupIdentity(
            "publication-stale-abandonment-anchor-cleanup-v1",
            observation.InventoryDigest,
            observation.CandidateObjectIdentity!,
            anchor.AnchorHeader.ObjectIdentity);
    }

    private static bool CanAuthorizeSticky(
        PublicationRecoveryObservation observation,
        string evidenceRecordIdentity,
        PublicationStickyWriteTransition transition)
    {
        if (!observation.IsLive ||
            observation.Candidate is null ||
            observation.Anchors != PublicationRecoveryAnchorState.None ||
            !observation.HistoricalRecords.IsEmpty ||
            !observation.CompletedAnchors.IsEmpty ||
            !observation.CleanupRecords.IsEmpty)
        {
            return false;
        }

        return transition switch
        {
            PublicationStickyWriteTransition.InitialIntent =>
                observation.Records.Length == 1 &&
                observation.Intent is { } intent &&
                StringComparer.Ordinal.Equals(
                    intent.RecordIdentity,
                    evidenceRecordIdentity) &&
                observation.StickyReadback is null &&
                observation.Failure is null &&
                observation.RetryIntent is null &&
                observation.RetryFailure is null &&
                observation.Abandonment is null &&
                observation.Recovery is null,
            PublicationStickyWriteTransition.RetryIntent =>
                observation.Records.Length == 3 &&
                observation.Intent is not null &&
                observation.Failure is
                {
                    Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
                } failure &&
                observation.RetryIntent is { } retryIntent &&
                StringComparer.Ordinal.Equals(
                    retryIntent.InitialIntentRecordIdentity,
                    observation.Intent.RecordIdentity) &&
                StringComparer.Ordinal.Equals(
                    retryIntent.InitialFailureRecordIdentity,
                    failure.RecordIdentity) &&
                StringComparer.Ordinal.Equals(
                    retryIntent.RecordIdentity,
                    evidenceRecordIdentity) &&
                observation.RetryFailure is null &&
                observation.StickyReadback is null &&
                observation.Abandonment is null &&
                observation.Recovery is null,
            _ => false,
        };
    }

    internal static PublicationMarkerAbsenceEvidence
        CreateMarkerAbsenceEvidence(
        PublicationRecoveryObservation observation,
        StickyDiscoveryKind kind,
        StickyPublicationReason reason)
    {
        if (observation is null ||
            !observation.IsLive ||
            observation.CandidateObjectIdentity is not { } candidate ||
            kind != StickyDiscoveryKind.Absent ||
            reason != StickyPublicationReason.None)
        {
            throw new ArgumentException(
                "An exact complete marker-absence observation is required.",
                nameof(observation));
        }

        var preimage = Encoding.UTF8.GetBytes(
            "publication-marker-absence-v1\n" + candidate + "\n" +
            observation.InventoryDigest + "\n" +
            observation.StoredPublication!.ReviewedHeadSha + "\n" +
            observation.StoredPublication.ScopeSha256 + "\n" +
            observation.StoredPublication.BodySha256);
        try
        {
            var identity = Convert.ToHexString(
                    SHA256.HashData(preimage))
                .ToLowerInvariant();
            return new PublicationMarkerAbsenceEvidence(
                CapabilityIssuer,
                candidate,
                observation.InventoryDigest,
                identity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    internal static bool TryConsumeMarkerAbsenceEvidence(
        PublicationRecoveryObservation observation,
        PublicationMarkerAbsenceEvidence evidence) =>
        observation is not null &&
        evidence is not null &&
        observation.IsLive &&
        observation.CandidateObjectIdentity is { } candidate &&
        evidence.TryConsume(
            CapabilityIssuer,
            candidate,
            observation.InventoryDigest);

    internal static string StaleCleanupClassificationIdentity(
        PublicationRecoveryObservation observation,
        AbandonmentV1 abandonment,
        PublicationMarkerAbsenceEvidence freshAbsence)
    {
        if (observation is null ||
            abandonment is null ||
            freshAbsence is null ||
            observation.CandidateObjectIdentity is not { } candidate)
        {
            throw new ArgumentException(
                "Exact stale-cleanup evidence is required.");
        }

        var preimage = Encoding.UTF8.GetBytes(
            "publication-stale-cleanup-v1\n" + candidate + "\n" +
            observation.InventoryDigest + "\n" +
            abandonment.RecordIdentity + "\n" +
            freshAbsence.EvidenceIdentity);
        try
        {
            return Convert.ToHexString(SHA256.HashData(preimage))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    internal static string HistoricalCleanupClassificationIdentity(
        PublicationRecoveryObservation observation,
        RetainedStateOpaqueRecord record)
    {
        if (observation is null ||
            record is null ||
            !observation.IsLive ||
            !observation.CurrentAcceptanceMatchesReviewedExecution ||
            observation.Candidate is not null ||
            !observation.HistoricalRecords.Any(item =>
                item.Metadata == record.Metadata &&
                item.Header == record.Header &&
                StringComparer.Ordinal.Equals(
                    item.InventoryDigest,
                    observation.InventoryDigest)))
        {
            throw new ArgumentException(
                "An exact authenticated historical record is required.",
                nameof(record));
        }

        var preimage = Encoding.UTF8.GetBytes(
            "publication-historical-cleanup-v1\n" +
            observation.InventoryDigest + "\n" +
            record.Header.ObjectIdentity + "\n" +
            record.Header.PredecessorIdentity);
        try
        {
            return Convert.ToHexString(SHA256.HashData(preimage))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    internal static string CompletedAnchorCleanupClassificationIdentity(
        PublicationRecoveryObservation observation,
        RetainedStatePublicationRecoveryAnchorEvidence anchor)
    {
        if (observation is null ||
            anchor is null ||
            !observation.IsLive ||
            !observation.CurrentAcceptanceMatchesReviewedExecution ||
            observation.Candidate is not null ||
            !observation.CompletedAnchors.Contains(anchor))
        {
            throw new ArgumentException(
                "An exact authenticated completed anchor is required.",
                nameof(anchor));
        }

        var preimage = Encoding.UTF8.GetBytes(
            "publication-completed-anchor-cleanup-v1\n" +
            observation.InventoryDigest + "\n" +
            anchor.AnchorHeader.ObjectIdentity + "\n" +
            anchor.TargetObjectIdentity);
        try
        {
            return Convert.ToHexString(SHA256.HashData(preimage))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    internal static string SupersededCleanupClassificationIdentity(
        RetainedStatePublicationRecoveryInventory inventory,
        RetainedStateOpaqueRecord record,
        string acceptedCandidateIdentity)
    {
        if (inventory is null ||
            record is null ||
            !LineageValidation.IsSha256(acceptedCandidateIdentity) ||
            !StringComparer.Ordinal.Equals(
                inventory.CurrentAcceptanceCandidateObjectIdentity,
                acceptedCandidateIdentity) ||
            inventory.Candidate is not null ||
            !inventory.Records.Any(item =>
                item.Metadata == record.Metadata &&
                item.Header == record.Header &&
                StringComparer.Ordinal.Equals(
                    item.InventoryDigest,
                    inventory.InventoryDigest)))
        {
            throw new ArgumentException(
                "An exact superseded recovery record is required.",
                nameof(record));
        }

        return CleanupIdentity(
            "publication-superseded-record-cleanup-v1",
            inventory.InventoryDigest,
            acceptedCandidateIdentity,
            record.Header.ObjectIdentity);
    }

    internal static string SupersededAnchorCleanupClassificationIdentity(
        RetainedStatePublicationRecoveryInventory inventory,
        RetainedStatePublicationRecoveryAnchorEvidence anchor,
        string acceptedCandidateIdentity)
    {
        if (inventory is null ||
            anchor is null ||
            !LineageValidation.IsSha256(acceptedCandidateIdentity) ||
            !StringComparer.Ordinal.Equals(
                inventory.CurrentAcceptanceCandidateObjectIdentity,
                acceptedCandidateIdentity) ||
            inventory.Candidate is not null ||
            !inventory.Anchors.Contains(anchor))
        {
            throw new ArgumentException(
                "An exact superseded completed anchor is required.",
                nameof(anchor));
        }

        return CleanupIdentity(
            "publication-superseded-anchor-cleanup-v1",
            inventory.InventoryDigest,
            acceptedCandidateIdentity,
            anchor.AnchorHeader.ObjectIdentity);
    }

    private static string CleanupIdentity(
        string domain,
        string inventoryDigest,
        string candidateIdentity,
        string targetIdentity)
    {
        var preimage = Encoding.UTF8.GetBytes(
            domain + "\n" + inventoryDigest + "\n" +
            candidateIdentity + "\n" + targetIdentity);
        try
        {
            return Convert.ToHexString(SHA256.HashData(preimage))
                .ToLowerInvariant();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(preimage);
        }
    }

    private static RetainedStateTransactionResult<
        PublicationRecoveryObservation> Fail() =>
        RetainedStateTransactionResult<PublicationRecoveryObservation>.Fail(
            RetainedStateTransactionCodes.Conflict);

    private static bool ValidAttemptGraph(
        ParsedRecordSet records,
        bool allowTerminalCleanupSubgraph = false)
    {
        if (records.Failure is { } initialFailure &&
            (records.Intent is null ||
                !StringComparer.Ordinal.Equals(
                    initialFailure.AttemptIntentRecordIdentity,
                    records.Intent.RecordIdentity)) &&
            !allowTerminalCleanupSubgraph)
        {
            return false;
        }

        if (records.RetryIntent is { } retryIntent &&
            (records.Intent is null ||
                records.Failure is not
                {
                    Outcome: BoundedGitHubPublisherOutcome.KnownNotWritten,
                } retryInitialFailure ||
                !StringComparer.Ordinal.Equals(
                    retryIntent.InitialIntentRecordIdentity,
                    records.Intent.RecordIdentity) ||
                !StringComparer.Ordinal.Equals(
                    retryIntent.InitialFailureRecordIdentity,
                    retryInitialFailure.RecordIdentity)) &&
            !allowTerminalCleanupSubgraph)
        {
            return false;
        }

        if (records.RetryFailure is { } retryFailure &&
            (records.RetryIntent is null ||
                !StringComparer.Ordinal.Equals(
                    retryFailure.RetryIntentRecordIdentity,
                    records.RetryIntent.RecordIdentity)) &&
            !allowTerminalCleanupSubgraph)
        {
            return false;
        }

        var activeIntentIdentity = records.RetryIntent?.RecordIdentity ??
            records.Intent?.RecordIdentity;
        var readback = records.StickyReadback ??
            records.Recovery?.StickyReadback;
        return (allowTerminalCleanupSubgraph ||
                records.RetryIntent is null || records.Failure is not null) &&
            (allowTerminalCleanupSubgraph ||
                records.RetryFailure is null ||
                records.RetryIntent is not null) &&
            (readback is null ||
                allowTerminalCleanupSubgraph &&
                    activeIntentIdentity is null ||
                activeIntentIdentity is not null &&
                StringComparer.Ordinal.Equals(
                    readback.AttemptIntentRecordIdentity,
                    activeIntentIdentity));
    }

    private static bool TryAddAcceptedCleanupRecords(
        ParsedRecordSet acceptedSet,
        ImmutableArray<RetainedStateOpaqueRecord>.Builder acceptedRecords,
        ImmutableArray<RetainedStateOpaqueRecord>.Builder cleanupOrder)
    {
        var initialCount = cleanupOrder.Count;
        return TryAddAcceptedCleanupRecord(
                acceptedSet.RetryFailureMetadata,
                acceptedRecords,
                cleanupOrder) &&
            TryAddAcceptedCleanupRecord(
                acceptedSet.RetryIntentMetadata,
                acceptedRecords,
                cleanupOrder) &&
            TryAddAcceptedCleanupRecord(
                acceptedSet.FailureMetadata,
                acceptedRecords,
                cleanupOrder) &&
            TryAddAcceptedCleanupRecord(
                acceptedSet.IntentMetadata,
                acceptedRecords,
                cleanupOrder) &&
            TryAddAcceptedCleanupRecord(
                acceptedSet.StickyReadbackMetadata,
                acceptedRecords,
                cleanupOrder) &&
            TryAddAcceptedCleanupRecord(
                acceptedSet.RecoveryMetadata,
                acceptedRecords,
                cleanupOrder) &&
            cleanupOrder.Count - initialCount == acceptedRecords.Count;
    }

    private static bool TryAddAcceptedCleanupRecord(
        OpaqueStoreObjectMetadata? metadata,
        ImmutableArray<RetainedStateOpaqueRecord>.Builder acceptedRecords,
        ImmutableArray<RetainedStateOpaqueRecord>.Builder cleanupOrder)
    {
        if (metadata is null)
        {
            return true;
        }

        var record = acceptedRecords.SingleOrDefault(candidate =>
            candidate.Metadata == metadata);
        if (record is null)
        {
            return false;
        }

        cleanupOrder.Add(record);
        return true;
    }

    private static bool TryDecode(
        AuthorizedAcceptedStateRestoreContext context,
        RetainedStateOpaqueRecord record,
        out DecodedRecord? decoded)
    {
        decoded = null;
        if (!RestrictedStateService.TryCopyRetainedStateOpaquePayload(
                context,
                record,
                out var payload))
        {
            return false;
        }

        try
        {
            object? value = null;
            var matches = 0;
            if (record.ObjectClass == StateObjectClass.PublicationIntent)
            {
                if (PublicationIntentV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var intent) &&
                    intent is not null)
                {
                    value = intent;
                    matches++;
                }
                if (PublicationRetryIntentV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var retryIntent) &&
                    retryIntent is not null)
                {
                    value = retryIntent;
                    matches++;
                }
                if (StickyReadbackRecordV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var readback) &&
                    readback is not null)
                {
                    value = readback;
                    matches++;
                }
                if (RecoveryRecordV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var recovery,
                        out _,
                        out _) &&
                    recovery is not null)
                {
                    value = recovery;
                    matches++;
                }
            }
            else if (record.ObjectClass ==
                StateObjectClass.PublicationFailure)
            {
                if (PublicationFailureV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var failure) &&
                    failure is not null)
                {
                    value = failure;
                    matches++;
                }
                if (PublicationRetryFailureV1Codec.TryDecode(
                        payload.AsSpan(),
                        out var retryFailure) &&
                    retryFailure is not null)
                {
                    value = retryFailure;
                    matches++;
                }
            }
            else if (record.ObjectClass == StateObjectClass.Abandonment &&
                AbandonmentV1Codec.TryDecode(
                    payload.AsSpan(),
                    out var abandonment) &&
                abandonment is not null)
            {
                value = abandonment;
                matches++;
            }

            if (matches != 1 || value is null)
            {
                return false;
            }

            decoded = new DecodedRecord(value, record.Metadata);
            return true;
        }
        finally
        {
            var array = ImmutableCollectionsMarshal.AsArray(payload);
            if (array is not null)
            {
                CryptographicOperations.ZeroMemory(array);
            }
        }
    }

    private sealed record DecodedRecord(
        object Value,
        OpaqueStoreObjectMetadata Metadata)
    {
        internal PublicationRecoveryPublicationV1 Publication => Value switch
        {
            PublicationIntentV1 value => value.Publication,
            PublicationRetryIntentV1 value => value.Publication,
            StickyReadbackRecordV1 value => value.Publication,
            PublicationFailureV1 value => value.Publication,
            PublicationRetryFailureV1 value => value.Publication,
            AbandonmentV1 value => value.Publication,
            RecoveryRecordV1 value => value.Publication,
            _ => throw new InvalidOperationException(),
        };

        internal bool Matches(ValidatedPublicationPayloadV1 value) =>
            StringComparer.Ordinal.Equals(
                Publication.ReviewedHeadSha,
                value.ReviewedHeadSha) &&
            StringComparer.Ordinal.Equals(
                Publication.ScopeSha256,
                value.ScopeSha256) &&
            StringComparer.Ordinal.Equals(
                Publication.BodySha256,
                value.BodySha256);
    }

    private sealed class ParsedRecordSet
    {
        internal PublicationIntentV1? Intent { get; private set; }
        internal OpaqueStoreObjectMetadata? IntentMetadata
        {
            get;
            private set;
        }
        internal PublicationRetryIntentV1? RetryIntent { get; private set; }
        internal OpaqueStoreObjectMetadata? RetryIntentMetadata
        {
            get;
            private set;
        }
        internal StickyReadbackRecordV1? StickyReadback { get; private set; }
        internal OpaqueStoreObjectMetadata? StickyReadbackMetadata
        {
            get;
            private set;
        }
        internal PublicationFailureV1? Failure { get; private set; }
        internal OpaqueStoreObjectMetadata? FailureMetadata
        {
            get;
            private set;
        }
        internal PublicationRetryFailureV1? RetryFailure { get; private set; }
        internal OpaqueStoreObjectMetadata? RetryFailureMetadata
        {
            get;
            private set;
        }
        internal AbandonmentV1? Abandonment { get; private set; }
        internal RecoveryRecordV1? Recovery { get; private set; }
        internal OpaqueStoreObjectMetadata? RecoveryMetadata
        {
            get;
            private set;
        }
        internal int Count { get; private set; }

        internal bool TryAdd(DecodedRecord decoded)
        {
            var added = false;
            switch (decoded.Value)
            {
                case PublicationIntentV1 value when Intent is null:
                    Intent = value;
                    IntentMetadata = decoded.Metadata;
                    added = true;
                    break;
                case PublicationRetryIntentV1 value when RetryIntent is null:
                    RetryIntent = value;
                    RetryIntentMetadata = decoded.Metadata;
                    added = true;
                    break;
                case StickyReadbackRecordV1 value
                    when StickyReadback is null:
                    StickyReadback = value;
                    StickyReadbackMetadata = decoded.Metadata;
                    added = true;
                    break;
                case PublicationFailureV1 value when Failure is null:
                    Failure = value;
                    FailureMetadata = decoded.Metadata;
                    added = true;
                    break;
                case PublicationRetryFailureV1 value
                    when RetryFailure is null:
                    RetryFailure = value;
                    RetryFailureMetadata = decoded.Metadata;
                    added = true;
                    break;
                case AbandonmentV1 value when Abandonment is null:
                    Abandonment = value;
                    added = true;
                    break;
                case RecoveryRecordV1 value when Recovery is null:
                    Recovery = value;
                    RecoveryMetadata = decoded.Metadata;
                    added = true;
                    break;
            }

            if (added)
            {
                Count++;
            }
            return added;
        }
    }
}
