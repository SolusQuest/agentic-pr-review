using System.Collections.Immutable;
using System.Reflection;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Host.State.OpaqueStore;

namespace AgenticPrReview.Runtime.Tests.Host.State.Locator;

internal static class LocatorTestData
{
    internal const long Now = 1_700_000_000;
    internal static readonly byte[] CurrentKey =
        Enumerable.Range(0, LocatorRootFormat.KeyBytes)
            .Select(value => (byte)value)
            .ToArray();
    internal static readonly byte[] PreviousKey =
        Enumerable.Range(1, LocatorRootFormat.KeyBytes)
            .Select(value => (byte)(value * 3))
            .ToArray();

    internal static string CurrentBase64 =>
        Convert.ToBase64String(CurrentKey);

    internal static string PreviousBase64 =>
        Convert.ToBase64String(PreviousKey);

    internal static AuthorizedLocatorAccess Access(
        string repositoryId = "owner/repository")
    {
        var constructor = typeof(AuthorizedLocatorAccess).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string)],
            modifiers: null);
        Assert.NotNull(constructor);
        return (AuthorizedLocatorAccess)constructor.Invoke([repositoryId]);
    }

    internal static LocatorStateKeyRing KeyRing(
        AuthorizedLocatorAccess access,
        string? previousBase64 = null,
        string repositoryId = "owner/repository",
        string? currentBase64 = null)
    {
        Assert.True(LocatorStateKeyRing.TryCreate(
            access,
            repositoryId,
            currentBase64 ?? CurrentBase64,
            previousBase64,
            out var keys,
            out var code),
            code);
        return keys!;
    }

    internal static LocatorRootSentinel Sentinel(
        LocatorStateKeyRing keys,
        byte[]? root = null,
        ulong generation = 0,
        long? requiredExpiry = null,
        ImmutableArray<LocatorArtifactIdentity>? predecessors = null,
        ImmutableArray<LocatorArtifactIdentity>? superseded = null,
        string? writerKeyId = null) =>
        new(
            root ?? Enumerable.Repeat((byte)0x41, 32).ToArray(),
            generation,
            writerKeyId ?? keys.CurrentKeyId,
            Now,
            requiredExpiry ?? Now + 1,
            predecessors ?? [],
            superseded ?? []);

    internal static OpaqueStoreObjectMetadata Metadata(
        string objectId,
        long expiresAt,
        string? archive = null,
        string? envelope = null) =>
        new(
            new OpaqueStoreObjectReference(
                new OpaqueStoreName(LocatorRootFormat.StoreName),
                new OpaqueStoreObjectId(objectId)),
            new OpaqueStoreProducingRun("test-run", 1),
            new OpaqueStoreArchiveDigest(
                archive ?? new string('a', 64)),
            new OpaqueStoreEncryptedObjectDigest(
                envelope ?? new string('b', 64)),
            expiresAt,
            128);
}

internal sealed class FrozenLocatorTimeProvider(long unixSeconds)
    : TimeProvider
{
    internal long UnixSeconds { get; set; } = unixSeconds;
    internal List<TimeSpan> ScheduledDelays { get; } = [];

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var fixedVisibilityDelay = dueTime == TimeSpan.FromSeconds(5) ||
            dueTime == TimeSpan.FromSeconds(10);
        if (fixedVisibilityDelay)
        {
            lock (ScheduledDelays) ScheduledDelays.Add(dueTime);
        }

        return TimeProvider.System.CreateTimer(
            callback,
            state,
            fixedVisibilityDelay ? TimeSpan.Zero : dueTime,
            period);
    }
}
