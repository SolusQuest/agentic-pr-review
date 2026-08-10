using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal sealed class LocatorRootService
{
    private static readonly OpaqueStoreName SentinelName =
        new(LocatorRootFormat.StoreName);
    private const int ReconciliationAttempts = 3;

    private readonly IRestrictedStateStore store;
    private readonly LocatorStateKeyRing keys;
    private readonly TimeProvider timeProvider;

    internal LocatorRootService(
        IRestrictedStateStore store,
        LocatorStateKeyRing keys,
        TimeProvider? timeProvider = null)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
        this.keys = keys ?? throw new ArgumentNullException(nameof(keys));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<LocatorRootResult> ResolveAsync(
        AuthorizedLocatorAccess? access,
        long dependentExpiresAtUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (!keys.Allows(access))
        {
            return LocatorRootResult.Fail(LocatorCodes.AccessDenied);
        }

        if (dependentExpiresAtUnixSeconds is < 0 or >
            RestrictedStateFormat.MaximumUnixSeconds)
        {
            return LocatorRootResult.Fail(LocatorCodes.Invalid);
        }

        var now = timeProvider.GetUtcNow().ToUnixTimeSeconds();
        if (now is < 0 or > RestrictedStateFormat.MaximumUnixSeconds ||
            !StateRetentionRequirements.TryGetRequiredSentinelExpiry(
                now,
                dependentExpiresAtUnixSeconds,
                out var requiredExpiry))
        {
            return LocatorRootResult.Fail(LocatorCodes.Unavailable);
        }

        LocatorSelectionResult? read = null;
        try
        {
            read = await ReadSelectionAsync(access!, cancellationToken)
                .ConfigureAwait(false);
            for (var attempt = 0;
                 read.RequiresCleanup && attempt < ReconciliationAttempts;
                 attempt++)
            {
                var cleanupDebt = read.CleanupDebt!;
                var recovered = await CleanupDebtAndReadAsync(
                        access!,
                        cleanupDebt)
                    .ConfigureAwait(false);
                if (!recovered.Succeeded)
                {
                    ClearSelection(recovered);
                    return LocatorRootResult.Fail(recovered.Code);
                }

                if (recovered.IsAbsent)
                {
                    ClearSelection(recovered);
                    if (cleanupDebt.Mode !=
                        LocatorCleanupMode.GenerationZeroAbsenceAllowed)
                    {
                        return LocatorRootResult.Fail(
                            LocatorCodes.Unavailable);
                    }

                    return await InitializeWithRootAsync(
                            access!,
                            cleanupDebt.ExpectedRoot,
                            now,
                            requiredExpiry,
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                var observationCode = ValidateCleanupObservation(
                    cleanupDebt,
                    recovered);
                if (observationCode is not null)
                {
                    ClearSelection(recovered);
                    return LocatorRootResult.Fail(observationCode);
                }

                ClearSelection(read);
                read = recovered;
            }

            if (read.RequiresCleanup)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            if (!read.Succeeded)
            {
                return LocatorRootResult.Fail(read.Code);
            }

            if (read.IsAbsent)
            {
                return await InitializeAsync(
                        access!,
                        now,
                        requiredExpiry,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var selection = read.Selection!;
            var successorRequired = RequiresSuccessor(
                selection,
                requiredExpiry);
            if (!successorRequired)
            {
                if (selection.SafeToDelete.IsEmpty)
                {
                    return CreateContext(access!, selection.Head);
                }

                return await CleanupAndFinalizeAsync(
                        access!,
                        selection,
                        selection.Head.Sentinel.Root,
                        selection.Head.Sentinel.Generation,
                        requiredExpiry)
                    .ConfigureAwait(false);
            }

            if (selection.PhysicalCount ==
                LocatorRootFormat.MaximumPhysicalSentinels)
            {
                var expectedRoot = selection.Head.Sentinel.Root;
                var minimumGeneration = selection.Head.Sentinel.Generation;
                var cleaned = await CleanupAndReadAsync(
                        access!,
                        selection)
                    .ConfigureAwait(false);
                try
                {
                    if (!cleaned.Succeeded)
                    {
                        return LocatorRootResult.Fail(cleaned.Code);
                    }

                    if (cleaned.IsAbsent ||
                        cleaned.RequiresCleanup ||
                        cleaned.Selection is null)
                    {
                        return LocatorRootResult.Fail(
                            LocatorCodes.Unavailable);
                    }

                    selection = cleaned.Selection!;
                    if (!CryptographicOperations.FixedTimeEquals(
                            selection.Head.Sentinel.Root,
                            expectedRoot))
                    {
                        return LocatorRootResult.Fail(LocatorCodes.Conflict);
                    }

                    if (selection.Head.Sentinel.Generation <
                        minimumGeneration)
                    {
                        return LocatorRootResult.Fail(
                            LocatorCodes.Unavailable);
                    }

                    if (selection.PhysicalCount ==
                        LocatorRootFormat.MaximumPhysicalSentinels)
                    {
                        return LocatorRootResult.Fail(
                            LocatorCodes.CleanupFailed);
                    }

                    if (!RequiresSuccessor(selection, requiredExpiry))
                    {
                        if (selection.SafeToDelete.IsEmpty)
                        {
                            return CreateContext(access!, selection.Head);
                        }

                        return await CleanupAndFinalizeAsync(
                                access!,
                                selection,
                                selection.Head.Sentinel.Root,
                                selection.Head.Sentinel.Generation,
                                requiredExpiry)
                            .ConfigureAwait(false);
                    }

                    return await AppendSuccessorAsync(
                            access!,
                            selection,
                            now,
                            requiredExpiry,
                            cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    ClearSelection(cleaned);
                }
            }

            return await AppendSuccessorAsync(
                    access!,
                    selection,
                    now,
                    requiredExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return LocatorRootResult.Fail(LocatorCodes.Unavailable);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                InvalidOperationException or
                OverflowException or
                CryptographicException)
        {
            return LocatorRootResult.Fail(LocatorCodes.Unavailable);
        }
        finally
        {
            ClearSelection(read);
        }
    }

    private async Task<LocatorRootResult> InitializeAsync(
        AuthorizedLocatorAccess access,
        long now,
        long requiredExpiry,
        CancellationToken cancellationToken)
    {
        if (!keys.TryDeriveInitialRoot(access, out var root))
        {
            return LocatorRootResult.Fail(LocatorCodes.KeyUnavailable);
        }

        try
        {
            return await InitializeWithRootAsync(
                    access,
                    root,
                    now,
                    requiredExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(root);
        }
    }

    private async Task<LocatorRootResult> InitializeWithRootAsync(
        AuthorizedLocatorAccess access,
        ReadOnlyMemory<byte> root,
        long now,
        long requiredExpiry,
        CancellationToken cancellationToken)
    {
        var sentinel = new LocatorRootSentinel(
            root.ToArray(),
            Generation: 0,
            keys.CurrentKeyId,
            now,
            requiredExpiry,
            [],
            []);
        try
        {
            return await UploadAndConvergeAsync(
                    access,
                    sentinel,
                    requiredExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sentinel.Root);
        }
    }

    private async Task<LocatorRootResult> AppendSuccessorAsync(
        AuthorizedLocatorAccess access,
        LocatorSelection selection,
        long now,
        long requiredExpiry,
        CancellationToken cancellationToken)
    {
        if (selection.Head.Sentinel.Generation == ulong.MaxValue ||
            selection.PhysicalCount >=
                LocatorRootFormat.MaximumPhysicalSentinels)
        {
            return LocatorRootResult.Fail(LocatorCodes.Conflict);
        }

        var predecessor = LocatorRootSentinelCodec.Identity(
            selection.Head.Metadata);
        var superseded = selection.SafeToDelete
            .Select(LocatorRootSentinelCodec.Identity)
            .OrderBy(reference => reference.ObjectId, StringComparer.Ordinal)
            .ThenBy(reference => reference.ArchiveSha256, StringComparer.Ordinal)
            .ThenBy(reference => reference.EnvelopeSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var successor = new LocatorRootSentinel(
            selection.Head.Sentinel.Root.ToArray(),
            checked(selection.Head.Sentinel.Generation + 1),
            keys.CurrentKeyId,
            now,
            requiredExpiry,
            [predecessor],
            superseded);
        try
        {
            return await UploadAndConvergeAsync(
                    access,
                    successor,
                    requiredExpiry,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(successor.Root);
        }
    }

    private async Task<LocatorRootResult> UploadAndConvergeAsync(
        AuthorizedLocatorAccess access,
        LocatorRootSentinel target,
        long requiredExpiry,
        CancellationToken cancellationToken)
    {
        if (!LocatorRootSentinelCodec.TryEncrypt(
                access,
                keys,
                target,
                out var envelope,
                out var code) ||
            envelope is null)
        {
            return LocatorRootResult.Fail(code);
        }

        try
        {
            var digest = OpaqueStoreHash.Sha256(envelope);
            var encryptedDigest = new OpaqueStoreEncryptedObjectDigest(
                digest);
            var correlation = LocatorCryptography.CorrelationId(
                Convert.FromHexString(digest));
            var upload = await store.UploadImmutableAsync(
                    new OpaqueStoreUploadRequest(
                        SentinelName,
                        new OpaqueStoreCorrelationId(correlation),
                        envelope,
                        encryptedDigest,
                        requiredExpiry),
                    cancellationToken)
                .ConfigureAwait(false);
            if (!upload.Succeeded &&
                upload.MutationState ==
                    OpaqueStoreMutationState.NotCommitted)
            {
                return LocatorRootResult.Fail(
                    MapStoreFailure(upload.Failure));
            }

            if (upload.Metadata is null)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            if (!OpaqueStoreValidation.IsValid(upload.Metadata) ||
                upload.Metadata.Reference.Name != SentinelName ||
                upload.Metadata.EncryptedObjectDigest != encryptedDigest ||
                upload.Metadata.Size != envelope.Length)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            if (upload.Metadata.ExpiresAtUnixSeconds <
                target.RequiredExpiresAtUnixSeconds)
            {
                await DeleteRejectedUploadAsync(upload.Metadata)
                    .ConfigureAwait(false);
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            var token = CancellationToken.None;
            var uploadedReadBack = await ReadBackWithRetriesAsync(
                    upload.Metadata,
                    token)
                .ConfigureAwait(false);
            if (!uploadedReadBack.Succeeded ||
                uploadedReadBack.Metadata != upload.Metadata)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            var read = await ReadSelectionWithRetriesAsync(
                    access,
                    token,
                    target,
                    upload.Metadata)
                .ConfigureAwait(false);
            try
            {
                if (!read.Succeeded || read.IsAbsent)
                {
                    return LocatorRootResult.Fail(read.Code);
                }

                var selection = read.Selection!;
                if (!SameRoot(target, selection.Head.Sentinel) ||
                    selection.Head.Sentinel.Generation < target.Generation ||
                    (selection.Head.Sentinel.Generation ==
                            target.Generation &&
                        !LocatorRootSelection.Equivalent(
                            target,
                            selection.Head.Sentinel)) ||
                    !StringComparer.Ordinal.Equals(
                        selection.Head.Sentinel.WriterKeyId,
                        keys.CurrentKeyId) ||
                    !IsAdequatelyRetained(selection.Head, requiredExpiry))
                {
                    return LocatorRootResult.Fail(LocatorCodes.Conflict);
                }

                return await CleanupAndFinalizeAsync(
                        access,
                        selection,
                        target.Root,
                        target.Generation,
                        requiredExpiry)
                    .ConfigureAwait(false);
            }
            finally
            {
                ClearSelection(read);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(envelope);
        }
    }

    private async Task<LocatorRootResult> CleanupAndFinalizeAsync(
        AuthorizedLocatorAccess access,
        LocatorSelection selection,
        ReadOnlyMemory<byte> expectedRoot,
        ulong minimumGeneration,
        long requiredExpiry)
    {
        LocatorSelectionResult? ownedRead = null;
        try
        {
            var read = selection.SafeToDelete.IsEmpty
                ? LocatorSelectionResult.Success(selection)
                : ownedRead = await CleanupAndReadAsync(access, selection)
                    .ConfigureAwait(false);
            if (!read.Succeeded)
            {
                return LocatorRootResult.Fail(read.Code);
            }

            if (read.IsAbsent ||
                read.RequiresCleanup ||
                read.Selection is null)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            var final = read.Selection!;
            if (final.PhysicalCount != 1 ||
                !final.SafeToDelete.IsEmpty ||
                final.Head.Sentinel.Generation < minimumGeneration ||
                !CryptographicOperations.FixedTimeEquals(
                    final.Head.Sentinel.Root,
                    expectedRoot.Span) ||
                !StringComparer.Ordinal.Equals(
                    final.Head.Sentinel.WriterKeyId,
                    keys.CurrentKeyId) ||
                !IsAdequatelyRetained(final.Head, requiredExpiry))
            {
                return LocatorRootResult.Fail(LocatorCodes.CleanupFailed);
            }

            var readBack = await store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(final.Head.Metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (!readBack.Succeeded ||
                readBack.Metadata != final.Head.Metadata ||
                readBack.Metadata.ExpiresAtUnixSeconds <
                    final.Head.Sentinel.RequiredExpiresAtUnixSeconds ||
                readBack.Metadata.ExpiresAtUnixSeconds < requiredExpiry)
            {
                return LocatorRootResult.Fail(LocatorCodes.Unavailable);
            }

            return CreateContext(access, final.Head);
        }
        finally
        {
            ClearSelection(ownedRead);
        }
    }

    private async Task<LocatorSelectionResult> CleanupAndReadAsync(
        AuthorizedLocatorAccess access,
        LocatorSelection selection)
    {
        LocatorSelectionResult? ownedRead = null;
        var current = selection;
        var pruningChainAnchors = false;
        try
        {
            foreach (var stage in selection.CleanupStages)
            {
                if (stage.Kind == LocatorCleanupStageKind.ChainAnchor)
                {
                    pruningChainAnchors = true;
                }
                else if (pruningChainAnchors)
                {
                    ClearSelection(ownedRead);
                    return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
                }

                if (current.Head.Metadata == stage.Target)
                {
                    ClearSelection(ownedRead);
                    return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
                }

                if (!current.SafeToDelete.Contains(stage.Target))
                {
                    continue;
                }

                if (stage.Kind == LocatorCleanupStageKind.ChainAnchor &&
                    current.CleanupStages.Any(candidate =>
                        candidate.Kind == LocatorCleanupStageKind.NonAnchor))
                {
                    ClearSelection(ownedRead);
                    return LocatorSelectionResult.Fail(
                        LocatorCodes.CleanupFailed);
                }

                _ = await store.DeleteExactAsync(
                        new OpaqueStoreDeleteRequest(stage.Target),
                        CancellationToken.None)
                    .ConfigureAwait(false);

                var observed = await ReadSelectionWithRetriesAsync(
                        access,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                ClearSelection(ownedRead);
                ownedRead = null;
                if (!observed.Succeeded ||
                    observed.IsAbsent ||
                    observed.RequiresCleanup ||
                    observed.Selection is null)
                {
                    return observed;
                }

                if (observed.Selection.Head.Metadata == stage.Target ||
                    observed.Selection.SafeToDelete.Contains(stage.Target))
                {
                    ClearSelection(observed);
                    return LocatorSelectionResult.Fail(
                        LocatorCodes.CleanupFailed);
                }

                ownedRead = observed;
                current = observed.Selection;
            }

            return ownedRead ?? LocatorSelectionResult.Success(selection);
        }
        catch
        {
            ClearSelection(ownedRead);
            throw;
        }
    }

    private async Task<LocatorSelectionResult> CleanupDebtAndReadAsync(
        AuthorizedLocatorAccess access,
        LocatorCleanupDebt cleanupDebt)
    {
        foreach (var metadata in cleanupDebt.Objects)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }

        return await ReadSelectionWithRetriesAsync(
                access,
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private async Task<LocatorSelectionResult>
        ReadSelectionWithRetriesAsync(
            AuthorizedLocatorAccess access,
            CancellationToken cancellationToken,
            LocatorRootSentinel? target = null,
            OpaqueStoreObjectMetadata? targetMetadata = null)
    {
        LocatorSelectionResult? last = null;
        for (var attempt = 0; attempt < ReconciliationAttempts; attempt++)
        {
            var current = await ReadSelectionAsync(
                    access,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.Succeeded && !current.IsAbsent)
            {
                if (current.RequiresCleanup)
                {
                    if (target is null)
                    {
                        return current;
                    }

                    ClearSelection(current);
                    return LocatorSelectionResult.Fail(
                        LocatorCodes.Unavailable);
                }

                if (target is null)
                {
                    return current;
                }

                var selection = current.Selection!;
                var head = selection.Head.Sentinel;
                if (!SameRoot(target, head))
                {
                    return current;
                }

                var targetVisible = targetMetadata is not null &&
                    (selection.Head.Metadata == targetMetadata ||
                        selection.SafeToDelete.Contains(targetMetadata));
                if (!targetVisible)
                {
                    ClearSelection(current);
                    last = null;
                    continue;
                }

                if (head.Generation > target.Generation ||
                    (head.Generation == target.Generation &&
                        LocatorRootSelection.Equivalent(target, head)))
                {
                    return current;
                }

                if (head.Generation == target.Generation)
                {
                    ClearSelection(current);
                    return LocatorSelectionResult.Fail(
                        LocatorCodes.Conflict);
                }

                ClearSelection(current);
                last = null;
                continue;
            }

            last = current;
            if (!current.Succeeded &&
                current.Code is LocatorCodes.Conflict or
                    LocatorCodes.KeyUnavailable or
                    LocatorCodes.AuthenticationFailed)
            {
                return current;
            }
        }

        return target is null && last is not null
            ? last
            : LocatorSelectionResult.Fail(LocatorCodes.Unavailable);
    }

    private async Task<LocatorSelectionResult> ReadSelectionAsync(
        AuthorizedLocatorAccess access,
        CancellationToken cancellationToken)
    {
        var list = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    SentinelName,
                    LocatorRootFormat.MaximumPhysicalSentinels),
                cancellationToken)
            .ConfigureAwait(false);
        if (list.Failure == OpaqueStoreFailure.Incomplete ||
            !list.Complete ||
            (!list.Objects.IsDefault &&
                list.Objects.Length >
                    LocatorRootFormat.MaximumPhysicalSentinels))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        if (!list.Succeeded)
        {
            return LocatorSelectionResult.Fail(
                MapStoreFailure(list.Failure));
        }

        if (list.Objects.Any(reference => reference.Name != SentinelName))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var authenticated = ImmutableArray.CreateBuilder<
            LocatorPhysicalCandidate>(list.Objects.Length);
        var unknown = ImmutableArray.CreateBuilder<LocatorUnknownArtifact>();
        try
        {
            var metadata = ImmutableArray.CreateBuilder<
                OpaqueStoreObjectMetadata>(list.Objects.Length);
            foreach (var reference in list.Objects.OrderBy(
                item => item.ObjectId.Value,
                StringComparer.Ordinal))
            {
                var metadataResult = await store.ReadMetadataAsync(
                        new OpaqueStoreMetadataRequest(reference),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!metadataResult.Succeeded ||
                    metadataResult.Metadata is null ||
                    metadataResult.Metadata.Reference != reference)
                {
                    return LocatorSelectionResult.Fail(
                        MapStoreFailure(metadataResult.Failure));
                }

                metadata.Add(metadataResult.Metadata);
            }

            foreach (var item in metadata)
            {
                var download = await store.DownloadAsync(
                        new OpaqueStoreDownloadRequest(
                            item,
                            LocatorRootFormat.MaximumEnvelopeBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!download.Succeeded || download.Metadata != item)
                {
                    if (download.Failure == OpaqueStoreFailure.Expired)
                    {
                        unknown.Add(new LocatorUnknownArtifact(
                            item,
                            LocatorCodes.Unavailable));
                        continue;
                    }

                    return LocatorSelectionResult.Fail(
                        MapStoreFailure(download.Failure));
                }

                if (LocatorRootSentinelCodec.TryDecrypt(
                        access,
                        keys,
                        download.EncryptedBytes.Span,
                        out var sentinel,
                        out var failureCode) &&
                    sentinel is not null)
                {
                    authenticated.Add(new LocatorPhysicalCandidate(
                        item,
                        sentinel));
                }
                else
                {
                    unknown.Add(new LocatorUnknownArtifact(
                        item,
                        failureCode));
                }
            }

            var selected = LocatorRootSelection.Select(
                authenticated.ToImmutable(),
                unknown.ToImmutable(),
                list.Objects.Length);
            if (!selected.Succeeded ||
                selected.IsAbsent ||
                selected.RequiresCleanup)
            {
                return selected;
            }

            var selection = selected.Selection!;
            var ownedHead = selection.Head with
            {
                Sentinel = selection.Head.Sentinel with
                {
                    Root = selection.Head.Sentinel.Root.ToArray(),
                },
            };
            return LocatorSelectionResult.Success(
                selection with { Head = ownedHead });
        }
        finally
        {
            foreach (var candidate in authenticated)
            {
                CryptographicOperations.ZeroMemory(
                    candidate.Sentinel.Root);
            }
        }
    }

    private LocatorRootResult CreateContext(
        AuthorizedLocatorAccess access,
        LocatorPhysicalCandidate candidate)
    {
        if (!LocatorContext.TryCreate(
                access,
                keys,
                candidate.Sentinel.Root,
                currentSingletonProven: true,
                timeProvider,
                out var context) ||
            context is null)
        {
            context?.Dispose();
            return LocatorRootResult.Fail(LocatorCodes.KeyUnavailable);
        }

        return LocatorRootResult.Success(context);
    }

    private static bool IsAdequatelyRetained(
        LocatorPhysicalCandidate candidate,
        long requiredExpiry) =>
        LocatorRootSelection.HasProvenAuthenticatedFloor(candidate) &&
        candidate.Sentinel.RequiredExpiresAtUnixSeconds >= requiredExpiry;

    private async Task<OpaqueStoreReadBackResult> ReadBackWithRetriesAsync(
        OpaqueStoreObjectMetadata metadata,
        CancellationToken cancellationToken)
    {
        OpaqueStoreReadBackResult? last = null;
        for (var attempt = 0; attempt < ReconciliationAttempts; attempt++)
        {
            last = await store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(metadata),
                    cancellationToken)
                .ConfigureAwait(false);
            if (last.Succeeded)
            {
                return last;
            }
        }

        return last ?? OpaqueStoreReadBackResult.Fail(
            OpaqueStoreFailure.OutcomeUnknown);
    }

    private async Task DeleteRejectedUploadAsync(
        OpaqueStoreObjectMetadata metadata)
    {
        for (var attempt = 0; attempt < ReconciliationAttempts; attempt++)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
            var list = await store.ListExactAsync(
                    new OpaqueStoreListRequest(
                        SentinelName,
                        LocatorRootFormat.MaximumPhysicalSentinels),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (list.Succeeded &&
                list.Complete &&
                !list.Objects.Contains(metadata.Reference))
            {
                return;
            }
        }
    }

    private static void ClearSelection(LocatorSelectionResult? result)
    {
        if (result?.Selection is not null)
        {
            CryptographicOperations.ZeroMemory(
                result.Selection.Head.Sentinel.Root);
        }

        if (result?.CleanupDebt is not null)
        {
            CryptographicOperations.ZeroMemory(
                result.CleanupDebt.ExpectedRoot);
        }
    }

    private bool RequiresSuccessor(
        LocatorSelection selection,
        long requiredExpiry) =>
        !StringComparer.Ordinal.Equals(
            selection.Head.Sentinel.WriterKeyId,
            keys.CurrentKeyId) ||
        !IsAdequatelyRetained(selection.Head, requiredExpiry);

    private static string? ValidateCleanupObservation(
        LocatorCleanupDebt expectation,
        LocatorSelectionResult observation)
    {
        if (observation.RequiresCleanup)
        {
            var nested = observation.CleanupDebt!;
            if (!CryptographicOperations.FixedTimeEquals(
                    nested.ExpectedRoot,
                    expectation.ExpectedRoot))
            {
                return LocatorCodes.Conflict;
            }

            return nested.MinimumGeneration < expectation.MinimumGeneration
                ? LocatorCodes.Unavailable
                : null;
        }

        if (observation.Selection is null)
        {
            return LocatorCodes.Unavailable;
        }

        if (!CryptographicOperations.FixedTimeEquals(
                observation.Selection.Head.Sentinel.Root,
                expectation.ExpectedRoot))
        {
            return LocatorCodes.Conflict;
        }

        return observation.Selection.Head.Sentinel.Generation <
            expectation.MinimumGeneration
            ? LocatorCodes.Unavailable
            : null;
    }

    private static bool SameRoot(
        LocatorRootSentinel left,
        LocatorRootSentinel right) =>
        CryptographicOperations.FixedTimeEquals(left.Root, right.Root);

    private static string MapStoreFailure(OpaqueStoreFailure failure) =>
        failure switch
        {
            OpaqueStoreFailure.Incomplete => LocatorCodes.Conflict,
            OpaqueStoreFailure.Conflict or
                OpaqueStoreFailure.Duplicate => LocatorCodes.Conflict,
            OpaqueStoreFailure.Cleanup => LocatorCodes.CleanupFailed,
            _ => LocatorCodes.Unavailable,
        };
}
