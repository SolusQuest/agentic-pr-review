using AgenticPrReview.Runtime.ActionHost;
using AgenticPrReview.Runtime.ActionHost.Authorization;
using AgenticPrReview.Runtime.ActionHost.Contracts;
using AgenticPrReview.Runtime.ActionHost.GitHub;
using AgenticPrReview.Runtime.Host.State.GitHubArtifacts;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;
using AgenticPrReview.Runtime.Host.State.Restore;
using AgenticPrReview.Runtime.Tests.Host.Action.Authorization;

namespace AgenticPrReview.Runtime.Tests.Host.State.GitHubArtifacts;

public sealed class GitHubArtifactRestConformanceTests
{
    [Fact]
    public async Task SharedConformanceTraversesCSharpRestAndRealPrivateSdkUploadIncludingMaximumCarrier()
    {
        var launch = ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;
        await using var fixture = await HybridArtifactFixture.StartAsync(launch);
        using var store = fixture.Store();
        await RestrictedStateStoreConformanceHarness.VerifyAsync(async action =>
        {
            await action(new(store,
                async expected => { fixture.Flag("duplicate-list"); return await store.ListExactAsync(new(expected.Reference.Name, 8), CancellationToken.None); },
                async expected => { fixture.Flag("missing-readback"); return await store.ReadBackExactAsync(new(expected), CancellationToken.None); },
                async expected =>
                {
                    var saved = fixture.Now; fixture.Now = expected.ExpiresAtUnixSeconds;
                    try { return await store.DownloadAsync(new(expected, (int)expected.Size), CancellationToken.None); }
                    finally { fixture.Now = saved; }
                },
                () => fixture.Flag("may-commit-upload"),
                async expected => { fixture.Flag("delete-unknown"); return await store.DeleteExactAsync(new(expected), CancellationToken.None); },
                provisionalUploadReceipts: true));
        });
        Assert.True(fixture.Count("sdk") > 0);
        Assert.True(fixture.Count("rest") > 0);
        Assert.True(fixture.Count("anonymous") > 0);
        Assert.Equal(fixture.Count("sdk"), fixture.Count("historical_reads"));
        Assert.Equal(0, fixture.Count("node_non_upload"));
    }

    [Theory]
    [InlineData("wrong-name", (int)OpaqueStoreFailure.Invalid)]
    [InlineData("wrong-run", (int)OpaqueStoreFailure.Conflict)]
    [InlineData("missing-run", (int)OpaqueStoreFailure.Invalid)]
    [InlineData("zero-run", (int)OpaqueStoreFailure.Invalid)]
    [InlineData("unsafe-run", (int)OpaqueStoreFailure.Invalid)]
    [InlineData("bad-digest", (int)OpaqueStoreFailure.DigestMismatch)]
    [InlineData("expired", (int)OpaqueStoreFailure.Expired)]
    [InlineData("wrong-attempt", (int)OpaqueStoreFailure.Conflict)]
    [InlineData("unsafe-redirect", (int)OpaqueStoreFailure.Invalid)]
    [InlineData("missing-readback", (int)OpaqueStoreFailure.NotFound)]
    public async Task RejectsUntrustedArtifactObservations(string fault, int expectedFailure)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        var uploaded = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), CancellationToken.None);
        Assert.True(uploaded.Succeeded, uploaded.Failure.ToString());
        store.Rest.Invalidate(uploaded.Metadata!.Reference.ObjectId.Value);
        fixture.Flag(fault);
        var result = await store.ReadBackExactAsync(new(uploaded.Metadata), CancellationToken.None);
        Assert.Equal((OpaqueStoreFailure)expectedFailure, result.Failure);
        Assert.False(result.Succeeded);
        Assert.Equal(0, fixture.Count("node_non_upload"));
    }

    [Fact]
    public async Task LostDeleteAcknowledgementIsUnknownWithoutReplayOrCachedAbsence()
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        var uploaded = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), CancellationToken.None);
        Assert.True(uploaded.Succeeded);
        fixture.Flag("may-commit-delete");
        var result = await store.DeleteExactAsync(new(uploaded.Metadata!), CancellationToken.None);
        Assert.Equal(OpaqueStoreMutationState.OutcomeUnknown, result.MutationState);
        Assert.Equal(OpaqueStoreFailure.OutcomeUnknown, result.Failure);
        Assert.Equal(1, fixture.Count("deletes"));
        Assert.Equal(OpaqueStoreFailure.NotFound,
            (await store.ReadMetadataAsync(new(uploaded.Metadata!.Reference), CancellationToken.None)).Failure);
        Assert.Equal(1, fixture.Count("deletes"));
    }

    [Fact]
    public async Task ConditionalMetadataReuseStillChecksUtcExpiryAndInvalidatesRejectedRecords()
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        var upload = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), default);
        Assert.True(upload.Succeeded);
        var primary = store.Rest.Budget.Primary;
        var downloads = fixture.Count("anonymous");
        fixture.Flag("not-modified");
        Assert.True((await store.ReadBackExactAsync(new(upload.Metadata!), default)).Succeeded);
        Assert.Equal(primary, store.Rest.Budget.Primary);
        Assert.Equal(1, store.Rest.Budget.NotModified);
        Assert.Equal(downloads, fixture.Count("anonymous"));
        var now = fixture.Now;
        fixture.Now = upload.Metadata!.ExpiresAtUnixSeconds;
        fixture.Flag("not-modified");
        Assert.Equal(OpaqueStoreFailure.Expired, (await store.ReadBackExactAsync(new(upload.Metadata), default)).Failure);
        fixture.Now = now;
        Assert.True((await store.ReadBackExactAsync(new(upload.Metadata), default)).Succeeded);
        Assert.True(fixture.Count("anonymous") > downloads);
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public async Task UploadReservesAllThreeAuthenticatedObservationsBeforeSdkEntry(int capacity, bool success)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store(new(capacity, capacity, 0));
        var result = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), CancellationToken.None);
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(success ? 1 : 0, fixture.Count("sdk"));
        Assert.Equal(success ? 3 : 0, store.Rest.Budget.Requests);
        Assert.Equal(success ? 3 : 0, store.Rest.Budget.Primary);
        Assert.Equal(success ? 3 : 0, store.Rest.Budget.SecondaryPoints);
        Assert.Equal(success ? 5 : 0, store.Rest.Budget.SdkMutationPoints);
    }

    [Theory]
    [InlineData("short-retention")]
    [InlineData("wrong-attempt")]
    [InlineData("wrong-name")]
    [InlineData("bad-digest")]
    [InlineData("missing-readback")]
    public async Task ValidSdkReceiptCannotCommitWithoutIndependentMatchingRestReadback(string fault)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        fixture.Flag(fault);
        var result = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), default);
        Assert.Equal(OpaqueStoreFailure.OutcomeUnknown, result.Failure);
        Assert.Equal(OpaqueStoreMutationState.OutcomeUnknown, result.MutationState);
        Assert.Null(result.Metadata);
        Assert.Equal(1, fixture.Count("sdk"));
        Assert.True(store.Rest.Budget.Requests > 0);
        Assert.Equal(0, fixture.Count("deletes"));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(6, true)]
    public async Task DeleteReservesExpectedReadMutationAndFreshAbsenceBeforeAnyDelete(int capacity, bool success)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store(new(capacity, capacity, 0));
        var uploaded = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), default);
        Assert.True(uploaded.Succeeded);
        var result = await store.DeleteExactAsync(new(uploaded.Metadata!), default);
        Assert.Equal(success, result.Succeeded);
        Assert.Equal(success ? 1 : 0, fixture.Count("deletes"));
        Assert.Equal(success ? 6 : 3, store.Rest.Budget.Requests);
        Assert.Equal(success ? OpaqueStoreMutationState.Committed : OpaqueStoreMutationState.NotCommitted, result.MutationState);
    }

    [Theory]
    [InlineData("may-commit-upload")]
    [InlineData("bad-receipt")]
    public async Task LostOrMalformedSdkReceiptCannotProduceAcceptedMetadataOrDeleteLiveStaging(string fault)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        fixture.Flag(fault);
        var result = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(), CancellationToken.None);
        Assert.Equal(OpaqueStoreFailure.OutcomeUnknown, result.Failure);
        Assert.Equal(OpaqueStoreMutationState.OutcomeUnknown, result.MutationState);
        Assert.Null(result.Metadata);
        Assert.Equal(1, fixture.Count("sdk"));
        Assert.Equal(0, store.Rest.Budget.Requests);
        Assert.NotEmpty(Directory.GetFiles(fixture.Staging, "artifact-envelope.json", SearchOption.AllDirectories));
    }

    [AgenticPrReview.Runtime.Tests.Host.Action.LinuxArtifactHostFact]
    public async Task UnsettledSdkRetentionRefusesReplacedDirectoryWithoutUnlinkingEitherIdentity()
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var staging = new ArtifactBridgeStaging(fixture.Staging);
        var scope = await staging.StageUploadAsync(new byte[] { 1, 2, 3 }, default);
        var operation = Path.GetDirectoryName(scope.FullPath)!;
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(operation, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var retained = operation + "-original";
        Directory.Move(operation, retained);
        Directory.CreateDirectory(operation);
        var marker = Path.Join(operation, "foreign-marker");
        await File.WriteAllTextAsync(marker, "keep");
        scope.RetainForBridgeCleanup();
        Assert.False(scope.CleanupSucceeded);
        await scope.DisposeAsync();
        Assert.Equal("keep", await File.ReadAllTextAsync(marker));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Join(retained, "object.bin")));
    }

    [Fact]
    public async Task InvocationOwnerSharesRepeatedFactoryCallsAndDisposesWithoutLeakingBudgetToNextInvocation()
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        var factory = new AcceptedStateProductionDependencies(new ActionHostGitHubAuthorizationTransportFactory(),
            fixture.Handlers, new(3, 3, 0), fixture.Clock);
        using var time = new ActionHostTimeBudget(TimeProvider.System, CancellationToken.None);
        using var journal = new ActionHostTransactionJournal(ActionHostCancellationState.Active, CancellationToken.None, time);
        async Task<OpaqueStoreUploadResult> Upload(IRestrictedStateStore store, string name, ActionHostTransactionJournal current)
        {
            Assert.True(current.TryBeginBusinessOperation(ActionHostOperationKind.StateSetup, CancellationToken.None, out var admitted));
            using (admitted!)
            {
                var result = await store.UploadImmutableAsync(HybridArtifactFixture.Upload(name), CancellationToken.None);
                admitted!.Resolve(result.Succeeded ? ActionHostOperationResolution.ResolvedCommitted : ActionHostOperationResolution.ResolvedNoCommit);
                return result;
            }
        }
        using (var invocation = new JournaledAcceptedStateProductionDependencies(factory, journal))
        {
            var first = invocation.CreateArtifactStore(fixture.Launch);
            var second = invocation.CreateArtifactStore(fixture.Launch);
            Assert.Same(first, second);
            Assert.True((await Upload(first, "first", journal)).Succeeded);
            var refused = await Upload(second, "second", journal);
            Assert.False(refused.Succeeded);
            Assert.Equal(OpaqueStoreMutationState.NotCommitted, refused.MutationState);
            Assert.Equal(1, fixture.Count("sdk"));
        }
        using var nextJournal = new ActionHostTransactionJournal(ActionHostCancellationState.Active, CancellationToken.None, time);
        using var next = new JournaledAcceptedStateProductionDependencies(factory, nextJournal);
        Assert.True((await Upload(next.CreateArtifactStore(fixture.Launch), "next", nextJournal)).Succeeded);
        Assert.Equal(2, fixture.Count("sdk"));
    }

    [Theory]
    [InlineData("http://a.blob.core.windows.net/a")]
    [InlineData("https://token@a.blob.core.windows.net/a")]
    [InlineData("https://a.blob.core.windows.net:444/a")]
    [InlineData("https://a.blob.core.windows.net/a#fragment")]
    [InlineData("https://127.0.0.1/a")]
    [InlineData("https://a.blob.core.windows.net.evil.test/a")]
    public void UnsafeSignedArchiveLocationsRefuse(string location) =>
        Assert.False(GitHubArtifactRestClient.SafeArchiveLocation(new Uri(location)));

    private static ActionHostLaunchContract Launch() =>
        ActionHostAuthorizationScenario.Valid(ActionHostAuthorizationRoute.WorkflowDispatch).Launch;

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(256)]
    [InlineData(1024)]
    public async Task CompleteRepositoryCensusIncludesLaterPages_APV026_027(int count)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        var values = Enumerable.Range(1, count).Select(id => new { id, name = ArtifactTransportFormat.PhysicalName(
            count > 256 && id <= count - 256 ? "other" : "opaque", new string('a', 64)) }).ToArray();
        await File.WriteAllBytesAsync(Path.Join(fixture.Control, "listing.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(values));
        var result = await store.ListExactAsync(new(new("opaque"), 256), CancellationToken.None);
        Assert.True(result.Succeeded, result.Failure.ToString());
        Assert.Equal(Math.Min(count, 256), result.Objects.Length);
        Assert.Equal(Math.Max(1, (count + 99) / 100), store.Rest.Budget.Requests);
    }

    [Theory]
    [InlineData("duplicate", (int)OpaqueStoreFailure.Duplicate)]
    [InlineData("drift-total", (int)OpaqueStoreFailure.Incomplete)]
    [InlineData("fail-later", (int)OpaqueStoreFailure.Io)]
    [InlineData("overflow", (int)OpaqueStoreFailure.Incomplete)]
    [InlineData("unsuffixed", (int)OpaqueStoreFailure.Incomplete)]
    [InlineData("malformed-family", (int)OpaqueStoreFailure.Incomplete)]
    public async Task PartialOrAmbiguousCensusNeverBecomesAbsence_APV026_027_028_030(string fault, int failure)
    {
        await using var fixture = await HybridArtifactFixture.StartAsync(Launch());
        using var store = fixture.Store();
        var count = fault == "overflow" ? 1025 : 101;
        var values = Enumerable.Range(1, count).Select(id => new { id = fault == "duplicate" && id == 101 ? 1 : id,
            name = fault == "unsuffixed" && id == 101 ? "opaque" : fault == "malformed-family" && id == 101 ?
                ArtifactTransportFormat.Family("opaque") + "bad" : ArtifactTransportFormat.PhysicalName("other", new string('a', 64)) }).ToArray();
        await File.WriteAllBytesAsync(Path.Join(fixture.Control, "listing.json"), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(values));
        if (fault is "drift-total" or "fail-later") fixture.Flag(fault);
        var result = await store.ListExactAsync(new(new("opaque"), 256), CancellationToken.None);
        Assert.Equal((OpaqueStoreFailure)failure, result.Failure);
        Assert.False(result.Complete);
        Assert.Empty(result.Objects);
    }
}
