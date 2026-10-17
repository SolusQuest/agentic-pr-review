using AgenticPrReview.Runtime.Host.State;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Transactions;

namespace AgenticPrReview.Runtime.Tests.Host.State;

public sealed class SolusContextClaimTests
{
    [Fact]
    public void GrantHasStableCanonicalVectorAndCannotBeRebound()
    {
        var claim = Claim();
        Assert.Equal(Guid.Parse("817ed35d-6839-987c-bd0a-e3d0477b3b22"), claim.GrantId);
        Assert.True(SolusContextClaimRecordCodec.IsValid(claim));
        Assert.False(SolusContextClaimRecordCodec.IsValid(claim with { Epoch = Hash('9') }));
        Assert.False(SolusContextClaimRecordCodec.IsValid(claim with
        {
            LogicalGenerationIdentity = Hash('9'),
        }));
        Assert.False(SolusContextClaimRecordCodec.IsValid(claim with
        {
            Source = claim.Source with { LogicalWorkId = Id(9) },
        }));
        Assert.False(SolusContextClaimRecordCodec.IsValid(claim with { ExecutionId = Id(9) }));
        Assert.False(SolusContextClaimRecordCodec.IsValid(claim with { RoundId = Id(9) }));
    }

    [Fact]
    public void CanonicalRecordRejectsEveryTruncationAndTrailingData()
    {
        var claim = Claim();
        Assert.True(SolusContextClaimRecordCodec.TryEncode(claim, out var bytes));
        Assert.True(SolusContextClaimRecordCodec.TryDecode(bytes, out var decoded));
        Assert.Equal(claim, decoded);
        for (var length = 0; length < bytes.Length; length++)
            Assert.False(SolusContextClaimRecordCodec.TryDecode(bytes.AsSpan(0, length), out _));
        Assert.False(SolusContextClaimRecordCodec.TryDecode([.. bytes, 0], out _));
        bytes[4] ^= 1;
        Assert.False(SolusContextClaimRecordCodec.TryDecode(bytes, out _));
    }

    [Fact]
    public void SourceRoundConflictsDespiteDifferentCheckpointAndGrant()
    {
        var first = Claim();
        var competing = Regrant(first with
        {
            Source = first.Source with { CheckpointId = Id(8) },
            ExecutionId = Id(9),
        });
        Assert.True(SolusContextClaimRecordCodec.IsValid(competing));
        Assert.NotEqual(first.GrantId, competing.GrantId);
        Assert.True(first.ConflictsWith(competing));
        var unrelated = Regrant(competing with
        {
            Source = competing.Source with { LogicalWorkId = Id(10) },
        });
        Assert.False(first.ConflictsWith(unrelated));
        Assert.True(first.ConflictsWith(unrelated with { GrantId = first.GrantId }));
    }

    [Fact]
    public void HeaderMustBindReceiptGenerationScopeAndSourceLifetime()
    {
        var claim = Claim();
        var header = new StateControlHeaderV1(claim.BaseScopeDigest, claim.Epoch,
            claim.SessionId, StateObjectClass.ContextClaim, Hash('1'), Hash('2'),
            claim.AcceptanceIdentity, claim.LogicalGenerationIdentity, "run", 1,
            100, claim.SourceExpiresAtUnixSeconds, claim.SourceExpiresAtUnixSeconds + 3600);
        Assert.True(SolusContextClaimRecordCodec.MatchesHeader(claim, header));
        Assert.False(SolusContextClaimRecordCodec.MatchesHeader(claim,
            header with { PredecessorIdentity = Hash('0') }));
        Assert.False(SolusContextClaimRecordCodec.MatchesHeader(claim,
            header with { LogicalExpiresAtUnixSeconds = claim.SourceExpiresAtUnixSeconds - 1 }));
        Assert.False(SolusContextClaimRecordCodec.MatchesHeader(claim,
            header with { CreatedAtUnixSeconds = claim.SourceExpiresAtUnixSeconds }));
        Assert.False(SolusContextClaimRecordCodec.MatchesHeader(claim,
            header with { ObjectClass = StateObjectClass.Candidate }));
    }

    [Fact]
    public void NewClassPreservesExistingNumbersAndAggregateBudget()
    {
        Assert.Equal(9, (int)StateObjectClass.Cleanup);
        Assert.Equal(10, (int)StateObjectClass.ContextClaim);
        Assert.Equal(72, LineageFormat.MaximumScopedObjects);
        Assert.Contains(StateObjectClass.ContextClaim, StateObjectClasses.Scoped);
        Assert.True(StateObjectClasses.TryParse("context_claim", out var parsed));
        Assert.Equal(StateObjectClass.ContextClaim, parsed);
        Assert.Equal(4096, LineageFormat.MaximumPayloadBytesForClass(parsed));
        var diagnostic = new StateReconciliationDiagnostic(StateReconciliationOwner.ContextClaim,
            StateReconciliationOutcome.OutcomeUnknown, StateReconciliationExactReadBack.Failed,
            1, StateReconciliationTerminal.Unavailable, 0);
        Assert.Contains("\"owner\":\"context_claim\"",
            StateReconciliationDiagnosticFormatter.Format(diagnostic));
    }

    [Fact]
    public void LocalStoreRejectsInvalidProducerCorrelation()
    {
        Assert.Throws<ArgumentException>(() => new LocalRestrictedStateStore("/tmp/unused",
            producingRun: new("", 1)));
        Assert.Throws<ArgumentException>(() => new LocalRestrictedStateStore("/tmp/unused",
            producingRun: new("run", -1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProducerCorrelationSurvivesFreshStoreReadBack(bool explicitProducer)
    {
        var root = Path.Join(Path.GetTempPath(), "apr-claim-producer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var producer = explicitProducer ? new OpaqueStoreProducingRun("900", 2) : null;
            var store = new LocalRestrictedStateStore(root, producingRun: producer);
            byte[] bytes = [1, 2, 3];
            var upload = await store.UploadImmutableAsync(new(new("producer-proof"),
                new(Guid.NewGuid().ToString("N")), bytes, new(OpaqueStoreHash.Sha256(bytes)),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600), default);
            Assert.True(upload.Succeeded, upload.Failure.ToString());
            Assert.NotNull(upload.Metadata);
            if (producer is not null) Assert.Equal(producer, upload.Metadata.ProducingRun);
            else
            {
                Assert.Equal(1, upload.Metadata.ProducingRun.Attempt);
                Assert.True(Guid.TryParseExact(upload.Metadata.ProducingRun.Identity, "N", out _));
                var another = await store.UploadImmutableAsync(new(new("another-producer-proof"),
                    new(Guid.NewGuid().ToString("N")), bytes, new(OpaqueStoreHash.Sha256(bytes)),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600), default);
                Assert.True(another.Succeeded);
                Assert.Equal(upload.Metadata.ProducingRun.Identity, another.Metadata!.ProducingRun.Identity);
                Assert.Equal(1, another.Metadata.ProducingRun.Attempt);
            }
            var fresh = new LocalRestrictedStateStore(root);
            if (!explicitProducer)
            {
                var otherInstance = await fresh.UploadImmutableAsync(new(new("other-instance-proof"),
                    new(Guid.NewGuid().ToString("N")), bytes, new(OpaqueStoreHash.Sha256(bytes)),
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600), default);
                Assert.True(otherInstance.Succeeded);
                Assert.NotEqual(upload.Metadata.ProducingRun.Identity, otherInstance.Metadata!.ProducingRun.Identity);
                Assert.Equal(1, otherInstance.Metadata.ProducingRun.Attempt);
            }
            var listed = await fresh.ListExactAsync(new(upload.Metadata.Reference.Name, 4), default);
            Assert.True(listed.Succeeded);
            Assert.Equal(upload.Metadata.Reference, Assert.Single(listed.Objects));
            var read = await fresh.ReadBackExactAsync(new(upload.Metadata), default);
            Assert.True(read.Succeeded);
            Assert.Equal(upload.Metadata, read.Metadata);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ExplicitObjectIdCollisionPreservesOriginalAndRejectsInvalidFactory()
    {
        var root = Path.Join(Path.GetTempPath(), "apr-claim-id-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new LocalRestrictedStateStore(root, objectIdFactory: () => new("12345"));
            OpaqueStoreUploadRequest Request(byte value) => new(new("object-id-proof"),
                new(Guid.NewGuid().ToString("N")), new byte[] { value },
                new(OpaqueStoreHash.Sha256(new byte[] { value })),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 600);
            var first = await store.UploadImmutableAsync(Request(1), default);
            Assert.True(first.Succeeded);
            Assert.Equal("12345", first.Metadata!.Reference.ObjectId.Value);
            var collision = await store.UploadImmutableAsync(Request(2), default);
            Assert.False(collision.Succeeded);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, collision.MutationState);
            var fresh = new LocalRestrictedStateStore(root);
            var read = await fresh.ReadBackExactAsync(new(first.Metadata), default);
            Assert.True(read.Succeeded);
            Assert.Equal(first.Metadata, read.Metadata);
            var downloaded = await fresh.DownloadAsync(new(first.Metadata, 4096), default);
            Assert.True(downloaded.Succeeded);
            Assert.Equal(new byte[] { 1 }, downloaded.EncryptedBytes.ToArray());
            var list = await fresh.ListExactAsync(new(first.Metadata.Reference.Name, 4), default);
            Assert.True(list.Succeeded);
            Assert.Equal(first.Metadata.Reference, Assert.Single(list.Objects));
            foreach (Func<OpaqueStoreObjectId> invalid in new Func<OpaqueStoreObjectId>[]
            {
                () => null!, () => new(""), () => throw new InvalidOperationException(),
            })
            {
                var denied = await new LocalRestrictedStateStore(root, objectIdFactory: invalid)
                    .UploadImmutableAsync(Request(3), default);
                Assert.False(denied.Succeeded);
                Assert.Equal(OpaqueStoreMutationState.NotCommitted, denied.MutationState);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task DirectoryGateExcludesUntilReleasedAndRejectsReplacementOrSymlink()
    {
        if (!OperatingSystem.IsLinux()) return;
        var parent = Path.Join(Path.GetTempPath(), "apr-claim-gate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var root = Path.Join(parent, "root"); Directory.CreateDirectory(root);
        try
        {
            using (var first = await SolusContextExclusion.AcquireAuthorizedDirectoryAsync(root, () => true, default))
            {
                Assert.NotNull(first);
                using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
                using var denied = await SolusContextExclusion.AcquireAuthorizedDirectoryAsync(root, () => true, timeout.Token);
                Assert.Null(denied);
                Assert.True(first.IsLive);
            }
            using var next = await SolusContextExclusion.AcquireAuthorizedDirectoryAsync(root, () => true, default);
            Assert.NotNull(next);
            Directory.Move(root, Path.Join(parent, "displaced"));
            Directory.CreateDirectory(root);
            Assert.False(next.IsLive);
            var link = Path.Join(parent, "link"); Directory.CreateSymbolicLink(link, root);
            using var linked = await SolusContextExclusion.AcquireAuthorizedDirectoryAsync(link, () => true, default);
            Assert.Null(linked);
            var file = Path.Join(parent, "file"); File.WriteAllText(file, "not-directory");
            using var special = await SolusContextExclusion.AcquireAuthorizedDirectoryAsync(file, () => true, default);
            Assert.Null(special);
        }
        finally { Directory.Delete(parent, recursive: true); }
    }

    private static SolusContextClaimRecord Claim() => Regrant(new(Hash('a'), Hash('b'),
        Hash('c'), Hash('d'), Hash('e'), Hash('f'), Hash('1'), Hash('2'),
        new(Id(1), Id(2), Id(3), Id(4)), Guid.Empty, Id(6), Id(7), 200));

    private static SolusContextClaimRecord Regrant(SolusContextClaimRecord value) =>
        value with { GrantId = SolusContextClaimRecordCodec.DeriveGrantId(value.BaseScopeDigest,
            value.Epoch, value.SessionId, value.LogicalGenerationIdentity,
            value.Source.LogicalWorkId, value.Source.RoundId, value.ExecutionId, value.RoundId) };

    private static string Hash(char value) => new(value, 64);
    private static Guid Id(int value) => Guid.ParseExact(value.ToString("x32"), "N");
}
