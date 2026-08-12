using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal static class RetainedStateAcceptanceRecoveryCodec
{
    internal const string Magic = "APRSAR01";
    internal const ushort Version = 1;

    internal static bool TryEncode(
        RetainedStateAcceptanceAttempt attempt,
        out byte[] bytes)
    {
        bytes = [];
        ReadOnlyMemory<byte> receipt = default;
        ReadOnlyMemory<byte> envelope = default;
        byte[] header = [];
        try
        {
            if (attempt is null ||
                !attempt.TryGetBytes(out receipt, out envelope) ||
                !StateControlHeaderV1Codec.TryEncode(
                    attempt.Header,
                    out header))
            {
                return false;
            }

            var writer = new LineageBinaryWriter();
            writer.WriteString(Magic);
            writer.WriteUInt16(Version);
            writer.WriteString(attempt.Name.Value);
            writer.WriteBytes(header);
            writer.WriteBytes(receipt.Span);
            writer.WriteBytes(envelope.Span);
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
        finally
        {
            CryptographicOperations.ZeroMemory(header);
        }
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out OpaqueStoreName? name,
        out StateControlHeaderV1? header,
        out AcceptanceReceiptV1? receipt,
        out byte[] receiptBytes,
        out byte[] envelopeBytes)
    {
        name = null;
        header = null;
        receipt = null;
        receiptBytes = [];
        envelopeBytes = [];
        byte[] headerBytes = [];
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
                    LineageFormat.MaximumHeaderBytes,
                    out headerBytes) ||
                !reader.TryReadBytes(
                    LineageFormat.MaximumReaderPayloadBytes,
                    out receiptBytes) ||
                !reader.TryReadBytes(
                    LineageFormat.MaximumEnvelopeBytes,
                    out envelopeBytes) ||
                !reader.IsComplete ||
                !StateControlHeaderV1Codec.TryDecode(
                    headerBytes,
                    receiptBytes,
                    out header) ||
                header is null ||
                !AcceptedStateAcceptanceReceiptCodec.TryDecode(
                    receiptBytes,
                    out receipt) ||
                receipt is null)
            {
                return false;
            }

            var parsedName = new OpaqueStoreName(nameValue);
            if (!OpaqueStoreValidation.IsValid(parsedName) ||
                header.ObjectClass != StateObjectClass.Acceptance ||
                header.CreatedAtUnixSeconds !=
                    receipt.AcceptedAtUnixSeconds ||
                header.LogicalExpiresAtUnixSeconds !=
                    receipt.LogicalExpiresAtUnixSeconds ||
                !StringComparer.Ordinal.Equals(
                    header.ProducingRunIdentity,
                    receipt.ProducingRunIdentity) ||
                header.ProducingRunAttempt !=
                    receipt.ProducingRunAttempt)
            {
                return false;
            }

            name = parsedName;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(headerBytes);
            if (name is null || header is null || receipt is null)
            {
                CryptographicOperations.ZeroMemory(receiptBytes);
                CryptographicOperations.ZeroMemory(envelopeBytes);
                receiptBytes = [];
                envelopeBytes = [];
            }
        }
    }
}
