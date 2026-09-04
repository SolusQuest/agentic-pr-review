using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed record ScopedStateUploadResult(
    string Code,
    OpaqueStoreMutationState MutationState,
    OpaqueStoreObjectMetadata? ReturnedMetadata,
    StateReconciliationExactReadBack ExactReadBack)
{
    internal OpaqueStoreObjectMetadata? Metadata => ReturnedMetadata;

    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        MutationState != OpaqueStoreMutationState.NotCommitted &&
        ReturnedMetadata is not null &&
        ExactReadBack == StateReconciliationExactReadBack.Matched;

    internal static ScopedStateUploadResult Success(
        OpaqueStoreObjectMetadata metadata,
        OpaqueStoreMutationState mutationState) =>
        new(
            LineageCodes.Ready,
            mutationState,
            metadata,
            StateReconciliationExactReadBack.Matched);

    internal static ScopedStateUploadResult Fail(
        string code,
        OpaqueStoreMutationState mutationState,
        OpaqueStoreObjectMetadata? returnedMetadata = null,
        StateReconciliationExactReadBack exactReadBack =
            StateReconciliationExactReadBack.NotAvailable) =>
        new(code, mutationState, returnedMetadata, exactReadBack);
}

internal sealed class ScopedStateUploadProtocol
{
    private const int ReconciliationAttempts = 3;
    private readonly IRestrictedStateStore store;

    internal ScopedStateUploadProtocol(IRestrictedStateStore store)
    {
        this.store = store ?? throw new ArgumentNullException(nameof(store));
    }

    internal async Task<ScopedStateUploadResult> UploadAndReadBackAsync(
        OpaqueStoreName name,
        ReadOnlyMemory<byte> encryptedBytes,
        long requiredExpiresAtUnixSeconds,
        CancellationToken cancellationToken)
    {
        if (!OpaqueStoreValidation.IsValid(name) ||
            encryptedBytes.Length is < 1 or >
                LineageFormat.MaximumEnvelopeBytes ||
            !LineageValidation.IsTime(requiredExpiresAtUnixSeconds) ||
            requiredExpiresAtUnixSeconds == 0)
        {
            return ScopedStateUploadResult.Fail(
                LineageCodes.Invalid,
                OpaqueStoreMutationState.NotCommitted,
                exactReadBack:
                    StateReconciliationExactReadBack.NotApplicable);
        }

        var encryptedDigest = new OpaqueStoreEncryptedObjectDigest(
            OpaqueStoreHash.Sha256(encryptedBytes.Span));
        var upload = await store.UploadImmutableAsync(
                new OpaqueStoreUploadRequest(
                    name,
                    new OpaqueStoreCorrelationId(
                        LineageCryptography.CorrelationId(
                            encryptedBytes.Span)),
                    encryptedBytes,
                    encryptedDigest,
                    requiredExpiresAtUnixSeconds),
                cancellationToken)
            .ConfigureAwait(false);
        if (upload.MutationState == OpaqueStoreMutationState.NotCommitted)
        {
            return ScopedStateUploadResult.Fail(
                MapFailure(upload.Failure),
                OpaqueStoreMutationState.NotCommitted,
                exactReadBack:
                    StateReconciliationExactReadBack.NotApplicable);
        }

        var metadata = upload.Metadata;
        if (!OpaqueStoreValidation.IsValid(metadata) ||
            metadata!.Reference.Name != name ||
            metadata.EncryptedObjectDigest != encryptedDigest ||
            metadata.Size != encryptedBytes.Length)
        {
            // The adapter-returned descriptor is not request authority. Do not
            // read or delete through it; later complete enumeration reconciles.
            return ScopedStateUploadResult.Fail(
                LineageCodes.Unavailable,
                upload.MutationState);
        }

        OpaqueStoreReadBackResult? readBack = null;
        for (var attempt = 0; attempt < ReconciliationAttempts; attempt++)
        {
            readBack = await store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (readBack.Succeeded && readBack.Metadata == metadata)
            {
                break;
            }
        }

        if (readBack is null ||
            !readBack.Succeeded ||
            readBack.Metadata != metadata)
        {
            return ScopedStateUploadResult.Fail(
                LineageCodes.Unavailable,
                upload.MutationState,
                metadata,
                StateReconciliationExactReadBack.Failed);
        }

        if (metadata.ExpiresAtUnixSeconds < requiredExpiresAtUnixSeconds)
        {
            // Exact binding and readback above are prerequisites for cleanup.
            return ScopedStateUploadResult.Fail(
                await DeleteAndVerifyAbsentAsync(metadata).ConfigureAwait(false)
                    ? LineageCodes.RetentionFailed
                    : LineageCodes.CleanupFailed,
                upload.MutationState,
                metadata,
                StateReconciliationExactReadBack.Matched);
        }

        return ScopedStateUploadResult.Success(metadata, upload.MutationState);
    }

    private async Task<bool> DeleteAndVerifyAbsentAsync(
        OpaqueStoreObjectMetadata target)
    {
        for (var attempt = 0; attempt < ReconciliationAttempts; attempt++)
        {
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(target),
                    CancellationToken.None)
                .ConfigureAwait(false);
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
                return true;
            }

            var metadata = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(target.Reference),
                    CancellationToken.None)
                .ConfigureAwait(false);
            if (metadata.Succeeded && metadata.Metadata != target)
            {
                return false;
            }
        }

        return false;
    }

    private static string MapFailure(OpaqueStoreFailure failure) =>
        failure switch
        {
            OpaqueStoreFailure.Conflict or OpaqueStoreFailure.Duplicate =>
                LineageCodes.Conflict,
            OpaqueStoreFailure.Cleanup => LineageCodes.CleanupFailed,
            _ => LineageCodes.Unavailable,
        };
}
