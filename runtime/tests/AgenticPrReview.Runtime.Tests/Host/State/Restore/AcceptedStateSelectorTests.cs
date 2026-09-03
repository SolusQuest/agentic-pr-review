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

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(5, true)]
    public void InitialAbsenceRetainsMinimumPlatformExpiry(
        long expiryDelta,
        bool expected)
    {
        using var fixture = SelectorFixture.Absent();
        var authority = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request).InitialAbsence!;
        using var observed = ReobserveAbsence(fixture, expiryDelta);

        Assert.Equal(expected, authority.Allows(
            fixture.Request.Access, fixture.Request, observed));
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("scope")]
    [InlineData("inventory")]
    [InlineData("key")]
    [InlineData("run")]
    [InlineData("attempt")]
    [InlineData("logical-expiry")]
    [InlineData("physical-count")]
    public void LongerPlatformRetentionDoesNotRelaxAbsenceIdentity(
        string mutation)
    {
        using var fixture = SelectorFixture.Absent();
        var authority = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request).InitialAbsence!;
        var request = mutation switch
        {
            "repository" => fixture.Request with
            {
                BaseScope = fixture.Request.BaseScope with
                {
                    RepositoryId = "other/repository",
                },
            },
            "run" => fixture.Request with
            {
                ProducingRunIdentity = "other-run",
            },
            "attempt" => fixture.Request with
            {
                ProducingRunAttempt = fixture.Request.ProducingRunAttempt + 1,
            },
            "logical-expiry" => fixture.Request with
            {
                RequiredLogicalExpiresAtUnixSeconds =
                    fixture.Request.RequiredLogicalExpiresAtUnixSeconds + 1,
            },
            _ => fixture.Request,
        };
        using var observed = ReobserveAbsence(fixture, 5, mutation);

        Assert.False(authority.Allows(request.Access, request, observed));
    }

    private static LineageReadOnlyObservationContext ReobserveAbsence(
        SelectorFixture fixture,
        long expiryDelta,
        string? mutation = null)
    {
        var before = fixture.Observation;
        return new LineageReadOnlyObservationContext(
            new ScopedStateInventorySnapshot(
                before.Snapshot!.Names, [], [], [],
                PhysicalCount: mutation == "physical-count" ? 1 : 0),
            LineageSelectionResult.Absent(),
            mutation == "scope" ? new string('1', 64) : before.BaseScopeDigest,
            mutation == "key" ? new string('2', 64) : before.CurrentKeyId,
            mutation == "inventory" ? new string('3', 64) : before.InventoryDigest,
            before.RequiredPlatformExpiresAtUnixSeconds + expiryDelta);
    }

    [Fact]
    public void UniqueInitialCandidateWithoutReceiptPreservesBootstrapForP5()
    {
        using var fixture = SelectorFixture.Ready(withReceipt: false);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.IsBootstrap);
        Assert.Equal(AcceptedStateCodes.Bootstrap, result.Code);
        Assert.Null(result.InitialAbsence);
        Assert.Null(result.Selection);
    }

    [Fact]
    public void AmbiguousInitialCandidatesWithoutReceiptFailClosed()
    {
        using var fixture = SelectorFixture.Ready(
            withReceipt: false,
            addDuplicateOriginal: true);

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
    public void TerminalReceiptExpiryPreservesSelectionForAdmission()
    {
        using var fixture = SelectorFixture.Ready();
        fixture.Time.UnixSeconds =
            AcceptedStateTestData.LogicalExpiresAtUnixSeconds;

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Expired, result.Code);
        Assert.NotNull(result.Expiry);
        Assert.NotNull(result.Selection);
        Assert.False(result.Expiry!.TryAuthorize(null, out _));
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

    [Fact]
    public void NonActiveAcceptedStateRequiresExactSelectedHeadEvidence()
    {
        using var fixture = SelectorFixture.Ready(addStaleCandidate: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
        Assert.False(result.IsBootstrap);
    }

    [Fact]
    public void ExactSelectedHeadEvidenceAccountsForNonActiveAcceptedState()
    {
        using var fixture = SelectorFixture.Ready(
            addStaleCandidate: true,
            authorizeStaleCandidate: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.Succeeded, result.Code);
    }

    [Fact]
    public void CopyMayNotNameItsOwnPhysicalArtifactAsSource()
    {
        using var fixture = SelectorFixture.Ready(
            copyOnly: true,
            selfSourceCopy: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.IncompatibleCurrent, result.Code);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("archive")]
    [InlineData("envelope")]
    public void CopyOnlyGenerationRequiresOneExactSourceTuple(
        string mutation)
    {
        using var fixture = SelectorFixture.Ready(
            copyOnly: true,
            addHigherCopy: true,
            copyProvenanceMutation: mutation);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void LiveOriginalRequiresCopySourceTupleToMatchMetadata()
    {
        using var fixture = SelectorFixture.Ready(
            addHigherCopy: true,
            addCopyAlongsideOriginal: true,
            copyProvenanceMutation: "archive");

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void CurrentGenerationMustNameImmediatePredecessorEnvelope()
    {
        using var fixture = SelectorFixture.BoundedTail(
            currentPredecessorMismatch: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void EveryPresentOlderReceiptMustContinueLogicalLineage()
    {
        using var fixture = SelectorFixture.BoundedTail(
            includeOlderReceipt: true,
            olderReceiptLogicalMismatch: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.Equal(AcceptedStateCodes.Conflict, result.Code);
    }

    [Fact]
    public void ConsistentPresentOlderReceiptMayTerminateAtAbsentTail()
    {
        using var fixture = SelectorFixture.BoundedTail(
            includeOlderReceipt: true);

        var result = new AcceptedStateSelector(fixture.Time)
            .Select(fixture.Observation, fixture.Request);

        Assert.True(result.Succeeded, result.Code);
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
            bool addDuplicateReceipt = false,
            bool addStaleCandidate = false,
            bool authorizeStaleCandidate = false,
            bool selfSourceCopy = false,
            bool addCopyAlongsideOriginal = false,
            string? copyProvenanceMutation = null)
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
            var copy = new AcceptedStatePhysicalCopyV1(
                ImmutableArray.CreateRange(generationBytes),
                logicalIdentity,
                AcceptedStateTestData.OriginalCandidateIdentity,
                SourceArtifactId: selfSourceCopy
                    ? addHigherCopy ? "10" : "20"
                    : "42",
                SourceArchiveSha256: new string('a', 64),
                SourceEncryptedEnvelopeSha256: new string('b', 64));
            if (copyOnly)
            {
                Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
                    copy,
                    out candidatePayload));
                candidateIdentity = new string('a', 64);
            }

            AuthenticatedStateObject? staleCandidate = null;
            if (addStaleCandidate)
            {
                staleCandidate = Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Candidate,
                    objectId: "41",
                    objectIdentity: new string('c', 64),
                    predecessorIdentity: null,
                    generationBytes,
                    epoch: new string('a', 64),
                    sessionId: new string('b', 64));
                authenticated.Add(staleCandidate);
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
                var higherPayload = candidatePayload;
                if (copyOnly || addCopyAlongsideOriginal)
                {
                    var higherCopy = addCopyAlongsideOriginal
                        ? copy with { SourceArtifactId = "10" }
                        : copy;
                    higherCopy = copyProvenanceMutation switch
                    {
                        "artifact" => higherCopy with
                        {
                            SourceArtifactId = "43",
                        },
                        "archive" => higherCopy with
                        {
                            SourceArchiveSha256 = new string('c', 64),
                        },
                        "envelope" => higherCopy with
                        {
                            SourceEncryptedEnvelopeSha256 =
                                new string('c', 64),
                        },
                        null => higherCopy,
                        _ => throw new InvalidOperationException(
                            "Unknown provenance mutation."),
                    };
                    Assert.True(AcceptedStatePhysicalCopyCodec.TryEncode(
                        higherCopy,
                        out higherPayload));
                }

                authenticated.Add(Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Candidate,
                    objectId: "20",
                    objectIdentity: new string('b', 64),
                    predecessorIdentity: null,
                    higherPayload));
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
            var selectedHead = Head(
                names,
                baseScopeDigest,
                authorizeStaleCandidate && staleCandidate is not null
                    ? [LineageHeadCodec.Evidence(staleCandidate.Metadata)]
                    : []);
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

        internal static SelectorFixture BoundedTail(
            bool currentPredecessorMismatch = false,
            bool includeOlderReceipt = false,
            bool olderReceiptLogicalMismatch = false)
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
                    currentPredecessorMismatch
                        ? new string('7', 64)
                        : firstGeneration.StateEnvelopeSha256,
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
            if (includeOlderReceipt)
            {
                _ = AcceptedStateTestData.Receipt(
                    olderReceiptLogicalMismatch
                        ? new string('8', 64)
                        : missingLogicalIdentity,
                    originalCandidateIdentity: new string('9', 64),
                    bytes: out var olderReceiptBytes);
                authenticated.Add(Object(
                    names,
                    baseScopeDigest,
                    StateObjectClass.Acceptance,
                    "29",
                    missingReceiptIdentity,
                    predecessorIdentity: null,
                    olderReceiptBytes));
            }

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
            byte[] payload,
            string? epoch = null,
            string? sessionId = null) =>
            new(
                Metadata(
                    names[objectClass],
                    objectId,
                    AcceptedStateTestData.LogicalExpiresAtUnixSeconds),
                new StateControlHeaderV1(
                    baseScopeDigest,
                    epoch ?? AcceptedStateTestData.Epoch,
                    sessionId ?? AcceptedStateTestData.SessionId,
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
            string baseScopeDigest,
            ImmutableArray<LineageArtifactEvidence>? authorizedHistorical =
                null)
        {
            var historical = authorizedHistorical ?? [];
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
            var isReset = !historical.IsEmpty;
            var head = new LineageHeadV1(
                isReset
                    ? LineageTransitionKind.Reset
                    : LineageTransitionKind.Initial,
                Ordinal: isReset ? 1UL : 0UL,
                LineageTestData.Reviewed(),
                PreviousEpoch: isReset ? new string('0', 64) : null,
                PreviousHeadIdentity: isReset ? new string('1', 64) : null,
                TransitionEvidenceIdentity: isReset
                    ? new string('2', 64)
                    : null,
                ExpiryBoundaryUnixSeconds: null,
                PhysicalPredecessors: isReset
                    ? [LineageHeadCodec.Evidence(metadata)]
                    : [],
                PhysicalSuperseded:
                    ImmutableArray<LineageArtifactEvidence>.Empty,
                Superseded: historical,
                CompletedCleanup:
                    ImmutableArray<LineageArtifactEvidence>.Empty,
                ResetAuthorityRunIdentity: isReset
                    ? "workflow-run-42"
                    : null,
                ResetAuthorityRunAttempt: isReset ? 1 : null);
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
