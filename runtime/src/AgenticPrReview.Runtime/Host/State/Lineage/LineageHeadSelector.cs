using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Lineage;

internal static class LineageHeadSelector
{
    internal static LineageSelectionResult Select(
        ImmutableArray<LineageHeadCandidate> candidates,
        ImmutableArray<UnknownStateObject> unknown,
        int physicalCount,
        string currentKeyId)
    {
        if (candidates.IsDefault ||
            unknown.IsDefault ||
            physicalCount != candidates.Length + unknown.Length ||
            physicalCount is < 0 or > LineageFormat.MaximumPhysicalPerClass ||
            !LineageValidation.IsSha256(currentKeyId))
        {
            return LineageSelectionResult.Fail(LineageCodes.Conflict);
        }

        if (physicalCount == 0)
        {
            return LineageSelectionResult.Absent();
        }

        if (candidates.IsEmpty)
        {
            return LineageSelectionResult.Fail(unknown.Any(item =>
                    StringComparer.Ordinal.Equals(
                        item.FailureCode,
                        LineageCodes.RetentionFailed))
                ? LineageCodes.RetentionFailed
                : LineageCodes.KeyUnavailable);
        }

        if (candidates.Any(candidate =>
                !OpaqueStoreValidation.IsValid(candidate.Metadata) ||
                !LineageValidation.IsValid(candidate.Header) ||
                candidate.Header.ObjectClass != StateObjectClass.LineageHead ||
                !LineageHeadCodec.IsValid(candidate.Head) ||
                !HeaderMatchesHead(candidate.Header, candidate.Head)))
        {
            return LineageSelectionResult.Fail(
                LineageCodes.AuthenticationFailed);
        }

        var groups = candidates
            .GroupBy(candidate =>
                candidate.Header.ObjectIdentity,
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToImmutableArray(),
                StringComparer.Ordinal);
        foreach (var group in groups.Values)
        {
            var exemplar = group[0].Head;
            if (group.Any(candidate =>
                    !LineageHeadCodec.Equivalent(exemplar, candidate.Head)))
            {
                return LineageSelectionResult.Fail(LineageCodes.Conflict);
            }
        }

        var maximalOrdinal = groups.Values.Max(group => group[0].Head.Ordinal);
        var maximal = groups.Values
            .Where(group => group[0].Head.Ordinal == maximalOrdinal)
            .ToImmutableArray();
        if (maximal.Length != 1)
        {
            return LineageSelectionResult.Fail(LineageCodes.Conflict);
        }

        var selectedGroup = maximal[0];
        var selected = ChooseSurvivor(selectedGroup, currentKeyId);
        var chainIdentities = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.Ordinal);
        chainIdentities.Add(selected.Header.ObjectIdentity);
        var chain = ImmutableArray.CreateBuilder<LineageHeadCandidate>();
        chain.Add(selected);
        LineageHeadCandidate? immediatePredecessor = null;
        var cursor = selected;
        while (cursor.Head.PreviousHeadIdentity is not null)
        {
            if (!groups.TryGetValue(
                    cursor.Head.PreviousHeadIdentity,
                    out var previousGroup))
            {
                break;
            }

            var previous = ChooseSurvivor(previousGroup, currentKeyId);
            if (previous.Head.Ordinal == ulong.MaxValue ||
                previous.Head.Ordinal + 1 != cursor.Head.Ordinal ||
                !StringComparer.Ordinal.Equals(
                    cursor.Head.PreviousEpoch,
                    previous.Header.Epoch) ||
                !StringComparer.Ordinal.Equals(
                    cursor.Header.PredecessorIdentity,
                    previous.Header.ObjectIdentity) ||
                !cursor.Head.PhysicalPredecessors.Any(evidence =>
                    LineageHeadCodec.Matches(evidence, previous.Metadata)) ||
                !chainIdentities.Add(previous.Header.ObjectIdentity))
            {
                return LineageSelectionResult.Fail(LineageCodes.Conflict);
            }

            immediatePredecessor ??= previous;
            chain.Add(previous);
            cursor = previous;
        }

        if (cursor.Head.PreviousHeadIdentity is null &&
            cursor.Head.Transition != LineageTransitionKind.Initial)
        {
            return LineageSelectionResult.Fail(LineageCodes.Conflict);
        }

        if (groups.Keys.Any(identity => !chainIdentities.Contains(identity)))
        {
            // A later head cannot retroactively arbitrate a known,
            // authenticated side branch by merely naming it as superseded.
            return LineageSelectionResult.Fail(LineageCodes.Conflict);
        }

        var authorizedEvidence = chain
            .SelectMany(candidate => candidate.Head.PhysicalSuperseded)
            .Concat(selected.Head.Superseded)
            .Concat(selected.Head.CompletedCleanup)
            .ToImmutableArray();
        var safeNonAnchors =
            ImmutableArray.CreateBuilder<OpaqueStoreObjectMetadata>();
        foreach (var group in groups.Values)
        {
            var survivor = ChooseSurvivor(group, currentKeyId);
            foreach (var duplicate in group.Where(duplicate =>
                duplicate.Metadata != survivor.Metadata))
            {
                safeNonAnchors.Add(duplicate.Metadata);
            }
        }

        var safeChainAnchors = chain
            .Where(candidate =>
                candidate.Header.ObjectIdentity !=
                    selected.Header.ObjectIdentity &&
                (immediatePredecessor is null ||
                    candidate.Header.ObjectIdentity !=
                        immediatePredecessor.Header.ObjectIdentity))
            .OrderBy(candidate => candidate.Head.Ordinal)
            .Select(candidate => candidate.Metadata)
            .ToImmutableArray();

        foreach (var item in unknown)
        {
            if (!OpaqueStoreValidation.IsValid(item.Metadata))
            {
                return LineageSelectionResult.Fail(
                    LineageCodes.KeyUnavailable);
            }

            if (selected.Head.PhysicalPredecessors.Any(evidence =>
                    LineageHeadCodec.Matches(evidence, item.Metadata)))
            {
                continue;
            }

            if (!authorizedEvidence.Any(evidence =>
                    LineageHeadCodec.Matches(evidence, item.Metadata)))
            {
                return LineageSelectionResult.Fail(
                    StringComparer.Ordinal.Equals(
                        item.FailureCode,
                        LineageCodes.RetentionFailed)
                        ? LineageCodes.RetentionFailed
                        : LineageCodes.KeyUnavailable);
            }

            safeNonAnchors.Add(item.Metadata);
        }

        return LineageSelectionResult.Success(new LineageHeadSelection(
            selected,
            immediatePredecessor,
            selectedGroup.Select(candidate => candidate.Metadata)
                .OrderBy(metadata => metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .ToImmutableArray(),
            safeNonAnchors.Distinct().OrderBy(
                    metadata => metadata.Reference.ObjectId.Value,
                    StringComparer.Ordinal)
                .ToImmutableArray(),
            safeChainAnchors,
            physicalCount));
    }

    private static bool HeaderMatchesHead(
        StateControlHeaderV1 header,
        LineageHeadV1 head) =>
        StringComparer.Ordinal.Equals(
            header.PredecessorIdentity,
            head.PreviousHeadIdentity) &&
        header.SuccessorIdentity is null &&
        (head.Transition == LineageTransitionKind.Initial
            ? header.PredecessorIdentity is null
            : header.PredecessorIdentity is not null);

    private static LineageHeadCandidate ChooseSurvivor(
        ImmutableArray<LineageHeadCandidate> group,
        string currentKeyId) =>
        group.OrderByDescending(candidate =>
                StringComparer.Ordinal.Equals(
                    candidate.Header.KeyId,
                    currentKeyId))
            .ThenByDescending(candidate =>
                candidate.Header.RequiredPlatformExpiresAtUnixSeconds)
            .ThenByDescending(candidate => candidate.Header.CreatedAtUnixSeconds)
            .ThenBy(candidate =>
                candidate.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First();
}
