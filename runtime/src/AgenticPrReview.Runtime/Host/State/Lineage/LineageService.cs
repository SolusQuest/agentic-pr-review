using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed class LineageService
{
    private readonly IRestrictedStateStore store;
    private readonly ScopedStateInventory inventory;
    private readonly ScopedStateUploadProtocol uploads;
    private readonly TimeProvider timeProvider;

    internal LineageService(
        IRestrictedStateStore store,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        inventory = new ScopedStateInventory(store);
        uploads = new ScopedStateUploadProtocol(store);
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<LineageResolveResult> ResolveAsync(
        LocatorContext context,
        LineageResolveRequest? request,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            request is null ||
            !LineageValidation.IsValid(request.BaseScope) ||
            !LineageValidation.IsValid(request.Reviewed) ||
            !LineageValidation.IsText(
                request.ProducingRunIdentity,
                LineageFormat.MaximumRunIdentityBytes) ||
            request.ProducingRunAttempt < 0 ||
            !request.Access.TryGetRepositoryId(
                request.Access,
                out var repositoryId) ||
            !StringComparer.Ordinal.Equals(
                repositoryId,
                request.BaseScope.RepositoryId))
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (!LineageValidation.IsTime(now) ||
            request.RequiredLogicalExpiresAtUnixSeconds < now ||
            !TryRequiredPlatformExpiry(
                now,
                request.RequiredLogicalExpiresAtUnixSeconds,
                out var requiredPlatformExpiry) ||
            !context.CoversDependentExpiry(
                request.Access,
                requiredPlatformExpiry) ||
            !LineageBaseScopeCodec.TryDigest(
                request.BaseScope,
                out var baseScopeDigest) ||
            !TryGetCurrentKeyId(
                context,
                request.Access,
                out var currentKeyId))
        {
            return LineageResolveResult.Fail(LineageCodes.RetentionFailed);
        }

        ObservationResult? observed = null;
        try
        {
            observed = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!observed.Succeeded)
            {
                return LineageResolveResult.Fail(observed.Code);
            }

            if (observed.Selection!.IsAbsent)
            {
                if (observed.Snapshot!.PhysicalCount != 0)
                {
                    return LineageResolveResult.Fail(LineageCodes.Conflict);
                }

                return await InitializeAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        now,
                        requiredPlatformExpiry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var selection = observed.Selection.Selection!;
            var staleCleanup = FindAuthorizedStaleCleanup(
                observed.Snapshot!,
                selection);
            if (staleCleanup is null)
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

            if (!staleCleanup.Value.IsEmpty)
            {
                if (!await DeleteAndVerifyAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        staleCleanup.Value,
                        cancellationToken)
                    .ConfigureAwait(false))
                {
                    return LineageResolveResult.Fail(
                        LineageCodes.CleanupFailed);
                }

                ClearObservation(observed);
                observed = await ReadObservationAsync(
                        context,
                        request.Access,
                        request.BaseScope,
                        baseScopeDigest,
                        currentKeyId,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!observed.Succeeded || observed.Selection!.IsAbsent)
                {
                    return LineageResolveResult.Fail(observed.Code);
                }

                selection = observed.Selection.Selection!;
            }

            if (!ValidateActiveState(observed.Snapshot!, selection.Head))
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

            var pending = SelectPendingIntent(
                observed.Snapshot!,
                selection.Head);
            if (!pending.Succeeded)
            {
                return LineageResolveResult.Fail(pending.Code);
            }

            if (pending.Intent is not null)
            {
                return await ResumeTransitionAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        now,
                        requiredPlatformExpiry,
                        observed,
                        pending.Intent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.Reset is not null)
            {
                if (!request.Reset.Allows(
                        request.Access,
                        request.BaseScope,
                        request.ProducingRunIdentity,
                        request.ProducingRunAttempt,
                        selection.Head.Header.ObjectIdentity))
                {
                    return LineageResolveResult.Fail(
                        LineageCodes.AccessDenied);
                }

                return await StartTransitionAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        now,
                        requiredPlatformExpiry,
                        observed,
                        LineageTransitionIntentKind.Reset,
                        request.Reset.RequestIdentity,
                        expiryBoundaryUnixSeconds: null,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var expiry = SelectExpiredAcceptance(
                observed.Snapshot!,
                selection.Head,
                now);
            if (!expiry.Succeeded)
            {
                return LineageResolveResult.Fail(expiry.Code);
            }

            if (expiry.Acceptance is not null)
            {
                return await StartTransitionAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        now,
                        requiredPlatformExpiry,
                        observed,
                        LineageTransitionIntentKind.Expiry,
                        expiry.Acceptance.Header.ObjectIdentity,
                        expiry.Acceptance.Header.LogicalExpiresAtUnixSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RefreshOrFinalizeAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    now,
                    requiredPlatformExpiry,
                    observed,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LineageResolveResult.Fail(LineageCodes.Unavailable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            return LineageResolveResult.Fail(LineageCodes.Unavailable);
        }
        finally
        {
            ClearObservation(observed);
        }
    }

    private async Task<LineageResolveResult> InitializeAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        long now,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        if (!LineageCryptography.TryDeriveInitialEpoch(
                context,
                request.Access,
                baseScopeDigest,
                request.Reviewed,
                out var epoch) ||
            !LineageCryptography.TryDeriveSessionId(
                context,
                request.Access,
                baseScopeDigest,
                epoch,
                out var sessionId))
        {
            return LineageResolveResult.Fail(LineageCodes.KeyUnavailable);
        }

        var head = new LineageHeadV1(
            LineageTransitionKind.Initial,
            Ordinal: 0,
            request.Reviewed,
            PreviousEpoch: null,
            PreviousHeadIdentity: null,
            TransitionEvidenceIdentity: null,
            ExpiryBoundaryUnixSeconds: null,
            PhysicalPredecessors: [],
            Superseded: [],
            CompletedCleanup: []);
        var written = await WriteHeadAsync(
                context,
                request,
                baseScopeDigest,
                epoch,
                sessionId,
                head,
                now,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!written.Succeeded)
        {
            return LineageResolveResult.Fail(written.Code);
        }

        return await ConvergeExpectedHeadAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                written.Header!.ObjectIdentity,
                requiredPlatformExpiry,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<LineageResolveResult> RefreshOrFinalizeAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        long now,
        long requiredPlatformExpiry,
        ObservationResult observed,
        CancellationToken cancellationToken)
    {
        var selection = observed.Selection!.Selection!;
        var head = selection.Head;
        if (StringComparer.Ordinal.Equals(
                head.Header.KeyId,
                currentKeyId) &&
            head.Header.RequiredPlatformExpiresAtUnixSeconds >=
                requiredPlatformExpiry &&
            head.Metadata.ExpiresAtUnixSeconds >= requiredPlatformExpiry)
        {
            return await ConvergeExpectedHeadAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    head.Header.ObjectIdentity,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!await EstablishHeadroomAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                observed,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
        }

        var written = await WriteHeadAsync(
                context,
                request,
                baseScopeDigest,
                head.Header.Epoch,
                head.Header.SessionId,
                head.Head,
                now,
                head.Header.LogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!written.Succeeded ||
            !StringComparer.Ordinal.Equals(
                written.Header!.ObjectIdentity,
                head.Header.ObjectIdentity))
        {
            return LineageResolveResult.Fail(written.Code);
        }

        return await ConvergeExpectedHeadAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                head.Header.ObjectIdentity,
                requiredPlatformExpiry,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<LineageResolveResult> StartTransitionAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        long now,
        long requiredPlatformExpiry,
        ObservationResult observed,
        LineageTransitionIntentKind kind,
        string evidenceIdentity,
        long? expiryBoundaryUnixSeconds,
        CancellationToken cancellationToken)
    {
        var active = observed.Selection!.Selection!.Head;
        var objectClass = LineageTransitionIntentCodec.ObjectClass(kind);
        var family = observed.Snapshot!.Authenticated
            .Where(item => item.Header.ObjectClass == objectClass)
            .ToImmutableArray();
        if (family.Length > LineageFormat.MaximumPhysicalPerClass)
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        if (family.Length >= LineageFormat.MaximumPhysicalPerClass)
        {
            var safe = family
                .Where(item => active.Head.Superseded
                    .Concat(active.Head.CompletedCleanup)
                    .Any(evidence => LineageHeadCodec.Matches(
                        evidence,
                        item.Metadata)))
                .Select(item => item.Metadata)
                .ToImmutableArray();
            if (safe.IsEmpty ||
                !await DeleteAndVerifyAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        safe,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return LineageResolveResult.Fail(
                    LineageCodes.CleanupFailed);
            }

            ClearObservation(observed);
            var reread = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!reread.Succeeded || reread.Selection!.IsAbsent)
            {
                ClearObservation(reread);
                return LineageResolveResult.Fail(reread.Code);
            }

            observed.Snapshot = reread.Snapshot;
            observed.Selection = reread.Selection;
            active = reread.Selection.Selection!.Head;
            family = reread.Snapshot!.Authenticated
                .Where(item => item.Header.ObjectClass == objectClass)
                .ToImmutableArray();
            if (family.Length > 7)
            {
                return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
            }
        }

        var targets = observed.Snapshot!.Authenticated
            .Where(item =>
                item.Header.ObjectClass != StateObjectClass.LineageHead &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    active.Header.Epoch))
            .Select(item => LineageHeadCodec.Evidence(item.Metadata))
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.ObjectId, StringComparer.Ordinal)
            .ToImmutableArray();
        var intent = new LineageTransitionIntentV1(
            kind,
            active.Header.ObjectIdentity,
            active.Header.Epoch,
            evidenceIdentity,
            expiryBoundaryUnixSeconds,
            LineageCryptography.InventoryDigest(targets),
            targets);
        var written = await WriteIntentAsync(
                context,
                request,
                baseScopeDigest,
                active,
                intent,
                now,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false);
        if (!written.Succeeded)
        {
            return LineageResolveResult.Fail(written.Code);
        }

        ClearObservation(observed);
        var converged = await ReadObservationAsync(
                context,
                request.Access,
                request.BaseScope,
                baseScopeDigest,
                currentKeyId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!converged.Succeeded || converged.Selection!.IsAbsent)
        {
            ClearObservation(converged);
            return LineageResolveResult.Fail(converged.Code);
        }

        var selectedIntent = SelectPendingIntent(
            converged.Snapshot!,
            converged.Selection.Selection!.Head);
        if (!selectedIntent.Succeeded ||
            selectedIntent.Intent is null ||
            !StringComparer.Ordinal.Equals(
                selectedIntent.Intent.Object.Header.ObjectIdentity,
                written.Header!.ObjectIdentity))
        {
            ClearObservation(converged);
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        try
        {
            return await ResumeTransitionAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    now,
                    requiredPlatformExpiry,
                    converged,
                    selectedIntent.Intent,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            ClearObservation(converged);
        }
    }

    private async Task<LineageResolveResult> ResumeTransitionAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        long now,
        long requiredPlatformExpiry,
        ObservationResult observed,
        SelectedIntent pending,
        CancellationToken cancellationToken)
    {
        var active = observed.Selection!.Selection!.Head;
        if (pending.Intent.Kind == LineageTransitionIntentKind.Reset &&
            (request.Reset is null ||
                !request.Reset.Allows(
                    request.Access,
                    request.BaseScope,
                    request.ProducingRunIdentity,
                    request.ProducingRunAttempt,
                    active.Header.ObjectIdentity) ||
                !StringComparer.Ordinal.Equals(
                    request.Reset.RequestIdentity,
                    pending.Intent.TransitionEvidenceIdentity)))
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        // An already-selected intent may validly be the eighth transition
        // record. No second intent append is pending. The successor head slot
        // is still mandatory before the first destructive delete.
        if (!await EstablishHeadroomAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                observed,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
        }

        var targets = ResolveTargets(observed.Snapshot!, pending.Intent.Targets);
        if (targets is null)
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        foreach (var target in targets.Value)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        ClearObservation(observed);
        var afterDelete = await ReadObservationAsync(
                context,
                request.Access,
                request.BaseScope,
                baseScopeDigest,
                currentKeyId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!afterDelete.Succeeded || afterDelete.Selection!.IsAbsent)
        {
            ClearObservation(afterDelete);
            return LineageResolveResult.Fail(afterDelete.Code);
        }

        try
        {
            if (pending.Intent.Targets.Any(target =>
                    ContainsEvidence(afterDelete.Snapshot!, target)) ||
                afterDelete.Snapshot!.Unknown.Any(item =>
                    pending.Intent.Targets.Any(target =>
                        LineageHeadCodec.Matches(target, item.Metadata))))
            {
                return LineageResolveResult.Fail(
                    LineageCodes.CleanupFailed);
            }

            active = afterDelete.Selection.Selection!.Head;
            if (!StringComparer.Ordinal.Equals(
                    active.Header.ObjectIdentity,
                    pending.Intent.PriorHeadIdentity) ||
                active.Head.Ordinal == ulong.MaxValue)
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

            var epoch = string.Empty;
            var derived = pending.Intent.Kind switch
            {
                LineageTransitionIntentKind.Reset =>
                    LineageCryptography.TryDeriveResetEpoch(
                        context,
                        request.Access,
                        baseScopeDigest,
                        active.Header.Epoch,
                        active.Header.ObjectIdentity,
                        pending.Intent.TransitionEvidenceIdentity,
                        request.Reviewed,
                        out epoch),
                LineageTransitionIntentKind.Expiry =>
                    LineageCryptography.TryDeriveExpiryEpoch(
                        context,
                        request.Access,
                        baseScopeDigest,
                        active.Header.Epoch,
                        active.Header.ObjectIdentity,
                        pending.Intent.TransitionEvidenceIdentity,
                        pending.Intent.ExpiryBoundaryUnixSeconds!.Value,
                        request.Reviewed,
                        out epoch),
                _ => false,
            };
            if (!derived ||
                !LineageCryptography.TryDeriveSessionId(
                    context,
                    request.Access,
                    baseScopeDigest,
                    epoch,
                    out var sessionId))
            {
                return LineageResolveResult.Fail(
                    LineageCodes.KeyUnavailable);
            }

            var successor = new LineageHeadV1(
                pending.Intent.Kind == LineageTransitionIntentKind.Reset
                    ? LineageTransitionKind.Reset
                    : LineageTransitionKind.Expiry,
                checked(active.Head.Ordinal + 1),
                request.Reviewed,
                active.Header.Epoch,
                active.Header.ObjectIdentity,
                pending.Object.Header.ObjectIdentity,
                pending.Intent.ExpiryBoundaryUnixSeconds,
                [LineageHeadCodec.Evidence(active.Metadata)],
                [LineageHeadCodec.Evidence(pending.Object.Metadata)],
                pending.Intent.Targets);
            var written = await WriteHeadAsync(
                    context,
                    request,
                    baseScopeDigest,
                    epoch,
                    sessionId,
                    successor,
                    now,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!written.Succeeded)
            {
                return LineageResolveResult.Fail(written.Code);
            }

            var result = await ConvergeExpectedHeadAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    written.Header!.ObjectIdentity,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                result.Context?.Dispose();
                return result;
            }

            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(pending.Object.Metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return result;
        }
        finally
        {
            ClearObservation(afterDelete);
        }
    }

    private async Task<bool> EstablishHeadroomAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        ObservationResult observed,
        CancellationToken cancellationToken)
    {
        var selection = observed.Selection!.Selection!;
        if (selection.PhysicalCount <= 7)
        {
            return true;
        }

        if (selection.SafeToDelete.IsEmpty)
        {
            return false;
        }

        foreach (var target in selection.SafeToDelete)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        ClearObservation(observed);
        var reread = await ReadObservationAsync(
                context,
                request.Access,
                request.BaseScope,
                baseScopeDigest,
                currentKeyId,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!reread.Succeeded ||
            reread.Selection!.IsAbsent ||
            reread.Selection.Selection!.PhysicalCount > 7)
        {
            ClearObservation(reread);
            return false;
        }

        observed.Snapshot = reread.Snapshot;
        observed.Selection = reread.Selection;
        return true;
    }

    private async Task<LineageResolveResult> ConvergeExpectedHeadAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        string expectedIdentity,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var observed = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    cancellationToken)
                .ConfigureAwait(false);
            try
            {
                if (!observed.Succeeded ||
                    observed.Selection!.IsAbsent ||
                    !StringComparer.Ordinal.Equals(
                        observed.Selection.Selection!.Head.Header.ObjectIdentity,
                        expectedIdentity) ||
                    observed.Selection.Selection.Head.Metadata
                        .ExpiresAtUnixSeconds < requiredPlatformExpiry)
                {
                    return LineageResolveResult.Fail(
                        observed.Succeeded
                            ? LineageCodes.Conflict
                            : observed.Code);
                }

                var selection = observed.Selection.Selection;
                if (selection.SafeToDelete.IsEmpty)
                {
                    return await FinalizeAsync(request, selection.Head)
                        .ConfigureAwait(false);
                }

                if (!await DeleteAndVerifyAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        selection.SafeToDelete,
                        CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    return LineageResolveResult.Fail(
                        LineageCodes.CleanupFailed);
                }
            }
            finally
            {
                ClearObservation(observed);
            }
        }

        return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
    }

    private async Task<LineageResolveResult> FinalizeAsync(
        LineageResolveRequest request,
        LineageHeadCandidate head)
    {
        var readBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(head.Metadata),
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!readBack.Succeeded || readBack.Metadata != head.Metadata)
        {
            return LineageResolveResult.Fail(LineageCodes.Unavailable);
        }

        return LineageResolveResult.Success(new SelectedLineageContext(
            request.Access,
            request.BaseScope.RepositoryId,
            new SelectedLineageSnapshot(
                head.Header.BaseScopeDigest,
                head.Header.Epoch,
                head.Header.SessionId,
                head.Header.ObjectIdentity,
                head.Head.Transition)));
    }

    private async Task<WrittenObjectResult> WriteHeadAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string epoch,
        string sessionId,
        LineageHeadV1 head,
        long now,
        long logicalExpiry,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        byte[] payload = [];
        byte[] canonicalScope = [];
        if (!LineageHeadCodec.TryEncode(head, out payload) ||
            !LineageBaseScopeCodec.TryEncode(
                request.BaseScope,
                out canonicalScope) ||
            !context.TryDeriveOpaqueName(
                request.Access,
                StateObjectClasses.ToWireName(StateObjectClass.LineageHead),
                canonicalScope,
                out var name) ||
            name is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(canonicalScope);
            return WrittenObjectResult.Fail(LineageCodes.Invalid);
        }

        try
        {
            var draft = new StateControlHeaderDraft(
                baseScopeDigest,
                epoch,
                sessionId,
                StateObjectClass.LineageHead,
                head.PreviousHeadIdentity,
                SuccessorIdentity: null,
                request.ProducingRunIdentity,
                request.ProducingRunAttempt,
                now,
                logicalExpiry,
                requiredPlatformExpiry);
            if (!StateControlEnvelopeV1Codec.TryEncrypt(
                    context,
                    request.Access,
                    name,
                    draft,
                    payload,
                    out var envelope,
                    out var header,
                    out var code) ||
                header is null)
            {
                CryptographicOperations.ZeroMemory(envelope);
                return WrittenObjectResult.Fail(code);
            }

            try
            {
                var uploaded = await uploads.UploadAndReadBackAsync(
                        name,
                        envelope,
                        requiredPlatformExpiry,
                        cancellationToken)
                    .ConfigureAwait(false);
                return uploaded.Succeeded
                    ? WrittenObjectResult.Success(
                        uploaded.Metadata!,
                        header)
                    : WrittenObjectResult.Fail(uploaded.Code);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    private async Task<WrittenObjectResult> WriteIntentAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        LineageHeadCandidate active,
        LineageTransitionIntentV1 intent,
        long now,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        var objectClass = LineageTransitionIntentCodec.ObjectClass(intent.Kind);
        byte[] payload = [];
        byte[] canonicalScope = [];
        if (!LineageTransitionIntentCodec.TryEncode(intent, out payload) ||
            !LineageBaseScopeCodec.TryEncode(
                request.BaseScope,
                out canonicalScope) ||
            !context.TryDeriveOpaqueName(
                request.Access,
                StateObjectClasses.ToWireName(objectClass),
                canonicalScope,
                out var name) ||
            name is null)
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(canonicalScope);
            return WrittenObjectResult.Fail(LineageCodes.Invalid);
        }

        try
        {
            var draft = new StateControlHeaderDraft(
                baseScopeDigest,
                active.Header.Epoch,
                active.Header.SessionId,
                objectClass,
                active.Header.ObjectIdentity,
                SuccessorIdentity: null,
                request.ProducingRunIdentity,
                request.ProducingRunAttempt,
                now,
                active.Header.LogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!StateControlEnvelopeV1Codec.TryEncrypt(
                    context,
                    request.Access,
                    name,
                    draft,
                    payload,
                    out var envelope,
                    out var header,
                    out var code) ||
                header is null)
            {
                CryptographicOperations.ZeroMemory(envelope);
                return WrittenObjectResult.Fail(code);
            }

            try
            {
                var uploaded = await uploads.UploadAndReadBackAsync(
                        name,
                        envelope,
                        requiredPlatformExpiry,
                        cancellationToken)
                    .ConfigureAwait(false);
                return uploaded.Succeeded
                    ? WrittenObjectResult.Success(
                        uploaded.Metadata!,
                        header)
                    : WrittenObjectResult.Fail(uploaded.Code);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    private async Task<ObservationResult> ReadObservationAsync(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        LineageBaseScope scope,
        string baseScopeDigest,
        string currentKeyId,
        CancellationToken cancellationToken)
    {
        var read = await inventory.ReadAsync(
                context,
                access,
                scope,
                baseScopeDigest,
                cancellationToken)
            .ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return ObservationResult.Fail(read.Code);
        }

        var snapshot = read.Snapshot!;
        var lineageName = snapshot.Names[StateObjectClass.LineageHead];
        var heads = ImmutableArray.CreateBuilder<LineageHeadCandidate>();
        foreach (var item in snapshot.Authenticated.Where(item =>
            item.Header.ObjectClass == StateObjectClass.LineageHead))
        {
            if (!LineageHeadCodec.TryDecode(item.Payload, out var head) ||
                head is null)
            {
                ScopedStateInventory.Clear(snapshot);
                return ObservationResult.Fail(
                    LineageCodes.AuthenticationFailed);
            }

            heads.Add(new LineageHeadCandidate(
                item.Metadata,
                item.Header,
                head));
        }

        var unknownHeads = snapshot.Unknown.Where(item =>
                item.Metadata.Reference.Name == lineageName)
            .ToImmutableArray();
        var authenticatedHeads = heads.ToImmutable();
        var selection = LineageHeadSelector.Select(
            authenticatedHeads,
            unknownHeads,
            authenticatedHeads.Length + unknownHeads.Length,
            currentKeyId);
        if (!selection.Succeeded)
        {
            ScopedStateInventory.Clear(snapshot);
            return ObservationResult.Fail(selection.Code);
        }

        return ObservationResult.Success(snapshot, selection);
    }

    private static PendingIntentResult SelectPendingIntent(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active)
    {
        var parsed = ImmutableArray.CreateBuilder<SelectedIntent>();
        foreach (var item in snapshot.Authenticated.Where(item =>
            item.Header.ObjectClass is StateObjectClass.Reset or
                StateObjectClass.ExpiryTransition &&
            StringComparer.Ordinal.Equals(
                item.Header.Epoch,
                active.Header.Epoch)))
        {
            if (!LineageTransitionIntentCodec.TryDecode(
                    item.Header.ObjectClass,
                    item.Payload,
                    out var intent) ||
                intent is null)
            {
                return PendingIntentResult.Fail(
                    LineageCodes.AuthenticationFailed);
            }

            if (StringComparer.Ordinal.Equals(
                    intent.PriorHeadIdentity,
                    active.Header.ObjectIdentity) &&
                !StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    intent.PriorHeadIdentity))
            {
                return PendingIntentResult.Fail(LineageCodes.Conflict);
            }

            if (StringComparer.Ordinal.Equals(
                    intent.PriorHeadIdentity,
                    active.Header.ObjectIdentity) &&
                StringComparer.Ordinal.Equals(
                    intent.PriorEpoch,
                    active.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    active.Header.ObjectIdentity))
            {
                parsed.Add(new SelectedIntent(item, intent));
            }
        }

        if (parsed.Count == 0)
        {
            return PendingIntentResult.None();
        }

        var identities = parsed
            .Select(value => value.Object.Header.ObjectIdentity)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        if (identities.Length != 1)
        {
            return PendingIntentResult.Fail(LineageCodes.Conflict);
        }

        return PendingIntentResult.Success(parsed
            .OrderByDescending(value =>
                value.Object.Header.RequiredPlatformExpiresAtUnixSeconds)
            .ThenBy(value =>
                value.Object.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First());
    }

    private static ExpiredAcceptanceResult SelectExpiredAcceptance(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active,
        long now)
    {
        var activeAcceptances = snapshot.Authenticated
            .Where(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    active.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    active.Header.SessionId))
            .ToImmutableArray();
        if (activeAcceptances.Any(item =>
                !StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    active.Header.ObjectIdentity)))
        {
            return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
        }

        var expired = activeAcceptances
            .Where(item => item.Header.LogicalExpiresAtUnixSeconds <= now)
            .ToImmutableArray();
        var identities = expired
            .Select(item => item.Header.ObjectIdentity)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        return identities.Length switch
        {
            0 => ExpiredAcceptanceResult.None(),
            1 => ExpiredAcceptanceResult.Success(expired
                .OrderBy(item =>
                    item.Metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .First()),
            _ => ExpiredAcceptanceResult.Fail(LineageCodes.Conflict),
        };
    }

    private static bool ValidateActiveState(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active) =>
        snapshot.Authenticated
            .Where(item => item.Header.ObjectClass !=
                StateObjectClass.LineageHead)
            .All(item =>
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    active.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    active.Header.SessionId) &&
                (item.Header.ObjectClass != StateObjectClass.Acceptance ||
                    StringComparer.Ordinal.Equals(
                        item.Header.PredecessorIdentity,
                        active.Header.ObjectIdentity)));

    private static ImmutableArray<OpaqueStoreObjectMetadata>?
        FindAuthorizedStaleCleanup(
            ScopedStateInventorySnapshot snapshot,
            LineageHeadSelection selection)
    {
        var evidence = selection.Head.Head.Superseded
            .Concat(selection.Head.Head.CompletedCleanup)
            .ToImmutableArray();
        var cleanup = ImmutableArray.CreateBuilder<OpaqueStoreObjectMetadata>();
        foreach (var item in snapshot.Authenticated.Where(item =>
            item.Header.ObjectClass != StateObjectClass.LineageHead &&
            !StringComparer.Ordinal.Equals(
                item.Header.Epoch,
                selection.Head.Header.Epoch)))
        {
            if (!evidence.Any(value =>
                    LineageHeadCodec.Matches(value, item.Metadata)))
            {
                return null;
            }

            cleanup.Add(item.Metadata);
        }

        foreach (var item in snapshot.Unknown.Where(item =>
            item.Metadata.Reference.Name !=
                snapshot.Names[StateObjectClass.LineageHead]))
        {
            if (!evidence.Any(value =>
                    LineageHeadCodec.Matches(value, item.Metadata)))
            {
                return null;
            }

            cleanup.Add(item.Metadata);
        }

        return cleanup.ToImmutable();
    }

    private async Task<bool> DeleteAndVerifyAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        ImmutableArray<OpaqueStoreObjectMetadata> targets,
        CancellationToken cancellationToken)
    {
        foreach (var target in targets)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        var reread = await inventory.ReadAsync(
                context,
                request.Access,
                request.BaseScope,
                baseScopeDigest,
                cancellationToken)
            .ConfigureAwait(false);
        try
        {
            return reread.Succeeded &&
                targets.All(target =>
                    !reread.Snapshot!.Authenticated.Any(item =>
                        item.Metadata == target) &&
                    !reread.Snapshot.Unknown.Any(item =>
                        item.Metadata == target));
        }
        finally
        {
            ScopedStateInventory.Clear(reread.Snapshot);
        }
    }

    private static ImmutableArray<OpaqueStoreObjectMetadata>? ResolveTargets(
        ScopedStateInventorySnapshot snapshot,
        ImmutableArray<LineageArtifactEvidence> evidence)
    {
        var targets = ImmutableArray.CreateBuilder<OpaqueStoreObjectMetadata>();
        foreach (var item in evidence)
        {
            var matches = snapshot.Authenticated
                .Select(value => value.Metadata)
                .Concat(snapshot.Unknown.Select(value => value.Metadata))
                .Where(metadata => LineageHeadCodec.Matches(item, metadata))
                .ToImmutableArray();
            if (matches.Length > 1)
            {
                return null;
            }

            if (matches.Length == 1)
            {
                targets.Add(matches[0]);
            }
        }

        return targets.ToImmutable();
    }

    private static bool ContainsEvidence(
        ScopedStateInventorySnapshot snapshot,
        LineageArtifactEvidence evidence) =>
        snapshot.Authenticated.Any(item =>
            LineageHeadCodec.Matches(evidence, item.Metadata));

    private static bool TryGetCurrentKeyId(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        out string keyId)
    {
        Span<byte> key = stackalloc byte[LineageFormat.KeyBytes];
        try
        {
            return context.TryCopyCurrentStateKey(access, key, out keyId);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private static bool TryRequiredPlatformExpiry(
        long now,
        long logicalExpiry,
        out long required)
    {
        required = 0;
        try
        {
            required = Math.Max(
                checked(now + StateRetentionRequirements
                    .ScopedPlatformRequestSeconds),
                checked(logicalExpiry + 24 * 60 * 60));
            return LineageValidation.IsTime(required);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static void ClearObservation(ObservationResult? observed)
    {
        if (observed?.Snapshot is not null)
        {
            ScopedStateInventory.Clear(observed.Snapshot);
            observed.Snapshot = null;
            observed.Selection = null;
        }
    }

    private sealed record WrittenObjectResult(
        string Code,
        OpaqueStoreObjectMetadata? Metadata,
        StateControlHeaderV1? Header)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Metadata is not null &&
            Header is not null;

        internal static WrittenObjectResult Success(
            OpaqueStoreObjectMetadata metadata,
            StateControlHeaderV1 header) =>
            new(LineageCodes.Ready, metadata, header);

        internal static WrittenObjectResult Fail(string code) =>
            new(code, null, null);
    }

    private sealed class ObservationResult
    {
        private ObservationResult(
            string code,
            ScopedStateInventorySnapshot? snapshot,
            LineageSelectionResult? selection)
        {
            Code = code;
            Snapshot = snapshot;
            Selection = selection;
        }

        internal string Code { get; }
        internal ScopedStateInventorySnapshot? Snapshot { get; set; }
        internal LineageSelectionResult? Selection { get; set; }
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Snapshot is not null &&
            Selection is not null;

        internal static ObservationResult Success(
            ScopedStateInventorySnapshot snapshot,
            LineageSelectionResult selection) =>
            new(LineageCodes.Ready, snapshot, selection);

        internal static ObservationResult Fail(string code) =>
            new(code, null, null);
    }

    private sealed record SelectedIntent(
        AuthenticatedStateObject Object,
        LineageTransitionIntentV1 Intent);

    private sealed record PendingIntentResult(
        string Code,
        SelectedIntent? Intent)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready);

        internal static PendingIntentResult None() =>
            new(LineageCodes.Ready, null);

        internal static PendingIntentResult Success(SelectedIntent intent) =>
            new(LineageCodes.Ready, intent);

        internal static PendingIntentResult Fail(string code) =>
            new(code, null);
    }

    private sealed record ExpiredAcceptanceResult(
        string Code,
        AuthenticatedStateObject? Acceptance)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready);

        internal static ExpiredAcceptanceResult None() =>
            new(LineageCodes.Ready, null);

        internal static ExpiredAcceptanceResult Success(
            AuthenticatedStateObject acceptance) =>
            new(LineageCodes.Ready, acceptance);

        internal static ExpiredAcceptanceResult Fail(string code) =>
            new(code, null);
    }
}
