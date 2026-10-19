using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal sealed record AcceptedStatePolicyBinding(
    string PolicyIdentitySha256,
    string ConfigSha256,
    string InstructionsSha256,
    string PayloadSha256,
    ActionHostPayloadContinuityMode PayloadContinuityMode,
    string BuildDiscriminator);

internal sealed record AcceptedStatePublicationBinding(
    R4PublicationScopeV1 Scope,
    string ScopeSha256,
    string RepositoryName,
    string PayloadSha256,
    string BuildDiscriminator);

internal static class AcceptedStateRestoreService
{
    private static bool MatchesPolicy(
        StateGenerationRecordV1 generation,
        AcceptedStatePolicyBinding expected) =>
        StringComparer.Ordinal.Equals(
            generation.PolicyIdentitySha256,
            expected.PolicyIdentitySha256) &&
        StringComparer.Ordinal.Equals(
            generation.ConfigSha256,
            expected.ConfigSha256) &&
        StringComparer.Ordinal.Equals(
            generation.InstructionsSha256,
            expected.InstructionsSha256) &&
        MatchesProducerPayload(generation.PayloadSha256, expected) &&
        StringComparer.Ordinal.Equals(
            generation.BuildDiscriminator,
            expected.BuildDiscriminator);

    internal static bool MatchesPublication(
        SelectedAcceptedGeneration selected,
        AcceptedStatePolicyBinding policy,
        AcceptedStatePublicationBinding expected)
    {
        var generation = selected.Generation;
        var receipt = selected.Receipt;
        if (!MatchesPolicy(generation, policy) ||
            !R4PublicationIdentityV1.IsValidScope(expected.Scope) ||
            !AcceptedStateRecordValidation.FixedDigest(
                expected.Scope.PolicyIdentitySha256,
                policy.PolicyIdentitySha256) ||
            !AcceptedStateRecordValidation.FixedDigest(
                expected.PayloadSha256,
                policy.PayloadSha256) ||
            !StringComparer.Ordinal.Equals(
                expected.BuildDiscriminator,
                policy.BuildDiscriminator) ||
            !StringComparer.Ordinal.Equals(
                R4PublicationIdentityV1.ComputeScopeSha256(expected.Scope),
                expected.ScopeSha256) ||
            expected.Scope.RepositoryId > long.MaxValue ||
            expected.Scope.PullRequestNumber > long.MaxValue ||
            !AcceptedStatePublicationPayloadCodec.TryDecode(
                generation.PublicationPayloadBytes.AsSpan(),
                out var publication) ||
            publication is null)
        {
            return false;
        }

        var repositoryId = (long)expected.Scope.RepositoryId;
        var pullRequestNumber = (long)expected.Scope.PullRequestNumber;
        var canonicalCommentUrl =
            $"https://github.com/{expected.RepositoryName}/pull/" +
            $"{pullRequestNumber}#issuecomment-{receipt.CommentId}";
        return publication.RepositoryId == repositoryId &&
            receipt.RepositoryId == repositoryId &&
            publication.PullRequestNumber == pullRequestNumber &&
            receipt.PullRequestNumber == pullRequestNumber &&
            StringComparer.Ordinal.Equals(
                publication.RepositoryName,
                expected.RepositoryName) &&
            StringComparer.Ordinal.Equals(
                receipt.CommentUrl,
                canonicalCommentUrl) &&
            StringComparer.Ordinal.Equals(
                publication.ScopeSha256,
                expected.ScopeSha256) &&
            StringComparer.Ordinal.Equals(
                receipt.ScopeSha256,
                expected.ScopeSha256) &&
            StringComparer.Ordinal.Equals(
                publication.BodySha256,
                receipt.BodySha256) &&
            StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                generation.ProducerHeadSha) &&
            StringComparer.Ordinal.Equals(
                publication.ReviewedHeadSha,
                receipt.ReviewedHeadSha) &&
            StringComparer.Ordinal.Equals(
                publication.PolicyIdentitySha256,
                expected.Scope.PolicyIdentitySha256) &&
            StringComparer.Ordinal.Equals(
                publication.PayloadSha256,
                generation.PayloadSha256) &&
            StringComparer.Ordinal.Equals(
                publication.BuildDiscriminator,
                expected.BuildDiscriminator) &&
            AcceptedStateRecordValidation.FixedDigest(
                generation.PublicationPayloadSha256,
                receipt.PublicationPayloadSha256);
    }

    private static bool MatchesProducerPayload(
        string producerPayloadSha256,
        AcceptedStatePolicyBinding expected) =>
        expected.PayloadContinuityMode switch
        {
            ActionHostPayloadContinuityMode.ExactExecutable =>
                StringComparer.Ordinal.Equals(
                    producerPayloadSha256,
                    expected.PayloadSha256),
            ActionHostPayloadContinuityMode.ExactSource =>
                Lineage.LineageValidation.IsSha256(
                    producerPayloadSha256),
            _ => false,
        };

}
