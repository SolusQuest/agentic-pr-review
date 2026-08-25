using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class StateControlEnvelopeV1Codec
{
    internal static bool TryEncrypt(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        OpaqueStoreName name,
        StateControlHeaderDraft draft,
        ReadOnlySpan<byte> payload,
        out byte[] envelope,
        out StateControlHeaderV1? header,
        out string code)
    {
        envelope = [];
        header = null;
        code = LineageCodes.Invalid;
        if (!OpaqueStoreValidation.IsValid(name) ||
            !LineageValidation.IsValid(draft) ||
            !LineageFormat.IsPayloadLengthAllowed(
                draft.ObjectClass,
                payload.Length))
        {
            return false;
        }

        Span<byte> key = stackalloc byte[LineageFormat.KeyBytes];
        if (!context.TryCopyCurrentStateKey(access, key, out var keyId))
        {
            code = LineageCodes.KeyUnavailable;
            return false;
        }

        byte[] headerBytes = [];
        byte[] plaintext = [];
        byte[] aad = [];
        byte[] nonce = [];
        byte[] ciphertext = [];
        byte[] tag = [];
        try
        {
            if (!StateControlHeaderV1Codec.TryCreate(
                    draft,
                    keyId,
                    payload,
                    out header) ||
                header is null ||
                !StateControlHeaderV1Codec.TryEncode(header, out headerBytes))
            {
                return false;
            }

            var plaintextWriter = new LineageBinaryWriter();
            plaintextWriter.WriteBytes(headerBytes);
            plaintextWriter.WriteBytes(payload);
            plaintext = plaintextWriter.ToArray();
            if (plaintext.Length > LineageFormat.MaximumEnvelopeBytes)
            {
                return false;
            }

            nonce = RandomNumberGenerator.GetBytes(LineageFormat.NonceBytes);
            ciphertext = new byte[plaintext.Length];
            tag = new byte[LineageFormat.TagBytes];
            aad = EncodeAad(name, keyId);
            using (var aes = new AesGcm(key, LineageFormat.TagBytes))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);
            }

            var writer = new LineageBinaryWriter();
            writer.WriteString(LineageFormat.EnvelopeMagic);
            writer.WriteUInt16(LineageFormat.Version);
            writer.WriteUInt16(LineageFormat.Aes256GcmAlgorithm);
            writer.WriteString(keyId);
            writer.WriteBytes(nonce);
            writer.WriteBytes(ciphertext);
            writer.WriteBytes(tag);
            envelope = writer.ToArray();
            if (envelope.Length > LineageFormat.MaximumEnvelopeBytes)
            {
                CryptographicOperations.ZeroMemory(envelope);
                envelope = [];
                return false;
            }

            code = LineageCodes.Ready;
            return true;
        }
        catch (CryptographicException)
        {
            code = LineageCodes.AuthenticationFailed;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(headerBytes);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
        }
    }

    internal static bool TryDecrypt(
        LocatorContext context,
        AuthorizedLocatorAccess access,
        OpaqueStoreName name,
        ReadOnlySpan<byte> envelope,
        out StateControlHeaderV1? header,
        out byte[] payload,
        out string code)
    {
        header = null;
        payload = [];
        code = LineageCodes.Invalid;
        if (!OpaqueStoreValidation.IsValid(name) ||
            envelope.Length is < 1 or > LineageFormat.MaximumEnvelopeBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(envelope);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, LineageFormat.EnvelopeMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != LineageFormat.Version ||
            !reader.TryReadUInt16(out var algorithm) ||
            algorithm != LineageFormat.Aes256GcmAlgorithm ||
            !reader.TryReadString(64, out var keyId) ||
            !LineageValidation.IsSha256(keyId) ||
            !reader.TryReadBytes(LineageFormat.NonceBytes, out var nonce) ||
            nonce.Length != LineageFormat.NonceBytes ||
            !reader.TryReadBytes(
                LineageFormat.MaximumEnvelopeBytes,
                out var ciphertext) ||
            ciphertext.Length < 1 ||
            !reader.TryReadBytes(LineageFormat.TagBytes, out var tag) ||
            tag.Length != LineageFormat.TagBytes ||
            !reader.IsComplete)
        {
            code = LineageCodes.AuthenticationFailed;
            return false;
        }

        Span<byte> key = stackalloc byte[LineageFormat.KeyBytes];
        if (!context.TryCopyApprovedReadKey(access, keyId, key))
        {
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            code = LineageCodes.KeyUnavailable;
            return false;
        }

        var plaintext = new byte[ciphertext.Length];
        var aad = EncodeAad(name, keyId);
        try
        {
            using (var aes = new AesGcm(key, LineageFormat.TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            }

            var plaintextReader = new LineageBinaryReader(plaintext);
            if (!plaintextReader.TryReadBytes(
                    LineageFormat.MaximumHeaderBytes,
                    out var headerBytes) ||
                !plaintextReader.TryReadBytes(
                    LineageFormat.MaximumReaderPayloadBytes,
                    out payload) ||
                !plaintextReader.IsComplete)
            {
                CryptographicOperations.ZeroMemory(headerBytes);
                CryptographicOperations.ZeroMemory(payload);
                payload = [];
                code = LineageCodes.AuthenticationFailed;
                return false;
            }

            try
            {
                if (!StateControlHeaderV1Codec.TryDecode(
                        headerBytes,
                        payload,
                        out header) ||
                    header is null ||
                    !StringComparer.Ordinal.Equals(header.KeyId, keyId))
                {
                    CryptographicOperations.ZeroMemory(payload);
                    payload = [];
                    code = LineageCodes.AuthenticationFailed;
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(headerBytes);
            }

            code = LineageCodes.Ready;
            return true;
        }
        catch (CryptographicException)
        {
            CryptographicOperations.ZeroMemory(payload);
            payload = [];
            code = LineageCodes.AuthenticationFailed;
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    internal static bool TryDecryptForSyntheticEvidence(
        OpaqueStoreName name,
        string currentKeyBase64,
        ReadOnlySpan<byte> envelope,
        out StateControlHeaderV1? header,
        out byte[] payload)
    {
        header = null;
        payload = [];
        byte[] key = [];
        byte[] plaintext = [];
        byte[] nonce = [];
        byte[] ciphertext = [];
        byte[] tag = [];
        byte[] aad = [];
        try
        {
            key = Convert.FromBase64String(currentKeyBase64);
            if (key.Length != LineageFormat.KeyBytes ||
                !StringComparer.Ordinal.Equals(
                    Convert.ToBase64String(key),
                    currentKeyBase64) ||
                !OpaqueStoreValidation.IsValid(name))
            {
                return false;
            }

            var reader = new LineageBinaryReader(envelope);
            if (!reader.TryReadString(32, out var magic) ||
                !StringComparer.Ordinal.Equals(magic, LineageFormat.EnvelopeMagic) ||
                !reader.TryReadUInt16(out var version) ||
                version != LineageFormat.Version ||
                !reader.TryReadUInt16(out var algorithm) ||
                algorithm != LineageFormat.Aes256GcmAlgorithm ||
                !reader.TryReadString(64, out var keyId) ||
                !StringComparer.Ordinal.Equals(
                    keyId,
                    LocatorCryptography.KeyId(key)) ||
                !reader.TryReadBytes(LineageFormat.NonceBytes, out nonce) ||
                nonce.Length != LineageFormat.NonceBytes ||
                !reader.TryReadBytes(
                    LineageFormat.MaximumEnvelopeBytes,
                    out ciphertext) ||
                ciphertext.Length < 1 ||
                !reader.TryReadBytes(LineageFormat.TagBytes, out tag) ||
                tag.Length != LineageFormat.TagBytes ||
                !reader.IsComplete)
            {
                return false;
            }

            plaintext = new byte[ciphertext.Length];
            aad = EncodeAad(name, keyId);
            using (var aes = new AesGcm(key, LineageFormat.TagBytes))
            {
                aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            }

            var plaintextReader = new LineageBinaryReader(plaintext);
            if (!plaintextReader.TryReadBytes(
                    LineageFormat.MaximumHeaderBytes,
                    out var headerBytes) ||
                !plaintextReader.TryReadBytes(
                    LineageFormat.MaximumReaderPayloadBytes,
                    out payload) ||
                !plaintextReader.IsComplete)
            {
                CryptographicOperations.ZeroMemory(headerBytes);
                CryptographicOperations.ZeroMemory(payload);
                payload = [];
                return false;
            }

            try
            {
                if (!StateControlHeaderV1Codec.TryDecode(
                        headerBytes,
                        payload,
                        out header) ||
                    header is null ||
                    !StringComparer.Ordinal.Equals(header.KeyId, keyId))
                {
                    CryptographicOperations.ZeroMemory(payload);
                    payload = [];
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(headerBytes);
            }

            return true;
        }
        catch (Exception exception) when (
            exception is FormatException or CryptographicException)
        {
            CryptographicOperations.ZeroMemory(payload);
            payload = [];
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(aad);
        }
    }

    internal static bool TryReadKeyId(
        ReadOnlySpan<byte> envelope,
        out string keyId)
    {
        keyId = string.Empty;
        var reader = new LineageBinaryReader(envelope);
        return reader.TryReadString(32, out var magic) &&
            StringComparer.Ordinal.Equals(magic, LineageFormat.EnvelopeMagic) &&
            reader.TryReadUInt16(out var version) &&
            version == LineageFormat.Version &&
            reader.TryReadUInt16(out var algorithm) &&
            algorithm == LineageFormat.Aes256GcmAlgorithm &&
            reader.TryReadString(64, out keyId) &&
            LineageValidation.IsSha256(keyId);
    }

    private static byte[] EncodeAad(OpaqueStoreName name, string keyId)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.EnvelopeAadPrefix);
        writer.WriteString(name.Value);
        writer.WriteString(keyId);
        writer.WriteUInt16(LineageFormat.Version);
        return writer.ToArray();
    }
}
