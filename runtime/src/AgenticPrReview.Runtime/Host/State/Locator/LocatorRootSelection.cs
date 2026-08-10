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
            return LocatorSelectionResult.Fail(
                unknown.Any(item => StringComparer.Ordinal.Equals(
                    item.FailureCode,
                    LocatorCodes.KeyUnavailable))
                    ? LocatorCodes.KeyUnavailable
                    : LocatorCodes.AuthenticationFailed);
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

        var retainedMaximal = maximal
            .Where(HasProvenAuthenticatedFloor)
            .ToImmutableArray();
        if (retainedMaximal.IsEmpty)
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Unavailable);
        }

        var head = retainedMaximal
            .OrderByDescending(candidate =>
                candidate.Metadata.ExpiresAtUnixSeconds)
            .ThenByDescending(candidate =>
                candidate.Sentinel.RequiredExpiresAtUnixSeconds)
            .ThenBy(
                candidate => candidate.Metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First();
        var byObjectId = authenticated.ToDictionary(
            candidate => candidate.Metadata.Reference.ObjectId.Value,
            StringComparer.Ordinal);
        if (!EdgesAreValid(authenticated, unknown, byObjectId))
        {
            return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
        }

        foreach (var candidate in authenticated)
        {
            if (ReferenceEquals(candidate, head) ||
                Equivalent(head.Sentinel, candidate.Sentinel))
            {
                continue;
            }

            if (!ContainsExact(
                    head.Sentinel.Predecessors,
                    candidate.Metadata) &&
                !ContainsExact(
                    head.Sentinel.Superseded,
                    candidate.Metadata))
            {
                return LocatorSelectionResult.Fail(LocatorCodes.Conflict);
            }
        }

        foreach (var artifact in unknown)
        {
            if (!ContainsExact(
                    head.Sentinel.Superseded,
                    artifact.Metadata))
            {
                return LocatorSelectionResult.Fail(
                    StringComparer.Ordinal.Equals(
                        artifact.FailureCode,
                        LocatorCodes.KeyUnavailable)
                        ? LocatorCodes.KeyUnavailable
                        : StringComparer.Ordinal.Equals(
                            artifact.FailureCode,
                            LocatorCodes.Unavailable)
                            ? LocatorCodes.Unavailable
                            : LocatorCodes.AuthenticationFailed);
            }
        }

        var safeToDelete = authenticated
            .Select(candidate => candidate.Metadata)
            .Concat(unknown.Select(item => item.Metadata))
            .Where(metadata => metadata != head.Metadata)
            .OrderBy(
                metadata => metadata.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();
        return LocatorSelectionResult.Success(
            new LocatorSelection(head, safeToDelete, physicalCount));
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

    private static bool ContainsExact(
        ImmutableArray<LocatorArtifactIdentity> references,
        OpaqueStoreObjectMetadata metadata) =>
        references.Any(reference =>
            LocatorRootSentinelCodec.IdentityEquals(reference, metadata));
}
