using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Agent.Core;

namespace AgenticPrReview.Runtime.Host.State;

internal sealed record RestrictedStateParsedEnvelope(
    string KeyId,
    byte[] Nonce,
    byte[] Ciphertext,
    byte[] Tag,
    byte[] Header);

internal static class RestrictedStateEnvelope
{
    private static readonly byte[] Magic =
        Encoding.ASCII.GetBytes(RestrictedStateFormat.Magic);
    private static readonly byte[] AadPrefix =
        Encoding.ASCII.GetBytes(RestrictedStateFormat.AadPrefix);
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    internal static bool TryEncrypt(
        AuthorizedStateAccess access,
        RestrictedStateBinding binding,
        ReadOnlySpan<byte> plaintext,
        IRestrictedStateKeyResolver keyResolver,
        out byte[]? envelope,
        out string failureCode)
    {
        envelope = null;
        failureCode = RestrictedStateCodes.EnvelopeInvalid;
        if (access is null ||
            binding.Scope != access.Scope ||
            !RestrictedStateValidation.IsValidBinding(binding) ||
            plaintext.Length is < 1 or > AgentLimits.SessionPlaintextBytes)
        {
            return false;
        }

        if (!keyResolver.TryGetCurrentWriteKey(
                access,
                out var resolvedKey) ||
            resolvedKey is null)
        {
            resolvedKey?.Dispose();
            failureCode = RestrictedStateCodes.KeyUnavailable;
            return false;
        }

        using (resolvedKey)
        {
            if (!RestrictedStateValidation.IsValidKeyId(resolvedKey.KeyId))
            {
                failureCode = RestrictedStateCodes.KeyUnavailable;
                return false;
            }

            Span<byte> key = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!resolvedKey.TryCopyMaterial(key))
                {
                    failureCode = RestrictedStateCodes.KeyUnavailable;
                    return false;
                }

                var nonce = RandomNumberGenerator.GetBytes(
                    RestrictedStateFormat.NonceBytes);
                var header = WriteHeader(
                    resolvedKey.KeyId,
                    nonce,
                    checked((uint)plaintext.Length));
                if (!TryBuildAad(
                        header,
                        binding,
                        out var aad))
                {
                    failureCode = RestrictedStateCodes.EnvelopeInvalid;
                    return false;
                }

                var ciphertext = new byte[plaintext.Length];
                var tag = new byte[RestrictedStateFormat.TagBytes];
                using (var aes = new AesGcm(
                    key,
                    RestrictedStateFormat.TagBytes))
                {
                    aes.Encrypt(
                        nonce,
                        plaintext,
                        ciphertext,
                        tag,
                        aad);
                }

                var writer = new ArrayBufferWriter<byte>(
                    checked(header.Length + ciphertext.Length + 18));
                Write(writer, header);
                Write(writer, ciphertext);
                WriteUInt16(writer, RestrictedStateFormat.TagBytes);
                Write(writer, tag);
                if (writer.WrittenCount > AgentLimits.StateEnvelopeBytes)
                {
                    CryptographicOperations.ZeroMemory(ciphertext);
                    failureCode = RestrictedStateCodes.EnvelopeInvalid;
                    return false;
                }

                envelope = writer.WrittenMemory.ToArray();
                failureCode = string.Empty;
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
        RestrictedStateBinding binding,
        ReadOnlySpan<byte> envelope,
        IRestrictedStateKeyResolver keyResolver,
        out byte[]? plaintext,
        out string failureCode)
    {
        plaintext = null;
        if (access is null ||
            binding.Scope != access.Scope ||
            !RestrictedStateValidation.IsValidBinding(binding) ||
            !TryParse(envelope, out var parsed))
        {
            failureCode = RestrictedStateCodes.EnvelopeInvalid;
            return false;
        }

        if (!keyResolver.TryGetApprovedReadKey(
                access,
                parsed!.KeyId,
                binding.ExpiresAtUnixSeconds,
                out var resolvedKey) ||
            resolvedKey is null ||
            !StringComparer.Ordinal.Equals(
                resolvedKey.KeyId,
                parsed.KeyId))
        {
            resolvedKey?.Dispose();
            failureCode = RestrictedStateCodes.KeyUnavailable;
            return false;
        }

        using (resolvedKey)
        {
            Span<byte> key = stackalloc byte[RestrictedStateFormat.KeyBytes];
            try
            {
                if (!resolvedKey.TryCopyMaterial(key))
                {
                    failureCode = RestrictedStateCodes.KeyUnavailable;
                    return false;
                }

                if (!TryBuildAad(
                        parsed.Header,
                        binding,
                        out var aad))
                {
                    failureCode = RestrictedStateCodes.EnvelopeInvalid;
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
                        aad);
                }
                catch (AuthenticationTagMismatchException)
                {
                    CryptographicOperations.ZeroMemory(result);
                    failureCode =
                        RestrictedStateCodes.AuthenticationFailed;
                    return false;
                }
                catch (CryptographicException)
                {
                    CryptographicOperations.ZeroMemory(result);
                    failureCode =
                        RestrictedStateCodes.AuthenticationFailed;
                    return false;
                }

                plaintext = result;
                failureCode = string.Empty;
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }
    }

    internal static bool TryParse(
        ReadOnlySpan<byte> envelope,
        out RestrictedStateParsedEnvelope? parsed)
    {
        parsed = null;
        if (envelope.Length is < 1 or > AgentLimits.StateEnvelopeBytes)
        {
            return false;
        }

        var offset = 0;
        if (!TryReadBytes(envelope, ref offset, Magic.Length, out var magic) ||
            !magic.SequenceEqual(Magic) ||
            !TryReadUInt16(envelope, ref offset, out var version) ||
            version != RestrictedStateFormat.Version ||
            !TryReadUInt16(envelope, ref offset, out var algorithm) ||
            algorithm != RestrictedStateFormat.Aes256GcmAlgorithm ||
            !TryReadUtf8(
                envelope,
                ref offset,
                1,
                128,
                out var stateNamespace) ||
            !StringComparer.Ordinal.Equals(
                stateNamespace,
                RestrictedStateFormat.Namespace) ||
            !TryReadUtf8(
                envelope,
                ref offset,
                1,
                64,
                out var discriminator) ||
            !StringComparer.Ordinal.Equals(
                discriminator,
                RestrictedStateFormat.Discriminator) ||
            !TryReadAscii(
                envelope,
                ref offset,
                1,
                64,
                out var keyId) ||
            keyId is null ||
            !RestrictedStateValidation.IsValidKeyId(keyId) ||
            !TryReadUInt16(envelope, ref offset, out var nonceLength) ||
            nonceLength != RestrictedStateFormat.NonceBytes ||
            !TryReadBytes(
                envelope,
                ref offset,
                nonceLength,
                out var nonce) ||
            !TryReadUInt32(
                envelope,
                ref offset,
                out var ciphertextLength) ||
            ciphertextLength is 0 or > AgentLimits.SessionPlaintextBytes)
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
            !TryReadBytes(
                envelope,
                ref offset,
                tagLength,
                out var tag) ||
            offset != envelope.Length)
        {
            return false;
        }

        parsed = new RestrictedStateParsedEnvelope(
            keyId!,
            nonce.ToArray(),
            ciphertext.ToArray(),
            tag.ToArray(),
            envelope[..headerLength].ToArray());
        return true;
    }

    internal static bool TryBuildAad(
        ReadOnlySpan<byte> header,
        RestrictedStateBinding binding,
        out byte[]? aad)
    {
        aad = null;
        if (!RestrictedStateValidation.IsValidBinding(binding) ||
            !TryWriteBinding(binding, out var bindingBytes) ||
            bindingBytes.Length > RestrictedStateFormat.MaximumBindingBytes)
        {
            return false;
        }

        var writer = new ArrayBufferWriter<byte>(
            checked(AadPrefix.Length + header.Length + bindingBytes.Length));
        Write(writer, AadPrefix);
        Write(writer, header);
        Write(writer, bindingBytes);
        aad = writer.WrittenMemory.ToArray();
        return true;
    }

    internal static bool TryBuildAadVector(
        string keyId,
        ReadOnlySpan<byte> nonce,
        uint ciphertextLength,
        RestrictedStateBinding binding,
        out byte[]? aad)
    {
        aad = null;
        if (!RestrictedStateValidation.IsValidKeyId(keyId) ||
            nonce.Length != RestrictedStateFormat.NonceBytes ||
            ciphertextLength is 0 or > AgentLimits.SessionPlaintextBytes)
        {
            return false;
        }

        var header = WriteHeader(keyId, nonce, ciphertextLength);
        return TryBuildAad(header, binding, out aad);
    }

    internal static string EnvelopeSha256(ReadOnlySpan<byte> envelope) =>
        AgentCanonical.HashDomain(
            AgentCanonical.StateEnvelopeDomain,
            envelope);

    internal static string ObjectIdentity(
        RestrictedStateScope scope,
        string envelopeSha256)
    {
        var binding = Encoding.UTF8.GetBytes(
            $"{scope.RepositoryId}\n{scope.WorkflowIdentity}\n" +
            $"{scope.ReviewTarget}\n{scope.SessionId}\n{envelopeSha256}");
        return AgentCanonical.HashDomain(
            "apr.state-object.r2",
            binding);
    }

    private static byte[] WriteHeader(
        string keyId,
        ReadOnlySpan<byte> nonce,
        uint ciphertextLength)
    {
        var writer = new ArrayBufferWriter<byte>(128);
        Write(writer, Magic);
        WriteUInt16(writer, RestrictedStateFormat.Version);
        WriteUInt16(writer, RestrictedStateFormat.Aes256GcmAlgorithm);
        WriteUtf8(writer, RestrictedStateFormat.Namespace);
        WriteUtf8(writer, RestrictedStateFormat.Discriminator);
        WriteAscii(writer, keyId);
        WriteUInt16(writer, checked((ushort)nonce.Length));
        Write(writer, nonce);
        WriteUInt32(writer, ciphertextLength);
        return writer.WrittenMemory.ToArray();
    }

    private static bool TryWriteBinding(
        RestrictedStateBinding binding,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            var writer = new ArrayBufferWriter<byte>(512);
            var scope = binding.Scope;
            WriteUtf8(writer, scope.RepositoryId);
            WriteUtf8(writer, scope.WorkflowIdentity);
            WriteUInt64(writer, checked((ulong)scope.ReviewTarget));
            WriteAscii(writer, scope.SessionId);
            WriteUtf8(writer, scope.ProviderId);
            WriteUtf8(writer, scope.ModelId);
            WriteUtf8(writer, scope.AdapterId);
            WriteHex(writer, scope.PolicySha256, 32);
            WriteHex(writer, scope.LimitsSha256, 32);
            WriteHex(writer, scope.ToolsetSha256, 32);
            WriteUtf8(writer, scope.BuildId);
            WriteHex(writer, binding.ProducerBaseSha, 20);
            WriteHex(writer, binding.ProducerHeadSha, 20);
            WriteUInt64(writer, checked((ulong)binding.Generation));
            if (binding.PredecessorEnvelopeSha256 is null)
            {
                WriteByte(writer, 0);
            }
            else
            {
                WriteByte(writer, 1);
                WriteHex(
                    writer,
                    binding.PredecessorEnvelopeSha256,
                    32);
            }

            WriteInt64(writer, binding.AcceptedAtUnixSeconds);
            WriteInt64(writer, binding.ExpiresAtUnixSeconds);
            bytes = writer.WrittenMemory.ToArray();
            return bytes.Length <=
                RestrictedStateFormat.MaximumBindingBytes;
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

    private static void WriteUtf8(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = StrictUtf8.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteAscii(
        IBufferWriter<byte> writer,
        string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        WriteUInt16(writer, checked((ushort)bytes.Length));
        Write(writer, bytes);
    }

    private static void WriteHex(
        IBufferWriter<byte> writer,
        string value,
        int expectedBytes)
    {
        var decoded = Convert.FromHexString(value);
        if (decoded.Length != expectedBytes)
        {
            throw new FormatException("Invalid fixed hash.");
        }

        Write(writer, decoded);
    }

    private static void WriteByte(
        IBufferWriter<byte> writer,
        byte value)
    {
        var destination = writer.GetSpan(1);
        destination[0] = value;
        writer.Advance(1);
    }

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
        var destination = writer.GetSpan(value.Length);
        value.CopyTo(destination);
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

    private static bool TryReadUtf8(
        ReadOnlySpan<byte> source,
        ref int offset,
        int minimum,
        int maximum,
        out string? value)
    {
        value = null;
        if (!TryReadUInt16(source, ref offset, out var length) ||
            length < minimum ||
            length > maximum ||
            !TryReadBytes(source, ref offset, length, out var bytes))
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(bytes);
            return StrictUtf8.GetByteCount(value) == length;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
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
            length < minimum ||
            length > maximum ||
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

        value = Encoding.ASCII.GetString(bytes);
        return true;
    }
}
