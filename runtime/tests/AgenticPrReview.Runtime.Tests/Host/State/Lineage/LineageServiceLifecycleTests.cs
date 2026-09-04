using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageServiceLifecycleTests
{
    [Fact]
    public async Task NewlyUploadedInitialHeadWaitsForDelayedVisibility()
    {
        using var context = LineageTestData.Context();
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            HideNextUploadedObjectForNextLists = 2,
            HideUploadedObjectOnUploadCall = 1,
        };
        var diagnostics = new RecordingStateReconciliationDiagnosticSink();

        var result = await new LineageService(
                store,
                context.Time,
                diagnostics)
            .ResolveAsync(
                context.Context,
                LineageTestData.Request(context.Access),
                CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        result.Context!.Dispose();
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            context.Time.ScheduledDelays);
        Assert.Empty(diagnostics.Diagnostics);
    }

    [Fact]
    public async Task MissingUploadedInitialHeadEmitsOneBoundedDiagnostic()
    {
        using var context = LineageTestData.Context();
        var store = new ScriptedLocatorStore
        {
            FilterListsByName = true,
            HideNextUploadedObjectForNextLists = 3,
            HideUploadedObjectOnUploadCall = 1,
        };
        var diagnostics = new RecordingStateReconciliationDiagnosticSink();

        var result = await new LineageService(
                store,
                context.Time,
                diagnostics)
            .ResolveAsync(
                context.Context,
                LineageTestData.Request(context.Access),
                CancellationToken.None);

        Assert.Equal(LineageCodes.Unavailable, result.Code);
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            context.Time.ScheduledDelays);
        var diagnostic = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(StateReconciliationOwner.LineageHead, diagnostic.Owner);
        Assert.Equal(StateReconciliationOutcome.Committed, diagnostic.Outcome);
        Assert.Equal(StateReconciliationExactReadBack.Matched,
            diagnostic.ExactReadBack);
        Assert.Equal(3, diagnostic.Observations);
        Assert.Equal(StateReconciliationTerminal.TargetAbsent,
            diagnostic.Terminal);
        Assert.Equal(2, diagnostic.ScheduleIndex);
    }

    [Fact]
    public async Task LocalStoreSupportsInitialNextProcessResetAndExpiry()
    {
        await WithRootAsync(async root =>
        {
            SelectedLineageSnapshot initialSnapshot;
            using (var first = LineageTestData.Context())
            {
                var service = new LineageService(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: first.Time),
                    first.Time);
                var initialized = await service.ResolveAsync(
                    first.Context,
                    LineageTestData.Request(first.Access),
                    CancellationToken.None);
                Assert.True(initialized.Succeeded, initialized.Code);
                using (initialized.Context)
                {
                    Assert.True(initialized.Context!.TryGetSnapshot(
                        first.Access,
                        out var snapshot));
                    initialSnapshot = snapshot!;
                    Assert.Equal(
                        LineageTransitionKind.Initial,
                        snapshot!.Transition);
                    Assert.Matches("^[a-f0-9]{64}$", snapshot.SessionId);
                }
            }

            Assert.Single(Directory.GetFiles(root, "*.aprobject"));

            SelectedLineageSnapshot resetSnapshot;
            using (var nextProcess = LineageTestData.Context())
            {
                var store = new LocalRestrictedStateStore(
                    root,
                    timeProvider: nextProcess.Time);
                var service = new LineageService(store, nextProcess.Time);
                var discovered = await service.ResolveAsync(
                    nextProcess.Context,
                    LineageTestData.Request(nextProcess.Access),
                    CancellationToken.None);
                Assert.True(discovered.Succeeded, discovered.Code);
                using (discovered.Context)
                {
                    Assert.True(discovered.Context!.TryGetSnapshot(
                        nextProcess.Access,
                        out var snapshot));
                    Assert.Equal(initialSnapshot, snapshot);
                }

                await UploadOpaqueCleanupAsync(
                    store,
                    nextProcess,
                    initialSnapshot);

                var resetEvidence = LineageTestData.Reset(
                    nextProcess.Access,
                    initialSnapshot.LineageHeadIdentity,
                    new string('e', 64));
                var reset = await service.ResolveAsync(
                    nextProcess.Context,
                    LineageTestData.Request(
                        nextProcess.Access,
                        resetEvidence,
                        LineageTestData.Reviewed('3', '4')),
                    CancellationToken.None);
                Assert.True(reset.Succeeded, reset.Code);
                using (reset.Context)
                {
                    Assert.True(reset.Context!.TryGetSnapshot(
                        nextProcess.Access,
                        out var snapshot));
                    resetSnapshot = snapshot!;
                    Assert.Equal(
                        LineageTransitionKind.Reset,
                        resetSnapshot.Transition);
                    Assert.NotEqual(initialSnapshot.Epoch, resetSnapshot.Epoch);
                    Assert.NotEqual(
                        initialSnapshot.SessionId,
                        resetSnapshot.SessionId);
                }

                Assert.Equal(2, Directory.GetFiles(root, "*.aprobject").Length);

                var replayedReset = await service.ResolveAsync(
                    nextProcess.Context,
                    LineageTestData.Request(
                        nextProcess.Access,
                        resetEvidence,
                        LineageTestData.Reviewed('5', '6')),
                    CancellationToken.None);
                Assert.True(replayedReset.Succeeded, replayedReset.Code);
                using (replayedReset.Context)
                {
                    Assert.True(replayedReset.Context!.TryGetSnapshot(
                        nextProcess.Access,
                        out var replayedSnapshot));
                    Assert.Equal(resetSnapshot, replayedSnapshot);
                }

                Assert.Equal(2, Directory.GetFiles(root, "*.aprobject").Length);

                await UploadExpiredAcceptanceAsync(
                    store,
                    nextProcess,
                    resetSnapshot);
                var expired = await service.ResolveAsync(
                    nextProcess.Context,
                    LineageTestData.Request(
                        nextProcess.Access,
                        reviewed: LineageTestData.Reviewed('5', '6')),
                    CancellationToken.None);
                Assert.True(expired.Succeeded, expired.Code);
                using (expired.Context)
                {
                    Assert.True(expired.Context!.TryGetSnapshot(
                        nextProcess.Access,
                        out var snapshot));
                    Assert.Equal(LineageTransitionKind.Expiry, snapshot!.Transition);
                    Assert.NotEqual(resetSnapshot.Epoch, snapshot.Epoch);
                    Assert.NotEqual(resetSnapshot.SessionId, snapshot.SessionId);
                }

                Assert.Equal(2, Directory.GetFiles(root, "*.aprobject").Length);
            }

            using var finalProcess = LineageTestData.Context();
            var final = await new LineageService(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: finalProcess.Time),
                    finalProcess.Time)
                .ResolveAsync(
                    finalProcess.Context,
                    LineageTestData.Request(
                        finalProcess.Access,
                        reviewed: LineageTestData.Reviewed('5', '6')),
                    CancellationToken.None);
            Assert.True(final.Succeeded, final.Code);
            using (final.Context)
            {
                Assert.True(final.Context!.TryGetSnapshot(
                    finalProcess.Access,
                    out var finalSnapshot));
                Assert.Equal(
                    LineageTransitionKind.Expiry,
                    finalSnapshot!.Transition);
            }
        });
    }

    [Fact]
    public async Task UnauthorizedResetDoesNotMutateState()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initial = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initial.Succeeded, initial.Code);
            using (initial.Context)
            {
                Assert.True(initial.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                var forged = LineageTestData.Reset(
                    lease.Access,
                    snapshot!.LineageHeadIdentity,
                    new string('e', 64),
                    producingRunIdentity: "other-run");
                var rejected = await service.ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, forged),
                    CancellationToken.None);
                Assert.False(rejected.Succeeded);
                Assert.Equal(LineageCodes.AccessDenied, rejected.Code);

                var wrongRoute = LineageTestData.Reset(
                    lease.Access,
                    snapshot!.LineageHeadIdentity,
                    new string('f', 64),
                    trustedWorkflowRoute: "pull_request");
                var routeRejected = await service.ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access, wrongRoute),
                    CancellationToken.None);
                Assert.False(routeRejected.Succeeded);
                Assert.Equal(
                    LineageCodes.AccessDenied,
                    routeRejected.Code);
            }

            Assert.Single(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    [Fact]
    public async Task PreviousKeyHeadRefreshesUnderCurrentKeyWithoutIdentityChange()
    {
        await WithRootAsync(async root =>
        {
            SelectedLineageSnapshot initialSnapshot;
            using (var initialLease = LineageTestData.Context())
            {
                var initial = await new LineageService(
                        new LocalRestrictedStateStore(
                            root,
                            timeProvider: initialLease.Time),
                        initialLease.Time)
                    .ResolveAsync(
                        initialLease.Context,
                        LineageTestData.Request(initialLease.Access),
                        CancellationToken.None);
                Assert.True(initial.Succeeded, initial.Code);
                using (initial.Context)
                {
                    Assert.True(initial.Context!.TryGetSnapshot(
                        initialLease.Access,
                        out var snapshot));
                    initialSnapshot = snapshot!;
                }
            }

            var before = Assert.Single(
                Directory.GetFiles(root, "*.aprobject"));
            var nextKey = Convert.ToBase64String(
                Enumerable.Repeat((byte)0xc3, 32).ToArray());
            using var rotatedLease = LineageTestData.Context(
                previousBase64: LocatorTestData.CurrentBase64,
                currentBase64: nextKey);
            var refreshed = await new LineageService(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: rotatedLease.Time),
                    rotatedLease.Time)
                .ResolveAsync(
                    rotatedLease.Context,
                    LineageTestData.Request(rotatedLease.Access),
                    CancellationToken.None);
            Assert.True(refreshed.Succeeded, refreshed.Code);
            using (refreshed.Context)
            {
                Assert.True(refreshed.Context!.TryGetSnapshot(
                    rotatedLease.Access,
                    out var snapshot));
                Assert.Equal(initialSnapshot, snapshot);
            }

            var after = Assert.Single(
                Directory.GetFiles(root, "*.aprobject"));
            Assert.NotEqual(before, after);
        });
    }

    [Fact]
    public async Task FreshProcessCleansUnknownPreviousKeyRefreshSource()
    {
        await WithRootAsync(async root =>
        {
            SelectedLineageSnapshot initialSnapshot;
            OpaqueStoreObjectMetadata oldPhysical;
            using (var initialLease = LineageTestData.Context())
            {
                var store = new LocalRestrictedStateStore(
                    root,
                    timeProvider: initialLease.Time);
                var initial = await new LineageService(store, initialLease.Time)
                    .ResolveAsync(
                        initialLease.Context,
                        LineageTestData.Request(initialLease.Access),
                        CancellationToken.None);
                Assert.True(initial.Succeeded, initial.Code);
                using (initial.Context)
                {
                    Assert.True(initial.Context!.TryGetSnapshot(
                        initialLease.Access,
                        out var snapshot));
                    initialSnapshot = snapshot!;
                }

                var lineageName = ResolveName(
                    initialLease,
                    StateObjectClass.LineageHead);
                var listed = await store.ListExactAsync(
                    new OpaqueStoreListRequest(
                        lineageName,
                        LineageFormat.MaximumPhysicalPerClass),
                    CancellationToken.None);
                Assert.True(listed.Succeeded);
                var reference = Assert.Single(listed.Objects);
                var metadata = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(reference),
                    CancellationToken.None);
                Assert.True(metadata.Succeeded);
                oldPhysical = metadata.Metadata!;
            }

            var nextKey = Convert.ToBase64String(
                Enumerable.Repeat((byte)0xc3, 32).ToArray());
            using (var rotatedLease = LineageTestData.Context(
                previousBase64: LocatorTestData.CurrentBase64,
                currentBase64: nextKey))
            {
                var inner = new LocalRestrictedStateStore(
                    root,
                    timeProvider: rotatedLease.Time);
                var interrupted = await new LineageService(
                        new TargetDeleteOutcomeUnknownStore(
                            inner,
                            oldPhysical),
                        rotatedLease.Time)
                    .ResolveAsync(
                        rotatedLease.Context,
                        LineageTestData.Request(rotatedLease.Access),
                        CancellationToken.None);
                Assert.False(interrupted.Succeeded);
                Assert.Equal(LineageCodes.CleanupFailed, interrupted.Code);
                Assert.Null(interrupted.Context);
                Assert.Equal(2, Directory.GetFiles(
                    root,
                    "*.aprobject").Length);
            }

            using var freshLease = LineageTestData.Context(
                currentBase64: nextKey);
            var recovered = await new LineageService(
                    new LocalRestrictedStateStore(
                        root,
                        timeProvider: freshLease.Time),
                    freshLease.Time)
                .ResolveAsync(
                    freshLease.Context,
                    LineageTestData.Request(freshLease.Access),
                    CancellationToken.None);
            Assert.True(recovered.Succeeded, recovered.Code);
            using (recovered.Context)
            {
                Assert.True(recovered.Context!.TryGetSnapshot(
                    freshLease.Access,
                    out var snapshot));
                Assert.Equal(initialSnapshot, snapshot);
            }

            Assert.Single(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    [Fact]
    public async Task RetentionRefreshPreservesEpochSessionAndLogicalHead()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initial = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initial.Succeeded, initial.Code);
            SelectedLineageSnapshot initialSnapshot;
            using (initial.Context)
            {
                Assert.True(initial.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                initialSnapshot = snapshot!;
            }

            var before = Assert.Single(
                Directory.GetFiles(root, "*.aprobject"));
            lease.Time.UnixSeconds += 24 * 60 * 60;
            var extendedLogicalExpiry =
                LineageTestData.LogicalExpiry + 24 * 60 * 60;
            var refreshRequest = LineageTestData.Request(lease.Access) with
            {
                RequiredLogicalExpiresAtUnixSeconds = extendedLogicalExpiry,
            };
            var refreshed = await service.ResolveAsync(
                lease.Context,
                refreshRequest,
                CancellationToken.None);
            Assert.True(refreshed.Succeeded, refreshed.Code);
            using (refreshed.Context)
            {
                Assert.True(refreshed.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(initialSnapshot, snapshot);
            }

            var after = Assert.Single(
                Directory.GetFiles(root, "*.aprobject"));
            Assert.NotEqual(before, after);

            Assert.True(LineageBaseScopeCodec.TryDigest(
                LineageTestData.Scope(),
                out var baseScopeDigest));
            var inventory = await new ScopedStateInventory(store).ReadAsync(
                lease.Context,
                lease.Access,
                LineageTestData.Scope(),
                baseScopeDigest,
                CancellationToken.None);
            Assert.True(inventory.Succeeded, inventory.Code);
            try
            {
                var head = Assert.Single(inventory.Snapshot!.Authenticated
                    .Where(item => item.Header.ObjectClass ==
                        StateObjectClass.LineageHead));
                Assert.Equal(
                    extendedLogicalExpiry,
                    head.Header.LogicalExpiresAtUnixSeconds);
            }
            finally
            {
                ScopedStateInventory.Clear(inventory.Snapshot);
            }
        });
    }

    [Fact]
    public async Task RepeatedRefreshCompactsAbsentPhysicalEvidence()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var initialized = await new LineageService(store, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            initialized.Context!.Dispose();

            for (var refresh = 1; refresh <= 12; refresh++)
            {
                var request = LineageTestData.Request(lease.Access) with
                {
                    RequiredLogicalExpiresAtUnixSeconds =
                        LineageTestData.LogicalExpiry + refresh * 60,
                };
                var result = await new LineageService(store, lease.Time)
                    .ResolveAsync(
                        lease.Context,
                        request,
                        CancellationToken.None);
                Assert.True(result.Succeeded, result.Code);
                result.Context!.Dispose();
            }

            Assert.True(LineageBaseScopeCodec.TryDigest(
                LineageTestData.Scope(),
                out var baseScopeDigest));
            var inventory = await new ScopedStateInventory(store).ReadAsync(
                lease.Context,
                lease.Access,
                LineageTestData.Scope(),
                baseScopeDigest,
                CancellationToken.None);
            try
            {
                Assert.True(inventory.Succeeded, inventory.Code);
                var physicalHead = Assert.Single(
                    inventory.Snapshot!.Authenticated.Where(item =>
                        item.Header.ObjectClass ==
                            StateObjectClass.LineageHead));
                Assert.True(LineageHeadCodec.TryDecode(
                    physicalHead.Payload,
                    out var head));
                Assert.NotNull(head);
                Assert.Single(head!.PhysicalSuperseded);
                Assert.Empty(head.Superseded);
                Assert.Empty(head.CompletedCleanup);
            }
            finally
            {
                ScopedStateInventory.Clear(inventory.Snapshot);
            }
        });
    }

    [Fact]
    public async Task ResetHeadRefreshPreservesTheProducingRunAuthority()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            Assert.True(initialized.Context!.TryGetSnapshot(
                lease.Access,
                out var initialSnapshot));
            initialized.Context.Dispose();

            var resetEvidence = LineageTestData.Reset(
                lease.Access,
                initialSnapshot!.LineageHeadIdentity,
                new string('e', 64));
            var reset = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(
                    lease.Access,
                    resetEvidence,
                    LineageTestData.Reviewed('3', '4')),
                CancellationToken.None);
            Assert.True(reset.Succeeded, reset.Code);
            Assert.True(reset.Context!.TryGetSnapshot(
                lease.Access,
                out var resetSnapshot));
            reset.Context.Dispose();

            var nextRun = LineageTestData.Request(
                lease.Access,
                reviewed: LineageTestData.Reviewed('5', '6')) with
            {
                ProducingRunIdentity = "run-two",
                ProducingRunAttempt = 2,
                RequiredLogicalExpiresAtUnixSeconds =
                    LineageTestData.LogicalExpiry + 3_600,
            };
            var refreshed = await new LineageService(store, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    nextRun,
                    CancellationToken.None);

            Assert.True(refreshed.Succeeded, refreshed.Code);
            Assert.True(refreshed.Context!.TryGetSnapshot(
                lease.Access,
                out var refreshedSnapshot));
            Assert.Equal(resetSnapshot, refreshedSnapshot);
            refreshed.Context.Dispose();
        });
    }

    [Fact]
    public async Task ExpiryUsesTheUniqueCurrentAcceptance()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var expiredPredecessor = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.Now - 60,
                payloadMarker: 1);
            _ = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                expiredPredecessor.ObjectIdentity,
                logicalExpiry: LineageTestData.Now + 60,
                payloadMarker: 2);

            var beforeBoundary = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(beforeBoundary.Succeeded, beforeBoundary.Code);
            using (beforeBoundary.Context)
            {
                Assert.True(beforeBoundary.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(selected, snapshot);
            }

            lease.Time.UnixSeconds = LineageTestData.Now + 61;
            var afterBoundary = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(
                    lease.Access,
                    reviewed: LineageTestData.Reviewed('3', '4')),
                CancellationToken.None);
            Assert.True(afterBoundary.Succeeded, afterBoundary.Code);
            using (afterBoundary.Context)
            {
                Assert.True(afterBoundary.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                Assert.Equal(LineageTransitionKind.Expiry, snapshot!.Transition);
                Assert.NotEqual(selected.Epoch, snapshot.Epoch);
            }
        });
    }

    [Fact]
    public async Task AuthenticatedHeadWithNonCanonicalEpochAndSessionIsRejected()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            Assert.True(LineageBaseScopeCodec.TryDigest(
                LineageTestData.Scope(),
                out var baseScopeDigest));
            Assert.True(LineageBaseScopeCodec.TryEncode(
                LineageTestData.Scope(),
                out var canonicalScope));
            try
            {
                Assert.True(lease.Context.TryDeriveOpaqueName(
                    lease.Access,
                    StateObjectClasses.ToWireName(
                        StateObjectClass.LineageHead),
                    canonicalScope,
                    out var name));
                Assert.NotNull(name);
                var head = new LineageHeadV1(
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
                Assert.True(LineageHeadCodec.TryEncode(head, out var payload));
                try
                {
                    var draft = new StateControlHeaderDraft(
                        baseScopeDigest,
                        new string('e', 64),
                        new string('f', 64),
                        StateObjectClass.LineageHead,
                        PredecessorIdentity: null,
                        SuccessorIdentity: null,
                        "workflow-run-42",
                        ProducingRunAttempt: 1,
                        LineageTestData.Now,
                        LineageTestData.LogicalExpiry,
                        LineageTestData.Now + 8 * 24 * 60 * 60);
                    Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                        lease.Context,
                        lease.Access,
                        name!,
                        draft,
                        payload,
                        out var envelope,
                        out _,
                        out var code), code);
                    try
                    {
                        var uploaded = await new ScopedStateUploadProtocol(
                                store)
                            .UploadAndReadBackAsync(
                                name!,
                                envelope,
                                draft.RequiredPlatformExpiresAtUnixSeconds,
                                CancellationToken.None);
                        Assert.True(uploaded.Succeeded, uploaded.Code);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(envelope);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(canonicalScope);
            }

            var result = await new LineageService(store, lease.Time)
                .ResolveAsync(
                    lease.Context,
                    LineageTestData.Request(lease.Access),
                    CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.AuthenticationFailed, result.Code);
            Assert.Null(result.Context);
        });
    }

    [Fact]
    public async Task SiblingAcceptanceSuccessorsConflict()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            _ = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.Now + 60,
                payloadMarker: 1);
            _ = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.Now + 120,
                payloadMarker: 2);

            var result = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.Conflict, result.Code);
            Assert.Null(result.Context);
        });
    }

    [Fact]
    public async Task UnderRetainedAcceptanceSuccessorDoesNotRollBack()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var predecessor = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 1);
            _ = await UploadAcceptanceAsync(
                store,
                lease,
                selected,
                predecessor.ObjectIdentity,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 2,
                underRetained: true);

            var acceptanceName = ResolveName(
                lease,
                StateObjectClass.Acceptance);
            var before = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    acceptanceName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(before.Succeeded);
            Assert.Equal(2, before.Objects.Length);

            var result = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.RetentionFailed, result.Code);
            Assert.Null(result.Context);
            var after = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    acceptanceName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(after.Succeeded);
            Assert.Equal(before.Objects
                    .OrderBy(item => item.ObjectId.Value,
                        StringComparer.Ordinal),
                after.Objects.OrderBy(item => item.ObjectId.Value,
                    StringComparer.Ordinal));
        });
    }

    [Theory]
    [InlineData("candidate")]
    [InlineData("publication_intent")]
    public async Task UnderRetainedLaterOwnedSuccessorDoesNotRollBack(
        string objectClassName)
    {
        await WithRootAsync(async root =>
        {
            var objectClass = objectClassName switch
            {
                "candidate" => StateObjectClass.Candidate,
                "publication_intent" => StateObjectClass.PublicationIntent,
                _ => throw new InvalidOperationException(objectClassName)
            };
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var predecessor = await UploadStateObjectAsync(
                store,
                lease,
                selected,
                StateObjectClass.Candidate,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 1);
            _ = await UploadStateObjectAsync(
                store,
                lease,
                selected,
                objectClass,
                predecessor.Header.ObjectIdentity,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 2,
                underRetained: true);

            var candidateName = ResolveName(
                lease,
                StateObjectClass.Candidate);
            var successorName = ResolveName(lease, objectClass);
            var candidatesBefore = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    candidateName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            var successorsBefore = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    successorName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(candidatesBefore.Succeeded);
            Assert.True(successorsBefore.Succeeded);

            var result = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);

            Assert.False(result.Succeeded);
            Assert.Equal(LineageCodes.RetentionFailed, result.Code);
            Assert.Null(result.Context);
            var candidatesAfter = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    candidateName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            var successorsAfter = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    successorName,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(candidatesAfter.Succeeded);
            Assert.True(successorsAfter.Succeeded);
            Assert.Equal(candidatesBefore.Objects
                    .OrderBy(item => item.ObjectId.Value,
                        StringComparer.Ordinal),
                candidatesAfter.Objects.OrderBy(
                    item => item.ObjectId.Value,
                    StringComparer.Ordinal));
            Assert.Equal(successorsBefore.Objects
                    .OrderBy(item => item.ObjectId.Value,
                        StringComparer.Ordinal),
                successorsAfter.Objects.OrderBy(
                    item => item.ObjectId.Value,
                    StringComparer.Ordinal));
        });
    }

    [Fact]
    public async Task UnderRetainedEquivalentLaterOwnedCopyCanBeCleaned()
    {
        await WithRootAsync(async root =>
        {
            using var lease = LineageTestData.Context();
            var store = new LocalRestrictedStateStore(
                root,
                timeProvider: lease.Time);
            var service = new LineageService(store, lease.Time);
            var initialized = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);
            Assert.True(initialized.Succeeded, initialized.Code);
            SelectedLineageSnapshot selected;
            using (initialized.Context)
            {
                Assert.True(initialized.Context!.TryGetSnapshot(
                    lease.Access,
                    out var snapshot));
                selected = snapshot!;
            }

            var retained = await UploadStateObjectAsync(
                store,
                lease,
                selected,
                StateObjectClass.Candidate,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 1);
            var underRetained = await UploadStateObjectAsync(
                store,
                lease,
                selected,
                StateObjectClass.Candidate,
                predecessorIdentity: null,
                logicalExpiry: LineageTestData.LogicalExpiry,
                payloadMarker: 1,
                underRetained: true);
            Assert.Equal(
                retained.Header.ObjectIdentity,
                underRetained.Header.ObjectIdentity);
            Assert.NotEqual(
                retained.Metadata.Reference,
                underRetained.Metadata.Reference);

            var result = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
                CancellationToken.None);

            Assert.True(result.Succeeded, result.Code);
            result.Context!.Dispose();
            var name = ResolveName(lease, StateObjectClass.Candidate);
            var after = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    name,
                    LineageFormat.MaximumPhysicalPerClass),
                CancellationToken.None);
            Assert.True(after.Succeeded);
            Assert.Equal(
                retained.Metadata.Reference,
                Assert.Single(after.Objects));
        });
    }

    private static async Task UploadExpiredAcceptanceAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected)
    {
        _ = await UploadAcceptanceAsync(
            store,
            lease,
            selected,
            predecessorIdentity: null,
            logicalExpiry: LineageTestData.Now - 60,
            payloadMarker: 9);
    }

    private static OpaqueStoreName ResolveName(
        LineageTestData.ContextLease lease,
        StateObjectClass objectClass)
    {
        Assert.True(LineageBaseScopeCodec.TryEncode(
            LineageTestData.Scope(),
            out var canonicalScope));
        try
        {
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(objectClass),
                canonicalScope,
                out var name));
            return name!;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    private static async Task<StateControlHeaderV1> UploadAcceptanceAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        string? predecessorIdentity,
        long logicalExpiry,
        byte payloadMarker,
        bool underRetained = false)
    {
        var uploaded = await UploadStateObjectAsync(
            store,
            lease,
            selected,
            StateObjectClass.Acceptance,
            predecessorIdentity,
            logicalExpiry,
            payloadMarker,
            underRetained);
        return uploaded.Header;
    }

    private static async Task<(
        StateControlHeaderV1 Header,
        OpaqueStoreObjectMetadata Metadata)> UploadStateObjectAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected,
        StateObjectClass objectClass,
        string? predecessorIdentity,
        long logicalExpiry,
        byte payloadMarker,
        bool underRetained = false)
    {
        Assert.True(LineageBaseScopeCodec.TryEncode(
            LineageTestData.Scope(),
            out var canonicalScope));
        try
        {
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(objectClass),
                canonicalScope,
                out var name));
            Assert.NotNull(name);
            var draft = new StateControlHeaderDraft(
                selected.BaseScopeDigest,
                selected.Epoch,
                selected.SessionId,
                objectClass,
                predecessorIdentity,
                SuccessorIdentity: null,
                "state-generation-run",
                ProducingRunAttempt: 1,
                LineageTestData.Now - 60,
                logicalExpiry,
                LineageTestData.Now + 8 * 24 * 60 * 60);
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name!,
                draft,
                [payloadMarker],
                out var envelope,
                out var header,
                out var code), code);
            try
            {
                OpaqueStoreObjectMetadata metadata;
                if (underRetained)
                {
                    var uploaded = await store.UploadImmutableAsync(
                        new OpaqueStoreUploadRequest(
                            name!,
                            new OpaqueStoreCorrelationId(
                                LineageCryptography.CorrelationId(envelope)),
                            envelope,
                            new OpaqueStoreEncryptedObjectDigest(
                                OpaqueStoreHash.Sha256(envelope)),
                            draft.RequiredPlatformExpiresAtUnixSeconds -
                                3_601),
                        CancellationToken.None);
                    Assert.True(uploaded.Succeeded);
                    Assert.NotNull(uploaded.Metadata);
                    Assert.True(uploaded.Metadata!.ExpiresAtUnixSeconds <
                        draft.RequiredPlatformExpiresAtUnixSeconds);
                    metadata = uploaded.Metadata;
                }
                else
                {
                    var uploaded = await new ScopedStateUploadProtocol(store)
                        .UploadAndReadBackAsync(
                            name!,
                            envelope,
                            draft.RequiredPlatformExpiresAtUnixSeconds,
                            CancellationToken.None);
                    Assert.True(uploaded.Succeeded, uploaded.Code);
                    Assert.NotNull(uploaded.Metadata);
                    metadata = uploaded.Metadata!;
                }

                return (header!, metadata);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    private static async Task UploadOpaqueCleanupAsync(
        IRestrictedStateStore store,
        LineageTestData.ContextLease lease,
        SelectedLineageSnapshot selected)
    {
        Assert.True(LineageBaseScopeCodec.TryEncode(
            LineageTestData.Scope(),
            out var canonicalScope));
        try
        {
            Assert.True(lease.Context.TryDeriveOpaqueName(
                lease.Access,
                StateObjectClasses.ToWireName(StateObjectClass.Cleanup),
                canonicalScope,
                out var name));
            Assert.NotNull(name);
            var draft = new StateControlHeaderDraft(
                selected.BaseScopeDigest,
                selected.Epoch,
                selected.SessionId,
                StateObjectClass.Cleanup,
                PredecessorIdentity: selected.LineageHeadIdentity,
                SuccessorIdentity: null,
                "s6-cleanup-run",
                ProducingRunAttempt: 1,
                LineageTestData.Now,
                LineageTestData.LogicalExpiry,
                LineageTestData.Now + 8 * 24 * 60 * 60);
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name!,
                draft,
                [0xde, 0xad, 0xbe, 0xef],
                out var envelope,
                out _,
                out var code), code);
            try
            {
                var uploaded = await new ScopedStateUploadProtocol(store)
                    .UploadAndReadBackAsync(
                        name!,
                        envelope,
                        draft.RequiredPlatformExpiresAtUnixSeconds,
                        CancellationToken.None);
                Assert.True(uploaded.Succeeded, uploaded.Code);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(envelope);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalScope);
        }
    }

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-lineage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            await action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TargetDeleteOutcomeUnknownStore(
        IRestrictedStateStore inner,
        OpaqueStoreObjectMetadata target) : IRestrictedStateStore
    {
        public Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken) =>
            inner.ListExactAsync(request, cancellationToken);

        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken) =>
            inner.ReadMetadataAsync(request, cancellationToken);

        public Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken) =>
            inner.DownloadAsync(request, cancellationToken);

        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken) =>
            inner.UploadImmutableAsync(request, cancellationToken);

        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken) =>
            inner.ReadBackExactAsync(request, cancellationToken);

        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken) =>
            request.Expected == target
                ? Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown))
                : inner.DeleteExactAsync(request, cancellationToken);
    }
}
