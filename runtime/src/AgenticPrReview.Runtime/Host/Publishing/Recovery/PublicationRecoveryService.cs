using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.Host.Publishing.GitHub.Sticky;
using AgenticPrReview.Runtime.Host.Publishing.Rendering;
using AgenticPrReview.Runtime.Host.State.Restore;

namespace AgenticPrReview.Runtime.Host.Publishing.Recovery;

internal sealed class PublicationRecoveryService
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly StickyCommentPublisher publisher;

    internal PublicationRecoveryService(StickyCommentPublisher publisher) =>
        this.publisher = publisher ??
            throw new ArgumentNullException(nameof(publisher));

    internal async Task<PublicationRecoveryEvaluation> ClassifyBeforeProviderAsync(
        ActionHostGitHubToken token,
        ActionHostAuthorizer.AuthorizedInvocation authorization,
        R4PublicationScopeV1 scope,
        ValidatedPublicationPayloadV1? storedPublication,
        PublicationRecoveryInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(authorization);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(inventory);

        if (inventory.CandidateCount == 0)
        {
            return Evaluation(
                PublicationRecoveryClassifier.Classify(inventory),
                null,
                StickyDiscoveryKind.Absent,
                StickyPublicationReason.None);
        }

        if (!TryRestoreRendered(storedPublication, out var rendered) ||
            rendered is null ||
            !AuthorizedStickyReadbackRequest.TryCreateRecovery(
                authorization,
                scope,
                rendered,
                out var request) ||
            request is null)
        {
            return Evaluation(
                PublicationRecoveryClassifier.Classify(inventory with
                {
                    EnumerationComplete = false,
                    Marker = PublicationMarkerObservation.Incomplete,
                }),
                null,
                StickyDiscoveryKind.InvalidOrIncomplete,
                StickyPublicationReason.AdmissionInvalid);
        }

        var discovered = await publisher.DiscoverAsync(
                token,
                request,
                cancellationToken)
            .ConfigureAwait(false);
        var marker = discovered.Kind switch
        {
            StickyDiscoveryKind.Absent => PublicationMarkerObservation.Absent,
            StickyDiscoveryKind.ExactTarget
                when discovered.Receipt is not null =>
                PublicationMarkerObservation.Exact,
            StickyDiscoveryKind.StaleTarget =>
                PublicationMarkerObservation.Ambiguous,
            _ => PublicationMarkerObservation.Incomplete,
        };
        var complete = marker is
            PublicationMarkerObservation.Absent or
            PublicationMarkerObservation.Exact;
        var classified = PublicationRecoveryClassifier.Classify(
            inventory with
            {
                EnumerationComplete =
                    inventory.EnumerationComplete && complete,
                Marker = marker,
            });
        return Evaluation(
            classified,
            marker == PublicationMarkerObservation.Exact
                ? discovered.Receipt
                : null,
            discovered.Kind,
            discovered.Reason);
    }

    internal static bool TryRestoreRendered(
        ValidatedPublicationPayloadV1? stored,
        out R4RenderedStickyComment? rendered)
    {
        rendered = null;
        if (stored is null || stored.FinalizedCommentUtf8.IsDefaultOrEmpty)
        {
            return false;
        }

        try
        {
            var comment = StrictUtf8.GetString(
                stored.FinalizedCommentUtf8.AsSpan());
            var inspected = R4StickyMarker.Inspect(comment);
            if (inspected.Kind != R4StickyInspectionKind.ValidR4 ||
                inspected.Body is null ||
                inspected.Identity is null ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.ScopeSha256,
                    stored.ScopeSha256) ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.BodySha256,
                    stored.BodySha256) ||
                !StringComparer.Ordinal.Equals(
                    inspected.Identity.HeadSha,
                    stored.ReviewedHeadSha))
            {
                return false;
            }

            rendered = new R4RenderedStickyComment(
                comment,
                inspected.Body,
                inspected.Identity,
                ImmutableArray<R4FindingIdentityV1>.Empty,
                RenderedFindingCount: 0,
                OmittedFindingCount: 0);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    private static PublicationRecoveryEvaluation Evaluation(
        PublicationRecoveryDecision decision,
        StickyCommentPublisher.StickyPublicationReceipt? receipt,
        StickyDiscoveryKind kind,
        StickyPublicationReason reason) =>
        new(decision, receipt, kind, reason);
}
