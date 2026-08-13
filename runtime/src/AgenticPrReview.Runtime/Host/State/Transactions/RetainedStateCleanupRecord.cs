using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed record RetainedStateCleanupRecord(
    string TerminalAcceptanceIdentity,
    string BaseScopeDigest,
    string Epoch,
    string SessionId,
    string PreCleanupInventoryDigest,
    ImmutableArray<OpaqueStoreObjectMetadata> Targets,
    string OperationIdentity);

internal static class RetainedStateCleanupRecordCodec
{
    internal const string Magic = "APRSCU01";
    internal const string OperationDomain = "apr.state-cleanup.s6";
    internal const ushort Version = 1;
    internal const int MaximumPayloadBytes = 256 * 1024;

    internal static bool TryCreate(
        string terminalAcceptanceIdentity,
        string baseScopeDigest,
        string epoch,
        string sessionId,
        string preCleanupInventoryDigest,
        ImmutableArray<OpaqueStoreObjectMetadata> targets,
        out RetainedStateCleanupRecord? value)
    {
        value = null;
        if (!LineageValidation.IsSha256(terminalAcceptanceIdentity) ||
            !LineageValidation.IsSha256(baseScopeDigest) ||
            !LineageValidation.IsSha256(epoch) ||
            !LineageValidation.IsSha256(sessionId) ||
            !LineageValidation.IsSha256(preCleanupInventoryDigest) ||
            targets.IsDefault ||
            targets.Length > LineageFormat.MaximumScopedObjects ||
            targets.Any(item => !OpaqueStoreValidation.IsValid(item)))
        {
            return false;
        }

        var ordered = targets
            .Distinct()
            .OrderBy(item => item.Reference.Name.Value, StringComparer.Ordinal)
            .ThenBy(
                item => item.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();
        if (ordered.Length != targets.Length)
        {
            return false;
        }

        var provisional = new RetainedStateCleanupRecord(
            terminalAcceptanceIdentity,
            baseScopeDigest,
            epoch,
            sessionId,
            preCleanupInventoryDigest,
            ordered,
            OperationIdentity: new string('0', 64));
        if (!TryWriteCore(provisional, includeIdentity: false, out var framed))
        {
            return false;
        }

        try
        {
            var operationIdentity = HashOperation(framed);
            value = provisional with
            {
                OperationIdentity = operationIdentity,
            };
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    internal static bool TryEncode(
        RetainedStateCleanupRecord? value,
        out byte[] bytes)
    {
        bytes = [];
        if (!IsValid(value) ||
            !TryWriteCore(value!, includeIdentity: true, out bytes) ||
            bytes.Length > MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }

        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out RetainedStateCleanupRecord? value)
    {
        value = null;
        if (bytes.Length is < 1 or > MaximumPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, Magic) ||
            !reader.TryReadUInt16(out var version) ||
            version != Version ||
            !reader.TryReadString(64, out var terminal) ||
            !reader.TryReadString(64, out var baseScope) ||
            !reader.TryReadString(64, out var epoch) ||
            !reader.TryReadString(64, out var session) ||
            !reader.TryReadString(64, out var inventory) ||
            !reader.TryReadUInt16(out var count) ||
            count > LineageFormat.MaximumScopedObjects)
        {
            return false;
        }

        var targets = ImmutableArray.CreateBuilder<
            OpaqueStoreObjectMetadata>(count);
        for (var index = 0; index < count; index++)
        {
            if (!TryReadMetadata(ref reader, out var metadata) ||
                metadata is null)
            {
                return false;
            }

            targets.Add(metadata);
        }

        if (!reader.TryReadString(64, out var operationIdentity) ||
            !reader.IsComplete)
        {
            return false;
        }

        var candidate = new RetainedStateCleanupRecord(
            terminal,
            baseScope,
            epoch,
            session,
            inventory,
            targets.MoveToImmutable(),
            operationIdentity);
        if (!IsValid(candidate) ||
            !TryEncode(candidate, out var canonical))
        {
            return false;
        }

        try
        {
            if (!bytes.SequenceEqual(canonical))
            {
                return false;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }

        value = candidate;
        return true;
    }

    internal static bool IsValid(RetainedStateCleanupRecord? value)
    {
        if (value is null ||
            !LineageValidation.IsSha256(value.TerminalAcceptanceIdentity) ||
            !LineageValidation.IsSha256(value.BaseScopeDigest) ||
            !LineageValidation.IsSha256(value.Epoch) ||
            !LineageValidation.IsSha256(value.SessionId) ||
            !LineageValidation.IsSha256(value.PreCleanupInventoryDigest) ||
            !LineageValidation.IsSha256(value.OperationIdentity) ||
            value.Targets.IsDefault ||
            value.Targets.Length > LineageFormat.MaximumScopedObjects ||
            value.Targets.Any(item => !OpaqueStoreValidation.IsValid(item)) ||
            !value.Targets.SequenceEqual(value.Targets
                .OrderBy(
                    item => item.Reference.Name.Value,
                    StringComparer.Ordinal)
                .ThenBy(
                    item => item.Reference.ObjectId.Value,
                    StringComparer.Ordinal)) ||
            value.Targets.Distinct().Count() != value.Targets.Length)
        {
            return false;
        }

        var withoutIdentity = value with
        {
            OperationIdentity = new string('0', 64),
        };
        if (!TryWriteCore(
                withoutIdentity,
                includeIdentity: false,
                out var framed))
        {
            return false;
        }

        try
        {
            return AcceptedStateRecordValidation.FixedDigest(
                HashOperation(framed),
                value.OperationIdentity);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }

    private static bool TryWriteCore(
        RetainedStateCleanupRecord value,
        bool includeIdentity,
        out byte[] bytes)
    {
        bytes = [];
        try
        {
            var writer = new LineageBinaryWriter();
            writer.WriteString(Magic);
            writer.WriteUInt16(Version);
            writer.WriteString(value.TerminalAcceptanceIdentity);
            writer.WriteString(value.BaseScopeDigest);
            writer.WriteString(value.Epoch);
            writer.WriteString(value.SessionId);
            writer.WriteString(value.PreCleanupInventoryDigest);
            writer.WriteUInt16(checked((ushort)value.Targets.Length));
            foreach (var target in value.Targets)
            {
                WriteMetadata(writer, target);
            }

            if (includeIdentity)
            {
                writer.WriteString(value.OperationIdentity);
            }

            bytes = writer.ToArray();
            return bytes.Length <= MaximumPayloadBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
                EncoderFallbackException or
                OverflowException)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
    }

    private static void WriteMetadata(
        LineageBinaryWriter writer,
        OpaqueStoreObjectMetadata value)
    {
        writer.WriteString(value.Reference.Name.Value);
        writer.WriteString(value.Reference.ObjectId.Value);
        writer.WriteString(value.ProducingRun.Identity);
        writer.WriteInt64(value.ProducingRun.Attempt);
        writer.WriteString(value.ArchiveDigest.Sha256);
        writer.WriteString(value.EncryptedObjectDigest.Sha256);
        writer.WriteInt64(value.ExpiresAtUnixSeconds);
        writer.WriteInt64(value.Size);
    }

    private static bool TryReadMetadata(
        ref LineageBinaryReader reader,
        out OpaqueStoreObjectMetadata? metadata)
    {
        metadata = null;
        if (!reader.TryReadString(
                OpaqueStoreLimits.MaximumNameBytes,
                out var name) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var objectId) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var run) ||
            !reader.TryReadInt64(out var attempt) ||
            !reader.TryReadString(64, out var archive) ||
            !reader.TryReadString(64, out var encrypted) ||
            !reader.TryReadInt64(out var expiresAt) ||
            !reader.TryReadInt64(out var size))
        {
            return false;
        }

        var candidate = new OpaqueStoreObjectMetadata(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(name),
                new OpaqueStoreObjectId(objectId)),
            new OpaqueStoreProducingRun(run, attempt),
            new OpaqueStoreArchiveDigest(archive),
            new OpaqueStoreEncryptedObjectDigest(encrypted),
            expiresAt,
            size);
        if (!OpaqueStoreValidation.IsValid(candidate))
        {
            return false;
        }

        metadata = candidate;
        return true;
    }

    private static string HashOperation(ReadOnlySpan<byte> bytes)
    {
        var writer = new LineageBinaryWriter();
        writer.WriteString(OperationDomain);
        writer.WriteBytes(bytes);
        var framed = writer.ToArray();
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(framed));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }
}
