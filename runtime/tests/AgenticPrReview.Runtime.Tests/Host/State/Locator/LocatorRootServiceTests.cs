using System.Collections.Immutable;
using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocatorRootServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task NewlyUploadedLocatorWaitsForDelayedVisibility(
        int hiddenObservations)
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            HideNextUploadedObjectForNextLists = hiddenObservations,
            HideUploadedObjectOnUploadCall = 1,
        };
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var diagnostics = new RecordingStateReconciliationDiagnosticSink();

        var result = await new LocatorRootService(
                store,
                keys,
                time,
                diagnostics)
            .ResolveAsync(access, 0, CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        result.Context!.Dispose();
        TimeSpan[] expectedDelays = hiddenObservations switch
        {
            0 => [],
            1 => [TimeSpan.FromSeconds(5)],
            _ => [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
        };
        Assert.Equal(expectedDelays, time.ScheduledDelays);
        Assert.Equal(1, store.UploadCalls);
        Assert.Empty(diagnostics.Diagnostics);
    }

    [Fact]
    public async Task MissingUploadedLocatorEmitsOneBoundedDiagnostic()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            HideNextUploadedObjectForNextLists = 3,
            HideUploadedObjectOnUploadCall = 1,
        };
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var diagnostics = new RecordingStateReconciliationDiagnosticSink();

        var result = await new LocatorRootService(
                store,
                keys,
                time,
                diagnostics)
            .ResolveAsync(access, 0, CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, result.Code);
        Assert.Equal(
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)],
            time.ScheduledDelays);
        var diagnostic = Assert.Single(diagnostics.Diagnostics);
        Assert.Equal(StateReconciliationOwner.LocatorRoot, diagnostic.Owner);
        Assert.Equal(StateReconciliationOutcome.Committed, diagnostic.Outcome);
        Assert.Equal(StateReconciliationExactReadBack.Matched,
            diagnostic.ExactReadBack);
        Assert.Equal(3, diagnostic.Observations);
        Assert.Equal(StateReconciliationTerminal.TargetAbsent,
            diagnostic.Terminal);
        Assert.Equal(2, diagnostic.ScheduleIndex);
    }

    [Theory]
    [InlineData((int)OpaqueStoreFailure.Incomplete,
        (int)StateReconciliationTerminal.Incomplete)]
    [InlineData((int)OpaqueStoreFailure.Cancelled,
        (int)StateReconciliationTerminal.Cancelled)]
    [InlineData((int)OpaqueStoreFailure.Conflict,
        (int)StateReconciliationTerminal.Conflict)]
    public async Task LocatorDiagnosticPreservesPostUploadFailure(
        int failureValue,
        int expectedTerminalValue)
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var failure = (OpaqueStoreFailure)failureValue;
        var store = new ScriptedLocatorStore();
        store.AfterUpload = (_, _) =>
        {
            if (failure == OpaqueStoreFailure.Incomplete)
            {
                store.ListComplete = false;
            }
            else
            {
                store.ListFailure = failure;
            }
        };
        var time = new FrozenLocatorTimeProvider(LocatorTestData.Now);
        var diagnostics = new RecordingStateReconciliationDiagnosticSink();

        var result = await new LocatorRootService(
                store,
                keys,
                time,
                diagnostics)
            .ResolveAsync(access, 0, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Empty(time.ScheduledDelays);
        Assert.Equal((StateReconciliationTerminal)expectedTerminalValue,
            Assert.Single(diagnostics.Diagnostics).Terminal);
    }

    [Fact]
    public async Task ConcurrentSameKeyInitializersConvergeToOneSentinel()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        using var store = new ConcurrentInitializationLocatorStore();
        var firstService = new LocatorRootService(
            store,
            keys,
            new FrozenLocatorTimeProvider(LocatorTestData.Now));
        var secondService = new LocatorRootService(
            store,
            keys,
            new FrozenLocatorTimeProvider(LocatorTestData.Now));

        var first = Task.Run(() => firstService.ResolveAsync(
            access,
            0,
            CancellationToken.None));
        var second = Task.Run(() => secondService.ResolveAsync(
            access,
            0,
            CancellationToken.None));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.True(result.Succeeded, result.Code));
        Assert.Single(store.Inner.Objects);
        Assert.Equal(2, store.Inner.UploadCalls);
    }

    [Fact]
    public async Task ConcurrentDifferentCurrentKeysProduceRootConflict()
    {
        using var access = LocatorTestData.Access();
        var other = Enumerable.Repeat((byte)0x9a, 32).ToArray();
        var otherBase64 = Convert.ToBase64String(other);
        using var firstKeys = LocatorTestData.KeyRing(
            access,
            previousBase64: otherBase64);
        using var secondKeys = LocatorTestData.KeyRing(
            access,
            previousBase64: LocatorTestData.CurrentBase64,
            currentBase64: otherBase64);
        using var store = new ConcurrentInitializationLocatorStore();

        var first = Task.Run(() => new LocatorRootService(
                store,
                firstKeys,
                new FrozenLocatorTimeProvider(LocatorTestData.Now))
            .ResolveAsync(access, 0, CancellationToken.None));
        var second = Task.Run(() => new LocatorRootService(
                store,
                secondKeys,
                new FrozenLocatorTimeProvider(LocatorTestData.Now))
            .ResolveAsync(access, 0, CancellationToken.None));
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result =>
            Assert.Equal(LocatorCodes.Conflict, result.Code));
        Assert.Equal(2, store.Inner.Objects.Length);
        CryptographicOperations.ZeroMemory(other);
    }

    [Fact]
    public async Task CompleteAbsenceInitializesAndCleanRetryIsReadOnly()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);

        var initialized = await service.ResolveAsync(
            access,
            dependentExpiresAtUnixSeconds: 0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        Assert.Single(store.Objects);
        Assert.Equal(1, store.UploadCalls);
        Assert.True(store.ReadBackCalls >= 2);

        store.ResetCounts();
        var retried = await service.ResolveAsync(
            access,
            dependentExpiresAtUnixSeconds: 0,
            CancellationToken.None);
        Assert.True(retried.Succeeded, retried.Code);
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(0, store.DeleteCalls);
        Assert.True(initialized.Context!.TryDeriveOpaqueName(
            access,
            "scope",
            [4, 5, 6],
            out var firstName));
        Assert.True(retried.Context!.TryDeriveOpaqueName(
            access,
            "scope",
            [4, 5, 6],
            out var secondName));
        Assert.Equal(firstName, secondName);
    }

    [Fact]
    public async Task WrongOrCancelledAuthorityPerformsNoStoreMutation()
    {
        using var access = LocatorTestData.Access();
        using var wrong = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);

        var denied = await service.ResolveAsync(
            wrong,
            0,
            CancellationToken.None);
        Assert.Equal(LocatorCodes.AccessDenied, denied.Code);
        Assert.Equal(0, store.ListCalls);
        Assert.Equal(0, store.UploadCalls);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var cancelled = await service.ResolveAsync(
            access,
            0,
            cancellation.Token);
        Assert.Equal(LocatorCodes.Unavailable, cancelled.Code);
        Assert.Equal(0, store.UploadCalls);
    }

    [Theory]
    [InlineData((int)OpaqueStoreFailure.Incomplete, 0)]
    [InlineData((int)OpaqueStoreFailure.Io, 1)]
    [InlineData((int)OpaqueStoreFailure.DigestMismatch, 2)]
    public async Task ReadGateFailuresNeverBootstrap(
        int failureValue,
        int gate)
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var failure = (OpaqueStoreFailure)failureValue;
        if (gate == 0)
        {
            store.ListFailure = failure;
        }
        else
        {
            Assert.True((await Service(store, keys).ResolveAsync(
                access,
                0,
                CancellationToken.None)).Succeeded);
            store.ResetCounts();
            if (gate == 1)
            {
                store.MetadataFailure = failure;
            }
            else
            {
                store.DownloadFailure = failure;
            }
        }

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(result.Succeeded);
        Assert.Equal(0, store.UploadCalls);
    }

    [Fact]
    public async Task NonCommittedUploadFailsAndFreshRetryCanInitialize()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            NextUploadFailure = OpaqueStoreFailure.Io,
            NextUploadMutationState =
                OpaqueStoreMutationState.NotCommitted,
        };
        var service = Service(store, keys);

        var failed = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(failed.Succeeded);
        Assert.Empty(store.Objects);

        var retried = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(retried.Succeeded, retried.Code);
        Assert.Single(store.Objects);
        Assert.Equal(2, store.UploadCalls);
    }

    [Fact]
    public async Task AdequateDuplicateDebtCleansWithoutNewGeneration()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);
        Assert.True((await service.ResolveAsync(
            access,
            0,
            CancellationToken.None)).Succeeded);
        var original = store.Objects.Single();
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            store.Bytes(original),
            out var sentinel,
            out _));
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel!,
            out var duplicate,
            out _));
        store.Add(duplicate!, original.ExpiresAtUnixSeconds + 1);
        store.ResetCounts();

        var result = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Code);
        Assert.Single(store.Objects);
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(1, store.DeleteCalls);
        var survivor = DecryptSingle(store, access, keys);
        Assert.Equal<ulong>(0, survivor.Generation);
    }

    [Fact]
    public async Task PreviousKeyRotationPreservesOpaqueRootAndUsesCurrentWriter()
    {
        using var access = LocatorTestData.Access();
        using var oldKeys = LocatorTestData.KeyRing(
            access,
            currentBase64: LocatorTestData.PreviousBase64);
        var store = new ScriptedLocatorStore();
        var oldResult = await Service(store, oldKeys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(oldResult.Succeeded, oldResult.Code);
        Assert.True(oldResult.Context!.TryDeriveOpaqueName(
            access,
            "state",
            [7, 8, 9],
            out var oldName));

        using var rotated = LocatorTestData.KeyRing(
            access,
            LocatorTestData.PreviousBase64);
        store.ResetCounts();
        var rotatedResult = await Service(store, rotated).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(rotatedResult.Succeeded, rotatedResult.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.Single(store.Objects);
        Assert.True(rotatedResult.Context!.TryDeriveOpaqueName(
            access,
            "state",
            [7, 8, 9],
            out var rotatedName));
        Assert.Equal(oldName, rotatedName);
        var sentinel = DecryptSingle(store, access, rotated);
        Assert.Equal<ulong>(1, sentinel.Generation);
        Assert.Equal(rotated.CurrentKeyId, sentinel.WriterKeyId);
    }

    [Fact]
    public async Task DependentCoverageRefreshesAndEqualitySatisfiesMargin()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);
        var initial = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initial.Succeeded, initial.Code);
        var initialSentinel = DecryptSingle(store, access, keys);
        var dependent = initialSentinel.RequiredExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds;
        store.ResetCounts();

        var equal = await service.ResolveAsync(
            access,
            dependent,
            CancellationToken.None);
        Assert.True(equal.Succeeded, equal.Code);
        Assert.Equal(0, store.UploadCalls);

        var needsRefresh = await service.ResolveAsync(
            access,
            dependent + 1,
            CancellationToken.None);
        Assert.True(needsRefresh.Succeeded, needsRefresh.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Theory]
    [InlineData(
        (int)OpaqueStoreFailure.Io,
        (int)OpaqueStoreMutationState.Committed)]
    [InlineData(
        (int)OpaqueStoreFailure.OutcomeUnknown,
        (int)OpaqueStoreMutationState.OutcomeUnknown)]
    public async Task PossibleCommittedUploadReconcilesByReadbackAndRelist(
        int failureValue,
        int mutationStateValue)
    {
        var failure = (OpaqueStoreFailure)failureValue;
        var mutationState =
            (OpaqueStoreMutationState)mutationStateValue;
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            NextUploadFailure = failure,
            NextUploadMutationState = mutationState,
            PersistFailedUpload = true,
        };

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Code);
        Assert.Single(store.Objects);
        Assert.True(store.ReadBackCalls >= 2);
    }

    [Fact]
    public async Task DelayedPostUploadListingIsRetriedWithoutReupload()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            HideExistingObjectsForNextLists = 2,
        };

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.True(store.ListCalls >= 4);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task DelayedSuccessorBehindVisiblePredecessorIsRetried()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);
        var initialized = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var sentinel = DecryptSingle(store, access, keys);
        store.ResetCounts();
        store.HideNewestObjectForNextLists = 1;

        var refreshed = await service.ResolveAsync(
            access,
            sentinel.RequiredExpiresAtUnixSeconds -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1,
            CancellationToken.None);

        Assert.True(refreshed.Succeeded, refreshed.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.True(store.ListCalls >= 4);
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Fact]
    public async Task PersistentlyStalePredecessorYieldsUnavailableUntilRetry()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);
        var initialized = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var sentinel = DecryptSingle(store, access, keys);
        store.ResetCounts();
        store.HideNewestObjectForNextLists = 3;

        var delayed = await service.ResolveAsync(
            access,
            sentinel.RequiredExpiresAtUnixSeconds -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, delayed.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal(2, store.Objects.Length);

        store.ResetCounts();
        var recovered = await service.ResolveAsync(
            access,
            sentinel.RequiredExpiresAtUnixSeconds -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1,
            CancellationToken.None);
        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task UnderRetainedUploadIsRemovedAndCannotAccumulate()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            ExtraRetentionSeconds = -1,
            NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown,
            NextDeleteMutationState =
                OpaqueStoreMutationState.OutcomeUnknown,
        };
        var service = Service(store, keys);

        var rejected = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, rejected.Code);
        Assert.Empty(store.Objects);
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal(2, store.DeleteCalls);

        store.ExtraRetentionSeconds = 3_600;
        store.ResetCounts();
        var retried = await service.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(retried.Succeeded, retried.Code);
        Assert.Single(store.Objects);
        Assert.Equal(1, store.UploadCalls);
        retried.Context!.Dispose();

        var retained = DecryptSingle(store, access, keys);
        store.ExtraRetentionSeconds = -1;
        store.ResetCounts();
        var rejectedRefresh = await service.ResolveAsync(
            access,
            retained.RequiredExpiresAtUnixSeconds -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1,
            CancellationToken.None);
        Assert.Equal(LocatorCodes.Unavailable, rejectedRefresh.Code);
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(0, DecryptSingle(
            store,
            access,
            keys).Generation);
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal(1, store.DeleteCalls);

        store.ExtraRetentionSeconds = 3_600;
        store.ResetCounts();
        var recoveredRefresh = await service.ResolveAsync(
            access,
            retained.RequiredExpiresAtUnixSeconds -
                StateRetentionRequirements.SentinelDependentMarginSeconds + 1,
            CancellationToken.None);
        Assert.True(recoveredRefresh.Succeeded, recoveredRefresh.Code);
        recoveredRefresh.Context!.Dispose();
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public async Task MismatchedUploadMetadataNeverDeletesOrReadsReferencedObject(
        int mismatch,
        bool outcomeUnknown)
    {
        using var access = LocatorTestData.Access();
        using var oldKeys = LocatorTestData.KeyRing(
            access,
            currentBase64: LocatorTestData.PreviousBase64);
        var store = new ScriptedLocatorStore();
        var initialized = await Service(store, oldKeys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var oldMetadata = store.Objects.Single();

        using var rotated = LocatorTestData.KeyRing(
            access,
            previousBase64: LocatorTestData.PreviousBase64);
        store.NextUploadMetadataTransform = actual => mismatch switch
        {
            0 => actual with
            {
                Reference = actual.Reference with
                {
                    Name = new OpaqueStoreName("mismatched-locator-name"),
                },
            },
            1 => actual with
            {
                EncryptedObjectDigest =
                    new OpaqueStoreEncryptedObjectDigest(
                        new string('0', 64)),
            },
            2 => actual with { Size = checked(actual.Size + 1) },
            _ => oldMetadata,
        };
        if (outcomeUnknown)
        {
            store.NextUploadFailure = OpaqueStoreFailure.OutcomeUnknown;
            store.NextUploadMutationState =
                OpaqueStoreMutationState.OutcomeUnknown;
        }

        store.ResetCounts();
        var dependent = oldMetadata.ExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;

        var rejected = await Service(store, rotated).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, rejected.Code);
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal(0, store.ReadBackCalls);
        Assert.Equal(0, store.DeleteCalls);
        Assert.Contains(oldMetadata, store.Objects);
        Assert.Equal(2, store.Objects.Length);
    }

    [Fact]
    public async Task PersistentUnderRetainedInitializationRecoversFresh()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            ExtraRetentionSeconds = -1,
            NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown,
            NextDeleteMutationState =
                OpaqueStoreMutationState.OutcomeUnknown,
            DeleteFailuresRemaining = 3,
        };

        var rejected = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, rejected.Code);
        Assert.Single(store.Objects);
        Assert.Equal(3, store.DeleteCalls);

        store.ExtraRetentionSeconds = 3_600;
        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Single(store.Objects);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal<ulong>(0, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Fact]
    public async Task PersistentUnderRetainedRefreshRecoversPredecessorFresh()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var initialized = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var original = DecryptSingle(store, access, keys);
        var dependent = original.RequiredExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;
        store.ExtraRetentionSeconds = -1;
        store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
        store.NextDeleteMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        store.DeleteFailuresRemaining = 3;
        store.ResetCounts();

        var rejected = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, rejected.Code);
        Assert.Equal(2, store.Objects.Length);
        Assert.Equal(3, store.DeleteCalls);

        store.ExtraRetentionSeconds = 3_600;
        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Single(store.Objects);
        Assert.Equal(1, store.UploadCalls);
        Assert.True(store.DeleteCalls >= 2);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Fact]
    public async Task UnderRetainedUploadRecoversAfterDelayedVisibility()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            ExtraRetentionSeconds = -1,
            ReadBackFailure = OpaqueStoreFailure.Io,
            NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown,
            NextDeleteMutationState =
                OpaqueStoreMutationState.OutcomeUnknown,
            DeleteFailuresRemaining = 1,
            HideExistingObjectsForNextLists = 1,
        };

        var rejected = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, rejected.Code);
        Assert.Single(store.Objects);
        Assert.Equal(0, store.ReadBackCalls);
        Assert.Equal(1, store.DeleteCalls);

        store.ExtraRetentionSeconds = 3_600;
        store.ReadBackFailure = OpaqueStoreFailure.None;
        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Single(store.Objects);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(1, store.UploadCalls);
    }

    [Fact]
    public async Task PersistedUnderFloorSentinelIsCleanedAndReinitialized()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var sentinel = LocatorTestData.Sentinel(
            keys,
            requiredExpiry: LocatorTestData.Now +
                StateRetentionRequirements.SentinelRequestSeconds);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel,
            out var envelope,
            out var code),
            code);
        store.Add(
            envelope!,
            sentinel.RequiredExpiresAtUnixSeconds - 1);

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        result.Context!.Dispose();
        Assert.Equal(1, store.UploadCalls);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task EightEquivalentCopiesKeepStrongFloorWithoutUploading()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore
        {
            NextUploadFailure = OpaqueStoreFailure.Io,
            NextUploadMutationState =
                OpaqueStoreMutationState.NotCommitted,
        };
        var requiredFloor = LocatorTestData.Now +
            StateRetentionRequirements.SentinelRequestSeconds;
        var lower = LocatorTestData.Sentinel(
            keys,
            requiredExpiry: requiredFloor - 1);
        for (var index = 0; index < 7; index++)
        {
            Assert.True(LocatorRootSentinelCodec.TryEncrypt(
                access,
                keys,
                lower,
                out var lowerEnvelope,
                out var lowerCode),
                lowerCode);
            store.Add(
                lowerEnvelope!,
                requiredFloor + 2_000 + index,
                $"lower-floor-{index}");
        }

        var stronger = lower with
        {
            RequiredExpiresAtUnixSeconds = requiredFloor,
        };
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            stronger,
            out var strongerEnvelope,
            out var strongerCode),
            strongerCode);
        store.Add(
            strongerEnvelope!,
            requiredFloor + 1_000,
            "stronger-floor");
        store.ResetCounts();

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        result.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(7, store.DeleteCalls);
        Assert.Equal(
            "stronger-floor",
            Assert.Single(store.Objects).Reference.ObjectId.Value);
    }

    [Fact]
    public async Task ExpiredExactSupersededArtifactIsPruned()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var root = Enumerable.Repeat((byte)0x52, 32).ToArray();
        var requiredExpiry = LocatorTestData.Now +
            StateRetentionRequirements.SentinelRequestSeconds;
        var old = LocatorTestData.Sentinel(
            keys,
            root: root.ToArray(),
            requiredExpiry: requiredExpiry);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            old,
            out var oldEnvelope,
            out var oldCode),
            oldCode);
        var oldMetadata = store.Add(
            oldEnvelope!,
            old.RequiredExpiresAtUnixSeconds + 3_600,
            "expired-superseded");
        var absentPredecessor = LocatorRootSentinelCodec.Identity(
            LocatorTestData.Metadata(
                "pruned-predecessor",
                old.RequiredExpiresAtUnixSeconds + 3_600));
        var head = LocatorTestData.Sentinel(
            keys,
            root: root.ToArray(),
            generation: 1,
            requiredExpiry: requiredExpiry,
            predecessors: [absentPredecessor],
            superseded:
            [
                LocatorRootSentinelCodec.Identity(oldMetadata),
            ]);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            head,
            out var headEnvelope,
            out var headCode),
            headCode);
        store.Add(
            headEnvelope!,
            head.RequiredExpiresAtUnixSeconds + 7_200,
            "live-head");
        store.MarkExpired(oldMetadata);

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        Assert.Single(store.Objects);
        Assert.Equal("live-head", store.Objects[0].Reference.ObjectId.Value);
        CryptographicOperations.ZeroMemory(root);
    }

    [Fact]
    public async Task ExpiredSingletonMapsToUnavailable()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var sentinel = LocatorTestData.Sentinel(
            keys,
            requiredExpiry: LocatorTestData.Now + 100);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel,
            out var envelope,
            out var code),
            code);
        var metadata = store.Add(
            envelope!,
            LocatorTestData.Now + 200,
            "expired-singleton");
        store.MarkExpired(metadata);

        var result = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, result.Code);
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(0, store.DeleteCalls);
    }

    [Fact]
    public async Task LocatorOutcomesDistinguishInvalidUnavailableAndConflict()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);

        var invalid = await Service(new ScriptedLocatorStore(), keys)
            .ResolveAsync(access, -1, CancellationToken.None);
        Assert.Equal(LocatorCodes.Invalid, invalid.Code);

        var overflowTime = new LocatorRootService(
            new ScriptedLocatorStore(),
            keys,
            new FrozenLocatorTimeProvider(
                RestrictedStateFormat.MaximumUnixSeconds));
        var unavailable = await overflowTime.ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.Equal(LocatorCodes.Unavailable, unavailable.Code);

        var partialStore = new ScriptedLocatorStore
        {
            ListComplete = false,
        };
        var partial = await Service(partialStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.Equal(LocatorCodes.Conflict, partial.Code);

        var overCapStore = new ScriptedLocatorStore();
        for (var index = 0; index <=
            LocatorRootFormat.MaximumPhysicalSentinels; index++)
        {
            overCapStore.Add(
                [1],
                LocatorTestData.Now + 1,
                $"over-cap-{index}");
        }

        var overCap = await Service(overCapStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.Equal(LocatorCodes.Conflict, overCap.Code);
    }

    [Fact]
    public async Task DeleteOutcomeUnknownConvergesOnlyWhenObjectIsAbsent()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);

        var absentStore = await StoreWithDuplicateAsync(access, keys);
        absentStore.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
        absentStore.NextDeleteMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        absentStore.RemoveOnDeleteFailure = true;
        var absent = await Service(absentStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(absent.Succeeded, absent.Code);
        Assert.Single(absentStore.Objects);

        var remainsStore = await StoreWithDuplicateAsync(access, keys);
        remainsStore.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
        remainsStore.NextDeleteMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        var remains = await Service(remainsStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(remains.Succeeded);
        Assert.Equal(LocatorCodes.CleanupFailed, remains.Code);
        Assert.Equal(2, remainsStore.Objects.Length);
    }

    [Fact]
    public async Task EightRecordsAreReducedBeforeSuccessorAppend()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var service = Service(store, keys);
        Assert.True((await service.ResolveAsync(
            access,
            0,
            CancellationToken.None)).Succeeded);
        var metadata = store.Objects.Single();
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            store.Bytes(metadata),
            out var sentinel,
            out _));
        for (var index = 0; index < 7; index++)
        {
            Assert.True(LocatorRootSentinelCodec.TryEncrypt(
                access,
                keys,
                sentinel!,
                out var duplicate,
                out _));
            store.Add(
                duplicate!,
                metadata.ExpiresAtUnixSeconds + index + 1);
        }

        store.ResetCounts();
        var dependent = metadata.ExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;
        var result = await service.ResolveAsync(
            access,
            dependent,
            CancellationToken.None);
        Assert.True(result.Succeeded, result.Code);
        Assert.Single(store.Objects);
        Assert.Equal(1, store.UploadCalls);
        Assert.True(store.DeleteCalls >= 8);
        Assert.Equal(8, store.MaximumObservedObjects);
    }

    [Fact]
    public async Task AtCapacityPersistentCleanupFailureIsNotConflict()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var (store, _, head) = await StoreAtCapacityAsync(access, keys);
        store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
        store.NextDeleteMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        store.DeleteFailuresRemaining =
            LocatorRootFormat.MaximumPhysicalSentinels - 1;
        var dependent = head.ExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;

        var blocked = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.CleanupFailed, blocked.Code);
        Assert.NotEqual(LocatorCodes.Conflict, blocked.Code);
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(
            LocatorRootFormat.MaximumPhysicalSentinels,
            store.Objects.Length);

        store.NextDeleteFailure = OpaqueStoreFailure.None;
        store.NextDeleteMutationState =
            OpaqueStoreMutationState.NotCommitted;
        store.DeleteFailuresRemaining = 0;
        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(1, store.UploadCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task DuplicateCleanupRejectsConcurrentUnderRetainedSuccessor()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = await StoreWithDuplicateAsync(access, keys);
        var head = store.Objects
            .OrderByDescending(item => item.ExpiresAtUnixSeconds)
            .First();
        var duplicate = store.Objects.Single(item => item != head);
        var headSentinel = Decrypt(store, head, access, keys);
        var successor = LocatorTestData.Sentinel(
            keys,
            root: headSentinel.Root.ToArray(),
            generation: 1,
            requiredExpiry: headSentinel.RequiredExpiresAtUnixSeconds,
            predecessors: [LocatorRootSentinelCodec.Identity(head)],
            superseded: [LocatorRootSentinelCodec.Identity(duplicate)]);
        var successorEnvelope = Encrypt(access, keys, successor);
        store.BeforeDelete = () => store.Add(
            successorEnvelope,
            successor.RequiredExpiresAtUnixSeconds - 1,
            "concurrent-under-retained-duplicate-successor");

        var blocked = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, blocked.Code);
        Assert.NotEqual(LocatorCodes.Ready, blocked.Code);
        Assert.Equal(0, store.UploadCalls);

        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task SuccessorPruningRejectsConcurrentUnderRetainedSuccessor()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var initialized = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var originalMetadata = store.Objects.Single();
        var original = Decrypt(store, originalMetadata, access, keys);
        var dependent = original.RequiredExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;
        store.ResetCounts();
        store.BeforeDelete = () =>
        {
            var successorMetadata = store.Objects.Single(item =>
                item != originalMetadata);
            var successor = Decrypt(
                store,
                successorMetadata,
                access,
                keys);
            var concurrent = LocatorTestData.Sentinel(
                keys,
                root: successor.Root.ToArray(),
                generation: checked(successor.Generation + 1),
                requiredExpiry: successor.RequiredExpiresAtUnixSeconds,
                predecessors:
                [
                    LocatorRootSentinelCodec.Identity(successorMetadata),
                ],
                superseded:
                [
                    LocatorRootSentinelCodec.Identity(originalMetadata),
                ]);
            store.Add(
                Encrypt(access, keys, concurrent),
                concurrent.RequiredExpiresAtUnixSeconds - 1,
                "concurrent-under-retained-pruning-successor");
        };

        var blocked = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, blocked.Code);
        Assert.NotEqual(LocatorCodes.Ready, blocked.Code);
        Assert.Equal(1, store.UploadCalls);

        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);
        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task HeadroomCleanupRejectsConcurrentUnderRetainedSuccessor()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var (store, sentinel, head) = await StoreAtCapacityAsync(
            access,
            keys);
        var superseded = store.Objects
            .Where(item => item != head)
            .Select(LocatorRootSentinelCodec.Identity)
            .OrderBy(item => item.ObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.ArchiveSha256, StringComparer.Ordinal)
            .ThenBy(item => item.EnvelopeSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var requiredExpiry = head.ExpiresAtUnixSeconds + 1;
        var concurrent = LocatorTestData.Sentinel(
            keys,
            root: sentinel.Root.ToArray(),
            generation: 1,
            requiredExpiry: requiredExpiry,
            predecessors: [LocatorRootSentinelCodec.Identity(head)],
            superseded: superseded);
        var concurrentEnvelope = Encrypt(access, keys, concurrent);
        store.BeforeDelete = () => store.Add(
            concurrentEnvelope,
            requiredExpiry - 1,
            "concurrent-under-retained-headroom-successor");
        var dependent = head.ExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;

        var blocked = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.Unavailable, blocked.Code);
        Assert.NotEqual(LocatorCodes.Ready, blocked.Code);
        Assert.Equal(0, store.UploadCalls);

        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);
        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(1, store.UploadCalls);
        Assert.Single(store.Objects);
    }

    [Fact]
    public async Task HeadroomCleanupAdoptsConcurrentAdequateSuccessor()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var (store, sentinel, head) = await StoreAtCapacityAsync(
            access,
            keys);
        var superseded = store.Objects
            .Where(item => item != head)
            .Select(LocatorRootSentinelCodec.Identity)
            .OrderBy(item => item.ObjectId, StringComparer.Ordinal)
            .ThenBy(item => item.ArchiveSha256, StringComparer.Ordinal)
            .ThenBy(item => item.EnvelopeSha256, StringComparer.Ordinal)
            .ToImmutableArray();
        var requiredExpiry = head.ExpiresAtUnixSeconds + 1;
        var concurrent = LocatorTestData.Sentinel(
            keys,
            root: sentinel.Root.ToArray(),
            generation: 1,
            requiredExpiry: requiredExpiry,
            predecessors: [LocatorRootSentinelCodec.Identity(head)],
            superseded: superseded);
        var concurrentEnvelope = Encrypt(access, keys, concurrent);
        store.BeforeDelete = () => store.Add(
            concurrentEnvelope,
            requiredExpiry + 3_600,
            "concurrent-adequate-headroom-successor");
        var dependent = head.ExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;

        var result = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.True(result.Succeeded, result.Code);
        result.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Fact]
    public async Task HiddenPreviousKeyFallbackPreservesRootDuringDebtCleanup()
    {
        using var access = LocatorTestData.Access();
        using var oldKeys = LocatorTestData.KeyRing(
            access,
            currentBase64: LocatorTestData.PreviousBase64);
        var store = new ScriptedLocatorStore();
        var oldResult = await Service(store, oldKeys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(oldResult.Succeeded, oldResult.Code);
        Assert.True(oldResult.Context!.TryDeriveOpaqueName(
            access,
            "hidden-fallback",
            [1, 5, 3],
            out var oldName));
        oldResult.Context.Dispose();
        var oldMetadata = store.Objects.Single();
        var oldSentinel = Decrypt(store, oldMetadata, access, oldKeys);

        using var rotated = LocatorTestData.KeyRing(
            access,
            previousBase64: LocatorTestData.PreviousBase64);
        var underRetained = LocatorTestData.Sentinel(
            rotated,
            root: oldSentinel.Root.ToArray(),
            generation: 1,
            requiredExpiry:
                oldSentinel.RequiredExpiresAtUnixSeconds + 1,
            predecessors: [LocatorRootSentinelCodec.Identity(oldMetadata)]);
        store.Add(
            Encrypt(access, rotated, underRetained),
            underRetained.RequiredExpiresAtUnixSeconds - 1,
            "under-retained-rotated-successor");
        store.AfterDelete = () =>
            store.HideExistingObjectsForNextLists = 1;
        store.ResetCounts();

        var recovered = await Service(store, rotated).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        Assert.True(recovered.Context!.TryDeriveOpaqueName(
            access,
            "hidden-fallback",
            [1, 5, 3],
            out var recoveredName));
        recovered.Context.Dispose();
        Assert.Equal(oldName, recoveredName);
        Assert.Equal(1, store.UploadCalls);
        Assert.Single(store.Objects);
        Assert.Equal(rotated.CurrentKeyId, DecryptSingle(
            store,
            access,
            rotated).WriterKeyId);
    }

    [Fact]
    public async Task InterruptedDuplicatePruningRetainsImmediatePredecessor()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var initialized = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var predecessor = store.Objects.Single();
        var sentinel = Decrypt(store, predecessor, access, keys);
        var duplicate = store.Add(
            Encrypt(access, keys, sentinel),
            predecessor.ExpiresAtUnixSeconds,
            "zz-later-equivalent-copy");
        store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
        store.NextDeleteMutationState =
            OpaqueStoreMutationState.OutcomeUnknown;
        store.ResetCounts();
        var dependent = sentinel.RequiredExpiresAtUnixSeconds -
            StateRetentionRequirements.SentinelDependentMarginSeconds + 1;

        var interrupted = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.CleanupFailed, interrupted.Code);
        Assert.Equal(1, store.DeleteCalls);
        Assert.Equal(1, store.UploadCalls);
        Assert.Contains(predecessor, store.Objects);
        Assert.Contains(duplicate, store.Objects);
        Assert.Equal(3, store.Objects.Length);

        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            dependent,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
        Assert.Equal<ulong>(1, DecryptSingle(
            store,
            access,
            keys).Generation);
    }

    [Fact]
    public async Task InterruptedChainPruningDeletesOldestAnchorFirst()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var store = new ScriptedLocatorStore();
        var root = Enumerable.Repeat((byte)0x6a, 32).ToArray();
        var requiredExpiry = LocatorTestData.Now +
            StateRetentionRequirements.SentinelRequestSeconds;
        var oldest = LocatorTestData.Sentinel(
            keys,
            root: root.ToArray(),
            requiredExpiry: requiredExpiry);
        var oldestMetadata = store.Add(
            Encrypt(access, keys, oldest),
            requiredExpiry + 3_600,
            "chain-generation-0");
        var predecessor = LocatorTestData.Sentinel(
            keys,
            root: root.ToArray(),
            generation: 1,
            requiredExpiry: requiredExpiry,
            predecessors:
            [
                LocatorRootSentinelCodec.Identity(oldestMetadata),
            ]);
        var predecessorMetadata = store.Add(
            Encrypt(access, keys, predecessor),
            requiredExpiry + 3_600,
            "chain-generation-1");
        var head = LocatorTestData.Sentinel(
            keys,
            root: root.ToArray(),
            generation: 2,
            requiredExpiry: requiredExpiry,
            predecessors:
            [
                LocatorRootSentinelCodec.Identity(predecessorMetadata),
            ]);
        var headMetadata = store.Add(
            Encrypt(access, keys, head),
            requiredExpiry + 3_600,
            "chain-generation-2");
        store.AfterDelete = () =>
        {
            store.NextDeleteFailure = OpaqueStoreFailure.OutcomeUnknown;
            store.NextDeleteMutationState =
                OpaqueStoreMutationState.OutcomeUnknown;
        };

        var interrupted = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.Equal(LocatorCodes.CleanupFailed, interrupted.Code);
        Assert.Equal(2, store.DeleteCalls);
        Assert.DoesNotContain(oldestMetadata, store.Objects);
        Assert.Contains(predecessorMetadata, store.Objects);
        Assert.Contains(headMetadata, store.Objects);

        store.ResetCounts();
        var recovered = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);

        Assert.True(recovered.Succeeded, recovered.Code);
        recovered.Context!.Dispose();
        Assert.Equal(0, store.UploadCalls);
        Assert.Equal(
            headMetadata,
            Assert.Single(store.Objects));
        CryptographicOperations.ZeroMemory(root);
    }

    [Fact]
    public async Task PartialEnumerationWrongKeyAndOverflowFailClosed()
    {
        using var access = LocatorTestData.Access();
        using var keys = LocatorTestData.KeyRing(access);
        var partial = new ScriptedLocatorStore { ListComplete = false };
        var partialResult = await Service(partial, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(partialResult.Succeeded);
        Assert.Equal(0, partial.UploadCalls);

        using var oldKeys = LocatorTestData.KeyRing(
            access,
            currentBase64: LocatorTestData.PreviousBase64);
        var oldStore = new ScriptedLocatorStore();
        Assert.True((await Service(oldStore, oldKeys).ResolveAsync(
            access,
            0,
            CancellationToken.None)).Succeeded);
        var lost = await Service(oldStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(lost.Succeeded);
        Assert.Equal(LocatorCodes.KeyUnavailable, lost.Code);

        var overflowStore = new ScriptedLocatorStore();
        var absentPredecessor = LocatorRootSentinelCodec.Identity(
            LocatorTestData.Metadata(
                "absent-overflow-predecessor",
                LocatorTestData.Now + 1));
        var overflowSentinel = LocatorTestData.Sentinel(
            keys,
            generation: ulong.MaxValue,
            requiredExpiry: LocatorTestData.Now + 1,
            predecessors: [absentPredecessor]);
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            overflowSentinel,
            out var overflowEnvelope,
            out _));
        overflowStore.Add(
            overflowEnvelope!,
            LocatorTestData.Now + 1);
        var overflow = await Service(overflowStore, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.False(overflow.Succeeded);
        Assert.Equal(LocatorCodes.Conflict, overflow.Code);
        Assert.Equal(0, overflowStore.UploadCalls);
    }

    private static LocatorRootService Service(
        ScriptedLocatorStore store,
        LocatorStateKeyRing keys) =>
        new(
            store,
            keys,
            new FrozenLocatorTimeProvider(LocatorTestData.Now));

    private static LocatorRootSentinel DecryptSingle(
        ScriptedLocatorStore store,
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys)
    {
        var metadata = store.Objects.Single();
        return Decrypt(store, metadata, access, keys);
    }

    private static LocatorRootSentinel Decrypt(
        ScriptedLocatorStore store,
        OpaqueStoreObjectMetadata metadata,
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys)
    {
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            store.Bytes(metadata),
            out var sentinel,
            out var code),
            code);
        return sentinel!;
    }

    private static byte[] Encrypt(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys,
        LocatorRootSentinel sentinel)
    {
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel,
            out var envelope,
            out var code),
            code);
        return envelope!;
    }

    private static async Task<(
        ScriptedLocatorStore Store,
        LocatorRootSentinel Sentinel,
        OpaqueStoreObjectMetadata Head)> StoreAtCapacityAsync(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys)
    {
        var store = new ScriptedLocatorStore();
        var initialized = await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        initialized.Context!.Dispose();
        var original = store.Objects.Single();
        var sentinel = Decrypt(store, original, access, keys);
        for (var index = 0; index <
            LocatorRootFormat.MaximumPhysicalSentinels - 1; index++)
        {
            store.Add(
                Encrypt(access, keys, sentinel),
                original.ExpiresAtUnixSeconds + index + 1);
        }

        var head = store.Objects
            .OrderByDescending(item => item.ExpiresAtUnixSeconds)
            .ThenBy(
                item => item.Reference.ObjectId.Value,
                StringComparer.Ordinal)
            .First();
        store.ResetCounts();
        return (store, sentinel, head);
    }

    private static async Task<ScriptedLocatorStore> StoreWithDuplicateAsync(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys)
    {
        var store = new ScriptedLocatorStore();
        Assert.True((await Service(store, keys).ResolveAsync(
            access,
            0,
            CancellationToken.None)).Succeeded);
        var metadata = store.Objects.Single();
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            store.Bytes(metadata),
            out var sentinel,
            out _));
        Assert.True(LocatorRootSentinelCodec.TryEncrypt(
            access,
            keys,
            sentinel!,
            out var duplicate,
            out _));
        store.Add(duplicate!, metadata.ExpiresAtUnixSeconds + 1);
        store.ResetCounts();
        return store;
    }
}
