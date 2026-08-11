using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal static class AcceptedStateFormat
{
    internal const string PublicationMagic = "APRVPP01";
    internal const string GenerationMagic = "APRSGR01";
    internal const string AcceptanceMagic = "APRACR01";
    internal const string PhysicalCopyMagic = "APRACP01";
    internal const string LogicalGenerationDomain =
        "apr.logical-generation.s5";
    internal const ushort Version = 1;
    internal const string RenderingVersion = "r4-sticky-v1";
    internal const long LogicalWindowSeconds = 604_800;
    internal const int MaximumPublicationPayloadBytes = 256 * 1024;
    internal const int MaximumGenerationPayloadBytes = 1_400_000;
    internal const int MaximumAcceptancePayloadBytes = 64 * 1024;
    internal const int MaximumPhysicalCopyPayloadBytes = 1_500_000;
    internal const int MaximumReaderPayloadBytes =
        MaximumPhysicalCopyPayloadBytes;
    internal const int MaximumRepositoryNameBytes = 256;
    internal const int MaximumCommentUrlBytes = 2_048;
}

internal static class AcceptedStateCodes
{
    internal const string Ready = "accepted_state_ready";
    internal const string Bootstrap = "accepted_state_bootstrap";
    internal const string AccessDenied = "accepted_state_access_denied";
    internal const string Absent = "accepted_state_absent";
    internal const string Expired = "accepted_state_expired";
    internal const string NonCurrent = "accepted_state_non_current";
    internal const string IncompatibleCurrent =
        "accepted_state_incompatible_current";
    internal const string AuthenticationFailed =
        "accepted_state_authentication_failed";
    internal const string ScopeMismatch = "accepted_state_scope_mismatch";
    internal const string AncestryFailed = "accepted_state_ancestry_failed";
    internal const string Overflow = "accepted_state_overflow";
    internal const string Conflict = "accepted_state_conflict";
    internal const string OutcomeUnknown = "accepted_state_outcome_unknown";
    internal const string KeyUnavailable = "accepted_state_key_unavailable";
}

internal sealed class ValidatedPublicationPayloadV1
{
    private ValidatedPublicationPayloadV1(
        ImmutableArray<byte> finalizedCommentUtf8,
        long repositoryId,
        string repositoryName,
        long pullRequestNumber,
        string scopeSha256,
        string bodySha256,
        string reviewedHeadSha,
        string policyIdentitySha256,
        string payloadSha256,
        string buildDiscriminator,
        string renderingVersion)
    {
        FinalizedCommentUtf8 = finalizedCommentUtf8;
        RepositoryId = repositoryId;
        RepositoryName = repositoryName;
        PullRequestNumber = pullRequestNumber;
        ScopeSha256 = scopeSha256;
        BodySha256 = bodySha256;
        ReviewedHeadSha = reviewedHeadSha;
        PolicyIdentitySha256 = policyIdentitySha256;
        PayloadSha256 = payloadSha256;
        BuildDiscriminator = buildDiscriminator;
        RenderingVersion = renderingVersion;
    }

    internal ImmutableArray<byte> FinalizedCommentUtf8 { get; }
    internal long RepositoryId { get; }
    internal string RepositoryName { get; }
    internal long PullRequestNumber { get; }
    internal string ScopeSha256 { get; }
    internal string BodySha256 { get; }
    internal string ReviewedHeadSha { get; }
    internal string PolicyIdentitySha256 { get; }
    internal string PayloadSha256 { get; }
    internal string BuildDiscriminator { get; }
    internal string RenderingVersion { get; }

    internal static bool TryCreate(
        string? finalizedComment,
        long repositoryId,
        string? repositoryName,
        long pullRequestNumber,
        string? policyIdentitySha256,
        string? payloadSha256,
        string? buildDiscriminator,
        string? renderingVersion,
        out ValidatedPublicationPayloadV1? value)
    {
        value = null;
        if (finalizedComment is null ||
            repositoryId <= 0 ||
            pullRequestNumber <= 0 ||
            !ActionHostContractValidation.IsRepositoryName(repositoryName) ||
            !LineageValidation.IsSha256(policyIdentitySha256) ||
            !LineageValidation.IsSha256(payloadSha256) ||
            !ActionHostContractValidation.IsBuildDiscriminator(
                buildDiscriminator) ||
            !StringComparer.Ordinal.Equals(
                renderingVersion,
                AcceptedStateFormat.RenderingVersion) ||
            !R4PublicationBudget.Fits(
                finalizedComment,
                R4PublicationBudget.MaximumScalars,
                R4PublicationBudget.MaximumUtf8Bytes))
        {
            return false;
        }

        var inspection = R4StickyMarker.Inspect(finalizedComment);
        if (inspection.Kind != R4StickyInspectionKind.ValidR4 ||
            inspection.Identity is null)
        {
            return false;
        }

        byte[]? serialized = null;
        try
        {
            if (!StickyCommentSerializer.TrySerialize(
                    finalizedComment,
                    out serialized) ||
                serialized is null)
            {
                return false;
            }

            var commentBytes = new UTF8Encoding(false, true)
                .GetBytes(finalizedComment);
            value = new ValidatedPublicationPayloadV1(
                ImmutableArray.CreateRange(commentBytes),
                repositoryId,
                repositoryName!,
                pullRequestNumber,
                inspection.Identity.ScopeSha256,
                inspection.Identity.BodySha256,
                inspection.Identity.HeadSha,
                policyIdentitySha256!,
                payloadSha256!,
                buildDiscriminator!,
                renderingVersion!);
            CryptographicOperations.ZeroMemory(commentBytes);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
        finally
        {
            if (serialized is not null)
            {
                CryptographicOperations.ZeroMemory(serialized);
            }
        }
    }

    internal static bool TryRehydrate(
        ReadOnlySpan<byte> finalizedCommentUtf8,
        long repositoryId,
        string? repositoryName,
        long pullRequestNumber,
        string? scopeSha256,
        string? bodySha256,
        string? reviewedHeadSha,
        string? policyIdentitySha256,
        string? payloadSha256,
        string? buildDiscriminator,
        string? renderingVersion,
        out ValidatedPublicationPayloadV1? value)
    {
        value = null;
        try
        {
            var comment = new UTF8Encoding(false, true)
                .GetString(finalizedCommentUtf8);
            if (!TryCreate(
                    comment,
                    repositoryId,
                    repositoryName,
                    pullRequestNumber,
                    policyIdentitySha256,
                    payloadSha256,
                    buildDiscriminator,
                    renderingVersion,
                    out var candidate) ||
                candidate is null ||
                !StringComparer.Ordinal.Equals(
                    candidate.ScopeSha256,
                    scopeSha256) ||
                !StringComparer.Ordinal.Equals(
                    candidate.BodySha256,
                    bodySha256) ||
                !StringComparer.Ordinal.Equals(
                    candidate.ReviewedHeadSha,
                    reviewedHeadSha))
            {
                return false;
            }

            value = candidate;
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public override string ToString() => "[PRIVATE]";
}

internal sealed record StateGenerationRecordV1(
    ImmutableArray<byte> EncryptedStateEnvelope,
    string StateEnvelopeSha256,
    string SessionSha256,
    string ProducerBaseSha,
    string ProducerHeadSha,
    long Generation,
    string? PredecessorEnvelopeSha256,
    string? PreviousLogicalGenerationIdentity,
    long PreparedAtUnixSeconds,
    long PreparedExpiresAtUnixSeconds,
    ImmutableArray<byte> PublicationPayloadBytes,
    string PublicationPayloadSha256,
    string PolicyIdentitySha256,
    string ConfigSha256,
    string InstructionsSha256,
    string PayloadSha256,
    string BuildDiscriminator)
{
    public override string ToString() => "[PRIVATE]";
}

internal sealed record AcceptanceReceiptV1(
    string LogicalGenerationIdentity,
    string OriginalCandidateObjectIdentity,
    string? PreviousLogicalGenerationIdentity,
    string? PreviousAcceptanceReceiptIdentity,
    string ReviewedHeadSha,
    StickyPublicationOperation PublicationOperation,
    long RepositoryId,
    long PullRequestNumber,
    long CommentId,
    string CommentUrl,
    string ScopeSha256,
    string BodySha256,
    string PublicationPayloadSha256,
    string ProducingRunIdentity,
    long ProducingRunAttempt,
    long AcceptedAtUnixSeconds,
    long LogicalExpiresAtUnixSeconds)
{
    public override string ToString() => "[PRIVATE]";
}

internal sealed record AcceptedStatePhysicalCopyV1(
    ImmutableArray<byte> CanonicalGenerationBytes,
    string LogicalGenerationIdentity,
    string OriginalCandidateObjectIdentity,
    string SourceArtifactId,
    string SourceArchiveSha256,
    string SourceEncryptedEnvelopeSha256)
{
    public override string ToString() => "[PRIVATE]";
}

internal static class AcceptedStateRecordValidation
{
    internal static bool IsValid(StateGenerationRecordV1? value) =>
        value is not null &&
        !value.EncryptedStateEnvelope.IsDefaultOrEmpty &&
        value.EncryptedStateEnvelope.Length <= AgentLimits.StateEnvelopeBytes &&
        RestrictedStateEnvelope.TryParse(
            value.EncryptedStateEnvelope.AsSpan(),
            out _) &&
        FixedDigest(
            RestrictedStateEnvelope.EnvelopeSha256(
                value.EncryptedStateEnvelope.AsSpan()),
            value.StateEnvelopeSha256) &&
        LineageValidation.IsSha256(value.SessionSha256) &&
        LineageValidation.IsGitSha(value.ProducerBaseSha) &&
        LineageValidation.IsGitSha(value.ProducerHeadSha) &&
        value.Generation is >= 0 and <= RestrictedStateFormat.MaximumGeneration &&
        ((value.Generation == 0 &&
                value.PredecessorEnvelopeSha256 is null &&
                value.PreviousLogicalGenerationIdentity is null) ||
            (value.Generation > 0 &&
                LineageValidation.IsSha256(
                    value.PredecessorEnvelopeSha256) &&
                LineageValidation.IsSha256(
                    value.PreviousLogicalGenerationIdentity))) &&
        LineageValidation.IsTime(value.PreparedAtUnixSeconds) &&
        LineageValidation.IsTime(value.PreparedExpiresAtUnixSeconds) &&
        value.PreparedAtUnixSeconds <= value.PreparedExpiresAtUnixSeconds &&
        value.PreparedExpiresAtUnixSeconds - value.PreparedAtUnixSeconds <=
            AcceptedStateFormat.LogicalWindowSeconds &&
        !value.PublicationPayloadBytes.IsDefaultOrEmpty &&
        value.PublicationPayloadBytes.Length <=
            AcceptedStateFormat.MaximumPublicationPayloadBytes &&
        AcceptedStatePublicationPayloadCodec.TryDecode(
            value.PublicationPayloadBytes.AsSpan(),
            out _) &&
        FixedDigest(
            Sha256(value.PublicationPayloadBytes.AsSpan()),
            value.PublicationPayloadSha256) &&
        LineageValidation.IsSha256(value.PolicyIdentitySha256) &&
        LineageValidation.IsSha256(value.ConfigSha256) &&
        LineageValidation.IsSha256(value.InstructionsSha256) &&
        LineageValidation.IsSha256(value.PayloadSha256) &&
        ActionHostContractValidation.IsBuildDiscriminator(
            value.BuildDiscriminator);

    internal static bool IsValid(AcceptanceReceiptV1? value)
    {
        if (value is null ||
            !LineageValidation.IsSha256(value.LogicalGenerationIdentity) ||
            !LineageValidation.IsSha256(
                value.OriginalCandidateObjectIdentity) ||
            !LineageValidation.IsOptionalSha256(
                value.PreviousLogicalGenerationIdentity) ||
            !LineageValidation.IsOptionalSha256(
                value.PreviousAcceptanceReceiptIdentity) ||
            (value.PreviousLogicalGenerationIdentity is null) !=
                (value.PreviousAcceptanceReceiptIdentity is null) ||
            !LineageValidation.IsGitSha(value.ReviewedHeadSha) ||
            !LineageValidation.IsSha256(value.PublicationPayloadSha256) ||
            !LineageValidation.IsText(
                value.ProducingRunIdentity,
                LineageFormat.MaximumRunIdentityBytes) ||
            value.ProducingRunAttempt < 0 ||
            !LineageValidation.IsTime(value.AcceptedAtUnixSeconds) ||
            value.AcceptedAtUnixSeconds >
                RestrictedStateFormat.MaximumUnixSeconds -
                    AcceptedStateFormat.LogicalWindowSeconds ||
            value.LogicalExpiresAtUnixSeconds !=
                value.AcceptedAtUnixSeconds +
                    AcceptedStateFormat.LogicalWindowSeconds)
        {
            return false;
        }

        return StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
            value.PublicationOperation,
            value.RepositoryId,
            value.PullRequestNumber,
            value.CommentId,
            value.CommentUrl,
            value.ScopeSha256,
            value.BodySha256,
            value.ReviewedHeadSha,
            out _);
    }

    internal static bool IsValid(AcceptedStatePhysicalCopyV1? value) =>
        value is not null &&
        !value.CanonicalGenerationBytes.IsDefaultOrEmpty &&
        value.CanonicalGenerationBytes.Length <=
            AcceptedStateFormat.MaximumGenerationPayloadBytes &&
        AcceptedStateGenerationRecordCodec.TryDecode(
            value.CanonicalGenerationBytes.AsSpan(),
            out _) &&
        LineageValidation.IsSha256(value.LogicalGenerationIdentity) &&
        LineageValidation.IsSha256(value.OriginalCandidateObjectIdentity) &&
        IsCanonicalPositiveDecimal(value.SourceArtifactId) &&
        LineageValidation.IsSha256(value.SourceArchiveSha256) &&
        LineageValidation.IsSha256(value.SourceEncryptedEnvelopeSha256);

    internal static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool FixedDigest(string left, string? right)
    {
        if (!LineageValidation.IsSha256(left) ||
            !LineageValidation.IsSha256(right))
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Convert.FromHexString(left),
            Convert.FromHexString(right!));
    }

    internal static bool IsCanonicalPositiveDecimal(string? value) =>
        ulong.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed > 0 &&
        StringComparer.Ordinal.Equals(
            value,
            parsed.ToString(CultureInfo.InvariantCulture));
}

internal static class AcceptedStateIdentity
{
    internal static bool TryComputeLogicalGeneration(
        ReadOnlySpan<byte> canonicalGeneration,
        string? baseScopeDigest,
        string? epoch,
        string? sessionId,
        string? previousAcceptanceReceiptIdentity,
        out string identity)
    {
        identity = string.Empty;
        if (canonicalGeneration is [] ||
            canonicalGeneration.Length >
                AcceptedStateFormat.MaximumGenerationPayloadBytes ||
            !LineageValidation.IsSha256(baseScopeDigest) ||
            !LineageValidation.IsSha256(epoch) ||
            !LineageValidation.IsSha256(sessionId) ||
            !LineageValidation.IsOptionalSha256(
                previousAcceptanceReceiptIdentity) ||
            !AcceptedStateGenerationRecordCodec.TryDecode(
                canonicalGeneration,
                out _))
        {
            return false;
        }

        var writer = new LineageBinaryWriter();
        writer.WriteString(AcceptedStateFormat.LogicalGenerationDomain);
        writer.WriteBytes(canonicalGeneration);
        writer.WriteString(baseScopeDigest!);
        writer.WriteString(epoch!);
        writer.WriteString(sessionId!);
        writer.WriteOptionalString(previousAcceptanceReceiptIdentity);
        var framed = writer.ToArray();
        try
        {
            identity = AcceptedStateRecordValidation.Sha256(framed);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(framed);
        }
    }
}
