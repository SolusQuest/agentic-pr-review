using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal static class RetainedStateOpaqueWriteRecoveryCodec
{
    internal const string Magic = "APROWR01";
    internal const ushort Version = 1;

    internal static bool TryEncode(
        StateObjectClass objectClass,
        long semanticRequiredExpiresAtUnixSeconds,
        string candidateObjectIdentity,
        OpaqueStoreName name,
        StateControlHeaderV1 header,
        ReadOnlySpan<byte> envelope,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            if (!Allowed(objectClass) ||
                !LineageValidation.IsTime(
                    semanticRequiredExpiresAtUnixSeconds) ||
                !LineageValidation.IsSha256(candidateObjectIdentity) ||
                !OpaqueStoreValidation.IsValid(name) ||
                header.ObjectClass != objectClass ||
                header.PredecessorIdentity != candidateObjectIdentity ||
                header.LogicalExpiresAtUnixSeconds !=
                    semanticRequiredExpiresAtUnixSeconds ||
                envelope.Length is < 1 or > LineageFormat.MaximumEnvelopeBytes)
            {
                return false;
            }

            var writer = new LineageBinaryWriter();
            writer.WriteString(Magic);
            writer.WriteUInt16(Version);
            writer.WriteUInt16((ushort)objectClass);
            writer.WriteInt64(semanticRequiredExpiresAtUnixSeconds);
            writer.WriteString(candidateObjectIdentity);
            writer.WriteString(name.Value);
            writer.WriteBytes(envelope);
            bytes = writer.ToArray();
            return bytes.Length <= LineageFormat.MaximumPayloadBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out StateObjectClass objectClass,
        out long semanticRequiredExpiresAtUnixSeconds,
        out string? candidateObjectIdentity,
        out OpaqueStoreName? name,
        out byte[] envelope)
    {
        objectClass = default;
        semanticRequiredExpiresAtUnixSeconds = 0;
        candidateObjectIdentity = null;
        name = null;
        envelope = [];
        try
        {
            if (bytes.Length is < 1 or > LineageFormat.MaximumPayloadBytes)
            {
                return false;
            }

            var reader = new LineageBinaryReader(bytes);
            if (!reader.TryReadString(32, out var magic) ||
                !StringComparer.Ordinal.Equals(magic, Magic) ||
                !reader.TryReadUInt16(out var version) ||
                version != Version ||
                !reader.TryReadUInt16(out var encodedClass) ||
                !Enum.IsDefined(typeof(StateObjectClass), (int)encodedClass) ||
                !reader.TryReadInt64(
                    out semanticRequiredExpiresAtUnixSeconds) ||
                !reader.TryReadString(
                    OpaqueStoreLimits.MaximumIdentityBytes,
                    out candidateObjectIdentity) ||
                !reader.TryReadString(
                    OpaqueStoreLimits.MaximumNameBytes,
                    out var nameValue) ||
                !reader.TryReadBytes(
                    LineageFormat.MaximumEnvelopeBytes,
                    out envelope) ||
                !reader.IsComplete)
            {
                return false;
            }

            objectClass = (StateObjectClass)encodedClass;
            name = new OpaqueStoreName(nameValue);
            if (!Allowed(objectClass) ||
                !LineageValidation.IsTime(
                    semanticRequiredExpiresAtUnixSeconds) ||
                !LineageValidation.IsSha256(candidateObjectIdentity) ||
                !OpaqueStoreValidation.IsValid(name))
            {
                return false;
            }

            return true;
        }
        finally
        {
            if (name is null || candidateObjectIdentity is null)
            {
                CryptographicOperations.ZeroMemory(envelope);
                envelope = [];
            }
        }
    }

    private static bool Allowed(StateObjectClass objectClass) =>
        objectClass is StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment;
}
