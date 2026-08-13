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

internal static class PublicationRecoveryRecordClasses
{
    internal static StateObjectClass For(PublicationRecoveryRecordKind kind) =>
        kind switch
        {
            PublicationRecoveryRecordKind.PublicationFailure =>
                StateObjectClass.PublicationFailure,
            PublicationRecoveryRecordKind.Abandonment =>
                StateObjectClass.Abandonment,
            _ => StateObjectClass.PublicationIntent,
        };
}

internal static class PublicationIntentV1Codec
{
    internal static bool TryCreate(
        PublicationRecoveryBindingV1 binding,
        long createdAtUnixSeconds,
        out PublicationIntentV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateIntent(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAtUnixSeconds,
        out StickyReadbackRecordV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateReadback(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAtUnixSeconds,
        out PublicationFailureV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateFailure(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        string completeMarkerAbsenceEvidenceIdentity,
        long abandonedAtUnixSeconds,
        out AbandonmentV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateAbandonment(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        string stickyReadbackRecordIdentity,
        ImmutableArray<byte> acceptanceRecoveryHandoff,
        long minimumSemanticExpiresAtUnixSeconds,
        out RecoveryRecordV1? value) =>
        PublicationRecoveryPayloadCodec.TryCreateRecovery(
            binding,
            stickyReadbackRecordIdentity,
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
        PublicationRecoveryBindingV1 binding,
        long createdAt,
        out PublicationIntentV1? value)
    {
        value = null;
        var provisional = new PublicationIntentV1(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        StickyCommentPublisher.StickyPublicationReceipt receipt,
        long observedAt,
        out StickyReadbackRecordV1? value)
    {
        value = null;
        if (receipt is null ||
            !StringComparer.Ordinal.Equals(
                receipt.ScopeSha256,
                binding.ScopeSha256) ||
            !StringComparer.Ordinal.Equals(
                receipt.BodySha256,
                binding.BodySha256) ||
            !StringComparer.Ordinal.Equals(
                receipt.HeadSha,
                binding.ReviewedHeadSha))
        {
            return false;
        }

        var provisional = new StickyReadbackRecordV1(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        BoundedGitHubPublisherOutcome outcome,
        StickyPublicationReason reason,
        long failedAt,
        out PublicationFailureV1? value)
    {
        value = null;
        var provisional = new PublicationFailureV1(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        string evidenceIdentity,
        long abandonedAt,
        out AbandonmentV1? value)
    {
        value = null;
        var provisional = new AbandonmentV1(
            binding,
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
        PublicationRecoveryBindingV1 binding,
        string stickyIdentity,
        ImmutableArray<byte> handoff,
        long minimumSemanticExpiry,
        out RecoveryRecordV1? value)
    {
        value = null;
        var provisional = new RecoveryRecordV1(
            binding,
            stickyIdentity,
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
                out var binding) &&
            reader.TryReadInt64(out var createdAt) &&
            reader.TryReadString(64, out var identity) &&
            reader.IsComplete &&
            TryCreateIntent(binding!, createdAt, out var canonical) &&
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
                out var binding) ||
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
                binding!.ScopeSha256,
                binding.BodySha256,
                binding.ReviewedHeadSha,
                out var receipt) ||
            receipt is null ||
            !TryCreateReadback(
                binding,
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
                out var binding) ||
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
                binding!,
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
                out var binding) ||
            !reader.TryReadString(64, out var evidence) ||
            !reader.TryReadInt64(out var abandonedAt) ||
            !reader.TryReadString(64, out var identity) ||
            !reader.IsComplete ||
            !TryCreateAbandonment(
                binding!,
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
                    out var binding) ||
                !reader.TryReadString(64, out var stickyIdentity))
            {
                return false;
            }

            handoffOffset = PrefixLength(
                PublicationRecoveryRecordKind.Recovery,
                binding!,
                stickyIdentity);
            if (!reader.TryReadBytes(
                    LineageFormat.MaximumPayloadBytes,
                    out handoff) ||
                handoff.Length == 0 ||
                !reader.TryReadInt64(out var minimumExpiry) ||
                !reader.TryReadString(64, out var identity) ||
                !reader.IsComplete ||
                !TryCreateRecovery(
                    binding!,
                    stickyIdentity,
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
            if (!TryKindAndBinding(
                    value,
                    out var kind,
                    out var binding,
                    out var identity) ||
                binding is null ||
                !ValidBinding(binding))
            {
                return false;
            }

            var writer = new LineageBinaryWriter();
            WriteHeader(writer, kind, binding);
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
                    when LineageValidation.IsSha256(
                            recovery.StickyReadbackRecordIdentity) &&
                        !recovery.AcceptanceRecoveryHandoff.IsDefaultOrEmpty &&
                        recovery.AcceptanceRecoveryHandoff.Length <=
                            LineageFormat.MaximumPayloadBytes &&
                        LineageValidation.IsTime(
                            recovery.MinimumSemanticExpiresAtUnixSeconds):
                    writer.WriteString(
                        recovery.StickyReadbackRecordIdentity);
                    handoffOffset = PrefixLength(
                        kind,
                        binding,
                        recovery.StickyReadbackRecordIdentity);
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
        out PublicationRecoveryBindingV1? binding)
    {
        reader = new LineageBinaryReader(bytes);
        binding = null;
        if (bytes.Length is < 1 or > LineageFormat.MaximumPayloadBytes ||
            !reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, Magic) ||
            !reader.TryReadUInt16(out var version) ||
            version != Version ||
            !reader.TryReadUInt16(out var kindValue) ||
            kindValue != (ushort)expectedKind ||
            !reader.TryReadString(64, out var baseScope) ||
            !reader.TryReadString(64, out var epoch) ||
            !reader.TryReadString(64, out var session) ||
            !reader.TryReadOptionalString(64, out var predecessor) ||
            !reader.TryReadString(64, out var candidate) ||
            !reader.TryReadString(40, out var head) ||
            !reader.TryReadString(64, out var scope) ||
            !reader.TryReadString(64, out var body))
        {
            return false;
        }

        var parsed = new PublicationRecoveryBindingV1(
            baseScope,
            epoch,
            session,
            predecessor,
            candidate,
            head,
            scope,
            body);
        if (!ValidBinding(parsed))
        {
            return false;
        }

        binding = parsed;
        return true;
    }

    private static void WriteHeader(
        LineageBinaryWriter writer,
        PublicationRecoveryRecordKind kind,
        PublicationRecoveryBindingV1 binding)
    {
        writer.WriteString(Magic);
        writer.WriteUInt16(Version);
        writer.WriteUInt16((ushort)kind);
        writer.WriteString(binding.BaseScopeDigest);
        writer.WriteString(binding.Epoch);
        writer.WriteString(binding.SessionId);
        writer.WriteOptionalString(binding.PredecessorAcceptanceIdentity);
        writer.WriteString(binding.CandidateObjectIdentity);
        writer.WriteString(binding.ReviewedHeadSha);
        writer.WriteString(binding.ScopeSha256);
        writer.WriteString(binding.BodySha256);
    }

    private static int PrefixLength(
        PublicationRecoveryRecordKind kind,
        PublicationRecoveryBindingV1 binding,
        string stickyIdentity)
    {
        var writer = new LineageBinaryWriter();
        WriteHeader(writer, kind, binding);
        writer.WriteString(stickyIdentity);
        writer.WriteUInt32(0);
        return writer.ToArray().Length;
    }

    private static bool TryKindAndBinding(
        object? value,
        out PublicationRecoveryRecordKind kind,
        out PublicationRecoveryBindingV1? binding,
        out string? identity)
    {
        (kind, binding, identity) = value switch
        {
            PublicationIntentV1 item => (
                PublicationRecoveryRecordKind.PublicationIntent,
                item.Binding,
                item.RecordIdentity),
            StickyReadbackRecordV1 item => (
                PublicationRecoveryRecordKind.StickyReadback,
                item.Binding,
                item.RecordIdentity),
            PublicationFailureV1 item => (
                PublicationRecoveryRecordKind.PublicationFailure,
                item.Binding,
                item.RecordIdentity),
            AbandonmentV1 item => (
                PublicationRecoveryRecordKind.Abandonment,
                item.Binding,
                item.RecordIdentity),
            RecoveryRecordV1 item => (
                PublicationRecoveryRecordKind.Recovery,
                item.Binding,
                item.RecordIdentity),
            _ => (default, null, null),
        };
        return binding is not null;
    }

    private static bool ValidBinding(PublicationRecoveryBindingV1? value) =>
        value is not null &&
        LineageValidation.IsSha256(value.BaseScopeDigest) &&
        LineageValidation.IsSha256(value.Epoch) &&
        LineageValidation.IsSha256(value.SessionId) &&
        LineageValidation.IsOptionalSha256(
            value.PredecessorAcceptanceIdentity) &&
        LineageValidation.IsSha256(value.CandidateObjectIdentity) &&
        IsLowerHex(value.ReviewedHeadSha, 40) &&
        LineageValidation.IsSha256(value.ScopeSha256) &&
        LineageValidation.IsSha256(value.BodySha256);

    private static bool ValidFailure(PublicationFailureV1 value) =>
        value.Outcome is
            BoundedGitHubPublisherOutcome.KnownNotWritten or
            BoundedGitHubPublisherOutcome.OutcomeUnknown or
            BoundedGitHubPublisherOutcome.CancelledBeforeSend or
            BoundedGitHubPublisherOutcome.AuthorizationOrValidationFailure &&
        Enum.IsDefined(value.Reason) &&
        value.Reason != StickyPublicationReason.None &&
        LineageValidation.IsTime(value.FailedAtUnixSeconds);

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
