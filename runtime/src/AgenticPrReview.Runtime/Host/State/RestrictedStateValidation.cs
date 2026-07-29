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

    internal static bool IsValidScope(RestrictedStateScope scope) =>
        scope is not null &&
        IsUtf8(scope.RepositoryId, 1, 128) &&
        IsUtf8(scope.WorkflowIdentity, 1, 256) &&
        scope.ReviewTarget is >= 1 and <= RestrictedStateFormat.MaximumReviewTarget &&
        scope.SessionId is not null &&
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
        candidate.Envelope.Length <= AgentLimits.StateEnvelopeBytes &&
        StringComparer.Ordinal.Equals(
            candidate.EnvelopeSha256,
            RestrictedStateEnvelope.EnvelopeSha256(candidate.Envelope)) &&
        StringComparer.Ordinal.Equals(
            candidate.ObjectIdentity,
            RestrictedStateEnvelope.ObjectIdentity(
                candidate.Binding,
                candidate.SessionSha256,
                candidate.EnvelopeSha256));

    internal static bool IsValidReceipt(PreparedStateReceipt receipt) =>
        receipt is not null &&
        receipt.Generation is >= 0 and <= RestrictedStateFormat.MaximumGeneration &&
        IsLowerHex(receipt.SessionSha256, 64) &&
        IsLowerHex(receipt.EnvelopeSha256, 64) &&
        IsLowerHex(receipt.ObjectIdentity, 64);

    internal static bool IsValidKeyId(string keyId) =>
        keyId is not null &&
        keyId.Length is >= 1 and <= 64 &&
        keyId.All(character => character <= 0x7f);

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
                metadataBytes +
                RestrictedStateSnapshotCodec.CandidateMetadataBytes(
                    candidate));
            lastGeneration = candidate.Binding.Generation;
            lastEnvelopeSha = candidate.EnvelopeSha256;
        }

        long totalBytes = checked(envelopeBytes + metadataBytes);
        if (snapshot.Staging is not null)
        {
            totalBytes = checked(
                totalBytes +
                snapshot.Staging.Envelope.Length +
                RestrictedStateSnapshotCodec.CandidateMetadataBytes(
                    snapshot.Staging));
            metadataBytes = checked(
                metadataBytes +
                RestrictedStateSnapshotCodec.CandidateMetadataBytes(
                    snapshot.Staging));
        }

        if (snapshot.Accepted.Length == 2)
        {
            var current = snapshot.Accepted[0];
            var predecessor = snapshot.Accepted[1];
            if (current.Binding.Generation !=
                    predecessor.Binding.Generation + 1 ||
                !StringComparer.Ordinal.Equals(
                    current.Binding.PredecessorEnvelopeSha256,
                    predecessor.EnvelopeSha256))
            {
                return false;
            }
        }

        var sessions = snapshot.Accepted
            .Select(candidate => candidate.SessionSha256)
            .ToHashSet(StringComparer.Ordinal);
        var envelopes = snapshot.Accepted
            .Select(candidate => candidate.EnvelopeSha256)
            .ToHashSet(StringComparer.Ordinal);
        var objects = snapshot.Accepted
            .Select(candidate => candidate.ObjectIdentity)
            .ToHashSet(StringComparer.Ordinal);
        if (sessions.Count != snapshot.Accepted.Length ||
            envelopes.Count != snapshot.Accepted.Length ||
            objects.Count != snapshot.Accepted.Length)
        {
            return false;
        }

        if (snapshot.Staging is not null)
        {
            var staging = snapshot.Staging;
            if (sessions.Contains(staging.SessionSha256) ||
                envelopes.Contains(staging.EnvelopeSha256) ||
                objects.Contains(staging.ObjectIdentity) ||
                (snapshot.Accepted.IsEmpty
                    ? staging.Binding.Generation != 0 ||
                        staging.Binding.PredecessorEnvelopeSha256 is not null
                    : snapshot.Accepted[0].Binding.Generation == long.MaxValue ||
                        staging.Binding.Generation !=
                            snapshot.Accepted[0].Binding.Generation + 1 ||
                        !StringComparer.Ordinal.Equals(
                            staging.Binding.PredecessorEnvelopeSha256,
                            snapshot.Accepted[0].EnvelopeSha256)))
            {
                return false;
            }
        }

        return envelopeBytes <= AgentLimits.CandidateEnvelopeTotalBytes &&
            metadataBytes <= AgentLimits.CandidateMetadataBytes &&
            totalBytes <= AgentLimits.StateScopeTotalBytes;
    }

    internal static bool IsLowerHex(string? value, int length)
    {
        if (value is null || value.Length != length)
        {
            return false;
        }

        return value.All(character =>
            character is (>= '0' and <= '9') or
                (>= 'a' and <= 'f'));
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
