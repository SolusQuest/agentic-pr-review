using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.State.Restore;

// The state/publication boundary owns receipt semantics; lineage only carries bounded scalars.
internal static class ResetPublicationTargetBinding
{
    internal static bool TryReceipt(this ResetPublicationTargetV1 target,
        out StickyCommentPublisher.StickyPublicationReceipt? receipt) =>
        StickyCommentPublisher.StickyPublicationReceipt.TryRehydrate(
            (StickyPublicationOperation)target.Operation, target.RepositoryId, target.PullRequestNumber,
            target.CommentId, target.CommentUrl, target.ScopeSha256, target.BodySha256, target.HeadSha, out receipt);

    internal static bool Matches(this ResetPublicationTargetV1 target, AcceptedStatePublicationBinding binding) =>
        ResetPublicationTargetV1.IsValid(target) && target.TryReceipt(out _) &&
        target.RepositoryId == (long)binding.Scope.RepositoryId &&
        target.PullRequestNumber == (long)binding.Scope.PullRequestNumber &&
        StringComparer.Ordinal.Equals(target.ScopeSha256, binding.ScopeSha256) &&
        StringComparer.Ordinal.Equals(target.CommentUrl,
            $"https://github.com/{binding.RepositoryName}/pull/{target.PullRequestNumber}#issuecomment-{target.CommentId}");
}
