using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal sealed record ScopedStateUploadResult(
    string Code,
    OpaqueStoreObjectMetadata? Metadata)
{
    internal bool Succeeded =>
        StringComparer.Ordinal.Equals(Code, LineageCodes.Ready) &&
        Metadata is not null;

    internal static ScopedStateUploadResult Success(
        OpaqueStoreObjectMetadata metadata) =>
        new(LineageCodes.Ready, metadata);

    internal static ScopedStateUploadResult Fail(string code) =>
        new(code, null);
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
            return ScopedStateUploadResult.Fail(LineageCodes.Invalid);
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
                MapFailure(upload.Failure));
        }

        var metadata = upload.Metadata;
        if (!OpaqueStoreValidation.IsValid(metadata) ||
            metadata!.Reference.Name != name ||
            metadata.EncryptedObjectDigest != encryptedDigest ||
            metadata.Size != encryptedBytes.Length)
        {
            // The adapter-returned descriptor is not request authority. Do not
            // read or delete through it; later complete enumeration reconciles.
            return ScopedStateUploadResult.Fail(LineageCodes.Unavailable);
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
            return ScopedStateUploadResult.Fail(LineageCodes.Unavailable);
        }

        if (metadata.ExpiresAtUnixSeconds < requiredExpiresAtUnixSeconds)
        {
            // Exact binding and readback above are prerequisites for cleanup.
            _ = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return ScopedStateUploadResult.Fail(
                LineageCodes.RetentionFailed);
        }

        return ScopedStateUploadResult.Success(metadata);
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
