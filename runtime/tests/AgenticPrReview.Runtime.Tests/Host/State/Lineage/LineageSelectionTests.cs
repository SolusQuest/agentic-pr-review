using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageSelectionTests
{
    [Fact]
    public void EquivalentPhysicalClaimsConvergeToCurrentKeySurvivor()
    {
        var head = InitialHead();
        var old = Candidate(
            "old",
            new string('1', 64),
            new string('a', 64),
            head,
            requiredExpiry: LineageTestData.LogicalExpiry + 10);
        var current = Candidate(
            "current",
            new string('2', 64),
            new string('a', 64),
            head,
            requiredExpiry: LineageTestData.LogicalExpiry + 20);

        var result = LineageHeadSelector.Select(
            [old, current],
            [],
            physicalCount: 2,
            currentKeyId: new string('2', 64));

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(current.Metadata, result.Selection!.Head.Metadata);
        Assert.Equal(old.Metadata, Assert.Single(
            result.Selection.SafeToDelete));
    }

    [Fact]
    public void DistinctConcurrentInitialClaimsConflict()
    {
        var result = LineageHeadSelector.Select(
        [
            Candidate(
                "one",
                new string('1', 64),
                new string('a', 64),
                InitialHead()),
            Candidate(
                "two",
                new string('1', 64),
                new string('b', 64),
                InitialHead() with
                {
                    Reviewed = LineageTestData.Reviewed('3', '4'),
                }),
        ],
            [],
            physicalCount: 2,
            currentKeyId: new string('1', 64));

        Assert.False(result.Succeeded);
        Assert.Equal(LineageCodes.Conflict, result.Code);
    }

    [Fact]
    public void MissingPrunedPredecessorIsAllowedButPresentMismatchFails()
    {
        var predecessor = Candidate(
            "predecessor",
            new string('1', 64),
            new string('a', 64),
            InitialHead());
        var successorHead = new LineageHeadV1(
            LineageTransitionKind.Reset,
            Ordinal: 1,
            LineageTestData.Reviewed('3', '4'),
            predecessor.Header.Epoch,
            predecessor.Header.ObjectIdentity,
            new string('c', 64),
            ExpiryBoundaryUnixSeconds: null,
            [LineageHeadCodec.Evidence(predecessor.Metadata)],
            [],
            [],
            []);
        var successor = Candidate(
            "successor",
            new string('1', 64),
            new string('b', 64),
            successorHead);

        var absent = LineageHeadSelector.Select(
            [successor],
            [],
            physicalCount: 1,
            currentKeyId: new string('1', 64));
        Assert.True(absent.Succeeded, absent.Code);
        Assert.Null(absent.Selection!.ImmediatePredecessor);

        var unavailablePredecessor = LineageHeadSelector.Select(
            [successor],
            [new UnknownStateObject(
                predecessor.Metadata,
                LineageCodes.KeyUnavailable)],
            physicalCount: 2,
            currentKeyId: new string('1', 64));
        Assert.True(unavailablePredecessor.Succeeded);
        Assert.Empty(unavailablePredecessor.Selection!.SafeToDelete);

        var substituted = predecessor with
        {
            Metadata = Metadata(
                "substituted",
                new string('f', 64),
                new string('e', 64)),
        };
        var present = LineageHeadSelector.Select(
            [substituted, successor],
            [],
            physicalCount: 2,
            currentKeyId: new string('1', 64));
        Assert.False(present.Succeeded);
        Assert.Equal(LineageCodes.Conflict, present.Code);
    }

    [Fact]
    public void UnknownHistoricalArtifactRequiresExactAuthenticatedEvidence()
    {
        var unknownMetadata = Metadata(
            "unknown",
            new string('d', 64),
            new string('e', 64));
        var head = InitialHead() with
        {
            PhysicalSuperseded =
                [LineageHeadCodec.Evidence(unknownMetadata)],
        };
        var current = Candidate(
            "head",
            new string('1', 64),
            new string('a', 64),
            head);
        var accepted = LineageHeadSelector.Select(
            [current],
            [new UnknownStateObject(
                unknownMetadata,
                LineageCodes.KeyUnavailable)],
            physicalCount: 2,
            currentKeyId: new string('1', 64));
        Assert.True(accepted.Succeeded, accepted.Code);
        Assert.Contains(unknownMetadata, accepted.Selection!.SafeToDelete);

        var changedVariants = new[]
        {
            unknownMetadata with
            {
                Reference = unknownMetadata.Reference with
                {
                    ObjectId = new OpaqueStoreObjectId("changed-object"),
                },
            },
            unknownMetadata with
            {
                ProducingRun = new OpaqueStoreProducingRun(
                    "changed-run",
                    unknownMetadata.ProducingRun.Attempt),
            },
            unknownMetadata with
            {
                ProducingRun = new OpaqueStoreProducingRun(
                    unknownMetadata.ProducingRun.Identity,
                    unknownMetadata.ProducingRun.Attempt + 1),
            },
            unknownMetadata with
            {
                ArchiveDigest = new OpaqueStoreArchiveDigest(
                    new string('f', 64)),
            },
            unknownMetadata with
            {
                EncryptedObjectDigest =
                    new OpaqueStoreEncryptedObjectDigest(
                        new string('f', 64)),
            },
            unknownMetadata with
            {
                ExpiresAtUnixSeconds =
                    unknownMetadata.ExpiresAtUnixSeconds + 1,
            },
            unknownMetadata with { Size = unknownMetadata.Size + 1 },
        };
        foreach (var changed in changedVariants)
        {
            var rejected = LineageHeadSelector.Select(
                [current],
                [new UnknownStateObject(
                    changed,
                    LineageCodes.KeyUnavailable)],
                physicalCount: 2,
                currentKeyId: new string('1', 64));
            Assert.False(rejected.Succeeded);
            Assert.Equal(LineageCodes.KeyUnavailable, rejected.Code);
        }
    }

    [Fact]
    public void AuthenticatedSideBranchCannotBeRetroactivelySuperseded()
    {
        var predecessor = Candidate(
            "predecessor",
            new string('1', 64),
            new string('a', 64),
            InitialHead());
        var first = Candidate(
            "first",
            new string('1', 64),
            new string('b', 64),
            Successor(predecessor, new string('1', 64)));
        var side = Candidate(
            "side",
            new string('1', 64),
            new string('c', 64),
            Successor(predecessor, new string('2', 64)));
        var newest = Candidate(
            "newest",
            new string('1', 64),
            new string('d', 64),
            Successor(first, new string('3', 64)) with
            {
                Superseded = [LineageHeadCodec.Evidence(side.Metadata)],
            });

        var result = LineageHeadSelector.Select(
            [predecessor, first, side, newest],
            [],
            physicalCount: 4,
            currentKeyId: new string('1', 64));

        Assert.False(result.Succeeded);
        Assert.Equal(LineageCodes.Conflict, result.Code);
    }

    [Fact]
    public void InitialHeadCannotClaimTransitionCleanupAuthority()
    {
        var evidence = LineageHeadCodec.Evidence(Metadata(
            "stale",
            new string('c', 64),
            new string('d', 64)));

        Assert.False(LineageHeadCodec.TryEncode(
            InitialHead() with { Superseded = [evidence] },
            out _));
        Assert.False(LineageHeadCodec.TryEncode(
            InitialHead() with { CompletedCleanup = [evidence] },
            out _));
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, false)]
    public void PhysicalHeadCountIsBounded(int count, bool expected)
    {
        var head = InitialHead();
        var candidates = Enumerable.Range(0, count)
            .Select(index => Candidate(
                $"head-{index}",
                new string('1', 64),
                new string('a', 64),
                head))
            .ToImmutableArray();
        var result = LineageHeadSelector.Select(
            candidates,
            [],
            count,
            new string('1', 64));
        Assert.Equal(expected, result.Succeeded);
    }

    private static LineageHeadV1 InitialHead() =>
        new(
            LineageTransitionKind.Initial,
            Ordinal: 0,
            LineageTestData.Reviewed(),
            PreviousEpoch: null,
            PreviousHeadIdentity: null,
            TransitionEvidenceIdentity: null,
            ExpiryBoundaryUnixSeconds: null,
            PhysicalPredecessors: [],
            PhysicalSuperseded: [],
            Superseded: [],
            CompletedCleanup: []);

    private static LineageHeadV1 Successor(
        LineageHeadCandidate predecessor,
        string transitionEvidenceIdentity) =>
        new(
            LineageTransitionKind.Reset,
            predecessor.Head.Ordinal + 1,
            LineageTestData.Reviewed('3', '4'),
            predecessor.Header.Epoch,
            predecessor.Header.ObjectIdentity,
            transitionEvidenceIdentity,
            ExpiryBoundaryUnixSeconds: null,
            [LineageHeadCodec.Evidence(predecessor.Metadata)],
            [],
            [],
            []);

    private static LineageHeadCandidate Candidate(
        string objectId,
        string keyId,
        string objectIdentity,
        LineageHeadV1 head,
        long? requiredExpiry = null)
    {
        var metadata = Metadata(
            objectId,
            archive: new string('c', 64),
            encrypted: new string('d', 64));
        return new LineageHeadCandidate(
            metadata,
            new StateControlHeaderV1(
                new string('0', 64),
                head.Transition == LineageTransitionKind.Initial
                    ? new string('9', 64)
                    : new string('8', 64),
                new string('7', 64),
                StateObjectClass.LineageHead,
                keyId,
                objectIdentity,
                head.PreviousHeadIdentity,
                SuccessorIdentity: null,
                "test-run",
                ProducingRunAttempt: 1,
                LineageTestData.Now,
                LineageTestData.LogicalExpiry,
                requiredExpiry ?? LineageTestData.Now + 8 * 24 * 60 * 60),
            head);
    }

    private static OpaqueStoreObjectMetadata Metadata(
        string objectId,
        string archive,
        string encrypted) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName("apr-state-lineage-head"),
                new OpaqueStoreObjectId(objectId)),
            new OpaqueStoreProducingRun("transport-run", 1),
            new OpaqueStoreArchiveDigest(archive),
            new OpaqueStoreEncryptedObjectDigest(encrypted),
            LineageTestData.SentinelExpiry,
            Size: 128);
}
