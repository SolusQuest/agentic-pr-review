using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageHeadCodec
{
    internal static bool TryEncode(LineageHeadV1? head, out byte[] payload)
    {
        payload = [];
        if (!IsValid(head))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.HeadMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteByte((byte)head!.Transition);
        writer.WriteUInt64(head.Ordinal);
        writer.WriteString(head.Reviewed.BaseSha);
        writer.WriteString(head.Reviewed.HeadSha);
        writer.WriteOptionalString(head.PreviousEpoch);
        writer.WriteOptionalString(head.PreviousHeadIdentity);
        writer.WriteOptionalString(head.TransitionEvidenceIdentity);
        writer.WriteOptionalString(head.ResetAuthorityRunIdentity);
        writer.WriteByte(head.ResetAuthorityRunAttempt is null ?
            (byte)0 : (byte)1);
        if (head.ResetAuthorityRunAttempt is not null)
        {
            writer.WriteInt64(head.ResetAuthorityRunAttempt.Value);
        }

        writer.WriteByte(head.ExpiryBoundaryUnixSeconds is null ? (byte)0 : (byte)1);
        if (head.ExpiryBoundaryUnixSeconds is not null)
        {
            writer.WriteInt64(head.ExpiryBoundaryUnixSeconds.Value);
        }

        WriteEvidence(writer, head.PhysicalPredecessors);
        WriteEvidence(writer, head.PhysicalSuperseded);
        WriteEvidence(writer, head.Superseded);
        WriteEvidence(writer, head.CompletedCleanup);
        payload = writer.ToArray();
        if (payload.Length > LineageFormat.MaximumPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(payload);
            payload = [];
            return false;
        }

        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> payload,
        out LineageHeadV1? head)
    {
        head = null;
        if (payload.Length is < 1 or > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(payload);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, LineageFormat.HeadMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != LineageFormat.Version ||
            !reader.TryReadByte(out var transitionValue) ||
            !Enum.IsDefined((LineageTransitionKind)transitionValue) ||
            !reader.TryReadUInt64(out var ordinal) ||
            !reader.TryReadString(64, out var baseSha) ||
            !reader.TryReadString(64, out var headSha) ||
            !reader.TryReadOptionalString(64, out var previousEpoch) ||
            !reader.TryReadOptionalString(64, out var previousHeadIdentity) ||
            !reader.TryReadOptionalString(64, out var transitionEvidenceIdentity) ||
            !reader.TryReadOptionalString(
                LineageFormat.MaximumRunIdentityBytes,
                out var resetAuthorityRunIdentity) ||
            !reader.TryReadByte(out var hasResetAuthorityAttempt) ||
            hasResetAuthorityAttempt > 1)
        {
            return false;
        }

        long? resetAuthorityRunAttempt = null;
        if (hasResetAuthorityAttempt == 1)
        {
            if (!reader.TryReadInt64(out var parsedResetAuthorityRunAttempt))
            {
                return false;
            }

            resetAuthorityRunAttempt = parsedResetAuthorityRunAttempt;
        }

        if (
            !reader.TryReadByte(out var hasExpiry) ||
            hasExpiry > 1)
        {
            return false;
        }

        long? expiryBoundary = null;
        if (hasExpiry == 1)
        {
            if (!reader.TryReadInt64(out var parsedExpiry))
            {
                return false;
            }

            expiryBoundary = parsedExpiry;
        }

        if (!TryReadEvidence(ref reader, out var predecessors) ||
            !TryReadEvidence(ref reader, out var physicalSuperseded) ||
            !TryReadEvidence(ref reader, out var superseded) ||
            !TryReadEvidence(ref reader, out var completedCleanup) ||
            !reader.IsComplete)
        {
            return false;
        }

        var candidate = new LineageHeadV1(
            (LineageTransitionKind)transitionValue,
            ordinal,
            new ReviewedTransitionFacts(baseSha, headSha),
            previousEpoch,
            previousHeadIdentity,
            transitionEvidenceIdentity,
            expiryBoundary,
            predecessors,
            physicalSuperseded,
            superseded,
            completedCleanup,
            resetAuthorityRunIdentity,
            resetAuthorityRunAttempt);
        if (!IsValid(candidate))
        {
            return false;
        }

        head = candidate;
        return true;
    }

    internal static bool Equivalent(LineageHeadV1 left, LineageHeadV1 right)
    {
        byte[] leftBytes = [];
        byte[] rightBytes = [];
        if (!TryEncodeIdentity(left, out leftBytes) ||
            !TryEncodeIdentity(right, out rightBytes))
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
            return false;
        }

        try
        {
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(leftBytes),
                SHA256.HashData(rightBytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leftBytes);
            CryptographicOperations.ZeroMemory(rightBytes);
        }
    }

    internal static LineageArtifactEvidence Evidence(
        OpaqueStoreObjectMetadata metadata) =>
        new(
            metadata.Reference.Name.Value,
            metadata.Reference.ObjectId.Value,
            metadata.ProducingRun.Identity,
            metadata.ProducingRun.Attempt,
            metadata.ArchiveDigest.Sha256,
            metadata.EncryptedObjectDigest.Sha256,
            metadata.ExpiresAtUnixSeconds,
            metadata.Size);

    internal static bool Matches(
        LineageArtifactEvidence evidence,
        OpaqueStoreObjectMetadata metadata) =>
        StringComparer.Ordinal.Equals(
            evidence.Name,
            metadata.Reference.Name.Value) &&
        StringComparer.Ordinal.Equals(
            evidence.ObjectId,
            metadata.Reference.ObjectId.Value) &&
        StringComparer.Ordinal.Equals(
            evidence.ProducingRunIdentity,
            metadata.ProducingRun.Identity) &&
        evidence.ProducingRunAttempt == metadata.ProducingRun.Attempt &&
        StringComparer.Ordinal.Equals(
            evidence.ArchiveSha256,
            metadata.ArchiveDigest.Sha256) &&
        StringComparer.Ordinal.Equals(
            evidence.EncryptedObjectSha256,
            metadata.EncryptedObjectDigest.Sha256) &&
        evidence.ExpiresAtUnixSeconds == metadata.ExpiresAtUnixSeconds &&
        evidence.Size == metadata.Size;

    internal static bool IsValid(LineageHeadV1? head)
    {
        if (head is null ||
            !Enum.IsDefined(head.Transition) ||
            !LineageValidation.IsValid(head.Reviewed) ||
            !LineageValidation.IsOptionalSha256(head.PreviousEpoch) ||
            !LineageValidation.IsOptionalSha256(head.PreviousHeadIdentity) ||
            !LineageValidation.IsOptionalSha256(
                head.TransitionEvidenceIdentity) ||
            ((head.ResetAuthorityRunIdentity is null) !=
                (head.ResetAuthorityRunAttempt is null)) ||
            (head.ResetAuthorityRunIdentity is not null &&
                !LineageValidation.IsText(
                    head.ResetAuthorityRunIdentity,
                    LineageFormat.MaximumRunIdentityBytes)) ||
            (head.ResetAuthorityRunAttempt is not null &&
                head.ResetAuthorityRunAttempt < 0) ||
            (head.ExpiryBoundaryUnixSeconds is not null &&
                !LineageValidation.IsTime(
                    head.ExpiryBoundaryUnixSeconds.Value)) ||
            !IsEvidenceSet(head.PhysicalPredecessors) ||
            !IsEvidenceSet(head.PhysicalSuperseded) ||
            !IsEvidenceSet(head.Superseded) ||
            !IsEvidenceSet(head.CompletedCleanup))
        {
            return false;
        }

        return head.Transition switch
        {
            LineageTransitionKind.Initial =>
                head.Ordinal == 0 &&
                head.PreviousEpoch is null &&
                head.PreviousHeadIdentity is null &&
                head.TransitionEvidenceIdentity is null &&
                head.ExpiryBoundaryUnixSeconds is null &&
                head.PhysicalPredecessors.IsEmpty &&
                head.Superseded.IsEmpty &&
                head.CompletedCleanup.IsEmpty &&
                head.ResetAuthorityRunIdentity is null &&
                head.ResetAuthorityRunAttempt is null,
            LineageTransitionKind.Reset =>
                head.Ordinal > 0 &&
                head.PreviousEpoch is not null &&
                head.PreviousHeadIdentity is not null &&
                head.TransitionEvidenceIdentity is not null &&
                head.ExpiryBoundaryUnixSeconds is null &&
                head.ResetAuthorityRunIdentity is not null &&
                head.ResetAuthorityRunAttempt is not null &&
                !head.PhysicalPredecessors.IsEmpty,
            LineageTransitionKind.Expiry =>
                head.Ordinal > 0 &&
                head.PreviousEpoch is not null &&
                head.PreviousHeadIdentity is not null &&
                head.TransitionEvidenceIdentity is not null &&
                head.ExpiryBoundaryUnixSeconds is not null &&
                head.ResetAuthorityRunIdentity is null &&
                head.ResetAuthorityRunAttempt is null &&
                !head.PhysicalPredecessors.IsEmpty,
            _ => false,
        };
    }

    private static bool IsEvidenceSet(
        ImmutableArray<LineageArtifactEvidence> evidence) =>
        !evidence.IsDefault &&
        evidence.Length <= LineageFormat.MaximumEvidenceObjects &&
        evidence.All(LineageValidation.IsValid) &&
        evidence.Select(ItemKey).Distinct(StringComparer.Ordinal).Count() ==
            evidence.Length;

    internal static bool TryEncodeIdentity(
        LineageHeadV1? head,
        out byte[] identity)
    {
        identity = [];
        if (!IsValid(head))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.HeadMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteByte((byte)head!.Transition);
        writer.WriteUInt64(head.Ordinal);
        writer.WriteString(head.Reviewed.BaseSha);
        writer.WriteString(head.Reviewed.HeadSha);
        writer.WriteOptionalString(head.PreviousEpoch);
        writer.WriteOptionalString(head.PreviousHeadIdentity);
        writer.WriteOptionalString(head.TransitionEvidenceIdentity);
        writer.WriteOptionalString(head.ResetAuthorityRunIdentity);
        writer.WriteByte(head.ResetAuthorityRunAttempt is null ?
            (byte)0 : (byte)1);
        if (head.ResetAuthorityRunAttempt is not null)
        {
            writer.WriteInt64(head.ResetAuthorityRunAttempt.Value);
        }

        writer.WriteByte(head.ExpiryBoundaryUnixSeconds is null ?
            (byte)0 : (byte)1);
        if (head.ExpiryBoundaryUnixSeconds is not null)
        {
            writer.WriteInt64(head.ExpiryBoundaryUnixSeconds.Value);
        }

        identity = writer.ToArray();
        return true;
    }

    internal static void WriteEvidence(
        LineageBinaryWriter writer,
        ImmutableArray<LineageArtifactEvidence> evidence)
    {
        writer.WriteUInt32(checked((uint)evidence.Length));
        foreach (var item in evidence.OrderBy(ItemKey, StringComparer.Ordinal))
        {
            writer.WriteString(item.Name);
            writer.WriteString(item.ObjectId);
            writer.WriteString(item.ProducingRunIdentity);
            writer.WriteInt64(item.ProducingRunAttempt);
            writer.WriteString(item.ArchiveSha256);
            writer.WriteString(item.EncryptedObjectSha256);
            writer.WriteInt64(item.ExpiresAtUnixSeconds);
            writer.WriteInt64(item.Size);
        }
    }

    internal static bool TryReadEvidence(
        ref LineageBinaryReader reader,
        out ImmutableArray<LineageArtifactEvidence> evidence)
    {
        evidence = [];
        if (!reader.TryReadUInt32(out var count) ||
            count > LineageFormat.MaximumEvidenceObjects)
        {
            return false;
        }

        var builder = ImmutableArray.CreateBuilder<LineageArtifactEvidence>(
            checked((int)count));
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadString(
                    OpaqueStoreLimits.MaximumNameBytes,
                    out var name) ||
                !reader.TryReadString(
                    OpaqueStoreLimits.MaximumIdentityBytes,
                    out var objectId) ||
                !reader.TryReadString(
                    OpaqueStoreLimits.MaximumIdentityBytes,
                    out var producingRunIdentity) ||
                !reader.TryReadInt64(out var producingRunAttempt) ||
                !reader.TryReadString(64, out var archiveSha256) ||
                !reader.TryReadString(64, out var encryptedSha256) ||
                !reader.TryReadInt64(out var expiresAtUnixSeconds) ||
                !reader.TryReadInt64(out var size))
            {
                return false;
            }

            builder.Add(new LineageArtifactEvidence(
                name,
                objectId,
                producingRunIdentity,
                producingRunAttempt,
                archiveSha256,
                encryptedSha256,
                expiresAtUnixSeconds,
                size));
        }

        evidence = builder.MoveToImmutable();
        return IsEvidenceSet(evidence);
    }

    private static string ItemKey(LineageArtifactEvidence item) =>
        string.Concat(
            item.Name,
            "\0",
            item.ObjectId,
            "\0",
            item.ProducingRunIdentity,
            "\0",
            item.ProducingRunAttempt,
            "\0",
            item.ArchiveSha256,
            "\0",
            item.EncryptedObjectSha256,
            "\0",
            item.ExpiresAtUnixSeconds,
            "\0",
            item.Size);
}
