using System.Text;
using System.Text.RegularExpressions;
using AgenticPrReview.Runtime.Agent;

namespace AgenticPrReview.Runtime.Host.State;

internal static partial class RestrictedStateValidation
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(false, true);

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SessionIdPattern();

    [GeneratedRegex("^[\\x21-\\x7e]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyIdPattern();

    internal static bool IsValidScope(RestrictedStateScope scope) =>
        scope is not null &&
        IsUtf8(scope.RepositoryId, 1, 128) &&
        IsUtf8(scope.WorkflowIdentity, 1, 256) &&
        scope.ReviewTarget is >= 1 and <= RestrictedStateFormat.MaximumReviewTarget &&
        SessionIdPattern().IsMatch(scope.SessionId) &&
        IsUtf8(scope.ProviderId, 1, 128) &&
        IsUtf8(scope.ModelId, 1, 128) &&
        IsUtf8(scope.AdapterId, 1, 128) &&
        IsLowerHex(scope.PolicySha256, 64) &&
        IsLowerHex(scope.LimitsSha256, 64) &&
        IsLowerHex(scope.ToolsetSha256, 64) &&
        IsUtf8(scope.BuildId, 1, 256);

    internal static bool IsValidBinding(RestrictedStateBinding binding) =>
        binding is not null &&
        IsValidScope(binding.Scope) &&
        IsLowerHex(binding.ProducerBaseSha, 40) &&
        IsLowerHex(binding.ProducerHeadSha, 40) &&
        binding.Generation is >= 0 and <= RestrictedStateFormat.MaximumGeneration &&
        ((binding.Generation == 0 &&
                binding.PredecessorEnvelopeSha256 is null) ||
            (binding.Generation > 0 &&
                IsLowerHex(binding.PredecessorEnvelopeSha256, 64))) &&
        IsValidLifetime(
            binding.AcceptedAtUnixSeconds,
            binding.ExpiresAtUnixSeconds);

    internal static bool IsValidLineage(AcceptedLineage lineage) =>
        lineage is not null &&
        IsValidScope(lineage.Scope) &&
        lineage.Generation is >= 0 and <= RestrictedStateFormat.MaximumGeneration &&
        IsLowerHex(lineage.SessionSha256, 64) &&
        IsLowerHex(lineage.EnvelopeSha256, 64) &&
        ((lineage.Generation == 0 &&
                lineage.ExpectedPredecessorEnvelopeSha256 is null) ||
            (lineage.Generation > 0 &&
                IsLowerHex(lineage.ExpectedPredecessorEnvelopeSha256, 64))) &&
        IsValidLifetime(
            lineage.AcceptedAtUnixSeconds,
            lineage.ExpiresAtUnixSeconds);

    internal static bool IsValidCandidate(RestrictedStateCandidate candidate) =>
        candidate is not null &&
        IsValidBinding(candidate.Binding) &&
        IsLowerHex(candidate.SessionSha256, 64) &&
        IsLowerHex(candidate.EnvelopeSha256, 64) &&
        IsLowerHex(candidate.ObjectIdentity, 64) &&
        candidate.Envelope is not null &&
        candidate.Envelope.Length > 0 &&
        candidate.Envelope.Length <= AgentLimits.StateEnvelopeBytes;

    internal static bool IsValidReceipt(PreparedStateReceipt receipt) =>
        receipt is not null &&
        receipt.Generation is >= 0 and <= RestrictedStateFormat.MaximumGeneration &&
        IsLowerHex(receipt.SessionSha256, 64) &&
        IsLowerHex(receipt.EnvelopeSha256, 64) &&
        IsLowerHex(receipt.ObjectIdentity, 64);

    internal static bool IsValidKeyId(string keyId) =>
        keyId is not null &&
        KeyIdPattern().IsMatch(keyId);

    internal static bool IsValidLifetime(
        long acceptedAtUnixSeconds,
        long expiresAtUnixSeconds) =>
        acceptedAtUnixSeconds is >= 0 and <= RestrictedStateFormat.MaximumUnixSeconds &&
        expiresAtUnixSeconds > acceptedAtUnixSeconds &&
        expiresAtUnixSeconds <= RestrictedStateFormat.MaximumUnixSeconds &&
        expiresAtUnixSeconds - acceptedAtUnixSeconds <=
            RestrictedStateFormat.MaximumRetentionSeconds;

    internal static bool IsValidSnapshot(RestrictedStateSnapshot snapshot)
    {
        if (snapshot is null ||
            snapshot.Accepted.IsDefault ||
            snapshot.Accepted.Length > AgentLimits.AcceptedCandidates ||
            (snapshot.Staging is not null &&
                !IsValidCandidate(snapshot.Staging)))
        {
            return false;
        }

        long envelopeBytes = 0;
        var metadataBytes = 0;
        long lastGeneration = long.MaxValue;
        string? lastEnvelopeSha = null;
        foreach (var candidate in snapshot.Accepted)
        {
            if (!IsValidCandidate(candidate) ||
                candidate.Binding.Generation > lastGeneration ||
                (candidate.Binding.Generation == lastGeneration &&
                    lastEnvelopeSha is not null &&
                    StringComparer.Ordinal.Compare(
                        candidate.EnvelopeSha256,
                        lastEnvelopeSha) < 0))
            {
                return false;
            }

            envelopeBytes = checked(envelopeBytes + candidate.Envelope.Length);
            metadataBytes = checked(
                metadataBytes + EstimateMetadataBytes(candidate));
            lastGeneration = candidate.Binding.Generation;
            lastEnvelopeSha = candidate.EnvelopeSha256;
        }

        var totalBytes = envelopeBytes;
        if (snapshot.Staging is not null)
        {
            totalBytes = checked(
                totalBytes + snapshot.Staging.Envelope.Length);
            metadataBytes = checked(
                metadataBytes + EstimateMetadataBytes(snapshot.Staging));
        }

        return envelopeBytes <= AgentLimits.CandidateEnvelopeTotalBytes &&
            metadataBytes <= AgentLimits.CandidateMetadataBytes &&
            totalBytes <= AgentLimits.StateScopeTotalBytes;
    }

    internal static int EstimateMetadataBytes(
        RestrictedStateCandidate candidate)
    {
        var scope = candidate.Binding.Scope;
        return checked(
            192 +
            Utf8Length(scope.RepositoryId) +
            Utf8Length(scope.WorkflowIdentity) +
            Encoding.ASCII.GetByteCount(scope.SessionId) +
            Utf8Length(scope.ProviderId) +
            Utf8Length(scope.ModelId) +
            Utf8Length(scope.AdapterId) +
            Utf8Length(scope.BuildId));
    }

    internal static bool IsLowerHex(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        foreach (var character in value)
        {
            if (character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }

    internal static bool IsUtf8(
        string? value,
        int minimumBytes,
        int maximumBytes)
    {
        if (value is null)
        {
            return false;
        }

        try
        {
            var bytes = StrictUtf8.GetByteCount(value);
            return bytes >= minimumBytes && bytes <= maximumBytes;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    internal static int Utf8Length(string value) =>
        StrictUtf8.GetByteCount(value);
}
