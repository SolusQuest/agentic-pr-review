using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

internal enum RestrictedStateTransactionCommitState
{
    ReadyForSelection,
}

internal sealed record RestrictedStateIndexedCandidate(
    RestrictedStateBinding Binding,
    string SessionSha256,
    string EnvelopeSha256,
    string ObjectIdentity,
    OpaqueStoreObjectMetadata Transport);

internal sealed record RestrictedStateTransactionIndex(
    RestrictedStateSnapshotVersion LogicalVersion,
    RestrictedStateSnapshotVersion PredecessorVersion,
    OpaqueStoreObjectMetadata? PredecessorIndex,
    string OperationIdentity,
    RestrictedStateTransactionCommitState CommitState,
    ImmutableArray<RestrictedStateIndexedCandidate> Accepted,
    RestrictedStateIndexedCandidate? Staging);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestrictedStateTransactionIndexDocument(
    string Kind,
    string CommitState,
    RestrictedStateSnapshotVersionDocument LogicalVersion,
    RestrictedStateSnapshotVersionDocument PredecessorVersion,
    OpaqueStoreMetadataDocument? PredecessorIndex,
    string OperationIdentity,
    RestrictedStateIndexedCandidateDocument[] Accepted,
    RestrictedStateIndexedCandidateDocument? Staging);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestrictedStateSnapshotVersionDocument(
    string Sha256,
    bool Exists);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestrictedStateIndexedCandidateDocument(
    RestrictedStateBindingDocument Binding,
    string SessionSha256,
    string EnvelopeSha256,
    string ObjectIdentity,
    OpaqueStoreMetadataDocument Transport);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestrictedStateBindingDocument(
    RestrictedStateScopeDocument Scope,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    long AcceptedAtUnixSeconds,
    long ExpiresAtUnixSeconds);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record RestrictedStateScopeDocument(
    string RepositoryId,
    string WorkflowIdentity,
    long ReviewTarget,
    string SessionId,
    string ProviderId,
    string ModelId,
    string AdapterId,
    string PolicySha256,
    string LimitsSha256,
    string ToolsetSha256,
    string BuildId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record OpaqueStoreMetadataDocument(
    string Name,
    string ObjectId,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    string ArchiveSha256,
    string EncryptedObjectSha256,
    long ExpiresAtUnixSeconds,
    long Size);

[JsonSerializable(typeof(RestrictedStateTransactionIndexDocument))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    PropertyNameCaseInsensitive = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Default)]
internal sealed partial class RestrictedStateTransactionIndexJsonContext
    : JsonSerializerContext;

internal static class RestrictedStateTransactionIndexCodec
{
    internal const string Kind =
        "agentic-pr-review/r3-restricted-state-transaction-index";
    internal const string ReadyForSelection = "ready_for_selection";
    internal const int MaximumPlaintextBytes = 256 * 1024;

    internal static bool TryWrite(
        RestrictedStateTransactionIndex value,
        out byte[] bytes)
    {
        bytes = [];
        if (!IsValid(value))
        {
            return false;
        }

        try
        {
            var document = ToDocument(value);
            bytes = JsonSerializer.SerializeToUtf8Bytes(
                document,
                RestrictedStateTransactionIndexJsonContext.Default
                    .RestrictedStateTransactionIndexDocument);
            return bytes.Length is > 0 and <= MaximumPlaintextBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                NotSupportedException or
                JsonException or
                EncoderFallbackException or
                OverflowException)
        {
            bytes = [];
            return false;
        }
    }

    internal static bool TryRead(
        ReadOnlySpan<byte> bytes,
        out RestrictedStateTransactionIndex? value)
    {
        value = null;
        if (bytes.Length is < 1 or > MaximumPlaintextBytes ||
            bytes.StartsWith(Encoding.UTF8.Preamble))
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                bytes,
                RestrictedStateTransactionIndexJsonContext.Default
                    .RestrictedStateTransactionIndexDocument);
            if (document is null ||
                !StringComparer.Ordinal.Equals(document.Kind, Kind) ||
                !StringComparer.Ordinal.Equals(
                    document.CommitState,
                    ReadyForSelection) ||
                !TryMap(document, out var mapped) ||
                !IsValid(mapped!))
            {
                return false;
            }

            var canonical = JsonSerializer.SerializeToUtf8Bytes(
                document,
                RestrictedStateTransactionIndexJsonContext.Default
                    .RestrictedStateTransactionIndexDocument);
            if (!bytes.SequenceEqual(canonical))
            {
                return false;
            }

            value = mapped;
            return true;
        }
        catch (Exception exception) when (
            exception is JsonException or
                NotSupportedException or
                EncoderFallbackException or
                ArgumentException or
                OverflowException)
        {
            return false;
        }
    }

    internal static bool IsValid(RestrictedStateTransactionIndex? value)
    {
        if (value is null ||
            value.Accepted.IsDefault ||
            value.Accepted.Length > AgentLimits.AcceptedCandidates ||
            value.CommitState !=
                RestrictedStateTransactionCommitState.ReadyForSelection ||
            !IsValid(value.LogicalVersion) ||
            !IsValid(value.PredecessorVersion) ||
            !OpaqueStoreValidation.IsValid(
                new OpaqueStoreCorrelationId(value.OperationIdentity)) ||
            (value.PredecessorVersion.Exists !=
                (value.PredecessorIndex is not null)) ||
            (value.PredecessorIndex is not null &&
                !OpaqueStoreValidation.IsValid(value.PredecessorIndex)) ||
            (value.Staging is not null && !IsValid(value.Staging)))
        {
            return false;
        }

        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in value.Accepted)
        {
            if (!IsValid(candidate) ||
                !identities.Add(candidate.Transport.Reference.ObjectId.Value))
            {
                return false;
            }
        }

        return value.Staging is null ||
            identities.Add(value.Staging.Transport.Reference.ObjectId.Value);
    }

    private static bool IsValid(RestrictedStateSnapshotVersion? value) =>
        value is not null &&
        ((!value.Exists && value.Sha256.Length == 0) ||
            (value.Exists &&
                RestrictedStateValidation.IsLowerHex(value.Sha256, 64)));

    private static bool IsValid(RestrictedStateIndexedCandidate value) =>
        value is not null &&
        RestrictedStateValidation.IsValidBinding(value.Binding) &&
        RestrictedStateValidation.IsLowerHex(value.SessionSha256, 64) &&
        RestrictedStateValidation.IsLowerHex(value.EnvelopeSha256, 64) &&
        RestrictedStateValidation.IsLowerHex(value.ObjectIdentity, 64) &&
        OpaqueStoreValidation.IsValid(value.Transport);

    private static RestrictedStateTransactionIndexDocument ToDocument(
        RestrictedStateTransactionIndex value) =>
        new(
            Kind,
            ReadyForSelection,
            ToDocument(value.LogicalVersion),
            ToDocument(value.PredecessorVersion),
            value.PredecessorIndex is null
                ? null
                : ToDocument(value.PredecessorIndex),
            value.OperationIdentity,
            value.Accepted.Select(ToDocument).ToArray(),
            value.Staging is null ? null : ToDocument(value.Staging));

    private static RestrictedStateSnapshotVersionDocument ToDocument(
        RestrictedStateSnapshotVersion value) =>
        new(value.Sha256, value.Exists);

    private static RestrictedStateIndexedCandidateDocument ToDocument(
        RestrictedStateIndexedCandidate value) =>
        new(
            ToDocument(value.Binding),
            value.SessionSha256,
            value.EnvelopeSha256,
            value.ObjectIdentity,
            ToDocument(value.Transport));

    private static RestrictedStateBindingDocument ToDocument(
        RestrictedStateBinding value) =>
        new(
            ToDocument(value.Scope),
            value.ProducerBaseSha,
            value.ProducerHeadSha,
            value.Generation,
            value.PredecessorEnvelopeSha256,
            value.AcceptedAtUnixSeconds,
            value.ExpiresAtUnixSeconds);

    private static RestrictedStateScopeDocument ToDocument(
        RestrictedStateScope value) =>
        new(
            value.RepositoryId,
            value.WorkflowIdentity,
            value.ReviewTarget,
            value.SessionId,
            value.ProviderId,
            value.ModelId,
            value.AdapterId,
            value.PolicySha256,
            value.LimitsSha256,
            value.ToolsetSha256,
            value.BuildId);

    private static OpaqueStoreMetadataDocument ToDocument(
        OpaqueStoreObjectMetadata value) =>
        new(
            value.Reference.Name.Value,
            value.Reference.ObjectId.Value,
            value.ProducingRun.Identity,
            value.ProducingRun.Attempt,
            value.ArchiveDigest.Sha256,
            value.EncryptedObjectDigest.Sha256,
            value.ExpiresAtUnixSeconds,
            value.Size);

    private static bool TryMap(
        RestrictedStateTransactionIndexDocument document,
        out RestrictedStateTransactionIndex? value)
    {
        value = null;
        if (document.LogicalVersion is null ||
            document.PredecessorVersion is null ||
            document.Accepted is null ||
            document.Accepted.Length > AgentLimits.AcceptedCandidates ||
            !TryMap(document.LogicalVersion, out var logicalVersion) ||
            !TryMap(document.PredecessorVersion, out var predecessorVersion) ||
            !TryMap(document.PredecessorIndex, out var predecessorIndex))
        {
            return false;
        }

        var accepted = ImmutableArray.CreateBuilder<
            RestrictedStateIndexedCandidate>(document.Accepted.Length);
        foreach (var candidate in document.Accepted)
        {
            if (!TryMap(candidate, out var mapped))
            {
                return false;
            }

            accepted.Add(mapped!);
        }

        RestrictedStateIndexedCandidate? staging = null;
        if (document.Staging is not null &&
            !TryMap(document.Staging, out staging))
        {
            return false;
        }

        value = new RestrictedStateTransactionIndex(
            logicalVersion!,
            predecessorVersion!,
            predecessorIndex,
            document.OperationIdentity,
            RestrictedStateTransactionCommitState.ReadyForSelection,
            accepted.MoveToImmutable(),
            staging);
        return true;
    }

    private static bool TryMap(
        RestrictedStateSnapshotVersionDocument document,
        out RestrictedStateSnapshotVersion? value)
    {
        value = document is null
            ? null
            : new RestrictedStateSnapshotVersion(
                document.Sha256,
                document.Exists);
        return IsValid(value);
    }

    private static bool TryMap(
        RestrictedStateIndexedCandidateDocument document,
        out RestrictedStateIndexedCandidate? value)
    {
        value = null;
        if (document is null ||
            !TryMap(document.Binding, out var binding) ||
            !TryMap(document.Transport, out var transport))
        {
            return false;
        }

        var candidate = new RestrictedStateIndexedCandidate(
            binding!,
            document.SessionSha256,
            document.EnvelopeSha256,
            document.ObjectIdentity,
            transport!);
        if (!IsValid(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    private static bool TryMap(
        RestrictedStateBindingDocument document,
        out RestrictedStateBinding? value)
    {
        value = null;
        if (document is null || !TryMap(document.Scope, out var scope))
        {
            return false;
        }

        var binding = new RestrictedStateBinding(
            scope!,
            document.ProducerBaseSha,
            document.ProducerHeadSha,
            document.Generation,
            document.PredecessorEnvelopeSha256,
            document.AcceptedAtUnixSeconds,
            document.ExpiresAtUnixSeconds);
        if (!RestrictedStateValidation.IsValidBinding(binding))
        {
            return false;
        }

        value = binding;
        return true;
    }

    private static bool TryMap(
        RestrictedStateScopeDocument document,
        out RestrictedStateScope? value)
    {
        value = null;
        if (document is null)
        {
            return false;
        }

        var scope = new RestrictedStateScope(
            document.RepositoryId,
            document.WorkflowIdentity,
            document.ReviewTarget,
            document.SessionId,
            document.ProviderId,
            document.ModelId,
            document.AdapterId,
            document.PolicySha256,
            document.LimitsSha256,
            document.ToolsetSha256,
            document.BuildId);
        if (!RestrictedStateValidation.IsValidScope(scope))
        {
            return false;
        }

        value = scope;
        return true;
    }

    private static bool TryMap(
        OpaqueStoreMetadataDocument? document,
        out OpaqueStoreObjectMetadata? value)
    {
        value = null;
        if (document is null)
        {
            return true;
        }

        var metadata = new OpaqueStoreObjectMetadata(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(document.Name),
                new OpaqueStoreObjectId(document.ObjectId)),
            new OpaqueStoreProducingRun(
                document.ProducingRunIdentity,
                document.ProducingRunAttempt),
            new OpaqueStoreArchiveDigest(document.ArchiveSha256),
            new OpaqueStoreEncryptedObjectDigest(
                document.EncryptedObjectSha256),
            document.ExpiresAtUnixSeconds,
            document.Size);
        if (!OpaqueStoreValidation.IsValid(metadata))
        {
            return false;
        }

        value = metadata;
        return true;
    }
}

internal static class RestrictedStateTransactionIndexEnvelope
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("APRRTX01");
    private static readonly byte[] AadPrefix =
        Encoding.ASCII.GetBytes("APR-STATE-TRANSACTION-INDEX-AAD-1\0");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private const ushort Version = 1;
    private const ushort Algorithm = 1;

    internal static bool TryEncrypt(
        AuthorizedStateAccess access,
        ReadOnlySpan<byte> plaintext,
        long expiresAtUnixSeconds,
        IRestrictedStateKeyResolver keyResolver,
        out byte[]? envelope,
        out RestrictedStateStoreFailure failure)
    {
        envelope = null;
        failure = RestrictedStateStoreFailure.Invalid;
        if (access is null ||
            keyResolver is null ||
            !RestrictedStateValidation.IsValidScope(access.Scope) ||
            plaintext.Length is < 1 or >
                RestrictedStateTransactionIndexCodec.MaximumPlaintextBytes ||
            expiresAtUnixSeconds is < 1 or >
                RestrictedStateFormat.MaximumUnixSeconds)
        {
            return false;
        }

        if (!keyResolver.TryGetCurrentWriteKey(access, out var resolvedKey) ||
            resolvedKey is null)
        {
            resolvedKey?.Dispose();
            failure = RestrictedStateStoreFailure.KeyUnavailable;
            return false;
        }

        using (resolvedKey)
        {
            Span<byte> key = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!RestrictedStateValidation.IsValidKeyId(
                        resolvedKey.KeyId) ||
                    !resolvedKey.TryCopyMaterial(key))
                {
                    failure = RestrictedStateStoreFailure.KeyUnavailable;
                    return false;
                }

                var nonce = RandomNumberGenerator.GetBytes(
                    RestrictedStateFormat.NonceBytes);
                var header = WriteHeader(
                    resolvedKey.KeyId,
                    expiresAtUnixSeconds,
                    nonce,
                    checked((uint)plaintext.Length));
                var aad = BuildAad(header, access.Scope);
                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[RestrictedStateFormat.TagBytes];
                using (var aes = new AesGcm(
                    key,
                    RestrictedStateFormat.TagBytes))
                {
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
                }

                var writer = new ArrayBufferWriter<byte>(
                    checked(header.Length + ciphertext.Length + 18));
                Write(writer, header);
                Write(writer, ciphertext);
                WriteUInt16(writer, RestrictedStateFormat.TagBytes);
                Write(writer, tag);
                if (writer.WrittenCount > OpaqueStoreLimits.MaximumObjectBytes)
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                    return false;
                }

                envelope = writer.WrittenMemory.ToArray();
                failure = RestrictedStateStoreFailure.None;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    internal static bool TryDecrypt(
        AuthorizedStateAccess access,
        ReadOnlySpan<byte> envelope,
        IRestrictedStateKeyResolver keyResolver,
        out byte[]? plaintext,
        out long expiresAtUnixSeconds,
        out RestrictedStateStoreFailure failure)
    {
        plaintext = null;
        expiresAtUnixSeconds = 0;
        failure = RestrictedStateStoreFailure.Invalid;
        if (access is null ||
            keyResolver is null ||
            !TryParse(envelope, out var parsed))
        {
            return false;
        }

        expiresAtUnixSeconds = parsed!.ExpiresAtUnixSeconds;
        if (!keyResolver.TryGetApprovedReadKey(
                access,
                parsed.KeyId,
                expiresAtUnixSeconds,
                out var resolvedKey) ||
            resolvedKey is null ||
            !StringComparer.Ordinal.Equals(
                resolvedKey.KeyId,
                parsed.KeyId))
        {
            resolvedKey?.Dispose();
            failure = RestrictedStateStoreFailure.KeyUnavailable;
            return false;
        }

        using (resolvedKey)
        {
            Span<byte> key = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!resolvedKey.TryCopyMaterial(key))
                {
                    failure = RestrictedStateStoreFailure.KeyUnavailable;
                    return false;
                }

                var result = new byte[parsed.Ciphertext.Length];
                try
                {
                    using var aes = new AesGcm(
                        key,
                        RestrictedStateFormat.TagBytes);
                    aes.Decrypt(
                        parsed.Nonce,
                        parsed.Ciphertext,
                        parsed.Tag,
                        result,
                        BuildAad(parsed.Header, access.Scope));
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(result);
                    failure = RestrictedStateStoreFailure.Authentication;
                    return false;
                }

                plaintext = result;
                failure = RestrictedStateStoreFailure.None;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    private static bool TryParse(
        ReadOnlySpan<byte> envelope,
        out RestrictedStateParsedTransactionIndexEnvelope? parsed)
    {
        parsed = null;
        if (envelope.Length is < 1 or > OpaqueStoreLimits.MaximumObjectBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(envelope, ref offset, Magic.Length, out var magic) ||
            !magic.SequenceEqual(Magic) ||
            !TryReadUInt16(envelope, ref offset, out var version) ||
            version != Version ||
            !TryReadUInt16(envelope, ref offset, out var algorithm) ||
            algorithm != Algorithm ||
            !TryReadAscii(envelope, ref offset, 1, 64, out var keyId) ||
            !RestrictedStateValidation.IsValidKeyId(keyId!) ||
            !TryReadInt64(envelope, ref offset, out var expiresAt) ||
            expiresAt is < 1 or > RestrictedStateFormat.MaximumUnixSeconds ||
            !TryReadUInt16(envelope, ref offset, out var nonceLength) ||
            nonceLength != RestrictedStateFormat.NonceBytes ||
            !TryReadBytes(
                envelope,
                ref offset,
                nonceLength,
                out var nonce) ||
            !TryReadUInt32(envelope, ref offset, out var ciphertextLength) ||
            ciphertextLength is 0 or >
                RestrictedStateTransactionIndexCodec.MaximumPlaintextBytes)
        {
            return false;
        }

        var headerLength = offset;
        if (!TryReadBytes(
                envelope,
                ref offset,
                checked((int)ciphertextLength),
                out var ciphertext) ||
            !TryReadUInt16(envelope, ref offset, out var tagLength) ||
            tagLength != RestrictedStateFormat.TagBytes ||
            !TryReadBytes(envelope, ref offset, tagLength, out var tag) ||
            offset != envelope.Length)
        {
            return false;
        }

        parsed = new RestrictedStateParsedTransactionIndexEnvelope(
            keyId!,
            expiresAt,
            nonce.ToArray(),
            ciphertext.ToArray(),
            tag.ToArray(),
            envelope[..headerLength].ToArray());
        return true;
    }

    private static byte[] WriteHeader(
        string keyId,
        long expiresAtUnixSeconds,
        ReadOnlySpan<byte> nonce,
        uint ciphertextLength)
    {
        var writer = new ArrayBufferWriter<byte>(128);
        Write(writer, Magic);
        WriteUInt16(writer, Version);
        WriteUInt16(writer, Algorithm);
        WriteAscii(writer, keyId);
        WriteInt64(writer, expiresAtUnixSeconds);
        WriteUInt16(writer, checked((ushort)nonce.Length));
        Write(writer, nonce);
        WriteUInt32(writer, ciphertextLength);
        return writer.WrittenMemory.ToArray();
    }

    private static byte[] BuildAad(
        ReadOnlySpan<byte> header,
        RestrictedStateScope scope)
    {
        var scopeBytes = RestrictedStateSnapshotCodec.WriteScopeIdentity(scope);
        var writer = new ArrayBufferWriter<byte>(
            checked(AadPrefix.Length + header.Length + scopeBytes.Length));
        Write(writer, AadPrefix);
        Write(writer, header);
        Write(writer, scopeBytes);
        return writer.WrittenMemory.ToArray();
    }

    private static void WriteAscii(IBufferWriter<byte> writer, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteUInt16(IBufferWriter<byte> writer, ushort value)
    {
        var span = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        writer.Advance(sizeof(ushort));
    }

    private static void WriteUInt32(IBufferWriter<byte> writer, uint value)
    {
        var span = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        writer.Advance(sizeof(uint));
    }

    private static void WriteInt64(IBufferWriter<byte> writer, long value)
    {
        var span = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(span, value);
        writer.Advance(sizeof(long));
    }

    private static void Write(
        IBufferWriter<byte> writer,
        ReadOnlySpan<byte> value)
    {
        value.CopyTo(writer.GetSpan(value.Length));
        writer.Advance(value.Length);
    }

    private static bool TryReadUInt16(
        ReadOnlySpan<byte> source,
        ref int offset,
        out ushort value)
    {
        value = 0;
        if (source.Length - offset < sizeof(ushort))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
        offset += sizeof(ushort);
        return true;
    }

    private static bool TryReadUInt32(
        ReadOnlySpan<byte> source,
        ref int offset,
        out uint value)
    {
        value = 0;
        if (source.Length - offset < sizeof(uint))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(source[offset..]);
        offset += sizeof(uint);
        return true;
    }

    private static bool TryReadInt64(
        ReadOnlySpan<byte> source,
        ref int offset,
        out long value)
    {
        value = 0;
        if (source.Length - offset < sizeof(long))
        {
            return false;
        }

        value = BinaryPrimitives.ReadInt64LittleEndian(source[offset..]);
        offset += sizeof(long);
        return true;
    }

    private static bool TryReadBytes(
        ReadOnlySpan<byte> source,
        ref int offset,
        int length,
        out ReadOnlySpan<byte> value)
    {
        value = default;
        if (length < 0 || source.Length - offset < length)
        {
            return false;
        }

        value = source.Slice(offset, length);
        offset += length;
        return true;
    }

    private static bool TryReadAscii(
        ReadOnlySpan<byte> source,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(source, ref offset, out var length) ||
            length < minimum || length > maximum ||
            !TryReadBytes(source, ref offset, length, out var bytes))
        {
            return false;
        }

        foreach (var current in bytes)
        {
            if (current > 0x7f)
            {
                return false;
            }
        }

        value = StrictUtf8.GetString(bytes);
        return true;
    }
}

internal sealed record RestrictedStateParsedTransactionIndexEnvelope(
    string KeyId,
    long ExpiresAtUnixSeconds,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    byte[] Header);
