using System.Collections.Immutable;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.State.Lineage;

namespace AgenticPrReview.Runtime.Tests.Host.State.Restore;

public sealed class AcceptedStateSelectorTests
{
    [Fact]
    public void SelectsAuthenticatedOriginalGeneration()
    {
        using var fixture = SelectorFixture.Ready();

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.False(result.IsBootstrap);
        Assert.Equal(
            fixture.LogicalIdentity,
            result.Selection!.Current.LogicalGenerationIdentity);
        Assert.Equal(
            "20",
            result.Selection.Current.Physical.Metadata.Reference.ObjectId.Value);
        Assert.Null(result.Selection.ImmediatePredecessor);
    }

    [Fact]
    public void CopyOnlyGenerationIsReadableAndLowestArtifactIdWins()
    {
        using var fixture = SelectorFixture.Ready(
            copyOnly: true,
            addHigherCopy: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.Equal(
            "10",
            result.Selection!.Current.Physical.Metadata.Reference.ObjectId.Value);
        Assert.Equal(
            AcceptedStateTestData.OriginalCandidateIdentity,
            result.Selection.Current.OriginalCandidateObjectIdentity);
    }

    [Fact]
    public void ExactPhysicalAbsenceMintsInitializationAuthority()
    {
        using var fixture = SelectorFixture.Absent();

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.IsBootstrap);
        Assert.NotNull(result.InitialAbsence);
        Assert.Null(result.Selection);
    }

    [Fact]
    public void CandidateWithoutReceiptNeverFallsBackToBootstrap()
    {
        using var fixture = SelectorFixture.Ready(withReceipt: false);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.False(result.IsBootstrap);
        Assert.Equal(AcceptedStateCodes.IncompatibleCurrent, result.Code);
    }

    [Fact]
    public void UnknownCandidateCopyFailsClosed()
    {
        using var fixture = SelectorFixture.Ready(addUnknownCandidate: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.OutcomeUnknown, result.Code);
        Assert.False(result.IsBootstrap);
    }

    [Fact]
    public void DuplicateOriginalCandidatesConflict()
    {
        using var fixture = SelectorFixture.Ready(addDuplicateOriginal: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void TerminalReceiptExpiryMintsTypedExpiryAuthority()
    {
        using var fixture = SelectorFixture.Ready();
        fixture.Time.UnixSeconds =
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds;

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Expired, result.Code);
        Assert.NotNull(result.Expiry);
        Assert.Null(result.Selection);
        Assert.False(result.IsBootstrap);
    }

    [Fact]
    public void OneAuthenticatedReceiptMayNotHaveTwoPhysicalCopies()
    {
        using var fixture = SelectorFixture.Ready(addDuplicateReceipt: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void BoundedTailRequiresCurrentAndImmediateButAllowsOlderAnchorAbsence()
    {
        using var fixture = SelectorFixture.BoundedTail();

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Selection!.ImmediatePredecessor);
        Assert.Equal(2, result.Selection.Current.Generation.Generation);
        Assert.Equal(
            1,
            result.Selection.ImmediatePredecessor.Generation.Generation);
    }

    private sealed class SelectorFixture : IDisposable
    {
        private readonly LineageTestData.ContextLease lease;

        private SelectorFixture(
            LineageTestData.ContextLease lease,
            LineageReadOnlyObservationContext observation,
            LineageResolveRequest request,
            string logicalIdentity)
        {
            this.lease = lease;
            Observation = observation;
            Request = request;
            LogicalIdentity = logicalIdentity;
        }

        internal LineageReadOnlyObservationContext Observation { get; }
        internal LineageResolveRequest Request { get; }
        internal string LogicalIdentity { get; }
        internal MutableLineageTimeProvider Time => lease.Time;

        internal static SelectorFixture Ready(
            bool copyOnly = false,
            bool addHigherCopy = false,
            bool withReceipt = true,
            bool addUnknownCandidate = false,
            bool addDuplicateOriginal = false,
            bool addDuplicateReceipt = false)
        {
            var lease = LineageTestData.Context(
                AcceptedStateTestData.AcceptedAtUnixSeconds);
            var request = LineageTestData.Request(lease.Access);
            Assert.True(LineageBaseScopeCodec.TryDigest(
                request.BaseScope,
                out var baseScopeDigest));
            _ = AcceptedStateTestData.Generation(out var generationBytes);
            Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                generationBytes,
                baseScopeDigest,
                AcceptedStateTestData.Epoch,
                AcceptedStateTestData.SessionId,
                previousAcceptanceReceiptIdentity: null,
                out var logicalIdentity));

            var names = Names();
            var authenticated = ImmutableArray.CreateBuilder<
                AuthenticatedStateObject>();
            var candidatePayload = generationBytes;
            var candidateIdentity =
                AcceptedStateTestData.OriginalCandidateIdentity;
            if (copyOnly)
            {
                var copy = new AcceptedStatePhysicalCopyV1(
                    ImmutableArray.CreateRange(generationBytes),
                    logicalIdentity,
                    AcceptedStateTestData.OriginalCandidateIdentity,
                    SourceArtifactId: "42",
                    SourceArchiveSha256: new string('a', 64),
                    SourceEncryptedEnvelopeSha256: new string('b', 64));
                Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
                    copy,
                    out candidatePayload));
                candidateIdentity = new string('a', 64);
            }

            authenticated.Add(Object(
                names,
                baseScopeDigest,
                StateObjectClass.Candidate,
                objectId: addHigherCopy ? "10" : "20",
                candidateIdentity,
                predecessorIdentity: null,
                candidatePayload));
            if (addHigherCopy)
            {
                authenticated.Add(Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Candidate,
                    objectId: "20",
                    objectIdentity: new string('b', 64),
                    predecessorIdentity: null,
                    candidatePayload));
            }

            if (addDuplicateOriginal)
            {
                authenticated.Add(Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Candidate,
                    objectId: "21",
                    objectIdentity: new string('c', 64),
                    predecessorIdentity: null,
                    generationBytes));
            }

            if (withReceipt)
            {
                _ = AcceptedStateTestData.Receipt(
                    logicalIdentity,
                    AcceptedStateTestData.OriginalCandidateIdentity,
                    out var receiptBytes);
                authenticated.Add(Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Acceptance,
                    objectId: "30",
                    AcceptedStateTestData.ReceiptIdentity,
                    predecessorIdentity: null,
                    receiptBytes));
                if (addDuplicateReceipt)
                {
                    authenticated.Add(Object(
                        names,
                        baseScopeDigest,
                        StateObjectClass.Acceptance,
                        objectId: "31",
                        AcceptedStateTestData.ReceiptIdentity,
                        predecessorIdentity: null,
                        receiptBytes));
                }
            }

            var unknown = addUnknownCandidate
                ? ImmutableArray.Create(new UnknownStateObject(
                    Metadata(
                        names[StateObjectClass.Candidate],
                        "40",
                        AcceptedStateTestData.LogicalExpiresAtUnixSeconds),
                    LineageCodes.AuthenticationFailed))
                : ImmutableArray<UnknownStateObject>.Empty;
            var selectedHead = Head(names, baseScopeDigest);
            var snapshot = new ScopedStateInventorySnapshot(
                names,
                authenticated.ToImmutable(),
                ImmutableArray<AuthenticatedStateObject>.Empty,
                unknown,
                authenticated.Count + unknown.Length + 1);
            var observation = new LineageReadOnlyObservationContext(
                snapshot,
                LineageSelectionResult.Success(new LineageHeadSelection(
                    selectedHead,
                    ImmediatePredecessor: null,
                    EquivalentPhysical:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    SafeNonAnchors:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    SafeChainAnchors:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    PhysicalCount: 1)),
                baseScopeDigest,
                currentKeyId: new string('d', 64),
                inventoryDigest: new string('e', 64),
                requiredPlatformExpiresAtUnixSeconds:
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            return new SelectorFixture(
                lease,
                observation,
                request,
                logicalIdentity);
        }

        internal static SelectorFixture BoundedTail()
        {
            var lease = LineageTestData.Context(
                AcceptedStateTestData.AcceptedAtUnixSeconds);
            var request = LineageTestData.Request(lease.Access);
            Assert.True(LineageBaseScopeCodec.TryDigest(
                request.BaseScope,
                out var baseScopeDigest));
            var names = Names();
            var authenticated = ImmutableArray.CreateBuilder<
                AuthenticatedStateObject>();
            var missingLogicalIdentity = new string('0', 64);
            var missingReceiptIdentity = new string('1', 64);
            var firstOriginalIdentity = new string('2', 64);
            var firstReceiptIdentity = new string('3', 64);
            var secondOriginalIdentity = new string('4', 64);

            var firstGeneration = AcceptedStateTestData.Generation(
                out var firstGenerationBytes,
                generation: 1,
                predecessorEnvelopeSha256: new string('5', 64),
                previousLogicalGenerationIdentity: missingLogicalIdentity);
            Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                firstGenerationBytes,
                baseScopeDigest,
                AcceptedStateTestData.Epoch,
                AcceptedStateTestData.SessionId,
                missingReceiptIdentity,
                out var firstLogicalIdentity));
            _ = AcceptedStateTestData.Receipt(
                firstLogicalIdentity,
                firstOriginalIdentity,
                out var firstReceiptBytes,
                missingLogicalIdentity,
                missingReceiptIdentity);

            var secondGeneration = AcceptedStateTestData.Generation(
                out var secondGenerationBytes,
                generation: 2,
                predecessorEnvelopeSha256:
                    firstGeneration.StateEnvelopeSha256,
                previousLogicalGenerationIdentity: firstLogicalIdentity);
            Assert.True(AcceptedStateIdentity.TryComputeLogicalGeneration(
                secondGenerationBytes,
                baseScopeDigest,
                AcceptedStateTestData.Epoch,
                AcceptedStateTestData.SessionId,
                firstReceiptIdentity,
                out var secondLogicalIdentity));
            _ = AcceptedStateTestData.Receipt(
                secondLogicalIdentity,
                secondOriginalIdentity,
                out var secondReceiptBytes,
                firstLogicalIdentity,
                firstReceiptIdentity);

            authenticated.Add(Object(
                names,
                baseScopeDigest,
                StateObjectClass.Candidate,
                "20",
                firstOriginalIdentity,
                missingReceiptIdentity,
                firstGenerationBytes));
            authenticated.Add(Object(
                names,
                baseScopeDigest,
                StateObjectClass.Acceptance,
                "30",
                firstReceiptIdentity,
                missingReceiptIdentity,
                firstReceiptBytes));
            authenticated.Add(Object(
                names,
                baseScopeDigest,
                StateObjectClass.Candidate,
                "21",
                secondOriginalIdentity,
                firstReceiptIdentity,
                secondGenerationBytes));
            authenticated.Add(Object(
                names,
                baseScopeDigest,
                StateObjectClass.Acceptance,
                "31",
                new string('6', 64),
                firstReceiptIdentity,
                secondReceiptBytes));

            var selectedHead = Head(names, baseScopeDigest);
            var snapshot = new ScopedStateInventorySnapshot(
                names,
                authenticated.ToImmutable(),
                ImmutableArray<AuthenticatedStateObject>.Empty,
                ImmutableArray<UnknownStateObject>.Empty,
                PhysicalCount: authenticated.Count + 1);
            var observation = new LineageReadOnlyObservationContext(
                snapshot,
                LineageSelectionResult.Success(new LineageHeadSelection(
                    selectedHead,
                    ImmediatePredecessor: null,
                    EquivalentPhysical:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    SafeNonAnchors:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    SafeChainAnchors:
                        ImmutableArray<OpaqueStoreObjectMetadata>.Empty,
                    PhysicalCount: 1)),
                baseScopeDigest,
                currentKeyId: new string('d', 64),
                inventoryDigest: new string('e', 64),
                requiredPlatformExpiresAtUnixSeconds:
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            return new SelectorFixture(
                lease,
                observation,
                request,
                secondLogicalIdentity);
        }

        internal static SelectorFixture Absent()
        {
            var lease = LineageTestData.Context(
                AcceptedStateTestData.AcceptedAtUnixSeconds);
            var request = LineageTestData.Request(lease.Access);
            Assert.True(LineageBaseScopeCodec.TryDigest(
                request.BaseScope,
                out var baseScopeDigest));
            var snapshot = new ScopedStateInventorySnapshot(
                Names(),
                ImmutableArray<AuthenticatedStateObject>.Empty,
                ImmutableArray<AuthenticatedStateObject>.Empty,
                ImmutableArray<UnknownStateObject>.Empty,
                PhysicalCount: 0);
            var observation = new LineageReadOnlyObservationContext(
                snapshot,
                LineageSelectionResult.Absent(),
                baseScopeDigest,
                currentKeyId: new string('d', 64),
                inventoryDigest: new string('e', 64),
                requiredPlatformExpiresAtUnixSeconds:
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            return new SelectorFixture(
                lease,
                observation,
                request,
                logicalIdentity: string.Empty);
        }

        public void Dispose()
        {
            Observation.Dispose();
            lease.Dispose();
        }

        private static ImmutableDictionary<StateObjectClass, OpaqueStoreName>
            Names() => StateObjectClasses.All.ToImmutableDictionary(
                value => value,
                value => new OpaqueStoreName(
                    "apr-test-" + StateObjectClasses.ToWireName(value)));

        private static AuthenticatedStateObject Object(
            ImmutableDictionary<StateObjectClass, OpaqueStoreName> names,
            string baseScopeDigest,
            StateObjectClass objectClass,
            string objectId,
            string objectIdentity,
            string? predecessorIdentity,
            byte[] payload) =>
            new(
                Metadata(
                    names[objectClass],
                    objectId,
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds),
                new StateControlHeaderV1(
                    baseScopeDigest,
                    AcceptedStateTestData.Epoch,
                    AcceptedStateTestData.SessionId,
                    objectClass,
                    KeyId: new string('d', 64),
                    objectIdentity,
                    predecessorIdentity,
                    SuccessorIdentity: null,
                    ProducingRunIdentity: "workflow-run-42",
                    ProducingRunAttempt: 1,
                    AcceptedStateTestData.AcceptedAtUnixSeconds,
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
                    RequiredPlatformExpiresAtUnixSeconds:
                        AcceptedStateTestData.LogicalExpiresAtUnixSeconds +
                            3_600),
                payload.ToArray());

        private static LineageHeadCandidate Head(
            ImmutableDictionary<StateObjectClass, OpaqueStoreName> names,
            string baseScopeDigest)
        {
            var metadata = Metadata(
                names[StateObjectClass.LineageHead],
                "1",
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            var header = new StateControlHeaderV1(
                baseScopeDigest,
                AcceptedStateTestData.Epoch,
                AcceptedStateTestData.SessionId,
                StateObjectClass.LineageHead,
                KeyId: new string('d', 64),
                ObjectIdentity: new string('f', 64),
                PredecessorIdentity: null,
                SuccessorIdentity: null,
                ProducingRunIdentity: "workflow-run-42",
                ProducingRunAttempt: 1,
                AcceptedStateTestData.AcceptedAtUnixSeconds,
                AcceptedStateTestData.LogicalExpiresAtUnixSeconds,
                RequiredPlatformExpiresAtUnixSeconds:
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds + 3_600);
            var head = new LineageHeadV1(
                LineageTransitionKind.Initial,
                Ordinal: 0,
                LineageTestData.Reviewed(),
                PreviousEpoch: null,
                PreviousHeadIdentity: null,
                TransitionEvidenceIdentity: null,
                ExpiryBoundaryUnixSeconds: null,
                PhysicalPredecessors:
                    ImmutableArray<LineageArtifactEvidence>.Empty,
                PhysicalSuperseded:
                    ImmutableArray<LineageArtifactEvidence>.Empty,
                Superseded: ImmutableArray<LineageArtifactEvidence>.Empty,
                CompletedCleanup:
                    ImmutableArray<LineageArtifactEvidence>.Empty);
            return new LineageHeadCandidate(metadata, header, head);
        }

        private static OpaqueStoreObjectMetadata Metadata(
            OpaqueStoreName name,
            string objectId,
            long expiresAtUnixSeconds) =>
            new(
                new OpaqueStoreObjectReference(
                    name,
                    new OpaqueStoreObjectId(objectId)),
                new OpaqueStoreProducingRun("workflow-run-42", 1),
                new OpaqueStoreArchiveDigest(new string('a', 64)),
                new OpaqueStoreEncryptedObjectDigest(new string('b', 64)),
                expiresAtUnixSeconds,
                Size: 1024);
    }
}
