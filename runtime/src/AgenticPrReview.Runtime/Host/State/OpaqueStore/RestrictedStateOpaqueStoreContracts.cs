using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace AgenticPrReview.Runtime.Host.State.OpaqueStore;

/// <summary>
/// Internal transport contract for bounded encrypted state objects. This is
/// neither a public ABI nor a user-loadable adapter surface.
/// </summary>
internal interface IRestrictedStateStore
{
    Task<OpaqueStoreListResult> ListExactAsync(
        OpaqueStoreListRequest request,
        CancellationToken cancellationToken);

    Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
        OpaqueStoreMetadataRequest request,
        CancellationToken cancellationToken);

    Task<OpaqueStoreDownloadResult> DownloadAsync(
        OpaqueStoreDownloadRequest request,
        CancellationToken cancellationToken);

    Task<OpaqueStoreUploadResult> UploadImmutableAsync(
        OpaqueStoreUploadRequest request,
        CancellationToken cancellationToken);

    Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
        OpaqueStoreReadBackRequest request,
        CancellationToken cancellationToken);

    Task<OpaqueStoreDeleteResult> DeleteExactAsync(
        OpaqueStoreDeleteRequest request,
        CancellationToken cancellationToken);
}

internal static class OpaqueStoreLimits
{
    internal const int MaximumNameBytes = 256;
    internal const int MaximumIdentityBytes = 256;
    internal const int MaximumCorrelationBytes = 256;
    internal const int MaximumObjects = 256;
    internal const int MaximumObjectBytes = 2 * 1024 * 1024;
}

internal enum OpaqueStoreFailure
{
    None,
    Cancelled,
    Invalid,
    NotFound,
    Incomplete,
    Duplicate,
    Conflict,
    Expired,
    DigestMismatch,
    OutcomeUnknown,
    Cleanup,
    Io,
}

internal enum OpaqueStoreMutationState
{
    NotCommitted,
    Committed,
    OutcomeUnknown,
}

internal sealed record OpaqueStoreName(string Value);

internal sealed record OpaqueStoreObjectId(string Value);

internal sealed record OpaqueStoreCorrelationId(string Value);

internal sealed record OpaqueStoreArchiveDigest(string Sha256);

internal sealed record OpaqueStoreEncryptedObjectDigest(string Sha256);

internal sealed record OpaqueStoreProducingRun(
    string Identity,
    long Attempt);

internal sealed record OpaqueStoreObjectReference(
    OpaqueStoreName Name,
    OpaqueStoreObjectId ObjectId);

internal sealed record OpaqueStoreObjectMetadata(
    OpaqueStoreObjectReference Reference,
    OpaqueStoreProducingRun ProducingRun,
    OpaqueStoreArchiveDigest ArchiveDigest,
    OpaqueStoreEncryptedObjectDigest EncryptedObjectDigest,
    long ExpiresAtUnixSeconds,
    long Size);

internal sealed record OpaqueStoreListRequest(
    OpaqueStoreName Name,
    int MaximumObjects);

internal sealed record OpaqueStoreMetadataRequest(
    OpaqueStoreObjectReference Reference);

internal sealed record OpaqueStoreDownloadRequest(
    OpaqueStoreObjectMetadata Expected,
    int MaximumBytes);

internal sealed record OpaqueStoreUploadRequest(
    OpaqueStoreName Name,
    OpaqueStoreCorrelationId CorrelationId,
    ReadOnlyMemory<byte> EncryptedBytes,
    OpaqueStoreEncryptedObjectDigest EncryptedObjectDigest,
    long MinimumExpiresAtUnixSeconds);

internal sealed record OpaqueStoreReadBackRequest(
    OpaqueStoreObjectMetadata Expected);

internal sealed record OpaqueStoreDeleteRequest(
    OpaqueStoreObjectMetadata Expected);

internal sealed record OpaqueStoreListResult(
    OpaqueStoreFailure Failure,
    ImmutableArray<OpaqueStoreObjectReference> Objects,
    bool Complete)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        Complete &&
        !Objects.IsDefault &&
        Objects.Length <= OpaqueStoreLimits.MaximumObjects &&
        Objects.All(OpaqueStoreValidation.IsValid) &&
        Objects.Select(item => item.ObjectId.Value)
            .Distinct(StringComparer.Ordinal)
            .Count() == Objects.Length;

    internal static OpaqueStoreListResult Fail(OpaqueStoreFailure failure) =>
        new(failure, [], Complete: false);
}

internal sealed record OpaqueStoreMetadataResult(
    OpaqueStoreFailure Failure,
    OpaqueStoreObjectMetadata? Metadata)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        OpaqueStoreValidation.IsValid(Metadata);

    internal static OpaqueStoreMetadataResult Fail(
        OpaqueStoreFailure failure) =>
        new(failure, null);
}

internal sealed record OpaqueStoreDownloadResult(
    OpaqueStoreFailure Failure,
    OpaqueStoreObjectMetadata? Metadata,
    ReadOnlyMemory<byte> EncryptedBytes)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        OpaqueStoreValidation.IsValid(Metadata) &&
        EncryptedBytes.Length == Metadata!.Size &&
        EncryptedBytes.Length is > 0 and <=
            OpaqueStoreLimits.MaximumObjectBytes &&
        StringComparer.Ordinal.Equals(
            OpaqueStoreHash.Sha256(EncryptedBytes.Span),
            Metadata.EncryptedObjectDigest.Sha256);

    internal static OpaqueStoreDownloadResult Fail(
        OpaqueStoreFailure failure) =>
        new(failure, null, ReadOnlyMemory<byte>.Empty);
}

internal sealed record OpaqueStoreUploadResult(
    OpaqueStoreFailure Failure,
    OpaqueStoreMutationState MutationState,
    OpaqueStoreObjectMetadata? Metadata)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        MutationState == OpaqueStoreMutationState.Committed &&
        OpaqueStoreValidation.IsValid(Metadata);

    internal static OpaqueStoreUploadResult Fail(
        OpaqueStoreFailure failure,
        OpaqueStoreMutationState mutationState =
            OpaqueStoreMutationState.NotCommitted,
        OpaqueStoreObjectMetadata? metadata = null) =>
        new(failure, mutationState, metadata);
}

internal sealed record OpaqueStoreReadBackResult(
    OpaqueStoreFailure Failure,
    OpaqueStoreObjectMetadata? Metadata)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        OpaqueStoreValidation.IsValid(Metadata);

    internal static OpaqueStoreReadBackResult Fail(
        OpaqueStoreFailure failure) =>
        new(failure, null);
}

internal sealed record OpaqueStoreDeleteResult(
    OpaqueStoreFailure Failure,
    OpaqueStoreMutationState MutationState)
{
    internal bool Succeeded =>
        Failure == OpaqueStoreFailure.None &&
        MutationState == OpaqueStoreMutationState.Committed;

    internal static OpaqueStoreDeleteResult Fail(
        OpaqueStoreFailure failure,
        OpaqueStoreMutationState mutationState =
            OpaqueStoreMutationState.NotCommitted) =>
        new(failure, mutationState);
}

internal static class OpaqueStoreValidation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool IsValid(OpaqueStoreName? value) =>
        value is not null &&
        IsBoundedOpaque(value.Value, OpaqueStoreLimits.MaximumNameBytes);

    internal static bool IsValid(OpaqueStoreObjectId? value) =>
        value is not null &&
        IsBoundedOpaque(
            value.Value,
            OpaqueStoreLimits.MaximumIdentityBytes);

    internal static bool IsValid(OpaqueStoreCorrelationId? value) =>
        value is not null &&
        IsBoundedOpaque(
            value.Value,
            OpaqueStoreLimits.MaximumCorrelationBytes);

    internal static bool IsValid(OpaqueStoreArchiveDigest? value) =>
        value is not null && IsLowerHexSha256(value.Sha256);

    internal static bool IsValid(
        OpaqueStoreEncryptedObjectDigest? value) =>
        value is not null && IsLowerHexSha256(value.Sha256);

    internal static bool IsValid(OpaqueStoreProducingRun? value) =>
        value is not null &&
        IsBoundedOpaque(
            value.Identity,
            OpaqueStoreLimits.MaximumIdentityBytes) &&
        value.Attempt >= 0;

    internal static bool IsValid(OpaqueStoreObjectReference? value) =>
        value is not null &&
        IsValid(value.Name) &&
        IsValid(value.ObjectId);

    internal static bool IsValid(OpaqueStoreObjectMetadata? value) =>
        value is not null &&
        IsValid(value.Reference) &&
        IsValid(value.ProducingRun) &&
        IsValid(value.ArchiveDigest) &&
        IsValid(value.EncryptedObjectDigest) &&
        value.ExpiresAtUnixSeconds is > 0 and <=
            RestrictedStateFormat.MaximumUnixSeconds &&
        value.Size is > 0 and <= OpaqueStoreLimits.MaximumObjectBytes;

    internal static bool IsValid(OpaqueStoreListRequest? value) =>
        value is not null &&
        IsValid(value.Name) &&
        value.MaximumObjects is > 0 and <= OpaqueStoreLimits.MaximumObjects;

    internal static bool IsValid(OpaqueStoreMetadataRequest? value) =>
        value is not null && IsValid(value.Reference);

    internal static bool IsValid(OpaqueStoreDownloadRequest? value) =>
        value is not null &&
        IsValid(value.Expected) &&
        value.MaximumBytes is > 0 and <=
            OpaqueStoreLimits.MaximumObjectBytes &&
        value.Expected.Size <= value.MaximumBytes;

    internal static bool IsValid(OpaqueStoreUploadRequest? value) =>
        value is not null &&
        IsValid(value.Name) &&
        IsValid(value.CorrelationId) &&
        IsValid(value.EncryptedObjectDigest) &&
        value.EncryptedBytes.Length is > 0 and <=
            OpaqueStoreLimits.MaximumObjectBytes &&
        value.MinimumExpiresAtUnixSeconds is > 0 and <=
            RestrictedStateFormat.MaximumUnixSeconds;

    internal static bool IsValid(OpaqueStoreReadBackRequest? value) =>
        value is not null && IsValid(value.Expected);

    internal static bool IsValid(OpaqueStoreDeleteRequest? value) =>
        value is not null && IsValid(value.Expected);

    private static bool IsBoundedOpaque(string? value, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains('\r', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var length = StrictUtf8.GetByteCount(value);
            return length is > 0 && length <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    private static bool IsLowerHexSha256(string? value)
    {
        if (value is not { Length: 64 })
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and not (
                    >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}

internal static class OpaqueStoreHash
{
    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
