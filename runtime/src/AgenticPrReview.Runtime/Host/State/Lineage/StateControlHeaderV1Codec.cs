using System.Security.Cryptography;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class StateControlHeaderV1Codec
{
    internal static bool TryCreate(
        StateControlHeaderDraft? draft,
        string keyId,
        ReadOnlySpan<byte> payload,
        out StateControlHeaderV1? header)
    {
        header = null;
        if (!LineageValidation.IsValid(draft) ||
            !LineageValidation.IsSha256(keyId) ||
            payload.Length > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        if (!TryEncodeIdentityPayload(
                draft!.ObjectClass,
                payload,
                out var identityPayload))
        {
            return false;
        }

        var semantic = EncodeSemantic(draft);
        try
        {
            header = new StateControlHeaderV1(
                draft!.BaseScopeDigest,
                draft.Epoch,
                draft.SessionId,
                draft.ObjectClass,
                keyId,
                LineageCryptography.ObjectIdentity(
                    semantic,
                    identityPayload),
                draft.PredecessorIdentity,
                draft.SuccessorIdentity,
                draft.ProducingRunIdentity,
                draft.ProducingRunAttempt,
                draft.CreatedAtUnixSeconds,
                draft.LogicalExpiresAtUnixSeconds,
                draft.RequiredPlatformExpiresAtUnixSeconds);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(semantic);
            CryptographicOperations.ZeroMemory(identityPayload);
        }
    }

    internal static bool TryEncode(
        StateControlHeaderV1? header,
        out byte[] bytes)
    {
        bytes = [];
        if (!LineageValidation.IsValid(header))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.HeaderMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteString(header!.BaseScopeDigest);
        writer.WriteString(header.Epoch);
        writer.WriteString(header.SessionId);
        writer.WriteString(StateObjectClasses.ToWireName(header.ObjectClass));
        writer.WriteString(header.KeyId);
        writer.WriteString(header.ObjectIdentity);
        writer.WriteOptionalString(header.PredecessorIdentity);
        writer.WriteOptionalString(header.SuccessorIdentity);
        writer.WriteString(header.ProducingRunIdentity);
        writer.WriteInt64(header.ProducingRunAttempt);
        writer.WriteInt64(header.CreatedAtUnixSeconds);
        writer.WriteInt64(header.LogicalExpiresAtUnixSeconds);
        writer.WriteInt64(header.RequiredPlatformExpiresAtUnixSeconds);
        bytes = writer.ToArray();
        if (bytes.Length > LineageFormat.MaximumHeaderBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }

        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        ReadOnlySpan<byte> payload,
        out StateControlHeaderV1? header)
    {
        header = null;
        if (bytes.Length is < 1 or > LineageFormat.MaximumHeaderBytes ||
            payload.Length > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, LineageFormat.HeaderMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != LineageFormat.Version ||
            !reader.TryReadString(64, out var baseScopeDigest) ||
            !reader.TryReadString(64, out var epoch) ||
            !reader.TryReadString(64, out var sessionId) ||
            !reader.TryReadString(64, out var objectClassText) ||
            !StateObjectClasses.TryParse(objectClassText, out var objectClass) ||
            objectClass == StateObjectClass.LocatorRoot ||
            !reader.TryReadString(64, out var keyId) ||
            !reader.TryReadString(64, out var objectIdentity) ||
            !reader.TryReadOptionalString(64, out var predecessorIdentity) ||
            !reader.TryReadOptionalString(64, out var successorIdentity) ||
            !reader.TryReadString(
                LineageFormat.MaximumRunIdentityBytes,
                out var producingRunIdentity) ||
            !reader.TryReadInt64(out var producingRunAttempt) ||
            !reader.TryReadInt64(out var createdAtUnixSeconds) ||
            !reader.TryReadInt64(out var logicalExpiresAtUnixSeconds) ||
            !reader.TryReadInt64(out var requiredPlatformExpiresAtUnixSeconds) ||
            !reader.IsComplete)
        {
            return false;
        }

        var candidate = new StateControlHeaderV1(
            baseScopeDigest,
            epoch,
            sessionId,
            objectClass,
            keyId,
            objectIdentity,
            predecessorIdentity,
            successorIdentity,
            producingRunIdentity,
            producingRunAttempt,
            createdAtUnixSeconds,
            logicalExpiresAtUnixSeconds,
            requiredPlatformExpiresAtUnixSeconds);
        if (!LineageValidation.IsValid(candidate))
        {
            return false;
        }

        var draft = new StateControlHeaderDraft(
            candidate.BaseScopeDigest,
            candidate.Epoch,
            candidate.SessionId,
            candidate.ObjectClass,
            candidate.PredecessorIdentity,
            candidate.SuccessorIdentity,
            candidate.ProducingRunIdentity,
            candidate.ProducingRunAttempt,
            candidate.CreatedAtUnixSeconds,
            candidate.LogicalExpiresAtUnixSeconds,
            candidate.RequiredPlatformExpiresAtUnixSeconds);
        if (!TryEncodeIdentityPayload(
                candidate.ObjectClass,
                payload,
                out var identityPayload))
        {
            return false;
        }

        var semantic = EncodeSemantic(draft);
        try
        {
            var expected = LineageCryptography.ObjectIdentity(
                semantic,
                identityPayload);
            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(expected),
                    Convert.FromHexString(candidate.ObjectIdentity)))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(semantic);
            CryptographicOperations.ZeroMemory(identityPayload);
        }

        header = candidate;
        return true;
    }

    private static byte[] EncodeSemantic(StateControlHeaderDraft draft)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.HeaderMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteString(draft.BaseScopeDigest);
        writer.WriteString(draft.Epoch);
        writer.WriteString(draft.SessionId);
        writer.WriteString(StateObjectClasses.ToWireName(draft.ObjectClass));
        writer.WriteOptionalString(draft.PredecessorIdentity);
        writer.WriteOptionalString(draft.SuccessorIdentity);
        if (draft.ObjectClass is not StateObjectClass.LineageHead and not
            StateObjectClass.Reset and not StateObjectClass.ExpiryTransition)
        {
            writer.WriteInt64(draft.LogicalExpiresAtUnixSeconds);
        }

        return writer.ToArray();
    }

    private static bool TryEncodeIdentityPayload(
        StateObjectClass objectClass,
        ReadOnlySpan<byte> payload,
        out byte[] identityPayload)
    {
        identityPayload = [];
        if (objectClass != StateObjectClass.LineageHead)
        {
            identityPayload = payload.ToArray();
            return true;
        }

        return LineageHeadCodec.TryDecode(payload, out var head) &&
            LineageHeadCodec.TryEncodeIdentity(head, out identityPayload);
    }
}
