using System.Collections.Immutable;
using AgenticPrReview.Runtime.Agent.Core;
using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class RestrictedStateStoreConformanceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteListMetadataDownloadReadbackAndDeleteConform(
        bool synthetic)
    {
        await RestrictedStateStoreConformanceHarness.VerifyAsync(
            action => WithStoreAsync(synthetic, action));
    }

    [Fact]
    public void ResultValidationRejectsPartialDuplicateAndAuthorityDefects()
    {
        var name = new OpaqueStoreName("name");
        var reference = new OpaqueStoreObjectReference(
            name,
            new OpaqueStoreObjectId("id"));
        var metadata = Metadata(reference, [1]);

        Assert.False(new OpaqueStoreListResult(
            OpaqueStoreFailure.None,
            [reference],
            Complete: false).Succeeded);
        Assert.False(new OpaqueStoreListResult(
            OpaqueStoreFailure.None,
            [reference, reference],
            Complete: true).Succeeded);
        Assert.False(new OpaqueStoreMetadataResult(
            OpaqueStoreFailure.None,
            metadata with
            {
                ProducingRun = metadata.ProducingRun with { Attempt = -1 },
            }).Succeeded);
        Assert.False(new OpaqueStoreDownloadResult(
            OpaqueStoreFailure.None,
            metadata,
            new byte[] { 1, 2 }).Succeeded);
        Assert.False(new OpaqueStoreDownloadResult(
            OpaqueStoreFailure.None,
            metadata,
            new byte[] { 2 }).Succeeded);
        Assert.False(new OpaqueStoreUploadResult(
            OpaqueStoreFailure.None,
            OpaqueStoreMutationState.OutcomeUnknown,
            metadata).Succeeded);
        Assert.False(new OpaqueStoreDeleteResult(
            OpaqueStoreFailure.None,
            OpaqueStoreMutationState.OutcomeUnknown).Succeeded);
    }

    [Fact]
    public async Task SyntheticAdapterProvesArchiveAndEncryptedDigestsAreIndependent()
    {
        var store = new SyntheticRestrictedStateStore();
        var bytes = new byte[] { 1, 2, 3 };
        var uploaded = await store.UploadImmutableAsync(
            new OpaqueStoreUploadRequest(
                new OpaqueStoreName("digests"),
                new OpaqueStoreCorrelationId("operation"),
                bytes,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Expires),
            CancellationToken.None);

        Assert.True(uploaded.Succeeded);
        Assert.NotEqual(
            uploaded.Metadata!.ArchiveDigest.Sha256,
            uploaded.Metadata.EncryptedObjectDigest.Sha256);
        Assert.NotEqual(
            typeof(OpaqueStoreArchiveDigest),
            typeof(OpaqueStoreEncryptedObjectDigest));
        var download = await store.DownloadAsync(
            new OpaqueStoreDownloadRequest(uploaded.Metadata, bytes.Length),
            CancellationToken.None);
        Assert.True(download.Succeeded);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("digest")]
    [InlineData("size")]
    [InlineData("expiry")]
    public async Task CoordinatorRejectsPersistedUploadAuthorityMismatch(
        string field)
    {
        var store = new SyntheticRestrictedStateStore((metadata, request) =>
            field switch
            {
                "name" => metadata with
                {
                    Reference = metadata.Reference with
                    {
                        Name = new OpaqueStoreName("wrong-name"),
                    },
                },
                "digest" => metadata with
                {
                    EncryptedObjectDigest =
                        new OpaqueStoreEncryptedObjectDigest(
                            new string('0', 64)),
                },
                "size" => metadata with { Size = metadata.Size + 1 },
                "expiry" => metadata with
                {
                    ExpiresAtUnixSeconds =
                        request.MinimumExpiresAtUnixSeconds - 1,
                },
                _ => throw new InvalidOperationException(),
            });
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            new RestrictedStateSnapshot([candidate], null),
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Invalid, result.Failure);
        Assert.Equal(0, store.ObjectCount);
    }

    [Fact]
    public async Task RollbackAmbiguityIsReportedAsCleanupFailure()
    {
        var store = new SyntheticRestrictedStateStore(
            (metadata, _) => metadata with
            {
                Reference = metadata.Reference with
                {
                    Name = new OpaqueStoreName("wrong-name"),
                },
            },
            failDelete: true);
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            new RestrictedStateSnapshot([candidate], null),
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Cleanup, result.Failure);
        Assert.Equal(1, store.ObjectCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrepareAndAcceptIgnoreCancellationAfterDurableIndexReadback(
        bool accept)
    {
        var store = new SyntheticRestrictedStateStore();
        var keys = new TestKeyResolver();
        var access = RestrictedStateTestData.Access();
        var service = new RestrictedStateService(
            store,
            keys,
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Now);
        RestrictedStatePrepareResult? prepared = null;
        if (accept)
        {
            prepared = await service.PrepareAsync(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                CancellationToken.None);
            Assert.Equal(StateAction.Prepared, prepared.Result.Action);
        }

        using var cancellation = new CancellationTokenSource();
        var cancelAtReadBack = store.ReadBackCalls + (accept ? 1 : 2);
        store.AfterReadBack = (call, _) =>
        {
            if (call == cancelAtReadBack)
            {
                cancellation.Cancel();
            }
        };

        var action = accept
            ? await service.AcceptAsync(
                access,
                null,
                prepared!.Receipt!,
                RestrictedStateTestData.SessionContext(),
                cancellation.Token)
            : (await service.PrepareAsync(
                access,
                new RestrictedStatePrepareRequest(
                    null,
                    new byte[] { 1 },
                    RestrictedStateTestData.SessionContext()),
                cancellation.Token)).Result;

        Assert.Equal(
            accept ? StateAction.Accepted : StateAction.Prepared,
            action.Action);
        store.AfterReadBack = null;
        var enumerated = await service.EnumerateAsync(
            access,
            CancellationToken.None);
        Assert.True(enumerated.Result.Action is
            StateAction.Enumerated or StateAction.Bootstrap);
    }

    [Fact]
    public async Task AmbiguousIndexRollbackPreservesRecoveryEvidence()
    {
        var store = new SyntheticRestrictedStateStore
        {
            FailDeleteOnCall = 1,
            FailListOnCall = 2,
        };
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);
        var replacement = new RestrictedStateSnapshot([candidate], null);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            replacement,
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Io, result.Failure);
        Assert.Equal(2, store.ObjectCount);
        var recovered = await coordinator.ReadAsync(
            access,
            CancellationToken.None);
        Assert.True(recovered.Succeeded);
        Assert.Null(recovered.Snapshot!.Staging);
        var recoveredCandidate = Assert.Single(recovered.Snapshot.Accepted);
        Assert.Equal(candidate.Binding, recoveredCandidate.Binding);
        Assert.Equal(candidate.SessionSha256, recoveredCandidate.SessionSha256);
        Assert.Equal(candidate.EnvelopeSha256, recoveredCandidate.EnvelopeSha256);
        Assert.Equal(candidate.ObjectIdentity, recoveredCandidate.ObjectIdentity);
        Assert.Equal(candidate.Envelope, recoveredCandidate.Envelope);
    }

    [Fact]
    public async Task AmbiguousIndexUploadReadBackPreservesRecoveryEvidence()
    {
        var store = new SyntheticRestrictedStateStore
        {
            FailDeleteOnCall = 1,
            FailReadBackOnCall = 2,
        };
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var candidate = RestrictedStateTestData.Candidate(access, keys);

        var result = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            new RestrictedStateSnapshot([candidate], null),
            CancellationToken.None);

        Assert.False(result.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Io, result.Failure);
        Assert.Equal(2, store.ObjectCount);
        var recovered = await coordinator.ReadAsync(
            access,
            CancellationToken.None);
        Assert.True(recovered.Succeeded);
        var recoveredCandidate = Assert.Single(
            recovered.Snapshot!.Accepted);
        Assert.Equal(candidate.ObjectIdentity, recoveredCandidate.ObjectIdentity);
        Assert.Equal(candidate.Envelope, recoveredCandidate.Envelope);
    }

    [Fact]
    public async Task PartialRawDeletionCannotReportSuccessfulReset()
    {
        var store = new SyntheticRestrictedStateStore
        {
            FailDeleteOnCall = 2,
        };
        var access = RestrictedStateTestData.Access();
        var bytes = new byte[] { 1 };
        store.Seed(Metadata(
            new OpaqueStoreObjectReference(
                RawName(access, index: true),
                new OpaqueStoreObjectId("index")),
            bytes), bytes);
        store.Seed(Metadata(
            new OpaqueStoreObjectReference(
                RawName(access, index: false),
                new OpaqueStoreObjectId("candidate")),
            bytes), bytes);
        var service = new RestrictedStateService(
            store,
            new TestKeyResolver(),
            new TestSessionAdmission(),
            () => RestrictedStateTestData.Now);

        var result = await service.ResetAsync(
            access,
            CancellationToken.None);

        Assert.Equal(StateAction.Failed, result.Action);
        Assert.NotEqual(RestrictedStateCodes.Reset, result.Code);
        Assert.Equal(1, store.ObjectCount);
    }

    [Fact]
    public async Task CleanupPreservesAConcurrentSuccessorCandidate()
    {
        var store = new SyntheticRestrictedStateStore();
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var first = RestrictedStateTestData.Candidate(access, keys);
        var staging = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation: 1,
            predecessor: first.EnvelopeSha256,
            plaintext: [4, 5, 6]);
        var successorStaging = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation: 2,
            predecessor: staging.EnvelopeSha256,
            plaintext: [7, 8, 9]);
        var initial = new RestrictedStateSnapshot([first], null);
        var selected = new RestrictedStateSnapshot([first], staging);
        var successor = new RestrictedStateSnapshot(
            [staging, first],
            successorStaging);
        var created = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            initial,
            CancellationToken.None);
        Assert.True(created.Committed);

        store.BeforeDelete = metadata =>
        {
            if (metadata.Reference.Name != RawName(access, index: true))
            {
                return;
            }

            store.BeforeDelete = null;
            var selectedIndex = ReadIndex(
                store,
                access,
                keys,
                SnapshotVersion(selected));
            var successorStagingMetadata = UploadCandidate(
                store,
                access,
                successorStaging,
                "concurrent-successor-candidate");
            UploadIndex(
                store,
                access,
                keys,
                selectedIndex.Metadata,
                selectedIndex.Index.LogicalVersion,
                successor,
                [
                    selectedIndex.Index.Staging!,
                    selectedIndex.Index.Accepted[0],
                ],
                Indexed(successorStaging, successorStagingMetadata),
                "concurrent-successor-index");
        };

        var advanced = await coordinator.CompareExchangeAsync(
            access,
            created.Version!,
            selected,
            CancellationToken.None);
        var observed = await coordinator.ReadAsync(
            access,
            CancellationToken.None);

        Assert.True(advanced.Committed);
        Assert.True(observed.Succeeded);
        Assert.Equal(SnapshotVersion(successor), observed.Version);
        Assert.Equal(
            successorStaging.ObjectIdentity,
            observed.Snapshot!.Staging!.ObjectIdentity);
    }

    [Fact]
    public async Task RawDeletionPreservesCandidatesOfAConcurrentSuccessor()
    {
        var store = new SyntheticRestrictedStateStore();
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var coordinator = new RestrictedStateOpaqueSnapshotStore(store, keys);
        var first = RestrictedStateTestData.Candidate(access, keys);
        var staging = RestrictedStateTestData.Candidate(
            access,
            keys,
            generation: 1,
            predecessor: first.EnvelopeSha256,
            plaintext: [4, 5, 6]);
        var initial = new RestrictedStateSnapshot([first], null);
        var successor = new RestrictedStateSnapshot([first], staging);
        var created = await coordinator.CompareExchangeAsync(
            access,
            RestrictedStateSnapshotVersion.Absent,
            initial,
            CancellationToken.None);
        var raw = await coordinator.ReadRawVersionAsync(
            access,
            CancellationToken.None);
        Assert.True(created.Committed);
        Assert.True(raw.Succeeded);

        store.BeforeDelete = metadata =>
        {
            if (metadata.Reference.Name != RawName(access, index: true))
            {
                return;
            }

            store.BeforeDelete = null;
            var current = ReadIndex(
                store,
                access,
                keys,
                SnapshotVersion(initial));
            var stagingMetadata = UploadCandidate(
                store,
                access,
                staging,
                "reset-successor-candidate");
            UploadIndex(
                store,
                access,
                keys,
                current.Metadata,
                current.Index.LogicalVersion,
                successor,
                current.Index.Accepted,
                Indexed(staging, stagingMetadata),
                "reset-successor-index");
        };

        var deleted = await coordinator.CompareDeleteRawAsync(
            access,
            raw.Version!,
            CancellationToken.None);
        var observed = await coordinator.ReadAsync(
            access,
            CancellationToken.None);

        Assert.False(deleted.Committed);
        Assert.Equal(RestrictedStateStoreFailure.Cleanup, deleted.Failure);
        Assert.True(observed.Succeeded);
        Assert.Equal(SnapshotVersion(successor), observed.Version);
        Assert.Equal(first.ObjectIdentity, observed.Snapshot!.Accepted[0].ObjectIdentity);
        Assert.Equal(staging.ObjectIdentity, observed.Snapshot.Staging!.ObjectIdentity);
    }

    [Fact]
    public async Task RawVersionUsesInjectiveNominalFieldFraming()
    {
        var store = new SyntheticRestrictedStateStore();
        var access = RestrictedStateTestData.Access();
        var name = RawName(access, index: false);
        var bytes = new byte[] { 1 };
        var first = Metadata(
            new OpaqueStoreObjectReference(
                name,
                new OpaqueStoreObjectId("a:b")),
            bytes) with
        {
            ProducingRun = new OpaqueStoreProducingRun("c", 1),
        };
        var second = Metadata(
            new OpaqueStoreObjectReference(
                name,
                new OpaqueStoreObjectId("a")),
            bytes) with
        {
            ProducingRun = new OpaqueStoreProducingRun("b:c", 1),
        };
        var coordinator = new RestrictedStateOpaqueSnapshotStore(
            store,
            new TestKeyResolver());
        store.Seed(first, bytes);
        var firstRead = await coordinator.ReadRawVersionAsync(
            access,
            CancellationToken.None);
        store.Clear();
        store.Seed(second, bytes);
        var secondRead = await coordinator.ReadRawVersionAsync(
            access,
            CancellationToken.None);

        Assert.True(firstRead.Succeeded);
        Assert.True(secondRead.Succeeded);
        Assert.NotEqual(firstRead.Version, secondRead.Version);
        var staleDelete = await coordinator.CompareDeleteRawAsync(
            access,
            firstRead.Version!,
            CancellationToken.None);
        Assert.Equal(RestrictedStateStoreFailure.Conflict, staleDelete.Failure);
        Assert.False(staleDelete.Committed);
        Assert.Equal(1, store.ObjectCount);
    }

    [Fact]
    public async Task DuplicateAdapterResultsCannotReportCompleteSuccess()
    {
        var store = new SyntheticRestrictedStateStore
        {
            DuplicateListResults = true,
        };
        var bytes = new byte[] { 1 };
        var upload = await store.UploadImmutableAsync(
            new OpaqueStoreUploadRequest(
                new OpaqueStoreName("duplicates"),
                new OpaqueStoreCorrelationId("operation"),
                bytes,
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Expires),
            CancellationToken.None);
        Assert.True(upload.Succeeded);

        var listed = await store.ListExactAsync(
            new OpaqueStoreListRequest(
                upload.Metadata!.Reference.Name,
                8),
            CancellationToken.None);

        Assert.True(listed.Complete);
        Assert.Equal(2, listed.Objects.Length);
        Assert.False(listed.Succeeded);
    }

    private static OpaqueStoreObjectMetadata Metadata(
        OpaqueStoreObjectReference reference,
        byte[] bytes) =>
        new(
            reference,
            new OpaqueStoreProducingRun("run", 1),
            new OpaqueStoreArchiveDigest(OpaqueStoreHash.Sha256(bytes)),
            new OpaqueStoreEncryptedObjectDigest(OpaqueStoreHash.Sha256(bytes)),
            RestrictedStateTestData.Expires,
            bytes.Length);

    private static RestrictedStateSnapshotVersion SnapshotVersion(
        RestrictedStateSnapshot snapshot)
    {
        Assert.True(RestrictedStateSnapshotCodec.TryWrite(snapshot, out var bytes));
        return new RestrictedStateSnapshotVersion(
            AgentCanonical.HashDomain("apr.state-snapshot.r2", bytes),
            Exists: true);
    }

    private static RestrictedStateIndexedCandidate Indexed(
        RestrictedStateCandidate candidate,
        OpaqueStoreObjectMetadata metadata) =>
        new(
            candidate.Binding,
            candidate.SessionSha256,
            candidate.EnvelopeSha256,
            candidate.ObjectIdentity,
            metadata);

    private static OpaqueStoreObjectMetadata UploadCandidate(
        SyntheticRestrictedStateStore store,
        AuthorizedStateAccess access,
        RestrictedStateCandidate candidate,
        string correlation)
    {
        var uploaded = store.UploadImmutableAsync(
                new OpaqueStoreUploadRequest(
                    RawName(access, index: false),
                    new OpaqueStoreCorrelationId(correlation),
                    candidate.Envelope,
                    new OpaqueStoreEncryptedObjectDigest(
                        OpaqueStoreHash.Sha256(candidate.Envelope)),
                    candidate.Binding.ExpiresAtUnixSeconds),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert.True(uploaded.Succeeded);
        return uploaded.Metadata!;
    }

    private static void UploadIndex(
        SyntheticRestrictedStateStore store,
        AuthorizedStateAccess access,
        TestKeyResolver keys,
        OpaqueStoreObjectMetadata predecessor,
        RestrictedStateSnapshotVersion predecessorVersion,
        RestrictedStateSnapshot snapshot,
        ImmutableArray<RestrictedStateIndexedCandidate> accepted,
        RestrictedStateIndexedCandidate? staging,
        string correlation)
    {
        var index = new RestrictedStateTransactionIndex(
            SnapshotVersion(snapshot),
            predecessorVersion,
            predecessor,
            correlation,
            RestrictedStateTransactionCommitState.ReadyForSelection,
            accepted,
            staging);
        Assert.True(RestrictedStateTransactionIndexCodec.TryWrite(
            index,
            out var plaintext));
        Assert.True(RestrictedStateTransactionIndexEnvelope.TryEncrypt(
            access,
            plaintext,
            RestrictedStateTestData.Expires,
            keys,
            out var envelope,
            out var failure), failure.ToString());
        var uploaded = store.UploadImmutableAsync(
                new OpaqueStoreUploadRequest(
                    RawName(access, index: true),
                    new OpaqueStoreCorrelationId(correlation),
                    envelope!,
                    new OpaqueStoreEncryptedObjectDigest(
                        OpaqueStoreHash.Sha256(envelope!)),
                    RestrictedStateTestData.Expires),
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        Assert.True(uploaded.Succeeded);
    }

    private static RestrictedStateIndexNode ReadIndex(
        SyntheticRestrictedStateStore store,
        AuthorizedStateAccess access,
        TestKeyResolver keys,
        RestrictedStateSnapshotVersion version)
    {
        foreach (var metadata in store.MetadataFor(
            RawName(access, index: true)))
        {
            Assert.True(RestrictedStateTransactionIndexEnvelope.TryDecrypt(
                access,
                store.BytesFor(metadata),
                keys,
                out var plaintext,
                out _,
                out var failure), failure.ToString());
            Assert.True(RestrictedStateTransactionIndexCodec.TryRead(
                plaintext!,
                out var index));
            if (index!.LogicalVersion == version)
            {
                return new RestrictedStateIndexNode(metadata, index);
            }
        }

        throw new InvalidOperationException("Expected transaction index not found.");
    }

    private static OpaqueStoreName RawName(
        AuthorizedStateAccess access,
        bool index)
    {
        var scope = RestrictedStateSnapshotCodec.WriteScopeIdentity(
            access.Scope);
        return new OpaqueStoreName(AgentCanonical.HashDomain(
            index
                ? "apr.state-r3-opaque-index-name.s1"
                : "apr.state-r3-opaque-candidate-name.s1",
            scope));
    }

    private static async Task WithStoreAsync(
        bool synthetic,
        Func<RestrictedStateStoreConformanceDriver, Task> action)
    {
        if (synthetic)
        {
            var store = new SyntheticRestrictedStateStore();
            await action(new RestrictedStateStoreConformanceDriver(
                store,
                async expected =>
                {
                    store.DuplicateListResults = true;
                    try
                    {
                        return await store.ListExactAsync(
                            new OpaqueStoreListRequest(
                                expected.Reference.Name,
                                OpaqueStoreLimits.MaximumObjects),
                            CancellationToken.None);
                    }
                    finally
                    {
                        store.DuplicateListResults = false;
                    }
                },
                expected =>
                {
                    store.MissNextReadBack = true;
                    return store.ReadBackExactAsync(
                        new OpaqueStoreReadBackRequest(expected),
                        CancellationToken.None);
                },
                async expected =>
                {
                    var previous = store.CurrentUnixSeconds;
                    store.CurrentUnixSeconds = expected.ExpiresAtUnixSeconds;
                    try
                    {
                        return await store.DownloadAsync(
                            new OpaqueStoreDownloadRequest(
                                expected,
                                checked((int)expected.Size)),
                            CancellationToken.None);
                    }
                    finally
                    {
                        store.CurrentUnixSeconds = previous;
                    }
                },
                () => store.ReportNextUploadMayCommitted = true,
                expected =>
                {
                    store.MakeNextDeleteOutcomeUnknown = true;
                    return store.DeleteExactAsync(
                        new OpaqueStoreDeleteRequest(expected),
                        CancellationToken.None);
                }));
            return;
        }

        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-state-conformance-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var timeProvider = new AdjustableTimeProvider();
            var failNextSync = false;
            var makeNextDeleteUnknown = false;
            var store = new LocalRestrictedStateStore(
                root,
                deleteTemporaryTestHook: path =>
                {
                    if (makeNextDeleteUnknown &&
                        path.EndsWith(".delete", StringComparison.Ordinal))
                    {
                        makeNextDeleteUnknown = false;
                        Directory.CreateDirectory(
                            OriginalPathFromTombstone(path));
                        return false;
                    }

                    File.Delete(path);
                    return true;
                },
                syncDirectoryTestHook: _ =>
                {
                    if (!failNextSync)
                    {
                        return true;
                    }

                    failNextSync = false;
                    return false;
                },
                timeProvider: timeProvider);
            await action(new RestrictedStateStoreConformanceDriver(
                store,
                async expected =>
                {
                    var source = FindLocalRecord(root, expected);
                    var fileName = Path.GetFileName(source);
                    const string extension = ".aprobject";
                    var prefix = fileName[..^(
                        64 + extension.Length)];
                    var duplicate = Path.Join(
                        root,
                        string.Concat(
                            prefix,
                            new string('f', 64),
                            extension));
                    File.Copy(source, duplicate, overwrite: false);
                    try
                    {
                        return await store.ListExactAsync(
                            new OpaqueStoreListRequest(
                                expected.Reference.Name,
                                OpaqueStoreLimits.MaximumObjects),
                            CancellationToken.None);
                    }
                    finally
                    {
                        File.Delete(duplicate);
                    }
                },
                async expected =>
                {
                    var source = FindLocalRecord(root, expected);
                    var hidden = string.Concat(source, ".missing");
                    File.Move(source, hidden, overwrite: false);
                    try
                    {
                        return await store.ReadBackExactAsync(
                            new OpaqueStoreReadBackRequest(expected),
                            CancellationToken.None);
                    }
                    finally
                    {
                        File.Move(hidden, source, overwrite: false);
                    }
                },
                async expected =>
                {
                    var previous = timeProvider.UnixSeconds;
                    timeProvider.UnixSeconds =
                        expected.ExpiresAtUnixSeconds;
                    try
                    {
                        return await store.DownloadAsync(
                            new OpaqueStoreDownloadRequest(
                                expected,
                                checked((int)expected.Size)),
                            CancellationToken.None);
                    }
                    finally
                    {
                        timeProvider.UnixSeconds = previous;
                    }
                },
                () => failNextSync = true,
                async expected =>
                {
                    makeNextDeleteUnknown = true;
                    var result = await store.DeleteExactAsync(
                        new OpaqueStoreDeleteRequest(expected),
                        CancellationToken.None);
                    var tombstone = Assert.Single(
                        Directory.GetFiles(root, "*.delete"));
                    var original = OriginalPathFromTombstone(tombstone);
                    if (Directory.Exists(original))
                    {
                        Directory.Delete(original);
                    }

                    File.Move(tombstone, original, overwrite: false);
                    return result;
                }));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string FindLocalRecord(
        string root,
        OpaqueStoreObjectMetadata expected) =>
        Directory.GetFiles(root, "*.aprobject").Single(path =>
            LocalOpaqueStoreRecordCodec.TryRead(
                File.ReadAllBytes(path),
                out var metadata,
                out _) &&
            metadata == expected);

    private static string OriginalPathFromTombstone(string tombstonePath)
    {
        var fileName = Path.GetFileName(tombstonePath);
        const int suffixLength = 1 + 32 + 7;
        var originalName = fileName[1..^suffixLength];
        return Path.Join(Path.GetDirectoryName(tombstonePath), originalName);
    }

    private sealed class AdjustableTimeProvider : TimeProvider
    {
        internal long UnixSeconds { get; set; } = RestrictedStateTestData.Now;

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);
    }

    private sealed class SyntheticRestrictedStateStore
        : IRestrictedStateStore
    {
        private readonly Func<
            OpaqueStoreObjectMetadata,
            OpaqueStoreUploadRequest,
            OpaqueStoreObjectMetadata>? mutateUploadMetadata;
        private readonly bool failDelete;
        private readonly Dictionary<string, (OpaqueStoreObjectMetadata, byte[])>
            objects = new(StringComparer.Ordinal);
        private int deleteCalls;
        private int listCalls;
        private int readBackAttempts;

        internal SyntheticRestrictedStateStore(
            Func<
                OpaqueStoreObjectMetadata,
                OpaqueStoreUploadRequest,
                OpaqueStoreObjectMetadata>? mutateUploadMetadata = null,
            bool failDelete = false)
        {
            this.mutateUploadMetadata = mutateUploadMetadata;
            this.failDelete = failDelete;
        }

        internal int ObjectCount => objects.Count;

        internal int ReadBackCalls { get; private set; }

        internal int FailDeleteOnCall { get; init; }

        internal int FailListOnCall { get; init; }

        internal int FailReadBackOnCall { get; init; }

        internal bool FailLists { get; set; }

        internal bool DuplicateListResults { get; set; }

        internal bool MissNextReadBack { get; set; }

        internal bool ReportNextUploadMayCommitted { get; set; }

        internal bool MakeNextDeleteOutcomeUnknown { get; set; }

        internal long CurrentUnixSeconds { get; set; } =
            RestrictedStateTestData.Now;

        internal Action<int, OpaqueStoreObjectMetadata>? AfterReadBack
        {
            get;
            set;
        }

        internal Action<OpaqueStoreObjectMetadata>? BeforeDelete
        {
            get;
            set;
        }

        public Task<OpaqueStoreListResult> ListExactAsync(
            OpaqueStoreListRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            listCalls++;
            if (FailLists || listCalls == FailListOnCall)
            {
                return Task.FromResult(OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Io));
            }

            if (!OpaqueStoreValidation.IsValid(request))
            {
                return Task.FromResult(OpaqueStoreListResult.Fail(
                    OpaqueStoreFailure.Invalid));
            }

            var matches = objects.Values
                .Select(item => item.Item1.Reference)
                .Where(item => item.Name == request.Name)
                .OrderBy(item => item.ObjectId.Value, StringComparer.Ordinal)
                .ToImmutableArray();
            if (DuplicateListResults && matches.Length > 0)
            {
                return Task.FromResult(new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    [matches[0], matches[0]],
                    Complete: true));
            }

            return Task.FromResult(matches.Length <= request.MaximumObjects
                ? new OpaqueStoreListResult(
                    OpaqueStoreFailure.None,
                    matches,
                    Complete: true)
                : OpaqueStoreListResult.Fail(OpaqueStoreFailure.Incomplete));
        }

        public Task<OpaqueStoreMetadataResult> ReadMetadataAsync(
            OpaqueStoreMetadataRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(TryGet(request.Reference, out var value)
                ? new OpaqueStoreMetadataResult(
                    OpaqueStoreFailure.None,
                    value.Metadata)
                : OpaqueStoreMetadataResult.Fail(OpaqueStoreFailure.NotFound));
        }

        public Task<OpaqueStoreDownloadResult> DownloadAsync(
            OpaqueStoreDownloadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryGet(request.Expected.Reference, out var value))
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            if (value.Metadata != request.Expected)
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.Conflict));
            }

            if (value.Metadata.ExpiresAtUnixSeconds <= CurrentUnixSeconds)
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.Expired));
            }

            if (value.Bytes.Length > request.MaximumBytes)
            {
                return Task.FromResult(OpaqueStoreDownloadResult.Fail(
                    OpaqueStoreFailure.DigestMismatch));
            }

            return Task.FromResult(new OpaqueStoreDownloadResult(
                OpaqueStoreFailure.None,
                value.Metadata,
                value.Bytes));
        }

        public Task<OpaqueStoreUploadResult> UploadImmutableAsync(
            OpaqueStoreUploadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!OpaqueStoreValidation.IsValid(request) ||
                OpaqueStoreHash.Sha256(request.EncryptedBytes.Span) !=
                    request.EncryptedObjectDigest.Sha256)
            {
                return Task.FromResult(OpaqueStoreUploadResult.Fail(
                    OpaqueStoreFailure.Invalid));
            }

            var bytes = request.EncryptedBytes.ToArray();
            var objectId = new OpaqueStoreObjectId(Guid.NewGuid().ToString("N"));
            var reference = new OpaqueStoreObjectReference(
                request.Name,
                objectId);
            var archiveInput = new byte[bytes.Length + 1];
            bytes.CopyTo(archiveInput, 1);
            var metadata = new OpaqueStoreObjectMetadata(
                reference,
                new OpaqueStoreProducingRun("synthetic-run", 7),
                new OpaqueStoreArchiveDigest(
                    OpaqueStoreHash.Sha256(archiveInput)),
                request.EncryptedObjectDigest,
                request.MinimumExpiresAtUnixSeconds + 1,
                bytes.Length);
            metadata = mutateUploadMetadata?.Invoke(metadata, request) ??
                metadata;
            objects.Add(objectId.Value, (metadata, bytes));
            if (ReportNextUploadMayCommitted)
            {
                ReportNextUploadMayCommitted = false;
                return Task.FromResult(OpaqueStoreUploadResult.Fail(
                    OpaqueStoreFailure.Io,
                    OpaqueStoreMutationState.Committed,
                    metadata));
            }

            return Task.FromResult(new OpaqueStoreUploadResult(
                OpaqueStoreFailure.None,
                OpaqueStoreMutationState.Committed,
                metadata));
        }

        public Task<OpaqueStoreReadBackResult> ReadBackExactAsync(
            OpaqueStoreReadBackRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            readBackAttempts++;
            if (readBackAttempts == FailReadBackOnCall)
            {
                return Task.FromResult(OpaqueStoreReadBackResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            if (MissNextReadBack)
            {
                MissNextReadBack = false;
                return Task.FromResult(OpaqueStoreReadBackResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            var result =
                TryGet(request.Expected.Reference, out var value) &&
                value.Metadata == request.Expected
                    ? new OpaqueStoreReadBackResult(
                        OpaqueStoreFailure.None,
                        value.Metadata)
                    : OpaqueStoreReadBackResult.Fail(
                        OpaqueStoreFailure.NotFound);
            if (result.Succeeded)
            {
                ReadBackCalls++;
                AfterReadBack?.Invoke(ReadBackCalls, result.Metadata!);
            }

            return Task.FromResult(result);
        }

        public Task<OpaqueStoreDeleteResult> DeleteExactAsync(
            OpaqueStoreDeleteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeDelete?.Invoke(request.Expected);
            if (!TryGet(request.Expected.Reference, out var value))
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.NotFound));
            }

            if (value.Metadata != request.Expected)
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.Conflict));
            }

            if (MakeNextDeleteOutcomeUnknown)
            {
                MakeNextDeleteOutcomeUnknown = false;
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown));
            }

            deleteCalls++;
            if (failDelete || deleteCalls == FailDeleteOnCall)
            {
                return Task.FromResult(OpaqueStoreDeleteResult.Fail(
                    OpaqueStoreFailure.OutcomeUnknown,
                    OpaqueStoreMutationState.OutcomeUnknown));
            }

            objects.Remove(request.Expected.Reference.ObjectId.Value);
            return Task.FromResult(new OpaqueStoreDeleteResult(
                OpaqueStoreFailure.None,
                OpaqueStoreMutationState.Committed));
        }

        internal void Seed(
            OpaqueStoreObjectMetadata metadata,
            byte[] bytes) =>
            objects.Add(
                metadata.Reference.ObjectId.Value,
                (metadata, bytes.ToArray()));

        internal void Clear() => objects.Clear();

        internal ImmutableArray<OpaqueStoreObjectMetadata> MetadataFor(
            OpaqueStoreName name) =>
            objects.Values
                .Select(item => item.Item1)
                .Where(item => item.Reference.Name == name)
                .ToImmutableArray();

        internal byte[] BytesFor(OpaqueStoreObjectMetadata metadata) =>
            objects[metadata.Reference.ObjectId.Value].Item2.ToArray();

        private bool TryGet(
            OpaqueStoreObjectReference reference,
            out (OpaqueStoreObjectMetadata Metadata, byte[] Bytes) value) =>
            objects.TryGetValue(reference.ObjectId.Value, out value) &&
            value.Metadata.Reference == reference;
    }
}

internal sealed class RestrictedStateStoreConformanceDriver(
    IRestrictedStateStore store,
    Func<OpaqueStoreObjectMetadata, Task<OpaqueStoreListResult>>
        observeDuplicateList,
    Func<OpaqueStoreObjectMetadata, Task<OpaqueStoreReadBackResult>>
        observeMissingReadBack,
    Func<OpaqueStoreObjectMetadata, Task<OpaqueStoreDownloadResult>>
        observeExpiredDownload,
    Action makeNextUploadMayCommitted,
    Func<OpaqueStoreObjectMetadata, Task<OpaqueStoreDeleteResult>>
        observeDeleteOutcomeUnknown)
{
    internal IRestrictedStateStore Store => store;

    internal Task<OpaqueStoreListResult> ObserveDuplicateListAsync(
        OpaqueStoreObjectMetadata expected) =>
        observeDuplicateList(expected);

    internal Task<OpaqueStoreReadBackResult> ObserveMissingReadBackAsync(
        OpaqueStoreObjectMetadata expected) =>
        observeMissingReadBack(expected);

    internal Task<OpaqueStoreDownloadResult> ObserveExpiredDownloadAsync(
        OpaqueStoreObjectMetadata expected) =>
        observeExpiredDownload(expected);

    internal void MakeNextUploadMayCommitted() =>
        makeNextUploadMayCommitted();

    internal Task<OpaqueStoreDeleteResult> ObserveDeleteOutcomeUnknownAsync(
        OpaqueStoreObjectMetadata expected) =>
        observeDeleteOutcomeUnknown(expected);
}

internal static class RestrictedStateStoreConformanceHarness
{
    internal static async Task VerifyAsync(
        Func<Func<RestrictedStateStoreConformanceDriver, Task>, Task> withStore)
    {
        await withStore(async driver =>
        {
            var store = driver.Store;
            var bytes = new byte[] { 0, 7, 0xff, 0, 9 };
            var name = new OpaqueStoreName("conformance");
            var upload = Request(name, "operation-1", bytes);
            var uploaded = await store.UploadImmutableAsync(
                upload,
                CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            Assert.Equal(bytes.Length, uploaded.Metadata!.Size);
            Assert.True(uploaded.Metadata.ExpiresAtUnixSeconds >=
                upload.MinimumExpiresAtUnixSeconds);

            var listed = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 8),
                CancellationToken.None);
            Assert.True(listed.Succeeded);
            Assert.True(listed.Complete);
            Assert.Equal(
                uploaded.Metadata.Reference,
                Assert.Single(listed.Objects));
            var duplicate = await driver.ObserveDuplicateListAsync(
                uploaded.Metadata);
            Assert.False(duplicate.Succeeded);

            var metadata = await store.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                CancellationToken.None);
            Assert.True(metadata.Succeeded);
            Assert.Equal(uploaded.Metadata, metadata.Metadata);

            var download = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(metadata.Metadata!, bytes.Length),
                CancellationToken.None);
            Assert.True(download.Succeeded);
            Assert.Equal(bytes, download.EncryptedBytes.ToArray());
            Assert.Equal(
                upload.EncryptedObjectDigest,
                download.Metadata!.EncryptedObjectDigest);
            var expired = await driver.ObserveExpiredDownloadAsync(
                uploaded.Metadata);
            Assert.Equal(OpaqueStoreFailure.Expired, expired.Failure);
            Assert.False(expired.Succeeded);

            var delayed = await driver.ObserveMissingReadBackAsync(
                uploaded.Metadata);
            var persistedReadBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.NotFound, delayed.Failure);
            Assert.True(persistedReadBack.Succeeded);
            Assert.Equal(uploaded.Metadata, persistedReadBack.Metadata);

            var wrongAuthority = uploaded.Metadata with
            {
                ProducingRun = uploaded.Metadata.ProducingRun with
                {
                    Attempt = uploaded.Metadata.ProducingRun.Attempt + 1,
                },
            };
            var wrongReadBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(wrongAuthority),
                CancellationToken.None);
            var wrongRun = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata with
                {
                    ProducingRun = uploaded.Metadata.ProducingRun with
                    {
                        Identity = "other-run",
                    },
                }),
                CancellationToken.None);
            var wrongArchive = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata with
                {
                    ArchiveDigest = new OpaqueStoreArchiveDigest(
                        new string('0', 64)),
                }),
                CancellationToken.None);
            var missingArchive = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata with
                {
                    ArchiveDigest = new OpaqueStoreArchiveDigest(string.Empty),
                }),
                CancellationToken.None);
            var wrongDownload = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    uploaded.Metadata with
                    {
                        EncryptedObjectDigest =
                            new OpaqueStoreEncryptedObjectDigest(
                                new string('0', 64)),
                    },
                    bytes.Length),
                CancellationToken.None);
            var missingEncryptedDigest = await store.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    uploaded.Metadata with
                    {
                        EncryptedObjectDigest =
                            new OpaqueStoreEncryptedObjectDigest(string.Empty),
                    },
                    bytes.Length),
                CancellationToken.None);
            Assert.False(wrongReadBack.Succeeded);
            Assert.False(wrongRun.Succeeded);
            Assert.False(wrongArchive.Succeeded);
            Assert.False(missingArchive.Succeeded);
            Assert.False(wrongDownload.Succeeded);
            Assert.False(missingEncryptedDigest.Succeeded);

            var second = await store.UploadImmutableAsync(
                upload with
                {
                    CorrelationId = new OpaqueStoreCorrelationId("operation-2"),
                },
                CancellationToken.None);
            Assert.True(second.Succeeded);
            Assert.NotEqual(
                uploaded.Metadata.Reference.ObjectId,
                second.Metadata!.Reference.ObjectId);
            var incomplete = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 1),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Incomplete, incomplete.Failure);
            Assert.False(incomplete.Succeeded);

            var maximumBytes = new byte[OpaqueStoreLimits.MaximumObjectBytes];
            maximumBytes[0] = 1;
            maximumBytes[^1] = 2;
            var maximum = await store.UploadImmutableAsync(
                Request(
                    new OpaqueStoreName("conformance-bound"),
                    "maximum",
                    maximumBytes),
                CancellationToken.None);
            var oversizedBytes = new byte[
                OpaqueStoreLimits.MaximumObjectBytes + 1];
            var oversized = await store.UploadImmutableAsync(
                Request(
                    new OpaqueStoreName("conformance-bound"),
                    "oversized",
                    oversizedBytes),
                CancellationToken.None);
            Assert.True(maximum.Succeeded);
            Assert.Equal(
                OpaqueStoreLimits.MaximumObjectBytes,
                maximum.Metadata!.Size);
            Assert.Equal(OpaqueStoreFailure.Invalid, oversized.Failure);

            var mayCommitRequest = Request(
                name,
                "may-commit",
                [3, 1, 4, 1, 5]);
            driver.MakeNextUploadMayCommitted();
            var mayCommit = await store.UploadImmutableAsync(
                mayCommitRequest,
                CancellationToken.None);
            Assert.False(mayCommit.Succeeded);
            Assert.Equal(OpaqueStoreFailure.Io, mayCommit.Failure);
            Assert.Equal(
                OpaqueStoreMutationState.Committed,
                mayCommit.MutationState);
            Assert.NotNull(mayCommit.Metadata);
            var mayCommitReadBack = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(mayCommit.Metadata!),
                CancellationToken.None);
            Assert.True(mayCommitReadBack.Succeeded);

            var unknownDelete = await driver
                .ObserveDeleteOutcomeUnknownAsync(mayCommit.Metadata!);
            Assert.False(unknownDelete.Succeeded);
            Assert.Equal(
                OpaqueStoreFailure.OutcomeUnknown,
                unknownDelete.Failure);
            Assert.Equal(
                OpaqueStoreMutationState.OutcomeUnknown,
                unknownDelete.MutationState);
            var retainedAfterUnknownDelete = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(mayCommit.Metadata!),
                CancellationToken.None);
            Assert.True(retainedAfterUnknownDelete.Succeeded);

            var invalidDigest = await store.UploadImmutableAsync(
                upload with
                {
                    CorrelationId = new OpaqueStoreCorrelationId("invalid"),
                    EncryptedObjectDigest =
                        new OpaqueStoreEncryptedObjectDigest(
                            new string('0', 64)),
                },
                CancellationToken.None);
            var invalidBound = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    name,
                    OpaqueStoreLimits.MaximumObjects + 1),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Invalid, invalidDigest.Failure);
            Assert.Equal(OpaqueStoreFailure.Invalid, invalidBound.Failure);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ListExactAsync(
                    new OpaqueStoreListRequest(name, 8),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DownloadAsync(
                    new OpaqueStoreDownloadRequest(uploaded.Metadata, bytes.Length),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.UploadImmutableAsync(
                    upload with
                    {
                        CorrelationId = new OpaqueStoreCorrelationId("cancelled"),
                    },
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(uploaded.Metadata),
                    cancelled.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(uploaded.Metadata),
                    cancelled.Token));

            foreach (var expected in new[]
            {
                uploaded.Metadata,
                second.Metadata,
                maximum.Metadata,
                mayCommit.Metadata,
            })
            {
                var deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(expected!),
                    CancellationToken.None);
                Assert.True(deleted.Succeeded);
            }

            var missing = await store.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.NotFound, missing.Failure);
        });
    }

    private static OpaqueStoreUploadRequest Request(
        OpaqueStoreName name,
        string correlation,
        byte[] bytes) =>
        new(
            name,
            new OpaqueStoreCorrelationId(correlation),
            bytes,
            new OpaqueStoreEncryptedObjectDigest(
                OpaqueStoreHash.Sha256(bytes)),
            RestrictedStateTestData.Now + 600);
}
