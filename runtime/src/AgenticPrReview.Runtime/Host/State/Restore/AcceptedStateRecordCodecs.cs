using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal static class AcceptedStatePublicationPayloadCodec
{
    internal static bool TryEncode(
        ValidatedPublicationPayloadV1? value,
        out byte[] bytes)
    {
        bytes = [];
        if (value is null)
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(AcceptedStateFormat.PublicationMagic);
        writer.WriteUInt16(AcceptedStateFormat.Version);
        writer.WriteBytes(value.FinalizedCommentUtf8.AsSpan());
        writer.WriteInt64(value.RepositoryId);
        writer.WriteString(value.RepositoryName);
        writer.WriteInt64(value.PullRequestNumber);
        writer.WriteString(value.ScopeSha256);
        writer.WriteString(value.BodySha256);
        writer.WriteString(value.ReviewedHeadSha);
        writer.WriteString(value.PolicyIdentitySha256);
        writer.WriteString(value.PayloadSha256);
        writer.WriteString(value.BuildDiscriminator);
        writer.WriteString(value.RenderingVersion);
        var candidate = writer.ToArray();
        if (candidate.Length >
            AcceptedStateFormat.MaximumPublicationPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }

        bytes = candidate;
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out ValidatedPublicationPayloadV1? value)
    {
        value = null;
        if (bytes.Length is < 1 or >
            AcceptedStateFormat.MaximumPublicationPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(
                magic,
                AcceptedStateFormat.PublicationMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != AcceptedStateFormat.Version ||
            !reader.TryReadBytes(
                R4PublicationBudget.MaximumUtf8Bytes,
                out var commentBytes))
        {
            return false;
        }

        try
        {
            if (!reader.TryReadInt64(out var repositoryId) ||
                !reader.TryReadString(
                    AcceptedStateFormat.MaximumRepositoryNameBytes,
                    out var repositoryName) ||
                !reader.TryReadInt64(out var pullRequestNumber) ||
                !reader.TryReadString(64, out var scopeSha256) ||
                !reader.TryReadString(64, out var bodySha256) ||
                !reader.TryReadString(40, out var reviewedHeadSha) ||
                !reader.TryReadString(64, out var policyIdentitySha256) ||
                !reader.TryReadString(64, out var payloadSha256) ||
                !reader.TryReadString(256, out var buildDiscriminator) ||
                !reader.TryReadString(64, out var renderingVersion) ||
                !reader.IsComplete)
            {
                return false;
            }

            return ValidatedPublicationPayloadV1.TryRehydrate(
                commentBytes,
                repositoryId,
                repositoryName,
                pullRequestNumber,
                scopeSha256,
                bodySha256,
                reviewedHeadSha,
                policyIdentitySha256,
                payloadSha256,
                buildDiscriminator,
                renderingVersion,
                out value);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(commentBytes);
        }
    }
}

internal static class AcceptedStateGenerationRecordCodec
{
    internal static bool TryEncode(
        StateGenerationRecordV1? value,
        out byte[] bytes)
    {
        bytes = [];
        if (!AcceptedStateRecordValidation.IsValid(value))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(AcceptedStateFormat.GenerationMagic);
        writer.WriteUInt16(AcceptedStateFormat.Version);
        writer.WriteBytes(value!.EncryptedStateEnvelope.AsSpan());
        writer.WriteString(value.StateEnvelopeSha256);
        writer.WriteString(value.SessionSha256);
        writer.WriteString(value.ProducerBaseSha);
        writer.WriteString(value.ProducerHeadSha);
        writer.WriteInt64(value.Generation);
        writer.WriteOptionalString(value.PredecessorEnvelopeSha256);
        writer.WriteOptionalString(value.PreviousLogicalGenerationIdentity);
        writer.WriteInt64(value.PreparedAtUnixSeconds);
        writer.WriteInt64(value.PreparedExpiresAtUnixSeconds);
        writer.WriteBytes(value.PublicationPayloadBytes.AsSpan());
        writer.WriteString(value.PublicationPayloadSha256);
        writer.WriteString(value.PolicyIdentitySha256);
        writer.WriteString(value.ConfigSha256);
        writer.WriteString(value.InstructionsSha256);
        writer.WriteString(value.PayloadSha256);
        writer.WriteString(value.BuildDiscriminator);
        var candidate = writer.ToArray();
        if (candidate.Length >
            AcceptedStateFormat.MaximumGenerationPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }

        bytes = candidate;
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out StateGenerationRecordV1? value)
    {
        value = null;
        if (bytes.Length is < 1 or >
            AcceptedStateFormat.MaximumGenerationPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(
                magic,
                AcceptedStateFormat.GenerationMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != AcceptedStateFormat.Version ||
            !reader.TryReadBytes(
                AgentLimits.StateEnvelopeBytes,
                out var encryptedStateEnvelope))
        {
            return false;
        }

        byte[] publicationPayload = [];
        try
        {
            if (!reader.TryReadString(64, out var stateEnvelopeSha256) ||
                !reader.TryReadString(64, out var sessionSha256) ||
                !reader.TryReadString(64, out var producerBaseSha) ||
                !reader.TryReadString(64, out var producerHeadSha) ||
                !reader.TryReadInt64(out var generation) ||
                !reader.TryReadOptionalString(
                    64,
                    out var predecessorEnvelopeSha256) ||
                !reader.TryReadOptionalString(
                    64,
                    out var previousLogicalGenerationIdentity) ||
                !reader.TryReadInt64(out var preparedAtUnixSeconds) ||
                !reader.TryReadInt64(out var preparedExpiresAtUnixSeconds) ||
                !reader.TryReadBytes(
                    AcceptedStateFormat.MaximumPublicationPayloadBytes,
                    out publicationPayload) ||
                !reader.TryReadString(64, out var publicationPayloadSha256) ||
                !reader.TryReadString(64, out var policyIdentitySha256) ||
                !reader.TryReadString(64, out var configSha256) ||
                !reader.TryReadString(64, out var instructionsSha256) ||
                !reader.TryReadString(64, out var payloadSha256) ||
                !reader.TryReadString(256, out var buildDiscriminator) ||
                !reader.IsComplete)
            {
                return false;
            }

            var candidate = new StateGenerationRecordV1(
                ImmutableArray.CreateRange(encryptedStateEnvelope),
                stateEnvelopeSha256,
                sessionSha256,
                producerBaseSha,
                producerHeadSha,
                generation,
                predecessorEnvelopeSha256,
                previousLogicalGenerationIdentity,
                preparedAtUnixSeconds,
                preparedExpiresAtUnixSeconds,
                ImmutableArray.CreateRange(publicationPayload),
                publicationPayloadSha256,
                policyIdentitySha256,
                configSha256,
                instructionsSha256,
                payloadSha256,
                buildDiscriminator);
            if (!AcceptedStateRecordValidation.IsValid(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encryptedStateEnvelope);
            CryptographicOperations.ZeroMemory(publicationPayload);
        }
    }
}

internal static class AcceptedStateAcceptanceReceiptCodec
{
    internal static bool TryEncode(
        AcceptanceReceiptV1? value,
        out byte[] bytes)
    {
        bytes = [];
        if (!AcceptedStateRecordValidation.IsValid(value))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(AcceptedStateFormat.AcceptanceMagic);
        writer.WriteUInt16(AcceptedStateFormat.Version);
        writer.WriteString(value!.LogicalGenerationIdentity);
        writer.WriteString(value.OriginalCandidateObjectIdentity);
        writer.WriteOptionalString(value.PreviousLogicalGenerationIdentity);
        writer.WriteOptionalString(value.PreviousAcceptanceReceiptIdentity);
        writer.WriteString(value.ReviewedHeadSha);
        writer.WriteByte((byte)value.PublicationOperation);
        writer.WriteInt64(value.RepositoryId);
        writer.WriteInt64(value.PullRequestNumber);
        writer.WriteInt64(value.CommentId);
        writer.WriteString(value.CommentUrl);
        writer.WriteString(value.ScopeSha256);
        writer.WriteString(value.BodySha256);
        writer.WriteString(value.PublicationPayloadSha256);
        writer.WriteString(value.ProducingRunIdentity);
        writer.WriteInt64(value.ProducingRunAttempt);
        writer.WriteInt64(value.AcceptedAtUnixSeconds);
        writer.WriteInt64(value.LogicalExpiresAtUnixSeconds);
        var candidate = writer.ToArray();
        if (candidate.Length >
            AcceptedStateFormat.MaximumAcceptancePayloadBytes)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }

        bytes = candidate;
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out AcceptanceReceiptV1? value)
    {
        value = null;
        if (bytes.Length is < 1 or >
            AcceptedStateFormat.MaximumAcceptancePayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(
                magic,
                AcceptedStateFormat.AcceptanceMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != AcceptedStateFormat.Version ||
            !reader.TryReadString(64, out var logicalGenerationIdentity) ||
            !reader.TryReadString(
                64,
                out var originalCandidateObjectIdentity) ||
            !reader.TryReadOptionalString(
                64,
                out var previousLogicalGenerationIdentity) ||
            !reader.TryReadOptionalString(
                64,
                out var previousAcceptanceReceiptIdentity) ||
            !reader.TryReadString(64, out var reviewedHeadSha) ||
            !reader.TryReadByte(out var operationValue) ||
            !reader.TryReadInt64(out var repositoryId) ||
            !reader.TryReadInt64(out var pullRequestNumber) ||
            !reader.TryReadInt64(out var commentId) ||
            !reader.TryReadString(
                AcceptedStateFormat.MaximumCommentUrlBytes,
                out var commentUrl) ||
            !reader.TryReadString(64, out var scopeSha256) ||
            !reader.TryReadString(64, out var bodySha256) ||
            !reader.TryReadString(64, out var publicationPayloadSha256) ||
            !reader.TryReadString(
                LineageFormat.MaximumRunIdentityBytes,
                out var producingRunIdentity) ||
            !reader.TryReadInt64(out var producingRunAttempt) ||
            !reader.TryReadInt64(out var acceptedAtUnixSeconds) ||
            !reader.TryReadInt64(out var logicalExpiresAtUnixSeconds) ||
            !reader.IsComplete)
        {
            return false;
        }

        var candidate = new AcceptanceReceiptV1(
            logicalGenerationIdentity,
            originalCandidateObjectIdentity,
            previousLogicalGenerationIdentity,
            previousAcceptanceReceiptIdentity,
            reviewedHeadSha,
            (StickyPublicationOperation)operationValue,
            repositoryId,
            pullRequestNumber,
            commentId,
            commentUrl,
            scopeSha256,
            bodySha256,
            publicationPayloadSha256,
            producingRunIdentity,
            producingRunAttempt,
            acceptedAtUnixSeconds,
            logicalExpiresAtUnixSeconds);
        if (!AcceptedStateRecordValidation.IsValid(candidate))
        {
            return false;
        }

        value = candidate;
        return true;
    }
}

internal static class AcceptedStatePhysicalCopyCodec
{
    internal static bool TryEncode(
        AcceptedStatePhysicalCopyV1? value,
        out byte[] bytes)
    {
        bytes = [];
        if (!AcceptedStateRecordValidation.IsValid(value))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(AcceptedStateFormat.PhysicalCopyMagic);
        writer.WriteUInt16(AcceptedStateFormat.Version);
        writer.WriteBytes(value!.CanonicalGenerationBytes.AsSpan());
        writer.WriteString(value.LogicalGenerationIdentity);
        writer.WriteString(value.OriginalCandidateObjectIdentity);
        writer.WriteString(value.SourceArtifactId);
        writer.WriteString(value.SourceArchiveSha256);
        writer.WriteString(value.SourceEncryptedEnvelopeSha256);
        var candidate = writer.ToArray();
        if (candidate.Length >
            AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes)
        {
            CryptographicOperations.ZeroMemory(candidate);
            return false;
        }

        bytes = candidate;
        return true;
    }

    internal static bool TryDecode(
        ReadOnlySpan<byte> bytes,
        out AcceptedStatePhysicalCopyV1? value)
    {
        value = null;
        if (bytes.Length is < 1 or >
            AcceptedStateFormat.MaximumPhysicalCopyPayloadBytes)
        {
            return false;
        }

        var reader = new LineageBinaryReader(bytes);
        if (!reader.TryReadString(32, out var magic) ||
            !StringComparer.Ordinal.Equals(
                magic,
                AcceptedStateFormat.PhysicalCopyMagic) ||
            !reader.TryReadUInt16(out var version) ||
            version != AcceptedStateFormat.Version ||
            !reader.TryReadBytes(
                AcceptedStateFormat.MaximumGenerationPayloadBytes,
                out var generationBytes))
        {
            return false;
        }

        try
        {
            if (!reader.TryReadString(
                    64,
                    out var logicalGenerationIdentity) ||
                !reader.TryReadString(
                    64,
                    out var originalCandidateObjectIdentity) ||
                !reader.TryReadString(32, out var sourceArtifactId) ||
                !reader.TryReadString(64, out var sourceArchiveSha256) ||
                !reader.TryReadString(
                    64,
                    out var sourceEncryptedEnvelopeSha256) ||
                !reader.IsComplete)
            {
                return false;
            }

            var candidate = new AcceptedStatePhysicalCopyV1(
                ImmutableArray.CreateRange(generationBytes),
                logicalGenerationIdentity,
                originalCandidateObjectIdentity,
                sourceArtifactId,
                sourceArchiveSha256,
                sourceEncryptedEnvelopeSha256);
            if (!AcceptedStateRecordValidation.IsValid(candidate))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(generationBytes);
        }
    }
}
