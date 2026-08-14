using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal static class PublicationReceiptMatcher
{
    internal static bool AreDurablyEqual(
        StickyCommentPublisher.StickyPublicationReceipt left,
        StickyCommentPublisher.StickyPublicationReceipt right) =>
        left.Operation == right.Operation &&
        HaveSamePublicationIdentity(left, right);

    internal static bool IsFreshObservationOf(
        StickyCommentPublisher.StickyPublicationReceipt durable,
        StickyCommentPublisher.StickyPublicationReceipt fresh) =>
        fresh.Operation == StickyPublicationOperation.Observed &&
        HaveSamePublicationIdentity(durable, fresh);

    private static bool HaveSamePublicationIdentity(
        StickyCommentPublisher.StickyPublicationReceipt left,
        StickyCommentPublisher.StickyPublicationReceipt right) =>
        left.RepositoryId == right.RepositoryId &&
        left.PullRequestNumber == right.PullRequestNumber &&
        left.CommentId == right.CommentId &&
        StringComparer.Ordinal.Equals(left.CommentUrl, right.CommentUrl) &&
        StringComparer.Ordinal.Equals(
            left.ScopeSha256,
            right.ScopeSha256) &&
        StringComparer.Ordinal.Equals(left.BodySha256, right.BodySha256) &&
        StringComparer.Ordinal.Equals(left.HeadSha, right.HeadSha);
}
