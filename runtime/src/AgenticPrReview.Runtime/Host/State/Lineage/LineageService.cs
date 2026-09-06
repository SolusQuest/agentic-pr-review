using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed class LineageService
{
    private readonly IRestrictedStateStore store;
    private readonly ScopedStateInventory inventory;
    private readonly ScopedStateUploadProtocol uploads;
    private readonly TimeProvider timeProvider;
    private readonly IStateReconciliationDiagnosticSink? diagnosticSink;

    internal LineageService(
        IRestrictedStateStore store,
        TimeProvider? timeProvider = null,
        IStateReconciliationDiagnosticSink? diagnosticSink = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        inventory = new ScopedStateInventory(store);
        uploads = new ScopedStateUploadProtocol(store);
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.diagnosticSink = diagnosticSink;
    }

    internal async Task<LineageReadOnlyObservationResult> ObserveReadOnlyAsync(
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
            return LineageReadOnlyObservationResult.Fail(
                LineageCodes.AccessDenied);
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
            return LineageReadOnlyObservationResult.Fail(
                LineageCodes.RetentionFailed);
        }

        ScopedStateInventorySnapshot? snapshot = null;
        try
        {
            var read = await inventory.ReadAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return LineageReadOnlyObservationResult.Fail(read.Code);
            }

            snapshot = read.Snapshot!;
            var lineageName = snapshot.Names[StateObjectClass.LineageHead];
            var heads = ImmutableArray.CreateBuilder<LineageHeadCandidate>();
            foreach (var item in snapshot.Authenticated.Where(item =>
                item.Header.ObjectClass == StateObjectClass.LineageHead))
            {
                if (!LineageHeadCodec.TryDecode(item.Payload, out var head) ||
                    head is null ||
                    !ValidateDerivedHead(
                        context,
                        request.Access,
                        baseScopeDigest,
                        item.Header,
                        head))
                {
                    return LineageReadOnlyObservationResult.Fail(
                        LineageCodes.AuthenticationFailed);
                }

                heads.Add(new LineageHeadCandidate(
                    item.Metadata,
                    item.Header,
                    head));
            }

            if (snapshot.UnderRetained.Any(item =>
                item.Header.ObjectClass == StateObjectClass.LineageHead))
            {
                return LineageReadOnlyObservationResult.Fail(
                    LineageCodes.RetentionFailed);
            }

            var unknownHeads = snapshot.Unknown.Where(item =>
                    item.Metadata.Reference.Name == lineageName)
                .ToImmutableArray();
            var authenticatedHeads = heads.ToImmutable();
            var selection = LineageHeadSelector.Select(
                authenticatedHeads,
                unknownHeads,
                authenticatedHeads.Length + unknownHeads.Length,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!selection.Succeeded)
            {
                return LineageReadOnlyObservationResult.Fail(selection.Code);
            }

            var evidence = snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Select(item => LineageHeadCodec.Evidence(item.Metadata))
                .Concat(snapshot.Unknown.Select(item =>
                    LineageHeadCodec.Evidence(item.Metadata)))
                .ToImmutableArray();
            var contextResult = new LineageReadOnlyObservationContext(
                snapshot,
                selection,
                baseScopeDigest,
                currentKeyId,
                LineageCryptography.InventoryDigest(evidence),
                requiredPlatformExpiry);
            snapshot = null;
            return LineageReadOnlyObservationResult.Success(contextResult);
        }
        catch (OperationCanceledException)
        {
            return LineageReadOnlyObservationResult.Fail(
                LineageCodes.Unavailable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            return LineageReadOnlyObservationResult.Fail(
                LineageCodes.Unavailable);
        }
        finally
        {
            ScopedStateInventory.Clear(snapshot);
        }
    }

    internal async Task<LineageResolveResult> InitializeAuthorizedAsync(
        LocatorContext context,
        LineageResolveRequest request,
        AcceptedStateSelector.AuthorizedInitialLineageAbsence authority,
        CancellationToken cancellationToken)
    {
        if (authority is null)
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        var read = await ObserveReadOnlyAsync(
                context,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        using var observation = read.Context;
        if (!read.Succeeded ||
            observation is null ||
            !authority.Allows(request.Access, request, observation))
        {
            return LineageResolveResult.Fail(
                read.Succeeded ? LineageCodes.Conflict : read.Code);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (!LineageValidation.IsTime(now) ||
            now > request.RequiredLogicalExpiresAtUnixSeconds)
        {
            return LineageResolveResult.Fail(LineageCodes.RetentionFailed);
        }

        return await InitializeAsync(
                context,
                request,
                observation.BaseScopeDigest,
                observation.CurrentKeyId,
                now,
                observation.RequiredPlatformExpiresAtUnixSeconds,
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal async Task<LineageResolveResult> ExpireAuthorizedAsync(
        LocatorContext context,
        LineageResolveRequest request,
        AcceptedStateSelector.AuthorizedAcceptedStateExpiry authority,
        LineagePendingExpiryIntent? expectedPending,
        CancellationToken cancellationToken)
    {
        if (authority is null)
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        var read = await ObserveReadOnlyAsync(
                context,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        using var observation = read.Context;
        if (!read.Succeeded ||
            observation is null ||
            !authority.Allows(request.Access, request, observation) ||
            observation.Selection.Selection is not { } selection)
        {
            return LineageResolveResult.Fail(
                read.Succeeded ? LineageCodes.Conflict : read.Code);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (!LineageValidation.IsTime(now) ||
            authority.LogicalExpiresAtUnixSeconds > now)
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        var pending = SelectPendingIntent(
            observation.Snapshot!,
            selection.Head,
            observation.CurrentKeyId,
            request.RequiredLogicalExpiresAtUnixSeconds,
            observation.RequiredPlatformExpiresAtUnixSeconds);
        if (!pending.Succeeded)
        {
            return LineageResolveResult.Fail(pending.Code);
        }

        if (expectedPending is not null &&
            (pending.Intent is null ||
                !MatchesPendingExpiry(
                    expectedPending,
                    pending.Intent,
                    selection.Head)))
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        if (pending.Intent is { } selectedIntent &&
            (selectedIntent.Intent.Kind !=
                LineageTransitionIntentKind.Expiry ||
                !StringComparer.Ordinal.Equals(
                    selectedIntent.Intent.PriorHeadIdentity,
                    selection.Head.Header.ObjectIdentity) ||
                !StringComparer.Ordinal.Equals(
                    selectedIntent.Intent.TransitionEvidenceIdentity,
                    authority.TerminalReceiptIdentity) ||
                selectedIntent.Intent.ExpiryBoundaryUnixSeconds !=
                    authority.LogicalExpiresAtUnixSeconds ||
                !StringComparer.Ordinal.Equals(
                    selectedIntent.Intent.InventorySha256,
                    authority.TargetInventoryDigest) ||
                !StringComparer.Ordinal.Equals(
                    LineageCryptography.InventoryDigest(
                        selectedIntent.Intent.Targets),
                    authority.TargetInventoryDigest)))
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        var snapshot = observation.DetachSnapshot();
        if (snapshot is null)
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        var owned = ObservationResult.Success(
            snapshot,
            observation.Selection);
        try
        {
            return pending.Intent is not null
                ? await ResumeTransitionAsync(
                        context,
                        request,
                        observation.BaseScopeDigest,
                        observation.CurrentKeyId,
                        now,
                        observation.RequiredPlatformExpiresAtUnixSeconds,
                        owned,
                        pending.Intent,
                        cancellationToken)
                    .ConfigureAwait(false)
                : await StartTransitionAsync(
                        context,
                        request,
                        observation.BaseScopeDigest,
                        observation.CurrentKeyId,
                        now,
                        observation.RequiredPlatformExpiresAtUnixSeconds,
                        owned,
                        LineageTransitionIntentKind.Expiry,
                        authority.TerminalReceiptIdentity,
                        authority.LogicalExpiresAtUnixSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
        }
        finally
        {
            ClearObservation(owned);
        }
    }

    internal async Task<LineageInterruptedTransitionRecoveryResult>
        RecoverInterruptedTransitionAsync(
            LocatorContext context,
            LineageResolveRequest request,
            LineageReadOnlyObservationContext observation,
            CancellationToken cancellationToken)
    {
        if (context is null || request is null || observation is null ||
            observation.Snapshot is not { } snapshot)
        {
            return LineageInterruptedTransitionRecoveryResult.Fail(
                LineageCodes.AccessDenied);
        }

        if (observation.Selection.IsAbsent)
        {
            return LineageInterruptedTransitionRecoveryResult.None();
        }

        var selection = observation.Selection.Selection;
        if (selection is null)
        {
            return LineageInterruptedTransitionRecoveryResult.Fail(
                LineageCodes.Conflict);
        }

        try
        {
            var pending = SelectPendingIntent(
                snapshot,
                selection.Head,
                observation.CurrentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                observation.RequiredPlatformExpiresAtUnixSeconds);
            if (!pending.Succeeded)
            {
                return LineageInterruptedTransitionRecoveryResult.Fail(
                    pending.Code);
            }

            var staleCleanup = FindAuthorizedStaleCleanup(
                snapshot,
                selection,
                pending.Intent);
            if (staleCleanup is null)
            {
                return LineageInterruptedTransitionRecoveryResult.Fail(
                    LineageCodes.Conflict);
            }

            if (pending.Intent is { } interrupted)
            {
                if (!staleCleanup.Value.IsEmpty)
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.Conflict);
                }

                var remaining = ResolveTargets(
                    snapshot,
                    interrupted.Intent.Targets);
                if (remaining is null)
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.Conflict);
                }

                if (interrupted.Intent.Kind !=
                    LineageTransitionIntentKind.Expiry)
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.AccessDenied);
                }

                if (remaining.Value.Length == interrupted.Intent.Targets.Length)
                {
                    // All accepted-state evidence is still available. Leave the
                    // intent in place so S5 can re-admit the accepted state
                    // and bind this exact intent to typed expiry authority.
                    return LineageInterruptedTransitionRecoveryResult
                        .AwaitingTypedExpiry(
                            PendingExpiry(interrupted, selection.Head));
                }

                var detached = observation.DetachSnapshot();
                if (detached is null)
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.Conflict);
                }

                var owned = ObservationResult.Success(
                    detached,
                    observation.Selection);
                var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
                if (!LineageValidation.IsTime(now))
                {
                    ClearObservation(owned);
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.Unavailable);
                }

                try
                {
                    var resumed = await ResumeTransitionAsync(
                            context,
                            request,
                            observation.BaseScopeDigest,
                            observation.CurrentKeyId,
                            now,
                            observation.RequiredPlatformExpiresAtUnixSeconds,
                            owned,
                            interrupted,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    return resumed.Succeeded && resumed.Context is not null
                        ? LineageInterruptedTransitionRecoveryResult.Success(
                            resumed.Context)
                        : LineageInterruptedTransitionRecoveryResult.Fail(
                            resumed.Code);
                }
                finally
                {
                    ClearObservation(owned);
                }
            }

            // Once the authenticated successor is selected, its supersession
            // and completed-cleanup evidence is the durable authority for
            // finishing the already-committed transition. This path never
            // creates a new reset or expiry intent.
            var transition = selection.Head.Head.Transition;
            var successorCleanupRequired = transition is
                    LineageTransitionKind.Expiry or
                    LineageTransitionKind.Reset &&
                (!staleCleanup.Value.IsEmpty ||
                    !selection.SafeToDelete.IsEmpty);
            if (!successorCleanupRequired)
            {
                return LineageInterruptedTransitionRecoveryResult.None();
            }

            var detachedForCleanup = observation.DetachSnapshot();
            if (detachedForCleanup is null)
            {
                return LineageInterruptedTransitionRecoveryResult.Fail(
                    LineageCodes.Conflict);
            }

            var ownedForCleanup = ObservationResult.Success(
                detachedForCleanup,
                observation.Selection);
            var nowForCleanup = timeProvider.GetUtcNow().ToUnixTimeSeconds();
            if (!LineageValidation.IsTime(nowForCleanup))
            {
                ClearObservation(ownedForCleanup);
                return LineageInterruptedTransitionRecoveryResult.Fail(
                    LineageCodes.Unavailable);
            }

            try
            {
                var converged = await ConvergeExpectedHeadAsync(
                        context,
                        request,
                        observation.BaseScopeDigest,
                        observation.CurrentKeyId,
                        selection.Head.Header,
                        nowForCleanup,
                        observation.RequiredPlatformExpiresAtUnixSeconds,
                        ExpectedHeadConvergenceMode.TransitionSuccessor,
                        written: null,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!converged.Succeeded)
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        converged.Code);
                }

                if (!staleCleanup.Value.IsEmpty &&
                    !await DeleteAndVerifyAsync(
                            context,
                            request,
                            observation.BaseScopeDigest,
                            observation.CurrentKeyId,
                            staleCleanup.Value,
                            CancellationToken.None)
                        .ConfigureAwait(false))
                {
                    return LineageInterruptedTransitionRecoveryResult.Fail(
                        LineageCodes.CleanupFailed);
                }

                var finalized = await FinalizeAsync(request, converged.Head!)
                    .ConfigureAwait(false);
                return finalized.Succeeded && finalized.Context is not null
                    ? LineageInterruptedTransitionRecoveryResult.Success(
                        finalized.Context)
                    : LineageInterruptedTransitionRecoveryResult.Fail(
                        finalized.Code);
            }
            finally
            {
                ClearObservation(ownedForCleanup);
            }
        }
        catch (OperationCanceledException)
        {
            return LineageInterruptedTransitionRecoveryResult.Fail(
                LineageCodes.Unavailable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            return LineageInterruptedTransitionRecoveryResult.Fail(
                LineageCodes.Unavailable);
        }
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
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
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
            var pendingBeforeCleanup = SelectPendingIntent(
                observed.Snapshot!,
                selection.Head,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!pendingBeforeCleanup.Succeeded)
            {
                return LineageResolveResult.Fail(pendingBeforeCleanup.Code);
            }

            var staleCleanup = FindAuthorizedStaleCleanup(
                observed.Snapshot!,
                selection,
                pendingBeforeCleanup.Intent);
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
                        request.RequiredLogicalExpiresAtUnixSeconds,
                        requiredPlatformExpiry,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!observed.Succeeded || observed.Selection!.IsAbsent)
                {
                    return LineageResolveResult.Fail(observed.Code);
                }

                selection = observed.Selection.Selection!;
            }

            var authority = EvaluateOrdinaryAuthority(
                observed.Snapshot!,
                selection.Head,
                request,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                now);
            if (!authority.Succeeded)
            {
                return LineageResolveResult.Fail(authority.Code);
            }

            if (authority.PendingIntent is not null)
            {
                return await ResumeTransitionAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        now,
                        requiredPlatformExpiry,
                        observed,
                        authority.PendingIntent,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (request.Reset is not null)
            {
                if (request.Reset.AllowsCompleted(
                        request.Access,
                        request.BaseScope,
                        baseScopeDigest,
                        request.ProducingRunIdentity,
                        request.ProducingRunAttempt,
                        selection.Head))
                {
                    if (!observed.Snapshot!.UnderRetained.IsEmpty)
                    {
                        return LineageResolveResult.Fail(
                            LineageCodes.RetentionFailed);
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

                if (!request.Reset.Allows(
                        request.Access,
                        request.BaseScope,
                        baseScopeDigest,
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

            if (authority.ExpiredAcceptance is not null)
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
                        authority.ExpiredAcceptance.Header.ObjectIdentity,
                        authority.ExpiredAcceptance.Header
                            .LogicalExpiresAtUnixSeconds,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!observed.Snapshot!.UnderRetained.IsEmpty)
            {
                return LineageResolveResult.Fail(
                    LineageCodes.RetentionFailed);
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

    internal async Task<LineageResolveResult> RefreshSelectedHeadAsync(
        LocatorContext context,
        LineageResolveRequest? request,
        SelectedLineageContext? expected,
        CancellationToken cancellationToken)
    {
        if (context is null ||
            request is null ||
            expected is null ||
            request.Reset is not null ||
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
                request.BaseScope.RepositoryId) ||
            !expected.TryGetSnapshot(request.Access, out var expectedSnapshot) ||
            expectedSnapshot is null)
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
            !StringComparer.Ordinal.Equals(
                baseScopeDigest,
                expectedSnapshot.BaseScopeDigest) ||
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
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!observed.Succeeded || observed.Selection!.IsAbsent)
            {
                return LineageResolveResult.Fail(
                    observed.Succeeded ? LineageCodes.Conflict : observed.Code);
            }

            var selected = observed.Selection.Selection!.Head;
            if (!StringComparer.Ordinal.Equals(
                    selected.Header.BaseScopeDigest,
                    expectedSnapshot.BaseScopeDigest) ||
                !StringComparer.Ordinal.Equals(
                    selected.Header.Epoch,
                    expectedSnapshot.Epoch) ||
                !StringComparer.Ordinal.Equals(
                    selected.Header.SessionId,
                    expectedSnapshot.SessionId) ||
                !StringComparer.Ordinal.Equals(
                    selected.Header.ObjectIdentity,
                    expectedSnapshot.LineageHeadIdentity) ||
                selected.Head.Transition != expectedSnapshot.Transition ||
                selected.Head.Reviewed != request.Reviewed)
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

            var authority = EvaluateOrdinaryAuthority(
                observed.Snapshot!,
                selected,
                request,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry,
                now);
            if (!authority.Succeeded ||
                authority.PendingIntent is not null ||
                authority.ExpiredAcceptance is not null ||
                !observed.Snapshot!.UnderRetained.IsEmpty)
            {
                return LineageResolveResult.Fail(
                    authority.Succeeded
                        ? LineageCodes.Conflict
                        : authority.Code);
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
            PhysicalSuperseded: [],
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
                frozenProducingRunIdentity: null,
                frozenProducingRunAttempt: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (written.Header is null)
        {
            return LineageResolveResult.Fail(written.Code);
        }

        // An equivalent concurrent initializer may select and prune this
        // process's physical upload after it commits but before its exact
        // readback. The intended authenticated identity is not authority by
        // itself; a complete fresh observation below must discover and select
        // that identity before any context is returned.
        return await ConvergeOrdinaryExpectedHeadAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                written.Header!,
                now,
                requiredPlatformExpiry,
                written,
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
            head.Header.LogicalExpiresAtUnixSeconds >=
                request.RequiredLogicalExpiresAtUnixSeconds &&
            head.Header.RequiredPlatformExpiresAtUnixSeconds >=
                requiredPlatformExpiry &&
            head.Metadata.ExpiresAtUnixSeconds >= requiredPlatformExpiry)
        {
            return await ConvergeOrdinaryExpectedHeadAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    head.Header,
                    now,
                    requiredPlatformExpiry,
                    written: null,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        if (!CanEncodeHead(BuildRefreshedHead(selection)) ||
            !await EstablishHeadroomAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                observed,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
        }

        selection = observed.Selection!.Selection!;
        head = selection.Head;
        var refreshedHead = BuildRefreshedHead(selection);
        var written = await WriteHeadAsync(
                context,
                request,
                baseScopeDigest,
                head.Header.Epoch,
                head.Header.SessionId,
                refreshedHead,
                now,
                Math.Max(
                    head.Header.LogicalExpiresAtUnixSeconds,
                    request.RequiredLogicalExpiresAtUnixSeconds),
                requiredPlatformExpiry,
                frozenProducingRunIdentity: null,
                frozenProducingRunAttempt: null,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        if (written.Header is null ||
            !StringComparer.Ordinal.Equals(
                written.Header.ObjectIdentity,
                head.Header.ObjectIdentity))
        {
            return LineageResolveResult.Fail(written.Code);
        }

        return await ConvergeOrdinaryExpectedHeadAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                written.Header,
                now,
                requiredPlatformExpiry,
                written,
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
        var objectClass = LineageTransitionIntentCodec.ObjectClass(kind);
        var active = observed.Selection!.Selection!.Head;
        if (observed.Snapshot!.Unknown.Any(item =>
                item.Metadata.Reference.Name != observed.Snapshot.Names[
                    StateObjectClass.LineageHead]))
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        var targets = observed.Snapshot.Authenticated
            .Concat(observed.Snapshot.UnderRetained)
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
            request.Reviewed,
            LineageCryptography.InventoryDigest(targets),
            targets,
            kind == LineageTransitionIntentKind.Reset
                ? request.ProducingRunIdentity
                : null,
            kind == LineageTransitionIntentKind.Reset
                ? request.ProducingRunAttempt
                : null);
        if (!TryBuildSuccessor(
                context,
                request,
                baseScopeDigest,
                observed.Selection.Selection,
                intent,
                LineageHeadCodec.Evidence(active.Metadata),
                out _,
                out _,
                out _) ||
            !await EstablishHeadroomAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                observed,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false) ||
            !await EstablishTransitionHeadroomAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                observed,
                objectClass,
                requiredPlatformExpiry,
                cancellationToken)
            .ConfigureAwait(false))
        {
            return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
        }

        active = observed.Selection!.Selection!.Head;
        if (!StringComparer.Ordinal.Equals(
                active.Header.ObjectIdentity,
                intent.PriorHeadIdentity) ||
            ResolveTargets(observed.Snapshot!, intent.Targets) is null)
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

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
        if (written.Header is null)
        {
            return LineageResolveResult.Fail(written.Code);
        }

        ClearObservation(observed);
        var convergence = await ConvergeExpectedIntentAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                written,
                requiredPlatformExpiry,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (!convergence.Succeeded)
        {
            return LineageResolveResult.Fail(convergence.Code);
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
                    convergence.Observation!,
                    convergence.Intent!,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            ClearObservation(convergence.Observation);
        }
    }

    private async Task<IntentConvergenceResult> ConvergeExpectedIntentAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        WrittenObjectResult written,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        var window = CreateVisibilityWindow(
            written,
            StateReconciliationOwner.LineageIntent);
        if (!written.CanReconcile || written.Header is null)
        {
            window.ReportFailure(
                written.DiagnosticTerminal ?? Terminal(written.Code));
            return IntentConvergenceResult.Fail(written.Code);
        }

        var expected = written.Header;
        while (true)
        {
            var candidate = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            window.RecordObservation();
            if (!candidate.Succeeded || candidate.Selection!.IsAbsent)
            {
                var code = candidate.Code;
                var absent = candidate.Succeeded && candidate.Selection!.IsAbsent;
                ClearObservation(candidate);
                if ((!candidate.Succeeded &&
                        candidate.DiagnosticTerminal is null &&
                        StringComparer.Ordinal.Equals(
                            code,
                            LineageCodes.Unavailable) || absent) &&
                    await window.WaitForNextObservationAsync()
                        .ConfigureAwait(false))
                {
                    continue;
                }

                window.ReportFailure(absent
                    ? StateReconciliationTerminal.TargetAbsent
                    : candidate.DiagnosticTerminal ?? Terminal(code));
                return IntentConvergenceResult.Fail(absent
                    ? LineageCodes.Unavailable
                    : code);
            }

            var selectedIntent = SelectPendingIntent(
                candidate.Snapshot!,
                candidate.Selection.Selection!.Head,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!selectedIntent.Succeeded)
            {
                ClearObservation(candidate);
                window.ReportFailure(Terminal(selectedIntent.Code));
                return IntentConvergenceResult.Fail(selectedIntent.Code);
            }

            if (selectedIntent.Intent is null)
            {
                ClearObservation(candidate);
                if (await window.WaitForNextObservationAsync()
                    .ConfigureAwait(false))
                {
                    continue;
                }

                window.ReportFailure(
                    StateReconciliationTerminal.TargetAbsent);
                return IntentConvergenceResult.Fail(LineageCodes.Unavailable);
            }

            if (!StringComparer.Ordinal.Equals(
                    selectedIntent.Intent.Object.Header.ObjectIdentity,
                    expected.ObjectIdentity))
            {
                ClearObservation(candidate);
                window.ReportFailure(StateReconciliationTerminal.Conflict);
                return IntentConvergenceResult.Fail(LineageCodes.Conflict);
            }

            if (written.ReturnedMetadata is not null &&
                selectedIntent.Intent.Object.Metadata !=
                    written.ReturnedMetadata)
            {
                ClearObservation(candidate);
                if (await window.WaitForNextObservationAsync()
                    .ConfigureAwait(false))
                {
                    continue;
                }

                window.ReportFailure(
                    StateReconciliationTerminal.TargetAbsent);
                return IntentConvergenceResult.Fail(LineageCodes.Unavailable);
            }

            if (!SatisfiesExpectedPhysical(
                    selectedIntent.Intent.Object,
                    expected,
                    requiredPlatformExpiry))
            {
                ClearObservation(candidate);
                window.ReportFailure(
                    StateReconciliationTerminal.RetentionFailed);
                return IntentConvergenceResult.Fail(LineageCodes.Unavailable);
            }

            return IntentConvergenceResult.Success(
                candidate,
                selectedIntent.Intent);
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
                    baseScopeDigest,
                    request.ProducingRunIdentity,
                    request.ProducingRunAttempt,
                    active.Header.ObjectIdentity) ||
                !StringComparer.Ordinal.Equals(
                    request.Reset.RequestIdentity,
                    pending.Intent.TransitionEvidenceIdentity) ||
                !StringComparer.Ordinal.Equals(
                    request.Reset.ProducingRunIdentity,
                    pending.Intent.ResetAuthorityRunIdentity) ||
                request.Reset.ProducingRunAttempt !=
                    pending.Intent.ResetAuthorityRunAttempt ||
                !StringComparer.Ordinal.Equals(
                    pending.Object.Header.ProducingRunIdentity,
                    pending.Intent.ResetAuthorityRunIdentity) ||
                pending.Object.Header.ProducingRunAttempt !=
                    pending.Intent.ResetAuthorityRunAttempt))
        {
            return LineageResolveResult.Fail(LineageCodes.AccessDenied);
        }

        if (!StringComparer.Ordinal.Equals(
                active.Header.ObjectIdentity,
                pending.Intent.PriorHeadIdentity) ||
            !TryBuildSuccessor(
                context,
                request,
                baseScopeDigest,
                observed.Selection.Selection,
                pending,
                out _,
                out _,
                out _))
        {
            return LineageResolveResult.Fail(LineageCodes.Conflict);
        }

        if (!SatisfiesCurrentRequirement(
                pending.Object,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry))
        {
            var headroom = await EstablishIntentRefreshHeadroomAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    observed,
                    pending,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!headroom.Succeeded)
            {
                return LineageResolveResult.Fail(headroom.Code);
            }

            pending = headroom.Intent!;
            active = observed.Selection!.Selection!.Head;
            var refreshed = await WriteIntentAsync(
                    context,
                    request,
                    baseScopeDigest,
                    active,
                    pending.Intent,
                    now,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (refreshed.Header is null)
            {
                return LineageResolveResult.Fail(refreshed.Code);
            }

            var convergence = await ConvergeExpectedIntentAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    refreshed,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!convergence.Succeeded)
            {
                return LineageResolveResult.Fail(convergence.Code);
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
                        convergence.Observation!,
                        convergence.Intent!,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                ClearObservation(convergence.Observation);
            }
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
                requiredPlatformExpiry,
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

        var redundantIntents = observed.Snapshot!.Authenticated
            .Concat(observed.Snapshot.UnderRetained)
            .Where(item =>
                item.Header.ObjectClass is StateObjectClass.Reset or
                    StateObjectClass.ExpiryTransition &&
                StringComparer.Ordinal.Equals(
                    item.Header.ObjectIdentity,
                    pending.Object.Header.ObjectIdentity) &&
                item.Metadata != pending.Object.Metadata)
            .Select(item => item.Metadata)
            .ToImmutableArray();

        active = observed.Selection.Selection!.Head;
        if (!StringComparer.Ordinal.Equals(
                active.Header.ObjectIdentity,
                pending.Intent.PriorHeadIdentity) ||
            !TryBuildSuccessor(
                context,
                request,
                baseScopeDigest,
                observed.Selection.Selection,
                pending,
                out _,
                out _,
                out _))
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

        foreach (var target in redundantIntents)
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
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry,
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
                        LineageHeadCodec.Matches(target, item.Metadata))) ||
                redundantIntents.Any(target =>
                    ContainsMetadata(afterDelete.Snapshot, target)))
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

            var selectedIntent = SelectPendingIntent(
                afterDelete.Snapshot,
                active,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!selectedIntent.Succeeded ||
                selectedIntent.Intent is null ||
                selectedIntent.Intent.Object.Metadata !=
                    pending.Object.Metadata)
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

            var unexpectedPriorState = afterDelete.Snapshot.Authenticated
                .Concat(afterDelete.Snapshot.UnderRetained)
                .Any(
                    item =>
                        item.Header.ObjectClass !=
                            StateObjectClass.LineageHead &&
                        item.Metadata != pending.Object.Metadata) ||
                afterDelete.Snapshot.Unknown.Any(item =>
                    item.Metadata.Reference.Name !=
                        afterDelete.Snapshot.Names[
                            StateObjectClass.LineageHead]);
            if (unexpectedPriorState ||
                !TryBuildSuccessor(
                    context,
                    request,
                    baseScopeDigest,
                    afterDelete.Selection.Selection,
                    pending,
                    out var epoch,
                    out var sessionId,
                    out var successor))
            {
                return LineageResolveResult.Fail(LineageCodes.Conflict);
            }

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
                    frozenProducingRunIdentity: null,
                    frozenProducingRunAttempt: null,
                    cancellationToken: CancellationToken.None)
                .ConfigureAwait(false);
            if (written.Header is null)
            {
                return LineageResolveResult.Fail(written.Code);
            }

            var result = await ConvergeExpectedHeadAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    written.Header!,
                    now,
                    requiredPlatformExpiry,
                    ExpectedHeadConvergenceMode.TransitionSuccessor,
                    written,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return LineageResolveResult.Fail(result.Code);
            }

            return await CompleteTransitionAsync(
                    context,
                    request,
                    baseScopeDigest,
                    currentKeyId,
                    pending,
                    written.Header.ObjectIdentity,
                    requiredPlatformExpiry)
                .ConfigureAwait(false);
        }
        finally
        {
            ClearObservation(afterDelete);
        }
    }

    private static LineageHeadV1 BuildRefreshedHead(
        LineageHeadSelection selection) =>
        selection.Head.Head with
        {
            PhysicalSuperseded = selection.EquivalentPhysical
                .AddRange(selection.SafeNonAnchors)
                .Select(LineageHeadCodec.Evidence)
                .Distinct()
                .ToImmutableArray(),
        };

    private static bool CanEncodeHead(LineageHeadV1 head)
    {
        if (!LineageHeadCodec.TryEncode(head, out var payload))
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(payload);
        return true;
    }

    private static bool TryBuildSuccessor(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        LineageHeadSelection selection,
        SelectedIntent pending,
        out string epoch,
        out string sessionId,
        out LineageHeadV1 successor) =>
        TryBuildSuccessor(
            context,
            request,
            baseScopeDigest,
            selection,
            pending.Intent,
            LineageHeadCodec.Evidence(pending.Object.Metadata),
            out epoch,
            out sessionId,
            out successor);

    private static bool TryBuildSuccessor(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        LineageHeadSelection selection,
        LineageTransitionIntentV1 intent,
        LineageArtifactEvidence intentEvidence,
        out string epoch,
        out string sessionId,
        out LineageHeadV1 successor)
    {
        epoch = string.Empty;
        sessionId = string.Empty;
        successor = null!;
        var active = selection.Head;
        if (active.Head.Ordinal == ulong.MaxValue)
        {
            return false;
        }

        var derived = intent.Kind switch
        {
            LineageTransitionIntentKind.Reset =>
                LineageCryptography.TryDeriveResetEpoch(
                    context,
                    request.Access,
                    baseScopeDigest,
                    active.Header.ObjectIdentity,
                    intent.TransitionEvidenceIdentity,
                    intent.ResetAuthorityRunIdentity!,
                    intent.ResetAuthorityRunAttempt!.Value,
                    out epoch),
            LineageTransitionIntentKind.Expiry =>
                LineageCryptography.TryDeriveExpiryEpoch(
                    context,
                    request.Access,
                    baseScopeDigest,
                    active.Header.ObjectIdentity,
                    intent.TransitionEvidenceIdentity,
                    intent.ExpiryBoundaryUnixSeconds!.Value,
                    out epoch),
            _ => false,
        };
        if (!derived ||
            !LineageCryptography.TryDeriveSessionId(
                context,
                request.Access,
                baseScopeDigest,
                epoch,
                out sessionId))
        {
            return false;
        }

        successor = new LineageHeadV1(
            intent.Kind == LineageTransitionIntentKind.Reset
                ? LineageTransitionKind.Reset
                : LineageTransitionKind.Expiry,
            checked(active.Head.Ordinal + 1),
            intent.Reviewed,
            active.Header.Epoch,
            active.Header.ObjectIdentity,
            intent.TransitionEvidenceIdentity,
            intent.ExpiryBoundaryUnixSeconds,
            selection.EquivalentPhysical
                .Select(LineageHeadCodec.Evidence)
                .Distinct()
                .ToImmutableArray(),
            PhysicalSuperseded: [],
            Superseded: [intentEvidence],
            CompletedCleanup: intent.Targets
                .Distinct()
                .ToImmutableArray(),
            ResetAuthorityRunIdentity:
                intent.ResetAuthorityRunIdentity,
            ResetAuthorityRunAttempt:
                intent.ResetAuthorityRunAttempt);
        if (!LineageHeadCodec.TryEncode(successor, out var encoded))
        {
            return false;
        }

        CryptographicOperations.ZeroMemory(encoded);
        return true;
    }

    private async Task<LineageResolveResult> CompleteTransitionAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        SelectedIntent pending,
        string expectedHeadIdentity,
        long requiredPlatformExpiry)
    {
        var intentEvidence = LineageHeadCodec.Evidence(
            pending.Object.Metadata);
        for (var attempt = 0;
            attempt <= LineageFormat.MaximumScopedObjects;
            attempt++)
        {
            var observed = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            try
            {
                if (!observed.Succeeded)
                {
                    if (StringComparer.Ordinal.Equals(
                            observed.Code,
                            LineageCodes.Unavailable) &&
                        observed.DiagnosticTerminal is null)
                    {
                        continue;
                    }

                    return LineageResolveResult.Fail(observed.Code);
                }

                if (observed.Selection!.IsAbsent ||
                    !StringComparer.Ordinal.Equals(
                        observed.Selection.Selection!.Head.Header
                            .ObjectIdentity,
                        expectedHeadIdentity))
                {
                    continue;
                }

                var snapshot = observed.Snapshot!;
                var selected = observed.Selection.Selection.Head;
                var authorizedHistorical = selected.Head.Superseded
                    .Concat(selected.Head.CompletedCleanup)
                    .ToImmutableArray();
                if (!authorizedHistorical.Any(evidence =>
                        EvidenceEquals(evidence, intentEvidence)) ||
                    pending.Intent.Targets.Any(target =>
                        !authorizedHistorical.Any(evidence =>
                            EvidenceEquals(evidence, target))))
                {
                    return LineageResolveResult.Fail(LineageCodes.Conflict);
                }

                var residual = ImmutableArray.CreateBuilder<
                    OpaqueStoreObjectMetadata>();
                foreach (var item in snapshot.Authenticated
                    .Concat(snapshot.UnderRetained)
                    .Where(item => item.Header.ObjectClass !=
                        StateObjectClass.LineageHead))
                {
                    if (!authorizedHistorical.Any(evidence =>
                            LineageHeadCodec.Matches(
                                evidence,
                                item.Metadata)))
                    {
                        return LineageResolveResult.Fail(
                            LineageCodes.Conflict);
                    }

                    residual.Add(item.Metadata);
                }

                foreach (var item in snapshot.Unknown.Where(item =>
                    item.Metadata.Reference.Name != snapshot.Names[
                        StateObjectClass.LineageHead]))
                {
                    if (!authorizedHistorical.Any(evidence =>
                            LineageHeadCodec.Matches(
                                evidence,
                                item.Metadata)))
                    {
                        return LineageResolveResult.Fail(
                            LineageCodes.Conflict);
                    }

                    residual.Add(item.Metadata);
                }

                if (residual.Count == 0)
                {
                    if (!ValidateActiveState(snapshot, selected))
                    {
                        return LineageResolveResult.Fail(
                            LineageCodes.Conflict);
                    }

                    return await FinalizeAsync(request, selected)
                        .ConfigureAwait(false);
                }

                _ = await store.DeleteExactAsync(
                        new OpaqueStoreDeleteRequest(residual[0]),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                ClearObservation(observed);
            }
        }

        return LineageResolveResult.Fail(LineageCodes.CleanupFailed);
    }

    private async Task<bool> EstablishHeadroomAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        ObservationResult observed,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        var expectedIdentity = observed.Selection!.Selection!
            .Head.Header.ObjectIdentity;
        for (var attempt = 0;
            attempt < LineageFormat.MaximumPhysicalPerClass;
            attempt++)
        {
            var selection = observed.Selection!.Selection!;
            if (selection.PhysicalCount <= 7)
            {
                return true;
            }

            var target = !selection.SafeNonAnchors.IsEmpty
                ? selection.SafeNonAnchors[0]
                : !selection.SafeChainAnchors.IsEmpty
                    ? selection.SafeChainAnchors[0]
                    : null;
            if (target is null)
            {
                return false;
            }

            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);

            ClearObservation(observed);
            var reread = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!reread.Succeeded ||
                reread.Selection!.IsAbsent ||
                !StringComparer.Ordinal.Equals(
                    reread.Selection.Selection!.Head.Header.ObjectIdentity,
                    expectedIdentity) ||
                reread.Selection.Selection.PhysicalCount >=
                    selection.PhysicalCount)
            {
                ClearObservation(reread);
                return false;
            }

            observed.Snapshot = reread.Snapshot;
            observed.Selection = reread.Selection;
        }

        return false;
    }

    private async Task<IntentHeadroomResult>
        EstablishIntentRefreshHeadroomAsync(
            LocatorContext context,
            LineageResolveRequest request,
            string baseScopeDigest,
            string currentKeyId,
            ObservationResult observed,
            SelectedIntent pending,
            long requiredPlatformExpiry,
            CancellationToken cancellationToken)
    {
        var expectedHeadIdentity = observed.Selection!.Selection!
            .Head.Header.ObjectIdentity;
        var expectedIntentIdentity = pending.Object.Header.ObjectIdentity;
        var objectClass = pending.Object.Header.ObjectClass;
        for (var attempt = 0;
            attempt < LineageFormat.MaximumPhysicalPerClass;
            attempt++)
        {
            var snapshot = observed.Snapshot!;
            var name = snapshot.Names[objectClass];
            var authenticated = snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Where(item => item.Header.ObjectClass == objectClass)
                .ToImmutableArray();
            var unknown = snapshot.Unknown
                .Where(item => item.Metadata.Reference.Name == name)
                .ToImmutableArray();
            if (!unknown.IsEmpty)
            {
                return IntentHeadroomResult.Fail(LineageCodes.Conflict);
            }

            var active = observed.Selection.Selection!.Head;
            var selected = SelectPendingIntent(
                snapshot,
                active,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!selected.Succeeded)
            {
                return IntentHeadroomResult.Fail(selected.Code);
            }

            if (selected.Intent is null ||
                selected.Intent.Object.Header.ObjectClass != objectClass ||
                !StringComparer.Ordinal.Equals(
                    selected.Intent.Object.Header.ObjectIdentity,
                    expectedIntentIdentity))
            {
                return IntentHeadroomResult.Fail(LineageCodes.Conflict);
            }

            pending = selected.Intent;
            if (authenticated.Length <= 7)
            {
                return IntentHeadroomResult.Success(pending);
            }

            var authorizedEvidence = active.Head.Superseded
                .Concat(active.Head.CompletedCleanup)
                .ToImmutableArray();
            var target = authenticated
                .Where(item => item.Metadata != pending.Object.Metadata)
                .OrderByDescending(item =>
                    StringComparer.Ordinal.Equals(
                        item.Header.Epoch,
                        active.Header.Epoch) &&
                    StringComparer.Ordinal.Equals(
                        item.Header.ObjectIdentity,
                        expectedIntentIdentity))
                .ThenBy(item => item.Metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .FirstOrDefault(item =>
                    (StringComparer.Ordinal.Equals(
                            item.Header.Epoch,
                            active.Header.Epoch) &&
                        StringComparer.Ordinal.Equals(
                            item.Header.ObjectIdentity,
                            expectedIntentIdentity)) ||
                    authorizedEvidence.Any(evidence =>
                        LineageHeadCodec.Matches(evidence, item.Metadata)));
            if (target is null)
            {
                return IntentHeadroomResult.Fail(LineageCodes.CleanupFailed);
            }

            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target.Metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);

            ClearObservation(observed);
            var reread = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!reread.Succeeded || reread.Selection!.IsAbsent ||
                !StringComparer.Ordinal.Equals(
                    reread.Selection.Selection!.Head.Header.ObjectIdentity,
                    expectedHeadIdentity))
            {
                var code = reread.Succeeded
                    ? LineageCodes.Conflict
                    : reread.Code;
                ClearObservation(reread);
                return IntentHeadroomResult.Fail(code);
            }

            var remainingUnknown = reread.Snapshot!.Unknown.Where(item =>
                    item.Metadata.Reference.Name ==
                        reread.Snapshot.Names[objectClass])
                .ToImmutableArray();
            if (!remainingUnknown.IsEmpty)
            {
                ClearObservation(reread);
                return IntentHeadroomResult.Fail(LineageCodes.Conflict);
            }

            var reselected = SelectPendingIntent(
                reread.Snapshot,
                reread.Selection.Selection.Head,
                currentKeyId,
                request.RequiredLogicalExpiresAtUnixSeconds,
                requiredPlatformExpiry);
            if (!reselected.Succeeded || reselected.Intent is null ||
                reselected.Intent.Object.Header.ObjectClass != objectClass ||
                !StringComparer.Ordinal.Equals(
                    reselected.Intent.Object.Header.ObjectIdentity,
                    expectedIntentIdentity))
            {
                var code = reselected.Succeeded
                    ? LineageCodes.Conflict
                    : reselected.Code;
                ClearObservation(reread);
                return IntentHeadroomResult.Fail(code);
            }

            var remaining = reread.Snapshot.Authenticated
                .Concat(reread.Snapshot.UnderRetained)
                .Count(item => item.Header.ObjectClass == objectClass);
            if (remaining >= authenticated.Length)
            {
                ClearObservation(reread);
                return IntentHeadroomResult.Fail(LineageCodes.CleanupFailed);
            }

            observed.Snapshot = reread.Snapshot;
            observed.Selection = reread.Selection;
            pending = reselected.Intent;
        }

        return IntentHeadroomResult.Fail(LineageCodes.CleanupFailed);
    }

    private async Task<bool> EstablishTransitionHeadroomAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        ObservationResult observed,
        StateObjectClass objectClass,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        var expectedHeadIdentity = observed.Selection!.Selection!
            .Head.Header.ObjectIdentity;
        for (var attempt = 0;
            attempt < LineageFormat.MaximumPhysicalPerClass;
            attempt++)
        {
            var name = observed.Snapshot!.Names[objectClass];
            var physical = observed.Snapshot.Authenticated
                .Where(item => item.Header.ObjectClass == objectClass)
                .Select(item => item.Metadata)
                .Concat(observed.Snapshot.UnderRetained
                    .Where(item => item.Header.ObjectClass == objectClass)
                    .Select(item => item.Metadata))
                .Concat(observed.Snapshot.Unknown
                    .Where(item => item.Metadata.Reference.Name == name)
                    .Select(item => item.Metadata))
                .ToImmutableArray();
            if (physical.Length <= 7)
            {
                return true;
            }

            var active = observed.Selection.Selection!.Head;
            var authorizedEvidence = active.Head.Superseded
                .Concat(active.Head.CompletedCleanup)
                .ToImmutableArray();
            var target = physical
                .OrderBy(metadata => metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .FirstOrDefault(metadata => authorizedEvidence.Any(evidence =>
                    LineageHeadCodec.Matches(evidence, metadata)));
            if (target is null)
            {
                return false;
            }

            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);

            ClearObservation(observed);
            var reread = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reread.Succeeded ||
                reread.Selection!.IsAbsent ||
                !StringComparer.Ordinal.Equals(
                    reread.Selection.Selection!.Head.Header.ObjectIdentity,
                    expectedHeadIdentity))
            {
                ClearObservation(reread);
                return false;
            }

            var remaining = reread.Snapshot!.Authenticated
                    .Concat(reread.Snapshot.UnderRetained)
                    .Count(item => item.Header.ObjectClass == objectClass) +
                reread.Snapshot.Unknown.Count(item =>
                    item.Metadata.Reference.Name ==
                        reread.Snapshot.Names[objectClass]);
            if (remaining >= physical.Length)
            {
                ClearObservation(reread);
                return false;
            }

            observed.Snapshot = reread.Snapshot;
            observed.Selection = reread.Selection;
        }

        return false;
    }

    private async Task<LineageResolveResult>
        ConvergeOrdinaryExpectedHeadAsync(
            LocatorContext context,
            LineageResolveRequest request,
            string baseScopeDigest,
            string currentKeyId,
            StateControlHeaderV1 expected,
            long now,
            long requiredPlatformExpiry,
            WrittenObjectResult? written,
            CancellationToken cancellationToken)
    {
        var converged = await ConvergeExpectedHeadAsync(
                context,
                request,
                baseScopeDigest,
                currentKeyId,
                expected,
                now,
                requiredPlatformExpiry,
                ExpectedHeadConvergenceMode.Ordinary,
                written,
                cancellationToken)
            .ConfigureAwait(false);
        return converged.Succeeded
            ? FinalizeSelected(request, converged.Head!)
            : LineageResolveResult.Fail(converged.Code);
    }

    private async Task<HeadConvergenceResult> ConvergeExpectedHeadAsync(
        LocatorContext context,
        LineageResolveRequest request,
        string baseScopeDigest,
        string currentKeyId,
        StateControlHeaderV1 expected,
        long now,
        long requiredPlatformExpiry,
        ExpectedHeadConvergenceMode mode,
        WrittenObjectResult? written,
        CancellationToken cancellationToken)
    {
        var window = written is null
            ? null
            : CreateVisibilityWindow(
                written,
                StateReconciliationOwner.LineageHead);
        HeadConvergenceResult Fail(
            string code,
            StateReconciliationTerminal? terminal = null)
        {
            window?.ReportFailure(terminal ?? Terminal(code));
            return HeadConvergenceResult.Fail(code);
        }

        if (written is not null && !written.CanReconcile)
        {
            if (StringComparer.Ordinal.Equals(
                    written.Code,
                    LineageCodes.RetentionFailed) ||
                StringComparer.Ordinal.Equals(
                    written.Code,
                    LineageCodes.CleanupFailed))
            {
                var cleanupObservation = await ReadObservationAsync(
                        context,
                        request.Access,
                        request.BaseScope,
                        baseScopeDigest,
                        currentKeyId,
                        request.RequiredLogicalExpiresAtUnixSeconds,
                        requiredPlatformExpiry,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ClearObservation(cleanupObservation);
            }

            return Fail(written.Code, written.DiagnosticTerminal);
        }

        var cleanupPending = false;
        var immediateAttempts = 0;
        while (immediateAttempts < LineageFormat.MaximumPhysicalPerClass)
        {
            if (window is null)
            {
                immediateAttempts++;
            }

            var observed = await ReadObservationAsync(
                    context,
                    request.Access,
                    request.BaseScope,
                    baseScopeDigest,
                    currentKeyId,
                    request.RequiredLogicalExpiresAtUnixSeconds,
                    requiredPlatformExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
            window?.RecordObservation();
            try
            {
                if (!observed.Succeeded)
                {
                    if (StringComparer.Ordinal.Equals(
                            observed.Code,
                            LineageCodes.Unavailable) &&
                        observed.DiagnosticTerminal is null)
                    {
                        if (window is not null)
                        {
                            if (await window.WaitForNextObservationAsync()
                                .ConfigureAwait(false))
                            {
                                continue;
                            }

                            return Fail(
                                LineageCodes.Unavailable,
                                StateReconciliationTerminal.Unavailable);
                        }

                        continue;
                    }

                    return Fail(
                        observed.Code,
                        observed.DiagnosticTerminal);
                }

                if (observed.Selection!.IsAbsent)
                {
                    if (window is not null)
                    {
                        if (await window.WaitForNextObservationAsync()
                            .ConfigureAwait(false))
                        {
                            continue;
                        }

                        return Fail(
                            LineageCodes.Unavailable,
                            StateReconciliationTerminal.TargetAbsent);
                    }

                    continue;
                }

                var selectedIdentity = observed.Selection.Selection!
                    .Head.Header.ObjectIdentity;
                if (!StringComparer.Ordinal.Equals(
                        selectedIdentity,
                        expected.ObjectIdentity))
                {
                    if (expected.PredecessorIdentity is not null &&
                        StringComparer.Ordinal.Equals(
                            selectedIdentity,
                            expected.PredecessorIdentity))
                    {
                        if (window is not null)
                        {
                            if (await window.WaitForNextObservationAsync()
                                .ConfigureAwait(false))
                            {
                                continue;
                            }

                            return Fail(
                                LineageCodes.Unavailable,
                                StateReconciliationTerminal.TargetAbsent);
                        }

                        continue;
                    }

                    return Fail(LineageCodes.Conflict);
                }

                var selection = observed.Selection.Selection;
                if (written?.ReturnedMetadata is not null &&
                    selection.Head.Metadata != written.ReturnedMetadata &&
                    !selection.SafeToDelete.Contains(
                        written.ReturnedMetadata))
                {
                    if (await window!.WaitForNextObservationAsync()
                        .ConfigureAwait(false))
                    {
                        continue;
                    }

                    // Equivalent concurrent head claims are idempotent. Once
                    // the complete visibility window is exhausted, another
                    // resolver may already have authenticated and pruned this
                    // process's physical duplicate. Never extend this fallback
                    // to multiple, unknown, or under-retained candidates.
                    if (!CanConvergeEquivalentHeadAfterReturnedObjectAbsence(
                            observed,
                            selection,
                            written.ReturnedMetadata))
                    {
                        return Fail(
                            LineageCodes.Unavailable,
                            StateReconciliationTerminal.TargetAbsent);
                    }
                }

                if (window is not null)
                {
                    immediateAttempts++;
                }
                var selectedHead = selection.Head;
                if (!StringComparer.Ordinal.Equals(
                        selectedHead.Header.KeyId,
                        expected.KeyId) ||
                    selectedHead.Header.LogicalExpiresAtUnixSeconds <
                        expected.LogicalExpiresAtUnixSeconds ||
                    selectedHead.Header.RequiredPlatformExpiresAtUnixSeconds <
                        expected.RequiredPlatformExpiresAtUnixSeconds ||
                    selectedHead.Header.RequiredPlatformExpiresAtUnixSeconds <
                        requiredPlatformExpiry ||
                    selectedHead.Metadata.ExpiresAtUnixSeconds <
                        selectedHead.Header.RequiredPlatformExpiresAtUnixSeconds)
                {
                    continue;
                }

                if (selection.SafeToDelete.IsEmpty)
                {
                    if (mode == ExpectedHeadConvergenceMode.Ordinary)
                    {
                        if (!observed.Snapshot!.UnderRetained.IsEmpty)
                        {
                            return Fail(LineageCodes.RetentionFailed);
                        }

                        if (observed.Snapshot.Unknown.Any(item =>
                                item.Metadata.Reference.Name !=
                                    observed.Snapshot.Names[
                                        StateObjectClass.LineageHead]))
                        {
                            return Fail(LineageCodes.Conflict);
                        }

                        var authority = EvaluateOrdinaryAuthority(
                            observed.Snapshot,
                            selection.Head,
                            request,
                            currentKeyId,
                            request.RequiredLogicalExpiresAtUnixSeconds,
                            requiredPlatformExpiry,
                            now);
                        if (!authority.Succeeded)
                        {
                            return Fail(authority.Code);
                        }

                        if (authority.RequiresTransition)
                        {
                            return Fail(LineageCodes.Unavailable);
                        }
                    }

                    if (await ReadBackHeadAsync(selection.Head)
                        .ConfigureAwait(false))
                    {
                        return HeadConvergenceResult.Success(selection.Head);
                    }

                    continue;
                }

                var target = !selection.SafeNonAnchors.IsEmpty
                    ? selection.SafeNonAnchors[0]
                    : selection.SafeChainAnchors[0];
                cleanupPending = true;
                if (!await DeleteAndVerifyAsync(
                        context,
                        request,
                        baseScopeDigest,
                        currentKeyId,
                        [target],
                        CancellationToken.None)
                    .ConfigureAwait(false))
                {
                    continue;
                }
            }
            finally
            {
                ClearObservation(observed);
            }
        }

        return Fail(cleanupPending
            ? LineageCodes.CleanupFailed
            : LineageCodes.Unavailable);
    }

    private PostUploadVisibilityWindow CreateVisibilityWindow(
        WrittenObjectResult written,
        StateReconciliationOwner owner) =>
        new(
            timeProvider,
            diagnosticSink,
            owner,
            written.MutationState switch
            {
                OpaqueStoreMutationState.Committed =>
                    StateReconciliationOutcome.Committed,
                OpaqueStoreMutationState.OutcomeUnknown =>
                    StateReconciliationOutcome.OutcomeUnknown,
                _ => StateReconciliationOutcome.NotCommitted,
            },
            written.ExactReadBack);

    private static StateReconciliationTerminal Terminal(string code) =>
        code switch
        {
            LineageCodes.Conflict => StateReconciliationTerminal.Conflict,
            LineageCodes.AuthenticationFailed =>
                StateReconciliationTerminal.AuthenticationFailed,
            LineageCodes.KeyUnavailable =>
                StateReconciliationTerminal.KeyUnavailable,
            LineageCodes.RetentionFailed =>
                StateReconciliationTerminal.RetentionFailed,
            LineageCodes.CleanupFailed =>
                StateReconciliationTerminal.CleanupFailed,
            LineageCodes.Invalid => StateReconciliationTerminal.Invalid,
            _ => StateReconciliationTerminal.Unavailable,
        };

    private async Task<LineageResolveResult> FinalizeAsync(
        LineageResolveRequest request,
        LineageHeadCandidate head)
    {
        if (!await ReadBackHeadAsync(head).ConfigureAwait(false))
        {
            return LineageResolveResult.Fail(LineageCodes.Unavailable);
        }

        return FinalizeSelected(request, head);
    }

    private async Task<bool> ReadBackHeadAsync(LineageHeadCandidate head)
    {
        var readBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(head.Metadata),
                CancellationToken.None)
            .ConfigureAwait(false);
        return readBack.Succeeded && readBack.Metadata == head.Metadata;
    }

    private static LineageResolveResult FinalizeSelected(
        LineageResolveRequest request,
        LineageHeadCandidate head) =>
        LineageResolveResult.Success(new SelectedLineageContext(
            request.Access,
            request.BaseScope.RepositoryId,
            new SelectedLineageSnapshot(
                head.Header.BaseScopeDigest,
                head.Header.Epoch,
                head.Header.SessionId,
                head.Header.ObjectIdentity,
                head.Head.Transition)));

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
        string? frozenProducingRunIdentity,
        long? frozenProducingRunAttempt,
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
                frozenProducingRunIdentity ?? request.ProducingRunIdentity,
                frozenProducingRunAttempt ?? request.ProducingRunAttempt,
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
                return WrittenObjectResult.FromUpload(uploaded, header);
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
                request.RequiredLogicalExpiresAtUnixSeconds,
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
                return WrittenObjectResult.FromUpload(uploaded, header);
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
        long requiredLogicalExpiry,
        long requiredPlatformExpiry,
        CancellationToken cancellationToken)
    {
        var cleanupStarted = false;
        for (var cleanupAttempt = 0;
            cleanupAttempt <= LineageFormat.MaximumScopedObjects;
            cleanupAttempt++)
        {
            var read = await inventory.ReadAsync(
                    context,
                    access,
                    scope,
                    baseScopeDigest,
                    cleanupStarted ? CancellationToken.None : cancellationToken)
                .ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return ObservationResult.Fail(cleanupStarted
                    ? LineageCodes.CleanupFailed
                    : read.Code,
                    cleanupStarted
                        ? StateReconciliationTerminal.CleanupFailed
                        : read.DiagnosticTerminal);
            }

            var snapshot = read.Snapshot!;
            var lineageName = snapshot.Names[StateObjectClass.LineageHead];
            var heads = ImmutableArray.CreateBuilder<LineageHeadCandidate>();
            var underRetainedHeads = ImmutableArray.CreateBuilder<
                LineageHeadCandidate>();
            foreach (var item in snapshot.Authenticated
                .Concat(snapshot.UnderRetained)
                .Where(item => item.Header.ObjectClass ==
                    StateObjectClass.LineageHead))
            {
                if (!LineageHeadCodec.TryDecode(item.Payload, out var head) ||
                    head is null ||
                    !ValidateDerivedHead(
                        context,
                        access,
                        baseScopeDigest,
                        item.Header,
                        head))
                {
                    ScopedStateInventory.Clear(snapshot);
                    return ObservationResult.Fail(
                        LineageCodes.AuthenticationFailed);
                }

                var candidate = new LineageHeadCandidate(
                    item.Metadata,
                    item.Header,
                    head);
                if (snapshot.UnderRetained.Contains(item))
                {
                    underRetainedHeads.Add(candidate);
                }
                else
                {
                    heads.Add(candidate);
                }
            }

            var unknownHeads = snapshot.Unknown.Where(item =>
                    item.Metadata.Reference.Name == lineageName)
                .ToImmutableArray();
            var authenticatedHeads = heads.ToImmutable();
            if (!snapshot.UnderRetained.IsEmpty)
            {
                var debt = SelectUnderRetainedCleanup(
                    snapshot,
                    authenticatedHeads,
                    underRetainedHeads.ToImmutable(),
                    unknownHeads,
                    currentKeyId,
                    requiredLogicalExpiry,
                    requiredPlatformExpiry);
                if (debt.Target is null)
                {
                    if (!StringComparer.Ordinal.Equals(
                            debt.Code,
                            LineageCodes.Ready))
                    {
                        ScopedStateInventory.Clear(snapshot);
                        return ObservationResult.Fail(debt.Code);
                    }
                }
                else
                {
                    if (cleanupAttempt ==
                        LineageFormat.MaximumScopedObjects)
                    {
                        ScopedStateInventory.Clear(snapshot);
                        return ObservationResult.Fail(
                            LineageCodes.CleanupFailed);
                    }

                    var target = debt.Target;
                    ScopedStateInventory.Clear(snapshot);
                    cleanupStarted = true;
                    try
                    {
                        _ = await store.DeleteExactAsync(
                                new OpaqueStoreDeleteRequest(target),
                                CancellationToken.None)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return ObservationResult.Fail(
                            LineageCodes.CleanupFailed);
                    }

                    continue;
                }
            }

            var selection = LineageHeadSelector.Select(
                authenticatedHeads,
                unknownHeads,
                authenticatedHeads.Length + unknownHeads.Length,
                currentKeyId,
                requiredLogicalExpiry,
                requiredPlatformExpiry);
            if (!selection.Succeeded)
            {
                ScopedStateInventory.Clear(snapshot);
                return ObservationResult.Fail(selection.Code);
            }

            return ObservationResult.Success(snapshot, selection);
        }

        return ObservationResult.Fail(LineageCodes.CleanupFailed);
    }

    private static CleanupDebtResult SelectUnderRetainedCleanup(
        ScopedStateInventorySnapshot snapshot,
        ImmutableArray<LineageHeadCandidate> authenticatedHeads,
        ImmutableArray<LineageHeadCandidate> underRetainedHeads,
        ImmutableArray<UnknownStateObject> unknownHeads,
        string currentKeyId,
        long requiredLogicalExpiry,
        long requiredPlatformExpiry)
    {
        foreach (var item in snapshot.UnderRetained
            .Where(item => item.Header.ObjectClass !=
                StateObjectClass.LineageHead)
            .OrderBy(item => item.Header.ObjectClass)
            .ThenBy(item => item.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal))
        {
            var hasEquivalent = snapshot.Authenticated.Any(candidate =>
                candidate.Header.ObjectClass == item.Header.ObjectClass &&
                StringComparer.Ordinal.Equals(
                    candidate.Header.ObjectIdentity,
                    item.Header.ObjectIdentity));
            if (hasEquivalent)
            {
                return CleanupDebtResult.Success(item.Metadata);
            }
        }

        if (underRetainedHeads.IsEmpty)
        {
            return CleanupDebtResult.Deferred();
        }

        if (CanReestablishInitialAbsence(
                snapshot,
                authenticatedHeads,
                underRetainedHeads))
        {
            return CleanupDebtResult.Success(underRetainedHeads
                .OrderBy(candidate =>
                    candidate.Metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .First()
                .Metadata);
        }

        var allHeads = authenticatedHeads.AddRange(underRetainedHeads);
        var topology = LineageHeadSelector.Select(
            allHeads,
            unknownHeads,
            allHeads.Length + unknownHeads.Length,
            currentKeyId,
            requiredLogicalExpiry,
            requiredPlatformExpiry);
        if (!topology.Succeeded || topology.IsAbsent)
        {
            return CleanupDebtResult.Fail(topology.Code);
        }

        var equivalentDebt = underRetainedHeads
            .Where(debt => authenticatedHeads.Any(candidate =>
                StringComparer.Ordinal.Equals(
                    candidate.Header.ObjectIdentity,
                    debt.Header.ObjectIdentity) &&
                LineageHeadCodec.Equivalent(candidate.Head, debt.Head)))
            .OrderBy(candidate => candidate.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .FirstOrDefault();
        if (equivalentDebt is not null)
        {
            return CleanupDebtResult.Success(equivalentDebt.Metadata);
        }

        var selected = topology.Selection!.Head;
        var selectedIsUnderRetained = underRetainedHeads.Any(candidate =>
            candidate.Metadata == selected.Metadata);
        if (!selectedIsUnderRetained)
        {
            return CleanupDebtResult.Fail(LineageCodes.RetentionFailed);
        }

        var predecessor = topology.Selection.ImmediatePredecessor;
        if (predecessor is not null &&
            authenticatedHeads.Any(candidate =>
                candidate.Metadata == predecessor.Metadata) &&
            underRetainedHeads
                .Where(candidate => StringComparer.Ordinal.Equals(
                    candidate.Header.ObjectIdentity,
                    selected.Header.ObjectIdentity))
                .SelectMany(candidate => candidate.Head.PhysicalPredecessors)
                .Any(evidence => LineageHeadCodec.Matches(
                    evidence,
                    predecessor.Metadata)))
        {
            return CleanupDebtResult.Success(selected.Metadata);
        }

        return CleanupDebtResult.Fail(LineageCodes.RetentionFailed);
    }

    private static bool CanReestablishInitialAbsence(
        ScopedStateInventorySnapshot snapshot,
        ImmutableArray<LineageHeadCandidate> authenticatedHeads,
        ImmutableArray<LineageHeadCandidate> underRetainedHeads)
    {
        if (!authenticatedHeads.IsEmpty ||
            !snapshot.Authenticated.IsEmpty ||
            !snapshot.Unknown.IsEmpty ||
            snapshot.UnderRetained.Length != underRetainedHeads.Length)
        {
            return false;
        }

        var root = underRetainedHeads[0];
        return root.Head.Transition == LineageTransitionKind.Initial &&
            root.Head.Ordinal == 0 &&
            underRetainedHeads.All(candidate =>
                candidate.Head.Transition == LineageTransitionKind.Initial &&
                candidate.Head.Ordinal == 0 &&
                StringComparer.Ordinal.Equals(
                    candidate.Header.Epoch,
                    root.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    candidate.Header.SessionId,
                    root.Header.SessionId) &&
                StringComparer.Ordinal.Equals(
                    candidate.Header.ObjectIdentity,
                    root.Header.ObjectIdentity) &&
                LineageHeadCodec.Equivalent(candidate.Head, root.Head));
    }

    private static bool SatisfiesExpectedPhysical(
        AuthenticatedStateObject selected,
        StateControlHeaderV1 expected,
        long requiredPlatformExpiry) =>
        StringComparer.Ordinal.Equals(
            selected.Header.KeyId,
            expected.KeyId) &&
        selected.Header.LogicalExpiresAtUnixSeconds >=
            expected.LogicalExpiresAtUnixSeconds &&
        selected.Header.RequiredPlatformExpiresAtUnixSeconds >=
            expected.RequiredPlatformExpiresAtUnixSeconds &&
        selected.Header.RequiredPlatformExpiresAtUnixSeconds >=
            requiredPlatformExpiry &&
        selected.Metadata.ExpiresAtUnixSeconds >=
            selected.Header.RequiredPlatformExpiresAtUnixSeconds;

    private static bool SatisfiesCurrentRequirement(
        AuthenticatedStateObject selected,
        string currentKeyId,
        long requiredLogicalExpiry,
        long requiredPlatformExpiry) =>
        StringComparer.Ordinal.Equals(
            selected.Header.KeyId,
            currentKeyId) &&
        selected.Header.LogicalExpiresAtUnixSeconds >=
            requiredLogicalExpiry &&
        selected.Header.RequiredPlatformExpiresAtUnixSeconds >=
            requiredPlatformExpiry &&
        selected.Metadata.ExpiresAtUnixSeconds >=
            selected.Header.RequiredPlatformExpiresAtUnixSeconds &&
        selected.Metadata.ExpiresAtUnixSeconds >= requiredPlatformExpiry;

    private static OrdinaryAuthorityResult EvaluateOrdinaryAuthority(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active,
        LineageResolveRequest request,
        string currentKeyId,
        long requiredLogicalExpiry,
        long requiredPlatformExpiry,
        long now)
    {
        if (!ValidateActiveState(snapshot, active))
        {
            return OrdinaryAuthorityResult.Fail(LineageCodes.Conflict);
        }

        var pending = SelectPendingIntent(
            snapshot,
            active,
            currentKeyId,
            requiredLogicalExpiry,
            requiredPlatformExpiry);
        if (!pending.Succeeded)
        {
            return OrdinaryAuthorityResult.Fail(pending.Code);
        }

        if (pending.Intent is not null)
        {
            return OrdinaryAuthorityResult.Pending(pending.Intent);
        }

        if (request.Reset is not null)
        {
            return OrdinaryAuthorityResult.Ready();
        }

        var expiry = SelectExpiredAcceptance(snapshot, active, now);
        if (!expiry.Succeeded)
        {
            return OrdinaryAuthorityResult.Fail(expiry.Code);
        }

        return expiry.Acceptance is null
            ? OrdinaryAuthorityResult.Ready()
            : OrdinaryAuthorityResult.Expired(expiry.Acceptance);
    }

    private static PendingIntentResult SelectPendingIntent(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active,
        string currentKeyId,
        long requiredLogicalExpiry,
        long requiredPlatformExpiry)
    {
        var parsed = ImmutableArray.CreateBuilder<SelectedIntent>();
        foreach (var item in snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item =>
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

            if (!StringComparer.Ordinal.Equals(
                    intent.PriorHeadIdentity,
                    active.Header.ObjectIdentity) ||
                !StringComparer.Ordinal.Equals(
                    intent.PriorEpoch,
                    active.Header.Epoch) ||
                !StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    active.Header.SessionId) ||
                !StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    active.Header.ObjectIdentity))
            {
                return PendingIntentResult.Fail(LineageCodes.Conflict);
            }

            if (intent.Kind == LineageTransitionIntentKind.Reset &&
                (!StringComparer.Ordinal.Equals(
                    item.Header.ProducingRunIdentity,
                    intent.ResetAuthorityRunIdentity) ||
                    item.Header.ProducingRunAttempt !=
                        intent.ResetAuthorityRunAttempt))
            {
                return PendingIntentResult.Fail(LineageCodes.AccessDenied);
            }

            parsed.Add(new SelectedIntent(item, intent));
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
            .OrderByDescending(value => SatisfiesCurrentRequirement(
                value.Object,
                currentKeyId,
                requiredLogicalExpiry,
                requiredPlatformExpiry))
            .ThenByDescending(value => StringComparer.Ordinal.Equals(
                value.Object.Header.KeyId,
                currentKeyId))
            .ThenByDescending(value =>
                value.Object.Header.LogicalExpiresAtUnixSeconds)
            .ThenByDescending(value =>
                value.Object.Header.RequiredPlatformExpiresAtUnixSeconds)
            .ThenBy(value =>
                value.Object.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First());
    }

    private static LineagePendingExpiryIntent PendingExpiry(
        SelectedIntent selected,
        LineageHeadCandidate head) =>
        new(
            selected.Object.Header.ObjectIdentity,
            selected.Intent.PriorHeadIdentity,
            selected.Intent.PriorEpoch,
            head.Header.SessionId,
            selected.Intent.TransitionEvidenceIdentity,
            selected.Intent.ExpiryBoundaryUnixSeconds!.Value,
            selected.Intent.InventorySha256);

    private static bool MatchesPendingExpiry(
        LineagePendingExpiryIntent expected,
        SelectedIntent actual,
        LineageHeadCandidate head) =>
        actual.Intent.Kind == LineageTransitionIntentKind.Expiry &&
        StringComparer.Ordinal.Equals(
            actual.Object.Header.ObjectIdentity,
            expected.IntentIdentity) &&
        StringComparer.Ordinal.Equals(
            actual.Intent.PriorHeadIdentity,
            expected.PriorHeadIdentity) &&
        StringComparer.Ordinal.Equals(
            actual.Intent.PriorEpoch,
            expected.PriorEpoch) &&
        StringComparer.Ordinal.Equals(
            head.Header.ObjectIdentity,
            expected.PriorHeadIdentity) &&
        StringComparer.Ordinal.Equals(
            head.Header.Epoch,
            expected.PriorEpoch) &&
        StringComparer.Ordinal.Equals(
            head.Header.SessionId,
            expected.SessionId) &&
        StringComparer.Ordinal.Equals(
            actual.Intent.TransitionEvidenceIdentity,
            expected.TerminalReceiptIdentity) &&
        actual.Intent.ExpiryBoundaryUnixSeconds ==
            expected.ExpiryBoundaryUnixSeconds &&
        StringComparer.Ordinal.Equals(
            actual.Intent.InventorySha256,
            expected.TargetInventoryDigest) &&
        StringComparer.Ordinal.Equals(
            LineageCryptography.InventoryDigest(actual.Intent.Targets),
            expected.TargetInventoryDigest);

    private static ExpiredAcceptanceResult SelectExpiredAcceptance(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active,
        long now)
    {
        var activeAcceptances = snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item =>
                item.Header.ObjectClass == StateObjectClass.Acceptance &&
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    active.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    active.Header.SessionId))
            .ToImmutableArray();
        if (activeAcceptances.IsEmpty)
        {
            return ExpiredAcceptanceResult.None();
        }

        var groups = activeAcceptances
            .GroupBy(item => item.Header.ObjectIdentity, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.Ordinal);
        if (groups.Values.Any(group => group.Any(item =>
                !StringComparer.Ordinal.Equals(
                    item.Header.PredecessorIdentity,
                    group[0].Header.PredecessorIdentity) ||
                item.Header.LogicalExpiresAtUnixSeconds !=
                    group[0].Header.LogicalExpiresAtUnixSeconds)))
        {
            return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
        }

        var referenced = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal);
        foreach (var predecessor in groups.Values
            .Select(group => group[0].Header.PredecessorIdentity)
            .OfType<string>())
        {
            if (!groups.ContainsKey(predecessor))
            {
                return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
            }

            referenced.Add(predecessor);
        }

        var leaves = groups
            .Where(pair => !referenced.Contains(pair.Key))
            .Select(pair => pair.Value)
            .ToImmutableArray();
        if (leaves.Length != 1)
        {
            return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
        }

        var visited = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal);
        var cursor = leaves[0][0];
        while (cursor.Header.PredecessorIdentity is not null)
        {
            if (!visited.Add(cursor.Header.ObjectIdentity) ||
                !groups.TryGetValue(
                    cursor.Header.PredecessorIdentity,
                    out var predecessor))
            {
                return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
            }

            cursor = predecessor[0];
        }

        if (!visited.Add(cursor.Header.ObjectIdentity) ||
            visited.Count != groups.Count)
        {
            return ExpiredAcceptanceResult.Fail(LineageCodes.Conflict);
        }

        var current = leaves[0]
            .OrderByDescending(item =>
                item.Header.RequiredPlatformExpiresAtUnixSeconds)
            .ThenByDescending(item => item.Metadata.ExpiresAtUnixSeconds)
            .ThenBy(item => item.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First();
        return current.Header.LogicalExpiresAtUnixSeconds <= now
            ? ExpiredAcceptanceResult.Success(current)
            : ExpiredAcceptanceResult.None();
    }

    private static bool ValidateActiveState(
        ScopedStateInventorySnapshot snapshot,
        LineageHeadCandidate active) =>
        snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item => item.Header.ObjectClass !=
                StateObjectClass.LineageHead)
            .All(item =>
                StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    active.Header.Epoch) &&
                StringComparer.Ordinal.Equals(
                    item.Header.SessionId,
                    active.Header.SessionId));

    private static ImmutableArray<OpaqueStoreObjectMetadata>?
        FindAuthorizedStaleCleanup(
            ScopedStateInventorySnapshot snapshot,
            LineageHeadSelection selection,
            SelectedIntent? pending)
    {
        var evidence = selection.Head.Head.Superseded
            .Concat(selection.Head.Head.CompletedCleanup)
            .ToImmutableArray();
        var pendingTargets = pending?.Intent.Targets ?? [];
        var cleanup = ImmutableArray.CreateBuilder<OpaqueStoreObjectMetadata>();
        foreach (var item in snapshot.Authenticated
            .Concat(snapshot.UnderRetained)
            .Where(item =>
                item.Header.ObjectClass != StateObjectClass.LineageHead &&
                !StringComparer.Ordinal.Equals(
                    item.Header.Epoch,
                    selection.Head.Header.Epoch)))
        {
            if (pendingTargets.Any(value =>
                    LineageHeadCodec.Matches(value, item.Metadata)))
            {
                continue;
            }

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
            if (pendingTargets.Any(value =>
                    LineageHeadCodec.Matches(value, item.Metadata)))
            {
                continue;
            }

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
                    !reread.Snapshot.UnderRetained.Any(item =>
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
        foreach (var matches in evidence.Select(item =>
            snapshot.Authenticated
                .Select(value => value.Metadata)
                .Concat(snapshot.UnderRetained.Select(value => value.Metadata))
                .Concat(snapshot.Unknown.Select(value => value.Metadata))
                .Where(metadata => LineageHeadCodec.Matches(item, metadata))
                .ToImmutableArray()))
        {
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
            LineageHeadCodec.Matches(evidence, item.Metadata)) ||
        snapshot.UnderRetained.Any(item =>
            LineageHeadCodec.Matches(evidence, item.Metadata));

    private static bool ContainsMetadata(
        ScopedStateInventorySnapshot snapshot,
        OpaqueStoreObjectMetadata metadata) =>
        snapshot.Authenticated.Any(item => item.Metadata == metadata) ||
        snapshot.UnderRetained.Any(item => item.Metadata == metadata) ||
        snapshot.Unknown.Any(item => item.Metadata == metadata);

    private static bool CanConvergeEquivalentHeadAfterReturnedObjectAbsence(
        ObservationResult observed,
        LineageHeadSelection selection,
        OpaqueStoreObjectMetadata returnedMetadata) =>
        observed.Snapshot is not null &&
        !ContainsMetadata(observed.Snapshot, returnedMetadata) &&
        observed.Snapshot.UnderRetained.IsEmpty &&
        observed.Snapshot.Unknown.IsEmpty &&
        selection.EquivalentPhysical.Length == 1;

    private static bool EvidenceEquals(
        LineageArtifactEvidence left,
        LineageArtifactEvidence right) => left == right;

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

    private static bool ValidateDerivedHead(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        string baseScopeDigest,
        StateControlHeaderV1 header,
        LineageHeadV1 head)
    {
        var epoch = string.Empty;
        var derived = head.Transition switch
        {
            LineageTransitionKind.Initial =>
                LineageCryptography.TryDeriveInitialEpoch(
                    context,
                    access,
                    baseScopeDigest,
                    out epoch),
            LineageTransitionKind.Reset =>
                LineageCryptography.TryDeriveResetEpoch(
                    context,
                    access,
                    baseScopeDigest,
                    head.PreviousHeadIdentity!,
                    head.TransitionEvidenceIdentity!,
                    head.ResetAuthorityRunIdentity!,
                    head.ResetAuthorityRunAttempt!.Value,
                    out epoch),
            LineageTransitionKind.Expiry =>
                LineageCryptography.TryDeriveExpiryEpoch(
                    context,
                    access,
                    baseScopeDigest,
                    head.PreviousHeadIdentity!,
                    head.TransitionEvidenceIdentity!,
                    head.ExpiryBoundaryUnixSeconds!.Value,
                    out epoch),
            _ => false,
        };
        return derived &&
            LineageCryptography.TryDeriveSessionId(
                context,
                access,
                baseScopeDigest,
                epoch,
                out var sessionId) &&
            StringComparer.Ordinal.Equals(header.Epoch, epoch) &&
            StringComparer.Ordinal.Equals(header.SessionId, sessionId);
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
                checked(logicalExpiry + StateRetentionRequirements
                    .SentinelDependentMarginSeconds));
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

    private enum ExpectedHeadConvergenceMode
    {
        Ordinary,
        TransitionSuccessor,
    }

    private sealed record HeadConvergenceResult(
        string Code,
        LineageHeadCandidate? Head)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Head is not null;

        internal static HeadConvergenceResult Success(
            LineageHeadCandidate head) =>
            new(LineageCodes.Ready, head);

        internal static HeadConvergenceResult Fail(string code) =>
            new(code, null);
    }

    private sealed record WrittenObjectResult(
        string Code,
        StateControlHeaderV1? Header,
        OpaqueStoreMutationState MutationState,
        OpaqueStoreObjectMetadata? ReturnedMetadata,
        StateReconciliationExactReadBack ExactReadBack,
        StateReconciliationTerminal? DiagnosticTerminal)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            MutationState != OpaqueStoreMutationState.NotCommitted &&
            ReturnedMetadata is not null &&
            ExactReadBack == StateReconciliationExactReadBack.Matched &&
            Header is not null;

        internal bool CanReconcile =>
            Header is not null &&
            MutationState != OpaqueStoreMutationState.NotCommitted &&
            (Succeeded ||
                StringComparer.Ordinal.Equals(Code, LineageCodes.Unavailable));

        internal static WrittenObjectResult FromUpload(
            ScopedStateUploadResult upload,
            StateControlHeaderV1 header) =>
            new(
                upload.Code,
                header,
                upload.MutationState,
                upload.ReturnedMetadata,
                upload.ExactReadBack,
                upload.DiagnosticTerminal);

        internal static WrittenObjectResult Fail(
            string code,
            StateControlHeaderV1? header = null) =>
            new(
                code,
                header,
                OpaqueStoreMutationState.NotCommitted,
                null,
                StateReconciliationExactReadBack.NotApplicable,
                null);
    }

    private sealed record CleanupDebtResult(
        string Code,
        OpaqueStoreObjectMetadata? Target)
    {
        internal static CleanupDebtResult Success(
            OpaqueStoreObjectMetadata target) =>
            new(LineageCodes.Ready, target);

        internal static CleanupDebtResult Deferred() =>
            new(LineageCodes.Ready, null);

        internal static CleanupDebtResult Fail(string code) =>
            new(code, null);
    }

    private sealed class ObservationResult
    {
        private ObservationResult(
            string code,
            ScopedStateInventorySnapshot? snapshot,
            LineageSelectionResult? selection,
            StateReconciliationTerminal? diagnosticTerminal)
        {
            Code = code;
            Snapshot = snapshot;
            Selection = selection;
            DiagnosticTerminal = diagnosticTerminal;
        }

        internal string Code { get; }
        internal ScopedStateInventorySnapshot? Snapshot { get; set; }
        internal LineageSelectionResult? Selection { get; set; }
        internal StateReconciliationTerminal? DiagnosticTerminal { get; }
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Snapshot is not null &&
            Selection is not null;

        internal static ObservationResult Success(
            ScopedStateInventorySnapshot snapshot,
            LineageSelectionResult selection) =>
            new(LineageCodes.Ready, snapshot, selection, null);

        internal static ObservationResult Fail(
            string code,
            StateReconciliationTerminal? diagnosticTerminal = null) =>
            new(code, null, null, diagnosticTerminal);
    }

    private sealed record SelectedIntent(
        AuthenticatedStateObject Object,
        LineageTransitionIntentV1 Intent);

    private sealed record OrdinaryAuthorityResult(
        string Code,
        SelectedIntent? PendingIntent,
        AuthenticatedStateObject? ExpiredAcceptance)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready);
        internal bool RequiresTransition =>
            PendingIntent is not null || ExpiredAcceptance is not null;

        internal static OrdinaryAuthorityResult Ready() =>
            new(LineageCodes.Ready, null, null);

        internal static OrdinaryAuthorityResult Pending(
            SelectedIntent intent) =>
            new(LineageCodes.Ready, intent, null);

        internal static OrdinaryAuthorityResult Expired(
            AuthenticatedStateObject acceptance) =>
            new(LineageCodes.Ready, null, acceptance);

        internal static OrdinaryAuthorityResult Fail(string code) =>
            new(code, null, null);
    }

    private sealed record IntentHeadroomResult(
        string Code,
        SelectedIntent? Intent)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Intent is not null;

        internal static IntentHeadroomResult Success(SelectedIntent intent) =>
            new(LineageCodes.Ready, intent);

        internal static IntentHeadroomResult Fail(string code) =>
            new(code, null);
    }

    private sealed record IntentConvergenceResult(
        string Code,
        ObservationResult? Observation,
        SelectedIntent? Intent)
    {
        internal bool Succeeded =>
            StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
            Observation is not null &&
            Intent is not null;

        internal static IntentConvergenceResult Success(
            ObservationResult observation,
            SelectedIntent intent) =>
            new(LineageCodes.Ready, observation, intent);

        internal static IntentConvergenceResult Fail(string code) =>
            new(code, null, null);
    }

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
