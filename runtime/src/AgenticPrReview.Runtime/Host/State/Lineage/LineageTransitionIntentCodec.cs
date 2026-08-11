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
        writer.WriteOptionalString(intent.ResetAuthorityRunIdentity);
        writer.WriteByte(intent.ResetAuthorityRunAttempt is null ?
            (byte)0 : (byte)1);
        if (intent.ResetAuthorityRunAttempt is not null)
        {
            writer.WriteInt64(intent.ResetAuthorityRunAttempt.Value);
        }

        writer.WriteByte(intent.ExpiryBoundaryUnixSeconds is null ?
            (byte)0 : (byte)1);
        if (intent.ExpiryBoundaryUnixSeconds is not null)
        {
            writer.WriteInt64(intent.ExpiryBoundaryUnixSeconds.Value);
        }

        writer.WriteString(intent.Reviewed.BaseSha);
        writer.WriteString(intent.Reviewed.HeadSha);
        writer.WriteString(intent.InventorySha256);
        LineageHeadCodec.WriteEvidence(writer, intent.Targets);

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

        if (!reader.TryReadString(64, out var baseSha) ||
            !reader.TryReadString(64, out var headSha) ||
            !reader.TryReadString(64, out var inventorySha256) ||
            !LineageHeadCodec.TryReadEvidence(ref reader, out var targets) ||
            !reader.IsComplete)
        {
            return false;
        }

        var candidate = new LineageTransitionIntentV1(
            (LineageTransitionIntentKind)kindValue,
            priorHeadIdentity,
            priorEpoch,
            transitionEvidenceIdentity,
            expiryBoundary,
            new ReviewedTransitionFacts(baseSha, headSha),
            inventorySha256,
            targets,
            resetAuthorityRunIdentity,
            resetAuthorityRunAttempt);
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
        ((intent.ResetAuthorityRunIdentity is null) ==
            (intent.ResetAuthorityRunAttempt is null)) &&
        (intent.ResetAuthorityRunIdentity is null ||
            LineageValidation.IsText(
                intent.ResetAuthorityRunIdentity,
                LineageFormat.MaximumRunIdentityBytes)) &&
        (intent.ResetAuthorityRunAttempt is null ||
            intent.ResetAuthorityRunAttempt >= 0) &&
        LineageValidation.IsValid(intent.Reviewed) &&
        LineageValidation.IsSha256(intent.InventorySha256) &&
        !intent.Targets.IsDefault &&
        intent.Targets.Length <= LineageFormat.MaximumEvidenceObjects &&
        intent.Targets.All(LineageValidation.IsValid) &&
        LineageCryptography.InventoryDigest(intent.Targets) ==
            intent.InventorySha256 &&
        intent.Kind switch
        {
            LineageTransitionIntentKind.Reset =>
                intent.ExpiryBoundaryUnixSeconds is null &&
                intent.ResetAuthorityRunIdentity is not null &&
                intent.ResetAuthorityRunAttempt is not null,
            LineageTransitionIntentKind.Expiry =>
                intent.ExpiryBoundaryUnixSeconds is not null &&
                LineageValidation.IsTime(
                    intent.ExpiryBoundaryUnixSeconds.Value) &&
                intent.ResetAuthorityRunIdentity is null &&
                intent.ResetAuthorityRunAttempt is null,
            _ => false,
        };
}
