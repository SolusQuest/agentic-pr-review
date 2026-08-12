using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal static class RetainedStateAcceptanceRecoveryCodec
{
    internal const string Magic = "APRSAR01";
    internal const ushort Version = 1;

    internal static bool TryEncode(
        RetainedStateAcceptanceAttempt attempt,
        RetainedStatePredecessorCopyAttempt? predecessorCopy,
        out byte[] bytes)
    {
        bytes = [];
        ReadOnlyMemory<byte> envelope = default;
        ReadOnlyMemory<byte> copyEnvelope = default;
        try
        {
            if (attempt is null ||
                !attempt.TryGetBytes(out _, out envelope) ||
                (predecessorCopy is not null &&
                    (!predecessorCopy.TryGetBytes(
                        out _,
                        out copyEnvelope))))
            {
                return false;
            }

            var writer = new LineageBinaryWriter();
            writer.WriteString(Magic);
            writer.WriteUInt16(Version);
            writer.WriteString(attempt.Name.Value);
            writer.WriteBytes(envelope.Span);
            writer.WriteUInt16(predecessorCopy is null ? (ushort)0 : (ushort)1);
            if (predecessorCopy is not null)
            {
                writer.WriteString(
                    predecessorCopy.LogicalGenerationIdentity);
                writer.WriteInt64(
                    predecessorCopy.RequiredLogicalExpiresAtUnixSeconds);
                writer.WriteInt64(
                    predecessorCopy.RequiredPlatformExpiresAtUnixSeconds);
                writer.WriteString(predecessorCopy.Name.Value);
                writer.WriteBytes(copyEnvelope.Span);
            }

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
        out OpaqueStoreName? name,
        out byte[] envelopeBytes,
        out RetainedStateRecoveredPredecessorCopy? predecessorCopy)
    {
        name = null;
        envelopeBytes = [];
        predecessorCopy = null;
        byte[] copyEnvelope = [];
        if (bytes.Length is < 1 or > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        try
        {
            var reader = new LineageBinaryReader(bytes);
            if (!reader.TryReadString(32, out var magic) ||
                !StringComparer.Ordinal.Equals(magic, Magic) ||
                !reader.TryReadUInt16(out var version) ||
                version != Version ||
                !reader.TryReadString(
                    OpaqueStoreLimits.MaximumNameBytes,
                    out var nameValue) ||
                !reader.TryReadBytes(
                    LineageFormat.MaximumEnvelopeBytes,
                    out envelopeBytes) ||
                !reader.TryReadUInt16(out var hasCopy) ||
                hasCopy > 1)
            {
                return false;
            }

            if (hasCopy == 1)
            {
                if (!reader.TryReadString(
                        OpaqueStoreLimits.MaximumIdentityBytes,
                        out var logicalGenerationIdentity) ||
                    !reader.TryReadInt64(out var requiredLogicalExpiry) ||
                    !reader.TryReadInt64(out var requiredPlatformExpiry) ||
                    !reader.TryReadString(
                        OpaqueStoreLimits.MaximumNameBytes,
                        out var copyNameValue) ||
                    !reader.TryReadBytes(
                        LineageFormat.MaximumEnvelopeBytes,
                        out copyEnvelope))
                {
                    return false;
                }

                var copyName = new OpaqueStoreName(copyNameValue);
                if (!LineageValidation.IsSha256(
                        logicalGenerationIdentity) ||
                    !LineageValidation.IsTime(requiredLogicalExpiry) ||
                    !LineageValidation.IsTime(requiredPlatformExpiry) ||
                    !OpaqueStoreValidation.IsValid(copyName))
                {
                    return false;
                }

                predecessorCopy = new RetainedStateRecoveredPredecessorCopy(
                    logicalGenerationIdentity,
                    requiredLogicalExpiry,
                    requiredPlatformExpiry,
                    copyName,
                    copyEnvelope);
                copyEnvelope = [];
            }

            if (!reader.IsComplete)
            {
                return false;
            }

            var parsedName = new OpaqueStoreName(nameValue);
            if (!OpaqueStoreValidation.IsValid(parsedName))
            {
                return false;
            }

            name = parsedName;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(copyEnvelope);
            if (name is null)
            {
                CryptographicOperations.ZeroMemory(envelopeBytes);
                envelopeBytes = [];
                predecessorCopy?.Dispose();
                predecessorCopy = null;
            }
        }
    }
}

internal sealed record RetainedStateRecoveredPredecessorCopy(
    string LogicalGenerationIdentity,
    long RequiredLogicalExpiresAtUnixSeconds,
    long RequiredPlatformExpiresAtUnixSeconds,
    OpaqueStoreName Name,
    byte[] Envelope) : IDisposable
{
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Envelope);
    }
}
