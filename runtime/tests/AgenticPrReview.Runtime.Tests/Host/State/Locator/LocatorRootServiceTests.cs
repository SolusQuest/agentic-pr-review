using System.Security.Cryptography;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

public sealed class LocatorRootServiceTests
{
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

    [Fact]
    public async Task PersistedUnderFloorSentinelCannotBeWeakenedLater()
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

        Assert.Equal(LocatorCodes.Unavailable, result.Code);
        Assert.Equal(0, store.UploadCalls);
        Assert.Single(store.Objects);
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
        Assert.True(LocatorRootSentinelCodec.TryDecrypt(
            access,
            keys,
            store.Bytes(metadata),
            out var sentinel,
            out var code),
            code);
        return sentinel!;
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
