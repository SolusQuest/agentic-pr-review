using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Host.State.Locator;

internal static class LocatorRootSelection
{
    internal static LocatorSelectionResult Select(
        ImmutableArray<LocatorPhysicalCandidate> authenticated,
        ImmutableArray<LocatorUnknownArtifact> unknown,
        int physicalCount)
    {
        if (authenticated.IsDefault ||
            unknown.IsDefault ||
            physicalCount < 0 ||
            authenticated.Length + unknown.Length != physicalCount)
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Unavailable);
        }

        if (physicalCount > LocatorRootFormat.MaximumPhysicalSentinels)
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        if (authenticated.Any(candidate =>
                !OpaqueStoreValidation.IsValid(candidate.Metadata) ||
                !LocatorRootSentinelCodec.IsValid(candidate.Sentinel)) ||
            unknown.Any(artifact =>
                !OpaqueStoreValidation.IsValid(artifact.Metadata)))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        if (physicalCount == 0)
        {
            return LocatorSelectionResult.Absent();
        }

        if (authenticated.IsEmpty)
        {
            return LocatorSelectionResult.Fail(ClassifyUnknown(unknown));
        }

        var root = authenticated[0].Sentinel.Root;
        if (authenticated.Any(candidate =>
            !CryptographicOperations.FixedTimeEquals(
                root,
                candidate.Sentinel.Root)))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var maximumGeneration = authenticated.Max(
            candidate => candidate.Sentinel.Generation);
        if (maximumGeneration == ulong.MaxValue)
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var maximal = authenticated
            .Where(candidate =>
                candidate.Sentinel.Generation == maximumGeneration)
            .ToImmutableArray();
        var representative = maximal[0];
        if (maximal.Any(candidate =>
            !Equivalent(representative.Sentinel, candidate.Sentinel)))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var byObjectId = authenticated.ToDictionary(
            candidate => candidate.Metadata.Reference.ObjectId.Value,
            StringComparer.Ordinal);
        if (!EdgesAreValid(authenticated, unknown, byObjectId))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var retainedMaximal = maximal
            .Where(HasProvenAuthenticatedFloor)
            .ToImmutableArray();
        if (retainedMaximal.IsEmpty)
        {
            return SelectCleanupDebt(
                authenticated,
                unknown,
                physicalCount,
                maximal);
        }

        var head = retainedMaximal
            .OrderByDescending(candidate =>
                candidate.Sentinel.RequiredExpiresAtUnixSeconds)
            .ThenByDescending(candidate =>
                candidate.Metadata.ExpiresAtUnixSeconds)
            .ThenBy(
                candidate => candidate.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First();

        var reachable = BuildReachableLineage(
            head,
            unknown,
            byObjectId,
            out var authorizedUnknown);

        foreach (var candidate in authenticated)
        {
            if (reachable.Any(reachableCandidate =>
                    Equivalent(
                        reachableCandidate.Sentinel,
                        candidate.Sentinel)))
            {
                continue;
            }

            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        var unauthorizedUnknown = unknown
            .Where(artifact => !authorizedUnknown.Contains(
                artifact.Metadata.Reference.ObjectId.Value))
            .ToImmutableArray();
        if (!unauthorizedUnknown.IsEmpty)
        {
            return LocatorSelectionResult.Fail(
                ClassifyUnknown(unauthorizedUnknown));
        }

        var reachableObjectIds = reachable
            .Select(candidate =>
                candidate.Metadata.Reference.ObjectId.Value)
            .ToHashSet(StringComparer.Ordinal);
        var nonAnchorStages = authenticated
            .Where(candidate => !reachableObjectIds.Contains(
                candidate.Metadata.Reference.ObjectId.Value))
            .Select(candidate => candidate.Metadata)
            .Concat(unknown.Select(item => item.Metadata))
            .OrderBy(
                metadata => metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .Select(metadata => new LocatorCleanupStage(
                metadata,
                LocatorCleanupStageKind.NonAnchor));
        var chainAnchorStages = reachable
            .Where(candidate => candidate.Metadata != head.Metadata)
            .OrderBy(candidate => candidate.Sentinel.Generation)
            .ThenBy(
                candidate => candidate.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .Select(candidate => new LocatorCleanupStage(
                candidate.Metadata,
                LocatorCleanupStageKind.ChainAnchor));
        var cleanupStages = nonAnchorStages
            .Concat(chainAnchorStages)
            .ToImmutableArray();
        return LocatorSelectionResult.Success(
            new LocatorSelection(head, cleanupStages, physicalCount));
    }

    internal static bool Equivalent(
        LocatorRootSentinel left,
        LocatorRootSentinel right) =>
        left.Generation == right.Generation &&
        StringComparer.Ordinal.Equals(
            left.WriterKeyId,
            right.WriterKeyId) &&
        CryptographicOperations.FixedTimeEquals(left.Root, right.Root) &&
        left.Predecessors.SequenceEqual(right.Predecessors) &&
        left.Superseded.SequenceEqual(right.Superseded);

    internal static bool HasProvenAuthenticatedFloor(
        LocatorPhysicalCandidate candidate) =>
        candidate.Metadata.ExpiresAtUnixSeconds >=
            candidate.Sentinel.RequiredExpiresAtUnixSeconds;

    private static LocatorSelectionResult SelectCleanupDebt(
        ImmutableArray<LocatorPhysicalCandidate> authenticated,
        ImmutableArray<LocatorUnknownArtifact> unknown,
        int physicalCount,
        ImmutableArray<LocatorPhysicalCandidate> maximal)
    {
        var debt = maximal
            .Select(candidate => candidate.Metadata)
            .OrderBy(
                metadata => metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();
        var maximumGeneration = maximal[0].Sentinel.Generation;
        var remaining = authenticated
            .Where(candidate =>
                candidate.Sentinel.Generation < maximumGeneration)
            .ToImmutableArray();

        if (maximumGeneration == 0)
        {
            return remaining.IsEmpty && unknown.IsEmpty
                ? LocatorSelectionResult.Cleanup(new LocatorCleanupDebt(
                    debt,
                    LocatorCleanupMode.GenerationZeroAbsenceAllowed,
                    maximal[0].Sentinel.Root.ToArray(),
                    MinimumGeneration: 0))
                : LocatorSelectionResult.Fail(
                    unknown.IsEmpty
                        ? LocatorCodes.Conflict
                        : ClassifyUnknown(unknown));
        }

        var fallback = Select(
            remaining,
            unknown,
            physicalCount - maximal.Length);
        if (!fallback.Succeeded)
        {
            return LocatorSelectionResult.Fail(fallback.Code);
        }

        if (fallback.IsAbsent)
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        if (fallback.RequiresCleanup || fallback.Selection is null)
        {
            if (fallback.CleanupDebt is not null)
            {
                CryptographicOperations.ZeroMemory(
                    fallback.CleanupDebt.ExpectedRoot);
            }

            return LocatorSelectionResult.Fail(LocatorCodes.Unavailable);
        }

        var fallbackHead = fallback.Selection.Head;
        var hasExactFallback = maximal.All(candidate =>
            candidate.Sentinel.Predecessors.Any(predecessor =>
                remaining.Any(possibleFallback =>
                    LocatorRootSentinelCodec.IdentityEquals(
                        predecessor,
                        possibleFallback.Metadata) &&
                    Equivalent(
                        possibleFallback.Sentinel,
                        fallbackHead.Sentinel))));
        return hasExactFallback
            ? LocatorSelectionResult.Cleanup(new LocatorCleanupDebt(
                debt,
                LocatorCleanupMode.SuccessorRequiresFallback,
                fallbackHead.Sentinel.Root.ToArray(),
                fallbackHead.Sentinel.Generation))
            : LocatorSelectionResult.Fail(LocatorCodes.Conflict);
    }

    private static string ClassifyUnknown(
        ImmutableArray<LocatorUnknownArtifact> unknown)
    {
        if (unknown.Any(item => StringComparer.Ordinal.Equals(
                item.FailureCode,
                LocatorCodes.KeyUnavailable)))
        {
            return LocatorCodes.KeyUnavailable;
        }

        return unknown.All(item => StringComparer.Ordinal.Equals(
            item.FailureCode,
            LocatorCodes.Unavailable))
            ? LocatorCodes.Unavailable
            : LocatorCodes.AuthenticationFailed;
    }

    private static ImmutableArray<LocatorPhysicalCandidate>
        BuildReachableLineage(
        LocatorPhysicalCandidate head,
        ImmutableArray<LocatorUnknownArtifact> unknown,
        Dictionary<string, LocatorPhysicalCandidate> byObjectId,
        out HashSet<string> authorizedUnknown)
    {
        var reachableBuilder = ImmutableArray.CreateBuilder<
            LocatorPhysicalCandidate>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        authorizedUnknown = new HashSet<string>(StringComparer.Ordinal);
        var unknownByObjectId = unknown.ToDictionary(
            artifact => artifact.Metadata.Reference.ObjectId.Value,
            StringComparer.Ordinal);
        var pending = new Stack<LocatorPhysicalCandidate>();
        pending.Push(head);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current.Metadata.Reference.ObjectId.Value))
            {
                continue;
            }

            reachableBuilder.Add(current);
            foreach (var predecessor in current.Sentinel.Predecessors)
            {
                if (byObjectId.TryGetValue(
                        predecessor.ObjectId,
                        out var resolved))
                {
                    pending.Push(resolved);
                }
                else if (unknownByObjectId.TryGetValue(
                        predecessor.ObjectId,
                        out var unknownPredecessor) &&
                    LocatorRootSentinelCodec.IdentityEquals(
                        predecessor,
                        unknownPredecessor.Metadata))
                {
                    authorizedUnknown.Add(predecessor.ObjectId);
                }
            }

            foreach (var superseded in current.Sentinel.Superseded)
            {
                if (unknownByObjectId.TryGetValue(
                        superseded.ObjectId,
                        out var unknownSuperseded) &&
                    LocatorRootSentinelCodec.IdentityEquals(
                        superseded,
                        unknownSuperseded.Metadata))
                {
                    authorizedUnknown.Add(superseded.ObjectId);
                }
            }
        }

        return reachableBuilder.ToImmutable();
    }

    private static bool EdgesAreValid(
        ImmutableArray<LocatorPhysicalCandidate> authenticated,
        ImmutableArray<LocatorUnknownArtifact> unknown,
        Dictionary<string, LocatorPhysicalCandidate> byObjectId)
    {
        var unknownByObjectId = unknown.ToDictionary(
            artifact => artifact.Metadata.Reference.ObjectId.Value,
            StringComparer.Ordinal);
        foreach (var candidate in authenticated)
        {
            foreach (var predecessor in candidate.Sentinel.Predecessors)
            {
                if (unknownByObjectId.TryGetValue(
                        predecessor.ObjectId,
                        out var unknownPredecessor))
                {
                    if (!LocatorRootSentinelCodec.IdentityEquals(
                            predecessor,
                            unknownPredecessor.Metadata))
                    {
                        return false;
                    }

                    continue;
                }

                if (!byObjectId.TryGetValue(
                        predecessor.ObjectId,
                        out var resolved))
                {
                    continue;
                }

                if (!LocatorRootSentinelCodec.IdentityEquals(
                        predecessor,
                        resolved.Metadata) ||
                    candidate.Sentinel.Generation == 0 ||
                    resolved.Sentinel.Generation !=
                        candidate.Sentinel.Generation - 1 ||
                    !CryptographicOperations.FixedTimeEquals(
                        candidate.Sentinel.Root,
                        resolved.Sentinel.Root))
                {
                    return false;
                }
            }

            foreach (var superseded in candidate.Sentinel.Superseded)
            {
                if (unknownByObjectId.TryGetValue(
                        superseded.ObjectId,
                        out var unknownSuperseded))
                {
                    if (!LocatorRootSentinelCodec.IdentityEquals(
                            superseded,
                            unknownSuperseded.Metadata))
                    {
                        return false;
                    }

                    continue;
                }

                if (!byObjectId.TryGetValue(
                        superseded.ObjectId,
                        out var resolved))
                {
                    continue;
                }

                if (!LocatorRootSentinelCodec.IdentityEquals(
                        superseded,
                        resolved.Metadata) ||
                    resolved.Sentinel.Generation >
                        candidate.Sentinel.Generation ||
                    !CryptographicOperations.FixedTimeEquals(
                        candidate.Sentinel.Root,
                        resolved.Sentinel.Root))
                {
                    return false;
                }
            }
        }

        return true;
    }
}
