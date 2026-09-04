using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed record RetainedStatePersistenceResult(
    string Code,
    OpaqueStoreObjectMetadata? Metadata,
    StateControlHeaderV1? Header,
    byte[]? Payload,
    string? InventoryDigest,
    bool MayHaveCommitted)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(
            Code,
            RetainedStateTransactionCodes.Persisted) &&
        Metadata is not null &&
        Header is not null &&
        Payload is not null &&
        LineageValidation.IsSha256(InventoryDigest);

    internal static RetainedStatePersistenceResult Success(
        OpaqueStoreObjectMetadata metadata,
        StateControlHeaderV1 header,
        byte[] payload,
        string inventoryDigest) =>
        new(
            RetainedStateTransactionCodes.Persisted,
            metadata,
            header,
            payload,
            inventoryDigest,
            MayHaveCommitted: true);

    internal static RetainedStatePersistenceResult Fail(
        string code,
        bool mayHaveCommitted = false) =>
        new(code, null, null, null, null, mayHaveCommitted);
}

internal sealed class RetainedStatePersistence
{
    private const int ReconciliationAttempts = 3;
    private readonly IRestrictedStateStore store;
    private readonly ScopedStateInventory inventory;
    private readonly TimeProvider timeProvider;
    private readonly IStateReconciliationDiagnosticSink? diagnosticSink;

    internal RetainedStatePersistence(
        object issuer,
        IRestrictedStateStore store,
        TimeProvider? timeProvider = null,
        IStateReconciliationDiagnosticSink? diagnosticSink = null)
    {
        RetainedStateCapabilityIssuer.Require(issuer);
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        inventory = new ScopedStateInventory(store);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.diagnosticSink = diagnosticSink;
    }

    internal static bool TryPrepareEnvelope(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        LineageBaseScope scope,
        SelectedLineageSnapshot selected,
        StateObjectClass objectClass,
        string? predecessorIdentity,
        string? successorIdentity,
        string producingRunIdentity,
        long producingRunAttempt,
        long createdAtUnixSeconds,
        long logicalExpiresAtUnixSeconds,
        long requiredPlatformExpiresAtUnixSeconds,
        ReadOnlySpan<byte> payload,
        out OpaqueStoreName? name,
        out byte[] envelope,
        out StateControlHeaderV1? header,
        out string code)
    {
        name = null;
        envelope = [];
        header = null;
        code = RetainedStateTransactionCodes.Invalid;
        if (context is null ||
            access is null ||
            !LineageValidation.IsValid(scope) ||
            selected is null ||
            !StringComparer.Ordinal.Equals(
                selected.BaseScopeDigest,
                Digest(scope)) ||
            objectClass is StateObjectClass.LocatorRoot or
                StateObjectClass.LineageHead or
                StateObjectClass.Reset or
                StateObjectClass.ExpiryTransition ||
            !context.CoversDependentExpiry(
                access,
                requiredPlatformExpiresAtUnixSeconds) ||
            !LineageBaseScopeCodec.TryEncode(scope, out var canonicalScope))
        {
            code = RetainedStateTransactionCodes.AccessDenied;
            return false;
        }

        try
        {
            if (!context.TryDeriveOpaqueName(
                    access,
                    StateObjectClasses.ToWireName(objectClass),
                    canonicalScope,
                    out name) ||
                name is null)
            {
                code = RetainedStateTransactionCodes.AccessDenied;
                return false;
            }

            var draft = new StateControlHeaderDraft(
                selected.BaseScopeDigest,
                selected.Epoch,
                selected.SessionId,
                objectClass,
                predecessorIdentity,
                successorIdentity,
                producingRunIdentity,
                producingRunAttempt,
                createdAtUnixSeconds,
                logicalExpiresAtUnixSeconds,
                requiredPlatformExpiresAtUnixSeconds);
            if (!StateControlEnvelopeV1Codec.TryEncrypt(
                    context,
                    access,
                    name,
                    draft,
                    payload,
                    out envelope,
                    out header,
                    out var lineageCode) ||
                header is null)
            {
                code = MapLineageCode(lineageCode);
                return false;
            }

            code = RetainedStateTransactionCodes.Ready;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    internal async Task<RetainedStatePersistenceResult>
        UploadAndReconcileAsync(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        LineageBaseScope scope,
        string baseScopeDigest,
        OpaqueStoreName name,
        ReadOnlyMemory<byte> immutableEnvelope,
        StateControlHeaderV1 expectedHeader,
        ReadOnlyMemory<byte> expectedPayload,
        string expectedProducingRunIdentity,
        long expectedProducingRunAttempt,
        long requiredPlatformExpiresAtUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            access is null ||
            !LineageValidation.IsValid(scope) ||
            !LineageValidation.IsSha256(baseScopeDigest) ||
            !StringComparer.Ordinal.Equals(
                baseScopeDigest,
                Digest(scope)) ||
            !OpaqueStoreValidation.IsValid(name) ||
            immutableEnvelope.Length is < 1 or >
                LineageFormat.MaximumEnvelopeBytes ||
            expectedHeader is null ||
            !LineageValidation.IsValid(expectedHeader) ||
            expectedPayload.Length > LineageFormat.MaximumReaderPayloadBytes ||
            !LineageValidation.IsText(
                expectedProducingRunIdentity,
                LineageFormat.MaximumRunIdentityBytes) ||
            expectedProducingRunAttempt < 0 ||
            expectedHeader.ProducingRunIdentity !=
                expectedProducingRunIdentity ||
            expectedHeader.ProducingRunAttempt !=
                expectedProducingRunAttempt ||
            expectedHeader.RequiredPlatformExpiresAtUnixSeconds !=
                requiredPlatformExpiresAtUnixSeconds ||
            !context.CoversDependentExpiry(
                access,
                requiredPlatformExpiresAtUnixSeconds))
        {
            return RetainedStatePersistenceResult.Fail(
                RetainedStateTransactionCodes.Invalid);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStatePersistenceResult.Fail(
                RetainedStateTransactionCodes.Cancelled);
        }

        var encryptedDigest = new OpaqueStoreEncryptedObjectDigest(
            OpaqueStoreHash.Sha256(immutableEnvelope.Span));
        OpaqueStoreUploadResult? upload = null;
        try
        {
            upload = await store.UploadImmutableAsync(
                    new OpaqueStoreUploadRequest(
                        name,
                        new OpaqueStoreCorrelationId(
                            LineageCryptography.CorrelationId(
                                immutableEnvelope.Span)),
                        immutableEnvelope,
                        encryptedDigest,
                        requiredPlatformExpiresAtUnixSeconds),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispatch has started. Reconcile with uncancelled reads because
            // cancellation no longer proves that the mutation was not applied.
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                CryptographicException)
        {
            // The store boundary may throw after committing. The inventory is
            // the source of truth for all post-dispatch outcomes.
        }

        if (upload?.MutationState == OpaqueStoreMutationState.NotCommitted)
        {
            var failed = CreateVisibilityWindow(
                expectedHeader.ObjectClass,
                StateReconciliationOutcome.NotCommitted,
                StateReconciliationExactReadBack.NotApplicable);
            failed.ReportFailure(StateReconciliationTerminal.NotCommitted);
            return RetainedStatePersistenceResult.Fail(
                MapStoreFailure(upload.Failure));
        }

        var returned = ExactReturnedMetadata(
                upload?.Metadata,
                name,
                encryptedDigest,
                immutableEnvelope.Length,
                expectedProducingRunIdentity,
                expectedProducingRunAttempt)
            ? upload!.Metadata
            : null;
        var exactReadBack = returned is null
            ? StateReconciliationExactReadBack.NotAvailable
            : StateReconciliationExactReadBack.Failed;
        if (returned is not null)
        {
            for (var attempt = 0;
                attempt < ReconciliationAttempts;
                attempt++)
            {
                var readBack = await store.ReadBackExactAsync(
                        new OpaqueStoreReadBackRequest(returned),
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (readBack.Succeeded && readBack.Metadata == returned)
                {
                    exactReadBack = StateReconciliationExactReadBack.Matched;
                    break;
                }
            }
        }

        var visibility = CreateVisibilityWindow(
            expectedHeader.ObjectClass,
            upload?.MutationState == OpaqueStoreMutationState.Committed
                ? StateReconciliationOutcome.Committed
                : StateReconciliationOutcome.OutcomeUnknown,
            exactReadBack);

        for (var attempt = 0;
            attempt < ReconciliationAttempts;
            attempt++)
        {
            var read = await inventory.ReadAsync(
                    context,
                    access,
                    scope,
                    baseScopeDigest,
                    CancellationToken.None)
                .ConfigureAwait(false);
            visibility.RecordObservation();
            if (!read.Succeeded || read.Snapshot is null)
            {
                if (StringComparer.Ordinal.Equals(
                        read.Code,
                        LineageCodes.Unavailable))
                {
                    _ = await visibility.WaitForNextObservationAsync()
                        .ConfigureAwait(false);
                    continue;
                }

                visibility.ReportFailure(Terminal(MapLineageCode(read.Code)));
                return RetainedStatePersistenceResult.Fail(
                    MapLineageCode(read.Code),
                    mayHaveCommitted: true);
            }

            try
            {
                var snapshot = read.Snapshot;
                if (snapshot.Unknown.Any(item =>
                    item.Metadata.Reference.Name == name))
                {
                    visibility.ReportFailure(
                        StateReconciliationTerminal.Unavailable);
                    return RetainedStatePersistenceResult.Fail(
                        RetainedStateTransactionCodes.OutcomeUnknown,
                        mayHaveCommitted: true);
                }

                var all = snapshot.Authenticated
                    .Concat(snapshot.UnderRetained)
                    .Where(item =>
                        item.Metadata.Reference.Name == name &&
                        item.Header.ObjectClass ==
                            expectedHeader.ObjectClass &&
                        item.Header == expectedHeader &&
                        item.Payload.AsSpan().SequenceEqual(
                            expectedPayload.Span) &&
                        item.Metadata.EncryptedObjectDigest == encryptedDigest &&
                        item.Metadata.Size == immutableEnvelope.Length &&
                        StringComparer.Ordinal.Equals(
                            item.Metadata.ProducingRun.Identity,
                            expectedProducingRunIdentity) &&
                        item.Metadata.ProducingRun.Attempt ==
                            expectedProducingRunAttempt &&
                        (returned is null || item.Metadata == returned))
                    .ToArray();
                if (all.Length > 1)
                {
                    visibility.ReportFailure(
                        StateReconciliationTerminal.Conflict);
                    return RetainedStatePersistenceResult.Fail(
                        RetainedStateTransactionCodes.Conflict,
                        mayHaveCommitted: true);
                }

                if (all.Length == 0)
                {
                    _ = await visibility.WaitForNextObservationAsync()
                        .ConfigureAwait(false);
                    continue;
                }

                var match = all[0];
                if (match.Metadata.ExpiresAtUnixSeconds <
                        requiredPlatformExpiresAtUnixSeconds ||
                    snapshot.UnderRetained.Contains(match))
                {
                    var removed = await DeleteExactAndVerifyAbsentAsync(
                            match.Metadata,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    visibility.ReportFailure(removed ==
                            RetainedStateTransactionCodes.Ready
                        ? StateReconciliationTerminal.RetentionFailed
                        : StateReconciliationTerminal.CleanupFailed);
                    return RetainedStatePersistenceResult.Fail(
                        StringComparer.Ordinal.Equals(
                            removed,
                            RetainedStateTransactionCodes.Ready)
                            ? RetainedStateTransactionCodes.RetentionFailed
                            : RetainedStateTransactionCodes.CleanupDebt,
                        mayHaveCommitted: true);
                }

                var digest = LineageCryptography.InventoryDigest(
                    snapshot.Authenticated
                        .Concat(snapshot.UnderRetained)
                        .Select(item =>
                            LineageHeadCodec.Evidence(item.Metadata))
                        .Concat(snapshot.Unknown.Select(item =>
                            LineageHeadCodec.Evidence(item.Metadata)))
                        .ToImmutableArray());
                return RetainedStatePersistenceResult.Success(
                    match.Metadata,
                    match.Header,
                    match.Payload.ToArray(),
                    digest);
            }
            finally
            {
                ScopedStateInventory.Clear(read.Snapshot);
            }
        }

        visibility.ReportFailure(StateReconciliationTerminal.TargetAbsent);
        return RetainedStatePersistenceResult.Fail(
            RetainedStateTransactionCodes.OutcomeUnknown,
            mayHaveCommitted: true);
    }

    internal async Task<RetainedStatePersistenceResult>
        ReconcileExistingAsync(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        LineageBaseScope scope,
        string baseScopeDigest,
        OpaqueStoreName name,
        ReadOnlyMemory<byte> immutableEnvelope,
        StateControlHeaderV1 expectedHeader,
        ReadOnlyMemory<byte> expectedPayload,
        string expectedProducingRunIdentity,
        long expectedProducingRunAttempt,
        long requiredPlatformExpiresAtUnixSeconds)
    {
        if (context is null ||
            access is null ||
            !LineageValidation.IsValid(scope) ||
            !LineageValidation.IsSha256(baseScopeDigest) ||
            !OpaqueStoreValidation.IsValid(name) ||
            immutableEnvelope.Length is < 1 or >
                LineageFormat.MaximumEnvelopeBytes ||
            expectedHeader is null ||
            !LineageValidation.IsValid(expectedHeader) ||
            expectedPayload.Length >
                LineageFormat.MaximumReaderPayloadBytes ||
            expectedHeader.ProducingRunIdentity !=
                expectedProducingRunIdentity ||
            expectedHeader.ProducingRunAttempt !=
                expectedProducingRunAttempt ||
            expectedHeader.RequiredPlatformExpiresAtUnixSeconds !=
                requiredPlatformExpiresAtUnixSeconds ||
            !context.CoversDependentExpiry(
                access,
                requiredPlatformExpiresAtUnixSeconds))
        {
            return RetainedStatePersistenceResult.Fail(
                RetainedStateTransactionCodes.Invalid);
        }

        var encryptedDigest = new OpaqueStoreEncryptedObjectDigest(
            OpaqueStoreHash.Sha256(immutableEnvelope.Span));
        var visibility = CreateVisibilityWindow(
            expectedHeader.ObjectClass,
            StateReconciliationOutcome.ReconcileOnly,
            StateReconciliationExactReadBack.NotApplicable);
        for (var attempt = 0;
            attempt < ReconciliationAttempts;
            attempt++)
        {
            var read = await inventory.ReadAsync(
                    context,
                    access,
                    scope,
                    baseScopeDigest,
                    CancellationToken.None)
                .ConfigureAwait(false);
            visibility.RecordObservation();
            if (!read.Succeeded || read.Snapshot is null)
            {
                if (StringComparer.Ordinal.Equals(
                        read.Code,
                        LineageCodes.Unavailable))
                {
                    _ = await visibility.WaitForNextObservationAsync()
                        .ConfigureAwait(false);
                    continue;
                }

                visibility.ReportFailure(Terminal(MapLineageCode(read.Code)));
                return RetainedStatePersistenceResult.Fail(
                    MapLineageCode(read.Code),
                    mayHaveCommitted: true);
            }

            try
            {
                var snapshot = read.Snapshot;
                if (snapshot.Unknown.Any(item =>
                    item.Metadata.Reference.Name == name))
                {
                    visibility.ReportFailure(
                        StateReconciliationTerminal.Unavailable);
                    return RetainedStatePersistenceResult.Fail(
                        RetainedStateTransactionCodes.OutcomeUnknown,
                        mayHaveCommitted: true);
                }

                var all = snapshot.Authenticated
                    .Concat(snapshot.UnderRetained)
                    .Where(item =>
                        item.Metadata.Reference.Name == name &&
                        item.Header.ObjectClass ==
                            expectedHeader.ObjectClass &&
                        item.Header == expectedHeader &&
                        item.Payload.AsSpan().SequenceEqual(
                            expectedPayload.Span) &&
                        item.Metadata.EncryptedObjectDigest ==
                            encryptedDigest &&
                        item.Metadata.Size == immutableEnvelope.Length &&
                        StringComparer.Ordinal.Equals(
                            item.Metadata.ProducingRun.Identity,
                            expectedProducingRunIdentity) &&
                        item.Metadata.ProducingRun.Attempt ==
                            expectedProducingRunAttempt)
                    .ToArray();
                if (all.Length > 1)
                {
                    visibility.ReportFailure(
                        StateReconciliationTerminal.Conflict);
                    return RetainedStatePersistenceResult.Fail(
                        RetainedStateTransactionCodes.Conflict,
                        mayHaveCommitted: true);
                }

                if (all.Length == 0)
                {
                    _ = await visibility.WaitForNextObservationAsync()
                        .ConfigureAwait(false);
                    continue;
                }

                var match = all[0];
                if (match.Metadata.ExpiresAtUnixSeconds <
                        requiredPlatformExpiresAtUnixSeconds ||
                    snapshot.UnderRetained.Contains(match))
                {
                    var removed = await DeleteExactAndVerifyAbsentAsync(
                            match.Metadata,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    visibility.ReportFailure(removed ==
                            RetainedStateTransactionCodes.Ready
                        ? StateReconciliationTerminal.RetentionFailed
                        : StateReconciliationTerminal.CleanupFailed);
                    return RetainedStatePersistenceResult.Fail(
                        StringComparer.Ordinal.Equals(
                            removed,
                            RetainedStateTransactionCodes.Ready)
                            ? RetainedStateTransactionCodes.RetentionFailed
                            : RetainedStateTransactionCodes.CleanupDebt,
                        mayHaveCommitted: true);
                }

                var digest = LineageCryptography.InventoryDigest(
                    snapshot.Authenticated
                        .Concat(snapshot.UnderRetained)
                        .Select(item =>
                            LineageHeadCodec.Evidence(item.Metadata))
                        .Concat(snapshot.Unknown.Select(item =>
                            LineageHeadCodec.Evidence(item.Metadata)))
                        .ToImmutableArray());
                return RetainedStatePersistenceResult.Success(
                    match.Metadata,
                    match.Header,
                    match.Payload.ToArray(),
                    digest);
            }
            finally
            {
                ScopedStateInventory.Clear(read.Snapshot);
            }
        }

        visibility.ReportFailure(StateReconciliationTerminal.TargetAbsent);
        return RetainedStatePersistenceResult.Fail(
            RetainedStateTransactionCodes.OutcomeUnknown,
            mayHaveCommitted: true);
    }

    internal async Task<string> DeleteExactAndVerifyAbsentAsync(
        OpaqueStoreObjectMetadata target,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(target))
        {
            return RetainedStateTransactionCodes.Invalid;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return RetainedStateTransactionCodes.Cancelled;
        }

        OpaqueStoreDeleteResult? deleted = null;
        try
        {
            deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Dispatch has started. Reconcile below without cancellation.
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                IOException or
                UnauthorizedAccessException or
                CryptographicException)
        {
            // The delete may have committed before the store threw.
        }

        if (deleted?.MutationState == OpaqueStoreMutationState.NotCommitted)
        {
            return MapStoreFailure(deleted.Failure);
        }

        for (var attempt = 0;
            attempt < ReconciliationAttempts;
            attempt++)
        {
            var listed = await store.ListExactAsync(
                    new OpaqueStoreListRequest(
                        target.Reference.Name,
                        LineageFormat.MaximumPhysicalPerClass),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!listed.Succeeded || !listed.Complete)
            {
                continue;
            }

            if (!listed.Objects.Contains(target.Reference))
            {
                return RetainedStateTransactionCodes.Ready;
            }

            var metadata = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(target.Reference),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (metadata.Succeeded && metadata.Metadata != target)
            {
                return RetainedStateTransactionCodes.Conflict;
            }
        }

        return RetainedStateTransactionCodes.CleanupDebt;
    }

    private static bool ExactReturnedMetadata(
        OpaqueStoreObjectMetadata? metadata,
        OpaqueStoreName name,
        OpaqueStoreEncryptedObjectDigest encryptedDigest,
        int size,
        string producingRunIdentity,
        long producingRunAttempt) =>
        OpaqueStoreValidation.IsValid(metadata) &&
        metadata!.Reference.Name == name &&
        metadata.EncryptedObjectDigest == encryptedDigest &&
        metadata.Size == size &&
        StringComparer.Ordinal.Equals(
            metadata.ProducingRun.Identity,
            producingRunIdentity) &&
        metadata.ProducingRun.Attempt == producingRunAttempt;

    private static string Digest(LineageBaseScope scope) =>
        LineageBaseScopeCodec.TryDigest(scope, out var value)
            ? value
            : string.Empty;

    private PostUploadVisibilityWindow CreateVisibilityWindow(
        StateObjectClass objectClass,
        StateReconciliationOutcome outcome,
        StateReconciliationExactReadBack exactReadBack) =>
        new(
            timeProvider,
            diagnosticSink,
            Owner(objectClass),
            outcome,
            exactReadBack);

    private static StateReconciliationOwner Owner(
        StateObjectClass objectClass) => objectClass switch
    {
        StateObjectClass.LocatorRoot => StateReconciliationOwner.LocatorRoot,
        StateObjectClass.LineageHead => StateReconciliationOwner.LineageHead,
        StateObjectClass.Candidate => StateReconciliationOwner.Candidate,
        StateObjectClass.PublicationIntent =>
            StateReconciliationOwner.PublicationIntent,
        StateObjectClass.Acceptance => StateReconciliationOwner.Acceptance,
        StateObjectClass.PublicationFailure =>
            StateReconciliationOwner.PublicationFailure,
        StateObjectClass.Abandonment => StateReconciliationOwner.Abandonment,
        StateObjectClass.Reset => StateReconciliationOwner.Reset,
        StateObjectClass.ExpiryTransition =>
            StateReconciliationOwner.ExpiryTransition,
        StateObjectClass.Cleanup => StateReconciliationOwner.Cleanup,
        _ => throw new ArgumentOutOfRangeException(nameof(objectClass)),
    };

    private static StateReconciliationTerminal Terminal(string code) =>
        code switch
        {
            RetainedStateTransactionCodes.Conflict =>
                StateReconciliationTerminal.Conflict,
            RetainedStateTransactionCodes.KeyUnavailable =>
                StateReconciliationTerminal.KeyUnavailable,
            RetainedStateTransactionCodes.RetentionFailed =>
                StateReconciliationTerminal.RetentionFailed,
            RetainedStateTransactionCodes.CleanupDebt =>
                StateReconciliationTerminal.CleanupFailed,
            RetainedStateTransactionCodes.Cancelled =>
                StateReconciliationTerminal.Cancelled,
            RetainedStateTransactionCodes.Invalid =>
                StateReconciliationTerminal.Invalid,
            _ => StateReconciliationTerminal.Unavailable,
        };

    private static string MapStoreFailure(OpaqueStoreFailure failure) =>
        failure switch
        {
            OpaqueStoreFailure.Conflict or OpaqueStoreFailure.Duplicate =>
                RetainedStateTransactionCodes.Conflict,
            OpaqueStoreFailure.Cleanup =>
                RetainedStateTransactionCodes.CleanupDebt,
            OpaqueStoreFailure.Invalid =>
                RetainedStateTransactionCodes.Invalid,
            _ => RetainedStateTransactionCodes.OutcomeUnknown,
        };

    private static string MapLineageCode(string code) =>
        StringComparer.Ordinal.Equals(code, LineageCodes.AccessDenied)
            ? RetainedStateTransactionCodes.AccessDenied
            : StringComparer.Ordinal.Equals(code, LineageCodes.KeyUnavailable)
                ? RetainedStateTransactionCodes.KeyUnavailable
                : StringComparer.Ordinal.Equals(code, LineageCodes.Conflict)
                    ? RetainedStateTransactionCodes.Conflict
                    : StringComparer.Ordinal.Equals(
                        code,
                        LineageCodes.RetentionFailed)
                        ? RetainedStateTransactionCodes.RetentionFailed
                        : StringComparer.Ordinal.Equals(
                            code,
                            LineageCodes.CleanupFailed)
                            ? RetainedStateTransactionCodes.CleanupDebt
                            : RetainedStateTransactionCodes.OutcomeUnknown;
}
