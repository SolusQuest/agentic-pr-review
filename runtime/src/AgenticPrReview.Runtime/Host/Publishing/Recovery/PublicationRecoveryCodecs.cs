using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Common;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal enum PublicationRecoveryRecordKind : ushort
{
    PublicationIntent = 1,
    StickyReadback,
    PublicationFailure,
    Abandonment,
    Recovery,
}

internal static class PublicationIntentV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryPublicationV1 publication,
        long createdAtUnixSeconds,
        out PublicationIntentV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateIntent(
            publication,
            createdAtUnixSeconds,
            out value);

    internal static bool TryEncode(PublicationIntentV1? value, out byte[] bytes) =>
        PublicationRecoveryPayloadCodec.TryEncode(value, out bytes);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out PublicationIntentV1? value) =>
        PublicationRecoveryPayloadCodec.TryDecode(bytes, out value);
}

internal static class StickyReadbackRecordV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryPublicationV1 publication,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAtUnixSeconds,
        out StickyReadbackRecordV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateReadback(
            publication,
            receipt,
            observedAtUnixSeconds,
            out value);

    internal static bool TryEncode(
        StickyReadbackRecordV1? value,
        out byte[] bytes) =>
        PublicationRecoveryPayloadCodec.TryEncode(value, out bytes);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out StickyReadbackRecordV1? value) =>
        PublicationRecoveryPayloadCodec.TryDecode(bytes, out value);
}

internal static class PublicationFailureV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryPublicationV1 publication,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAtUnixSeconds,
        out PublicationFailureV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateFailure(
            publication,
            outcome,
            reason,
            failedAtUnixSeconds,
            out value);

    internal static bool TryEncode(
        PublicationFailureV1? value,
        out byte[] bytes) =>
        PublicationRecoveryPayloadCodec.TryEncode(value, out bytes);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out PublicationFailureV1? value) =>
        PublicationRecoveryPayloadCodec.TryDecode(bytes, out value);
}

internal static class AbandonmentV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryPublicationV1 publication,
        string completeMarkerAbsenceEvidenceIdentity,
        long abandonedAtUnixSeconds,
        out AbandonmentV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateAbandonment(
            publication,
            completeMarkerAbsenceEvidenceIdentity,
            abandonedAtUnixSeconds,
            out value);

    internal static bool TryEncode(AbandonmentV1? value, out byte[] bytes) =>
        PublicationRecoveryPayloadCodec.TryEncode(value, out bytes);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out AbandonmentV1? value) =>
        PublicationRecoveryPayloadCodec.TryDecode(bytes, out value);
}

internal static class RecoveryRecordV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryPublicationV1 publication,
        StickyReadbackRecordV1 stickyReadback,
        ImmutableArray<byte> acceptanceRecoveryHandoff,
        long minimumSemanticExpiresAtUnixSeconds,
        out RecoveryRecordV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateRecovery(
            publication,
            stickyReadback,
            acceptanceRecoveryHandoff,
            minimumSemanticExpiresAtUnixSeconds,
            out value);

    internal static bool TryEncode(
        RecoveryRecordV1? value,
        out byte[] bytes,
        out int handoffOffset,
        out int handoffLength) =>
        PublicationRecoveryPayloadCodec.TryEncodeRecovery(
            value,
            out bytes,
            out handoffOffset,
            out handoffLength);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out RecoveryRecordV1? value,
        out int handoffOffset,
        out int handoffLength) =>
        PublicationRecoveryPayloadCodec.TryDecodeRecovery(
            bytes,
            out value,
            out handoffOffset,
            out handoffLength);
}

internal static class PublicationRecoveryPayloadCodec
{
    internal const string Magic = "APR5RC01";
    internal const ushort Version = 1;
    internal const string IdentityDomain =
        "apr.publication-recovery.p5/v1";
    private const int MaximumUrlBytes = 2_048;

    internal static bool TryCreateIntent(
        PublicationRecoveryPublicationV1 publication,
        long createdAt,
        out PublicationIntentV1? value)
    {
        value = null;
        var provisional = new PublicationIntentV1(
            publication,
            createdAt,
            Zeros());
        if (!TryIdentity(provisional, out var identity))
        {
            return false;
        }

        value = provisional with { RecordIdentity = identity };
        return true;
    }

    internal static bool TryCreateReadback(
        PublicationRecoveryPublicationV1 publication,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAt,
        out StickyReadbackRecordV1? value)
    {
        value = null;
        if (receipt is null ||
            !StringComparer.Ordinal.Equals(
                receipt.ScopeSha256,
                publication.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(
                receipt.BodySha256,
                publication.BodySha256) ||
            !StringComparer.Ordinal.Equals(
                receipt.HeadSha,
                publication.ReviewedHeadSha))
        {
            return false;
        }

        var provisional = new StickyReadbackRecordV1(
            publication,
            receipt.Operation,
            receipt.RepositoryId,
            receipt.PullRequestNumber,
            receipt.CommentId,
            receipt.CommentUrl,
            observedAt,
            Zeros());
        if (!provisional.TryRehydrate(out _) ||
            !TryIdentity(provisional, out var identity))
        {
            return false;
        }

        value = provisional with { RecordIdentity = identity };
        return true;
    }

    internal static bool TryCreateFailure(
        PublicationRecoveryPublicationV1 publication,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAt,
        out PublicationFailureV1? value)
    {
        value = null;
        var provisional = new PublicationFailureV1(
            publication,
            outcome,
            reason,
            failedAt,
            Zeros());
        if (!TryIdentity(provisional, out var identity))
        {
            return false;
        }

        value = provisional with { RecordIdentity = identity };
        return true;
    }

    internal static bool TryCreateAbandonment(
        PublicationRecoveryPublicationV1 publication,
        string evidenceIdentity,
        long abandonedAt,
        out AbandonmentV1? value)
    {
        value = null;
        var provisional = new AbandonmentV1(
            publication,
            evidenceIdentity,
            abandonedAt,
            Zeros());
        if (!TryIdentity(provisional, out var identity))
        {
            return false;
        }

        value = provisional with { RecordIdentity = identity };
        return true;
    }

    internal static bool TryCreateRecovery(
        PublicationRecoveryPublicationV1 publication,
        StickyReadbackRecordV1 stickyReadback,
        ImmutableArray<byte> handoff,
        long minimumSemanticExpiry,
        out RecoveryRecordV1? value)
    {
        value = null;
        if (stickyReadback is null ||
            stickyReadback.Publication != publication ||
            !stickyReadback.TryRehydrate(out _))
        {
            return false;
        }

        var provisional = new RecoveryRecordV1(
            publication,
            stickyReadback,
            handoff,
            minimumSemanticExpiry,
            Zeros());
        if (!TryIdentity(provisional, out var identity))
        {
            return false;
        }

        value = provisional with { RecordIdentity = identity };
        return true;
    }

    internal static bool TryEncode(
        PublicationIntentV1? value,
        out byte[] bytes) =>
        TryEncodeCore(value, includeIdentity: true, out bytes, out _, out _);

    internal static bool TryEncode(
        StickyReadbackRecordV1? value,
        out byte[] bytes) =>
        TryEncodeCore(value, includeIdentity: true, out bytes, out _, out _);

    internal static bool TryEncode(
        PublicationFailureV1? value,
        out byte[] bytes) =>
        TryEncodeCore(value, includeIdentity: true, out bytes, out _, out _);

    internal static bool TryEncode(
        AbandonmentV1? value,
        out byte[] bytes) =>
        TryEncodeCore(value, includeIdentity: true, out bytes, out _, out _);

    internal static bool TryEncodeRecovery(
        RecoveryRecordV1? value,
        out byte[] bytes,
        out int handoffOffset,
        out int handoffLength) =>
        TryEncodeCore(
            value,
            includeIdentity: true,
            out bytes,
            out handoffOffset,
            out handoffLength);

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out PublicationIntentV1? value)
    {
        value = null;
        return TryReadHeader(
                bytes,
                PublicationRecoveryRecordKind.PublicationIntent,
                out var reader,
                out var publication) &&
            reader.TryReadInt64(out var createdAt) &&
            reader.TryReadString(64, out var identity) &&
            reader.IsComplete &&
            TryCreateIntent(publication!, createdAt, out var canonical) &&
            canonical is not null &&
            StringComparer.Ordinal.Equals(
                canonical.RecordIdentity,
                identity) &&
            Canonical(bytes, canonical, TryEncode, out value);
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out StickyReadbackRecordV1? value)
    {
        value = null;
        if (!TryReadHeader(
                bytes,
                PublicationRecoveryRecordKind.StickyReadback,
                out var reader,
                out var publication) ||
            !reader.TryReadUInt16(out var operationValue) ||
            !Enum.IsDefined(
                typeof(StickyPublicationOperation),
                (int)operationValue) ||
            !reader.TryReadInt64(out var repositoryId) ||
            !reader.TryReadInt64(out var pullRequest) ||
            !reader.TryReadInt64(out var commentId) ||
            !reader.TryReadString(MaximumUrlBytes, out var commentUrl) ||
            !reader.TryReadInt64(out var observedAt) ||
            !reader.TryReadString(64, out var identity) ||
            !reader.IsComplete ||
            !StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
                (StickyPublicationOperation)operationValue,
                repositoryId,
                pullRequest,
                commentId,
                commentUrl,
                publication!.ScopeSha256,
                publication.BodySha256,
                publication.ReviewedHeadSha,
                out var receipt) ||
            receipt is null ||
            !TryCreateReadback(
                publication,
                receipt,
                observedAt,
                out var canonical) ||
            canonical is null ||
            !StringComparer.Ordinal.Equals(
                canonical.RecordIdentity,
                identity))
        {
            return false;
        }

        return Canonical(bytes, canonical, TryEncode, out value);
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out PublicationFailureV1? value)
    {
        value = null;
        if (!TryReadHeader(
                bytes,
                PublicationRecoveryRecordKind.PublicationFailure,
                out var reader,
                out var publication) ||
            !reader.TryReadUInt16(out var outcomeValue) ||
            !Enum.IsDefined(
                typeof(BoundedGitHubPublisherOutcome),
                (int)outcomeValue) ||
            !reader.TryReadUInt16(out var reasonValue) ||
            !Enum.IsDefined(
                typeof(StickyPublicationReason),
                (int)reasonValue) ||
            !reader.TryReadInt64(out var failedAt) ||
            !reader.TryReadString(64, out var identity) ||
            !reader.IsComplete ||
            !TryCreateFailure(
                publication!,
                (BoundedGitHubPublisherOutcome)outcomeValue,
                (StickyPublicationReason)reasonValue,
                failedAt,
                out var canonical) ||
            canonical is null ||
            !StringComparer.Ordinal.Equals(
                canonical.RecordIdentity,
                identity))
        {
            return false;
        }

        return Canonical(bytes, canonical, TryEncode, out value);
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out AbandonmentV1? value)
    {
        value = null;
        if (!TryReadHeader(
                bytes,
                PublicationRecoveryRecordKind.Abandonment,
                out var reader,
                out var publication) ||
            !reader.TryReadString(64, out var evidence) ||
            !reader.TryReadInt64(out var abandonedAt) ||
            !reader.TryReadString(64, out var identity) ||
            !reader.IsComplete ||
            !TryCreateAbandonment(
                publication!,
                evidence,
                abandonedAt,
                out var canonical) ||
            canonical is null ||
            !StringComparer.Ordinal.Equals(
                canonical.RecordIdentity,
                identity))
        {
            return false;
        }

        return Canonical(bytes, canonical, TryEncode, out value);
    }

    internal static bool TryDecodeRecovery(
        ReadOnlySpan<byte> bytes,
        out RecoveryRecordV1? value,
        out int handoffOffset,
        out int handoffLength)
    {
        value = null;
        handoffOffset = 0;
        handoffLength = 0;
        byte[] handoff = [];
        try
        {
            if (!TryReadHeader(
                    bytes,
                    PublicationRecoveryRecordKind.Recovery,
                    out var reader,
                    out var publication) ||
                !reader.TryReadUInt16(out var operationValue) ||
                !Enum.IsDefined(
                    typeof(StickyPublicationOperation),
                    (int)operationValue) ||
                !reader.TryReadInt64(out var repositoryId) ||
                !reader.TryReadInt64(out var pullRequest) ||
                !reader.TryReadInt64(out var commentId) ||
                !reader.TryReadString(MaximumUrlBytes, out var commentUrl) ||
                !reader.TryReadInt64(out var observedAt) ||
                !reader.TryReadString(64, out var stickyIdentity) ||
                !StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
                    (StickyPublicationOperation)operationValue,
                    repositoryId,
                    pullRequest,
                    commentId,
                    commentUrl,
                    publication!.ScopeSha256,
                    publication.BodySha256,
                    publication.ReviewedHeadSha,
                    out var receipt) ||
                receipt is null ||
                !TryCreateReadback(
                    publication,
                    receipt,
                    observedAt,
                    out var stickyReadback) ||
                stickyReadback is null ||
                !StringComparer.Ordinal.Equals(
                    stickyReadback.RecordIdentity,
                    stickyIdentity))
            {
                return false;
            }

            handoffOffset = PrefixLength(
                PublicationRecoveryRecordKind.Recovery,
                publication!,
                stickyReadback);
            if (!reader.TryReadBytes(
                    LineageFormat.MaximumPayloadBytes,
                    out handoff) ||
                handoff.Length == 0 ||
                !reader.TryReadInt64(out var minimumExpiry) ||
                !reader.TryReadString(64, out var identity) ||
                !reader.IsComplete ||
                !TryCreateRecovery(
                    publication!,
                    stickyReadback,
                    ImmutableArray.CreateRange(handoff),
                    minimumExpiry,
                    out var canonical) ||
                canonical is null ||
                !StringComparer.Ordinal.Equals(
                    canonical.RecordIdentity,
                    identity) ||
                !TryEncodeRecovery(
                    canonical,
                    out var encoded,
                    out var canonicalOffset,
                    out var canonicalLength))
            {
                return false;
            }

            try
            {
                if (!bytes.SequenceEqual(encoded) ||
                    canonicalOffset != handoffOffset ||
                    canonicalLength != handoff.Length)
                {
                    return false;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(encoded);
            }

            handoffLength = handoff.Length;
            value = canonical;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(handoff);
            if (value is null)
            {
                handoffOffset = 0;
                handoffLength = 0;
            }
        }
    }

    private static bool TryIdentity(object value, out string identity)
    {
        identity = string.Empty;
        if (!TryEncodeCore(
                value,
                includeIdentity: false,
                out var core,
                out _,
                out _))
        {
            return false;
        }

        try
        {
            var writer = new LineageBinaryWriter();
            writer.WriteString(IdentityDomain);
            writer.WriteBytes(core);
            var framed = writer.ToArray();
            try
            {
                identity = Convert.ToHexStringLower(
                    SHA256.HashData(framed));
                return true;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(framed);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(core);
        }
    }

    private static bool TryEncodeCore(
        object? value,
        bool includeIdentity,
        out byte[] bytes,
        out int handoffOffset,
        out int handoffLength)
    {
        bytes = [];
        handoffOffset = 0;
        handoffLength = 0;
        try
        {
            if (!TryKindAndPublication(
                    value,
                    out var kind,
                    out var publication,
                    out var identity) ||
                publication is null ||
                !ValidPublication(publication))
            {
                return false;
            }

            var writer = new LineageBinaryWriter();
            WriteHeader(writer, kind, publication);
            switch (value)
            {
                case PublicationIntentV1 intent
                    when LineageValidation.IsTime(
                        intent.CreatedAtUnixSeconds):
                    writer.WriteInt64(intent.CreatedAtUnixSeconds);
                    break;
                case StickyReadbackRecordV1 readback
                    when LineageValidation.IsTime(
                            readback.ObservedAtUnixSeconds) &&
                        readback.TryRehydrate(out _):
                    writer.WriteUInt16((ushort)readback.Operation);
                    writer.WriteInt64(readback.RepositoryId);
                    writer.WriteInt64(readback.PullRequestNumber);
                    writer.WriteInt64(readback.CommentId);
                    writer.WriteString(readback.CommentUrl);
                    writer.WriteInt64(readback.ObservedAtUnixSeconds);
                    break;
                case PublicationFailureV1 failure
                    when ValidFailure(failure):
                    writer.WriteUInt16((ushort)failure.Outcome);
                    writer.WriteUInt16((ushort)failure.Reason);
                    writer.WriteInt64(failure.FailedAtUnixSeconds);
                    break;
                case AbandonmentV1 abandonment
                    when LineageValidation.IsSha256(
                            abandonment
                                .CompleteMarkerAbsenceEvidenceIdentity) &&
                        LineageValidation.IsTime(
                            abandonment.AbandonedAtUnixSeconds):
                    writer.WriteString(
                        abandonment.CompleteMarkerAbsenceEvidenceIdentity);
                    writer.WriteInt64(abandonment.AbandonedAtUnixSeconds);
                    break;
                case RecoveryRecordV1 recovery
                    when recovery.StickyReadback is not null &&
                        recovery.StickyReadback.Publication == publication &&
                        LineageValidation.IsSha256(
                            recovery.StickyReadbackRecordIdentity) &&
                        recovery.StickyReadback.TryRehydrate(out _) &&
                        !recovery.AcceptanceRecoveryHandoff.IsDefaultOrEmpty &&
                        recovery.AcceptanceRecoveryHandoff.Length <=
                            LineageFormat.MaximumPayloadBytes &&
                        LineageValidation.IsTime(
                            recovery.MinimumSemanticExpiresAtUnixSeconds):
                    WriteReadbackFields(writer, recovery.StickyReadback);
                    handoffOffset = PrefixLength(
                        kind,
                        publication,
                        recovery.StickyReadback);
                    handoffLength =
                        recovery.AcceptanceRecoveryHandoff.Length;
                    writer.WriteBytes(
                        recovery.AcceptanceRecoveryHandoff.AsSpan());
                    writer.WriteInt64(
                        recovery.MinimumSemanticExpiresAtUnixSeconds);
                    break;
                default:
                    return false;
            }

            if (includeIdentity)
            {
                if (!LineageValidation.IsSha256(identity) ||
                    !TryIdentity(value!, out var expectedIdentity) ||
                    !StringComparer.Ordinal.Equals(
                        identity,
                        expectedIdentity))
                {
                    return false;
                }

                writer.WriteString(identity!);
            }

            bytes = writer.ToArray();
            return bytes.Length <= LineageFormat.MaximumPayloadBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            handoffOffset = 0;
            handoffLength = 0;
            return false;
        }
    }

    private static bool TryReadHeader(
        ReadOnlySpan<byte> bytes,
        PublicationRecoveryRecordKind expectedKind,
        out LineageBinaryReader reader,
        out PublicationRecoveryPublicationV1? publication)
    {
        reader = new LineageBinaryReader(bytes);
        publication = null;
        if (bytes.Length is < 1 or > LineageFormat.MaximumPayloadBytes ||
            !reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, Magic) ||
            !reader.TryReadUInt16(out var version) ||
            version != Version ||
            !reader.TryReadUInt16(out var kindValue) ||
            kindValue != (ushort)expectedKind ||
            !reader.TryReadString(40, out var head) ||
            !reader.TryReadString(64, out var scope) ||
            !reader.TryReadString(64, out var body))
        {
            return false;
        }

        var parsed = new PublicationRecoveryPublicationV1(
            head,
            scope,
            body);
        if (!ValidPublication(parsed))
        {
            return false;
        }

        publication = parsed;
        return true;
    }

    private static void WriteHeader(
        LineageBinaryWriter writer,
        PublicationRecoveryRecordKind kind,
        PublicationRecoveryPublicationV1 publication)
    {
        writer.WriteString(Magic);
        writer.WriteUInt16(Version);
        writer.WriteUInt16((ushort)kind);
        writer.WriteString(publication.ReviewedHeadSha);
        writer.WriteString(publication.ScopeSha256);
        writer.WriteString(publication.BodySha256);
    }

    private static int PrefixLength(
        PublicationRecoveryRecordKind kind,
        PublicationRecoveryPublicationV1 publication,
        StickyReadbackRecordV1 stickyReadback)
    {
        var writer = new LineageBinaryWriter();
        WriteHeader(writer, kind, publication);
        WriteReadbackFields(writer, stickyReadback);
        writer.WriteUInt32(0);
        return writer.ToArray().Length;
    }

    private static void WriteReadbackFields(
        LineageBinaryWriter writer,
        StickyReadbackRecordV1 readback)
    {
        writer.WriteUInt16((ushort)readback.Operation);
        writer.WriteInt64(readback.RepositoryId);
        writer.WriteInt64(readback.PullRequestNumber);
        writer.WriteInt64(readback.CommentId);
        writer.WriteString(readback.CommentUrl);
        writer.WriteInt64(readback.ObservedAtUnixSeconds);
        writer.WriteString(readback.RecordIdentity);
    }

    private static bool TryKindAndPublication(
        object? value,
        out PublicationRecoveryRecordKind kind,
        out PublicationRecoveryPublicationV1? publication,
        out string? identity)
    {
        (kind, publication, identity) = value switch
        {
            PublicationIntentV1 item => (
                PublicationRecoveryRecordKind.PublicationIntent,
                item.Publication,
                item.RecordIdentity),
            StickyReadbackRecordV1 item => (
                PublicationRecoveryRecordKind.StickyReadback,
                item.Publication,
                item.RecordIdentity),
            PublicationFailureV1 item => (
                PublicationRecoveryRecordKind.PublicationFailure,
                item.Publication,
                item.RecordIdentity),
            AbandonmentV1 item => (
                PublicationRecoveryRecordKind.Abandonment,
                item.Publication,
                item.RecordIdentity),
            RecoveryRecordV1 item => (
                PublicationRecoveryRecordKind.Recovery,
                item.Publication,
                item.RecordIdentity),
            _ => (default, null, null),
        };
        return publication is not null;
    }

    private static bool ValidPublication(
        PublicationRecoveryPublicationV1? value) =>
        value is not null &&
        IsLowerHex(value.ReviewedHeadSha, 40) &&
        LineageValidation.IsSha256(value.ScopeSha256) &&
        LineageValidation.IsSha256(value.BodySha256);

    private static bool ValidFailure(PublicationFailureV1 value) =>
        LineageValidation.IsTime(value.FailedAtUnixSeconds) &&
        value.Outcome switch
        {
            BoundedGitHubPublisherOutcome.KnownNotWritten =>
                value.Reason is StickyPublicationReason.RequestInvalid or
                    StickyPublicationReason.Deadline,
            BoundedGitHubPublisherOutcome.OutcomeUnknown =>
                value.Reason is
                    StickyPublicationReason.ReconciliationIncomplete or
                    StickyPublicationReason.Deadline,
            BoundedGitHubPublisherOutcome.CancelledBeforeSend =>
                value.Reason == StickyPublicationReason.Cancelled,
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure =>
                value.Reason is StickyPublicationReason.AdmissionInvalid or
                    StickyPublicationReason.DiscoveryIncomplete or
                    StickyPublicationReason.TargetConflict or
                    StickyPublicationReason.AuthorizationDenied,
            _ => false,
        };

    private static bool Canonical<T>(
        ReadOnlySpan<byte> source,
        T candidate,
        TryEncoder<T> encoder,
        out T? value) where T : class
    {
        value = null;
        if (!encoder(candidate, out var bytes))
        {
            return false;
        }

        try
        {
            if (!source.SequenceEqual(bytes))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private delegate bool TryEncoder<T>(T value, out byte[] bytes);

    private static bool IsLowerHex(string? value, int length) =>
        value is not null &&
        value.Length == length &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Zeros() => new('0', 64);
}
