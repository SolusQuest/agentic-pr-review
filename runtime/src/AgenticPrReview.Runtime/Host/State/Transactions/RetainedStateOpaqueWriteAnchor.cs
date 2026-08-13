using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Transactions;

internal sealed record RetainedStateOpaqueWriteAnchor(
    string CandidateObjectIdentity,
    string OperationIdentity,
    StateObjectClass ObjectClass,
    string? PredecessorIdentity,
    string? SuccessorIdentity,
    long SemanticRequiredExpiresAtUnixSeconds,
    long RequiredPlatformExpiresAtUnixSeconds,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    OpaqueStoreName TargetName,
    string TargetObjectIdentity,
    ImmutableArray<byte> TargetEnvelope,
    string TargetEnvelopeSha256,
    RetainedStateOpaqueWriteAnchorPhase DispatchPhase,
    string TargetPayloadSha256);

internal enum RetainedStateOpaqueWriteAnchorPhase
{
    PreparedBeforeTargetDispatch = 1,
}

internal static class RetainedStateOpaqueWriteAnchorCodec
{
    internal const string Magic = "APROWA01";
    internal const ushort Version = 1;

    internal static bool TryEncode(
        RetainedStateOpaqueWriteAnchor anchor,
        out byte[] bytes)
    {
        bytes = [];
        if (!IsValid(anchor))
        {
            return false;
        }

        try
        {
            var writer = new LineageBinaryWriter();
            writer.WriteString(Magic);
            writer.WriteUInt16(Version);
            writer.WriteString(anchor.CandidateObjectIdentity);
            writer.WriteString(anchor.OperationIdentity);
            writer.WriteString(StateObjectClasses.ToWireName(
                anchor.ObjectClass));
            writer.WriteOptionalString(anchor.PredecessorIdentity);
            writer.WriteOptionalString(anchor.SuccessorIdentity);
            writer.WriteInt64(
                anchor.SemanticRequiredExpiresAtUnixSeconds);
            writer.WriteInt64(
                anchor.RequiredPlatformExpiresAtUnixSeconds);
            writer.WriteString(anchor.ProducingRunIdentity);
            writer.WriteInt64(anchor.ProducingRunAttempt);
            writer.WriteString(anchor.TargetName.Value);
            writer.WriteString(anchor.TargetObjectIdentity);
            writer.WriteBytes(anchor.TargetEnvelope.AsSpan());
            writer.WriteString(anchor.TargetEnvelopeSha256);
            writer.WriteUInt16((ushort)anchor.DispatchPhase);
            writer.WriteString(anchor.TargetPayloadSha256);
            bytes = writer.ToArray();
            return bytes.Length <= LineageFormat.MaximumPayloadBytes;
        }
        catch (Exception exception) when (
            exception is ArgumentException or OverflowException)
        {
            bytes = [];
            return false;
        }
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out RetainedStateOpaqueWriteAnchor? anchor)
    {
        anchor = null;
        if (bytes.Length is < 1 or > LineageFormat.MaximumPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(magic, Magic) ||
            !reader.TryReadUInt16(out var version) ||
            version != Version ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var candidateIdentity) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var operationIdentity) ||
            !reader.TryReadString(64, out var objectClassValue) ||
            !StateObjectClasses.TryParse(objectClassValue, out var objectClass) ||
            !reader.TryReadOptionalString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var predecessorIdentity) ||
            !reader.TryReadOptionalString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var successorIdentity) ||
            !reader.TryReadInt64(out var semanticExpiry) ||
            !reader.TryReadInt64(out var platformExpiry) ||
            !reader.TryReadString(
                LineageFormat.MaximumRunIdentityBytes,
                out var producingRunIdentity) ||
            !reader.TryReadInt64(out var producingRunAttempt) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumNameBytes,
                out var targetNameValue) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var targetObjectIdentity) ||
            !reader.TryReadBytes(
                LineageFormat.MaximumEnvelopeBytes,
                out var targetEnvelope) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var targetEnvelopeSha256) ||
            !reader.TryReadUInt16(out var dispatchPhaseValue) ||
            !Enum.IsDefined(
                typeof(RetainedStateOpaqueWriteAnchorPhase),
                (int)dispatchPhaseValue) ||
            !reader.TryReadString(
                OpaqueStoreLimits.MaximumIdentityBytes,
                out var targetPayloadSha256) ||
            !reader.IsComplete)
        {
            return false;
        }

        var parsed = new RetainedStateOpaqueWriteAnchor(
            candidateIdentity,
            operationIdentity,
            objectClass,
            predecessorIdentity,
            successorIdentity,
            semanticExpiry,
            platformExpiry,
            producingRunIdentity,
            producingRunAttempt,
            new OpaqueStoreName(targetNameValue),
            targetObjectIdentity,
            ImmutableArray.CreateRange(targetEnvelope),
            targetEnvelopeSha256,
            (RetainedStateOpaqueWriteAnchorPhase)dispatchPhaseValue,
            targetPayloadSha256);
        if (!IsValid(parsed))
        {
            return false;
        }

        anchor = parsed;
        return true;
    }

    internal static bool IsAnchor(ReadOnlySpan<byte> bytes) =>
        TryDecode(bytes, out _);

    private static bool IsValid(RetainedStateOpaqueWriteAnchor? anchor) =>
        anchor is not null &&
        LineageValidation.IsSha256(anchor.CandidateObjectIdentity) &&
        LineageValidation.IsSha256(anchor.OperationIdentity) &&
        anchor.ObjectClass is (
            StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment) &&
        LineageValidation.IsOptionalSha256(anchor.PredecessorIdentity) &&
        LineageValidation.IsOptionalSha256(anchor.SuccessorIdentity) &&
        StringComparer.Ordinal.Equals(
            anchor.PredecessorIdentity,
            anchor.CandidateObjectIdentity) &&
        LineageValidation.IsTime(
            anchor.SemanticRequiredExpiresAtUnixSeconds) &&
        LineageValidation.IsTime(
            anchor.RequiredPlatformExpiresAtUnixSeconds) &&
        anchor.RequiredPlatformExpiresAtUnixSeconds >=
            anchor.SemanticRequiredExpiresAtUnixSeconds &&
        LineageValidation.IsText(
            anchor.ProducingRunIdentity,
            LineageFormat.MaximumRunIdentityBytes) &&
        anchor.ProducingRunAttempt >= 0 &&
        OpaqueStoreValidation.IsValid(anchor.TargetName) &&
        LineageValidation.IsSha256(anchor.TargetObjectIdentity) &&
        anchor.TargetEnvelope.Length is > 0 and <=
            LineageFormat.MaximumEnvelopeBytes &&
        LineageValidation.IsSha256(anchor.TargetEnvelopeSha256) &&
        StringComparer.Ordinal.Equals(
            anchor.TargetEnvelopeSha256,
            OpaqueStoreHash.Sha256(anchor.TargetEnvelope.AsSpan())) &&
        anchor.DispatchPhase ==
            RetainedStateOpaqueWriteAnchorPhase
                .PreparedBeforeTargetDispatch &&
        LineageValidation.IsSha256(anchor.TargetPayloadSha256);
}
