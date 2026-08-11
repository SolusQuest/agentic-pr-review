using System.Reflection;
using AgenticPrReview.Runtime.Host.State.Lineage;
using AgenticPrReview.Runtime.Host.State.Locator;
using AgenticPrReview.Runtime.Tests.Host.State.Locator;

namespace AgenticPrReview.Runtime.Tests.Host.State.Lineage;

internal static class LineageTestData
{
    internal const long Now = LocatorTestData.Now;
    internal const long LogicalExpiry =
        Now + StateRetentionRequirements.LogicalWindowSeconds;
    internal const long SentinelExpiry = Now + 30 * 24 * 60 * 60;
    internal static readonly byte[] Root =
        Enumerable.Repeat((byte)0x5a, LocatorRootFormat.RootBytes).ToArray();

    internal static LineageBaseScope Scope() =>
        new(
            "owner/repository",
            "workflow:review.yml@refs/heads/main",
            "pull_request_target:trusted",
            PullRequestNumber: 154,
            "openai",
            "gpt-5",
            "responses-api",
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            new string('d', 64),
            "runtime-payload-v1");

    internal static ReviewedTransitionFacts Reviewed(
        char baseCharacter = '1',
        char headCharacter = '2') =>
        new(new string(baseCharacter, 40), new string(headCharacter, 40));

    internal static ContextLease Context(
        long now = Now,
        string? previousBase64 = null,
        string? currentBase64 = null)
    {
        var access = LocatorTestData.Access();
        var keys = LocatorTestData.KeyRing(
            access,
            previousBase64,
            currentBase64: currentBase64);
        var time = new MutableLineageTimeProvider(now);
        Assert.True(LocatorContext.TryCreate(
            access,
            keys,
            Root,
            currentSingletonProven: true,
            SentinelExpiry,
            time,
            out var context));
        Assert.NotNull(context);
        return new ContextLease(access, keys, context, time);
    }

    internal static LineageResolveRequest Request(
        AuthorizedLocatorAccess access,
        AuthorizedLineageReset? reset = null,
        ReviewedTransitionFacts? reviewed = null) =>
        new(
            access,
            Scope(),
            reviewed ?? Reviewed(),
            "workflow-run-42",
            ProducingRunAttempt: 1,
            LogicalExpiry,
            reset);

    internal static AuthorizedLineageReset Reset(
        AuthorizedLocatorAccess access,
        string? priorHeadIdentity,
        string requestIdentity,
        string producingRunIdentity = "workflow-run-42",
        long producingRunAttempt = 1,
        string trustedWorkflowRoute = "workflow_dispatch")
    {
        Assert.True(LineageBaseScopeCodec.TryDigest(
            Scope(),
            out var baseScopeDigest));
        var constructor = typeof(AuthorizedLineageReset).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [
                typeof(AuthorizedLocatorAccess),
                typeof(string),
                typeof(string),
                typeof(long),
                typeof(string),
                typeof(string),
                typeof(string),
                typeof(long),
                typeof(string),
                typeof(string),
            ],
            modifiers: null);
        Assert.NotNull(constructor);
        return (AuthorizedLineageReset)constructor.Invoke(
        [
            access,
            baseScopeDigest,
            Scope().RepositoryId,
            Scope().PullRequestNumber,
            Scope().TrustedWorkflowIdentity,
            trustedWorkflowRoute,
            producingRunIdentity,
            producingRunAttempt,
            requestIdentity,
            priorHeadIdentity,
        ]);
    }

    internal sealed class ContextLease(
        AuthorizedLocatorAccess access,
        LocatorStateKeyRing keys,
        LocatorContext context,
        MutableLineageTimeProvider time) : IDisposable
    {
        internal AuthorizedLocatorAccess Access { get; } = access;
        internal LocatorStateKeyRing Keys { get; } = keys;
        internal LocatorContext Context { get; } = context;
        internal MutableLineageTimeProvider Time { get; } = time;

        public void Dispose()
        {
            Context.Dispose();
            Keys.Dispose();
            Access.Dispose();
        }
    }
}

internal sealed class MutableLineageTimeProvider(long unixSeconds)
    : TimeProvider
{
    internal long UnixSeconds { get; set; } = unixSeconds;

    public override DateTimeOffset GetUtcNow() =>
        DateTimeOffset.FromUnixTimeSeconds(UnixSeconds);
}
