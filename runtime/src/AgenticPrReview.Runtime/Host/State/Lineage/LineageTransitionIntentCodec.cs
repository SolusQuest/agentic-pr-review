using System.Collections.Immutable;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageTransitionIntentCodec
{
    private const byte IntentRecordKind = 1;

    internal static StateObjectClass ObjectClass(
        LineageTransitionIntentKind kind) =>
        kind switch
        {
            LineageTransitionIntentKind.Reset => StateObjectClass.Reset,
            LineageTransitionIntentKind.Expiry =>
                StateObjectClass.ExpiryTransition,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    internal static bool TryEncode(
        LineageTransitionIntentV1? intent,
        out byte[] payload)
    {
        payload = [];
        if (!IsValid(intent))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(LineageFormat.IntentMagic);
        writer.WriteUInt16(LineageFormat.Version);
        writer.WriteByte(IntentRecordKind);
        writer.WriteByte((byte)intent!.Kind);
        writer.WriteString(intent.PriorHeadIdentity);
        writer.WriteString(intent.PriorEpoch);
        writer.WriteString(intent.TransitionEvidenceIdentity);
        writer.WriteByte(intent.ExpiryBoundaryUnixSeconds is null ?
            (byte)0 : (byte)1);
        if (intent.ExpiryBoundaryUnixSeconds is not null)
        {
            writer.WriteInt64(intent.ExpiryBoundaryUnixSeconds.Value);
        }

        writer.WriteString(intent.InventorySha256);
        writer.WriteUInt32(checked((uint)intent.Targets.Length));
        foreach (var target in intent.Targets
            .OrderBy(value => value.Name, StringComparer.Ordinal)
            .ThenBy(value => value.ObjectId, StringComparer.Ordinal)
            .ThenBy(value => value.ArchiveSha256, StringComparer.Ordinal)
            .ThenBy(value => value.EncryptedObjectSha256, StringComparer.Ordinal))
        {
            writer.WriteString(target.Name);
            writer.WriteString(target.ObjectId);
            writer.WriteString(target.ArchiveSha256);
            writer.WriteString(target.EncryptedObjectSha256);
        }

        payload = writer.ToArray();
        return payload.Length <= LineageFormat.MaximumPayloadBytes;
    }

    internal static bool TryDecode(
        StateObjectClass objectClass,
        ReadOnlySpan<byte> payload,
        out LineageTransitionIntentV1? intent)
    {
        intent = null;
        if (objectClass is not StateObjectClass.Reset and not
            StateObjectClass.ExpiryTransition ||
            payload.Length is < 1 or > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(payload);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, LineageFormat.IntentMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != LineageFormat.Version ||
            !reader.TryReadByte(out var recordKind) ||
            recordKind != IntentRecordKind ||
            !reader.TryReadByte(out var kindValue) ||
            !Enum.IsDefined((LineageTransitionIntentKind)kindValue) ||
            ObjectClass((LineageTransitionIntentKind)kindValue) != objectClass ||
            !reader.TryReadString(64, out var priorHeadIdentity) ||
            !reader.TryReadString(64, out var priorEpoch) ||
            !reader.TryReadString(64, out var transitionEvidenceIdentity) ||
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

        if (!reader.TryReadString(64, out var inventorySha256) ||
            !reader.TryReadUInt32(out var count) ||
            count > LineageFormat.MaximumEvidenceObjects)
        {
            return false;
        }

        var targets = ImmutableArray.CreateBuilder<LineageArtifactEvidence>(
            checked((int)count));
        for (var index = 0; index < count; index++)
        {
            if (!reader.TryReadString(256, out var name) ||
                !reader.TryReadString(256, out var objectId) ||
                !reader.TryReadString(64, out var archiveSha256) ||
                !reader.TryReadString(64, out var encryptedSha256))
            {
                return false;
            }

            targets.Add(new LineageArtifactEvidence(
                name,
                objectId,
                archiveSha256,
                encryptedSha256));
        }

        if (!reader.IsComplete)
        {
            return false;
        }

        var candidate = new LineageTransitionIntentV1(
            (LineageTransitionIntentKind)kindValue,
            priorHeadIdentity,
            priorEpoch,
            transitionEvidenceIdentity,
            expiryBoundary,
            inventorySha256,
            targets.MoveToImmutable());
        if (!IsValid(candidate))
        {
            return false;
        }

        intent = candidate;
        return true;
    }

    internal static bool IsValid(LineageTransitionIntentV1? intent) =>
        intent is not null &&
        Enum.IsDefined(intent.Kind) &&
        LineageValidation.IsSha256(intent.PriorHeadIdentity) &&
        LineageValidation.IsSha256(intent.PriorEpoch) &&
        LineageValidation.IsSha256(intent.TransitionEvidenceIdentity) &&
        LineageValidation.IsSha256(intent.InventorySha256) &&
        !intent.Targets.IsDefault &&
        intent.Targets.Length <= LineageFormat.MaximumEvidenceObjects &&
        intent.Targets.All(LineageValidation.IsValid) &&
        LineageCryptography.InventoryDigest(intent.Targets) ==
            intent.InventorySha256 &&
        intent.Kind switch
        {
            LineageTransitionIntentKind.Reset =>
                intent.ExpiryBoundaryUnixSeconds is null,
            LineageTransitionIntentKind.Expiry =>
                intent.ExpiryBoundaryUnixSeconds is not null &&
                LineageValidation.IsTime(
                    intent.ExpiryBoundaryUnixSeconds.Value),
            _ => false,
        };
}
