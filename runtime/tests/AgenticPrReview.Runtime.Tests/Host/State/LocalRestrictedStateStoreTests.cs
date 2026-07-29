using System.Collections.Immutable;
using System.Runtime.Versioning;
using System.Text;
using AgenticPrReview.Runtime.Agent;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class LocalRestrictedStateStoreTests
{
    [Fact]
    public void ExplicitExistingRootIsRequired()
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-state-missing-{Guid.NewGuid():N}");
        var store = new LocalRestrictedStateStore(root);

        var read = store.Read(
            RestrictedStateTestData.Access(),
            CancellationToken.None);

        Assert.Equal(
            RestrictedStateStoreFailure.Invalid,
            read.Failure);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void SnapshotRoundTripsAcrossFreshStoreInstance()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var candidate = RestrictedStateTestData.Candidate(
                access,
                keys);
            var first = new LocalRestrictedStateStore(root);
            var initial = first.Read(access, CancellationToken.None);
            Assert.True(initial.Succeeded);
            Assert.False(initial.Version!.Exists);

            var write = first.CompareExchange(
                access,
                initial.Version,
                new RestrictedStateSnapshot([candidate], null),
                CancellationToken.None);
            Assert.True(write.Succeeded);

            var restarted = new LocalRestrictedStateStore(root);
            var restored = restarted.Read(
                access,
                CancellationToken.None);
            Assert.True(restored.Succeeded);
            var actual = Assert.Single(restored.Snapshot!.Accepted);
            Assert.Equal(candidate.Binding, actual.Binding);
            Assert.Equal(candidate.SessionSha256, actual.SessionSha256);
            Assert.Equal(candidate.EnvelopeSha256, actual.EnvelopeSha256);
            Assert.Equal(candidate.Envelope, actual.Envelope);
            Assert.Null(restored.Snapshot.Staging);
        });
    }

    [Fact]
    public void StaleWriterConflictsWithoutReplacingCommittedSnapshot()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var first = new LocalRestrictedStateStore(root);
            var second = new LocalRestrictedStateStore(root);
            var firstRead = first.Read(access, CancellationToken.None);
            var staleRead = second.Read(access, CancellationToken.None);
            var candidate = RestrictedStateTestData.Candidate(
                access,
                keys);

            Assert.True(first.CompareExchange(
                access,
                firstRead.Version!,
                new RestrictedStateSnapshot([], candidate),
                CancellationToken.None).Succeeded);
            var staleWrite = second.CompareExchange(
                access,
                staleRead.Version!,
                RestrictedStateSnapshot.Empty,
                CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Conflict,
                staleWrite.Failure);
            var visible = first.Read(access, CancellationToken.None);
            Assert.Equal(
                candidate.EnvelopeSha256,
                visible.Snapshot!.Staging!.EnvelopeSha256);
        });
    }

    [Fact]
    public void CorruptPartialTrailingAndOversizedSnapshotsFailClosed()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None).Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            var valid = File.ReadAllBytes(path);

            foreach (var bytes in new[]
            {
                valid[..^1],
                valid.Concat(new byte[] { 0 }).ToArray(),
                new byte[
                    RestrictedStateSnapshotCodec.MaximumSnapshotBytes + 1],
            })
            {
                File.WriteAllBytes(path, bytes);
                var read = store.Read(access, CancellationToken.None);
                Assert.Equal(
                    RestrictedStateStoreFailure.Invalid,
                    read.Failure);
            }
        });
    }

    [Fact]
    public void SnapshotNameIsOpaqueAndPlaintextIsEncrypted()
    {
        WithRoot(root =>
        {
            const string canary = "SYNTHETIC-SESSION-CANARY";
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var candidate = RestrictedStateTestData.Candidate(
                access,
                keys,
                plaintext: Encoding.UTF8.GetBytes(canary));
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot([candidate], null),
                CancellationToken.None).Succeeded);

            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            Assert.Matches(
                "^scope-[0-9a-f]{64}\\.aprstate$",
                Path.GetFileName(path));
            Assert.DoesNotContain(
                canary,
                Encoding.UTF8.GetString(File.ReadAllBytes(path)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                access.Scope.RepositoryId,
                Path.GetFileName(path),
                StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SnapshotCodecRejectsOutOfOrderAndOverLimitSets()
    {
        var access = RestrictedStateTestData.Access();
        var keys = new TestKeyResolver();
        var generation0 = RestrictedStateTestData.Candidate(
            access,
            keys);
        var generation1 = RestrictedStateTestData.Candidate(
            access,
            keys,
            1,
            generation0.EnvelopeSha256,
            plaintext: [2]);
        var generation2 = RestrictedStateTestData.Candidate(
            access,
            keys,
            2,
            generation1.EnvelopeSha256,
            plaintext: [3]);
        var outOfOrder = new RestrictedStateSnapshot(
            [generation0, generation1],
            null);
        var tooMany = new RestrictedStateSnapshot(
            [generation2, generation1, generation0],
            null);
        var duplicate = new RestrictedStateSnapshot(
            [generation1, generation1],
            null);
        var nonAdjacent = new RestrictedStateSnapshot(
            [generation2, generation0],
            null);
        var illegalStaging = new RestrictedStateSnapshot(
            [generation0],
            generation2);

        Assert.False(RestrictedStateValidation.IsValidSnapshot(outOfOrder));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(tooMany));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(duplicate));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(nonAdjacent));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(
            illegalStaging));
        Assert.False(RestrictedStateSnapshotCodec.TryWrite(
            outOfOrder,
            out _));
    }

    [Fact]
    public void CompareDeletePhysicallyRemovesSnapshotAndReturnsAbsentVersion()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            var write = store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None);
            Assert.True(write.Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));

            var deleted = store.CompareDelete(
                access,
                write.Version!,
                CancellationToken.None);

            Assert.True(deleted.Succeeded);
            Assert.Equal(
                RestrictedStateSnapshotVersion.Absent,
                deleted.Version);
            Assert.False(File.Exists(path));
            var after = store.Read(access, CancellationToken.None);
            Assert.True(after.Succeeded);
            Assert.False(after.Version!.Exists);
        });
    }

    [Fact]
    public void DirectoryAtScopePathIsUnsafeRatherThanAbsent()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None).Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            File.Delete(path);
            Directory.CreateDirectory(path);

            var read = store.Read(access, CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Invalid,
                read.Failure);
        });
    }

    [Fact]
    public void DanglingSymlinkAtScopePathIsUnsafeRatherThanAbsent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None).Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            File.Delete(path);
            File.CreateSymbolicLink(
                path,
                Path.Join(root, "missing-target"));

            var read = store.Read(access, CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Invalid,
                read.Failure);
        });
    }

    [Fact]
    public void RootSwapBeforeWriteFailsWithoutTouchingOutsideSentinel()
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
        var sentinel = Path.Join(outside, "sentinel");
        File.WriteAllText(sentinel, "unchanged");
        try
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var swapped = false;
            var store = new LocalRestrictedStateStore(
                root,
                () =>
                {
                    if (swapped)
                    {
                        return;
                    }

                    swapped = true;
                    Directory.Move(root, displaced);
                    Directory.CreateSymbolicLink(root, outside);
                });
            var initial = store.Read(access, CancellationToken.None);

            var write = store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Invalid,
                write.Failure);
            Assert.Equal("unchanged", File.ReadAllText(sentinel));
            Assert.Empty(
                Directory.GetFiles(outside, "scope-*.aprstate"));
            Assert.Empty(
                Directory.GetFiles(displaced, "*.tmp"));
        }
        finally
        {
            Directory.Delete(parent, recursive: true);
        }
    }

    [Fact]
    public void FullLogicalScopeByteLimitIsExactIncludingMetadata()
    {
        var access = RestrictedStateTestData.Access();
        var generation0 = CandidateWithEnvelope(
            access,
            RestrictedStateTestData.Binding(scope: access.Scope),
            new byte[AgentLimits.StateEnvelopeBytes],
            new string('a', 64));
        var generation1Binding = RestrictedStateTestData.Binding(
            1,
            generation0.EnvelopeSha256,
            access.Scope);
        var generation1 = CandidateWithEnvelope(
            access,
            generation1Binding,
            Enumerable.Repeat(
                    (byte)1,
                    AgentLimits.StateEnvelopeBytes)
                .ToArray(),
            new string('b', 64));
        var stagingBinding = RestrictedStateTestData.Binding(
            2,
            generation1.EnvelopeSha256,
            access.Scope);
        var provisional = CandidateWithEnvelope(
            access,
            stagingBinding,
            [2],
            new string('c', 64));
        var metadata =
            RestrictedStateValidation.EstimateMetadataBytes(generation0) +
            RestrictedStateValidation.EstimateMetadataBytes(generation1) +
            RestrictedStateValidation.EstimateMetadataBytes(provisional);
        var stagingLength =
            AgentLimits.StateScopeTotalBytes -
            (2 * AgentLimits.StateEnvelopeBytes) -
            metadata;
        var exactStaging = CandidateWithEnvelope(
            access,
            stagingBinding,
            Enumerable.Repeat((byte)2, stagingLength).ToArray(),
            provisional.SessionSha256);
        var exact = new RestrictedStateSnapshot(
            [generation1, generation0],
            exactStaging);
        var overStaging = CandidateWithEnvelope(
            access,
            stagingBinding,
            Enumerable.Repeat((byte)2, stagingLength + 1).ToArray(),
            provisional.SessionSha256);

        Assert.True(RestrictedStateValidation.IsValidSnapshot(exact));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(
            exact with { Staging = overStaging }));
    }

    [Fact]
    public void PrecommitCleanupFailurePreservesOldCurrent()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var initialStore = new LocalRestrictedStateStore(root);
            var absent = initialStore.Read(
                access,
                CancellationToken.None);
            var current = RestrictedStateTestData.Candidate(access, keys);
            var committed = initialStore.CompareExchange(
                access,
                absent.Version!,
                new RestrictedStateSnapshot([current], null),
                CancellationToken.None);
            Assert.True(committed.Succeeded);
            using var cancellation = new CancellationTokenSource();
            var failing = new LocalRestrictedStateStore(
                root,
                afterTemporaryFlushTestHook: cancellation.Cancel,
                deleteTemporaryTestHook: _ => false);
            var staging = RestrictedStateTestData.Candidate(
                access,
                keys,
                1,
                current.EnvelopeSha256,
                plaintext: [2]);

            var write = failing.CompareExchange(
                access,
                committed.Version!,
                new RestrictedStateSnapshot([current], staging),
                cancellation.Token);

            Assert.Equal(
                RestrictedStateStoreFailure.Cleanup,
                write.Failure);
            Assert.False(write.Committed);
            var visible = initialStore.Read(
                access,
                CancellationToken.None);
            Assert.Equal(
                current.EnvelopeSha256,
                Assert.Single(visible.Snapshot!.Accepted)
                    .EnvelopeSha256);
            Assert.Null(visible.Snapshot.Staging);
        });
    }

    [Fact]
    public void DirectorySyncFailureReportsCommittedOutcome()
    {
        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(
                root,
                syncDirectoryTestHook: _ => false);
            var initial = store.Read(access, CancellationToken.None);
            var candidate = RestrictedStateTestData.Candidate(
                access,
                keys);

            var write = store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot([candidate], null),
                CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Io,
                write.Failure);
            Assert.True(write.Committed);
            var visible = new LocalRestrictedStateStore(root).Read(
                access,
                CancellationToken.None);
            Assert.Equal(
                candidate.EnvelopeSha256,
                Assert.Single(visible.Snapshot!.Accepted)
                    .EnvelopeSha256);
        });
    }

    [Fact]
    public void LiveSymlinkAtScopePathIsRejectedWithoutReadingTarget()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None).Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            File.Delete(path);
            var outside = Path.Join(root, "outside-sentinel");
            File.WriteAllText(outside, "unchanged");
            File.CreateSymbolicLink(path, outside);

            var read = store.Read(access, CancellationToken.None);

            Assert.Equal(
                RestrictedStateStoreFailure.Invalid,
                read.Failure);
            Assert.Equal("unchanged", File.ReadAllText(outside));
        });
    }

    [Fact]
    [SupportedOSPlatform("linux")]
    public void InaccessibleScopeFileIsIoFailureRatherThanAbsent()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        WithRoot(root =>
        {
            var access = RestrictedStateTestData.Access();
            var keys = new TestKeyResolver();
            var store = new LocalRestrictedStateStore(root);
            var initial = store.Read(access, CancellationToken.None);
            Assert.True(store.CompareExchange(
                access,
                initial.Version!,
                new RestrictedStateSnapshot(
                    [RestrictedStateTestData.Candidate(access, keys)],
                    null),
                CancellationToken.None).Succeeded);
            var path = Assert.Single(
                Directory.GetFiles(root, "scope-*.aprstate"));
            var originalMode = File.GetUnixFileMode(path);
            try
            {
                File.SetUnixFileMode(path, UnixFileMode.None);

                var read = store.Read(access, CancellationToken.None);

                Assert.Equal(
                    RestrictedStateStoreFailure.Io,
                    read.Failure);
            }
            finally
            {
                File.SetUnixFileMode(path, originalMode);
            }
        });
    }

    private static void WithRoot(Action<string> action)
    {
        var root = Path.Join(
            Path.GetTempPath(),
            $"apr-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            action(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RestrictedStateCandidate CandidateWithEnvelope(
        AuthorizedStateAccess access,
        RestrictedStateBinding binding,
        byte[] envelope,
        string sessionSha)
    {
        Assert.Equal(access.Scope, binding.Scope);
        var envelopeSha =
            RestrictedStateEnvelope.EnvelopeSha256(envelope);
        return new RestrictedStateCandidate(
            binding,
            sessionSha,
            envelopeSha,
            RestrictedStateEnvelope.ObjectIdentity(
                binding,
                sessionSha,
                envelopeSha),
            envelope);
    }
}
