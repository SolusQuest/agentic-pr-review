using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.RestrictedStateTransactions;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class LocalRestrictedStateStoreTests
{
    [Fact]
    public async Task SixOperationsRoundTripAcrossFreshInstances()
    {
        await WithRootAsync(async root =>
        {
            var now = RestrictedStateTestData.Now;
            var store = Store(root, now);
            var bytes = new byte[] { 0, 1, 2, 3, 0xff };
            var request = Upload("opaque-name", bytes, now + 600);

            var uploaded = await store.UploadImmutableAsync(
                request,
                CancellationToken.None);

            Assert.True(uploaded.Succeeded);
            Assert.Equal(OpaqueStoreMutationState.Committed, uploaded.MutationState);
            Assert.NotNull(uploaded.Metadata);
            Assert.True(uploaded.Metadata.ExpiresAtUnixSeconds >=
                request.MinimumExpiresAtUnixSeconds);
            Assert.Equal(bytes.Length, uploaded.Metadata.Size);

            var fresh = Store(root, now);
            var listed = await fresh.ListExactAsync(
                new OpaqueStoreListRequest(request.Name, 4),
                CancellationToken.None);
            Assert.True(listed.Succeeded);
            Assert.Equal(uploaded.Metadata.Reference, Assert.Single(listed.Objects));

            var metadata = await fresh.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                CancellationToken.None);
            Assert.True(metadata.Succeeded);
            Assert.Equal(uploaded.Metadata, metadata.Metadata);

            var downloaded = await fresh.DownloadAsync(
                new OpaqueStoreDownloadRequest(
                    metadata.Metadata!,
                    OpaqueStoreLimits.MaximumObjectBytes),
                CancellationToken.None);
            Assert.True(downloaded.Succeeded);
            Assert.Equal(bytes, downloaded.EncryptedBytes.ToArray());
            Assert.Equal(
                OpaqueStoreHash.Sha256(bytes),
                downloaded.Metadata!.EncryptedObjectDigest.Sha256);

            var readBack = await fresh.ReadBackExactAsync(
                new OpaqueStoreReadBackRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.True(readBack.Succeeded);
            Assert.Equal(metadata.Metadata, readBack.Metadata);

            var deleted = await fresh.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(uploaded.Metadata),
                CancellationToken.None);
            Assert.True(
                deleted.Succeeded,
                $"{deleted.Failure}/{deleted.MutationState}");
            var missing = await fresh.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata.Reference),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.NotFound, missing.Failure);
        });
    }

    [Fact]
    public async Task ExactMaximumObjectAndExpiryAreAcceptedWithoutOverflow()
    {
        await WithRootAsync(async root =>
        {
            var bytes = new byte[OpaqueStoreLimits.MaximumObjectBytes];
            bytes[0] = 1;
            bytes[^1] = 2;
            var store = Store(root, RestrictedStateTestData.Now);

            var result = await store.UploadImmutableAsync(
                Upload(
                    "maximum-object",
                    bytes,
                    RestrictedStateFormat.MaximumUnixSeconds),
                CancellationToken.None);

            Assert.True(result.Succeeded);
            Assert.Equal(
                RestrictedStateFormat.MaximumUnixSeconds,
                result.Metadata!.ExpiresAtUnixSeconds);
            Assert.Equal(bytes.Length, result.Metadata.Size);
        });
    }

    [Fact]
    public async Task InvalidDigestAndOverBoundRequestsFailBeforeMutation()
    {
        await WithRootAsync(async root =>
        {
            var store = Store(root, RestrictedStateTestData.Now);
            var invalid = Upload("digest", [1, 2, 3], RestrictedStateTestData.Expires)
                with
            {
                EncryptedObjectDigest = new OpaqueStoreEncryptedObjectDigest(
                        new string('0', 64)),
            };

            var digest = await store.UploadImmutableAsync(
                invalid,
                CancellationToken.None);
            var overBound = await store.ListExactAsync(
                new OpaqueStoreListRequest(
                    new OpaqueStoreName(new string('n', 257)),
                    1),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Invalid, digest.Failure);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, digest.MutationState);
            Assert.Equal(OpaqueStoreFailure.Invalid, overBound.Failure);
            Assert.Empty(Directory.EnumerateFiles(root));
        });
    }

    [Fact]
    public async Task EveryOperationHonorsPreCancelledToken()
    {
        await WithRootAsync(async root =>
        {
            var store = Store(root, RestrictedStateTestData.Now);
            var uploaded = await store.UploadImmutableAsync(
                Upload("cancel", [9], RestrictedStateTestData.Expires),
                CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            var metadata = uploaded.Metadata!;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ListExactAsync(
                    new OpaqueStoreListRequest(metadata.Reference.Name, 4),
                    cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(metadata.Reference),
                    cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DownloadAsync(
                    new OpaqueStoreDownloadRequest(metadata, 4),
                    cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.UploadImmutableAsync(
                    Upload("cancel-2", [8], RestrictedStateTestData.Expires),
                    cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(metadata),
                    cancellation.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(metadata),
                    cancellation.Token));
        });
    }

    [Fact]
    public async Task CorruptPersistedRecordFailsClosed()
    {
        await WithRootAsync(async root =>
        {
            var store = Store(root, RestrictedStateTestData.Now);
            var uploaded = await store.UploadImmutableAsync(
                Upload("corrupt", [1, 2, 3], RestrictedStateTestData.Expires),
                CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            var path = Assert.Single(Directory.GetFiles(root, "*.aprobject"));
            await using (var stream = new FileStream(
                path,
                FileMode.Append,
                FileAccess.Write,
                FileShare.None,
                1,
                FileOptions.Asynchronous))
            {
                await stream.WriteAsync(new byte[] { 4 });
            }

            var metadata = await store.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata!.Reference),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Invalid, metadata.Failure);
            Assert.False(metadata.Succeeded);
        });
    }

    [Fact]
    public async Task DurabilityAndDeleteAmbiguityReportCommittedTruth()
    {
        await WithRootAsync(async root =>
        {
            var uploadStore = new LocalRestrictedStateStore(
                root,
                syncDirectoryTestHook: _ => false,
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));
            var upload = await uploadStore.UploadImmutableAsync(
                Upload("durability", [1], RestrictedStateTestData.Expires),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Io, upload.Failure);
            Assert.Equal(OpaqueStoreMutationState.Committed, upload.MutationState);
            Assert.NotNull(upload.Metadata);

            var deleteStore = new LocalRestrictedStateStore(
                root,
                deleteTemporaryTestHook: _ => false,
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));
            var deletion = await deleteStore.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(upload.Metadata),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Cleanup, deletion.Failure);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, deletion.MutationState);

            var stillPresent = await Store(root, RestrictedStateTestData.Now)
                .ReadBackExactAsync(
                    new OpaqueStoreReadBackRequest(upload.Metadata),
                    CancellationToken.None);
            Assert.True(stillPresent.Succeeded);
        });
    }

    [Fact]
    public async Task EncryptedTransactionIndexReconstructsSnapshotWithoutCanaries()
    {
        await WithRootAsync(async root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var candidate = RestrictedStateTestData.Candidate(
                access,
                keys,
                plaintext: "repository workflow session provider model generation predecessor"u8.ToArray());
            var replacement = new RestrictedStateSnapshot([candidate], null);
            var coordinator = new RestrictedStateOpaqueSnapshotStore(
                Store(root, RestrictedStateTestData.Now),
                keys);

            var written = await coordinator.CompareExchangeAsync(
                access,
                RestrictedStateSnapshotVersion.Absent,
                replacement,
                CancellationToken.None);

            Assert.True(written.Committed);
            Assert.Equal(RestrictedStateStoreFailure.None, written.Failure);
            var fresh = new RestrictedStateOpaqueSnapshotStore(
                Store(root, RestrictedStateTestData.Now),
                keys);
            var read = await fresh.ReadAsync(access, CancellationToken.None);
            Assert.Equal(RestrictedStateStoreFailure.None, read.Failure);
            Assert.NotNull(read.Snapshot);
            var restored = Assert.Single(read.Snapshot.Accepted);
            Assert.Equal(candidate.Binding, restored.Binding);
            Assert.Equal(candidate.SessionSha256, restored.SessionSha256);
            Assert.Equal(candidate.EnvelopeSha256, restored.EnvelopeSha256);
            Assert.Equal(candidate.ObjectIdentity, restored.ObjectIdentity);
            Assert.Equal(candidate.Envelope, restored.Envelope);
            Assert.Equal(replacement.Staging, read.Snapshot.Staging);
            Assert.True(read.Version!.Exists);

            var raw = Directory.GetFiles(root, "*.aprobject")
                .SelectMany(File.ReadAllBytes)
                .ToArray();
            var persisted = System.Text.Encoding.UTF8.GetString(raw);
            foreach (var canary in new[]
            {
                access.Scope.RepositoryId,
                access.Scope.WorkflowIdentity,
                access.Scope.SessionId,
                access.Scope.ProviderId,
                access.Scope.ModelId,
                "generation",
                "predecessor",
            })
            {
                Assert.DoesNotContain(canary, persisted, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task MaximumRetainedSnapshotUsesBoundedImmutableObjectsAndRejectsStaleWriter()
    {
        await WithRootAsync(async root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var generation0 = RestrictedStateTestData.Candidate(
                access,
                keys,
                generation: 0,
                predecessor: null,
                plaintext: [1]);
            var generation1 = RestrictedStateTestData.Candidate(
                access,
                keys,
                generation: 1,
                predecessor: generation0.EnvelopeSha256,
                plaintext: [2]);
            var generation2 = RestrictedStateTestData.Candidate(
                access,
                keys,
                generation: 2,
                predecessor: generation1.EnvelopeSha256,
                plaintext: [3]);
            var maximum = new RestrictedStateSnapshot(
                [generation1, generation0],
                generation2);
            Assert.True(RestrictedStateValidation.IsValidSnapshot(maximum));

            var first = new RestrictedStateOpaqueSnapshotStore(
                Store(root, RestrictedStateTestData.Now),
                keys);
            var stale = new RestrictedStateOpaqueSnapshotStore(
                Store(root, RestrictedStateTestData.Now),
                keys);
            var firstRead = await first.ReadAsync(
                access,
                CancellationToken.None);
            var staleRead = await stale.ReadAsync(
                access,
                CancellationToken.None);

            var written = await first.CompareExchangeAsync(
                access,
                firstRead.Version!,
                maximum,
                CancellationToken.None);
            var rejected = await stale.CompareExchangeAsync(
                access,
                staleRead.Version!,
                new RestrictedStateSnapshot([generation0], null),
                CancellationToken.None);

            Assert.True(written.Committed);
            Assert.Equal(RestrictedStateStoreFailure.Conflict, rejected.Failure);
            Assert.False(rejected.Committed);
            var restored = await new RestrictedStateOpaqueSnapshotStore(
                    Store(root, RestrictedStateTestData.Now),
                    keys)
                .ReadAsync(access, CancellationToken.None);
            Assert.Equal(2, restored.Snapshot!.Accepted.Length);
            Assert.NotNull(restored.Snapshot.Staging);

            var records = Directory.GetFiles(root, "*.aprobject");
            Assert.Equal(4, records.Length);
            foreach (var path in records)
            {
                Assert.True(LocalOpaqueStoreRecordCodec.TryRead(
                    File.ReadAllBytes(path),
                    out _,
                    out var payload));
                Assert.InRange(
                    payload.Length,
                    1,
                    OpaqueStoreLimits.MaximumObjectBytes);
            }
        });
    }

    [Fact]
    public async Task RootSwapBeforeWriteFailsWithoutTouchingOutsideFiles()
    {
        await WithLinuxSwapRootsAsync(async (root, displaced, outside) =>
        {
            var sentinel = Path.Join(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "outside");
            var store = new LocalRestrictedStateStore(
                root,
                beforeWriteTestHook: () => SwapRoot(root, displaced, outside),
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));

            var result = await store.UploadImmutableAsync(
                Upload("root-swap-before", [1], RestrictedStateTestData.Expires),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Invalid, result.Failure);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, result.MutationState);
            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
            Assert.Empty(Directory.GetFiles(displaced, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(displaced, "*.tmp"));
            Assert.Empty(Directory.GetFiles(outside, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(outside, "*.tmp"));
        });
    }

    [Fact]
    public async Task RootSwapAfterFinalProofRollsBackUpload()
    {
        await WithLinuxSwapRootsAsync(async (root, displaced, outside) =>
        {
            var sentinel = Path.Join(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "outside");
            var store = new LocalRestrictedStateStore(
                root,
                afterFinalRootProofTestHook: () =>
                    SwapRoot(root, displaced, outside),
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));

            var result = await store.UploadImmutableAsync(
                Upload("root-swap-final", [2], RestrictedStateTestData.Expires),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Invalid, result.Failure);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, result.MutationState);
            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
            Assert.Empty(Directory.GetFiles(displaced, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(displaced, "*.tmp"));
            Assert.Empty(Directory.GetFiles(outside, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(outside, "*.tmp"));
        });
    }

    [Fact]
    public async Task RootSwapBeforeDeleteLeavesOriginalObjectAndOutsideFilesUntouched()
    {
        await WithLinuxSwapRootsAsync(async (root, displaced, outside) =>
        {
            var sentinel = Path.Join(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "outside");
            var uploaded = await Store(root, RestrictedStateTestData.Now)
                .UploadImmutableAsync(
                    Upload("root-swap-delete", [3], RestrictedStateTestData.Expires),
                    CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            var store = new LocalRestrictedStateStore(
                root,
                afterFinalRootProofTestHook: () =>
                    SwapRoot(root, displaced, outside),
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));

            var result = await store.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(uploaded.Metadata!),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Invalid, result.Failure);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, result.MutationState);
            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
            Assert.Single(Directory.GetFiles(displaced, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(displaced, "*.delete"));
            Assert.Empty(Directory.GetFiles(outside, "*.aprobject"));
            Assert.Empty(Directory.GetFiles(outside, "*.delete"));
        });
    }

    [Fact]
    public async Task LiveSymlinkAtObjectPathIsRejectedWithoutReadingTarget()
    {
        await WithLinuxSwapRootsAsync(async (root, _, outside) =>
        {
            var sentinel = Path.Join(outside, "sentinel.txt");
            await File.WriteAllTextAsync(sentinel, "outside");
            var store = Store(root, RestrictedStateTestData.Now);
            var uploaded = await store.UploadImmutableAsync(
                Upload("object-symlink", [4], RestrictedStateTestData.Expires),
                CancellationToken.None);
            Assert.True(uploaded.Succeeded);
            var objectPath = Assert.Single(
                Directory.GetFiles(root, "*.aprobject"));
            File.Delete(objectPath);
            File.CreateSymbolicLink(objectPath, sentinel);

            var result = await store.ReadMetadataAsync(
                new OpaqueStoreMetadataRequest(uploaded.Metadata!.Reference),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Invalid, result.Failure);
            Assert.Equal("outside", await File.ReadAllTextAsync(sentinel));
        });
    }

    [Fact]
    public async Task OpaqueObjectIdsNeverBecomeFilesystemPathSyntax()
    {
        await WithParentRootAsync(async (parent, root) =>
        {
            var name = new OpaqueStoreName("path-identities");
            var bytes = new byte[] { 7 };
            var maliciousId = new OpaqueStoreObjectId(
                "slot/../../outside-object");
            var reference = new OpaqueStoreObjectReference(name, maliciousId);
            var metadata = new OpaqueStoreObjectMetadata(
                reference,
                new OpaqueStoreProducingRun("run", 1),
                new OpaqueStoreArchiveDigest(OpaqueStoreHash.Sha256(bytes)),
                new OpaqueStoreEncryptedObjectDigest(
                    OpaqueStoreHash.Sha256(bytes)),
                RestrictedStateTestData.Expires,
                bytes.Length);
            Assert.True(LocalOpaqueStoreRecordCodec.TryWrite(
                metadata,
                bytes,
                out var record));
            var prefix = string.Concat(
                "opaque-",
                OpaqueStoreHash.Sha256(
                    System.Text.Encoding.UTF8.GetBytes(name.Value)),
                "-");
            Directory.CreateDirectory(Path.Join(root, prefix + "slot"));
            var outside = Path.Join(parent, "outside-object.aprobject");
            await File.WriteAllBytesAsync(outside, record);
            var crafted = Path.Join(root, prefix + "crafted.aprobject");
            await File.WriteAllBytesAsync(crafted, record);
            var store = Store(root, RestrictedStateTestData.Now);

            foreach (var objectId in new[]
            {
                "../outside",
                "slash/value",
                @"backslash\value",
                @"C:\drive",
                @"\\server\share",
                "scheme://value",
                "nul\0value",
                maliciousId.Value,
            })
            {
                var item = metadata with
                {
                    Reference = reference with
                    {
                        ObjectId = new OpaqueStoreObjectId(objectId),
                    },
                };
                var read = await store.ReadMetadataAsync(
                    new OpaqueStoreMetadataRequest(item.Reference),
                    CancellationToken.None);
                var deleted = await store.DeleteExactAsync(
                    new OpaqueStoreDeleteRequest(item),
                    CancellationToken.None);
                Assert.Equal(OpaqueStoreFailure.NotFound, read.Failure);
                Assert.Equal(OpaqueStoreFailure.NotFound, deleted.Failure);
            }

            var listed = await store.ListExactAsync(
                new OpaqueStoreListRequest(name, 8),
                CancellationToken.None);
            Assert.Equal(OpaqueStoreFailure.Invalid, listed.Failure);
            Assert.Equal(record, await File.ReadAllBytesAsync(outside));
        });
    }

    [Fact]
    public async Task ExactDeleteRejectsObjectSubstitutedAfterValidation()
    {
        await WithRootAsync(async root =>
        {
            var initial = Store(root, RestrictedStateTestData.Now);
            var expected = await initial.UploadImmutableAsync(
                Upload("substitution", [1], RestrictedStateTestData.Expires),
                CancellationToken.None);
            var replacement = await initial.UploadImmutableAsync(
                Upload("substitution", [2], RestrictedStateTestData.Expires),
                CancellationToken.None);
            Assert.True(expected.Succeeded);
            Assert.True(replacement.Succeeded);
            var expectedPath = FindRecord(root, expected.Metadata!);
            var replacementPath = FindRecord(root, replacement.Metadata!);
            var displacedPath = Path.Join(root, "displaced.aprobject");
            var deleteStore = new LocalRestrictedStateStore(
                root,
                afterFinalRootProofTestHook: () =>
                {
                    File.Move(expectedPath, displacedPath);
                    File.Move(replacementPath, expectedPath);
                },
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));

            var deleted = await deleteStore.DeleteExactAsync(
                new OpaqueStoreDeleteRequest(expected.Metadata!),
                CancellationToken.None);

            Assert.Equal(OpaqueStoreFailure.Conflict, deleted.Failure);
            Assert.Equal(
                OpaqueStoreMutationState.NotCommitted,
                deleted.MutationState);
            var payloads = Directory.GetFiles(root, "*.aprobject")
                .Select(path =>
                {
                    Assert.True(LocalOpaqueStoreRecordCodec.TryRead(
                        File.ReadAllBytes(path),
                        out _,
                        out var payload));
                    return Assert.Single(payload.ToArray());
                })
                .Order()
                .ToArray();
            Assert.Equal(new byte[] { 1, 2 }, payloads);
        });
    }

    [Fact]
    public async Task PartialPhysicalDeletionCannotCommitAggregateDelete()
    {
        await WithRootAsync(async root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var candidate = RestrictedStateTestData.Candidate(access, keys);
            var initial = new RestrictedStateOpaqueSnapshotStore(
                Store(root, RestrictedStateTestData.Now),
                keys);
            var written = await initial.CompareExchangeAsync(
                access,
                RestrictedStateSnapshotVersion.Absent,
                new RestrictedStateSnapshot([candidate], null),
                CancellationToken.None);
            Assert.True(written.Committed);
            var deletes = 0;
            var failing = new LocalRestrictedStateStore(
                root,
                deleteTemporaryTestHook: path =>
                {
                    if (!path.EndsWith(
                            ".delete",
                            StringComparison.Ordinal))
                    {
                        File.Delete(path);
                        return true;
                    }

                    deletes++;
                    if (deletes == 2)
                    {
                        return false;
                    }

                    File.Delete(path);
                    return true;
                },
                timeProvider: new FrozenTimeProvider(RestrictedStateTestData.Now));
            var coordinator = new RestrictedStateOpaqueSnapshotStore(
                failing,
                keys);
            var raw = await coordinator.ReadRawVersionAsync(
                access,
                CancellationToken.None);

            var result = await coordinator.CompareDeleteRawAsync(
                access,
                raw.Version!,
                CancellationToken.None);

            Assert.False(result.Committed);
            Assert.Equal(RestrictedStateStoreFailure.Cleanup, result.Failure);
            Assert.Single(Directory.GetFiles(root, "*.aprobject"));
        });
    }

    private static LocalRestrictedStateStore Store(string root, long now) =>
        new(root, timeProvider: new FrozenTimeProvider(now));

    private static OpaqueStoreUploadRequest Upload(
        string name,
        byte[] bytes,
        long minimumExpiry) =>
        new(
            new OpaqueStoreName(name),
            new OpaqueStoreCorrelationId(Guid.NewGuid().ToString("N")),
            bytes,
            new OpaqueStoreEncryptedObjectDigest(
                OpaqueStoreHash.Sha256(bytes)),
            minimumExpiry);

    private static async Task WithRootAsync(Func<string, Task> action)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-state-{Guid.NewGuid():N}");
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

    private static async Task WithLinuxSwapRootsAsync(
        Func<string, string, string, Task> action)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var parent = Path.Join(
            Path.GetTempPath(),
            $"apr-state-swap-{Guid.NewGuid():N}");
        var root = Path.Join(parent, "root");
        var displaced = Path.Join(parent, "displaced");
        var outside = Path.Join(parent, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            await action(root, displaced, outside);
        }
        finally
        {
            if (Directory.Exists(root) &&
                (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            {
                Directory.Delete(root);
            }

            Directory.Delete(parent, recursive: true);
        }
    }

    private static void SwapRoot(
        string root,
        string displaced,
        string outside)
    {
        Directory.Move(root, displaced);
        Directory.CreateSymbolicLink(root, outside);
    }

    private static async Task WithParentRootAsync(
        Func<string, string, Task> action)
    {
        var parent = Path.Join(
            Path.GetTempPath(),
            $"apr-state-parent-{Guid.NewGuid():N}");
        var root = Path.Join(parent, "root");
        Directory.CreateDirectory(root);
        try
        {
            await action(parent, root);
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    private static string FindRecord(
        string root,
        OpaqueStoreObjectMetadata expected) =>
        Directory.GetFiles(root, "*.aprobject").Single(path =>
            LocalOpaqueStoreRecordCodec.TryRead(
                File.ReadAllBytes(path),
                out var metadata,
                out _) &&
            metadata == expected);

    private sealed class FrozenTimeProvider(long unixSeconds) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
    }
}
