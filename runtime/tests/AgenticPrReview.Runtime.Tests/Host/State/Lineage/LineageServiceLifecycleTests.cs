using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageServiceLifecycleTests
{
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
            var refreshed = await service.ResolveAsync(
                lease.Context,
                LineageTestData.Request(lease.Access),
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
        });
    }

    private static async Task UploadExpiredAcceptanceAsync(
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
                StateObjectClasses.ToWireName(StateObjectClass.Acceptance),
                canonicalScope,
                out var name));
            Assert.NotNull(name);
            var draft = new StateControlHeaderDraft(
                selected.BaseScopeDigest,
                selected.Epoch,
                selected.SessionId,
                StateObjectClass.Acceptance,
                PredecessorIdentity: selected.LineageHeadIdentity,
                SuccessorIdentity: null,
                "state-generation-run",
                ProducingRunAttempt: 1,
                LineageTestData.Now - 60,
                LineageTestData.Now,
                LineageTestData.Now + 8 * 24 * 60 * 60);
            Assert.True(StateControlEnvelopeV1Codec.TryEncrypt(
                lease.Context,
                lease.Access,
                name!,
                draft,
                [9, 8, 7, 6],
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
}
