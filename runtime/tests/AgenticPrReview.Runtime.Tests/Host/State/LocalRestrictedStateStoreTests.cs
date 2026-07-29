using System.Collections.Immutable;
using System.Text;
using AgenticPrReview.Runtime.Host.State;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class LocalRestrictedStateStoreTests
{
    [Fact]
    public void ExplicitExistingRootIsRequired()
    {
        var root = Path.Combine(
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
            generation0.EnvelopeSha256);
        var outOfOrder = new RestrictedStateSnapshot(
            [generation0, generation1],
            null);
        var tooMany = new RestrictedStateSnapshot(
            [generation1, generation0, generation0],
            null);

        Assert.False(RestrictedStateValidation.IsValidSnapshot(outOfOrder));
        Assert.False(RestrictedStateValidation.IsValidSnapshot(tooMany));
        Assert.False(RestrictedStateSnapshotCodec.TryWrite(
            outOfOrder,
            out _));
    }

    private static void WithRoot(Action<string> action)
    {
        var root = Path.Combine(
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
}
