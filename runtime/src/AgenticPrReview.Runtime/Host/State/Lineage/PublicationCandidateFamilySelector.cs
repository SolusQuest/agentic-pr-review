namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class PublicationCandidateFamilySelector
{
    internal static AuthenticatedStateObject[] SelectLive(
        IEnumerable<AuthenticatedStateObject> candidates,
        IEnumerable<AuthenticatedStateObject> familyEvidence,
        string epoch,
        string sessionId,
        long trustedNowUnixSeconds)
    {
        var liveDescendantParents = familyEvidence
            .Where(item =>
                IsFamilyEvidence(item) &&
                InScope(item, epoch, sessionId) &&
                item.Header.LogicalExpiresAtUnixSeconds >=
                    trustedNowUnixSeconds &&
                item.Header.PredecessorIdentity is not null)
            .Select(item => item.Header.PredecessorIdentity!)
            .ToHashSet(StringComparer.Ordinal);
        return candidates.Where(item =>
                item.Header.ObjectClass == StateObjectClass.Candidate &&
                InScope(item, epoch, sessionId) &&
                (item.Header.LogicalExpiresAtUnixSeconds >=
                    trustedNowUnixSeconds ||
                    liveDescendantParents.Contains(
                        item.Header.ObjectIdentity)))
            .ToArray();
    }

    private static bool IsFamilyEvidence(AuthenticatedStateObject item) =>
        item.Header.ObjectClass is
            StateObjectClass.PublicationIntent or
            StateObjectClass.PublicationFailure or
            StateObjectClass.Abandonment or
            StateObjectClass.Cleanup;

    private static bool InScope(
        AuthenticatedStateObject item,
        string epoch,
        string sessionId) =>
        StringComparer.Ordinal.Equals(item.Header.Epoch, epoch) &&
        StringComparer.Ordinal.Equals(item.Header.SessionId, sessionId);
}
