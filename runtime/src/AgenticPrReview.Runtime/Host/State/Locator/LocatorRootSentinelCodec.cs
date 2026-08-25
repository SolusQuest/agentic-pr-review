using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class LocatorRootSentinelCodec
{
    private static readonly byte[] PlaintextMagic =
        Encoding.ASCII.GetBytes(LocatorRootFormat.PlaintextMagic);
    private static readonly byte[] EnvelopeMagic =
        Encoding.ASCII.GetBytes(LocatorRootFormat.EnvelopeMagic);
    private static readonly byte[] AadPrefix =
        Encoding.ASCII.GetBytes(LocatorRootFormat.AadPrefix);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryEncrypt(
        AuthorizedLocatorAccess? access,
        LocatorStateKeyRing keys,
        LocatorRootSentinel sentinel,
        out byte[]? envelope,
        out string failureCode)
    {
        envelope = null;
        failureCode = LocatorCodes.Invalid;
        if (access is null ||
            !keys.Allows(access) ||
            !IsValid(sentinel) ||
            !StringComparer.Ordinal.Equals(
                sentinel.WriterKeyId,
                keys.CurrentKeyId) ||
            !access.TryGetRepositoryId(access, out var repositoryId) ||
            !TryWritePlaintext(sentinel, out var plaintext))
        {
            return false;
        }

        if (!keys.TryGetCurrent(access, out var resolved) ||
            resolved is null)
        {
            resolved?.Dispose();
            CryptographicOperations.ZeroMemory(plaintext);
            failureCode = LocatorCodes.KeyUnavailable;
            return false;
        }

        using (resolved)
        {
            Span<byte> key = stackalloc byte[LocatorRootFormat.KeyBytes];
            try
            {
                if (!resolved.TryCopyMaterial(key))
                {
                    failureCode = LocatorCodes.KeyUnavailable;
                    return false;
                }

                var nonce = RandomNumberGenerator.GetBytes(
                    LocatorRootFormat.NonceBytes);
                var header = WriteHeader(
                    resolved.KeyId,
                    nonce,
                    checked((uint)plaintext.Length));
                if (!TryBuildAad(header, repositoryId, out var aad))
                {
                    return false;
                }

                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[LocatorRootFormat.TagBytes];
                try
                {
                    using var aes = new AesGcm(
                        key,
                        LocatorRootFormat.TagBytes);
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

                    var writer = new ArrayBufferWriter<byte>(
                        checked(
                            header.Length +
                            ciphertext.Length +
                            tag.Length));
                    Write(writer, header);
                    Write(writer, ciphertext);
                    Write(writer, tag);
                    if (writer.WrittenCount >
                        LocatorRootFormat.MaximumEnvelopeBytes)
                    {
                        return false;
                    }

                    envelope = writer.WrittenMemory.ToArray();
                    failureCode = string.Empty;
                    return true;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException or
                    EncoderFallbackException or
                    FormatException or
                    OverflowException or
                    CryptographicException)
            {
                return false;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal static bool TryDecrypt(
        AuthorizedLocatorAccess? access,
        LocatorStateKeyRing keys,
        ReadOnlySpan<byte> envelope,
        out LocatorRootSentinel? sentinel,
        out string failureCode)
    {
        sentinel = null;
        failureCode = LocatorCodes.Invalid;
        if (access is null ||
            !keys.Allows(access) ||
            !access.TryGetRepositoryId(access, out var repositoryId) ||
            !TryParseEnvelope(envelope, out var parsed))
        {
            return false;
        }

        if (!keys.TryGetApprovedRead(
                access,
                parsed!.KeyId,
                out var resolved) ||
            resolved is null)
        {
            resolved?.Dispose();
            failureCode = LocatorCodes.KeyUnavailable;
            return false;
        }

        using (resolved)
        {
            Span<byte> key = stackalloc byte[LocatorRootFormat.KeyBytes];
            var plaintext = new byte[parsed.Ciphertext.Length];
            try
            {
                if (!resolved.TryCopyMaterial(key) ||
                    !TryBuildAad(
                        parsed.Header,
                        repositoryId,
                        out var aad))
                {
                    failureCode = LocatorCodes.KeyUnavailable;
                    return false;
                }

                try
                {
                    using var aes = new AesGcm(
                        key,
                        LocatorRootFormat.TagBytes);
                    aes.Decrypt(
                        parsed.Nonce,
                        parsed.Ciphertext,
                        parsed.Tag,
                        plaintext,
                        aad);
                }
                catch (CryptographicException)
                {
                    failureCode = LocatorCodes.AuthenticationFailed;
                    return false;
                }

                if (!TryReadPlaintext(plaintext, out var decoded) ||
                    decoded is null)
                {
                    failureCode = LocatorCodes.Invalid;
                    return false;
                }

                if (!StringComparer.Ordinal.Equals(
                        decoded.WriterKeyId,
                        parsed.KeyId))
                {
                    CryptographicOperations.ZeroMemory(decoded.Root);
                    failureCode = LocatorCodes.Invalid;
                    return false;
                }

                sentinel = decoded;
                failureCode = string.Empty;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
                CryptographicOperations.ZeroMemory(plaintext);
            }
        }
    }

    internal static bool IsValid(LocatorRootSentinel? sentinel)
    {
        if (sentinel is null ||
            sentinel.Root is not { Length: LocatorRootFormat.RootBytes } ||
            !IsLowerHex(sentinel.WriterKeyId) ||
            sentinel.CreatedAtUnixSeconds is < 0 or >
                RestrictedStateFormat.MaximumUnixSeconds ||
            sentinel.RequiredExpiresAtUnixSeconds is <= 0 or >
                RestrictedStateFormat.MaximumUnixSeconds ||
            sentinel.RequiredExpiresAtUnixSeconds <=
                sentinel.CreatedAtUnixSeconds ||
            !IsCanonicalReferences(sentinel.Predecessors) ||
            !IsCanonicalReferences(sentinel.Superseded))
        {
            return false;
        }

        if ((sentinel.Generation == 0 &&
                (!sentinel.Predecessors.IsEmpty ||
                    !sentinel.Superseded.IsEmpty)) ||
            (sentinel.Generation > 0 &&
                sentinel.Predecessors.Length != 1))
        {
            return false;
        }

        var predecessorIds = sentinel.Predecessors
            .Select(item => item.ObjectId);
        var supersededIds = sentinel.Superseded
            .Select(item => item.ObjectId);
        return !predecessorIds.Intersect(
                supersededIds,
                StringComparer.Ordinal)
            .Any();
    }

    internal static LocatorArtifactIdentity Identity(
        OpaqueStoreObjectMetadata metadata) =>
        new(
            metadata.Reference.ObjectId.Value,
            metadata.ArchiveDigest.Sha256,
            metadata.EncryptedObjectDigest.Sha256);

    internal static bool IdentityEquals(
        LocatorArtifactIdentity identity,
        OpaqueStoreObjectMetadata metadata) =>
        StringComparer.Ordinal.Equals(
            identity.ObjectId,
            metadata.Reference.ObjectId.Value) &&
        StringComparer.Ordinal.Equals(
            identity.ArchiveSha256,
            metadata.ArchiveDigest.Sha256) &&
        StringComparer.Ordinal.Equals(
            identity.EnvelopeSha256,
            metadata.EncryptedObjectDigest.Sha256);

    private static bool TryWritePlaintext(
        LocatorRootSentinel sentinel,
        out byte[] plaintext)
    {
        plaintext = [];
        if (!IsValid(sentinel))
        {
            return false;
        }

        try
        {
            var writer = new ArrayBufferWriter<byte>(512);
            Write(writer, PlaintextMagic);
            WriteUInt16(writer, LocatorRootFormat.Version);
            Write(writer, sentinel.Root);
            WriteUInt64(writer, sentinel.Generation);
            WriteHex(writer, sentinel.WriterKeyId);
            WriteInt64(writer, sentinel.CreatedAtUnixSeconds);
            WriteInt64(writer, sentinel.RequiredExpiresAtUnixSeconds);
            WriteReferences(writer, sentinel.Predecessors);
            WriteReferences(writer, sentinel.Superseded);
            if (writer.WrittenCount >
                LocatorRootFormat.MaximumEnvelopeBytes)
            {
                return false;
            }

            plaintext = writer.WrittenMemory.ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                EncoderFallbackException or
                FormatException or
                OverflowException)
        {
            return false;
        }
    }

    private static bool TryReadPlaintext(
        ReadOnlySpan<byte> plaintext,
        out LocatorRootSentinel? sentinel)
    {
        sentinel = null;
        var offset = 0;
        if (!TryReadBytes(
                plaintext,
                ref offset,
                PlaintextMagic.Length,
                out var magic) ||
            !magic.SequenceEqual(PlaintextMagic) ||
            !TryReadUInt16(plaintext, ref offset, out var version) ||
            version != LocatorRootFormat.Version ||
            !TryReadBytes(
                plaintext,
                ref offset,
                LocatorRootFormat.RootBytes,
                out var root) ||
            !TryReadUInt64(plaintext, ref offset, out var generation) ||
            !TryReadHex(plaintext, ref offset, out var writerKeyId) ||
            !TryReadInt64(plaintext, ref offset, out var createdAt) ||
            !TryReadInt64(plaintext, ref offset, out var requiredExpiry) ||
            !TryReadReferences(
                plaintext,
                ref offset,
                out var predecessors) ||
            !TryReadReferences(
                plaintext,
                ref offset,
                out var superseded) ||
            offset != plaintext.Length)
        {
            return false;
        }

        var decoded = new LocatorRootSentinel(
            root.ToArray(),
            generation,
            writerKeyId!,
            createdAt,
            requiredExpiry,
            predecessors,
            superseded);
        if (!IsValid(decoded))
        {
            CryptographicOperations.ZeroMemory(decoded.Root);
            return false;
        }

        sentinel = decoded;
        return true;
    }

    private static bool TryParseEnvelope(
        ReadOnlySpan<byte> envelope,
        out LocatorParsedEnvelope? parsed)
    {
        parsed = null;
        if (envelope.Length is < 1 or >
            LocatorRootFormat.MaximumEnvelopeBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(
                envelope,
                ref offset,
                EnvelopeMagic.Length,
                out var magic) ||
            !magic.SequenceEqual(EnvelopeMagic) ||
            !TryReadUInt16(envelope, ref offset, out var version) ||
            version != LocatorRootFormat.Version ||
            !TryReadUInt16(envelope, ref offset, out var algorithm) ||
            algorithm != LocatorRootFormat.Aes256GcmAlgorithm ||
            !TryReadHex(envelope, ref offset, out var keyId) ||
            !TryReadBytes(
                envelope,
                ref offset,
                LocatorRootFormat.NonceBytes,
                out var nonce) ||
            !TryReadUInt32(
                envelope,
                ref offset,
                out var ciphertextLength) ||
            ciphertextLength is 0 or >
                LocatorRootFormat.MaximumEnvelopeBytes)
        {
            return false;
        }

        var headerLength = offset;
        if (!TryReadBytes(
                envelope,
                ref offset,
                checked((int)ciphertextLength),
                out var ciphertext) ||
            !TryReadBytes(
                envelope,
                ref offset,
                LocatorRootFormat.TagBytes,
                out var tag) ||
            offset != envelope.Length)
        {
            return false;
        }

        parsed = new LocatorParsedEnvelope(
            keyId!,
            nonce.ToArray(),
            ciphertext.ToArray(),
            tag.ToArray(),
            envelope[..headerLength].ToArray());
        return true;
    }

    private static byte[] WriteHeader(
        string keyId,
        ReadOnlySpan<byte> nonce,
        uint ciphertextLength)
    {
        var writer = new ArrayBufferWriter<byte>(64);
        Write(writer, EnvelopeMagic);
        WriteUInt16(writer, LocatorRootFormat.Version);
        WriteUInt16(writer, LocatorRootFormat.Aes256GcmAlgorithm);
        WriteHex(writer, keyId);
        Write(writer, nonce);
        WriteUInt32(writer, ciphertextLength);
        return writer.WrittenMemory.ToArray();
    }

    private static bool TryBuildAad(
        ReadOnlySpan<byte> header,
        string repositoryId,
        out byte[] aad)
    {
        aad = [];
        try
        {
            var repository = StrictUtf8.GetBytes(repositoryId);
            var writer = new ArrayBufferWriter<byte>(
                checked(
                    AadPrefix.Length +
                    header.Length +
                    sizeof(ushort) +
                    repository.Length));
            Write(writer, AadPrefix);
            Write(writer, header);
            WriteUInt16(writer, checked((ushort)repository.Length));
            Write(writer, repository);
            CryptographicOperations.ZeroMemory(repository);
            aad = writer.WrittenMemory.ToArray();
            return true;
        }
        catch (Exception exception) when (
            exception is EncoderFallbackException or OverflowException)
        {
            return false;
        }
    }

    private static void WriteReferences(
        IBufferWriter<byte> writer,
        ImmutableArray<LocatorArtifactIdentity> references)
    {
        WriteUInt16(writer, checked((ushort)references.Length));
        foreach (var reference in references)
        {
            var objectId = StrictUtf8.GetBytes(reference.ObjectId);
            WriteUInt16(writer, checked((ushort)objectId.Length));
            Write(writer, objectId);
            WriteHex(writer, reference.ArchiveSha256);
            WriteHex(writer, reference.EnvelopeSha256);
        }
    }

    private static bool TryReadReferences(
        ReadOnlySpan<byte> source,
        ref int offset,
        out ImmutableArray<LocatorArtifactIdentity> references)
    {
        references = [];
        if (!TryReadUInt16(source, ref offset, out var count) ||
            count > LocatorRootFormat.MaximumReferences)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<
            LocatorArtifactIdentity>(count);
        for (var index = 0; index < count; index++)
        {
            if (!TryReadUInt16(source, ref offset, out var length) ||
                length is 0 or > OpaqueStoreLimits.MaximumIdentityBytes ||
                !TryReadBytes(
                    source,
                    ref offset,
                    length,
                    out var objectIdBytes) ||
                !TryDecodeUtf8(objectIdBytes, out var objectId) ||
                !TryReadHex(source, ref offset, out var archive) ||
                !TryReadHex(source, ref offset, out var envelope))
            {
                return false;
            }

            builder.Add(new LocatorArtifactIdentity(
                objectId!,
                archive!,
                envelope!));
        }

        references = builder.ToImmutable();
        return IsCanonicalReferences(references);
    }

    private static bool IsCanonicalReferences(
        ImmutableArray<LocatorArtifactIdentity> references)
    {
        if (references.IsDefault ||
            references.Length > LocatorRootFormat.MaximumReferences ||
            references.Any(reference =>
                !OpaqueStoreValidation.IsValid(
                    new OpaqueStoreObjectId(reference.ObjectId)) ||
                !IsLowerHex(reference.ArchiveSha256) ||
                !IsLowerHex(reference.EnvelopeSha256)))
        {
            return false;
        }

        for (var index = 1; index < references.Length; index++)
        {
            if (Compare(references[index - 1], references[index]) >= 0)
            {
                return false;
            }
        }

        return true;
    }

    private static int Compare(
        LocatorArtifactIdentity left,
        LocatorArtifactIdentity right)
    {
        var objectId = StringComparer.Ordinal.Compare(
            left.ObjectId,
            right.ObjectId);
        if (objectId != 0)
        {
            return objectId;
        }

        var archive = StringComparer.Ordinal.Compare(
            left.ArchiveSha256,
            right.ArchiveSha256);
        return archive != 0
            ? archive
            : StringComparer.Ordinal.Compare(
                left.EnvelopeSha256,
                right.EnvelopeSha256);
    }

    private static bool IsLowerHex(string? value)
    {
        if (value is not { Length: LocatorRootFormat.DigestBytes * 2 })
        {
            return false;
        }

        return value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');
    }

    private static bool TryDecodeUtf8(
        ReadOnlySpan<byte> bytes,
        out string? value)
    {
        value = null;
        try
        {
            value = StrictUtf8.GetString(bytes);
            return StrictUtf8.GetByteCount(value) == bytes.Length;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static void WriteHex(
        IBufferWriter<byte> writer,
        string value) =>
        Write(writer, Convert.FromHexString(value));

    private static void WriteUInt16(
        IBufferWriter<byte> writer,
        int value)
    {
        var destination = writer.GetSpan(sizeof(ushort));
        BinaryPrimitives.WriteUInt16LittleEndian(
            destination,
            checked((ushort)value));
        writer.Advance(sizeof(ushort));
    }

    private static void WriteUInt32(
        IBufferWriter<byte> writer,
        uint value)
    {
        var destination = writer.GetSpan(sizeof(uint));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value);
        writer.Advance(sizeof(uint));
    }

    private static void WriteUInt64(
        IBufferWriter<byte> writer,
        ulong value)
    {
        var destination = writer.GetSpan(sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
        writer.Advance(sizeof(ulong));
    }

    private static void WriteInt64(
        IBufferWriter<byte> writer,
        long value)
    {
        var destination = writer.GetSpan(sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(destination, value);
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

    private static bool TryReadUInt64(
        ReadOnlySpan<byte> source,
        ref int offset,
        out ulong value)
    {
        value = 0;
        if (source.Length - offset < sizeof(ulong))
        {
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(source[offset..]);
        offset += sizeof(ulong);
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

    private static bool TryReadHex(
        ReadOnlySpan<byte> source,
        ref int offset,
        out string? value)
    {
        value = null;
        if (!TryReadBytes(
                source,
                ref offset,
                LocatorRootFormat.DigestBytes,
                out var bytes))
        {
            return false;
        }

        value = Convert.ToHexStringLower(bytes);
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

    private sealed record LocatorParsedEnvelope(
        string KeyId,
        byte[] Nonce,
        byte[] Ciphertext,
        byte[] Tag,
        byte[] Header);
}
