using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

public sealed class LineageRefreshOnlyTests
{
    [Fact]
    public async Task RefreshPreservesSelectedSemanticHeadAndExactExpiry()
    {
        using var fixture = LineageTestData.Context();
        var store = Store();
        var service = new LineageService(store, fixture.Time);
        var initialized = await service.ResolveAsync(
            fixture.Context,
            LineageTestData.Request(fixture.Access),
            CancellationToken.None);
        Assert.True(initialized.Succeeded, initialized.Code);
        using var selected = initialized.Context!;
        Assert.True(selected.TryGetSnapshot(
            fixture.Access,
            out var before));
        var uploadsBefore = store.UploadCalls;

        var refreshed = await service.RefreshSelectedHeadAsync(
            fixture.Context,
            LineageTestData.Request(fixture.Access),
            selected,
            CancellationToken.None);

        Assert.True(refreshed.Succeeded, refreshed.Code);
        using var result = refreshed.Context!;
        Assert.True(result.TryGetSnapshot(fixture.Access, out var after));
        Assert.Equal(before, after);
        Assert.Equal(uploadsBefore, store.UploadCalls);
        Assert.Equal(
            LineageTestData.Now +
                StateRetentionRequirements.ScopedPlatformRequestSeconds,
            store.Objects.Max(item => item.ExpiresAtUnixSeconds));
    }

    [Fact]
    public async Task RefreshRejectsResetAndChangedReviewWithoutMutation()
    {
        using var fixture = LineageTestData.Context();
        var store = Store();
        var service = new LineageService(store, fixture.Time);
        var initialized = await service.ResolveAsync(
            fixture.Context,
            LineageTestData.Request(fixture.Access),
            CancellationToken.None);
        using var selected = initialized.Context!;
        Assert.True(selected.TryGetSnapshot(
            fixture.Access,
            out var snapshot));
        var uploadsBefore = store.UploadCalls;
        var reset = LineageTestData.Reset(
            fixture.Access,
            snapshot!.LineageHeadIdentity,
            new string('e', 64));

        var resetRejected = await service.RefreshSelectedHeadAsync(
            fixture.Context,
            LineageTestData.Request(fixture.Access, reset),
            selected,
            CancellationToken.None);
        var reviewRejected = await service.RefreshSelectedHeadAsync(
            fixture.Context,
            LineageTestData.Request(
                fixture.Access,
                reviewed: LineageTestData.Reviewed('3', '4')),
            selected,
            CancellationToken.None);

        Assert.Equal(LineageCodes.AccessDenied, resetRejected.Code);
        Assert.Equal(LineageCodes.Conflict, reviewRejected.Code);
        Assert.Equal(uploadsBefore, store.UploadCalls);
    }

    private static ScriptedLocatorStore Store() => new()
    {
        FilterListsByName = true,
        UseNumericObjectIds = true,
        ProducingRunIdentity = "workflow-run-42",
        ProducingRunAttempt = 1,
        ExtraRetentionSeconds = 0,
    };
}
