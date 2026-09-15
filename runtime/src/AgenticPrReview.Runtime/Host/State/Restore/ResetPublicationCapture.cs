using AgenticPrReview.Runtime.Host.Publishing.Recovery;
using AgenticPrReview.Runtime.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Host.State.Restore;

internal static class ResetPublicationCapture
{
    internal static bool TryCapture(
        LineageReadOnlyObservationContext observation,
        LineageResolveRequest request,
        AcceptedStatePolicyBinding policy,
        AcceptedStatePublicationBinding publication,
        TimeProvider time,
        out ResetPublicationTargetV1? target)
    {
        target = null;
        var head = observation.Selection.Selection?.Head;
        var snapshot = observation.Snapshot;
        if (head is null || snapshot is null || !snapshot.Unknown.IsEmpty) return false;
        var selected = new AcceptedStateSelector(time).Select(observation, request);
        if (!selected.Succeeded && !selected.IsBootstrap) return false;
        var active = snapshot.Authenticated.Concat(snapshot.UnderRetained)
            .Where(item => item.Header.Epoch == head.Header.Epoch && item.Header.SessionId == head.Header.SessionId);
        if (!PublicationRecoveryInventoryFactory.ResetSourceRecordsAreAccepted(active, selected.Selection)) return false;
        if (selected.Selection?.Current is { } current)
        {
            if (!AcceptedStateRestoreService.MatchesPublication(current, policy, publication)) return false;
            var receipt = current.Receipt;
            target = new((byte)receipt.PublicationOperation, receipt.RepositoryId,
                receipt.PullRequestNumber, receipt.CommentId, receipt.CommentUrl,
                receipt.ScopeSha256, receipt.BodySha256, receipt.ReviewedHeadSha,
                current.ReceiptPhysical.Header.ObjectIdentity, head.Header.Epoch,
                receipt.LogicalExpiresAtUnixSeconds);
        }
        else target = head.Head.ResetPublicationTarget;
        return target is null || target.Matches(publication) &&
            target.ExpiresAtUnixSeconds > time.GetUtcNow().ToUnixTimeSeconds();
    }
}
