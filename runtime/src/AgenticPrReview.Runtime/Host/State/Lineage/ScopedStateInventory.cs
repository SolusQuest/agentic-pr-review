using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed record ScopedStateInventoryResult(
    string Code,
    ScopedStateInventorySnapshot? Snapshot)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        Snapshot is not null;

    internal static ScopedStateInventoryResult Success(
        ScopedStateInventorySnapshot snapshot) =>
        new(LineageCodes.Ready, snapshot);

    internal static ScopedStateInventoryResult Fail(string code) =>
        new(code, null);
}

internal sealed class ScopedStateInventory
{
    private readonly IRestrictedStateStore store;

    internal ScopedStateInventory(IRestrictedStateStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal async Task<ScopedStateInventoryResult> ReadAsync(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        LineageBaseScope scope,
        string baseScopeDigest,
        CancellationToken cancellationToken)
    {
        if (!LineageBaseScopeCodec.TryEncode(scope, out var canonicalScope) ||
            !LineageValidation.IsSha256(baseScopeDigest))
        {
            return ScopedStateInventoryResult.Fail(LineageCodes.Invalid);
        }

        try
        {
            var names = ImmutableDictionary.CreateBuilder<
                StateObjectClass,
                OpaqueStoreName>();
            var references = ImmutableArray.CreateBuilder<(
                StateObjectClass ObjectClass,
                OpaqueStoreObjectMetadata Metadata)>();

            // Phase one is complete exact listing and metadata validation for
            // every class. No epoch/session derivation occurs in this method.
            foreach (var objectClass in StateObjectClasses.Scoped)
            {
                if (!context.TryDeriveOpaqueName(
                        access,
                        StateObjectClasses.ToWireName(objectClass),
                        canonicalScope,
                        out var name) ||
                    name is null)
                {
                    return ScopedStateInventoryResult.Fail(
                        LineageCodes.AccessDenied);
                }

                names.Add(objectClass, name);
                var list = await store.ListExactAsync(
                        new OpaqueStoreListRequest(
                            name,
                            LineageFormat.MaximumPhysicalPerClass),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (list.Failure == OpaqueStoreFailure.Incomplete ||
                    !list.Complete ||
                    (!list.Objects.IsDefault &&
                        list.Objects.Length >
                            LineageFormat.MaximumPhysicalPerClass))
                {
                    return ScopedStateInventoryResult.Fail(
                        LineageCodes.Conflict);
                }

                if (!list.Succeeded ||
                    list.Objects.Any(reference => reference.Name != name))
                {
                    return ScopedStateInventoryResult.Fail(
                        MapFailure(list.Failure));
                }

                foreach (var reference in list.Objects.OrderBy(
                    value => value.ObjectId.Value,
                    StringComparer.Ordinal))
                {
                    var metadata = await store.ReadMetadataAsync(
                            new OpaqueStoreMetadataRequest(reference),
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!metadata.Succeeded ||
                        metadata.Metadata is null ||
                        metadata.Metadata.Reference != reference)
                    {
                        return ScopedStateInventoryResult.Fail(
                            MapFailure(metadata.Failure));
                    }

                    references.Add((objectClass, metadata.Metadata));
                    if (references.Count > LineageFormat.MaximumScopedObjects)
                    {
                        return ScopedStateInventoryResult.Fail(
                            LineageCodes.Conflict);
                    }
                }
            }

            // Phase two authenticates bounded envelopes only after the full
            // physical inventory is known.
            var authenticated = ImmutableArray.CreateBuilder<
                AuthenticatedStateObject>(references.Count);
            var underRetained = ImmutableArray.CreateBuilder<
                AuthenticatedStateObject>();
            var unknown = ImmutableArray.CreateBuilder<UnknownStateObject>();
            foreach (var item in references)
            {
                var download = await store.DownloadAsync(
                        new OpaqueStoreDownloadRequest(
                            item.Metadata,
                            LineageFormat.MaximumEnvelopeBytes),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!download.Succeeded || download.Metadata != item.Metadata)
                {
                    if (download.Failure == OpaqueStoreFailure.Expired)
                    {
                        unknown.Add(new UnknownStateObject(
                            item.Metadata,
                            LineageCodes.Unavailable));
                        continue;
                    }

                    return ScopedStateInventoryResult.Fail(
                        MapFailure(download.Failure));
                }

                if (!StateControlEnvelopeV1Codec.TryDecrypt(
                        context,
                        access,
                        item.Metadata.Reference.Name,
                        download.EncryptedBytes.Span,
                        out var header,
                        out var payload,
                        out var code) ||
                    header is null)
                {
                    CryptographicOperations.ZeroMemory(payload);
                    if (StringComparer.Ordinal.Equals(
                            code,
                            LineageCodes.KeyUnavailable))
                    {
                        unknown.Add(new UnknownStateObject(
                            item.Metadata,
                            code));
                        continue;
                    }

                    return ScopedStateInventoryResult.Fail(code);
                }

                if (!StringComparer.Ordinal.Equals(
                        header.BaseScopeDigest,
                        baseScopeDigest) ||
                    header.ObjectClass != item.ObjectClass)
                {
                    CryptographicOperations.ZeroMemory(payload);
                    return ScopedStateInventoryResult.Fail(
                        LineageCodes.AuthenticationFailed);
                }

                if (item.Metadata.ExpiresAtUnixSeconds <
                    header.RequiredPlatformExpiresAtUnixSeconds)
                {
                    underRetained.Add(new AuthenticatedStateObject(
                        item.Metadata,
                        header,
                        payload));
                    continue;
                }

                authenticated.Add(new AuthenticatedStateObject(
                    item.Metadata,
                    header,
                    payload));
            }

            return ScopedStateInventoryResult.Success(
                new ScopedStateInventorySnapshot(
                    names.ToImmutable(),
                    authenticated.ToImmutable(),
                    underRetained.ToImmutable(),
                    unknown.ToImmutable(),
                    references.Count));
        }
        catch (OperationCanceledException)
        {
            return ScopedStateInventoryResult.Fail(LineageCodes.Unavailable);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    internal static void Clear(ScopedStateInventorySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        foreach (var item in snapshot.Authenticated)
        {
            CryptographicOperations.ZeroMemory(item.Payload);
        }

        foreach (var item in snapshot.UnderRetained)
        {
            CryptographicOperations.ZeroMemory(item.Payload);
        }
    }

    private static string MapFailure(OpaqueStoreFailure failure) =>
        failure switch
        {
            OpaqueStoreFailure.Incomplete or
                OpaqueStoreFailure.Conflict or
                OpaqueStoreFailure.Duplicate => LineageCodes.Conflict,
            OpaqueStoreFailure.Cleanup => LineageCodes.CleanupFailed,
            _ => LineageCodes.Unavailable,
        };
}
